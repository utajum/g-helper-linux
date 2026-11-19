namespace GHelper.Linux.Input;

/// <summary>What a gesture step asks the NumberPad service to do.</summary>
public enum NumberPadEvent { KeyDown, KeyUp, Toggle, Brightness, Calculator, Pointer }

/// <summary>One gesture output: keys, a request, or a frame for the virtual touchpad.</summary>
public readonly record struct NumberPadOutput(NumberPadEvent Kind, ushort[]? Keys = null, PointerFrame? Frame = null);

/// <summary>
/// NumberPad touch logic without I/O, after the ASUS Windows driver. Feed
/// multi-touch evdev events and the clock; read back outputs.
/// Holding the NumLock zone asks for <see cref="NumberPadEvent.Toggle"/>; holding
/// the calculator zone while active asks for <see cref="NumberPadEvent.Brightness"/>;
/// sliding out of a zone asks for <see cref="NumberPadEvent.Calculator"/>.
/// While active, a finger on a key presses it after a short rest (released on
/// lift) or taps it on a quick lift; a finger that moves first, or lands outside
/// the keys, becomes a pointer for the virtual touchpad (Windows mixed mode).
/// </summary>
public sealed class NumberPadGesture
{
    // ASUS Windows driver values: frames of 8 ms, distances in mm.
    private const long CalcHoldMs = 40 * 8;   // NumAdv_Trigger_FC
    private const long KeyRestMs = 10 * 8;    // Gesture_TouchSendKey_Trig_FC
    private const long ExtraRestMs = 3 * 8;   // per extra finger, up to two
    private const double MoveMm = 4.838;      // Gesture_Trig_UM
    private const double SlideMm = 7.0;       // EnAppGesture_Trig_UM
    private const double TapMm = 10;          // iTAPPING_THRESHOLD_MM
    private const long TapMs = 350;           // Min_Fir_Det_TAP_Interval_T
    private const int ReplayFrames = 30;      // iGestureEdgeTempDataLen

    private enum Role { Ignored, Pending, Key, Pointer, Lock, Calc }

    private sealed class Contact
    {
        public int Id, X, Y, StartX, StartY;
        public long Start;
        public Role Role;
        public int Cell = -1;
        public ushort[]? Down;
        public long Due = -1;
        public bool Sliding, Done, Fired;
        public double ExitDist;
        public readonly List<(int X, int Y)> Trail = new();
    }

    private sealed class Raw
    {
        public int Id = -1, X, Y;
    }

    private readonly NumberPadSurface _surface;
    private readonly Dictionary<int, Raw> _raw = new();
    private readonly SortedDictionary<int, Contact> _contacts = new();
    private readonly List<NumberPadOutput> _out = new();
    private int _slot;
    private bool _button, _lastButton;
    private bool _gestureMode;
    private long _liftTime = -1;
    private int _liftX, _liftY;

    public NumberPadGesture(NumberPadSurface surface)
    {
        _surface = surface;
    }

    /// <summary>True while touches type keys. Change it with <see cref="SetActive"/>.</summary>
    public bool Active { get; private set; }

    /// <summary>Ms until the next timer fires, or -1 when none is pending.</summary>
    public long MsUntilDue(long now)
    {
        long due = -1;
        foreach (var c in _contacts.Values)
        {
            long d = DueOf(c);
            if (d >= 0 && (due < 0 || d < due))
                due = d;
        }
        return due < 0 ? -1 : Math.Max(0, due - now);
    }

    /// <summary>Switch typing on or off. Pressed keys are released and pointer touches lifted.</summary>
    public IReadOnlyList<NumberPadOutput> SetActive(bool active)
    {
        _out.Clear();
        if (active == Active)
            return _out;
        bool hadPointer = _contacts.Values.Any(c => c.Role == Role.Pointer);
        Active = active;
        _gestureMode = false;
        foreach (var c in _contacts.Values)
        {
            if (c.Role == Role.Key && c.Down != null)
                _out.Add(new(NumberPadEvent.KeyUp, c.Down));
            if (c.Role is Role.Pending or Role.Key or Role.Pointer)
                c.Role = Role.Ignored;
            else if (c.Role == Role.Calc && !active)
                c.Due = -1;
        }
        if (hadPointer)
            _out.Add(new(NumberPadEvent.Pointer, Frame: new PointerFrame(Array.Empty<PointerContact>(), false)));
        return _out;
    }

    /// <summary>Fire due timers.</summary>
    public IReadOnlyList<NumberPadOutput> Tick(long now)
    {
        _out.Clear();
        foreach (var c in _contacts.Values)
        {
            long due = DueOf(c);
            if (due < 0 || now < due)
                continue;
            if (c.Role == Role.Pending)
            {
                int cell = _surface.Hit(c.X, c.Y).Cell;
                c.Down = _surface.Layout.Cells[cell >= 0 ? cell : c.Cell];
                c.Role = Role.Key;
                if (c.Down != null)
                    _out.Add(new(NumberPadEvent.KeyDown, c.Down));
                continue;
            }
            c.Due = -1;
            c.Done = c.Fired = true;
            if (c.Role == Role.Lock)
                _out.Add(new(NumberPadEvent.Toggle));
            else if (Active)
                _out.Add(new(NumberPadEvent.Brightness));
        }
        return _out;
    }

    /// <summary>Feed one evdev event; outputs come on SYN_REPORT.</summary>
    public IReadOnlyList<NumberPadOutput> Feed(ushort type, ushort code, int value, long now)
    {
        _out.Clear();
        if (type == EvdevInterop.EV_ABS)
        {
            if (code == EvdevInterop.ABS_MT_SLOT)
                _slot = value;
            else if (code == EvdevInterop.ABS_MT_TRACKING_ID)
                RawSlot().Id = value;
            else if (code == EvdevInterop.ABS_MT_POSITION_X)
                RawSlot().X = value;
            else if (code == EvdevInterop.ABS_MT_POSITION_Y)
                RawSlot().Y = value;
        }
        else if (type == EvdevInterop.EV_KEY && code == EvdevInterop.BTN_LEFT)
            _button = value != 0;
        else if (type == EvdevInterop.EV_SYN && code == EvdevInterop.SYN_REPORT)
            Frame(now);
        return _out;
    }

    private Raw RawSlot()
    {
        if (!_raw.TryGetValue(_slot, out var r))
            _raw[_slot] = r = new Raw();
        return r;
    }

    private long DueOf(Contact c)
    {
        if (c.Role != Role.Pending)
            return c.Due;
        int n = _contacts.Count;
        return c.Start + KeyRestMs + ExtraRestMs * Math.Min(2, n - 1);
    }

    private double Mm(double mm) => mm * _surface.UnitsPerMm;

    private static double Dist(int x0, int y0, int x1, int y1) => Math.Sqrt((double)(x1 - x0) * (x1 - x0) + (double)(y1 - y0) * (y1 - y0));

    private void Frame(long now)
    {
        bool dirty = false;
        foreach (int slot in _contacts.Keys.ToList())
        {
            var c = _contacts[slot];
            if (_raw.TryGetValue(slot, out var r) && r.Id == c.Id)
                continue;
            dirty |= c.Role == Role.Pointer;
            Lift(c, now);
            _contacts.Remove(slot);
        }
        foreach (var (slot, r) in _raw)
        {
            if (r.Id < 0)
                continue;
            if (!_contacts.TryGetValue(slot, out var c))
            {
                _contacts[slot] = c = Down(r.X, r.Y, now);
                c.Id = r.Id;
                dirty |= c.Role == Role.Pointer;
            }
            else if (r.X != c.X || r.Y != c.Y)
            {
                c.X = r.X;
                c.Y = r.Y;
                dirty |= Move(c, slot);
            }
        }
        if (_button != _lastButton)
        {
            _lastButton = _button;
            dirty = true;
            if (_button && Active)
                foreach (var (slot, c) in _contacts)
                    if (c.Role == Role.Pending)
                    {
                        _gestureMode = true;
                        Promote(c, slot);
                    }
        }
        if (dirty && Active)
            EmitPointer();
    }

    private Contact Down(int x, int y, long now)
    {
        var c = new Contact { X = x, Y = y, StartX = x, StartY = y, Start = now };
        var hit = _surface.Hit(x, y);
        if (hit.Toggle)
        {
            // Layouts without driver data keep a key under the toggle corner; a quick tap types it.
            c.Role = Role.Lock;
            c.Cell = hit.Cell;
            c.Due = now + _surface.Layout.HoldMs;
        }
        else if (hit.Calc)
        {
            c.Role = Role.Calc;
            c.Due = Active ? now + CalcHoldMs : -1;
        }
        else if (!Active)
            c.Role = Role.Ignored;
        else if (hit.Cell < 0)
            c.Role = Role.Pointer;
        else if (_gestureMode && _liftTime >= 0 && now - _liftTime < TapMs
            && Dist(_liftX, _liftY, x, y) < Mm(TapMm))
            c.Role = Role.Pointer;
        else
        {
            _gestureMode = false;
            c.Role = Role.Pending;
            c.Cell = hit.Cell;
            c.Trail.Add((x, y));
        }
        return c;
    }

    // Returns true when the pointer frame changed.
    private bool Move(Contact c, int slot)
    {
        switch (c.Role)
        {
            case Role.Pointer:
                return true;
            case Role.Pending:
                c.Trail.Add((c.X, c.Y));
                if (c.Trail.Count > ReplayFrames)
                    c.Trail.RemoveAt(0);
                // With two or more fingers on keys the driver checks only the first contact.
                if (Dist(c.StartX, c.StartY, c.X, c.Y) > Mm(MoveMm)
                    && (_contacts.Values.Count(o => o.Role is Role.Pending or Role.Key) == 1 || _contacts.Keys.First() == slot))
                {
                    _gestureMode = true;
                    foreach (var (s, o) in _contacts)
                        if (o.Role == Role.Pending)
                            Promote(o, s);
                    return true;
                }
                return false;
            case Role.Lock or Role.Calc when !c.Done:
                double dist = Dist(c.StartX, c.StartY, c.X, c.Y);
                if (c.Sliding)
                {
                    if (dist - c.ExitDist > Mm(SlideMm))
                    {
                        c.Done = true;
                        _out.Add(new(NumberPadEvent.Calculator));
                    }
                    return false;
                }
                var hit = _surface.Hit(c.X, c.Y);
                if (c.Role == Role.Lock ? hit.Toggle : hit.Calc)
                    return false;
                c.Due = -1;
                c.Sliding = c.Role == Role.Calc || _surface.Layout.LockSlide;
                c.Done = !c.Sliding;
                c.ExitDist = dist;
                return false;
            default:
                return false;
        }
    }

    private void Lift(Contact c, long now)
    {
        if (c.Role == Role.Lock && !c.Fired && Active && c.Cell >= 0 && _surface.Hit(c.X, c.Y).Cell == c.Cell)
        {
            var keys = _surface.Layout.Cells[c.Cell]!;
            _out.Add(new(NumberPadEvent.KeyDown, keys));
            _out.Add(new(NumberPadEvent.KeyUp, keys));
        }
        else if (c.Role == Role.Pending)
        {
            int cell = _surface.Hit(c.X, c.Y).Cell;
            var keys = _surface.Layout.Cells[cell >= 0 ? cell : c.Cell];
            if (keys != null)
            {
                _out.Add(new(NumberPadEvent.KeyDown, keys));
                _out.Add(new(NumberPadEvent.KeyUp, keys));
            }
        }
        else if (c.Role == Role.Key && c.Down != null)
            _out.Add(new(NumberPadEvent.KeyUp, c.Down));
        else if (c.Role == Role.Pointer)
        {
            _liftTime = now;
            _liftX = c.X;
            _liftY = c.Y;
        }
    }

    // Replay the withheld path so the pointer sees the whole stroke.
    private void Promote(Contact c, int slot)
    {
        c.Role = Role.Pointer;
        for (int i = 0; i < c.Trail.Count - 1; i++)
            EmitPointer(slot, c.Trail[i].X, c.Trail[i].Y);
        c.Trail.Clear();
    }

    private void EmitPointer(int overrideSlot = -1, int x = 0, int y = 0)
    {
        var list = new List<PointerContact>();
        foreach (var (slot, c) in _contacts)
            if (c.Role == Role.Pointer)
                list.Add(slot == overrideSlot ? new(slot, x, y) : new(slot, c.X, c.Y));
        _out.Add(new(NumberPadEvent.Pointer, Frame: new PointerFrame(list, _button)));
    }
}

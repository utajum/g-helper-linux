namespace GHelper.Linux.Input;

/// <summary>A touch the virtual touchpad should see, keyed by the real slot.</summary>
public readonly record struct PointerContact(int Slot, int X, int Y);

/// <summary>Pointer touches and the clickpad button after one touchpad frame.</summary>
public sealed record PointerFrame(IReadOnlyList<PointerContact> Contacts, bool Button);

/// <summary>
/// Turns <see cref="PointerFrame"/>s into evdev multi-touch protocol B events
/// for the virtual touchpad: slots, own tracking ids, BTN_TOUCH, the
/// BTN_TOOL_* finger count, single-touch ABS_X/Y and BTN_LEFT.
/// </summary>
public sealed class PointerWriter
{
    private static readonly ushort[] Tools =
    {
        EvdevInterop.BTN_TOOL_FINGER, EvdevInterop.BTN_TOOL_DOUBLETAP, EvdevInterop.BTN_TOOL_TRIPLETAP,
        EvdevInterop.BTN_TOOL_QUADTAP, EvdevInterop.BTN_TOOL_QUINTTAP,
    };

    private readonly Dictionary<int, PointerContact> _prev = new();
    private int _nextId;
    private int _tool = -1;
    private bool _button;

    /// <summary>Events for one frame, ending with SYN_REPORT.</summary>
    public List<(ushort Type, ushort Code, int Value)> Encode(PointerFrame frame)
    {
        var ev = new List<(ushort, ushort, int)>();
        var cur = frame.Contacts.ToDictionary(c => c.Slot);

        foreach (int slot in _prev.Keys.Where(s => !cur.ContainsKey(s)).ToList())
        {
            ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, slot));
            ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_TRACKING_ID, -1));
            _prev.Remove(slot);
        }
        foreach (var c in frame.Contacts.OrderBy(c => c.Slot))
        {
            bool known = _prev.TryGetValue(c.Slot, out var p);
            if (known && p == c)
                continue;
            ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_SLOT, c.Slot));
            if (!known)
                ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_TRACKING_ID, _nextId++ & 0xffff));
            if (!known || p.X != c.X)
                ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_POSITION_X, c.X));
            if (!known || p.Y != c.Y)
                ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_MT_POSITION_Y, c.Y));
            _prev[c.Slot] = c;
        }

        int count = frame.Contacts.Count;
        int tool = count == 0 ? -1 : Math.Min(count, Tools.Length) - 1;
        if (tool != _tool)
        {
            if (_tool < 0)
                ev.Add((EvdevInterop.EV_KEY, EvdevInterop.BTN_TOUCH, 1));
            else
                ev.Add((EvdevInterop.EV_KEY, Tools[_tool], 0));
            if (tool < 0)
                ev.Add((EvdevInterop.EV_KEY, EvdevInterop.BTN_TOUCH, 0));
            else
                ev.Add((EvdevInterop.EV_KEY, Tools[tool], 1));
            _tool = tool;
        }
        if (count > 0)
        {
            var first = frame.Contacts.MinBy(c => c.Slot);
            ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_X, first.X));
            ev.Add((EvdevInterop.EV_ABS, EvdevInterop.ABS_Y, first.Y));
        }
        if (frame.Button != _button)
        {
            ev.Add((EvdevInterop.EV_KEY, EvdevInterop.BTN_LEFT, frame.Button ? 1 : 0));
            _button = frame.Button;
        }
        ev.Add((EvdevInterop.EV_SYN, EvdevInterop.SYN_REPORT, 0));
        return ev;
    }
}

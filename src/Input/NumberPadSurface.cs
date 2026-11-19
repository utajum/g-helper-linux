namespace GHelper.Linux.Input;

/// <summary>Touchpad evdev range: maxima and resolution in units per mm (0 if unknown).</summary>
public readonly record struct TouchpadRange(int XMax, int YMax, int XRes, int YRes);

/// <summary>What a touch lands on. <see cref="Cell"/> is -1 when no key fires.</summary>
public readonly record struct NumberPadHit(bool Toggle, int Cell, bool Calc = false);

/// <summary>
/// A layout placed on one touchpad, with every zone converted to evdev units
/// once. With driver geometry and a known resolution, sizes are in mm and
/// split as in the ASUS Windows driver; else the grid spans the whole pad and
/// the toggle zone is a corner fraction. On driver layouts a touch in the
/// NumLock or calculator zone never types, as on Windows.
/// </summary>
public sealed class NumberPadSurface
{
    private readonly Zone _toggle;
    private readonly Zone? _calc;
    private readonly Bands _cols, _rows;
    private readonly bool _toggleTypes;

    public NumberPadSurface(NumberPadLayout layout, TouchpadRange r)
    {
        Layout = layout;
        // Without a resolution, assume a pad about 100 mm wide.
        UnitsPerMm = r.XRes > 0 ? r.XRes : Math.Max(1, r.XMax / 100.0);
        var g = layout.Geometry;
        _toggleTypes = g == null;
        if (g != null && r.XRes > 0 && r.YRes > 0)
        {
            long xr = r.XRes, yr = r.YRes;
            long top = g.LockTop * yr, bottom = (g.LockTop + g.LockHeight) * yr;
            _toggle = g.LockOnLeft
                ? new(g.LockInset * xr, (g.LockInset + g.LockEdge) * xr, top, bottom)
                : new(r.XMax - g.LockEdge * xr, r.XMax - g.LockInset * xr, top, bottom);
            if (g.CalcWidth > 0)
                _calc = new(0, g.CalcWidth * xr, 0, g.CalcHeight * yr);
            _cols = Bands.Driver(g.Left * xr, g.FirstCol * xr, g.LastCol * xr, r.XMax - g.Right * xr, layout.Cols);
            _rows = Bands.Driver(g.Top * yr, g.FirstRow * yr, g.LastRow * yr, r.YMax - g.Bottom * yr, layout.Rows);
        }
        else
        {
            long cx = (long)(r.XMax * layout.CornerXMinFrac), cy = (long)(r.YMax * layout.CornerYMaxFrac);
            _toggle = g?.LockOnLeft == true ? new(0, r.XMax - cx, 0, cy) : new(cx, r.XMax, 0, cy);
            _cols = Bands.Even(r.XMax, layout.Cols);
            _rows = Bands.Even(r.YMax, layout.Rows);
        }
    }

    public NumberPadLayout Layout { get; }

    /// <summary>Evdev units per mm on the X axis; distances use this for both axes, as on Windows.</summary>
    public double UnitsPerMm { get; }

    /// <summary>Toggle flag, calculator flag and key cell under a touch.</summary>
    public NumberPadHit Hit(int x, int y)
    {
        bool toggle = _toggle.Contains(x, y);
        if (toggle && !_toggleTypes)
            return new(toggle, -1);
        if (_calc?.Contains(x, y) == true)
            return new(toggle, -1, true);
        int col = _cols.Of(x), row = _rows.Of(y);
        if (col < 0 || row < 0)
            return new(toggle, -1);
        int idx = row * Layout.Cols + col;
        return new(toggle, Layout.Cells[idx] == null ? -1 : idx);
    }

    private readonly record struct Zone(long X0, long X1, long Y0, long Y1)
    {
        public bool Contains(long x, long y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    }

    // Windows axis split: dead border, fixed first and last band, equal middle bands.
    private readonly record struct Bands(long Lead, long First, long Last, long End, long Mid, int Count)
    {
        public static Bands Driver(long lead, long first, long last, long end, int count)
            => new(lead, first, last, end, Math.Max(1, (end - lead - first - last) / (count - 2)), count);

        public static Bands Even(long max, int count)
        {
            long size = Math.Max(1, max / count);
            return new(0, size, size, max, size, count);
        }

        public int Of(long v)
        {
            if (v >= Lead && v < Lead + First)
                return 0;
            if (v > End - Last && v <= End)
                return Count - 1;
            if (v < Lead || v > End)
                return -1;
            return (int)Math.Min(Count - 1, (v - Lead - First) / Mid + 1);
        }
    }
}

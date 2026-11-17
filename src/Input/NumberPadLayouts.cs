namespace GHelper.Linux.Input;

/// <summary>
/// A NumberPad key grid. <see cref="Cells"/> is row-major with
/// Rows*Cols entries; each entry is a press-and-release key sequence
/// (presses fire in order, releases in reverse, so macros like Shift+5
/// get textbook modifier ordering). Null entries are inert.
/// </summary>
public sealed class NumberPadLayout
{
    public string Name { get; init; } = "";
    public int Rows { get; init; }
    public int Cols { get; init; }
    public ushort[]?[] Cells { get; init; } = Array.Empty<ushort[]?>();

    // Top-right activation zone as fractions of the touchpad's evdev range; used without mm data.
    public double CornerXMinFrac { get; init; } = 0.85;
    public double CornerYMaxFrac { get; init; } = 0.20;

    // ASUS Windows driver geometry; null for layouts without driver data.
    public NumberPadGeometry? Geometry { get; init; }
    public long HoldMs { get; init; } = 1000;

    // Sliding out of the NumLock icon opens a calculator (driver layout bit 0x200000).
    public bool LockSlide { get; init; }
}

/// <summary>
/// Key area and zones from the ASUS Windows driver, in mm, in INF
/// NUMPADLAYOUT_01..14 order. The NumLock zone spans LockEdge..LockInset from
/// the right edge (or LockInset..LockInset+LockEdge from the left) and
/// LockTop..LockTop+LockHeight from the top. The calculator zone (INF NumAdv)
/// is the top-left CalcWidth x CalcHeight corner; 0 means none.
/// </summary>
public sealed record NumberPadGeometry(
    int FirstCol, int LastCol, int FirstRow, int LastRow,
    int LockEdge, int LockHeight,
    int Left, int Right, int Top, int Bottom,
    int CalcWidth, int CalcHeight,
    int LockInset, int LockTop, bool LockOnLeft = false);

/// <summary>
/// Static NumberPad layouts. DMI overrides win; else the touchpad ACPI ID
/// picks a layout from the ASUS Windows driver; else a universal 4x4 grid.
/// </summary>
public static class NumberPadLayouts
{
    // KEY_* codes from linux/input-event-codes.h (numpad subset).
    public const ushort KEY_5 = 6;
    public const ushort KEY_BACKSPACE = 14;
    public const ushort KEY_LEFTSHIFT = 42;
    public const ushort KEY_KPASTERISK = 55;
    public const ushort KEY_NUMLOCK = 69;
    public const ushort KEY_KP7 = 71;
    public const ushort KEY_KP8 = 72;
    public const ushort KEY_KP9 = 73;
    public const ushort KEY_KPMINUS = 74;
    public const ushort KEY_KP4 = 75;
    public const ushort KEY_KP5 = 76;
    public const ushort KEY_KP6 = 77;
    public const ushort KEY_KPPLUS = 78;
    public const ushort KEY_KP1 = 79;
    public const ushort KEY_KP2 = 80;
    public const ushort KEY_KP3 = 81;
    public const ushort KEY_KP0 = 82;
    public const ushort KEY_KPDOT = 83;
    public const ushort KEY_KPENTER = 96;
    public const ushort KEY_KPSLASH = 98;
    public const ushort KEY_KPEQUAL = 117;

    /// <summary>Every key code any layout can emit; the uinput device declares these.</summary>
    public static readonly ushort[] AllKeys =
    {
        KEY_KP0, KEY_KP1, KEY_KP2, KEY_KP3, KEY_KP4,
        KEY_KP5, KEY_KP6, KEY_KP7, KEY_KP8, KEY_KP9,
        KEY_KPDOT, KEY_KPENTER, KEY_KPPLUS, KEY_KPMINUS,
        KEY_KPASTERISK, KEY_KPSLASH, KEY_BACKSPACE,
        KEY_NUMLOCK, KEY_KPEQUAL, KEY_LEFTSHIFT, KEY_5,
    };

    private static ushort[] K(params ushort[] keys) => keys;

    /// <summary>Default 4x4 grid mirroring a standard physical numpad.</summary>
    public static readonly NumberPadLayout Universal4x4 = new()
    {
        Name = "Universal",
        Rows = 4,
        Cols = 4,
        Cells = new ushort[]?[]
        {
            K(KEY_KP7), K(KEY_KP8),   K(KEY_KP9),     K(KEY_BACKSPACE),
            K(KEY_KP4), K(KEY_KP5),   K(KEY_KP6),     K(KEY_KPASTERISK),
            K(KEY_KP1), K(KEY_KP2),   K(KEY_KP3),     K(KEY_KPMINUS),
            K(KEY_KP0), K(KEY_KPDOT), K(KEY_KPENTER), K(KEY_KPPLUS),
        },
    };

    /// <summary>
    /// 5x4 grid with a dedicated operator column (Zenbook UX3405MA family).
    /// The rightmost column stacks a tall Backspace over rows 0+1; the %
    /// cell is a Shift+5 macro because Linux has no KEY_KPPERCENT.
    /// </summary>
    public static readonly NumberPadLayout Operator5x4 = new()
    {
        Name = "Operator column",
        Rows = 4,
        Cols = 5,
        Cells = new ushort[]?[]
        {
            K(KEY_KP7), K(KEY_KP8),   K(KEY_KP9),     K(KEY_KPSLASH),    K(KEY_BACKSPACE),
            K(KEY_KP4), K(KEY_KP5),   K(KEY_KP6),     K(KEY_KPASTERISK), K(KEY_BACKSPACE),
            K(KEY_KP1), K(KEY_KP2),   K(KEY_KP3),     K(KEY_KPMINUS),    K(KEY_LEFTSHIFT, KEY_5),
            K(KEY_KP0), K(KEY_KPDOT), K(KEY_KPENTER), K(KEY_KPPLUS),     K(KEY_KPEQUAL),
        },
    };

    /// <summary>
    /// Windows driver grid for ROG Strix (GH*) and NumberPad 1.0 (NHL/NHM/NHS)
    /// touchpads: digits in a 3x4 area, an operator column, and tall
    /// Backspace/Enter cells on the far right.
    /// </summary>
    private static readonly ushort[]?[] RogStrix5x4Cells =
    {
        K(KEY_KP7), K(KEY_KP8), K(KEY_KP9),   K(KEY_KPSLASH),    K(KEY_BACKSPACE),
        K(KEY_KP4), K(KEY_KP5), K(KEY_KP6),   K(KEY_KPASTERISK), K(KEY_BACKSPACE),
        K(KEY_KP1), K(KEY_KP2), K(KEY_KP3),   K(KEY_KPMINUS),    K(KEY_KPENTER),
        K(KEY_KP0), K(KEY_KP0), K(KEY_KPDOT), K(KEY_KPPLUS),     K(KEY_KPENTER),
    };

    private static readonly NumberPadLayout Strix5x4 = new() { Rows = 4, Cols = 5, Cells = RogStrix5x4Cells };

    /// <summary>Windows driver 4x5 grid with =, % and Backspace on top (NVM).</summary>
    private static readonly NumberPadLayout OperatorRow4x5 = new()
    {
        Rows = 5,
        Cols = 4,
        Cells = new ushort[]?[]
        {
            K(KEY_KPEQUAL), K(KEY_LEFTSHIFT, KEY_5), K(KEY_BACKSPACE), K(KEY_BACKSPACE),
            K(KEY_KP7),     K(KEY_KP8),              K(KEY_KP9),       K(KEY_KPSLASH),
            K(KEY_KP4),     K(KEY_KP5),              K(KEY_KP6),       K(KEY_KPASTERISK),
            K(KEY_KP1),     K(KEY_KP2),              K(KEY_KP3),       K(KEY_KPMINUS),
            K(KEY_KP0),     K(KEY_KPDOT),            K(KEY_KPENTER),   K(KEY_KPPLUS),
        },
    };

    /// <summary>Windows driver classic 4x5 grid (GV*); the NumLock icon sits on cell 0.</summary>
    private static readonly NumberPadLayout Classic4x5 = new()
    {
        Rows = 5,
        Cols = 4,
        Cells = new ushort[]?[]
        {
            null,       K(KEY_KPSLASH), K(KEY_KPASTERISK), K(KEY_KPMINUS),
            K(KEY_KP7), K(KEY_KP8),     K(KEY_KP9),        K(KEY_KPPLUS),
            K(KEY_KP4), K(KEY_KP5),     K(KEY_KP6),        K(KEY_KPPLUS),
            K(KEY_KP1), K(KEY_KP2),     K(KEY_KP3),        K(KEY_KPENTER),
            K(KEY_KP0), K(KEY_KP0),     K(KEY_KPDOT),      K(KEY_KPENTER),
        },
    };

    private static NumberPadLayout Driver(string name, NumberPadLayout grid, NumberPadGeometry geometry,
        long holdMs = 320, double cornerX = 0.85, double cornerY = 0.20, bool lockSlide = false) => new()
        {
            Name = name,
            Rows = grid.Rows,
            Cols = grid.Cols,
            Cells = grid.Cells,
            Geometry = geometry,
            HoldMs = holdMs,
            CornerXMinFrac = cornerX,
            CornerYMaxFrac = cornerY,
            LockSlide = lockSlide,
        };

    // ASUS Windows driver 17.0.0.27-29 layouts; hold = NumLock_Trigger_FC x 8 ms (62 on GH2/GH3, else 40).
    private static readonly Dictionary<string, NumberPadLayout> DriverLayouts = new[]
    {
        Driver("NHXL", Operator5x4, new(21, 21, 13, 16, 10, 12, 8, 8, 4, 0, 10, 12, 0, 0)),
        Driver("NHL", Strix5x4, new(21, 21, 20, 16, 10, 8, 0, 0, 0, 0, 0, 0, 0, 0)),
        Driver("NHM", Strix5x4, new(21, 21, 13, 13, 10, 7, 0, 0, 0, 0, 0, 0, 0, 0)),
        Driver("NHS", Strix5x4, new(20, 21, 11, 15, 10, 7, 0, 0, 0, 0, 0, 0, 0, 0)),
        Driver("NH4XLNDZ", Operator5x4, new(24, 24, 18, 21, 10, 12, 12, 12, 12, 0, 10, 12, 0, 0)),
        Driver("NH3XLNDZ", Operator5x4, new(20, 21, 16, 16, 10, 12, 9, 9, 10, 0, 10, 12, 0, 0)),
        Driver("NH2XLNDZ", Operator5x4, new(21, 21, 15, 17, 10, 12, 10, 10, 9, 0, 10, 12, 0, 0)),
        Driver("NH2XLNDZ9LED", Operator5x4, new(20, 20, 14, 16, 8, 10, 7, 7, 10, 0, 8, 10, 0, 0)),
        Driver("NHXLNDZ", Operator5x4, new(22, 22, 13, 14, 8, 12, 6, 6, 8, 0, 8, 12, 0, 0)),
        Driver("NHLNDZ", Operator5x4, new(17, 17, 13, 17, 8, 12, 5, 5, 10, 0, 8, 12, 0, 0)),
        Driver("NHMNDZ", Operator5x4, new(17, 19, 11, 13, 8, 12, 3, 3, 8, 0, 8, 12, 0, 0)),
        Driver("NVM", OperatorRow4x5, new(12, 12, 13, 17, 7, 7, 3, 4, 6, 0, 7, 7, 0, 0)),
        Driver("NH2XLOL", Operator5x4, new(21, 23, 14, 16, 10, 12, 7, 7, 10, 0, 0, 0, 0, 0), lockSlide: true),
        Driver("NHLOL", Operator5x4, new(18, 18, 14, 16, 10, 12, 3, 3, 10, 0, 0, 0, 0, 0), lockSlide: true),
        Driver("NHMOL", Operator5x4, new(18, 18, 12, 13, 10, 12, 3, 3, 7, 0, 0, 0, 0, 0), lockSlide: true),
        Driver("NHMOLFP", Operator5x4, new(18, 18, 12, 10, 10, 13, 3, 3, 13, 0, 0, 0, 0, 0, LockOnLeft: true), lockSlide: true),
        Driver("GH3XLNDZ", Strix5x4, new(17, 20, 15, 20, 31, 18, 13, 11, 9, 0, 0, 0, 11, 9), 496, 0.75, 0.25),
        Driver("GH2XLNDZ", Strix5x4, new(22, 22, 16, 20, 30, 16, 10, 10, 6, 0, 0, 0, 10, 6), 496),
        Driver("GHM", Strix5x4, new(20, 20, 13, 13, 20, 13, 0, 0, 0, 0, 0, 0, 0, 0)),
        Driver("GVL", Classic4x5, new(13, 13, 17, 16, 13, 17, 2, 2, 7, 6, 0, 0, 2, 7, LockOnLeft: true)),
        Driver("GVM_A", Classic4x5, new(14, 14, 11, 12, 14, 14, 0, 0, 0, 0, 0, 0, 0, 0, LockOnLeft: true)),
        Driver("CH3XLNDZ", Operator5x4, new(22, 22, 16, 18, 10, 12, 7, 8, 10, 0, 0, 0, 0, 0)),
        Driver("CHXLNDZ", Operator5x4, new(22, 22, 12, 15, 8, 12, 8, 8, 12, 0, 0, 0, 0, 0)),
        Driver("GH4XLNDZ", Strix5x4, new(27, 22, 15, 15, 22, 12, 6, 5, 18, 8, 0, 0, 4, 2)),
    }.ToDictionary(l => l.Name);

    // Driver hardware ID table; skipped: ASUE1404/ELAN1404 (GVM, no NumLock zone),
    // ASUE1700 (NHMSEAMLESS, gone in 17.0.0.28) and FTE1407 (FTE touchpads are not detected).
    private static readonly Dictionary<string, string> TouchpadIds = new (string Id, string Layout)[]
    {
        ("ELAN1200", "NH4XLNDZ"), ("ELAN1301", "NH4XLNDZ"), ("ASUP1204", "NH2XLOL"),
        ("ELAN1400", "NHL"), ("ELAN1401", "NHM"), ("ELAN1402", "NHS"),
        ("ELAN1405", "GHM"), ("ELAN1406", "NVM"), ("ELAN1407", "NHXL"),
        ("ASUE1400", "NHL"), ("ASUE1401", "NHM"), ("ASUE1402", "NHS"),
        ("ASUE1403", "GH2XLNDZ"), ("ASUE1405", "GHM"), ("ASUE1406", "NVM"),
        ("ASUE1407", "NHXL"), ("ASUE1408", "GVM_A"), ("ASUE1409", "NHMOL"),
        ("ASUE140A", "NHXLNDZ"), ("ASUE140B", "NHLNDZ"), ("ASUE140C", "NHMNDZ"),
        ("ASUE140D", "NH2XLNDZ"), ("ASUE140E", "NHXLNDZ"), ("ASUE140F", "GVL"),
        ("ASUE1410", "CHXLNDZ"), ("ASUE1411", "NHLOL"), ("ASUE1412", "NH3XLNDZ"),
        ("ASUE1413", "NH4XLNDZ"), ("ASUE1414", "NH2XLOL"), ("ASUE1415", "CH3XLNDZ"),
        ("ASUE1416", "GH3XLNDZ"), ("ASUE1417", "NH2XLNDZ9LED"), ("ASUE1418", "CH3XLNDZ"),
        ("ASUE1800", "NHMOLFP"),
        ("ASUF1407", "NHXL"), ("ASUF1411", "NHLOL"), ("ASUF1415", "NH2XLNDZ9LED"), ("ASUF1416", "GH3XLNDZ"),
        ("ASUP1411", "NHLOL"), ("ASUP1412", "NHMOL"), ("ASUP1413", "NH4XLNDZ"),
        ("ASUP1414", "NH2XLOL"), ("ASUP1415", "NH2XLNDZ9LED"), ("ASUP1416", "NH2XLNDZ"),
        ("ASCF1400", "GH4XLNDZ"), ("ASCP1400", "GH4XLNDZ"),
    }.ToDictionary(e => e.Id, e => e.Layout);

    // Substring of DMI product_name -> layout. First match wins.
    private static readonly (string Needle, NumberPadLayout Layout)[] Overrides =
    {
        ("G533", DriverLayouts["GH2XLNDZ"]),
        ("G614", DriverLayouts["GH3XLNDZ"]),
        ("G634", DriverLayouts["GH3XLNDZ"]),
        ("UX3405MA", Operator5x4),
        ("UM3402", Operator5x4),
        ("B3302", Operator5x4),
    };

    /// <summary>
    /// Layout for a laptop: DMI override first, then the touchpad ACPI ID
    /// ("ASUE1416" in "ASUE1416:00 04F3:3275 Touchpad"), then <see cref="Universal4x4"/>.
    /// </summary>
    public static NumberPadLayout Resolve(string productName, string touchpadName = "")
    {
        foreach (var (needle, layout) in Overrides)
            if (productName.Contains(needle))
                return layout;
        return TouchpadIds.TryGetValue(touchpadName.Split(':')[0], out string? name)
            ? DriverLayouts[name]
            : Universal4x4;
    }
}

namespace GHelper.Linux.Input;

/// <summary>
/// A proportional NumberPad grid. <see cref="Cells"/> is row-major with
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

    // Top-right activation zone as fractions of the touchpad's evdev range.
    // Models differ: most layouts keep the printed NumLK icon in the
    // extreme corner, while G614/G634 put its label in the rightmost
    // quarter of the touchpad.
    public double CornerXMinFrac { get; init; } = 0.85;
    public double CornerYMaxFrac { get; init; } = 0.20;
}

/// <summary>
/// Static NumberPad layouts. A single universal 4x4 grid covers most
/// models; a DMI-matched override table maps specific product names to a
/// 5x4 grid with a dedicated operator column. Adding a new override is
/// purely additive.
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
    /// Shared key grid for the ROG Strix 16/18 touchpads: digits in a 3x4
    /// area, a dedicated operator column, and tall Backspace/Enter cells on
    /// the far right. Read-only after construction.
    /// </summary>
    private static readonly ushort[]?[] RogStrix5x4Cells =
    {
        K(KEY_KP7), K(KEY_KP8), K(KEY_KP9),   K(KEY_KPSLASH),    K(KEY_BACKSPACE),
        K(KEY_KP4), K(KEY_KP5), K(KEY_KP6),   K(KEY_KPASTERISK), K(KEY_BACKSPACE),
        K(KEY_KP1), K(KEY_KP2), K(KEY_KP3),   K(KEY_KPMINUS),    K(KEY_KPENTER),
        K(KEY_KP0), K(KEY_KP0), K(KEY_KPDOT), K(KEY_KPPLUS),     K(KEY_KPENTER),
    };

    /// <summary>
    /// ROG Strix G533 layout. Kept at the original activation zone so G533
    /// behavior does not change until it is verified on that hardware.
    /// </summary>
    public static readonly NumberPadLayout RogG5335x4 = new()
    {
        Name = "ROG G533",
        Rows = 4,
        Cols = 5,
        Cells = RogStrix5x4Cells,
    };

    /// <summary>
    /// ROG Strix G614/G634 layout. Same 5x4 key grid as the G533, but the
    /// printed NUMLK icon spans the rightmost ~25% x ~23% of the touchpad,
    /// not just its extreme corner, so holding the label toggles the pad.
    /// </summary>
    public static readonly NumberPadLayout RogG6145x4 = new()
    {
        Name = "ROG G614/G634",
        Rows = 4,
        Cols = 5,
        CornerXMinFrac = 0.75,
        CornerYMaxFrac = 0.25,
        Cells = RogStrix5x4Cells,
    };

    // Substring of DMI product_name -> layout. First match wins.
    private static readonly (string Needle, NumberPadLayout Layout)[] Overrides =
    {
        ("G533", RogG5335x4),
        ("G614", RogG6145x4),
        ("G634", RogG6145x4),
        ("UX3405MA", Operator5x4),
        ("UM3402", Operator5x4),
        ("B3302", Operator5x4),
    };

    /// <summary>Layout for the given DMI product name, falling back to <see cref="Universal4x4"/>.</summary>
    public static NumberPadLayout ForProduct(string productName)
    {
        foreach (var (needle, layout) in Overrides)
            if (productName.Contains(needle))
                return layout;
        return Universal4x4;
    }
}

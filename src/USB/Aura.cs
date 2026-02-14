using System.Text;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.USB;

/// <summary>
/// AURA keyboard RGB modes.
/// Values match the AURA HID protocol byte values.
/// </summary>
public enum AuraMode : int
{
    AuraStatic = 0,
    AuraBreathe = 1,
    AuraColorCycle = 2,
    AuraRainbow = 3,
    Star = 4,
    Rain = 5,
    Highlight = 6,
    Laser = 7,
    Ripple = 8,
    AuraStrobe = 10,
    Comet = 11,
    Flash = 12,
}

/// <summary>
/// AURA animation speed.
/// </summary>
public enum AuraSpeed : int
{
    Slow = 0,
    Normal = 1,
    Fast = 2,
}

/// <summary>
/// Keyboard backlight power zone flags (boot/sleep/shutdown/awake).
/// Controls which lighting zones remain on in each power state.
/// </summary>
public class AuraPower
{
    public bool BootLogo, BootKeyb, AwakeLogo, AwakeKeyb;
    public bool SleepLogo, SleepKeyb, ShutdownLogo, ShutdownKeyb;
    public bool BootBar, AwakeBar, SleepBar, ShutdownBar;
    public bool BootLid, AwakeLid, SleepLid, ShutdownLid;
    public bool BootRear, AwakeRear, SleepRear, ShutdownRear;
}

/// <summary>
/// Linux port of G-Helper's Aura.cs.
/// Implements the ASUS AURA HID protocol for keyboard RGB control.
/// 
/// Protocol summary:
///   Message:    [0x5D, 0xB3, zone, mode, R, G, B, speed, direction, random, R2, G2, B2] (17 bytes)
///   Apply:      [0x5D, 0xB4]
///   Set:        [0x5D, 0xB5, 0, 0, 0]
///   Brightness: [0x5D, 0xBA, 0xC5, 0xC4, level]
///   Init:       [0x5D, 0xB9], "ASUS Tech.Inc.", [0x5D, 0x05, 0x20, 0x31, 0, 0x1A]
///   Power:      [0x5D, 0xBD, 0x01, keyb, bar, lid, rear, 0xFF]
///   Direct:     [0x5D, 0xBC, ...] (per-key or 4-zone)
/// </summary>
public static class Aura
{
    private static readonly byte[] MESSAGE_APPLY = { AsusHid.AURA_ID, 0xB4 };
    private static readonly byte[] MESSAGE_SET = { AsusHid.AURA_ID, 0xB5, 0, 0, 0 };

    private const int AURA_ZONES = 8;

    private static AuraMode _mode = AuraMode.AuraStatic;
    private static AuraSpeed _speed = AuraSpeed.Normal;

    public static byte ColorR = 255;
    public static byte ColorG = 255;
    public static byte ColorB = 255;

    public static byte Color2R = 0;
    public static byte Color2G = 0;
    public static byte Color2B = 0;

    private static bool _backlight = true;
    private static bool _initDirect = false;

    // Model detection (cached)
    private static bool _isACPI = AppConfig.IsTUF() || AppConfig.IsVivoZenPro();
    private static bool _isStrix = AppConfig.IsAdvancedRGB() && !AppConfig.IsNoDirectRGB();
    private static bool _isStrix4Zone = AppConfig.Is4ZoneRGB();
    private static bool _isSingleColor = AppConfig.IsSingleColor();

    // ── Mode dictionaries ──

    private static readonly Dictionary<AuraMode, string> ModesSingleColor = new()
    {
        { AuraMode.AuraStatic, "Static" },
        { AuraMode.AuraBreathe, "Breathe" },
        { AuraMode.AuraStrobe, "Strobe" },
    };

    private static readonly Dictionary<AuraMode, string> ModesStandard = new()
    {
        { AuraMode.AuraStatic, "Static" },
        { AuraMode.AuraBreathe, "Breathe" },
        { AuraMode.AuraColorCycle, "Color Cycle" },
        { AuraMode.AuraRainbow, "Rainbow" },
        { AuraMode.AuraStrobe, "Strobe" },
    };

    private static readonly Dictionary<AuraMode, string> ModesStrix = new()
    {
        { AuraMode.AuraStatic, "Static" },
        { AuraMode.AuraBreathe, "Breathe" },
        { AuraMode.AuraColorCycle, "Color Cycle" },
        { AuraMode.AuraRainbow, "Rainbow" },
        { AuraMode.Star, "Star" },
        { AuraMode.Rain, "Rain" },
        { AuraMode.Highlight, "Highlight" },
        { AuraMode.Laser, "Laser" },
        { AuraMode.Ripple, "Ripple" },
        { AuraMode.AuraStrobe, "Strobe" },
        { AuraMode.Comet, "Comet" },
        { AuraMode.Flash, "Flash" },
    };

    // ── Properties ──

    public static AuraMode Mode
    {
        get => _mode;
        set => _mode = GetModes().ContainsKey(value) ? value : AuraMode.AuraStatic;
    }

    public static AuraSpeed Speed
    {
        get => _speed;
        set => _speed = GetSpeeds().ContainsKey(value) ? value : AuraSpeed.Normal;
    }

    /// <summary>Whether the current mode supports a second color (Breathe only, non-ACPI).</summary>
    public static bool HasSecondColor()
    {
        return _mode == AuraMode.AuraBreathe && !_isACPI;
    }

    /// <summary>Whether the current mode uses a color at all (Rainbow/ColorCycle don't).</summary>
    public static bool UsesColor()
    {
        return _mode != AuraMode.AuraColorCycle && _mode != AuraMode.AuraRainbow;
    }

    // ── Mode/Speed lists ──

    public static Dictionary<AuraMode, string> GetModes()
    {
        if (_isSingleColor) return ModesSingleColor;

        if (AppConfig.IsAdvancedRGB() && !AppConfig.Is4ZoneRGB())
            return ModesStrix;

        var modes = new Dictionary<AuraMode, string>(ModesStandard);
        if (_isACPI) modes.Remove(AuraMode.AuraRainbow);
        return modes;
    }

    public static Dictionary<AuraSpeed, string> GetSpeeds()
    {
        return new Dictionary<AuraSpeed, string>
        {
            { AuraSpeed.Slow, "Slow" },
            { AuraSpeed.Normal, "Normal" },
            { AuraSpeed.Fast, "Fast" },
        };
    }

    // ── Color helpers ──

    public static void SetColor(int argb)
    {
        ColorR = (byte)((argb >> 16) & 0xFF);
        ColorG = (byte)((argb >> 8) & 0xFF);
        ColorB = (byte)(argb & 0xFF);
    }

    public static void SetColor2(int argb)
    {
        Color2R = (byte)((argb >> 16) & 0xFF);
        Color2G = (byte)((argb >> 8) & 0xFF);
        Color2B = (byte)(argb & 0xFF);
    }

    public static int GetColorArgb()
    {
        return (255 << 24) | (ColorR << 16) | (ColorG << 8) | ColorB;
    }

    public static int GetColor2Argb()
    {
        return (255 << 24) | (Color2R << 16) | (Color2G << 8) | Color2B;
    }

    // ── Protocol messages ──

    /// <summary>
    /// Build the 17-byte AURA mode message.
    /// Format: [0x5D, 0xB3, zone, mode, R, G, B, speed, direction, random, R2, G2, B2]
    /// </summary>
    public static byte[] AuraMessage(AuraMode mode, byte r, byte g, byte b,
                                      byte r2, byte g2, byte b2,
                                      int speed, bool mono = false)
    {
        byte[] msg = new byte[17];
        msg[0] = AsusHid.AURA_ID;
        msg[1] = 0xB3;
        msg[2] = 0x00; // Zone
        msg[3] = (byte)mode;
        msg[4] = r;
        msg[5] = mono ? (byte)0 : g;
        msg[6] = mono ? (byte)0 : b;
        msg[7] = (byte)speed;
        msg[8] = 0x00; // direction
        // Random color flag: if color is black, use random; if Breathe mode, mark as 2-color
        msg[9] = (r == 0 && g == 0 && b == 0)
            ? (byte)0xFF
            : (mode == AuraMode.AuraBreathe ? (byte)0x01 : (byte)0x00);
        msg[10] = r2;
        msg[11] = mono ? (byte)0 : g2;
        msg[12] = mono ? (byte)0 : b2;
        return msg;
    }

    /// <summary>
    /// Initialize the AURA device (handshake sequence).
    /// </summary>
    public static void Init()
    {
        AsusHid.Write(new List<byte[]>
        {
            new byte[] { AsusHid.AURA_ID, 0xB9 },
            Encoding.ASCII.GetBytes("]ASUS Tech.Inc."),
            new byte[] { AsusHid.AURA_ID, 0x05, 0x20, 0x31, 0, 0x1A },
        }, "AuraInit");
    }

    /// <summary>
    /// Set keyboard backlight brightness via HID.
    /// Level: 0=off, 1=low, 2=medium, 3=high
    /// </summary>
    public static void ApplyBrightness(int brightness, string log = "Backlight")
    {
        if (brightness == 0) _backlight = false;

        if (AppConfig.IsInputBacklight())
            AsusHid.WriteInput(new byte[] { AsusHid.INPUT_ID, 0xBA, 0xC5, 0xC4, (byte)brightness }, log);
        else
            AsusHid.Write(new byte[] { AsusHid.AURA_ID, 0xBA, 0xC5, 0xC4, (byte)brightness }, log);

        if (brightness > 0)
        {
            if (!_backlight) _initDirect = true;
            _backlight = true;
        }
    }

    /// <summary>
    /// Build the power zone control message.
    /// Controls which lighting zones stay on during boot/sleep/shutdown.
    /// </summary>
    public static byte[] AuraPowerMessage(AuraPower flags)
    {
        byte keyb = 0, bar = 0, lid = 0, rear = 0;

        if (flags.BootLogo) keyb |= 1 << 0;
        if (flags.BootKeyb) keyb |= 1 << 1;
        if (flags.AwakeLogo) keyb |= 1 << 2;
        if (flags.AwakeKeyb) keyb |= 1 << 3;
        if (flags.SleepLogo) keyb |= 1 << 4;
        if (flags.SleepKeyb) keyb |= 1 << 5;
        if (flags.ShutdownLogo) keyb |= 1 << 6;
        if (flags.ShutdownKeyb) keyb |= 1 << 7;

        if (flags.AwakeBar) bar |= 1 << 0;
        if (flags.BootBar) bar |= 1 << 1;
        if (flags.AwakeBar) bar |= 1 << 2;
        if (flags.SleepBar) bar |= 1 << 3;
        if (flags.ShutdownBar) bar |= 1 << 4;

        if (flags.BootLid) lid |= 1 << 0;
        if (flags.AwakeLid) lid |= 1 << 1;
        if (flags.SleepLid) lid |= 1 << 2;
        if (flags.ShutdownLid) lid |= 1 << 3;
        if (flags.BootLid) lid |= 1 << 4;
        if (flags.AwakeLid) lid |= 1 << 5;
        if (flags.SleepLid) lid |= 1 << 6;
        if (flags.ShutdownLid) lid |= 1 << 7;

        if (flags.BootRear) rear |= 1 << 0;
        if (flags.AwakeRear) rear |= 1 << 1;
        if (flags.SleepRear) rear |= 1 << 2;
        if (flags.ShutdownRear) rear |= 1 << 3;
        if (flags.BootRear) rear |= 1 << 4;
        if (flags.AwakeRear) rear |= 1 << 5;
        if (flags.SleepRear) rear |= 1 << 6;
        if (flags.ShutdownRear) rear |= 1 << 7;

        return new byte[] { AsusHid.AURA_ID, 0xBD, 0x01, keyb, bar, lid, rear, 0xFF };
    }

    /// <summary>
    /// Apply the current AURA power zone settings from config.
    /// </summary>
    public static void ApplyPower()
    {
        var flags = new AuraPower
        {
            AwakeKeyb = AppConfig.IsNotFalse("keyboard_awake"),
            BootKeyb = AppConfig.IsNotFalse("keyboard_boot"),
            SleepKeyb = AppConfig.IsNotFalse("keyboard_sleep"),
            ShutdownKeyb = AppConfig.IsNotFalse("keyboard_shutdown"),

            AwakeLogo = AppConfig.IsNotFalse("keyboard_awake_logo"),
            BootLogo = AppConfig.IsNotFalse("keyboard_boot_logo"),
            SleepLogo = AppConfig.IsNotFalse("keyboard_sleep_logo"),
            ShutdownLogo = AppConfig.IsNotFalse("keyboard_shutdown_logo"),

            AwakeBar = AppConfig.IsNotFalse("keyboard_awake_bar"),
            BootBar = AppConfig.IsNotFalse("keyboard_boot_bar"),
            SleepBar = AppConfig.IsNotFalse("keyboard_sleep_bar"),
            ShutdownBar = AppConfig.IsNotFalse("keyboard_shutdown_bar"),

            AwakeLid = AppConfig.IsNotFalse("keyboard_awake_lid"),
            BootLid = AppConfig.IsNotFalse("keyboard_boot_lid"),
            SleepLid = AppConfig.IsNotFalse("keyboard_sleep_lid"),
            ShutdownLid = AppConfig.IsNotFalse("keyboard_shutdown_lid"),

            AwakeRear = AppConfig.IsNotFalse("keyboard_awake_lid"),
            BootRear = AppConfig.IsNotFalse("keyboard_boot_lid"),
            SleepRear = AppConfig.IsNotFalse("keyboard_sleep_lid"),
            ShutdownRear = AppConfig.IsNotFalse("keyboard_shutdown_lid"),
        };

        AsusHid.Write(AuraPowerMessage(flags));
    }

    // ── 4-zone direct RGB map ──

    /// <summary>
    /// Zone mapping for 4-zone Strix keyboards.
    /// 6 keyboard LEDs (Z1-Z4 + 2 unused) + 6 lightbar LEDs.
    /// </summary>
    private static readonly byte[] Packet4Zone = new byte[]
    {
        // Z1  Z2  Z3  Z4  NA  NA  (keyboard zones)
           0,  1,  2,  3,  0,  0,
        // RR  R   RM  LM  L   LL  (lightbar)
           7,  7,  6,  5,  4,  4,
    };

    // ── Per-key maps (for per-key RGB Strix) ──

    private static readonly byte[] PacketMap = new byte[]
    {
        /*          VDN  VUP  MICM HPFN ARMC */
                     2,   3,   4,   5,   6,
        /* ESC       F1   F2   F3   F4   F5   F6   F7   F8   F9  F10  F11  F12            DEL15 DEL17 PAUS PRT  HOME */
            21,      23,  24,  25,  26,  28,  29,  30,  31,  33,  34,  35,  36,             37,  38,  39,  40,  41,
        /* BKTK  1    2    3    4    5    6    7    8    9    0    -    =  BSPC BSPC BSPC PLY15 NMLK NMDV NMTM NMMI */
            42,  43,  44,  45,  46,  47,  48,  49,  50,  51,  52,  53,  54,  55,  56,  57,  58,  59,  60,  61,  62,
        /* TAB   Q    W    E    R    T    Y    U    I    O    P    [    ]    \            STP15 NM7  NM8  NM9  NMPL */
            63,  64,  65,  66,  67,  68,  69,  70,  71,  72,  73,  74,  75,  76,             79,  80,  81,  82,  83,
        /* CPLK  A    S    D    F    G    H    J    K    L    ;    "    #  ENTR ENTR ENTR PRV15 NM4  NM5  NM6  NMPL */
            84,  85,  86,  87,  88,  89,  90,  91,  92,  93,  94,  95,  96,  97,  98,  99, 100, 101, 102, 103, 104,
        /* LSFT ISO\ Z    X    C    V    B    N    M    ,    .    /  RSFT RSFT RSFT ARWU NXT15 NM1  NM2  NM3  NMER */
           105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 139, 121, 122, 123, 124, 125,
        /* LCTL LFNC LWIN LALT           SPC            RALT RFNC RCTL      ARWL ARWD ARWR PRT15      NM0  NMPD NMER */
           126, 127, 128, 129,           131,           135, 136, 137,      159, 160, 161, 142,       144, 145, 146,
        /* LB1  LB2  LB3                                                    ARW? ARWL? ARWD? ARWR?     LB4  LB5  LB6  */
           174, 173, 172,                                                   120, 140, 141, 143,       171, 170, 169,
        /* KSTN LOGO LIDL LIDR */
             0, 167, 176, 177,
    };

    private static readonly byte[] PacketZone = new byte[]
    {
        /*          VDN  VUP  MICM HPFN ARMC */
                     0,   0,   1,   1,   1,
        /* ESC       F1   F2   F3   F4   F5   F6   F7   F8   F9  F10  F11  F12            DEL15 DEL17 PAUS PRT  HOM */
             0,       0,   0,   1,   1,   1,   1,   2,   2,   2,   2,   3,   3,              3,   3,   3,   3,   3,
        /* BKTK  1    2    3    4    5    6    7    8    9    0    -    =  BSPC BSPC BSPC PLY15 NMLK NMDV NMTM NMMI */
             0,   0,   0,   0,   1,   1,   1,   1,   2,   2,   2,   2,   3,   3,   3,   3,   3,   3,   3,   3,   3,
        /* TAB   Q    W    E    R    T    Y    U    I    O    P    [    ]    \            STP15 NM7  NM8  NM9  NMPL */
             0,   0,   0,   0,   1,   1,   1,   1,   2,   2,   2,   2,   3,   3,              3,   3,   3,   3,   3,
        /* CPLK  A    S    D    F    G    H    J    K    L    ;    "    #  ENTR ENTR ENTR PRV15 NM4  NM5  NM6  NMPL */
             0,   0,   0,   0,   1,   1,   1,   1,   2,   2,   2,   2,   3,   3,   3,   3,   3,   3,   3,   3,   3,
        /* LSFT ISO\ Z    X    C    V    B    N    M    ,    .    /  RSFT RSFT RSFT ARWU NXT15 NM1  NM2  NM3  NMER */
             0,   0,   0,   0,   1,   1,   1,   1,   2,   2,   2,   2,   3,   3,   3,   3,   3,   3,   3,   3,   3,
        /* LCTL LFNC LWIN LALT           SPC            RALT RFNC RCTL      ARWL ARWD ARWR PRT15      NM0  NMPD NMER */
             0,   0,   0,   0,            1,              2,   2,   2,        3,   3,   3,   3,         3,   3,   3,
        /* LB1  LB1  LB3                                                    ARW? ARW? ARW? ARW?       LB4  LB5  LB6  */
             5,   5,   4,                                                     3,   3,   3,   3,         6,   7,   7,
        /* KSTN LOGO LIDL LIDR */
             3,   0,   0,   3,
    };

    /// <summary>
    /// Apply the full AURA mode (read config, set mode+color on device).
    /// This is the main entry point for applying keyboard lighting.
    /// </summary>
    public static void ApplyAura()
    {
        Mode = (AuraMode)AppConfig.Get("aura_mode");
        Speed = (AuraSpeed)AppConfig.Get("aura_speed");
        SetColor(AppConfig.Get("aura_color", unchecked((int)0xFFFFFFFF)));
        SetColor2(AppConfig.Get("aura_color2", 0));

        Logger.WriteLine($"ApplyAura: mode={Mode} speed={Speed} color=#{ColorR:X2}{ColorG:X2}{ColorB:X2} color2=#{Color2R:X2}{Color2G:X2}{Color2B:X2}");

        // Map speed enum to protocol byte values
        int speedByte = Speed switch
        {
            AuraSpeed.Normal => 0xEB,
            AuraSpeed.Fast => 0xF5,
            AuraSpeed.Slow => 0xE1,
            _ => 0xEB
        };

        // Build and send the mode message
        var msg = AuraMessage(Mode, ColorR, ColorG, ColorB,
                              Color2R, Color2G, Color2B,
                              speedByte, _isSingleColor);

        AsusHid.Write(new List<byte[]> { msg, MESSAGE_SET, MESSAGE_APPLY });

        // TUF uses ACPI path (handled via sysfs multi_intensity on Linux)
        if (_isACPI)
        {
            App.Wmi?.SetKeyboardRgb(ColorR, ColorG, ColorB);
        }
    }

    /// <summary>
    /// Apply a single color to the entire keyboard using direct RGB mode.
    /// Used by Heatmap, Battery, and Ambient modes.
    /// </summary>
    public static void ApplyDirect(byte r, byte g, byte b, bool init = false)
    {
        if (!_backlight) return;

        if (_isACPI)
        {
            App.Wmi?.SetKeyboardRgb(r, g, b);
            return;
        }

        if (AppConfig.IsNoDirectRGB())
        {
            AsusHid.Write(new List<byte[]>
            {
                AuraMessage(AuraMode.AuraStatic, r, g, b, r, g, b, 0xEB, _isSingleColor),
                MESSAGE_SET
            }, null);
            return;
        }

        if (_isStrix)
        {
            // For Strix, fill all zones with the same color
            var colors = new byte[AURA_ZONES * 3];
            for (int i = 0; i < AURA_ZONES; i++)
            {
                colors[i * 3] = r;
                colors[i * 3 + 1] = g;
                colors[i * 3 + 2] = b;
            }
            ApplyDirectZones(colors, init);
            return;
        }

        // Simple direct mode for non-Strix
        if (init || _initDirect)
        {
            _initDirect = false;
            Init();
            AsusHid.WriteAura(new byte[] { AsusHid.AURA_ID, 0xBC, 1 });
        }

        byte[] buffer = new byte[12];
        buffer[0] = AsusHid.AURA_ID;
        buffer[1] = 0xBC;
        buffer[2] = 1;
        buffer[3] = 1;
        buffer[9] = r;
        buffer[10] = g;
        buffer[11] = b;

        AsusHid.WriteAura(buffer);
    }

    /// <summary>
    /// Apply per-zone colors using direct mode (Strix per-key or 4-zone).
    /// colors: flat array of R,G,B triplets for each zone (up to AURA_ZONES).
    /// </summary>
    public static void ApplyDirectZones(byte[] colors, bool init = false)
    {
        if (!_backlight) return;

        const byte keySet = 167;
        const byte ledCount = 178;
        const ushort mapSize = 3 * ledCount;
        const byte ledsPerPacket = 16;

        byte[] buffer = new byte[64];
        byte[] keyBuf = new byte[mapSize];

        buffer[0] = AsusHid.AURA_ID;
        buffer[1] = 0xBC;
        buffer[2] = 0;
        buffer[3] = 1;
        buffer[4] = 1;
        buffer[5] = 1;
        buffer[6] = 0;
        buffer[7] = 0x10;

        if (init || _initDirect)
        {
            _initDirect = false;
            AsusHid.WriteAura(new byte[] { AsusHid.AURA_ID, 0xBC });
        }

        Array.Clear(keyBuf, 0, keyBuf.Length);

        if (!_isStrix4Zone) // per-key
        {
            for (int ledIndex = 0; ledIndex < PacketMap.Length; ledIndex++)
            {
                ushort offset = (ushort)(3 * PacketMap[ledIndex]);
                byte zone = PacketZone[ledIndex];
                int colorOff = zone * 3;

                if (colorOff + 2 < colors.Length)
                {
                    keyBuf[offset] = colors[colorOff];
                    keyBuf[offset + 1] = colors[colorOff + 1];
                    keyBuf[offset + 2] = colors[colorOff + 2];
                }
            }

            for (int i = 0; i < keySet; i += ledsPerPacket)
            {
                byte ledsRemaining = (byte)(keySet - i);
                if (ledsRemaining < ledsPerPacket) buffer[7] = ledsRemaining;

                buffer[6] = (byte)i;
                Buffer.BlockCopy(keyBuf, 3 * i, buffer, 9, 3 * buffer[7]);
                AsusHid.WriteAura(buffer);
            }
        }

        buffer[4] = 0x04;
        buffer[5] = 0x00;
        buffer[6] = 0x00;
        buffer[7] = 0x00;

        if (_isStrix4Zone)
        {
            // 4-zone mode
            int ledCount4Z = Packet4Zone.Length;
            for (int ledIndex = 0; ledIndex < ledCount4Z; ledIndex++)
            {
                byte zone = Packet4Zone[ledIndex];
                int colorOff = zone * 3;
                if (colorOff + 2 < colors.Length)
                {
                    keyBuf[ledIndex * 3] = colors[colorOff];
                    keyBuf[ledIndex * 3 + 1] = colors[colorOff + 1];
                    keyBuf[ledIndex * 3 + 2] = colors[colorOff + 2];
                }
            }
            Buffer.BlockCopy(keyBuf, 0, buffer, 9, 3 * ledCount4Z);
            AsusHid.WriteAura(buffer);
            return;
        }

        // Send remaining lightbar LEDs
        Buffer.BlockCopy(keyBuf, 3 * keySet, buffer, 9, 3 * (ledCount - keySet));
        AsusHid.WriteAura(buffer);
    }

    /// <summary>
    /// Check if AURA HID devices are available on this system.
    /// </summary>
    public static bool IsAvailable() => AsusHid.IsAvailable();
}

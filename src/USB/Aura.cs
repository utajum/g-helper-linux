using System.Text;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.USB;

/// <summary>
/// AURA keyboard RGB modes.
/// Values 0-12 match the AURA HID protocol byte values.
/// Values 20+ are software-driven "custom RGB" modes that don't use
/// the firmware mode bytes - they timer-paint via ApplyDirect.
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
    // CPU temp → keyboard color. Blue idle, red hot. Run a load (e.g. stress-ng --cpu 4) to warm it up.
    Heatmap = 20,
    // GPU mode → keyboard color. Eco=green, Std=yellow, Ultimate=red. Refreshes when you switch via tray menu.
    GpuMode = 21,
    // Screen colors -> keyboard (+ lightbar on Strix). Captured via X11 or the Wayland portal.
    Ambient = 22,
    // Battery % → keyboard color. Red low, yellow mid, lime full. Unplug AC to watch it drift.
    Battery = 23,
    // Two-color gradient across keyboard + lightbar zones (Strix only). Pick Color1 + Color2; blends left→right.
    Gradient = 24,
    // 8-zone diagnostic rainbow (R/O/Y/G/C/B/M/W) painted across keyboard + lightbar.
    // Used to verify zone wiring on per-key/multi-zone hardware after detection.
    ZoneTest = 25,
}

/// <summary>
/// Backlight type byte returned in <c>response[9]</c> from the AURA capability
/// query (see <see cref="HidrawHelper.QueryAuraCapabilities"/>).
/// </summary>
public enum AuraBacklightType : byte
{
    Unknown = 0x00,
    /// <summary>4-zone keyboard (Strix limited / G614 4-zone). Direct RGB targets 4 keyboard + 4 lightbar zones.</summary>
    MultiZone = 0x02,
    /// <summary>Per-key RGB Strix. Direct RGB targets ~167 individual keys + 11 lightbar/logo/lid zones.</summary>
    PerKey = 0x03,
    /// <summary>Single-zone (whole keyboard one color). Used by some lower-end Strix and dynamic-lighting models.</summary>
    SingleZone = 0x04,
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

    public static byte Color2R;
    public static byte Color2G;
    public static byte Color2B;

    // Rear-light state (Z13 only). The rear glow window/logo on the lid is a
    // separate AURA device (PID 0x18C6) and accepts its own AuraMessage with
    // independent mode + color. Speed shares the main keyboard's speed value.
    private static AuraMode _rearMode = AuraMode.AuraStatic;
    public static byte RearR = 255;
    public static byte RearG = 255;
    public static byte RearB = 255;

    private static bool _backlight = true;
    private static bool _initDirect;


    private static readonly System.Timers.Timer _customTimer = CreateCustomTimer();

    private static System.Timers.Timer CreateCustomTimer()
    {
        var t = new System.Timers.Timer(2000) { AutoReset = true };
        t.Elapsed += (_, _) =>
        {
            if (!BacklightOn())
                return;
            try
            {
                switch (_mode)
                {
                    case AuraMode.Heatmap:
                        CustomRgb.ApplyHeatmap();
                        break;
                    case AuraMode.Battery:
                        CustomRgb.ApplyBattery();
                        break;
                    case AuraMode.Ambient:
                        CustomRgb.ApplyAmbient();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Aura custom timer error: {ex.Message}");
            }
        };
        return t;
    }

    // Model detection
    //
    // _isACPI controls whether we use the sysfs kbd_rgb_mode path (TUF)
    // in addition to the HID AURA protocol. Upstream G-Helper always
    // sends BOTH HID and WMI/ACPI commands for TUF models simultaneously.
    // The WMI/ACPI path (sysfs kbd_rgb_mode on Linux) is what actually
    // controls the keyboard on TUF hardware. The HID commands also fire
    // (via AsusHid.Write) but may or may not take effect.
    //
    // Previously we tried to override this to false when an I2C-HID AURA
    // device was detected (FA608PP), but that disabled the sysfs path
    // which is the one that actually works.
    private static bool _isACPI = AppConfig.IsTUF() || AppConfig.IsVivoZenPro();

    private static bool _oobeDisabled;

    // Hardware-detected state (populated by DetectBacklightType())
    //
    // When the AURA capability probe succeeds, these describe the actual zones
    // and features the keyboard reports. When BacklightType stays Unknown the
    // device gets the basic AURA mode set - no model-list backstop.

    /// <summary>The probed backlight type byte (response[9] from the capability query).</summary>
    public static AuraBacklightType BacklightType { get; private set; } = AuraBacklightType.Unknown;

    /// <summary>True if a successful capability probe set <see cref="BacklightType"/>.
    /// When false the device falls through to the basic single-zone path.</summary>
    public static bool IsBacklightDetected => BacklightType != AuraBacklightType.Unknown;

    /// <summary>Probed: device has a logo zone (lid logo on Strix/Zephyrus).</summary>
    public static bool HasLogo { get; private set; }

    /// <summary>Probed: device has a front lightbar.</summary>
    public static bool HasLightbar { get; private set; }

    /// <summary>Pre-2021 Strix/Scar firmware (feat1==0 probe). Direct-mode</summary>
    public static bool IsOldStrix { get; private set; }

    /// <summary>Probed: device has rear-glow (rear panel light, e.g. Strix Scar / Z13 lid).</summary>
    public static bool HasRearglow { get; private set; }
    private static bool _hasRearStrip;

    /// <summary>White-only keyboard flag. Initialized from <see cref="Helpers.AppConfig.IsWhite"/>
    /// (model list); FORCED to <c>true</c> by the probe FEAT2_ONE_ZONE_RED_EFFECT bit.
    /// AuraMessage / GetModes / UI checks read it directly without a wrapper.</summary>
    public static bool isWhite = AppConfig.IsWhite();

    // Numpad keyboards (G713R) need an alternate per-key zone map - the extra
    // numpad column shifts zone boundaries left of the modifier cluster.
    // Static cache avoids re-querying AppConfig on every direct-RGB frame.
    private static readonly bool _isStrixNumpad = AppConfig.IsStrixNumpad();

    // Strix path selectors
    // No AppConfig fallback: when BacklightType==Unknown both selectors are
    // false, so the device takes the basic single-color direct-RGB path.
    // IsNoDirectRGB still excludes GA503 / G533Q / GU502 (confirmed broken).

    /// <summary>True for Strix per-key OR multi-zone keyboards (use direct-RGB packet path).</summary>
    private static bool _isStrix =>
        (BacklightType == AuraBacklightType.MultiZone || BacklightType == AuraBacklightType.PerKey)
        && !AppConfig.IsNoDirectRGB();

    /// <summary>True for 4-zone Strix keyboards (use the 4-zone direct-RGB packet path).</summary>
    private static bool _isStrix4Zone => BacklightType == AuraBacklightType.MultiZone;

    /// <summary>True if this device exposes per-zone direct RGB
    /// (Strix per-key or 4-zone). Used by CustomRgb.ApplyGradient
    /// to decide between per-zone painting and single-color fallback.</summary>
    public static bool IsStrixZoned => _isStrix || _isStrix4Zone;

    // Properties

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

    /// <summary>Mode for the rear glow zone (Z13). Validated against
    /// <see cref="GetRearModes"/> on set; falls back to <see cref="AuraMode.AuraStatic"/>
    /// for unsupported values.</summary>
    public static AuraMode RearMode
    {
        get => _rearMode;
        set => _rearMode = GetRearModes().ContainsKey(value) ? value : AuraMode.AuraStatic;
    }

    /// <summary>Whether the current mode supports a second color (Breathe + Gradient).
    /// Allowed on non-ACPI devices, or ACPI devices that use DynamicLighting only.</summary>
    public static bool HasSecondColor()
    {
        return (_mode == AuraMode.AuraBreathe || _mode == AuraMode.Gradient)
            && (!_isACPI || AppConfig.IsDynamicLightingOnly());
    }

    /// <summary>Whether the current mode treats black as firmware-random color
    /// (upstream #5608). Picker shows a Random button for these.</summary>
    public static bool HasRandomColor()
    {
        return _mode == AuraMode.Star || _mode == AuraMode.Highlight
            || _mode == AuraMode.Laser || _mode == AuraMode.Ripple;
    }

    /// <summary>Whether the current mode uses Color1 at all. Rainbow/ColorCycle don't,
    /// and the auto-color modes (Heatmap/GpuMode/Battery) compute their own color.</summary>
    public static bool UsesColor()
    {
        return _mode != AuraMode.AuraColorCycle
            && _mode != AuraMode.AuraRainbow
            && _mode != AuraMode.Heatmap
            && _mode != AuraMode.GpuMode
            && _mode != AuraMode.Battery
            && _mode != AuraMode.Ambient;
    }

    /// <summary>Whether the current mode honours the speed dropdown.
    /// <list type="bullet">
    /// <item><c>AuraStatic</c>: no animation, speed has no effect.</item>
    /// <item><c>Heatmap</c>/<c>GpuMode</c>/<c>Battery</c>: software-driven, polled at fixed intervals; speed has no effect.</item>
    /// <item><c>Gradient</c>/<c>ZoneTest</c>: static paint, speed has no effect.</item>
    /// <item>All other modes (Breathe / ColorCycle / Rainbow / Strobe / per-key effects):
    /// firmware uses the speed byte to set animation rate.</item>
    /// </list>
    /// Used to hide the speed combo in MainWindow / ExtraWindow when speed has no effect.
    /// </summary>
    public static bool UsesSpeed()
    {
        return _mode != AuraMode.AuraStatic
            && _mode != AuraMode.Heatmap
            && _mode != AuraMode.GpuMode
            && _mode != AuraMode.Battery
            && _mode != AuraMode.Ambient
            && _mode != AuraMode.Gradient
            && _mode != AuraMode.ZoneTest;
    }

    // Mode/Speed lists

    public static Dictionary<AuraMode, string> GetModes()
    {
        // White-only / single-color: tiny fixed set (probe FEAT2 ONE_ZONE_RED_EFFECT
        // or AppConfig.IsWhite model list).
        if (isWhite)
        {
            return new Dictionary<AuraMode, string>
            {
                { AuraMode.AuraStatic, Labels.Get("aura_static") },
                { AuraMode.AuraBreathe, Labels.Get("aura_breathe") },
                { AuraMode.AuraStrobe, Labels.Get("aura_strobe") },
            };
        }

        // Dynamic-Lighting-only models (S560, M540, UX760) - reduced firmware effects
        if (AppConfig.IsDynamicLightingOnly())
        {
            return new Dictionary<AuraMode, string>
            {
                { AuraMode.AuraStatic, Labels.Get("aura_static") },
                { AuraMode.AuraBreathe, Labels.Get("aura_color_cycle") },
                { AuraMode.AuraRainbow, Labels.Get("aura_rainbow") },
                { AuraMode.AuraStrobe, Labels.Get("aura_strobe") },
            };
        }

        // Detection-driven build. When BacklightType == Unknown (probe failed
        // or not run), perKey & multiZone are both false, so the device gets
        // the basic mode set: Static, Breathe, ColorCycle, Strobe,
        // Heatmap, GpuMode, Battery.
        bool perKey = BacklightType == AuraBacklightType.PerKey;
        bool multiZone = BacklightType == AuraBacklightType.MultiZone;
        bool isStrixKb = perKey || multiZone;
        bool isAlly = AppConfig.IsAlly();

        var modes = new Dictionary<AuraMode, string>
        {
            [AuraMode.AuraStatic] = Labels.Get("aura_static"),
            [AuraMode.AuraBreathe] = Labels.Get("aura_breathe"),
            [AuraMode.AuraColorCycle] = Labels.Get("aura_color_cycle"),
        };

        // Rainbow only works on Strix keyboards; TUF/ACPI and LampArray-era
        // basic firmware ignore or garble it (upstream e9b93159)
        if (isStrixKb && !_isACPI)
            modes[AuraMode.AuraRainbow] = Labels.Get("aura_rainbow");

        if (perKey)
        {
            // Per-key animation effects. Comet (0x0B) and Flash (0x0C) are
            // included here (NOT in the wider isStrixKb block) because the
            // firmware only implements them on full per-key hardware - on
            // 4-zone MultiZone keyboards (e.g. G614JVR) the firmware silently
            // ignores these mode bytes. asusctl's authoritative model database
            // (rog-aura/data/aura_support.ron) confirms: G614J/JJ/JZ list only
            // basic firmware modes; G614JIR/JU (per-key advanced_type) list the
            // full set including Comet/Flash. Diverges from upstream which
            // offers these on MultiZone too.
            modes[AuraMode.Star] = Labels.Get("aura_star");
            modes[AuraMode.Rain] = Labels.Get("aura_rain");
            modes[AuraMode.Highlight] = Labels.Get("aura_highlight");
            modes[AuraMode.Laser] = Labels.Get("aura_laser");
            modes[AuraMode.Ripple] = Labels.Get("aura_ripple");
            modes[AuraMode.Comet] = Labels.Get("aura_comet");
            modes[AuraMode.Flash] = Labels.Get("aura_flash");
        }

        modes[AuraMode.AuraStrobe] = Labels.Get("aura_strobe");

        // Ally is a special case: only Battery custom mode (no Heatmap/GpuMode)
        if (isAlly)
        {
            modes[AuraMode.Battery] = Labels.Get("aura_battery");
            return modes;
        }

        // Software-driven Linux extras (always available outside Ally)
        modes[AuraMode.Heatmap] = Labels.Get("aura_heatmap");
        modes[AuraMode.GpuMode] = Labels.Get("aura_gpu_mode");
        modes[AuraMode.Ambient] = Labels.Get("aura_ambient");
        modes[AuraMode.Battery] = Labels.Get("aura_battery");

        // Gradient + ZoneTest only on per-zone hardware (probe-confirmed)
        if (isStrixKb)
        {
            modes[AuraMode.Gradient] = Labels.Get("aura_gradient");
            modes[AuraMode.ZoneTest] = Labels.Get("aura_zone_test");
        }

        return modes;
    }

    public static Dictionary<AuraSpeed, string> GetSpeeds()
    {
        return new Dictionary<AuraSpeed, string>
        {
            { AuraSpeed.Slow, Labels.Get("speed_slow") },
            { AuraSpeed.Normal, Labels.Get("speed_normal") },
            { AuraSpeed.Fast, Labels.Get("speed_fast") },
        };
    }

    /// <summary>
    /// Modes available for the rear glow zone (Z13). Restricted to the 5 firmware
    /// modes the rear-light controller supports - none of the per-key effects
    /// (Star/Rain/etc.) or software-driven modes (Heatmap/Battery) apply.
    /// </summary>
    public static Dictionary<AuraMode, string> GetRearModes()
    {
        return new Dictionary<AuraMode, string>
        {
            { AuraMode.AuraStatic,     Labels.Get("aura_static") },
            { AuraMode.AuraBreathe,    Labels.Get("aura_breathe") },
            { AuraMode.AuraColorCycle, Labels.Get("aura_color_cycle") },
            { AuraMode.AuraRainbow,    Labels.Get("aura_rainbow") },
            { AuraMode.AuraStrobe,     Labels.Get("aura_strobe") },
        };
    }

    // Color helpers

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

    public static void SetRearColor(int argb)
    {
        RearR = (byte)((argb >> 16) & 0xFF);
        RearG = (byte)((argb >> 8) & 0xFF);
        RearB = (byte)(argb & 0xFF);
    }

    public static int GetRearColorArgb()
    {
        return (255 << 24) | (RearR << 16) | (RearG << 8) | RearB;
    }

    // Protocol messages

    /// <summary>
    /// Build the 17-byte AURA mode message.
    /// Format: [0x5D, 0xB3, zone, mode, R, G, B, speed, direction, random, R2, G2, B2]
    /// <para>White-only keyboards (<see cref="isWhite"/>) zero out the G/B channels
    /// so the firmware emits clean white instead of color-mixed. The mono flag is
    /// read from the static field at message-build time.</para>
    /// </summary>
    public static byte[] AuraMessage(AuraMode mode, byte r, byte g, byte b,
                                      byte r2, byte g2, byte b2,
                                      int speed)
    {
        bool mono = isWhite;
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
    /// Initialize the AURA device (handshake sequence) and probe hardware
    /// capabilities to populate <see cref="BacklightType"/> + <c>Has*</c>
    /// flags. TUF/ACPI models skip the probe (they don't speak the AURA
    /// HID feature-report protocol; sysfs kbd_rgb_mode is the real path).
    ///
    /// <para>Synchronous so callers (MainWindow startup, ExtraWindow form-open
    /// retry) see populated state immediately on return. Adds ~50-100ms blocking
    /// to the calling thread for the SetFeature/GetFeature ioctl pair on first
    /// call; subsequent calls early-exit in DetectBacklightType when state is
    /// already populated.</para>
    /// </summary>
    public static void Init()
    {
        if (!_oobeDisabled)
        {
            HidrawHelper.DisableBacklightOobe();
            _oobeDisabled = true;
        }

        // Modern AURA firmware prefers feature-report transport over output
        // writes for the handshake (matches Armoury Crate and asusctl). Capability
        // probe (0x05 0x20 0x31 0 0x20) runs from DetectBacklightType() below.
        AsusHid.SetFeatureAura([AsusHid.AURA_ID, 0xB9]);
        AsusHid.SetFeatureAura([AsusHid.AURA_ID, .. Encoding.ASCII.GetBytes("ASUS Tech.Inc.")]);

        // Run probe synchronously so callers see populated BacklightType + Has*
        // flags on return. ~50-100ms first call; near-instant when re-invoked
        // (DetectBacklightType early-exits via IsBacklightDetected).
        DetectBacklightType();

        // Dynamic Lighting init enables the rear window / logo RGB controller.
        // Upstream only fires this for Z13; we extend to Slash + IntelHX + TUF
        // (broader IsDynamicLighting set) because the I2C-HID + asus-armoury
        // path on those chassis needs the same handshake before lights respond.
        if (AppConfig.IsDynamicLighting())
            AsusHid.Write([AsusHid.AURA_ID, 0xC0, 0x03, 0x01], "Dynamic Lighting Init");

        // ProArt models need a separate INPUT_ID handshake to wake their
        // RGB controller.
        if (AppConfig.IsProArt())
        {
            AsusHid.WriteInput(new byte[] { AsusHid.INPUT_ID, 0x05, 0x20, 0x31, 0x00, 0x08 }, "ProArt Init");
            AsusHid.WriteInput(new byte[] { AsusHid.INPUT_ID, 0xBA, 0xC5, 0xC4 }, "ProArt Init");
            AsusHid.WriteInput(new byte[] { AsusHid.INPUT_ID, 0xD0, 0x8F, 0x01 }, "ProArt Init");
            AsusHid.WriteInput(new byte[] { AsusHid.INPUT_ID, 0xD0, 0x85, 0xFF }, "ProArt Init");
        }
    }

    /// <summary>
    /// Send the AURA capability query and parse the response into
    /// <see cref="BacklightType"/> / <c>Has*</c> / <see cref="isWhite"/> state.
    /// Ported from upstream G-Helper's AURA detection (PR #5299).
    ///
    /// <para>Skipped on TUF/ACPI - those models are driven via sysfs and don't
    /// expose the capability query.</para>
    /// </summary>
    private static void DetectBacklightType()
    {
        if (_isACPI)
            return;

        // Query: [AURA_ID, 0x05, 0x20, 0x31, 0x00, 0x20]
        byte[] query = { AsusHid.AURA_ID, 0x05, 0x20, 0x31, 0x00, 0x20 };

        // Already probed once - resend the query as a keep-alive so the firmware
        // doesn't sleep its capability state, but skip the parse.
        if (IsBacklightDetected)
        {
            try
            {
                AsusHid.AuraProbe(query);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"AURA probe keep-alive failed: {ex.Message}");
            }
            return;
        }

        try
        {
            var response = AsusHid.AuraProbe(query);

            if (AppConfig.IsZ13())
            {
                HasLogo = true;
                HasRearglow = true;
            }

            if (response is null || response.Length < 18)
                return;

            byte typeByte = response[9];
            byte year = response[10];
            byte layout = response[12];
            byte feat1 = response[13];
            byte feat2 = response[14];
            // [17] family is only valid when year >= 0x23; older firmware leaves it 0
            byte family = year >= 0x23 ? response[17] : (byte)0;

            // Feature bitmasks (firmware-defined)
            const byte FEAT1_LOGO = 0x01;
            const byte FEAT1_LIGHTBAR = 0x02;
            const byte FEAT1_VCUT = 0x10;
            const byte FEAT1_AERO = 0x20;
            const byte FEAT1_BUMP = 0x40;
            const byte FEAT1_REARGLOW = 0x80;
            const byte FEAT2_DEFAULT_COLOR = 0x04;
            const byte FEAT2_RGB_WHEEL = 0x08;
            const byte FEAT2_ONE_ZONE_RED_EFFECT = 0x10;
            const byte FEAT2_BIT_FORMAT_KEY_POS = 0x40;

            string familyName = family switch
            {
                0x01 => "Strix",
                0x02 => "Flow",
                0x04 => "Zephyrus",
                0x08 => "TUF",
                0x10 => "NR2301",
                0x20 => "Desktop",
                0x00 => "(pre-2023)",
                _ => $"unknown(0x{family:X2})"
            };

            Logger.WriteLine(
                $"Aura Probe: Type=0x{typeByte:X2} Year=0x{year:X2} Layout=0x{layout:X2} " +
                $"Feat1=0x{feat1:X2} Feat2=0x{feat2:X2} Family=0x{family:X2} ({familyName})");
            Logger.WriteLine(
                $"Aura Probe Feat1: Logo={(feat1 & FEAT1_LOGO) != 0} Lightbar={(feat1 & FEAT1_LIGHTBAR) != 0} " +
                $"VCut={(feat1 & FEAT1_VCUT) != 0} Aero={(feat1 & FEAT1_AERO) != 0} " +
                $"Bump={(feat1 & FEAT1_BUMP) != 0} Rearglow={(feat1 & FEAT1_REARGLOW) != 0}");
            Logger.WriteLine(
                $"Aura Probe Feat2: DefaultColor={(feat2 & FEAT2_DEFAULT_COLOR) != 0} RGBWheel={(feat2 & FEAT2_RGB_WHEEL) != 0} " +
                $"OneZoneRedEffect={(feat2 & FEAT2_ONE_ZONE_RED_EFFECT) != 0} PerKeyMap={(feat2 & FEAT2_BIT_FORMAT_KEY_POS) != 0}");

            // Map type byte → enum. Unknown values stay Unknown so the device
            // falls through to the basic AURA mode set on next GetModes() call.
            BacklightType = typeByte switch
            {
                (byte)AuraBacklightType.MultiZone => AuraBacklightType.MultiZone,
                (byte)AuraBacklightType.PerKey => AuraBacklightType.PerKey,
                (byte)AuraBacklightType.SingleZone => AuraBacklightType.SingleZone,
                _ => AuraBacklightType.Unknown
            };

            if (!IsBacklightDetected)
                return;

            // Persist for diagnostics + future fast-path read on resume.
            AppConfig.Set("backlight_type", typeByte);

            // Old Strix/Scar report feat1==0; assume logo+lightbar. (#5590)
            IsOldStrix = feat1 == 0 && AppConfig.IsStrix();
            if (IsOldStrix)
                feat1 = FEAT1_LOGO | FEAT1_LIGHTBAR;

            HasLogo = (feat1 & FEAT1_LOGO) != 0 || AppConfig.IsLidLogo();
            HasLightbar = (feat1 & FEAT1_LIGHTBAR) != 0;
            // VCUT bit = rearglow on old Scar. (#5590)
            HasRearglow = (feat1 & (FEAT1_REARGLOW | FEAT1_VCUT)) != 0 || AppConfig.IsZ13();
            _hasRearStrip = (feat1 & FEAT1_REARGLOW) != 0;

            // FEAT2 ONE_ZONE_RED_EFFECT means white-only keyboard (single-zone
            // red firmware effect mapped to white). Force-flip the mutable static
            // so GetModes / AuraMessage / UI all read the live value.
            // Guard on typeByte != 0 to avoid false positives from devices that
            // report no backlight type but still set the feature bit.
            if (typeByte != 0x00 && (feat2 & FEAT2_ONE_ZONE_RED_EFFECT) != 0)
                isWhite = true;

            Logger.WriteLine(
                $"Aura Probe DONE: BacklightType={BacklightType} HasLogo={HasLogo} HasLightbar={HasLightbar} " +
                $"HasRearglow={HasRearglow} isWhite={isWhite}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"AURA DetectBacklightType failed: {ex.Message}");
        }
    }

    /// <summary>
    /// AppConfig key for the current power state. AC and battery have separate
    /// brightness levels. If <c>keyboard_brightness_ac</c> isn't set yet (older
    /// configs), the AC path falls back to <c>keyboard_brightness</c>.
    /// </summary>
    public static string GetBrightnessConfigKey()
    {
        bool onAc = App.Power?.IsOnAcPower() ?? true;
        return onAc ? "keyboard_brightness_ac" : "keyboard_brightness";
    }

    /// <summary>
    /// Read the configured brightness for the current AC/battery state and apply it.
    /// Used on AC/DC transitions and at startup.
    /// </summary>
    public static void ApplyConfiguredBrightness(string log = "Configured")
    {
        bool onAc = App.Power?.IsOnAcPower() ?? true;
        int level;
        if (onAc)
        {
            // Migrate older configs that only have keyboard_brightness
            level = AppConfig.Get("keyboard_brightness_ac", -1);
            if (level < 0)
                level = AppConfig.Get("keyboard_brightness", -1);
        }
        else
        {
            level = AppConfig.Get("keyboard_brightness", -1);
        }
        if (level < 0) // never configured - leave hardware as-is
            return;
        ApplyBrightness(Math.Clamp(level, 0, 3), log);
    }

    /// <summary>
    /// Set keyboard backlight brightness via HID.
    /// Level: 0=off, 1=low, 2=medium, 3=high
    /// </summary>
    public static void ApplyBrightness(int brightness, string log = "Backlight")
    {
        if (brightness == 0)
            _backlight = false;

        // Ally needs the AURA_ID feature report so RGB turns off at 0%. (#5591)
        if (AppConfig.IsAlly())
            AsusHid.SetFeatureAura(new byte[] { AsusHid.AURA_ID, 0xBA, 0xC5, 0xC4, (byte)brightness });
        else
            AsusHid.WriteInput(new byte[] { AsusHid.INPUT_ID, 0xBA, 0xC5, 0xC4, (byte)brightness }, log);

        App.Wmi?.SetKeyboardBrightness(brightness);

        if (brightness > 0)
        {
            if (!_backlight)
                _initDirect = true;
            _backlight = true;
        }
    }

    // Fn keys and the desktop slider change brightness without ApplyBrightness.
    private static bool BacklightOn()
    {
        if (!_backlight && (App.Wmi?.GetKeyboardBrightness() ?? 0) > 0)
        {
            Logger.WriteLine("Aura: backlight turned on outside G-Helper");
            _initDirect = true;
            _backlight = true;
        }
        return _backlight;
    }

    /// <summary>
    /// Build the power zone control message.
    /// Controls which lighting zones stay on during boot/sleep/shutdown.
    /// </summary>
    public static byte[] AuraPowerMessage(AuraPower flags)
    {
        byte keyb = 0, bar = 0, lid = 0, rear = 0;

        if (flags.BootLogo)
            keyb |= 1 << 0;
        if (flags.BootKeyb)
            keyb |= 1 << 1;
        if (flags.AwakeLogo)
            keyb |= 1 << 2;
        if (flags.AwakeKeyb)
            keyb |= 1 << 3;
        if (flags.SleepLogo)
            keyb |= 1 << 4;
        if (flags.SleepKeyb)
            keyb |= 1 << 5;
        if (flags.ShutdownLogo)
            keyb |= 1 << 6;
        if (flags.ShutdownKeyb)
            keyb |= 1 << 7;

        if (flags.AwakeBar)
            bar |= 1 << 0;
        if (flags.BootBar)
            bar |= 1 << 1;
        if (flags.AwakeBar)
            bar |= 1 << 2;
        if (flags.SleepBar)
            bar |= 1 << 3;
        if (flags.ShutdownBar)
            bar |= 1 << 4;

        if (flags.BootLid)
            lid |= 1 << 0;
        if (flags.AwakeLid)
            lid |= 1 << 1;
        if (flags.SleepLid)
            lid |= 1 << 2;
        if (flags.ShutdownLid)
            lid |= 1 << 3;
        if (flags.BootLid)
            lid |= 1 << 4;
        if (flags.AwakeLid)
            lid |= 1 << 5;
        if (flags.SleepLid)
            lid |= 1 << 6;
        if (flags.ShutdownLid)
            lid |= 1 << 7;

        if (flags.BootRear)
            rear |= 1 << 0;
        if (flags.AwakeRear)
            rear |= 1 << 1;
        if (flags.SleepRear)
            rear |= 1 << 2;
        if (flags.ShutdownRear)
            rear |= 1 << 3;
        if (flags.BootRear)
            rear |= 1 << 4;
        if (flags.AwakeRear)
            rear |= 1 << 5;
        if (flags.SleepRear)
            rear |= 1 << 6;
        if (flags.ShutdownRear)
            rear |= 1 << 7;

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

        // Z13: rear window/logo is controlled by a mix of Logo + Bar + Lid flags.
        // Copy Logo state into Bar and Lid so the rear panel light responds to
        // the "Logo" checkboxes in the UI (upstream pattern).
        if (AppConfig.IsZ13())
        {
            flags.AwakeBar = flags.AwakeLogo;
            flags.BootBar = flags.BootLogo;
            flags.SleepBar = flags.SleepLogo;
            flags.ShutdownBar = flags.ShutdownLogo;
            flags.AwakeLid = flags.AwakeLogo;
            flags.BootLid = flags.BootLogo;
            flags.SleepLid = flags.SleepLogo;
            flags.ShutdownLid = flags.ShutdownLogo;
        }

        // TUF: use sysfs kbd_rgb_state instead of HID power message
        if (_isACPI)
        {
            var wmi = App.Wmi as GHelper.Linux.Platform.Linux.LinuxAsusWmi;
            if (wmi != null && wmi.HasKeyboardRgbMode())
            {
                wmi.SetKeyboardRgbState(flags.BootKeyb, flags.AwakeKeyb, flags.SleepKeyb, flags.ShutdownKeyb);
                Logger.WriteLine($"TUF kbd_rgb_state: boot={flags.BootKeyb} awake={flags.AwakeKeyb} sleep={flags.SleepKeyb} shutdown={flags.ShutdownKeyb}");
                return;
            }
        }

        // Ally / Ally X: a separate report ID (0xD1 not 0xBD) and a different
        // bit layout. Single byte packs all 4 power states for the gamepad
        // backlight (no logo / lightbar / lid concept on a handheld). Matches
        // asusctl rog-aura/src/keyboard/power.rs:113-118 (PowerZones::Ally).
        if (AppConfig.IsAlly())
        {
            byte bits = 0;
            if (flags.BootKeyb)
                bits |= 1 << 0;
            if (flags.AwakeKeyb)
                bits |= 1 << 1;
            if (flags.SleepKeyb)
                bits |= 1 << 2;
            if (flags.ShutdownKeyb)
                bits |= 1 << 3;
            AsusHid.Write(new byte[] { AsusHid.AURA_ID, 0xD1, 0x09, 0x01, bits }, "AuraPower(Ally)");
            return;
        }

        AsusHid.Write(AuraPowerMessage(flags));
    }

    /// <summary>
    /// Apply the rear glow zone (Z13 only) - sends an AuraMessage routed to
    /// the rear-light device (PID 0x18C6). Reads <c>rear_mode</c> + <c>rear_color</c>
    /// from AppConfig; speed shares the main keyboard's <see cref="Speed"/> value.
    /// Early-returns on non-<see cref="AppConfig.HasRearLight"/> models so it's
    /// safe to call unconditionally from <see cref="ApplyAura"/>.
    /// </summary>
    public static void ApplyRearLight()
    {
        if (!AppConfig.HasRearLight())
            return;

        RearMode = (AuraMode)AppConfig.Get("rear_mode");
        SetRearColor(AppConfig.Get("rear_color", unchecked((int)0xFFFFFFFF)));

        int speedByte = Speed switch
        {
            AuraSpeed.Normal => 0xEB,
            AuraSpeed.Fast => 0xF5,
            AuraSpeed.Slow => 0xE1,
            _ => 0xEB
        };

        var msg = AuraMessage(RearMode, RearR, RearG, RearB, RearR, RearG, RearB, speedByte);
        AsusHid.Write(new List<byte[]> { msg, MESSAGE_SET, MESSAGE_APPLY }, "Rear", AsusHid.REAR_LIGHT_PIDS);
    }

    // 4-zone direct RGB map

    /// <summary>
    /// Zone mapping for 4-zone Strix keyboards (default wiring).
    /// 6 keyboard LEDs (Z1-Z4 + 2 unused) + 6 lightbar LEDs.
    /// Lightbar is wired R→L (matches OpenRGB Value 169..174).
    /// </summary>
    private static readonly byte[] Packet4Zone = new byte[]
    {
        // Z1  Z2  Z3  Z4  NA  NA  (keyboard zones)
           0,  1,  2,  3,  0,  0,
        // R1  R2  R3  L3  L2  L1  (lightbar, R->L wire ascending)
           7,  7,  6,  5,  4,  4,
    };

    /// <summary>
    /// Zone mapping for the G513 family - lightbar is physically wired L→R
    /// instead of R→L. Selected by <see cref="AppConfig.IsStrix4ZoneFlipped"/>
    /// in <see cref="ApplyDirectZones"/> and <see cref="ApplyDirectLightbar"/>.
    /// </summary>
    private static readonly byte[] Packet4ZoneFlipped = new byte[]
    {
        // Z1  Z2  Z3  Z4  NA  NA  (keyboard zones)
           0,  1,  2,  3,  0,  0,
        // L1  L2  L3  R3  R2  R1  (lightbar, L->R wire ascending - G513 quirk)
           4,  4,  5,  6,  7,  7,
    };

    // Per-key maps (for per-key RGB Strix)

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
    /// Per-key zone map for Strix numpad (G713R) keyboards. The numpad column
    /// shifts the zone boundaries left of the modifier cluster: zones 0/1/2/3
    /// span <i>five</i> columns each instead of four. Selected by
    /// <see cref="_isStrixNumpad"/> in <see cref="ApplyDirectZones"/>; everywhere
    /// else <see cref="PacketZone"/> applies.
    /// </summary>
    private static readonly byte[] PacketZoneNumpad = new byte[]
    {
        /*          VDN  VUP  MICM HPFN ARMC */
                     0,   0,   0,   1,   1,
        /* ESC       F1   F2   F3   F4   F5   F6   F7   F8   F9  F10  F11  F12            DEL15 DEL17 PAUS PRT  HOM */
             0,       0,   0,   0,   1,   1,   1,   1,   1,   2,   2,   2,   2,              3,   3,   3,   3,   3,
        /* BKTK  1    2    3    4    5    6    7    8    9    0    -    =  BSPC BSPC BSPC PLY15 NMLK NMDV NMTM NMMI */
             0,   0,   0,   0,   0,   1,   1,   1,   1,   1,   2,   2,   2,   2,   2,   2,   3,   3,   3,   3,   3,
        /* TAB   Q    W    E    R    T    Y    U    I    O    P    [    ]    \            STP15 NM7  NM8  NM9  NMPL */
             0,   0,   0,   0,   0,   1,   1,   1,   1,   1,   2,   2,   2,   2,              3,   3,   3,   3,   3,
        /* CPLK  A    S    D    F    G    H    J    K    L    ;    "    #  ENTR ENTR ENTR PRV15 NM4  NM5  NM6  NMPL */
             0,   0,   0,   0,   0,   1,   1,   1,   1,   1,   2,   2,   2,   2,   2,   2,   3,   3,   3,   3,   3,
        /* LSFT ISO\ Z    X    C    V    B    N    M    ,    .    /  RSFT RSFT RSFT ARWU NXT15 NM1  NM2  NM3  NMER */
             0,   0,   0,   0,   0,   1,   1,   1,   1,   1,   2,   2,   2,   2,   2,   2,   3,   3,   3,   3,   3,
        /* LCTL LFNC LWIN LALT           SPC            RALT RFNC RCTL      ARWL ARWD ARWR PRT15      NM0  NMPD NMER */
             0,   0,   0,   0,            1,              1,   2,   2,        2,   2,   2,   3,         3,   3,   3,
        /* LB1  LB1  LB3                                                    ARW? ARW? ARW? ARW?       LB4  LB5  LB6  */
             5,   5,   4,                                                     2,   2,   2,   3,         6,   7,   7,
        /* KSTN LOGO LIDL LIDR */
             3,   0,   0,   3,
    };

    /// <summary>
    /// Cycle to the next (or previous) aura mode and apply it.
    /// direction: +1 = forward, -1 = backward.
    /// Returns the name of the new mode for notification display.
    /// </summary>
    public static string CycleAuraMode(int direction = 1)
    {
        var modes = GetModes();
        var modeKeys = new List<AuraMode>(modes.Keys);

        if (modeKeys.Count == 0)
            return Labels.Get("aura_no_modes");

        // Current mode
        var current = (AuraMode)AppConfig.Get("aura_mode");
        int currentIdx = modeKeys.IndexOf(current);

        // If current mode not found in list, start from 0
        if (currentIdx < 0)
            currentIdx = 0;

        // Advance
        int nextIdx = (currentIdx + direction + modeKeys.Count) % modeKeys.Count;
        var nextMode = modeKeys[nextIdx];

        // Save and apply
        AppConfig.Set("aura_mode", (int)nextMode);
        ApplyAura();

        string modeName = modes[nextMode];
        Logger.WriteLine($"CycleAuraMode: {modes.GetValueOrDefault(current, "?")} → {modeName}");
        return modeName;
    }

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

        // LampArray models: hand array to host or firmware depending on mode.
        // While the async probe runs, skip; Probe() re-calls ApplyAura after.
        AsusLampArray.SetMode(Mode);
        if (AsusLampArray.Probing)
            return;

        // Custom RGB modes - software-driven, dispatched to CustomRgb.
        // Stop the timer first so a previous mode's tick can't race a new selection.
        _customTimer.Stop();
        if (Mode != AuraMode.Ambient)
            Platform.Linux.ScreenCapture.Stop();
        switch (Mode)
        {
            case AuraMode.Ambient:
                CustomRgb.ApplyAmbient(true);
                _customTimer.Interval = AppConfig.Get("aura_refresh", AppConfig.IsStrix() ? 100 : 300);
                _customTimer.Start();
                return;
            case AuraMode.Heatmap:
                CustomRgb.ApplyHeatmap(true);
                _customTimer.Interval = 2000;
                _customTimer.Start();
                return;
            case AuraMode.Battery:
                CustomRgb.ApplyBattery();
                _customTimer.Interval = 30000;
                _customTimer.Start();
                return;
            case AuraMode.GpuMode:
                CustomRgb.ApplyGpuColor();
                return;  // event-driven, no timer
            case AuraMode.Gradient:
                CustomRgb.ApplyGradient();
                return;  // static, no timer
            case AuraMode.ZoneTest:
                CustomRgb.ApplyZoneTest();
                return;  // static diagnostic, no timer
        }

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
                              speedByte);

        // Restrict to keyboard / lightbar PIDs so the rear-light device (Z13)
        // doesn't receive keyboard-protocol packets it can't interpret.
        AsusHid.Write(new List<byte[]> { msg, MESSAGE_SET, MESSAGE_APPLY }, "Aura", AsusHid.MAIN_AURA_PIDS);

        // Z13 rear glow zone - independent device (PID 0x18C6), own mode/color.
        // Early-returns on non-Z13 hardware (HasRearLight = IsZ13).
        ApplyRearLight();

        // TUF/VivoZenPro: use sysfs kbd_rgb_mode (primary) + multi_intensity (fallback)
        if (_isACPI)
        {
            var wmi = App.Wmi as GHelper.Linux.Platform.Linux.LinuxAsusWmi;
            if (wmi != null && wmi.HasKeyboardRgbMode())
            {
                // Map AuraMode to TUF kbd_rgb_mode byte value
                int tufMode = Mode switch
                {
                    AuraMode.AuraStatic => 0,
                    AuraMode.AuraBreathe => 1,
                    AuraMode.AuraColorCycle => 2,
                    AuraMode.AuraRainbow => 3,
                    AuraMode.AuraStrobe => 10,
                    _ => 0  // Default to static for unsupported modes
                };
                // Map speed enum to TUF speed byte (0=slow, 1=normal, 2=fast)
                int tufSpeed = Speed switch
                {
                    AuraSpeed.Slow => 0,
                    AuraSpeed.Normal => 1,
                    AuraSpeed.Fast => 2,
                    _ => 1
                };
                wmi.SetKeyboardRgbMode(tufMode, ColorR, ColorG, ColorB, tufSpeed);
                Logger.WriteLine($"TUF kbd_rgb_mode: mode={tufMode} color=#{ColorR:X2}{ColorG:X2}{ColorB:X2} speed={tufSpeed}");
            }
            else
            {
                // Fallback to multi_intensity (older kernels or non-TUF ACPI models)
                App.Wmi?.SetKeyboardRgb(ColorR, ColorG, ColorB);
            }
        }
    }

    /// <summary>
    /// Apply a single color to the entire keyboard using direct RGB mode.
    /// Used by Heatmap, Battery, and Ambient modes.
    /// </summary>
    public static void ApplyDirect(byte r, byte g, byte b, bool init = false)
    {
        if (!BacklightOn())
            return;

        // LampArray models ignore legacy 0xBC direct writes
        if (AsusLampArray.Available)
        {
            AsusLampArray.SetColor(r, g, b);
            return;
        }

        if (_isACPI)
        {
            var wmi = App.Wmi as GHelper.Linux.Platform.Linux.LinuxAsusWmi;
            if (wmi != null && wmi.HasKeyboardRgbMode())
            {
                // Use kbd_rgb_mode with Static mode (0) for direct color
                wmi.SetKeyboardRgbMode(0, r, g, b, 0);
            }
            else
            {
                App.Wmi?.SetKeyboardRgb(r, g, b);
            }
            return;
        }

        if (AppConfig.IsNoDirectRGB())
        {
            AsusHid.SetFeatureAura(AuraMessage(AuraMode.AuraStatic, r, g, b, r, g, b, 0xEB));
            AsusHid.SetFeatureAura(MESSAGE_SET);
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
            // Re-handshake with SetFeature + a small delay before the first
            // direct packet keeps the firmware from dropping the next frame
            // on cold-start. Trailing byte enables direct-RGB mode; ancient
            // Strix firmware wants 0
            AsusHid.SetFeatureAura(new byte[] { AsusHid.AURA_ID, 0xBC, (byte)(IsOldStrix ? 0 : 1) });
            Thread.Sleep(50);
        }

        byte[] buffer = new byte[12];
        buffer[0] = AsusHid.AURA_ID;
        buffer[1] = 0xBC;
        buffer[2] = 1;
        buffer[3] = 1;
        buffer[9] = r;
        buffer[10] = g;
        buffer[11] = b;

        AsusHid.SetFeatureAura(buffer);
    }

    /// <summary>
    /// Apply per-zone colors using direct mode (Strix per-key or 4-zone).
    /// colors: flat array of R,G,B triplets for each zone (up to AURA_ZONES).
    /// </summary>
    public static void ApplyDirectZones(byte[] colors, bool init = false)
    {
        if (!BacklightOn())
            return;

        // LampArray models ignore legacy 0xBC direct writes
        if (AsusLampArray.Available)
        {
            AsusLampArray.SetColors(colors);
            return;
        }

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
            // SetFeature handshake instead of output write; ancient Strix
            // firmware wants trailing 0
            AsusHid.SetFeatureAura(new byte[] { AsusHid.AURA_ID, 0xBC, (byte)(IsOldStrix ? 0 : 1) });
            Thread.Sleep(50);
        }

        Array.Clear(keyBuf, 0, keyBuf.Length);

        if (!_isStrix4Zone) // per-key
        {
            // G713R (numpad Strix) uses a wider zone map; everything else uses
            // the 4-column PacketZone.
            var zoneMap = _isStrixNumpad ? PacketZoneNumpad : PacketZone;
            for (int ledIndex = 0; ledIndex < PacketMap.Length; ledIndex++)
            {
                ushort offset = (ushort)(3 * PacketMap[ledIndex]);
                byte zone = zoneMap[ledIndex];
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
                if (ledsRemaining < ledsPerPacket)
                    buffer[7] = ledsRemaining;

                buffer[6] = (byte)i;
                Buffer.BlockCopy(keyBuf, 3 * i, buffer, 9, 3 * buffer[7]);
                AsusHid.SetFeatureAura(buffer);
                Thread.Sleep(1);
            }
        }

        buffer[4] = 0x04;
        buffer[5] = 0x00;
        buffer[6] = 0x00;
        buffer[7] = 0x00;

        if (_isStrix4Zone)
        {
            // 4-zone mode - choose the lightbar map by chassis quirk.
            // G513 wires the lightbar L→R (Packet4ZoneFlipped); everything else R→L.
            var map = AppConfig.IsStrix4ZoneFlipped() ? Packet4ZoneFlipped : Packet4Zone;
            int ledCount4Z = map.Length;
            for (int ledIndex = 0; ledIndex < ledCount4Z; ledIndex++)
            {
                byte zone = map[ledIndex];
                int colorOff = zone * 3;
                if (colorOff + 2 < colors.Length)
                {
                    keyBuf[ledIndex * 3] = colors[colorOff];
                    keyBuf[ledIndex * 3 + 1] = colors[colorOff + 1];
                    keyBuf[ledIndex * 3 + 2] = colors[colorOff + 2];
                }
            }
            Buffer.BlockCopy(keyBuf, 0, buffer, 9, 3 * ledCount4Z);
            AsusHid.SetFeatureAura(buffer);
            Thread.Sleep(1);
            return;
        }

        // Send remaining lightbar LEDs
        Buffer.BlockCopy(keyBuf, 3 * keySet, buffer, 9, 3 * (ledCount - keySet));
        AsusHid.SetFeatureAura(buffer);

        if (!_hasRearStrip || colors.Length < 12)
            return;

        // Rear strip: 39 LEDs, gradient across zones 0..3
        const byte rearCount = 39;
        buffer[4] = 0x05;

        for (int i = 0; i < rearCount; i += ledsPerPacket)
        {
            byte leds = (byte)Math.Min(ledsPerPacket, rearCount - i);

            buffer[6] = (byte)(i + 1);
            buffer[7] = leds;

            for (int n = 0; n < leds; n++)
            {
                float f = (i + n) / (float)(rearCount - 1) * 3;
                int z = Math.Min(2, (int)f);
                var c = ColorUtils.GetWeightedAverage(
                    Avalonia.Media.Color.FromRgb(colors[z * 3], colors[z * 3 + 1], colors[z * 3 + 2]),
                    Avalonia.Media.Color.FromRgb(colors[z * 3 + 3], colors[z * 3 + 4], colors[z * 3 + 5]),
                    f - z);

                buffer[9 + n * 3] = c.R;
                buffer[10 + n * 3] = c.G;
                buffer[11 + n * 3] = c.B;
            }

            Array.Clear(buffer, 9 + leds * 3, buffer.Length - 9 - leds * 3);
            AsusHid.SetFeatureAura(buffer);
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Send only the lightbar zones via direct RGB. Used by the ZoneTest mode
    /// to verify chassis lightbar wiring independently of the keyboard payload.
    /// <para>The same flipped-map quirk applies (G513). Argument <paramref name="colors"/>
    /// must contain at least 8 zones × 3 bytes (RGB).</para>
    /// </summary>
    public static void ApplyDirectLightbar(byte[] colors)
    {
        if (!BacklightOn())
            return;

        // LampArray path already paints the bar in SetColors
        if (AsusLampArray.Available)
            return;

        var map = AppConfig.IsStrix4ZoneFlipped() ? Packet4ZoneFlipped : Packet4Zone;
        byte[] buffer = new byte[64];
        buffer[0] = AsusHid.AURA_ID;
        buffer[1] = 0xBC;
        buffer[2] = 0;
        buffer[3] = 1;
        buffer[4] = 0x04;

        for (int i = 0; i < map.Length; i++)
        {
            byte zone = map[i];
            int colorOff = zone * 3;
            int o = 9 + i * 3;
            if (colorOff + 2 < colors.Length)
            {
                buffer[o] = colors[colorOff];
                buffer[o + 1] = colors[colorOff + 1];
                buffer[o + 2] = colors[colorOff + 2];
            }
        }

        AsusHid.SetFeatureAura(buffer);
    }

    /// <summary>
    /// Check if any AURA control path is available on this system.
    /// Checks USB-HID, I2C-HID hidraw, and TUF kbd_rgb_mode sysfs.
    /// The sysfs path is needed for TUF models where hid_asus isn't loaded
    /// (e.g., kernel 6.19+ with asus_armoury, or I2C keyboards that don't
    /// respond to HID AURA protocol like the FA608PP).
    /// </summary>
    public static bool IsAvailable()
    {
        // ASUS AURA is only relevant on ASUS hardware. Lenovo devices
        // use the LenovoRgb backend instead.
        if (Helpers.AppConfig.IsLenovoDevice())
            return false;

        if (AsusHid.IsAvailable())
            return true;

        // TUF/VivoZenPro: kbd_rgb_mode sysfs works independently of HID
        if (_isACPI)
        {
            var wmi = App.Wmi as Platform.Linux.LinuxAsusWmi;
            if (wmi != null && wmi.HasKeyboardRgbMode())
                return true;
        }
        return false;
    }
}

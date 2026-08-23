using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GHelper.Linux.Helpers;

/// <summary>
/// AOT-compatible JSON serialization context for config.
/// We use Dictionary&lt;string, JsonElement&gt; instead of Dictionary&lt;string, object&gt;
/// because AOT source generators cannot resolve polymorphic 'object' values at compile time.
/// JsonElement is a self-describing value type that serializes/deserializes without reflection.
/// Lenient read options (trailing commas, comments) so a hand-edited config
/// doesn't lose all settings over a stray comma.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSourceGenerationOptions(WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
internal partial class ConfigJsonContext : JsonSerializerContext { }

/// <summary>
/// Linux port of G-Helper's AppConfig.cs.
/// Stores configuration in ~/.config/ghelper/config.json (XDG-compliant).
/// 
/// Key differences from Windows version:
///   - Uses XDG config dir instead of %APPDATA%
///   - Model detection via DMI sysfs instead of WMI
///   - No WMI or Registry access
///   - Same JSON format for config portability
///   - Uses JsonElement storage for Native AOT compatibility
/// </summary>
public static class AppConfig
{
    // ConfigDir/ConfigFile/BackupFile are intentionally NOT readonly so the
    // C# test harness can redirect them at runtime via ResetForTest(...).
    // In production they are written once at static-ctor time and never
    // mutated again.
    private static string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "ghelper");

    private static string ConfigFile = Path.Combine(ConfigDir, "config.json");
    private static string BackupFile = ConfigFile + ".bak";

    /// <summary>
    /// Reset all in-memory state and redirect on-disk storage to a fresh
    /// directory. Used by the C# scenario test harness so each test starts
    /// with a clean config without forking a new process. No-op outside
    /// tests - production code never calls this.
    /// </summary>
    internal static void ResetForTest(string newConfigDir)
    {
        lock (_lock)
        {
            _writeTimer?.Stop();
            _config.Clear();
            _lastWrite = 0;
            _model = null;
            _modelShort = null;
            _bios = null;

            ConfigDir = newConfigDir;
            ConfigFile = Path.Combine(newConfigDir, "config.json");
            BackupFile = ConfigFile + ".bak";
            Directory.CreateDirectory(ConfigDir);

            if (File.Exists(ConfigFile))
            {
                try
                {
                    string text = File.ReadAllText(ConfigFile);
                    _config = JsonSerializer.Deserialize(text, ConfigJsonContext.Default.DictionaryStringJsonElement)
                        ?? new Dictionary<string, JsonElement>();
                }
                catch
                {
                    _config = new Dictionary<string, JsonElement>();
                }
            }
        }
    }

    private static Dictionary<string, JsonElement> _config = new();
    private static readonly object _lock = new();
    // Guards the config file itself, not _config. Never taken with _lock held.
    private static readonly object _writeLock = new();
    private static System.Timers.Timer? _writeTimer;
    private static long _lastWrite;

    private static string? _model;
    private static string? _modelShort;
    private static string? _bios;

    /// <summary>
    /// Get the app version from assembly metadata (set by .csproj &lt;Version&gt; or CI).
    /// Returns "1.0.0" as fallback if metadata is unavailable.
    /// </summary>
    public static string AppVersion
    {
        get
        {
            var attr = typeof(AppConfig).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
            // Strip commit hash if present (e.g., "1.0.7+abc123" → "1.0.7")
            var version = attr?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
            return version;
        }
    }

    static AppConfig()
    {
        Directory.CreateDirectory(ConfigDir);

        if (File.Exists(ConfigFile))
        {
            try
            {
                string text = File.ReadAllText(ConfigFile);
                _config = JsonSerializer.Deserialize(text, ConfigJsonContext.Default.DictionaryStringJsonElement)
                    ?? new Dictionary<string, JsonElement>();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Broken config: {ex.Message}");
                if (!TryRecoverConfig(ConfigFile))
                    TryLoadBackup();
            }
        }
        else
        {
            Init();
        }

        // Debounced write timer (2 second delay like original)
        _writeTimer = new System.Timers.Timer(2000);
        _writeTimer.Elapsed += (_, _) => FlushConfig();
        _writeTimer.AutoReset = false;
    }

    // Core Get/Set

    public static int Get(string name, int empty = -1)
    {
        lock (_lock)
        {
            if (_config.TryGetValue(name, out var je))
            {
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out int intVal))
                    return intVal;
                if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out intVal))
                    return intVal;
                if (je.ValueKind == JsonValueKind.True)
                    return 1;
                if (je.ValueKind == JsonValueKind.False)
                    return 0;
            }
            return empty;
        }
    }

    public static bool Is(string name)
    {
        return Get(name) == 1;
    }

    public static bool IsNotFalse(string name)
    {
        return Get(name) != 0;
    }

    public static string? GetString(string name, string? empty = null)
    {
        lock (_lock)
        {
            if (_config.TryGetValue(name, out var je))
            {
                if (je.ValueKind == JsonValueKind.String)
                    return je.GetString();
                // For numbers/bools, return their string representation
                if (je.ValueKind != JsonValueKind.Null && je.ValueKind != JsonValueKind.Undefined)
                    return je.ToString();
            }
            return empty;
        }
    }

    public static bool Exists(string name)
    {
        lock (_lock)
        {
            return _config.ContainsKey(name);
        }
    }

    public static void Set(string name, int value)
    {
        lock (_lock)
        {
            // Create a JsonElement from an int by parsing its string representation
            _config[name] = JsonDocument.Parse(value.ToString()).RootElement.Clone();
        }
        ScheduleWrite();
    }

    public static void Set(string name, string value)
    {
        lock (_lock)
        {
            // Create a JsonElement from a string by JSON-encoding it
            _config[name] = JsonDocument.Parse($"\"{EscapeJsonString(value)}\"").RootElement.Clone();
        }
        ScheduleWrite();
    }

    public static void Remove(string name)
    {
        lock (_lock)
        {
            _config.Remove(name);
        }
        ScheduleWrite();
    }

    /// <summary>Store a dictionary as a JSON sub-object. AOT-safe (no reflection).</summary>
    public static void SetObject(string name, Dictionary<string, object> value)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var kv in value)
            {
                w.WritePropertyName(kv.Key);
                switch (kv.Value)
                {
                    case int i:
                        w.WriteNumberValue(i);
                        break;
                    case bool b:
                        w.WriteBooleanValue(b);
                        break;
                    case string s:
                        w.WriteStringValue(s);
                        break;
                    case List<int> li:
                        w.WriteStartArray();
                        foreach (int v in li)
                            w.WriteNumberValue(v);
                        w.WriteEndArray();
                        break;
                    case List<string> ls:
                        w.WriteStartArray();
                        foreach (string v in ls)
                            w.WriteStringValue(v);
                        w.WriteEndArray();
                        break;
                    default:
                        w.WriteNullValue();
                        break;
                }
            }
            w.WriteEndObject();
        }
        string json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        lock (_lock)
        {
            _config[name] = JsonDocument.Parse(json).RootElement.Clone();
        }
        ScheduleWrite();
    }

    /// <summary>Read a JSON sub-object. Returns null if the key is missing or not an object.</summary>
    public static Dictionary<string, object>? GetObject(string name)
    {
        lock (_lock)
        {
            if (!_config.TryGetValue(name, out var je) || je.ValueKind != JsonValueKind.Object)
                return null;
            var dict = new Dictionary<string, object>();
            foreach (var prop in je.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
    }

    // Mode-aware Get/Set (per performance mode)

    public static int GetMode(string name, int empty = -1)
    {
        int mode = GetCurrentMode();
        return Get($"{name}_{mode}", empty);
    }

    public static string? GetModeString(string name)
    {
        int mode = GetCurrentMode();
        return GetString($"{name}_{mode}");
    }

    public static bool IsMode(string name)
    {
        return GetMode(name) == 1;
    }

    public static void SetMode(string name, int value)
    {
        int mode = GetCurrentMode();
        Set($"{name}_{mode}", value);
    }

    public static void SetMode(string name, string value)
    {
        int mode = GetCurrentMode();
        Set($"{name}_{mode}", value);
    }

    public static void RemoveMode(string name)
    {
        int mode = GetCurrentMode();
        Remove($"{name}_{mode}");
    }

    // Fan curve config

    public static string GetFanParamName(int fanIndex, string paramName = "fan_profile")
    {
        int mode = GetCurrentMode();
        string fanName = fanIndex switch
        {
            1 => "gpu",
            2 => "mid",
            3 => "xgm",
            _ => "cpu"
        };
        return $"{paramName}_{fanName}_{mode}";
    }

    public static byte[] GetFanConfig(int fanIndex)
    {
        string? curveString = GetString(GetFanParamName(fanIndex));
        if (curveString != null)
            return StringToBytes(curveString);
        return Array.Empty<byte>();
    }

    public static void SetFanConfig(int fanIndex, byte[] curve)
    {
        string bitCurve = BitConverter.ToString(curve);
        Set(GetFanParamName(fanIndex), bitCurve);
    }

    public static byte[] StringToBytes(string str)
    {
        string[] arr = str.Split('-');
        byte[] array = new byte[arr.Length];
        for (int i = 0; i < arr.Length; i++)
            array[i] = Convert.ToByte(arr[i], 16);
        return array;
    }

    /// <summary>Default fan curves per mode (from original G-Helper).</summary>
    public static byte[] GetDefaultCurve(int fanIndex)
    {
        int mode = GetCurrentMode();

        return mode switch
        {
            // Turbo (1)
            1 => fanIndex switch
            {
                1 => StringToBytes("1E-3F-44-48-4C-50-54-62-16-1F-26-2D-39-47-55-5F"), // GPU
                _ => StringToBytes("1E-3F-44-48-4C-50-54-62-11-1A-22-29-34-43-51-5A"), // CPU
            },
            // Silent (2)
            2 => fanIndex switch
            {
                1 => StringToBytes("1E-31-3B-42-47-50-5A-64-00-00-04-11-1B-23-28-2D"),
                _ => StringToBytes("1E-31-3B-42-47-50-5A-64-00-00-03-0C-14-1C-22-29"),
            },
            // Balanced (0) / default
            _ => fanIndex switch
            {
                1 => StringToBytes("3A-3D-40-44-48-4D-51-62-0C-16-1D-1F-26-2D-34-4A"),
                3 => StringToBytes("32-3C-46-50-5A-64-6E-78-1E-28-32-3C-46-48-48-48"), // XGM dock GPU
                _ => StringToBytes("3A-3D-40-44-48-4D-51-62-08-11-16-1A-22-29-30-45"),
            },
        };
    }

    // Vendor detection (Linux: DMI sysfs)

    private static string? _vendor;

    /// <summary>DMI system vendor string (e.g. "ASUSTeK COMPUTER INC.", "LENOVO").</summary>
    public static string GetDmiVendor()
    {
        if (_vendor != null)
            return _vendor;

        _vendor = Platform.Linux.SysfsHelper.ReadAttribute(
            Path.Combine(Platform.Linux.SysfsHelper.DmiId, "sys_vendor")) ?? "";
        return _vendor;
    }

    /// <summary>Hardware vendor class this build is running on. Generic
    /// covers everything that is neither ASUS nor Lenovo (Dell, HP,
    /// Framework, desktops): universal features only, no vendor-specific
    /// provisioning or probes.</summary>
    public enum DeviceVendor { Asus, Lenovo, Generic }

    private static DeviceVendor? _vendorKind;

    /// <summary>Detected vendor class. Overridable with the "vendor" config
    /// key set to "asus", "lenovo" or "generic" (cached; restart to apply).</summary>
    public static DeviceVendor Vendor => _vendorKind ??= DetectVendor();

    private static DeviceVendor DetectVendor()
    {
        string? force = GetString("vendor");
        if (force != null)
        {
            if (force.Equals("lenovo", StringComparison.OrdinalIgnoreCase))
                return DeviceVendor.Lenovo;
            if (force.Equals("asus", StringComparison.OrdinalIgnoreCase))
                return DeviceVendor.Asus;
            if (force.Equals("generic", StringComparison.OrdinalIgnoreCase))
                return DeviceVendor.Generic;
        }

        string vendor = GetDmiVendor();
        if (vendor.Contains("LENOVO", StringComparison.OrdinalIgnoreCase)
            || vendor.Contains("MOTOROLA", StringComparison.OrdinalIgnoreCase))
            return DeviceVendor.Lenovo;
        if (vendor.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            return DeviceVendor.Asus;
        return DeviceVendor.Generic;
    }

    /// <summary>True on Lenovo hardware (DMI sys_vendor contains LENOVO, or
    /// Motorola which Lenovo uses on some devices).</summary>
    public static bool IsLenovoDevice() => Vendor == DeviceVendor.Lenovo;

    /// <summary>True on ASUS hardware only (DMI sys_vendor contains ASUS).
    /// Non-ASUS, non-Lenovo machines are Generic, NOT ASUS: ASUS-only
    /// features (fan curve editor, NumberPad, AURA hysteresis) stay off.</summary>
    public static bool IsAsusDevice() => Vendor == DeviceVendor.Asus;

    /// <summary>Neither ASUS nor Lenovo: universal feature set only.</summary>
    public static bool IsGenericDevice() => Vendor == DeviceVendor.Generic;

    // Model detection (Linux: DMI sysfs)

    public static string GetModel()
    {
        if (_model != null)
            return _model;

        _model = Platform.Linux.SysfsHelper.ReadAttribute(
            Path.Combine(Platform.Linux.SysfsHelper.DmiId, "product_name")) ?? "";

        return _model;
    }

    public static string GetModelShort()
    {
        string model = GetModel();
        int trim = model.LastIndexOf('_');
        if (trim > 0)
            model = model[..trim];
        return model;
    }

    /// <summary>Get BIOS version and model short name from DMI bios_version.</summary>
    public static (string? bios, string? modelShort) GetBiosAndModel()
    {
        if (_bios != null && _modelShort != null)
            return (_bios, _modelShort);

        string? biosVer = Platform.Linux.SysfsHelper.ReadAttribute(
            Path.Combine(Platform.Linux.SysfsHelper.DmiId, "bios_version"));

        if (biosVer != null)
        {
            string[] results = biosVer.Split(".");
            if (results.Length > 1)
            {
                _modelShort = results[0];
                _bios = results[1];
            }
            else
            {
                _modelShort = biosVer;
            }
        }

        return (_bios, _modelShort);
    }

    public static bool ContainsModel(string contains)
    {
        return GetModel().Contains(contains, StringComparison.OrdinalIgnoreCase);
    }

    // Model queries (ported from Windows AppConfig - all 67 methods)

    // Brand / family
    public static bool IsTUF() => ContainsModel("TUF") || ContainsModel("TX Gaming") || ContainsModel("TX Air");
    public static bool IsROG() => ContainsModel("ROG");
    public static bool IsStrix() => ContainsModel("Strix") || ContainsModel("Scar") || ContainsModel("G703G");
    public static bool IsVivoZenbook() => ContainsModel("Vivobook") || ContainsModel("Zenbook") || ContainsModel("EXPERTBOOK") || ContainsModel(" V16") || ContainsModel("ASUSLaptop");
    public static bool IsProArt() => ContainsModel("ProArt");
    public static bool IsDUO() => ContainsModel("Duo") || ContainsModel("GX550") || ContainsModel("GX551") || ContainsModel("GX650") || ContainsModel("UX840") || ContainsModel("UX482");
    /// <summary>ROG Ally family by explicit model prefixes (RC71L Ally,
    /// RC72L Ally X, RC73 next-gen), not a bare "RC7" substring that could
    /// match unrelated product names.</summary>
    public static bool IsAlly() =>
        ContainsModel("RC71") || ContainsModel("RC72") || ContainsModel("RC73");

    /// <summary>Lenovo Legion Go family, by DMI product_name numbers (same
    /// list hhd matches): Go 83E1, Go 2 83N0/83N1, Go S 83L3/83N6/83Q2/83Q3.</summary>
    public static bool IsLegionGo() =>
        ContainsModel("83E1") || ContainsModel("83N0") || ContainsModel("83N1")
        || ContainsModel("83L3") || ContainsModel("83N6") || ContainsModel("83Q2")
        || ContainsModel("83Q3");

    /// <summary>Any supported gaming handheld (ROG Ally or Legion Go).</summary>
    public static bool IsHandheldDevice() => IsAlly() || IsLegionGo();
    public static bool IsASUS() => ContainsModel("ROG") || ContainsModel("TUF") || ContainsModel("Vivobook") || ContainsModel("Zenbook");
    public static bool IsVivoZenPro() => ContainsModel("Vivobook") || ContainsModel("Zenbook") || ContainsModel("ProArt") || ContainsModel("EXPERTBOOK") || ContainsModel(" V16") || ContainsModel("ASUSLaptop");

    // Specific model variants
    public static bool IsARCNM() => ContainsModel("GZ301VIC");
    public static bool IsZ1325() => ContainsModel("GZ302E");
    public static bool IsZ13() => ContainsModel("Z13");
    public static bool IsPZ13() => ContainsModel("PZ13");
    public static bool IsS17() => ContainsModel("S17");
    public static bool IsX13() => ContainsModel("X13");
    public static bool IsG14AMD() => ContainsModel("GA402R");
    public static bool IsOnlyAIMAX() => ContainsModel("FA401EA") || ContainsModel("HN7306EA");
    public static bool IsAdvantageEdition() => ContainsModel("13QY");

    /// <summary>Lid logo LED present but not reported by the AURA feat1 probe.
    /// Covers Z13 and Advantage Edition (G513QY / G713QY).</summary>
    public static bool IsLidLogo() => IsZ13() || IsAdvantageEdition();

    // GPU / power management
    /// <summary>
    /// Models where firmware forgets dgpu_disable across reboots, requiring the
    /// boot service to force-set the Eco flag on every startup. Mirrors the
    /// upstream Windows IsEcoBootFix() model list. On these models the
    /// "Re-apply Eco on every boot" option is always on and grayed out.
    /// </summary>
    public static bool IsEcoBootFixModel() =>
        ContainsModel("G635L") || ContainsModel("G615L") ||
        ContainsModel("G835L") || ContainsModel("G815L") ||
        ContainsModel("FA506") || ContainsModel("FX517");

    public static bool NoGpu() => Is("no_gpu") || ContainsModel("UX540") || ContainsModel("M560") || ContainsModel("GZ302") || IsOnlyAIMAX();
    public static bool IsAMDiGPU() => ContainsModel("GV301RA") || ContainsModel("GV302XA") || ContainsModel("GZ302") || IsOnlyAIMAX() || IsAlly();

    public static bool IsForceSetGPUMode() => Is("gpu_mode_force_set") || (ContainsModel("503") && IsNotFalse("gpu_mode_force_set"));
    public static bool IsShutdownReset() => Is("shutdown_reset") || ContainsModel("FX507Z");
    // NOT WIRED ON LINUX (upstream parity stub).
    public static bool IsStopAC() => IsAlly() || Is("stop_ac");
    public static bool IsChargeLimit6080() => ContainsModel("GU405") || ContainsModel("GU606") || ContainsModel("H760") || ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605") || ContainsModel("GA503R") || (IsTUF() && !(ContainsModel("FX507Z") || ContainsModel("FA617") || ContainsModel("FA607")));

    // Dynamic boost
    public static bool DynamicBoost5() => ContainsModel("GZ301ZE");
    public static bool DynamicBoost15() => ContainsModel("FX507ZC4") || ContainsModel("GA403UM") || ContainsModel("GU605CP") || ContainsModel("FX608J") || ContainsModel("FX608L") || ContainsModel("FA608U") || ContainsModel("FA608P") || ContainsModel("FA401K") || ContainsModel("FA401UM") || ContainsModel("FA401UH") || ContainsModel("GA403GM") || ContainsModel("GU405AM") || ContainsModel("GU405AP") || ContainsModel("GU606AM") || ContainsModel("GU606AP");
    public static bool DynamicBoost20() => ContainsModel("GU605") || ContainsModel("GA605") || ContainsModel("GU405");

    // Performance mode
    public static bool IsAlwaysUltimate() => ContainsModel("FA507NUR") || ContainsModel("FA506NCR") || ContainsModel("FA507NVR");
    public static bool IsModeReapplyRequired() => Is("mode_reapply") || ContainsModel("FA401");
    public static bool IsResetRequired() => ContainsModel("GA403UI") || ContainsModel("GA403UU") || ContainsModel("GA403UV") || ContainsModel("FA507XV");
    public static bool IsPowerRequired() => ContainsModel("GU605M") || ContainsModel("FX507") || ContainsModel("FX517") || ContainsModel("FX707");

    public static bool IsReapplyTempRequired() => ContainsModel("GA402") || ContainsModel("GV601");

    public static bool IsReapplyRyzen() => ContainsModel("G614F") || ContainsModel("G814F") || ContainsModel("G733P");

    // FX506HC / FA808U: if dgpu_disable=1 is the live state at shutdown,
    // these BIOSes fail to re-enumerate the dGPU on next cold boot
    public static bool IsStandardModeFix() =>
        Is("shutdown_gpu") || ((ContainsModel("FX506HC") || ContainsModel("FA808U")) && IsNotFalse("shutdown_gpu"));

    public static bool IsManualModeRequired() =>
        Is("manual_mode") || ContainsModel("G733");

    public static bool IsKeystone() =>
        ContainsModel("G531") || ContainsModel("G731") || ContainsModel("G532") || ContainsModel("G732") || ContainsModel("G533") || ContainsModel("G733");

    // Fan control
    public static bool IsFanRequired() => ContainsModel("GA402X") || ContainsModel("GU604") || ContainsModel("G513") || ContainsModel("G713R") || ContainsModel("G713P") || ContainsModel("GU605") || ContainsModel("GA605") || ContainsModel("G634J") || ContainsModel("G834J") || ContainsModel("G614J") || ContainsModel("G814J") || ContainsModel("FX507V") || ContainsModel("FX507Z") || ContainsModel("FX608") || ContainsModel("FA608P") || ContainsModel("G614F") || ContainsModel("G614P") || ContainsModel("G614R") || ContainsModel("G733") || ContainsModel("H7606");
    public static bool IsClampFanDots() => IsNotFalse("fan_clamp");

    // RGB / AURA
    public static bool IsWhite() => ContainsModel("GA401") || ContainsModel("FX517Z") || ContainsModel("FX516P") || ContainsModel("X13") || IsARCNM() || ContainsModel("FA617N") || ContainsModel("FA617X") || NoAura() || Is("no_rgb");
    public static bool NoAura() => (ContainsModel("GA401I") && !ContainsModel("GA401IHR")) || ContainsModel("GA502IU") || ContainsModel("HN7306") || ContainsModel("M6500X");
    public static bool IsBacklightZones() => IsStrix() || IsZ13();

    // 2024+ models whose lightbar/keyboard use HID LampArray for direct RGB
    public static bool IsLampArray() =>
        ContainsModel("G614") || ContainsModel("G615") || ContainsModel("G634") || ContainsModel("G635") ||
        ContainsModel("G814") || ContainsModel("G815") || ContainsModel("G834") || ContainsModel("G835") ||
        IsSlash();
    /// <summary>True for chassis whose lightbar is wired L→R instead of R→L
    /// (G513 family). Selects the alternate 4-zone packet map in Aura.cs.</summary>
    public static bool IsStrix4ZoneFlipped() => ContainsModel("G513");
    public static bool IsNoDirectRGB() =>
        ContainsModel("GA503") || ContainsModel("G533Q") || ContainsModel("GU502") || IsSlash();
    public static bool IsSlash() => ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605") || IsSlashLong();
    public static bool IsSlashLong() => ContainsModel("GA405") || ContainsModel("GU405") || ContainsModel("GU606") || ContainsModel("GX651");
    public static bool IsSlashAura() => ContainsModel("GA605") || ContainsModel("GU605C") || ContainsModel("GA403W") || ContainsModel("GA403UM") || ContainsModel("GA403UP") || ContainsModel("GA403UH") || ContainsModel("GU405") || ContainsModel("GU606");
    public static bool IsAnimeMatrix() => ContainsModel("GA401") || ContainsModel("GA402") || ContainsModel("GU604V") || ContainsModel("G835") || ContainsModel("G815") || ContainsModel("G635") || ContainsModel("G615");

    // Dynamic Lighting
    public static bool IsDynamicLighting() => IsSlash() || IsIntelHX() || IsTUF() || IsZ13();
    public static bool IsDynamicLightingOnly() => ContainsModel("S560") || ContainsModel("M540") || ContainsModel("UX760");
    public static bool IsDynamicLightingInit() => ContainsModel("FA608") || Is("lighting_init");

    /// <summary>
    /// GPU mode UI flag. Optimized (auto AC/DC dGPU switching) is hidden by
    /// default because the underlying mechanism live dgpu_disable writes
    /// can stall the kernel for 30-60s on many firmware revisions (issue #94).
    /// The only reliable path to disable the dGPU is reboot-based, so auto
    /// switching is not safe. Power users can re-enable the Optimized button
    /// and the tray entry by setting "gpu_optimized_enabled": 1 in
    /// ~/.config/ghelper/config.json.
    /// </summary>
    public static bool IsOptimizedGpuModeEnabled() => Is("gpu_optimized_enabled");

    /// <summary>
    /// GPU backend selector. Two values are supported:
    ///   "asus-wmi" - the boot service writes dgpu_disable=1 via firmware
    ///                (asus-nb-wmi / asus-armoury). The modprobe + udev block
    ///                files are temporary trampolines cleaned up on success.
    ///   "pci"        The boot service never touches
    ///                firmware sysfs. The modprobe block + udev hot-remove
    ///                rule ARE the persistent Eco state and survive across
    ///                reboots until the user switches to Standard.
    /// Default "asus-wmi" so existing ASUS users see no behavioural change.
    /// Auto-detected on first run: <see cref="AutoDetectGpuBackendIfUnset"/>
    /// sets "pci" when no asus-nb-wmi / asus-armoury platform device is
    /// present, since the WMI path cannot work on those systems.
    /// </summary>
    public static string GetGpuBackend()
    {
        string? raw = GetString("gpu_backend");
        if (string.IsNullOrWhiteSpace(raw))
            return "asus-wmi";
        string normalized = raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pci" => "pci",
            "asus-wmi" => "asus-wmi",
            _ => "asus-wmi"
        };
    }

    /// <summary>True when the user (or first-run auto-detect) selected the
    /// PCI backend instead of the default ASUS WMI flow.</summary>
    public static bool IsPciGpuBackend() => GetGpuBackend() == "pci";

    /// <summary>
    /// On first run, choose a sane default for the GPU backend if the user
    /// has not yet set one. Non-ASUS systems (no asus-nb-wmi / asus-armoury
    /// platform device) cannot use the WMI flow at all, so we silently
    /// default them to "pci". ASUS systems keep the historical default of
    /// "asus-wmi" unless the user explicitly opts in.
    /// </summary>
    public static void AutoDetectGpuBackendIfUnset()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(GetString("gpu_backend")))
                return; // user already chose
            bool hasAsusPlatform =
                Directory.Exists("/sys/bus/platform/devices/asus-nb-wmi") ||
                Directory.Exists("/sys/devices/platform/asus-nb-wmi") ||
                Directory.Exists("/sys/class/firmware-attributes/asus-armoury");
            if (!hasAsusPlatform)
            {
                Set("gpu_backend", "pci");
                Logger.WriteLine("AppConfig: no ASUS platform device detected - defaulting gpu_backend=pci");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"AppConfig: AutoDetectGpuBackendIfUnset failed: {ex.Message}");
        }
    }

    // Keyboard / input
    public static bool IsStrixNumpad() => ContainsModel("G713R");
    public static bool NoMKeys() => (ContainsModel("Z13") && !IsARCNM()) || ContainsModel("FX706") || ContainsModel("FA706") || ContainsModel("FA506") || ContainsModel("FX506") || ContainsModel("Duo") || ContainsModel("FX505");
    public static bool IsM4Button() => IsDUO() || ContainsModel("GZ302EA");
    // NOT WIRED ON LINUX: FnLockKeymap.MediaKeysSubstrings is the live copy
    // (a superset that adds NoAura models). Keep both in sync when editing.
    public static bool MediaKeys() => (ContainsModel("GA401I") && !ContainsModel("GA401IHR")) || ContainsModel("G712L") || ContainsModel("GX502L");
    // NOT WIRED ON LINUX (upstream parity stub).
    public static bool IsHardwareHotkeys() => ContainsModel("FX506");
    public static bool IsHardwareTouchpadToggle() => GetModelShort().Contains("FA507");
    public static bool IsNoFNV() => ContainsModel("FX507") || ContainsModel("FX707");

    // CPU platform
    public static bool IsIntelHX() => ContainsModel("G814") || ContainsModel("G614") || ContainsModel("G834") || ContainsModel("G634") || ContainsModel("G835") || ContainsModel("G635") || ContainsModel("G815") || ContainsModel("G615");
    public static bool Is8Ecores() => ContainsModel("FX507Z") || ContainsModel("GU603ZV");
    public static bool IsCPULight() => ContainsModel("GA402X") || ContainsModel("GA605") || ContainsModel("GA403") || ContainsModel("FA507N") || ContainsModel("FA507X") || ContainsModel("FA707N") || ContainsModel("FA707X") || ContainsModel("GZ302") || ContainsModel("GU405") || ContainsModel("GX651");

    // Display
    public static bool IsOLED() =>
        ContainsModel("OLED") || IsSlash() || ContainsModel("M7600") || ContainsModel("UX64") ||
        ContainsModel("UX34") || ContainsModel("UX53") || ContainsModel("K360") || ContainsModel("X150") ||
        ContainsModel("M340") || ContainsModel("M350") || ContainsModel("K650") || ContainsModel("UM53") ||
        ContainsModel("K660") || ContainsModel("UX84") || ContainsModel("M650") || ContainsModel("M550") ||
        ContainsModel("M540") || ContainsModel("K340") || ContainsModel("K350") || ContainsModel("M140") ||
        ContainsModel("S540") || ContainsModel("S550") || ContainsModel("M7400") || ContainsModel("N650") ||
        ContainsModel("HN7306") || ContainsModel("H760") || ContainsModel("UX5406") || ContainsModel("M5606") ||
        ContainsModel("X513") || ContainsModel("N7400") || ContainsModel("UX760") || ContainsModel("Q530VJ") ||
        ContainsModel("TP3407") || ContainsModel("HT7407") || ContainsModel("3607");
    public static bool IsNoOverdrive() => Is("no_overdrive");
    public static bool SwappedBrightness() => ContainsModel("FA506IEB") || ContainsModel("FA506IH") || ContainsModel("FA506IC") || ContainsModel("FA506II") || ContainsModel("FX506LU") || ContainsModel("FX506IC") || ContainsModel("FX506LH") || ContainsModel("FA506IV") || ContainsModel("FA706IC") || ContainsModel("FA706IH");
    public static bool IsForceMiniled() =>
        ContainsModel("G834JYR") || ContainsModel("G834JZR") || ContainsModel("G634JZR") ||
        ContainsModel("G835L") || ContainsModel("G635L") || Is("force_miniled");

    // Form factor / misc
    public static bool HasTabletMode() => ContainsModel("X16") || ContainsModel("X13") || ContainsModel("Z13");
    public static bool IsSleepBacklight() => ContainsModel("FA617") || ContainsModel("FX507") || ContainsModel("FA507");
    public static bool NoWMI() => ContainsModel("GL704G") || ContainsModel("GM501G") || ContainsModel("GX501G");

    // UI / config-only
    public static bool IsBWIcon() => Is("bw_icon");
    public static bool IsAutoStatusLed() => Is("auto_status_led");

    // Battery-specific config check (original logic: fallback to zone config if bat-specific not set)
    public static bool IsOnBattery(string zone) => Get(zone + "_bat", Get(zone)) != 0;

    // Rear glow zone (Z13's rear-of-lid window/logo). Used by Aura.ApplyRearLight
    // to early-return on hardware without the rear-light controller (PID 0x18C6).
    public static bool HasRearLight() => IsZ13();

    // Auto-ASPM (PCIe link power state) toggle - on by default; consumed by
    // ModeControl when applying a performance mode. UI not exposed (kernel
    // pcie_aspm config often blocks runtime writes).
    public static bool IsAutoASPM() => IsNotFalse("aspm");

    // Models that handle FN-Lock in firmware (no software remapper needed).
    // Predicate added for upstream parity; Linux still runs the uinput
    // remapper on these models since both paths coexist without conflict.
    public static bool IsHardwareFnLock() => IsVivoZenPro() || ContainsModel("GZ302EA");

    // Models with inverted FN-Lock semantics (FW state 0=locked / 1=unlocked).
    // Predicate added for upstream parity; not consumed on Linux because the
    // uinput remapper is independent of firmware FN-Lock state.
    public static bool IsInvertedFNLock() =>
        ContainsModel("M140") || ContainsModel("S550") || ContainsModel("K650") || ContainsModel("P540") || IsTUF();

    public static bool IsSleepReset() =>
        Is("sleep_reset") || ContainsModel("GU605MI") || ContainsModel("GU605MV") || ContainsModel("GU605CR");

    // Helpers

    private static int GetCurrentMode()
    {
        // Current performance mode: 0=Balanced, 1=Turbo, 2=Silent
        return Get("performance_mode", 0);
    }

    /// <summary>Escape special characters for JSON string values.</summary>
    private static string EscapeJsonString(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    private static void Init()
    {
        _config = new Dictionary<string, JsonElement>();
        // Set default performance mode
        Set("performance_mode", 0);
        // Force immediate write for initial config
        FlushConfig();
    }

    // Matches "key": value pairs where value is a string, number, bool or null.
    // Used to salvage settings from a truncated/corrupted config file.
    private static readonly Regex KeyValueRegex = new(
        @"""((?:\\.|[^""\\])*)""\s*:\s*(""(?:\\.|[^""\\])*""|-?\d+(?:\.\d+)?|true|false|null)");

    /// <summary>
    /// Last-resort recovery for a corrupted config: extract all simple
    /// key-value pairs via regex, rebuild a valid JSON object and parse that.
    /// Returns false if nothing could be salvaged.
    /// </summary>
    private static bool TryRecoverConfig(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            var pairs = new Dictionary<string, string>();
            foreach (Match m in KeyValueRegex.Matches(File.ReadAllText(path)))
                pairs["\"" + m.Groups[1].Value + "\""] = m.Groups[2].Value;

            if (pairs.Count == 0)
                return false;

            string rebuilt = "{" + string.Join(",", pairs.Select(p => p.Key + ":" + p.Value)) + "}";
            _config = JsonSerializer.Deserialize(rebuilt, ConfigJsonContext.Default.DictionaryStringJsonElement)
                ?? new Dictionary<string, JsonElement>();
            Logger.WriteLine($"Recovered {pairs.Count} values from broken config {path}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Config recovery failed {path}: {ex.Message}");
            return false;
        }
    }

    private static void TryLoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile))
            {
                string text = File.ReadAllText(BackupFile);
                _config = JsonSerializer.Deserialize(text, ConfigJsonContext.Default.DictionaryStringJsonElement)
                    ?? new Dictionary<string, JsonElement>();
                Logger.WriteLine("Loaded config from backup");
            }
            else
            {
                Init();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Broken backup config: {ex.Message}");
            Init();
        }
    }

    /// <summary>Force immediate config write to disk (bypasses 2-second debounce timer).</summary>
    public static void Flush() => FlushConfig();

    private static void ScheduleWrite()
    {
        _writeTimer?.Stop();
        _writeTimer?.Start();
    }

    private static void FlushConfig()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_config, ConfigJsonContext.Default.DictionaryStringJsonElement);
            }

            // Write to a sibling temp file then rename. A crash or power loss
            // mid-write leaves the old config intact instead of a truncated one.
            // Serialised because the debounce timer and Flush() can land here
            // together and would otherwise share the one temp path.
            var tmpFile = ConfigFile + ".tmp";
            lock (_writeLock)
            {
                try
                {
                    File.WriteAllText(tmpFile, json);
                    File.Move(tmpFile, ConfigFile, true);
                }
                catch
                {
                    try
                    { File.Delete(tmpFile); }
                    catch { }
                    throw;
                }
            }

            _lastWrite = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            // Create backup after successful write
            Task.Run(async () =>
            {
                await Task.Delay(5000);
                if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - _lastWrite) < 4000)
                    return;

                try
                {
                    var text = File.ReadAllText(ConfigFile);
                    if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("{") && text.TrimEnd().EndsWith("}"))
                        File.Copy(ConfigFile, BackupFile, true);
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Config write failed: {ex.Message}");
        }
    }
}

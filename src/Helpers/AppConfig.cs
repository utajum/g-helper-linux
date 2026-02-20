using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GHelper.Linux.Helpers;

/// <summary>
/// AOT-compatible JSON serialization context for config.
/// We use Dictionary&lt;string, JsonElement&gt; instead of Dictionary&lt;string, object&gt;
/// because AOT source generators cannot resolve polymorphic 'object' values at compile time.
/// JsonElement is a self-describing value type that serializes/deserializes without reflection.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
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
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "ghelper");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
    private static readonly string BackupFile = ConfigFile + ".bak";

    private static Dictionary<string, JsonElement> _config = new();
    private static readonly object _lock = new();
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
        // Migrate from old config dir if it exists and new one doesn't
        MigrateOldConfigDir();

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

    /// <summary>
    /// One-time migration from ~/.config/ghelper-linux/ to ~/.config/ghelper/.
    /// Moves config.json and backup if they exist. Removes old dir if empty.
    /// </summary>
    private static void MigrateOldConfigDir()
    {
        try
        {
            var oldDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "ghelper-linux");

            if (!Directory.Exists(oldDir) || Directory.Exists(ConfigDir))
                return; // Nothing to migrate, or new dir already exists

            Directory.CreateDirectory(ConfigDir);

            foreach (var file in Directory.GetFiles(oldDir))
            {
                var dest = Path.Combine(ConfigDir, Path.GetFileName(file));
                File.Move(file, dest);
            }

            // Remove old dir if now empty
            if (Directory.GetFiles(oldDir).Length == 0 && Directory.GetDirectories(oldDir).Length == 0)
                Directory.Delete(oldDir);

            Logger.WriteLine($"Migrated config from {oldDir} → {ConfigDir}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Config migration failed (non-fatal): {ex.Message}");
        }
    }

    // ── Core Get/Set ──

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
                if (je.ValueKind == JsonValueKind.True) return 1;
                if (je.ValueKind == JsonValueKind.False) return 0;
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

    // ── Mode-aware Get/Set (per performance mode) ──

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

    // ── Fan curve config ──

    public static string GetFanParamName(int fanIndex, string paramName = "fan_profile")
    {
        int mode = GetCurrentMode();
        string fanName = fanIndex switch
        {
            1 => "gpu",
            2 => "mid",
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
                _ => StringToBytes("3A-3D-40-44-48-4D-51-62-08-11-16-1A-22-29-30-45"),
            },
        };
    }

    // ── Model detection (Linux: DMI sysfs) ──

    public static string GetModel()
    {
        if (_model != null) return _model;

        _model = Platform.Linux.SysfsHelper.ReadAttribute(
            Path.Combine(Platform.Linux.SysfsHelper.DmiId, "product_name")) ?? "";

        return _model;
    }

    public static string GetModelShort()
    {
        string model = GetModel();
        int trim = model.LastIndexOf('_');
        if (trim > 0) model = model[..trim];
        return model;
    }

    /// <summary>Get BIOS version and model short name from DMI bios_version.</summary>
    public static (string? bios, string? modelShort) GetBiosAndModel()
    {
        if (_bios != null && _modelShort != null) return (_bios, _modelShort);

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

    // ── Model queries (ported from Windows AppConfig — all 67 methods) ──

    // Brand / family
    public static bool IsTUF() => ContainsModel("TUF") || ContainsModel("TX Gaming") || ContainsModel("TX Air");
    public static bool IsROG() => ContainsModel("ROG");
    public static bool IsStrix() => ContainsModel("Strix") || ContainsModel("Scar") || ContainsModel("G703G");
    public static bool IsVivoZenbook() => ContainsModel("Vivobook") || ContainsModel("Zenbook") || ContainsModel("EXPERTBOOK") || ContainsModel(" V16");
    public static bool IsProArt() => ContainsModel("ProArt");
    public static bool IsDUO() => ContainsModel("Duo") || ContainsModel("GX550") || ContainsModel("GX551") || ContainsModel("GX650") || ContainsModel("UX840") || ContainsModel("UX482");
    public static bool IsAlly() => ContainsModel("RC7");
    public static bool IsASUS() => ContainsModel("ROG") || ContainsModel("TUF") || ContainsModel("Vivobook") || ContainsModel("Zenbook");
    public static bool IsVivoZenPro() => ContainsModel("Vivobook") || ContainsModel("Zenbook") || ContainsModel("ProArt") || ContainsModel("EXPERTBOOK") || ContainsModel(" V16");

    // Specific model variants
    public static bool IsARCNM() => ContainsModel("GZ301VIC");
    public static bool IsZ1325() => ContainsModel("GZ302E");
    public static bool IsZ13() => ContainsModel("Z13");
    public static bool IsPZ13() => ContainsModel("PZ13");
    public static bool IsS17() => ContainsModel("S17");
    public static bool IsX13() => ContainsModel("X13");
    public static bool IsG14AMD() => ContainsModel("GA402R");
    public static bool IsFA401EA() => ContainsModel("FA401EA");
    public static bool IsAdvantageEdition() => ContainsModel("13QY");

    // GPU / power management
    public static bool NoGpu() => Is("no_gpu") || ContainsModel("UX540") || ContainsModel("M560") || ContainsModel("GZ302") || IsFA401EA();
    public static bool IsAMDiGPU() => ContainsModel("GV301RA") || ContainsModel("GV302XA") || ContainsModel("GZ302") || IsFA401EA() || IsAlly();
    public static bool IsGPUFix() => Is("gpu_fix") || (ContainsModel("GA402X") && IsNotFalse("gpu_fix"));
    public static bool IsForceSetGPUMode() => Is("gpu_mode_force_set") || (ContainsModel("503") && IsNotFalse("gpu_mode_force_set"));
    public static bool IsNVPlatform() => Is("nv_platform");
    public static bool IsShutdownReset() => Is("shutdown_reset") || ContainsModel("FX507Z");
    public static bool IsStopAC() => IsAlly() || Is("stop_ac");
    public static bool IsChargeLimit6080() => ContainsModel("H760") || ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605") || ContainsModel("GA503R") || (IsTUF() && !(ContainsModel("FX507Z") || ContainsModel("FA617") || ContainsModel("FA607")));

    // Dynamic boost
    public static bool DynamicBoost5() => ContainsModel("GZ301ZE");
    public static bool DynamicBoost15() => ContainsModel("FX507ZC4") || ContainsModel("GA403UM") || ContainsModel("GU605CP") || ContainsModel("FX608J") || ContainsModel("FX608L") || ContainsModel("FA608U") || ContainsModel("FA608P") || ContainsModel("FA608W") || ContainsModel("FA401K") || ContainsModel("FA401UM") || ContainsModel("FA401UH");
    public static bool DynamicBoost20() => ContainsModel("GU605") || ContainsModel("GA605");

    // Performance mode
    public static bool NoAutoUltimate() => ContainsModel("G614") || ContainsModel("GU604") || ContainsModel("FX507") || ContainsModel("G513") || ContainsModel("FA617") || ContainsModel("G834") || ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605") || ContainsModel("GU603VV");
    public static bool IsAlwaysUltimate() => ContainsModel("FA507NUR") || ContainsModel("FA506NCR") || ContainsModel("FA507NVR");
    public static bool IsManualModeRequired() => IsMode("auto_apply_power") && (Is("manual_mode") || ContainsModel("G733"));
    public static bool IsModeReapplyRequired() => Is("mode_reapply") || ContainsModel("FA401");
    public static bool IsResetRequired() => ContainsModel("GA403") || ContainsModel("FA507XV");
    public static bool IsPowerRequired() => ContainsModel("FX507") || ContainsModel("FX517") || ContainsModel("FX707");

    // Fan control
    public static bool IsFanRequired() => ContainsModel("GA402X") || ContainsModel("GU604") || ContainsModel("G513") || ContainsModel("G713R") || ContainsModel("G713P") || ContainsModel("GU605") || ContainsModel("GA605") || ContainsModel("G634J") || ContainsModel("G834J") || ContainsModel("G614J") || ContainsModel("G814J") || ContainsModel("FX507V") || ContainsModel("FX507ZV") || ContainsModel("FX608") || ContainsModel("G614F") || ContainsModel("G614R") || ContainsModel("G733") || ContainsModel("H7606");
    public static bool IsClampFanDots() => Is("fan_clamp") || (IsTUF() && IsNotFalse("fan_clamp"));

    // RGB / AURA
    public static bool IsSingleColor() => ContainsModel("GA401") || ContainsModel("FX517Z") || ContainsModel("FX516P") || ContainsModel("X13") || IsARCNM() || ContainsModel("FA617N") || ContainsModel("FA617X") || NoAura() || Is("no_rgb");
    public static bool NoAura() => (ContainsModel("GA401I") && !ContainsModel("GA401IHR")) || ContainsModel("GA502IU") || ContainsModel("HN7306");
    public static bool IsAdvancedRGB() => IsStrix() || ContainsModel("GX650");
    public static bool IsBacklightZones() => IsStrix() || IsZ13();
    public static bool IsStrixLimitedRGB() =>
        ContainsModel("G614PM") || ContainsModel("G614PP") || ContainsModel("G614PR") || ContainsModel("G512LI") ||
        ContainsModel("G513R") || ContainsModel("G713QM") || ContainsModel("G713PV") || ContainsModel("G513IE") ||
        ContainsModel("G713RC") || ContainsModel("G713IC") || ContainsModel("G713PU") || ContainsModel("G513QM") ||
        ContainsModel("G513QC") || ContainsModel("G531G") || ContainsModel("G615JMR") || ContainsModel("G615LM") ||
        ContainsModel("G615LR") || ContainsModel("G815LR");
    public static bool IsPossible4ZoneRGB() =>
        ContainsModel("G614JI_") || ContainsModel("G614JV_") || ContainsModel("G614JZ") ||
        ContainsModel("G614JU") || IsStrixLimitedRGB();
    public static bool Is4ZoneRGB() => IsPossible4ZoneRGB() && !Is("per_key_rgb");
    public static bool IsNoDirectRGB() =>
        ContainsModel("GA503") || ContainsModel("G533Q") || ContainsModel("GU502") ||
        ContainsModel("GU603") || IsSlash() || IsAlly();
    public static bool IsSlash() => ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605");
    public static bool IsSlashAura() => ContainsModel("GA605") || ContainsModel("GU605C") || ContainsModel("GA403W") || ContainsModel("GA403UM") || ContainsModel("GA403UP") || ContainsModel("GA403UH");
    public static bool IsAnimeMatrix() => ContainsModel("GA401") || ContainsModel("GA402") || ContainsModel("GU604V") || ContainsModel("G835") || ContainsModel("G815") || ContainsModel("G635") || ContainsModel("G615");

    // Dynamic Lighting
    public static bool IsDynamicLighting() => IsSlash() || IsIntelHX() || IsTUF() || IsZ13();
    public static bool IsDynamicLightingOnly() => ContainsModel("S560") || ContainsModel("M540") || ContainsModel("UX760");
    public static bool IsDynamicLightingInit() => ContainsModel("FA608") || Is("lighting_init");

    // Keyboard / input
    public static bool IsInputBacklight() => ContainsModel("GA503") || IsSlash() || IsVivoZenPro();
    public static bool IsStrixNumpad() => ContainsModel("G713R");
    public static bool NoMKeys() => (ContainsModel("Z13") && !IsARCNM()) || ContainsModel("FX706") || ContainsModel("FA706") || ContainsModel("FA506") || ContainsModel("FX506") || ContainsModel("Duo") || ContainsModel("FX505");
    public static bool IsM4Button() => IsDUO() || ContainsModel("GZ302EA");
    public static bool MediaKeys() => (ContainsModel("GA401I") && !ContainsModel("GA401IHR")) || ContainsModel("G712L") || ContainsModel("GX502L");
    public static bool IsHardwareHotkeys() => ContainsModel("FX506");
    public static bool IsHardwareTouchpadToggle() => ContainsModel("FA507");
    public static bool IsNoFNV() => ContainsModel("FX507") || ContainsModel("FX707");

    // CPU platform
    public static bool IsIntelHX() => ContainsModel("G814") || ContainsModel("G614") || ContainsModel("G834") || ContainsModel("G634") || ContainsModel("G835") || ContainsModel("G635") || ContainsModel("G815") || ContainsModel("G615");
    public static bool Is8Ecores() => ContainsModel("FX507Z") || ContainsModel("GU603ZV");
    public static bool IsAMDLight() => ContainsModel("GA402X") || ContainsModel("GA605") || ContainsModel("GA403") || ContainsModel("FA507N") || ContainsModel("FA507X") || ContainsModel("FA707N") || ContainsModel("FA707X") || ContainsModel("GZ302");

    // Display
    public static bool IsOLED() =>
        ContainsModel("OLED") || IsSlash() || ContainsModel("M7600") || ContainsModel("UX64") ||
        ContainsModel("UX34") || ContainsModel("UX53") || ContainsModel("K360") || ContainsModel("X150") ||
        ContainsModel("M340") || ContainsModel("M350") || ContainsModel("K650") || ContainsModel("UM53") ||
        ContainsModel("K660") || ContainsModel("UX84") || ContainsModel("M650") || ContainsModel("M550") ||
        ContainsModel("M540") || ContainsModel("K340") || ContainsModel("K350") || ContainsModel("M140") ||
        ContainsModel("S540") || ContainsModel("S550") || ContainsModel("M7400") || ContainsModel("N650") ||
        ContainsModel("HN7306") || ContainsModel("H760") || ContainsModel("UX5406") || ContainsModel("M5606") ||
        ContainsModel("X513") || ContainsModel("N7400") || ContainsModel("UX760");
    public static bool IsNoOverdrive() => Is("no_overdrive");
    public static bool SwappedBrightness() => ContainsModel("FA506IEB") || ContainsModel("FA506IH") || ContainsModel("FA506IC") || ContainsModel("FA506II") || ContainsModel("FX506LU") || ContainsModel("FX506IC") || ContainsModel("FX506LH") || ContainsModel("FA506IV") || ContainsModel("FA706IC") || ContainsModel("FA706IH");
    public static bool SaveDimming() => Is("save_dimming");
    public static bool IsForceMiniled() =>
        ContainsModel("G834JYR") || ContainsModel("G834JZR") || ContainsModel("G634JZR") ||
        ContainsModel("G835LW") || ContainsModel("G835LX") || ContainsModel("G635LW") ||
        ContainsModel("G635LX") || Is("force_miniled");

    // Form factor / misc
    public static bool HasTabletMode() => ContainsModel("X16") || ContainsModel("X13") || ContainsModel("Z13");
    public static bool IsSleepBacklight() => ContainsModel("FA617") || ContainsModel("FX507");
    public static bool IsNoSleepEvent() => ContainsModel("FX505");
    public static bool NoWMI() => ContainsModel("GL704G") || ContainsModel("GM501G") || ContainsModel("GX501G");

    // UI / config-only
    public static bool IsBWIcon() => Is("bw_icon");
    public static bool IsAutoStatusLed() => Is("auto_status_led");

    // Battery-specific config check (original logic: fallback to zone config if bat-specific not set)
    public static bool IsOnBattery(string zone) => Get(zone + "_bat", Get(zone)) != 0;

    // ── Helpers ──

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

            File.WriteAllText(ConfigFile, json);
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

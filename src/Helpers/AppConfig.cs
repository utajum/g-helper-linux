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
/// Stores configuration in ~/.config/ghelper-linux/config.json (XDG-compliant).
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
        ".config", "ghelper-linux");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
    private static readonly string BackupFile = ConfigFile + ".bak";

    private static Dictionary<string, JsonElement> _config = new();
    private static readonly object _lock = new();
    private static System.Timers.Timer? _writeTimer;
    private static long _lastWrite;

    private static string? _model;

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

    public static bool ContainsModel(string contains)
    {
        return GetModel().Contains(contains, StringComparison.OrdinalIgnoreCase);
    }

    // ── Model queries (ported from Windows AppConfig) ──

    public static bool IsTUF() => ContainsModel("TUF") || ContainsModel("TX Gaming") || ContainsModel("TX Air");
    public static bool IsROG() => ContainsModel("ROG");
    public static bool IsStrix() => ContainsModel("Strix") || ContainsModel("Scar") || ContainsModel("G703G");
    public static bool IsVivoZenbook() => ContainsModel("Vivobook") || ContainsModel("Zenbook") || ContainsModel("EXPERTBOOK") || ContainsModel(" V16");
    public static bool IsProArt() => ContainsModel("ProArt");
    public static bool IsDUO() => ContainsModel("Duo") || ContainsModel("GX550") || ContainsModel("GX551") || ContainsModel("GX650") || ContainsModel("UX840") || ContainsModel("UX482");
    public static bool IsAlly() => ContainsModel("RC7");

    public static bool NoGpu() => Is("no_gpu") || ContainsModel("UX540") || ContainsModel("UM560") || ContainsModel("GZ302");
    public static bool IsSingleColor() => ContainsModel("GA401") || ContainsModel("FX517Z") || ContainsModel("FX516P") || ContainsModel("X13") || Is("no_rgb");
    public static bool IsNoOverdrive() => Is("no_overdrive");
    public static bool IsOLED() => ContainsModel("OLED") || ContainsModel("M7600") || ContainsModel("UX64") || ContainsModel("UX34");

    public static bool IsChargeLimit6080() =>
        ContainsModel("H760") || ContainsModel("GA403") || ContainsModel("GU605") || ContainsModel("GA605") ||
        ContainsModel("GA503R") || (IsTUF() && !(ContainsModel("FX507Z") || ContainsModel("FA617") || ContainsModel("FA607")));

    public static bool IsForceMiniled() =>
        ContainsModel("G834JYR") || ContainsModel("G834JZR") || ContainsModel("G634JZR") ||
        ContainsModel("G835LW") || ContainsModel("G835LX") || Is("force_miniled");

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

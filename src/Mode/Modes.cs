using GHelper.Linux.I18n;

namespace GHelper.Linux.Mode;

/// <summary>
/// Performance mode definitions and management.
/// Ported from G-Helper's Modes.cs - same mode system:
///   0 = Balanced, 1 = Turbo, 2 = Silent, 3+ = Custom
/// </summary>
public static class Modes
{
    private const int MaxModes = 20;

    /// <summary>Get the current performance mode index.</summary>
    public static int GetCurrent()
    {
        return Helpers.AppConfig.Get("performance_mode", 0);
    }

    /// <summary>Set the current performance mode.</summary>
    public static void SetCurrent(int mode)
    {
        bool onAc = App.Power?.IsOnAcPower() ?? true;
        Helpers.AppConfig.Set($"performance_{(onAc ? 1 : 0)}", mode);
        Helpers.AppConfig.Set("performance_mode", mode);
    }

    /// <summary>Get the base mode (0-2) for a given mode index.</summary>
    public static int GetBase(int mode)
    {
        if (mode >= 0 && mode <= 2)
            return mode;
        return Helpers.AppConfig.Get($"mode_base_{mode}", -1);
    }

    /// <summary>Get base mode of the current mode.</summary>
    public static int GetCurrentBase()
    {
        return GetBase(GetCurrent());
    }

    /// <summary>Check if a custom mode exists.</summary>
    public static bool Exists(int mode)
    {
        return GetBase(mode) >= 0;
    }

    /// <summary>Get the display name for a mode.</summary>
    public static string GetName(int mode)
    {
        return mode switch
        {
            0 => Labels.Get("mode_balanced"),
            1 => Labels.Get("mode_turbo"),
            2 => Labels.Get("mode_silent"),
            _ => Helpers.AppConfig.GetString($"mode_name_{mode}") ?? Labels.Format("mode_custom", mode - 2)
        };
    }

    /// <summary>Get display name for current mode.</summary>
    public static string GetCurrentName()
    {
        return GetName(GetCurrent());
    }

    /// <summary>Whether current mode is a custom mode (3+).</summary>
    public static bool IsCurrentCustom()
    {
        return GetCurrent() > 2;
    }

    /// <summary>Get all mode indices in cycle order.</summary>
    public static List<int> GetList()
    {
        var modes = new List<int> { 2, 0, 1 }; // Silent → Balanced → Turbo

        for (int i = 3; i < MaxModes; i++)
        {
            if (Exists(i))
                modes.Add(i);
        }

        return modes;
    }

    /// <summary>Get modes as dictionary (id → name).</summary>
    public static Dictionary<int, string> GetDictionary()
    {
        var modes = new Dictionary<int, string>
        {
            { 2, Labels.Get("mode_silent") },
            { 0, Labels.Get("mode_balanced") },
            { 1, Labels.Get("mode_turbo") }
        };

        for (int i = 3; i < MaxModes; i++)
        {
            if (Exists(i))
                modes.Add(i, GetName(i));
        }

        return modes;
    }

    /// <summary>Get the next mode in the cycle.</summary>
    public static int GetNext(bool back = false)
    {
        var modes = GetList();
        int index = modes.IndexOf(GetCurrent());

        if (back)
        {
            index--;
            if (index < 0)
                index = modes.Count - 1;
        }
        else
        {
            index++;
            if (index >= modes.Count)
                index = 0;
        }

        return modes[index];
    }

    // Settings stored per mode as "<setting>_<mode>". Power limits ("limit_*",
    // names come from firmware attributes) and fan curves ("fan_profile_*")
    // match by prefix.
    private static readonly HashSet<string> ModeSettings = new()
    {
        "fan_xgm", "gpu_boost", "gpu_temp", "gpu_power_lim", "gpu_tgp", "gpu_base_tgp",
        "gpu_clock_core", "gpu_clock_mem", "gpu_clock_lock", "gpu_mem_clock_lock",
        "cpu_uv", "cpu_uv_mv", "igpu_uv", "auto_uv", "auto_boost",
        "auto_apply", "auto_apply_power", "auto_apply_fans", "auto_apply_gpu",
        "hysteresis_up", "hysteresis_down", "mode_command",
    };

    private static bool IsModeSetting(string name) =>
        ModeSettings.Contains(name) || name.StartsWith("limit_") || name.StartsWith("fan_profile_");

    /// <summary>Config keys of <paramref name="mode"/> as (key, setting name).</summary>
    private static IEnumerable<(string Key, string Setting)> ModeKeys(int mode)
    {
        string suffix = $"_{mode}";
        foreach (var key in Helpers.AppConfig.GetKeys())
        {
            if (key.EndsWith(suffix) && IsModeSetting(key[..^suffix.Length]))
                yield return (key, key[..^suffix.Length]);
        }
    }

    /// <summary>Add a new custom mode (copies from current mode).</summary>
    public static int Add()
    {
        int currentMode = GetCurrent();

        for (int i = 3; i < MaxModes; i++)
        {
            if (Exists(i))
                continue;

            Helpers.AppConfig.Set($"mode_base_{i}", GetCurrentBase());
            Helpers.AppConfig.Set($"mode_name_{i}", Labels.Format("mode_custom", i - 2));

            foreach (var (key, setting) in ModeKeys(currentMode))
                Helpers.AppConfig.Copy(key, $"{setting}_{i}");

            return i;
        }

        return -1;
    }

    /// <summary>Remove a custom mode.</summary>
    public static void Remove(int mode)
    {
        if (mode <= 2)
            return; // Can't remove built-in modes

        foreach (var (key, _) in ModeKeys(mode))
            Helpers.AppConfig.Remove(key);
        Helpers.AppConfig.Remove($"mode_base_{mode}");
        Helpers.AppConfig.Remove($"mode_name_{mode}");
    }
}

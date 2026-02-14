namespace GHelper.Linux.Mode;

/// <summary>
/// Performance mode definitions and management.
/// Ported from G-Helper's Modes.cs — same mode system:
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
        if (mode >= 0 && mode <= 2) return mode;
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
            0 => "Balanced",
            1 => "Turbo",
            2 => "Silent",
            _ => Helpers.AppConfig.GetString($"mode_name_{mode}") ?? $"Custom {mode - 2}"
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
            if (Exists(i)) modes.Add(i);
        }

        return modes;
    }

    /// <summary>Get modes as dictionary (id → name).</summary>
    public static Dictionary<int, string> GetDictionary()
    {
        var modes = new Dictionary<int, string>
        {
            { 2, "Silent" },
            { 0, "Balanced" },
            { 1, "Turbo" }
        };

        for (int i = 3; i < MaxModes; i++)
        {
            if (Exists(i)) modes.Add(i, GetName(i));
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
            if (index < 0) index = modes.Count - 1;
        }
        else
        {
            index++;
            if (index >= modes.Count) index = 0;
        }

        return modes[index];
    }

    /// <summary>Add a new custom mode (copies from current mode).</summary>
    public static int Add()
    {
        int currentMode = GetCurrent();

        for (int i = 3; i < MaxModes; i++)
        {
            if (Exists(i)) continue;

            Helpers.AppConfig.Set($"mode_base_{i}", GetCurrentBase());
            Helpers.AppConfig.Set($"mode_name_{i}", $"Custom {i - 2}");

            return i;
        }

        return -1;
    }

    /// <summary>Remove a custom mode.</summary>
    public static void Remove(int mode)
    {
        if (mode <= 2) return; // Can't remove built-in modes

        var keys = new[]
        {
            "mode_base", "mode_name", "powermode", "limit_total",
            "limit_slow", "limit_fast", "limit_cpu",
            "fan_profile_cpu", "fan_profile_gpu", "fan_profile_mid",
            "gpu_power", "gpu_boost", "gpu_temp",
            "gpu_core", "gpu_memory", "gpu_clock_limit",
            "auto_boost", "auto_apply", "auto_apply_power",
            "auto_apply_fans"
        };

        foreach (var key in keys)
        {
            Helpers.AppConfig.Remove($"{key}_{mode}");
        }
    }
}

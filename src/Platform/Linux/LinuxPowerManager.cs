namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux power management via sysfs and kernel interfaces.
/// Replaces Windows PowrProf.dll functionality.
/// </summary>
public class LinuxPowerManager : IPowerManager
{
    private readonly string? _batteryDir;
    private readonly string? _acDir;

    public LinuxPowerManager()
    {
        _batteryDir = SysfsHelper.FindBattery();
        _acDir = SysfsHelper.FindAcAdapter();
    }

    public void SetCpuBoost(bool enabled)
    {
        // Try Intel pstate first
        var intelPath = Path.Combine(SysfsHelper.IntelPstate, "no_turbo");
        if (SysfsHelper.Exists(intelPath))
        {
            SysfsHelper.WriteInt(intelPath, enabled ? 0 : 1); // no_turbo: 0=boost on, 1=boost off
            return;
        }

        // Generic cpufreq boost
        var boostPath = "/sys/devices/system/cpu/cpufreq/boost";
        if (SysfsHelper.Exists(boostPath))
        {
            SysfsHelper.WriteInt(boostPath, enabled ? 1 : 0);
            return;
        }

        // AMD pstate
        var amdPath = "/sys/devices/system/cpu/amd_pstate/status";
        if (SysfsHelper.Exists(amdPath))
        {
            // AMD pstate boost is per-CPU via cpufreq/boost
            SysfsHelper.WriteInt("/sys/devices/system/cpu/cpufreq/boost", enabled ? 1 : 0);
        }
    }

    public bool GetCpuBoost()
    {
        // Intel pstate
        var intelPath = Path.Combine(SysfsHelper.IntelPstate, "no_turbo");
        if (SysfsHelper.Exists(intelPath))
            return SysfsHelper.ReadInt(intelPath, 0) == 0; // 0 = boost enabled

        // Generic
        return SysfsHelper.ReadInt("/sys/devices/system/cpu/cpufreq/boost", 1) == 1;
    }

    public void SetPlatformProfile(string profile)
    {
        // /sys/firmware/acpi/platform_profile accepts a subset of: "low-power", "balanced", "performance", "quiet"
        // Available profiles vary by firmware — read platform_profile_choices first
        if (SysfsHelper.Exists(SysfsHelper.PlatformProfile))
        {
            string? choices = SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfileChoices);
            if (choices != null)
            {
                // choices is space-separated, e.g. "low-power balanced performance"
                var available = choices.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (!available.Contains(profile))
                {
                    // Try mapping to closest available:
                    //   "low-power" → "quiet" → "balanced"
                    //   "performance" → "balanced"
                    string? fallback = profile switch
                    {
                        "low-power" when available.Contains("quiet") => "quiet",
                        "low-power" when available.Contains("balanced") => "balanced",
                        "performance" when available.Contains("balanced") => "balanced",
                        "quiet" when available.Contains("low-power") => "low-power",
                        _ => null
                    };

                    if (fallback != null)
                    {
                        Helpers.Logger.WriteLine($"Platform profile '{profile}' not available, using '{fallback}' (choices: {choices})");
                        profile = fallback;
                    }
                    else
                    {
                        Helpers.Logger.WriteLine($"Platform profile '{profile}' not supported (choices: {choices}), skipping");
                        return;
                    }
                }
            }

            SysfsHelper.WriteAttribute(SysfsHelper.PlatformProfile, profile);
            return;
        }

        // Fallback: power-profiles-daemon via D-Bus (via powerprofilesctl CLI)
        SysfsHelper.RunCommand("powerprofilesctl", $"set {profile}");
    }

    public string GetPlatformProfile()
    {
        if (SysfsHelper.Exists(SysfsHelper.PlatformProfile))
            return SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfile) ?? "balanced";

        return SysfsHelper.RunCommand("powerprofilesctl", "get") ?? "balanced";
    }

    public void SetAspmPolicy(string policy)
    {
        if (SysfsHelper.Exists(SysfsHelper.PcieAspm))
            SysfsHelper.WriteAttribute(SysfsHelper.PcieAspm, policy);
    }

    public string GetAspmPolicy()
    {
        var raw = SysfsHelper.ReadAttribute(SysfsHelper.PcieAspm) ?? "default";
        // The active policy is enclosed in brackets: "default performance [powersave] powersupersave"
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"\[(\w+)\]");
        return match.Success ? match.Groups[1].Value : raw;
    }

    public bool IsOnAcPower()
    {
        if (_acDir != null)
            return SysfsHelper.ReadInt(Path.Combine(_acDir, "online"), 0) == 1;

        // Fallback: check battery status
        if (_batteryDir != null)
        {
            var status = SysfsHelper.ReadAttribute(Path.Combine(_batteryDir, "status"));
            return status != null && (status == "Charging" || status == "Full" || status == "Not charging");
        }

        return true; // Assume AC if no battery info
    }

    public int GetBatteryPercentage()
    {
        if (_batteryDir == null) return -1;
        return SysfsHelper.ReadInt(Path.Combine(_batteryDir, "capacity"), -1);
    }

    public int GetBatteryDrainRate()
    {
        if (_batteryDir == null) return 0;

        // power_now is in microwatts
        int powerUw = SysfsHelper.ReadInt(Path.Combine(_batteryDir, "power_now"), 0);
        int powerMw = powerUw / 1000;

        var status = SysfsHelper.ReadAttribute(Path.Combine(_batteryDir, "status"));
        // Return positive for discharging, negative for charging
        return status == "Discharging" ? powerMw : -powerMw;
    }

    public int GetBatteryHealth()
    {
        if (_batteryDir == null) return -1;

        int fullCharge = SysfsHelper.ReadInt(Path.Combine(_batteryDir, "energy_full"), -1);
        int designCapacity = SysfsHelper.ReadInt(Path.Combine(_batteryDir, "energy_full_design"), -1);

        if (fullCharge < 0 || designCapacity <= 0) return -1;
        return (int)(fullCharge * 100.0 / designCapacity);
    }
}

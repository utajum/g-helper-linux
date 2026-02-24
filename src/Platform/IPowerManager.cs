namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over OS power management.
/// Windows: PowrProf.dll (power plans, CPU boost, ASPM)
/// Linux: sysfs cpufreq, platform_profile, pcie_aspm, logind
/// </summary>
public interface IPowerManager
{
    /// <summary>Enable/disable CPU turbo boost.</summary>
    void SetCpuBoost(bool enabled);

    /// <summary>Get CPU turbo boost state.</summary>
    bool GetCpuBoost();

    /// <summary>Set platform performance profile. "balanced", "performance", "low-power"</summary>
    void SetPlatformProfile(string profile);

    /// <summary>Get current platform profile.</summary>
    string GetPlatformProfile();

    /// <summary>Set PCIe ASPM policy. "default", "performance", "powersave", "powersupersave"</summary>
    void SetAspmPolicy(string policy);

    /// <summary>Get current ASPM policy.</summary>
    string GetAspmPolicy();

    /// <summary>Check if on AC power.</summary>
    bool IsOnAcPower();

    /// <summary>Get battery percentage (0-100).</summary>
    int GetBatteryPercentage();

    /// <summary>Get battery discharge rate in milliwatts (positive = discharging).</summary>
    int GetBatteryDrainRate();

    /// <summary>Get battery health (full charge capacity / design capacity * 100).</summary>
    int GetBatteryHealth();

    /// <summary>
    /// Fired when AC power state changes (plugged in / unplugged).
    /// Argument: true = on AC, false = on battery.
    /// </summary>
    event Action<bool>? PowerStateChanged;

    /// <summary>Start monitoring power state changes (polls AC adapter status).</summary>
    void StartPowerMonitoring();

    /// <summary>Stop monitoring power state changes.</summary>
    void StopPowerMonitoring();
}

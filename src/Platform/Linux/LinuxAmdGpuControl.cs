using System.Globalization;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux AMD GPU control using amdgpu sysfs interface.
/// 
/// Sysfs paths (under /sys/class/hwmon/hwmon*/):
///   - temp1_input: GPU edge temperature (millidegrees)
///   - freq1_input: Current GPU clock (Hz)
///   - freq2_input: Current memory clock (Hz)  
///   - power1_average: Current power draw (microwatts)
///   - device/gpu_busy_percent: GPU utilization
///   - device/pp_od_clk_voltage: Overclocking table
///   - device/pp_dpm_sclk: Available/active GPU P-states
///   - device/pp_dpm_mclk: Available/active memory P-states
///   - device/power_dpm_force_performance_level: Performance level control
/// 
/// Requires the amdgpu kernel module (loaded by default with AMD GPUs).
/// Root access may be needed for writes (overclocking).
/// </summary>
public class LinuxAmdGpuControl : IGpuControl
{
    private string? _hwmonDir;
    private string? _deviceDir;
    private string? _gpuName;
    private bool _available;

    public string Vendor => "AMD";

    public LinuxAmdGpuControl()
    {
        _hwmonDir = FindAmdGpuHwmon();

        if (_hwmonDir != null)
        {
            // The device sysfs dir is at hwmon's device symlink
            _deviceDir = Path.Combine(_hwmonDir, "device");
            if (!Directory.Exists(_deviceDir))
                _deviceDir = null;
        }

        _available = _hwmonDir != null;

        if (_available)
        {
            _gpuName = QueryGpuName();
            Helpers.Logger.WriteLine($"AMD GPU found: {_gpuName ?? "unknown"} (hwmon={_hwmonDir})");
        }
        else
        {
            Helpers.Logger.WriteLine("AMD dGPU not available");
        }
    }

    public bool IsAvailable() => _available;

    public string? GetGpuName() => _gpuName;

    // ── Temperature ──

    public int? GetCurrentTemp()
    {
        if (_hwmonDir == null) return null;

        // temp1_input = edge temperature in millidegrees Celsius
        int temp = SysfsHelper.ReadInt(Path.Combine(_hwmonDir, "temp1_input"), -1);
        if (temp > 0) return temp / 1000;

        // temp2_input = junction temperature (some GPUs)
        temp = SysfsHelper.ReadInt(Path.Combine(_hwmonDir, "temp2_input"), -1);
        if (temp > 0) return temp / 1000;

        return null;
    }

    // ── Utilization ──

    public int? GetGpuUse()
    {
        if (_deviceDir == null) return null;

        // gpu_busy_percent: 0-100
        int usage = SysfsHelper.ReadInt(Path.Combine(_deviceDir, "gpu_busy_percent"), -1);
        return usage >= 0 ? usage : null;
    }

    // ── Clocks ──

    public int? GetCurrentClock()
    {
        if (_hwmonDir == null) return null;

        // freq1_input: current GPU clock in Hz
        var freqStr = SysfsHelper.ReadAttribute(Path.Combine(_hwmonDir, "freq1_input"));
        if (freqStr != null && long.TryParse(freqStr, out long freqHz))
            return (int)(freqHz / 1_000_000); // Hz → MHz

        // Fallback: parse pp_dpm_sclk for the active state (marked with *)
        return ParseActiveClockFromDpm("pp_dpm_sclk");
    }

    public int? GetCurrentMemoryClock()
    {
        if (_hwmonDir == null) return null;

        // freq2_input: current memory clock in Hz
        var freqStr = SysfsHelper.ReadAttribute(Path.Combine(_hwmonDir, "freq2_input"));
        if (freqStr != null && long.TryParse(freqStr, out long freqHz))
            return (int)(freqHz / 1_000_000);

        // Fallback: parse pp_dpm_mclk
        return ParseActiveClockFromDpm("pp_dpm_mclk");
    }

    // ── Power ──

    public int? GetCurrentPower()
    {
        if (_hwmonDir == null) return null;

        // power1_average: average power in microwatts
        var powerStr = SysfsHelper.ReadAttribute(Path.Combine(_hwmonDir, "power1_average"));
        if (powerStr != null && long.TryParse(powerStr, out long microWatts))
            return (int)(microWatts / 1_000_000); // µW → W

        return null;
    }

    // ── Clock Control ──

    public void SetClockLimit(int maxMhz)
    {
        if (_deviceDir == null) return;

        var ppOdPath = Path.Combine(_deviceDir, "pp_od_clk_voltage");
        var perfLevelPath = Path.Combine(_deviceDir, "power_dpm_force_performance_level");

        if (maxMhz <= 0)
        {
            // Reset to automatic
            SysfsHelper.WriteAttribute(perfLevelPath, "auto");
            SysfsHelper.WriteAttribute(ppOdPath, "r"); // reset
            SysfsHelper.WriteAttribute(ppOdPath, "c"); // commit
            Helpers.Logger.WriteLine("AMD GPU: Reset clock limits to auto");
        }
        else
        {
            maxMhz = Math.Clamp(maxMhz, 200, 3000);

            // Set manual performance level to allow overriding
            SysfsHelper.WriteAttribute(perfLevelPath, "manual");

            // Write to pp_od_clk_voltage: "s 1 <maxMhz>"
            // s = sclk, 1 = highest state
            SysfsHelper.WriteAttribute(ppOdPath, $"s 1 {maxMhz}");
            SysfsHelper.WriteAttribute(ppOdPath, "c"); // commit

            Helpers.Logger.WriteLine($"AMD GPU: Set max clock to {maxMhz} MHz");
        }
    }

    public void SetCoreClockOffset(int offsetMhz)
    {
        if (_deviceDir == null) return;

        var ppOdPath = Path.Combine(_deviceDir, "pp_od_clk_voltage");
        var perfLevelPath = Path.Combine(_deviceDir, "power_dpm_force_performance_level");

        // amdgpu doesn't have a direct "offset" concept like NVIDIA
        // Instead, we modify the OD voltage/frequency curve
        // For RDNA2+, we can use: "vo <point> <freq> <voltage>"

        if (offsetMhz == 0)
        {
            SysfsHelper.WriteAttribute(ppOdPath, "r"); // reset
            SysfsHelper.WriteAttribute(ppOdPath, "c"); // commit
            SysfsHelper.WriteAttribute(perfLevelPath, "auto");
            Helpers.Logger.WriteLine("AMD GPU: Reset core clock offset");
            return;
        }

        // Get current max clock and add offset
        int? currentMax = GetMaxSclk();
        if (currentMax == null)
        {
            Helpers.Logger.WriteLine("AMD GPU: Cannot determine current max sclk for offset");
            return;
        }

        int targetClock = currentMax.Value + offsetMhz;
        targetClock = Math.Max(200, targetClock);

        SysfsHelper.WriteAttribute(perfLevelPath, "manual");
        SysfsHelper.WriteAttribute(ppOdPath, $"s 1 {targetClock}");
        SysfsHelper.WriteAttribute(ppOdPath, "c");

        Helpers.Logger.WriteLine($"AMD GPU: Set core clock offset {offsetMhz} MHz (target={targetClock} MHz)");
    }

    public void SetMemoryClockOffset(int offsetMhz)
    {
        if (_deviceDir == null) return;

        var ppOdPath = Path.Combine(_deviceDir, "pp_od_clk_voltage");
        var perfLevelPath = Path.Combine(_deviceDir, "power_dpm_force_performance_level");

        if (offsetMhz == 0)
        {
            SysfsHelper.WriteAttribute(ppOdPath, "r");
            SysfsHelper.WriteAttribute(ppOdPath, "c");
            SysfsHelper.WriteAttribute(perfLevelPath, "auto");
            Helpers.Logger.WriteLine("AMD GPU: Reset memory clock offset");
            return;
        }

        int? currentMax = GetMaxMclk();
        if (currentMax == null)
        {
            Helpers.Logger.WriteLine("AMD GPU: Cannot determine current max mclk for offset");
            return;
        }

        int targetClock = currentMax.Value + offsetMhz;
        targetClock = Math.Max(200, targetClock);

        SysfsHelper.WriteAttribute(perfLevelPath, "manual");
        SysfsHelper.WriteAttribute(ppOdPath, $"m 1 {targetClock}");
        SysfsHelper.WriteAttribute(ppOdPath, "c");

        Helpers.Logger.WriteLine($"AMD GPU: Set memory clock offset {offsetMhz} MHz (target={targetClock} MHz)");
    }

    // ── Private helpers ──

    /// <summary>
    /// Find the discrete AMD GPU hwmon (not the iGPU).
    /// Preference: device with higher power limit → likely dGPU
    /// </summary>
    private static string? FindAmdGpuHwmon()
    {
        var hwmons = SysfsHelper.FindAllHwmonByName("amdgpu");
        if (hwmons.Count == 0) return null;
        if (hwmons.Count == 1) return hwmons[0];

        // Multiple amdgpu hwmons — pick the dGPU (higher power cap)
        string? best = null;
        long bestPower = 0;

        foreach (var hwmon in hwmons)
        {
            // power1_cap in microwatts — dGPU typically has much higher cap
            var capStr = SysfsHelper.ReadAttribute(Path.Combine(hwmon, "power1_cap"));
            if (capStr != null && long.TryParse(capStr, out long cap))
            {
                if (cap > bestPower)
                {
                    bestPower = cap;
                    best = hwmon;
                }
            }
        }

        return best ?? hwmons[0];
    }

    private string? QueryGpuName()
    {
        if (_deviceDir == null) return null;

        // Try to read the PCI device name
        var marketingName = SysfsHelper.ReadAttribute(Path.Combine(_deviceDir, "product_name"));
        if (marketingName != null) return marketingName;

        // Fallback: read from lspci
        var vbiosVersion = SysfsHelper.ReadAttribute(Path.Combine(_deviceDir, "vbios_version"));
        if (vbiosVersion != null) return $"AMD GPU ({vbiosVersion})";

        // Last resort: use the hwmon name
        return "AMD GPU";
    }

    /// <summary>
    /// Parse pp_dpm_sclk or pp_dpm_mclk for the currently active P-state.
    /// Format: "0: 500Mhz *\n1: 1200Mhz\n2: 2400Mhz"
    /// The active state is marked with *
    /// </summary>
    private int? ParseActiveClockFromDpm(string dpmFile)
    {
        if (_deviceDir == null) return null;

        var content = SysfsHelper.ReadAttribute(Path.Combine(_deviceDir, dpmFile));
        if (content == null) return null;

        foreach (var line in content.Split('\n'))
        {
            if (line.Contains('*'))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*Mhz");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int mhz))
                    return mhz;
            }
        }

        return null;
    }

    /// <summary>Get the max sclk from pp_od_clk_voltage or pp_dpm_sclk.</summary>
    private int? GetMaxSclk()
    {
        return GetMaxClockFromDpm("pp_dpm_sclk");
    }

    /// <summary>Get the max mclk from pp_dpm_mclk.</summary>
    private int? GetMaxMclk()
    {
        return GetMaxClockFromDpm("pp_dpm_mclk");
    }

    private int? GetMaxClockFromDpm(string dpmFile)
    {
        if (_deviceDir == null) return null;

        var content = SysfsHelper.ReadAttribute(Path.Combine(_deviceDir, dpmFile));
        if (content == null) return null;

        int maxClock = -1;
        foreach (var line in content.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*Mhz");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int mhz))
            {
                if (mhz > maxClock)
                    maxClock = mhz;
            }
        }

        return maxClock > 0 ? maxClock : null;
    }
}

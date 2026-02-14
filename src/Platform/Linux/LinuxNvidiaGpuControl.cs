using System.Globalization;
using System.Text.RegularExpressions;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux NVIDIA GPU control using:
///   - nvidia-smi CLI for monitoring and clock limits
///   - /sys/class/hwmon/ nvidia hwmon for temperature
///   - nvidia-settings for clock offsets (requires X11)
/// 
/// Requires nvidia proprietary driver to be installed.
/// nvidia-smi is the most reliable cross-version approach on Linux.
/// 
/// NVML P/Invoke is possible but nvidia-smi is always present when the driver is
/// installed and handles version compatibility automatically.
/// </summary>
public class LinuxNvidiaGpuControl : IGpuControl
{
    private string? _hwmonDir;
    private string? _gpuName;
    private bool _available;

    public string Vendor => "NVIDIA";

    public LinuxNvidiaGpuControl()
    {
        _hwmonDir = SysfsHelper.FindHwmonByName("nvidia");
        _available = CheckAvailability();

        if (_available)
        {
            _gpuName = QueryGpuName();
            Helpers.Logger.WriteLine($"NVIDIA GPU found: {_gpuName ?? "unknown"}");
        }
        else
        {
            Helpers.Logger.WriteLine("NVIDIA GPU not available (nvidia-smi not found or no GPU)");
        }
    }

    public bool IsAvailable() => _available;

    public string? GetGpuName() => _gpuName;

    // ── Temperature ──

    public int? GetCurrentTemp()
    {
        // Method 1: hwmon sysfs (fastest, no process spawn)
        if (_hwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_hwmonDir, "temp1_input"), -1);
            if (temp > 0) return temp / 1000;
        }

        // Method 2: nvidia-smi
        var output = RunNvidiaSmi("--query-gpu=temperature.gpu", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int smiTemp))
            return smiTemp;

        return null;
    }

    // ── Utilization ──

    public int? GetGpuUse()
    {
        var output = RunNvidiaSmi("--query-gpu=utilization.gpu", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int usage))
            return usage;

        return null;
    }

    // ── Clocks ──

    public int? GetCurrentClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.graphics", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int clock))
            return clock;

        return null;
    }

    public int? GetCurrentMemoryClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.memory", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int clock))
            return clock;

        return null;
    }

    // ── Power ──

    public int? GetCurrentPower()
    {
        var output = RunNvidiaSmi("--query-gpu=power.draw", "--format=csv,noheader,nounits");
        if (output != null && double.TryParse(output.Trim(), CultureInfo.InvariantCulture, out double watts))
            return (int)Math.Round(watts);

        return null;
    }

    // ── Clock Limits ──

    public void SetClockLimit(int maxMhz)
    {
        if (!_available) return;

        if (maxMhz <= 0)
        {
            // Reset GPU clock limit
            SysfsHelper.RunCommand("nvidia-smi", "-rgc");
            Helpers.Logger.WriteLine("NVIDIA: Reset GPU clock limit");
        }
        else
        {
            maxMhz = Math.Clamp(maxMhz, 200, 3000);
            SysfsHelper.RunCommand("nvidia-smi", $"-lgc 0,{maxMhz}");
            Helpers.Logger.WriteLine($"NVIDIA: Set GPU clock limit to {maxMhz} MHz");
        }
    }

    // ── Clock Offsets ──

    public void SetCoreClockOffset(int offsetMhz)
    {
        if (!_available) return;

        // nvidia-settings requires X11. On X11, this works:
        // nvidia-settings -a "[gpu:0]/GPUGraphicsClockOffsetAllPerformanceLevels=<offset>"
        // On Wayland, we need nvidia-smi or coolbits
        var result = SysfsHelper.RunCommand("nvidia-settings",
            $"-a \"[gpu:0]/GPUGraphicsClockOffsetAllPerformanceLevels={offsetMhz}\"");

        if (result == null)
        {
            // Fallback: try nvidia-smi lock-gpu-clocks with offset
            // This is less precise but works on Wayland
            Helpers.Logger.WriteLine($"NVIDIA: nvidia-settings not available, GPU core offset not set");
            return;
        }

        Helpers.Logger.WriteLine($"NVIDIA: Set core clock offset to {offsetMhz} MHz");
    }

    public void SetMemoryClockOffset(int offsetMhz)
    {
        if (!_available) return;

        var result = SysfsHelper.RunCommand("nvidia-settings",
            $"-a \"[gpu:0]/GPUMemoryTransferRateOffsetAllPerformanceLevels={offsetMhz}\"");

        if (result == null)
        {
            Helpers.Logger.WriteLine($"NVIDIA: nvidia-settings not available, GPU memory offset not set");
            return;
        }

        Helpers.Logger.WriteLine($"NVIDIA: Set memory clock offset to {offsetMhz} MHz");
    }

    // ── Extended queries (not in interface but useful) ──

    /// <summary>Get comprehensive GPU status in one call.</summary>
    public (int? temp, int? usage, int? clock, int? memClock, int? power, int? fanSpeed)?
        GetFullStatus()
    {
        // Single nvidia-smi call for all values (much faster than 5 separate calls)
        var output = RunNvidiaSmi(
            "--query-gpu=temperature.gpu,utilization.gpu,clocks.current.graphics,clocks.current.memory,power.draw,fan.speed",
            "--format=csv,noheader,nounits");

        if (output == null) return null;

        var parts = output.Split(',');
        if (parts.Length < 6) return null;

        int? ParsePart(string s)
        {
            s = s.Trim();
            if (s == "[N/A]" || s == "N/A" || s == "") return null;
            if (int.TryParse(s, out int v)) return v;
            if (double.TryParse(s, CultureInfo.InvariantCulture, out double d)) return (int)Math.Round(d);
            return null;
        }

        return (
            temp: ParsePart(parts[0]),
            usage: ParsePart(parts[1]),
            clock: ParsePart(parts[2]),
            memClock: ParsePart(parts[3]),
            power: ParsePart(parts[4]),
            fanSpeed: ParsePart(parts[5])
        );
    }

    // ── Private helpers ──

    private bool CheckAvailability()
    {
        // Check if nvidia-smi exists and returns successfully
        var output = RunNvidiaSmi("--query-gpu=name", "--format=csv,noheader");
        return output != null && output.Trim().Length > 0;
    }

    private string? QueryGpuName()
    {
        var output = RunNvidiaSmi("--query-gpu=name", "--format=csv,noheader");
        return output?.Trim();
    }

    private static string? RunNvidiaSmi(string query, string format = "")
    {
        var args = string.IsNullOrEmpty(format) ? query : $"{query} {format}";
        return SysfsHelper.RunCommand("nvidia-smi", args);
    }
}

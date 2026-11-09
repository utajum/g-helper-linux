using System;
using System.IO;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.Block;

/// <summary>Tracks the dGPU PCI power state: the block saves power only when it reaches D3cold.</summary>
public static class DgpuPowerMonitor
{
    /// <summary>PCI D-states, as reported by sysfs.</summary>
    public enum PowerState
    {
        Unknown,
        D0,
        D1,
        D2,
        D3Hot,
        D3Cold,
    }

    /// <summary>How long the dGPU may stay awake with the block on before a warning.</summary>
    private static readonly TimeSpan ColdGrace = TimeSpan.FromSeconds(60);

    private static string? _bdf;
    private static DateTime _rescanUtc;
    private static DateTime _blockedSinceUtc;
    private static bool _reachedCold;
    private static bool _warned;

    /// <summary>Bus address of the discrete GPU, or null; cached for the boot.</summary>
    public static string? DiscreteBdf(bool rescan = false)
    {
        if (_bdf != null || (!rescan && DateTime.UtcNow < _rescanUtc))
            return _bdf;

        // Re-scan slowly: a dGPU removed in Eco appears after Standard.
        _rescanUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        try
        {
            foreach (var dev in Directory.EnumerateDirectories("/sys/bus/pci/devices"))
            {
                string cls = SysfsHelper.ReadAttribute(Path.Combine(dev, "class")) ?? "";
                if (!cls.StartsWith("0x0300", StringComparison.Ordinal)
                    && !cls.StartsWith("0x0302", StringComparison.Ordinal))
                    continue;
                if (SysfsHelper.ReadInt(Path.Combine(dev, "boot_vga"), 0) == 1)
                    continue; // iGPU drives the panel, never the one we block
                _bdf = Path.GetFileName(dev);
                break;
            }
        }
        catch { }
        return _bdf;
    }

    /// <summary>Map the sysfs power_state string onto the enum.</summary>
    public static PowerState Parse(string? raw) => (raw ?? "").Trim() switch
    {
        "D0" => PowerState.D0,
        "D1" => PowerState.D1,
        "D2" => PowerState.D2,
        "D3hot" => PowerState.D3Hot,
        "D3cold" => PowerState.D3Cold,
        _ => PowerState.Unknown,
    };

    /// <summary>Current D-state of the discrete GPU.</summary>
    public static PowerState Current()
    {
        string? bdf = DiscreteBdf();
        if (bdf == null)
            return PowerState.Unknown;
        return Parse(SysfsHelper.ReadAttributeSilent($"/sys/bus/pci/devices/{bdf}/power_state"));
    }

    /// <summary>Display string for the UI, e.g. "D3cold".</summary>
    public static string CurrentText() => Current() switch
    {
        PowerState.D0 => "D0",
        PowerState.D1 => "D1",
        PowerState.D2 => "D2",
        PowerState.D3Hot => "D3hot",
        PowerState.D3Cold => "D3cold",
        _ => "unknown",
    };

    /// <summary>True when the dGPU is powered down as far as it goes.</summary>
    public static bool IsCold() => Current() == PowerState.D3Cold;

    /// <summary>Start the clock on reaching D3cold.</summary>
    public static void NoteBlockEnabled()
    {
        _blockedSinceUtc = DateTime.UtcNow;
        _reachedCold = false;
        _warned = false;
        Logger.WriteLine($"DgpuPowerMonitor: block enabled, dGPU is {CurrentText()}");
    }

    /// <summary>Called when the block turns off.</summary>
    public static void NoteBlockDisabled()
    {
        _blockedSinceUtc = DateTime.MinValue;
        _reachedCold = false;
        _warned = false;
    }

    /// <summary>Poll while blocked: a warning key once per enable when the dGPU stays awake, else null.</summary>
    public static string? PollForWarning()
    {
        if (_blockedSinceUtc == DateTime.MinValue)
            return null;

        if (IsCold())
        {
            if (!_reachedCold)
            {
                _reachedCold = true;
                Logger.WriteLine("DgpuPowerMonitor: dGPU reached D3cold, block is saving power");
            }
            return null;
        }

        if (_reachedCold || _warned)
            return null; // woken again by something legitimate, or already told
        if (DateTime.UtcNow - _blockedSinceUtc < ColdGrace)
            return null;

        _warned = true;
        string detail = DescribeWhyAwake();
        Logger.WriteLine($"DgpuPowerMonitor: dGPU still {CurrentText()} after " +
                         $"{ColdGrace.TotalSeconds:0}s with the block on ({detail})");
        return detail;
    }

    /// <summary>Did the dGPU reach D3cold since the block was enabled.</summary>
    public static bool ReachedCold => _reachedCold;

    /// <summary>Likely reason a blocked dGPU stays awake, as a label key.</summary>
    public static string DescribeWhyAwake()
    {
        string? bdf = DiscreteBdf();
        if (bdf == null)
            return "gpu_block_cold_unknown";

        string powerDir = $"/sys/bus/pci/devices/{bdf}/power";
        if (SysfsHelper.ReadAttributeSilent(Path.Combine(powerDir, "control")) != "auto")
            return "gpu_block_cold_runtimepm";
        if (RuntimeD3Off(SysfsHelper.ReadAttributeSilent($"/proc/driver/nvidia/gpus/{bdf}/power")))
            return "gpu_block_cold_unknown";
        if (SysfsHelper.ReadInt(Path.Combine(powerDir, "runtime_usage"), -1) > 0)
            return "gpu_block_cold_holders";
        return "gpu_block_cold_unknown";
    }

    /// <summary>NVIDIA reports runtime D3 off: the driver itself keeps the card awake.</summary>
    internal static bool RuntimeD3Off(string? procPower)
    {
        foreach (var line in (procPower ?? "").Split('\n'))
            if (line.StartsWith("Runtime D3 status:", StringComparison.Ordinal))
                return !line.Contains("Enabled", StringComparison.Ordinal);
        return false;
    }
}

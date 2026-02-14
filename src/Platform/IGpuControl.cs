namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over GPU monitoring and control.
/// Windows: NvAPIWrapper.Net (NVIDIA), atiadlxx.dll (AMD)
/// Linux: nvidia-smi/NVML (NVIDIA), amdgpu sysfs (AMD)
/// </summary>
public interface IGpuControl
{
    /// <summary>GPU vendor name.</summary>
    string Vendor { get; }

    /// <summary>GPU model name (e.g., "RTX 4060 Laptop").</summary>
    string? GetGpuName();

    /// <summary>Get GPU temperature in Celsius. null if unavailable.</summary>
    int? GetCurrentTemp();

    /// <summary>Get GPU utilization percentage. null if unavailable.</summary>
    int? GetGpuUse();

    /// <summary>Get GPU clock in MHz. null if unavailable.</summary>
    int? GetCurrentClock();

    /// <summary>Get GPU memory clock in MHz. null if unavailable.</summary>
    int? GetCurrentMemoryClock();

    /// <summary>Get GPU power draw in watts. null if unavailable.</summary>
    int? GetCurrentPower();

    /// <summary>Set maximum GPU clock limit in MHz. 0 to reset.</summary>
    void SetClockLimit(int maxMhz);

    /// <summary>Set core clock offset in MHz (overclocking).</summary>
    void SetCoreClockOffset(int offsetMhz);

    /// <summary>Set memory clock offset in MHz.</summary>
    void SetMemoryClockOffset(int offsetMhz);

    /// <summary>Check if this GPU control is available.</summary>
    bool IsAvailable();
}

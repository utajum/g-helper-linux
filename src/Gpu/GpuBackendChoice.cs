using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;

namespace GHelper.Linux.Gpu;

/// <summary>
/// Which dGPU backend is in use ("asus-wmi", "pci" or "ebpf"). The boot service
/// follows /etc/ghelper/backend, so the marker is always written before the config.
/// </summary>
internal static class GpuBackendChoice
{
    // Backend the first start picked by itself; null once the user owns the choice.
    private static string? _autoPicked;

    /// <summary>First start: eBPF where it can run now, else PCI without ASUS firmware. Existing installs keep their backend.</summary>
    internal static void ResolveAtStartup(IHardwareControl? wmi, Func<bool> bpfAvailable)
    {
        _autoPicked = null;
        if (!string.IsNullOrWhiteSpace(AppConfig.GetString("gpu_backend")))
            return;
        if (AppConfig.IsNewInstall && TryBpf(wmi, bpfAvailable))
            return;
        if (!AppConfig.HasAsusPlatformDevice())
        {
            AppConfig.Set("gpu_backend", "pci");
            Logger.WriteLine("GpuBackendChoice: no ASUS platform device - gpu_backend=pci");
        }
        if (AppConfig.IsNewInstall)
            _autoPicked = AppConfig.GetString("gpu_backend") ?? "";
    }

    /// <summary>First start, after the system files prompt: try eBPF again unless the user picked a backend.</summary>
    internal static bool RetryNewInstall(IHardwareControl? wmi, Func<bool> bpfAvailable)
    {
        if (_autoPicked == null || (AppConfig.GetString("gpu_backend") ?? "") != _autoPicked)
            return false;
        _autoPicked = null;
        return TryBpf(wmi, bpfAvailable);
    }

    /// <summary>Switch the backend: marker first, then config. False leaves both as they were.</summary>
    internal static bool Select(string backend)
    {
        if (backend is not ("asus-wmi" or "pci" or "ebpf"))
        {
            Logger.WriteLine($"GpuBackendChoice: rejected invalid backend '{backend}'");
            return false;
        }
        // The MUX latch is real firmware state, so a backend change keeps it.
        if (!GpuBootFiles.SetBackend(backend))
        {
            Logger.WriteLine($"GpuBackendChoice: marker not saved, staying on {AppConfig.GetGpuBackend()}");
            return false;
        }
        AppConfig.Set("gpu_backend", backend);
        AppConfig.Flush();
        Logger.WriteLine($"GpuBackendChoice: gpu_backend={backend}");
        return true;
    }

    /// <summary>eBPF cannot stay: the config takes the marker's other backend, else the fallback. Returns it.</summary>
    internal static string LeaveBpf()
    {
        string marker = GpuBootFiles.ReadBackend();
        string backend = marker is "asus-wmi" or "pci" ? marker : Fallback();
        AppConfig.Set("gpu_backend", backend);
        Logger.WriteLine($"GpuBackendChoice: leaving ebpf, marker '{marker}' - gpu_backend={backend}");
        return backend;
    }

    // Backend when eBPF cannot run: MUX mode on ASUS, PCI elsewhere.
    private static string Fallback() => AppConfig.HasAsusPlatformDevice() ? "asus-wmi" : "pci";

    /// <summary>No backend box ticked means eBPF, unless this kernel or build can never run it.</summary>
    internal static string WhenUnticked()
        => Block.GpuBlockCapability.IsHardBlock(Block.GpuBlockCapability.UnavailableKey()) ? Fallback() : "ebpf";

    private static bool TryBpf(IHardwareControl? wmi, Func<bool> bpfAvailable)
    {
        // Firmware Eco or Ultimate set by another OS needs the MUX backend to undo.
        if (wmi == null || wmi.GetGpuEco() || wmi.GetGpuMuxMode() == 0)
            return false;
        // Some iGPU-only firmwares expose dgpu_disable; the filter needs a real dGPU.
        if (DgpuPci.Find() == null)
            return false;
        if (GpuBootFiles.ReadBackend() is not ("" or "ebpf") || !bpfAvailable())
            return false;
        if (!Select("ebpf"))
            return false;
        Logger.WriteLine("GpuBackendChoice: new install - gpu_backend=ebpf");
        return true;
    }
}

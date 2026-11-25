using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;

namespace GHelper.Linux.Gpu;

/// <summary>How ghelper switches the dGPU off. Config and marker store it as "asus-wmi", "pci" or "ebpf".</summary>
public enum GpuBackendKind { AsusWmi, Pci, Ebpf }

/// <summary>
/// Which dGPU backend is in use; the only writer of the gpu_backend config. The boot
/// service follows /etc/ghelper/backend: a user pick writes that marker first, while
/// the automatic defaults (first-start PCI, leaving eBPF) only follow it.
/// </summary>
internal static class GpuBackendChoice
{
    // Set while the app picked the backend, not the user: eBPF replaces it once it can run.
    private const string AutoKey = "gpu_backend_auto";

    /// <summary>Config and marker text of a backend.</summary>
    internal static string Name(GpuBackendKind backend) => backend switch
    {
        GpuBackendKind.Pci => "pci",
        GpuBackendKind.Ebpf => "ebpf",
        _ => "asus-wmi",
    };

    /// <summary>Backend from config or marker text; null when empty or unknown.</summary>
    internal static GpuBackendKind? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "asus-wmi" => GpuBackendKind.AsusWmi,
        "pci" => GpuBackendKind.Pci,
        "ebpf" => GpuBackendKind.Ebpf,
        _ => null,
    };

    /// <summary>Startup: eBPF while the app owns the choice and it can run, else PCI without ASUS firmware. A user pick is kept.</summary>
    internal static void ResolveAtStartup(IHardwareControl? wmi, Func<bool> bpfAvailable)
    {
        bool unset = string.IsNullOrWhiteSpace(AppConfig.GetString("gpu_backend"));
        if (AppConfig.IsNewInstall && unset)
            AppConfig.Set(AutoKey, 1);
        if (AppConfig.Is(AutoKey) && TryBpf(wmi, bpfAvailable))
            return;
        if (unset && !AppConfig.HasAsusPlatformDevice())
        {
            AppConfig.Set("gpu_backend", Name(GpuBackendKind.Pci));
            Logger.WriteLine("GpuBackendChoice: no ASUS platform device - gpu_backend=pci");
        }
    }

    /// <summary>Same start, after the system files prompt: try eBPF again while the app owns the choice.</summary>
    internal static bool RetryAutoDefault(IHardwareControl? wmi, Func<bool> bpfAvailable, Action waitForSystemFiles)
    {
        if (!AppConfig.Is(AutoKey))
            return false;
        waitForSystemFiles();
        return TryBpf(wmi, bpfAvailable);
    }

    /// <summary>Switch the backend: marker first, then config. False leaves both as they were.</summary>
    internal static bool Select(GpuBackendKind backend)
    {
        // The MUX latch is real firmware state, so a backend change keeps it.
        if (!GpuBootFiles.SetBackend(backend))
        {
            Logger.WriteLine($"GpuBackendChoice: marker not saved, staying on {Name(AppConfig.GetGpuBackend())}");
            return false;
        }
        AppConfig.Set("gpu_backend", Name(backend));
        AppConfig.Set(AutoKey, 0);
        AppConfig.Flush();
        Logger.WriteLine($"GpuBackendChoice: gpu_backend={Name(backend)}");
        return true;
    }

    /// <summary>eBPF cannot stay: the config takes the marker's other backend, else the fallback. Returns it.</summary>
    internal static GpuBackendKind LeaveBpf()
    {
        GpuBackendKind? marker = GpuBootFiles.ReadBackend();
        GpuBackendKind backend = marker is GpuBackendKind.AsusWmi or GpuBackendKind.Pci ? marker.Value : Fallback();
        AppConfig.Set("gpu_backend", Name(backend));
        Logger.WriteLine($"GpuBackendChoice: leaving ebpf - gpu_backend={Name(backend)}");
        return backend;
    }

    /// <summary>No backend box ticked means eBPF, unless this kernel or build can never run it.</summary>
    internal static GpuBackendKind WhenUnticked()
        => Block.GpuBlockCapability.IsHardBlock(Block.GpuBlockCapability.UnavailableKey()) ? Fallback() : GpuBackendKind.Ebpf;

    // Backend when eBPF cannot run: MUX mode on ASUS, PCI elsewhere.
    private static GpuBackendKind Fallback() => AppConfig.HasAsusPlatformDevice() ? GpuBackendKind.AsusWmi : GpuBackendKind.Pci;

    private static bool TryBpf(IHardwareControl? wmi, Func<bool> bpfAvailable)
    {
        // Firmware Eco or Ultimate set by another OS needs the MUX backend to undo.
        if (wmi == null || wmi.GetGpuEco() || wmi.GetGpuMuxMode() == 0)
            return false;
        // Switch only from Standard: Eco and Optimized state belongs to the current backend.
        if (AppConfig.GetString("gpu_mode") is not (null or "" or "standard") || AppConfig.Is("gpu_auto"))
            return false;
        // Some iGPU-only firmwares expose dgpu_disable; the filter needs a real dGPU.
        if (DgpuPci.Find() == null)
            return false;
        // A first start keeps a marker another install left; later the marker is our own pick's.
        GpuBackendKind? marker = GpuBootFiles.ReadBackend();
        bool markerOk = marker is null or GpuBackendKind.Ebpf
            || (!AppConfig.IsNewInstall && marker == AppConfig.GetGpuBackend());
        if (!markerOk || !bpfAvailable())
            return false;
        if (!Select(GpuBackendKind.Ebpf))
            return false;
        Logger.WriteLine("GpuBackendChoice: automatic default - gpu_backend=ebpf");
        return true;
    }
}

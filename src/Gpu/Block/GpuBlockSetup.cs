using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.Block;

/// <summary>Updates rows for the eBPF block setup: bpf in the kernel LSM list and libbpf.</summary>
public static class GpuBlockSetup
{
    /// <summary>Written by lsm-enable; lsm-disable reverts only what it records.</summary>
    public static readonly string LsmStatePath =
        GPUModeControl.TestPathPrefix + "/etc/ghelper/lsm-enable.state";

    public enum RowState { Ok, Missing, NotApplicable, Unknown }

    /// <summary>bpf added by G-Helper and waiting for a reboot to become active.</summary>
    public static bool LsmSetForNextBoot()
        => File.Exists(LsmStatePath) && !GpuBlockCapability.IsLsmBpfActive();

    /// <summary>Kernel row. With the backend off, only a change G-Helper made is listed.</summary>
    public static RowState KernelRowState(bool selected, bool active, bool setByUs, Func<bool> kernelCan)
    {
        if (!selected)
            return setByUs ? RowState.Ok : RowState.NotApplicable;
        if (active || setByUs)
            return RowState.Ok;
        return kernelCan() ? RowState.Missing : RowState.NotApplicable;
    }

    /// <summary>"eBPF: libbpf library" row. loads is null when gpu-helper could not be asked.</summary>
    public static RowState LibbpfRowState(bool selected, bool? loads)
        => !selected ? RowState.NotApplicable
            : loads switch { true => RowState.Ok, false => RowState.Missing, null => RowState.Unknown };

    /// <summary>Command that installs libbpf with this system's package manager, or null when none is known.</summary>
    public static string? LibbpfInstallCommand(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (exists("/run/ostree-booted"))
            return "sudo rpm-ostree install libbpf";
        if (exists("/usr/bin/apt"))
            return "sudo apt install libbpf1";
        if (exists("/usr/bin/dnf"))
            return "sudo dnf install libbpf";
        if (exists("/usr/bin/pacman"))
            return "sudo pacman -S libbpf";
        if (exists("/usr/bin/zypper"))
            return "sudo zypper install libbpf1";
        return null;
    }

    /// <summary>Kernel parameter to add by hand.</summary>
    public static string ManualParameter()
    {
        string? lsm = SysfsHelper.ReadAttributeSilent("/sys/kernel/security/lsm");
        if (string.IsNullOrWhiteSpace(lsm))
            return "lsm=landlock,lockdown,yama,integrity,apparmor,bpf";
        return $"lsm={lsm.Trim()},bpf";
    }
}

using System;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.Block;

/// <summary>What the eBPF GPU block needs from the system, and what is missing.</summary>
public static class GpuBlockCapability
{
    /// <summary>Parsed output of "gpu-helper bpf-caps".</summary>
    public sealed class Caps
    {
        public bool LsmActive;
        public string KernelConfig = "unknown";
        public bool Bpffs;
        public bool Libbpf;
        public bool Filter;
        public bool Btf;
        public string Lockdown = "none";
        public bool Attached;
        public bool HelperRan;
    }

    /// <summary>Parse the "bpf-caps" key=value output.</summary>
    public static Caps ParseCaps(string? stdout)
    {
        var caps = new Caps();
        if (string.IsNullOrWhiteSpace(stdout))
            return caps;

        caps.HelperRan = true;
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = raw.Substring(0, eq).Trim();
            string val = raw.Substring(eq + 1).Trim();
            switch (key)
            {
                case "lsm_active":
                    caps.LsmActive = val == "1";
                    break;
                case "kernel_config":
                    caps.KernelConfig = val;
                    break;
                case "bpffs":
                    caps.Bpffs = val == "1";
                    break;
                case "libbpf":
                    caps.Libbpf = val == "1";
                    break;
                case "filter":
                    caps.Filter = val == "1";
                    break;
                case "btf":
                    caps.Btf = val == "1";
                    break;
                case "lockdown":
                    caps.Lockdown = val;
                    break;
                case "attached":
                    caps.Attached = val == "1";
                    break;
            }
        }
        return caps;
    }

    /// <summary>Read the active LSM list; securityfs needs no privilege.</summary>
    public static bool IsLsmBpfActive()
    {
        string? lsm = SysfsHelper.ReadAttributeSilent("/sys/kernel/security/lsm");
        if (string.IsNullOrEmpty(lsm))
            return false;
        foreach (var token in lsm.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (token.Trim() == "bpf")
                return true;
        return false;
    }

    /// <summary>Query the helper for the full capability set.</summary>
    public static Caps Query()
    {
        var (stdout, _, _) = SysfsHelper.RunSudoOrPkexecEx(
            SysfsHelper.GpuHelperPath, new[] { "bpf-caps" }, allowPkexec: false);
        return ParseCaps(stdout);
    }

    /// <summary>True when the block can be turned on now.</summary>
    public static bool IsAvailable()
    {
        // Cheap unprivileged check first.
        if (!IsLsmBpfActive())
            return false;
        var caps = Query();
        return caps.HelperRan && caps.LsmActive && caps.Bpffs && caps.Libbpf && caps.Filter;
    }

    /// <summary>Label key of the reason the block is unavailable, or empty.</summary>
    public static string UnavailableKey()
    {
        if (!IsLsmBpfActive())
        {
            if (GpuBlockSetup.LsmSetForNextBoot())
                return "gpu_block_unavail_reboot";
            // Only a missing boot parameter is fixable.
            var probe = Query();
            return probe.KernelConfig == "n" ? "gpu_block_unavail_kernel" : "gpu_block_unavail_lsm";
        }

        var caps = Query();
        if (!caps.HelperRan)
            return "gpu_block_unavail_helper";
        if (!caps.Filter)
            return "gpu_block_unavail_filter";
        if (!caps.Libbpf)
            return "gpu_block_unavail_libbpf";
        if (caps.Lockdown == "confidentiality")
            return "gpu_block_unavail_lockdown";
        return "";
    }

    /// <summary>True for reasons no Updates row can fix: the kernel, the build or lockdown.</summary>
    public static bool IsHardBlock(string key)
        => key is "gpu_block_unavail_kernel" or "gpu_block_unavail_filter" or "gpu_block_unavail_lockdown";
}

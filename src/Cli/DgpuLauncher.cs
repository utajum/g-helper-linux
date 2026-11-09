using System.Collections;
using System.Runtime.InteropServices;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Cli;

/// <summary>"ghelper --dgpu [--only] command [args]": exec a program the eBPF block allows on the dGPU.</summary>
public static class DgpuLauncher
{
    public static int Run(string[] args)
    {
        bool only = args.Length > 0 && args[0] == "--only";
        string[] cmd = only ? args[1..] : args;
        if (cmd.Length == 0)
        {
            Console.Error.WriteLine("usage: ghelper --dgpu [--only] <command> [args...]");
            return 2;
        }

        string? bdf = Gpu.Block.DgpuPowerMonitor.DiscreteBdf();
        string vendor = bdf == null ? "" : SysfsHelper.ReadAttribute($"/sys/bus/pci/devices/{bdf}/vendor") ?? "";
        var env = BuildEnv(Environment.GetEnvironmentVariables(), vendor, only);

        var argv = new IntPtr[cmd.Length + 1];
        for (int i = 0; i < cmd.Length; i++)
            argv[i] = Marshal.StringToCoTaskMemUTF8(cmd[i]);
        var envp = new IntPtr[env.Count + 1];
        int n = 0;
        foreach (var kv in env)
            envp[n++] = Marshal.StringToCoTaskMemUTF8($"{kv.Key}={kv.Value}");

        execvpe(argv[0], argv, envp);
        int errno = Marshal.GetLastSystemError();
        Console.Error.WriteLine($"ghelper --dgpu: cannot run {cmd[0]}: {Marshal.GetPInvokeErrorMessage(errno)}");
        return 127;
    }

    /// <summary>The current environment plus the PRIME offload switches for the dGPU vendor and the block's marker.</summary>
    public static SortedDictionary<string, string> BuildEnv(IDictionary current, string vendor, bool only)
    {
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in current)
            env[(string)e.Key] = e.Value as string ?? "";
        if (vendor == "0x10de")
        {
            env["__NV_PRIME_RENDER_OFFLOAD"] = "1";
            env["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
            env["__VK_LAYER_NV_optimus"] = "NVIDIA_only";
        }
        else
        {
            env["DRI_PRIME"] = "1";
        }
        env[only ? "GHELPER_FORCE_DGPU" : "GHELPER_DGPU"] = "1";
        return env;
    }

    [DllImport("libc")]
    private static extern int execvpe(IntPtr file, IntPtr[] argv, IntPtr[] envp);
}

using System;
using System.Collections.Generic;
using System.IO;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.Block;

/// <summary>Apps that keep dGPU access while the block is on: process names or KEY=VALUE entries.</summary>
public static class GpuBlockPolicy
{
    /// <summary>Written by gpu-helper as root, world readable.</summary>
    public static readonly string PolicyPath =
        GPUModeControl.TestPathPrefix + "/etc/ghelper/gpu-allowed-comms";

    /// <summary>Matches USER_COMMS_MAX in bpf_ops.c.</summary>
    public const int MaxEntries = 50;

    /// <summary>TASK_COMM_LEN minus the terminator.</summary>
    public const int MaxNameLength = 15;

    /// <summary>GH_ENV_LEN in the filter minus the terminator.</summary>
    public const int MaxEnvLength = 47;

    /// <summary>Environment entries the filter allows at exec. Mirrors ALLOWED_ENVS and FORCED_ENVS.</summary>
    public static readonly string[] BuiltinEnvs =
    {
        "__NV_PRIME_RENDER_OFFLOAD=1", "DRI_PRIME=1", "GHELPER_DGPU=1", "CARDWIRE_ALLOW=1",
        "GHELPER_FORCE_DGPU=1", "CARDWIRE_FORCE_DGPU=1",
    };

    /// <summary>A Steam game is allowed by the id Steam puts in its environment.</summary>
    public static string SteamEntry(string appId) => "SteamAppId=" + appId;

    /// <summary>Label key when a name or KEY=VALUE entry is invalid, else null. Mirrors bpf_ops.c.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "gpu_apps_invalid";
        int eq = name.IndexOf('=');
        if (eq >= 0)
        {
            if (eq == 0 || eq == name.Length - 1 || name.Length > MaxEnvLength)
                return "gpu_apps_invalid";
            foreach (char c in name)
                if (!char.IsAsciiLetterOrDigit(c) && "_=.:-".IndexOf(c) < 0)
                    return "gpu_apps_invalid";
            return null;
        }
        if (name.Length > MaxNameLength)
            return "gpu_apps_too_long";
        foreach (char c in name)
        {
            if (c <= 0x20 || c == 0x7f || c == '/' || c == '\\')
                return "gpu_apps_invalid";
        }
        return null;
    }

    /// <summary>Built-in entries plus the card's DRI_PRIME forms, as populate_envs() writes them.</summary>
    public static List<string> EnvEntries(string? bdf)
    {
        var list = new List<string>(BuiltinEnvs);
        if (bdf == null)
            return list;
        list.Add("DRI_PRIME=pci-" + bdf.Replace(':', '_').Replace('.', '_'));
        string vendor = SysfsHelper.ReadAttribute($"/sys/bus/pci/devices/{bdf}/vendor") ?? "";
        string device = SysfsHelper.ReadAttribute($"/sys/bus/pci/devices/{bdf}/device") ?? "";
        if (vendor.Length == 6 && device.Length == 6)
            list.Add($"DRI_PRIME={vendor[2..]}:{device[2..]}");
        return list;
    }

    /// <summary>User entries, in file order. Built-ins are not included.</summary>
    public static IReadOnlyList<string> List()
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(PolicyPath))
                return list;
            foreach (var raw in File.ReadAllLines(PolicyPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (Validate(line) != null || list.Contains(line))
                    continue;
                list.Add(line);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuBlockPolicy: read failed: {ex.Message}");
        }
        return list;
    }

    /// <summary>True when the process or an ancestor matches by name or environment, the filter's own rule.</summary>
    public static bool IsAllowedTree(int pid, IReadOnlyList<string> allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        for (int depth = 0; pid > 1 && depth < 64; depth++)
        {
            string stat;
            try
            { stat = File.ReadAllText($"/proc/{pid}/stat"); }
            catch
            { return false; }
            int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
            if (open < 0 || close < open)
                return false;
            if (set.Contains(stat.Substring(open + 1, close - open - 1)) || HasEnvEntry(pid, set))
                return true;
            var rest = stat.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length < 2 || !int.TryParse(rest[1], out pid))
                return false;
        }
        return false;
    }

    private static bool HasEnvEntry(int pid, HashSet<string> set)
    {
        try
        {
            foreach (var entry in File.ReadAllText($"/proc/{pid}/environ").Split('\0'))
                if (set.Contains(entry))
                    return true;
        }
        catch { }
        return false;
    }

    public static bool Allow(string name) => Set(name, true);

    public static bool Deny(string name) => Set(name, false);

    private static bool Set(string name, bool allow)
    {
        if (Validate(name) != null)
            return false;

        if (GPUModeControl.IsTestMode)
            return SetInTestMode(name, allow);

        string verb = allow ? "allow-comm" : "deny-comm";
        var (_, stderr, exit) = SysfsHelper.RunSudoOrPkexecRaw(
            SysfsHelper.GpuHelperPath, new[] { "bpf-block", verb, name },
            allowPkexec: true);
        if (exit != 0)
        {
            Logger.WriteLine($"GpuBlockPolicy: {verb} {name} failed (exit={exit}) {stderr.Trim()}");
            return false;
        }
        Logger.WriteLine($"GpuBlockPolicy: {verb} {name}");
        return true;
    }

    /// <summary>Same file effect the helper would have, without privileges.</summary>
    private static bool SetInTestMode(string name, bool allow)
    {
        try
        {
            var list = new List<string>(List());
            if (allow)
            {
                if (list.Contains(name))
                    return true;
                if (list.Count >= MaxEntries)
                    return false;
                list.Add(name);
            }
            else if (!list.Remove(name))
            {
                return true;
            }

            string? dir = Path.GetDirectoryName(PolicyPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllLines(PolicyPath, list);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuBlockPolicy: test-mode write failed: {ex.Message}");
            return false;
        }
    }
}

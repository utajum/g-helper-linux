using System;
using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.Block;

/// <summary>Turns the eBPF GPU block on and off: the dGPU nodes return ENOENT and runtime PM powers it down.</summary>
public static class GpuBlockControl
{
    /// <summary>Helper exit code meaning the feature is not usable here.</summary>
    private const int ExitUnavailable = 4;

    // Status needs a privileged call; cache it for the 2 s UI tick.
    private static Status? _cachedStatus;
    private static DateTime _cachedStatusUtc;
    private static readonly TimeSpan StatusCacheFor = TimeSpan.FromSeconds(3);

    /// <summary>Test seam: models the filter in memory.</summary>
    internal interface IBlockOps
    {
        bool Active { get; }
        bool Enable(bool interactive);
        bool Disable(bool interactive);
        bool Refresh();
        List<NvidiaHolder> Holders();
    }

    internal static IBlockOps? TestOps { get; set; }

    /// <summary>True when the filter is attached and blocking right now.</summary>
    public static bool IsActive()
    {
        if (TestOps != null)
            return TestOps.Active;
        var status = QueryStatus();
        return status.Attached && status.Mode == 1;
    }

    /// <summary>Drop the cached status so the next query re-reads the filter.</summary>
    public static void InvalidateCache() => _cachedStatus = null;

    /// <summary>Unprivileged probe: the helper keeps a /run state file while blocking.</summary>
    public static bool IsActiveFast()
    {
        if (TestOps != null)
            return TestOps.Active;
        string prefix = Environment.GetEnvironmentVariable("GHELPER_TEST_ROOT") ?? "";
        return File.Exists(prefix + "/run/ghelper/bpf-block-pm.state");
    }

    public sealed record Status(bool Attached, int Mode, int BlockedFiles);

    /// <summary>Parse "bpf-block status" output. Testable without the helper.</summary>
    public static Status ParseStatus(string? stdout)
    {
        bool attached = false;
        int mode = 0, blocked = 0;
        foreach (var token in (stdout ?? "").Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = token.Substring(0, eq);
            string val = token.Substring(eq + 1);
            switch (key)
            {
                case "attached":
                    attached = val == "1";
                    break;
                case "mode":
                    int.TryParse(val, out mode);
                    break;
                case "blocked":
                    int.TryParse(val, out blocked);
                    break;
            }
        }
        return new Status(attached, mode, blocked);
    }

    public static Status QueryStatus()
    {
        if (_cachedStatus != null && DateTime.UtcNow - _cachedStatusUtc < StatusCacheFor)
            return _cachedStatus;

        var (stdout, _, _) = SysfsHelper.RunSudoOrPkexecEx(
            SysfsHelper.GpuHelperPath, new[] { "bpf-block", "status" }, allowPkexec: false);
        _cachedStatus = ParseStatus(stdout);
        _cachedStatusUtc = DateTime.UtcNow;
        return _cachedStatus;
    }

    /// <summary>Hide the dGPU. False when the filter could not be attached; nothing changed.</summary>
    public static bool Enable(bool interactive = true)
    {
        if (TestOps != null)
            return TestOps.Enable(interactive);

        string? bdf = DgpuPowerMonitor.DiscreteBdf(rescan: true);
        if (bdf == null)
        {
            Logger.WriteLine("GpuBlockControl: no discrete GPU found, nothing to block");
            return false;
        }

        // Stop our telemetry: it would reopen the GPU.
        GpuQueryGate.Hold("ebpf gpu block enabled");

        var (stdout, stderr, exit) = SysfsHelper.RunSudoOrPkexecRaw(
            SysfsHelper.GpuHelperPath, new[] { "bpf-block", "on", bdf },
            allowPkexec: interactive);

        if (exit != 0)
        {
            // Roll the gate back; the GPU is still visible.
            GpuQueryGate.Resume();
            InvalidateCache();
            string why = exit == ExitUnavailable ? "unavailable" : "failed";
            Logger.WriteLine($"GpuBlockControl: enable {why} (exit={exit}) {stderr.Trim()}");
            return false;
        }

        InvalidateCache();
        Logger.WriteLine($"GpuBlockControl: block enabled on {bdf} ({stdout.Trim()})");
        DgpuPowerMonitor.NoteBlockEnabled();
        return true;
    }

    /// <summary>Make the dGPU visible again. A failure leaves the filter attached.</summary>
    public static bool Disable(bool interactive = true)
    {
        if (TestOps != null)
            return TestOps.Disable(interactive);

        var (stdout, stderr, exit) = SysfsHelper.RunSudoOrPkexecRaw(
            SysfsHelper.GpuHelperPath, new[] { "bpf-block", "off" },
            allowPkexec: interactive);

        InvalidateCache();
        DgpuPowerMonitor.NoteBlockDisabled();
        GpuQueryGate.Resume();

        if (exit != 0)
        {
            Logger.WriteLine($"GpuBlockControl: disable failed (exit={exit}) {stderr.Trim()}");
            return false;
        }
        Logger.WriteLine($"GpuBlockControl: block disabled ({stdout.Trim()})");
        return true;
    }

    /// <summary>Apps not allowed by the list that opened the dGPU before the block.</summary>
    public static List<NvidiaHolder> HoldersToClose()
    {
        if (TestOps != null)
            return TestOps.Holders();
        var allowed = new List<string>(GpuBlockPolicy.List());
        allowed.AddRange(GpuBlockPolicy.EnvEntries(DgpuPowerMonitor.DiscreteBdf()));
        var list = new List<NvidiaHolder>();
        NvidiaProcessScanner.InvalidateScanCache();
        foreach (var h in NvidiaProcessScanner.ScanHolders())
            if (h.BlocksUnload && !GpuBlockPolicy.IsAllowedTree(h.Pid, allowed))
                list.Add(h);
        return list;
    }

    /// <summary>Re-resolve the blocked nodes after a driver reload made new inodes.</summary>
    public static bool Refresh()
    {
        if (TestOps != null)
            return TestOps.Refresh();
        string? bdf = DgpuPowerMonitor.DiscreteBdf(rescan: true);
        if (bdf == null)
            return false;

        var (_, stderr, exit) = SysfsHelper.RunSudoOrPkexecEx(
            SysfsHelper.GpuHelperPath, new[] { "bpf-block", "refresh", bdf }, allowPkexec: false);
        if (exit != 0)
        {
            Logger.WriteLine($"GpuBlockControl: refresh failed (exit={exit}) {stderr.Trim()}");
            return false;
        }
        Logger.WriteLine($"GpuBlockControl: refreshed blocked files for {bdf}");
        return true;
    }

    /// <summary>A process name the filter denied, and how long ago.</summary>
    public sealed record RefusedApp(string Comm, long AgeSeconds, int Count);

    /// <summary>Parse "bpf-block refused": "seconds count name" per line, newest first.</summary>
    public static List<RefusedApp> ParseRefused(string? stdout)
    {
        var list = new List<RefusedApp>();
        foreach (var line in (stdout ?? "").Split('\n'))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length == 3 && long.TryParse(parts[0], out long age)
                && int.TryParse(parts[1], out int count) && parts[2].Length > 0)
                list.Add(new RefusedApp(parts[2], age, count));
        }
        return list;
    }

    /// <summary>Processes denied since the block went on. Empty when it is off.</summary>
    public static List<RefusedApp> Refused()
    {
        if (TestOps != null || !IsActiveFast())
            return new List<RefusedApp>();
        var (stdout, _, _) = SysfsHelper.RunSudoOrPkexecEx(
            SysfsHelper.GpuHelperPath, new[] { "bpf-block", "refused" }, allowPkexec: false);
        return ParseRefused(stdout);
    }
}

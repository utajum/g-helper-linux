using System;
using System.Collections.Generic;
using System.IO;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Gpu.Block;

/// <summary>One process name holding GPU nodes; Pids 0 for an allow list entry not running.</summary>
public sealed record GpuAppUse(string Comm, int Pids, bool OnDgpu, bool OnIgpu, bool Allowed = false,
    bool Refused = false, string? Title = null);

/// <summary>Running apps per GPU, from their open device nodes (unprivileged).</summary>
public static class GpuProcessList
{
    /// <summary>Split DRM nodes into dGPU and iGPU sets; unknown counts as iGPU.</summary>
    public static (HashSet<string> Dgpu, HashSet<string> Igpu) SplitNodes(
        IEnumerable<(string Node, string Bdf)> nodes, string? dgpuBdf)
    {
        var dgpu = new HashSet<string>(StringComparer.Ordinal);
        var igpu = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, bdf) in nodes)
        {
            if (dgpuBdf != null && string.Equals(bdf, dgpuBdf, StringComparison.OrdinalIgnoreCase))
                dgpu.Add(node);
            else
                igpu.Add(node);
        }
        return (dgpu, igpu);
    }

    /// <summary>Classify one process from its open device paths; /dev/nvidia* is the dGPU.</summary>
    public static (bool OnDgpu, bool OnIgpu) Classify(
        IEnumerable<string> openPaths, ISet<string> dgpuNodes, ISet<string> igpuNodes)
    {
        bool d = false, i = false;
        foreach (var path in openPaths)
        {
            if (path.StartsWith("/dev/nvidia", StringComparison.Ordinal))
                d = true;
            else if (dgpuNodes.Contains(path))
                d = true;
            else if (igpuNodes.Contains(path))
                i = true;
        }
        return (d, i);
    }

    /// <summary>DRM device nodes with the PCI address each one belongs to.</summary>
    private static List<(string Node, string Bdf)> ReadDrmNodes()
    {
        var result = new List<(string, string)>();
        string drmRoot = GPUModeControl.TestPathPrefix + "/sys/class/drm";
        try
        {
            if (!Directory.Exists(drmRoot))
                return result;
            foreach (var entry in Directory.EnumerateDirectories(drmRoot))
            {
                string name = Path.GetFileName(entry);
                // card1-eDP-1 style connector entries have no device node.
                if (name.Contains('-'))
                    continue;
                if (!name.StartsWith("card", StringComparison.Ordinal)
                    && !name.StartsWith("renderD", StringComparison.Ordinal))
                    continue;
                string devLink = Path.Combine(entry, "device");
                string? target;
                try
                { target = File.ResolveLinkTarget(devLink, true)?.FullName; }
                catch
                { continue; }
                if (target == null)
                    continue;
                result.Add(($"/dev/dri/{name}", Path.GetFileName(target)));
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuProcessList: drm scan failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>Merge a scan with the allow list into window rows, refused and dGPU apps first.</summary>
    public static List<GpuAppUse> MergeWithPolicy(
        IEnumerable<GpuAppUse> running, IEnumerable<string> allowed,
        IEnumerable<string>? refused = null, IReadOnlyDictionary<string, string>? titles = null)
    {
        var allowSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var refusedSet = new HashSet<string>(refused ?? Array.Empty<string>(), StringComparer.Ordinal);
        var rows = new List<GpuAppUse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var app in running)
        {
            rows.Add(app with { Allowed = allowSet.Contains(app.Comm), Refused = refusedSet.Contains(app.Comm) });
            seen.Add(app.Comm);
        }

        foreach (var name in allowSet)
        {
            if (seen.Add(name))
                rows.Add(new GpuAppUse(name, 0, false, false, true, Title: titles?.GetValueOrDefault(name)));
        }

        foreach (var name in refusedSet)
        {
            if (seen.Add(name))
                rows.Add(new GpuAppUse(name, 0, false, false, Refused: true));
        }

        rows.Sort((a, b) =>
        {
            bool af = a.Refused && !a.Allowed, bf = b.Refused && !b.Allowed;
            if (af != bf)
                return af ? -1 : 1;
            bool ar = a.Pids > 0, br = b.Pids > 0;
            if (ar != br)
                return ar ? -1 : 1;
            if (a.OnDgpu != b.OnDgpu)
                return a.OnDgpu ? -1 : 1;
            return string.Compare(a.Comm, b.Comm, StringComparison.Ordinal);
        });

        return rows;
    }

    /// <summary>Scan the user processes for open GPU nodes, grouped by process name.</summary>
    public static IReadOnlyList<GpuAppUse> Scan()
    {
        var byComm = new SortedDictionary<string, (int Pids, bool D, bool I)>(StringComparer.Ordinal);
        try
        {
            var (dgpu, igpu) = SplitNodes(ReadDrmNodes(), DgpuPowerMonitor.DiscreteBdf());
            int self = Environment.ProcessId;

            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.Length == 0 || !char.IsDigit(leaf[0]) || !int.TryParse(leaf, out int pid) || pid == self)
                    continue;

                var open = new List<string>();
                try
                {
                    foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                    {
                        string? target;
                        try
                        { target = File.ResolveLinkTarget(fd, false)?.FullName; }
                        catch
                        { continue; }
                        if (target != null && target.StartsWith("/dev/", StringComparison.Ordinal))
                            open.Add(target);
                    }
                }
                catch
                { continue; /* exited or not ours */ }

                var (d, i) = Classify(open, dgpu, igpu);
                if (!d && !i)
                    continue;

                string comm;
                try
                { comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim(); }
                catch
                { continue; }
                if (comm.Length == 0 || comm == "ghelper")
                    continue;

                byComm.TryGetValue(comm, out var agg);
                byComm[comm] = (agg.Pids + 1, agg.D || d, agg.I || i);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuProcessList: scan failed: {ex.Message}");
        }

        var list = new List<GpuAppUse>(byComm.Count);
        foreach (var (comm, agg) in byComm)
            list.Add(new GpuAppUse(comm, agg.Pids, agg.D, agg.I));
        return list;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.Install;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.NVidia;

/// <summary>
/// Sync state of the on-disk gpu-helper vs the copy embedded in this ghelper
/// binary. Drives the startup self-update prompt.
/// </summary>
public enum HelperState { InSync, Stale, Missing }

/// <summary>
/// One process detected as a NVIDIA holder. Categories:
///   FdCount    /dev/nvidia* file descriptors - hard rmmod blocker.
///   DriFdCount /dev/dri/card or renderD FDs on the nvidia DRM device -
///              pins nvidia_drm refcount (invisible to /dev/nvidia* scans).
///   I2cFdCount /dev/i2c-N FDs on nvidia I2C adapters (DDC/CI) - pins the
///              nvidia module refcount (powerdevil, OpenRGB are common holders).
///   LibsMapped libnvidia-*/libcuda in /proc/pid/maps but no device FDs.
///              Doesn't block unload, but the process will malfunction.
/// </summary>
public sealed record NvidiaHolder(
    int Pid, string Comm, string User,
    int FdCount, int LibsMapped, bool IsOwnedByCurrentUser,
    string ServiceUnit = "", int DriFdCount = 0, int I2cFdCount = 0)
{
    public bool BlocksUnload => FdCount > 0 || DriFdCount > 0 || I2cFdCount > 0;
}

public static class NvidiaProcessScanner
{
    [DllImport("libc")]
    private static extern uint getuid();

    private static readonly Lazy<uint> _currentUid = new(() =>
    {
        try
        { return getuid(); }
        catch { return uint.MaxValue; }
    });

    private static readonly int _selfPid = Environment.ProcessId;

    private static string HelperPath => SysfsHelper.GpuHelperPath;
    private static volatile bool _helperChecked;
    private static readonly object _helperLock = new();

    private static readonly object _privCacheLock = new();
    private static DateTime _privCacheTime = DateTime.MinValue;
    private static List<NvidiaHolder>? _privCacheResults;
    private static List<string> _filteredSystemCache = new();
    private const int PrivilegedCacheSeconds = 5;

    /// <summary>
    /// Formatted strings for system processes filtered from the last scan.
    /// Same PID:comm(user)/Nfds format as the holder log lines.
    /// </summary>
    public static IReadOnlyList<string> GetFilteredSystemProcesses()
    {
        lock (_privCacheLock)
            return _filteredSystemCache;
    }

    private static readonly string[] SystemCommPrefixes = new[]
    {
        // NVIDIA daemons
        "nvidia-persiste", "nvidia-powerd", "nvidia-bug-repor",

        // Display servers + compositors
        "Xwayland", "Xorg", "kwin", "Hyprland", "sway", "river", "weston",

        // Display managers
        "sddm", "gdm", "lightdm",

        // KDE Plasma
        "plasmashell", "kded", "kdeconnect", "ksmserver", "ksecretd",
        "kaccess", "kwalletd", "ksystemstats", "kglobalaccel", "polkit-kde",
        "DiscoverNotifie", "krunner", "kactivitymanage", "kioslave", "kioworker",
        "baloo_", "akonadi", "dolphin", "drkonqi",

        // GNOME
        "gnome-shell", "mutter", "gnome-session", "gnome-keyring", "gsd-",
        "gjs", "gnome-terminal", "nautilus", "evolution-", "tracker-miner",
        "gnome-control", "gnome-disk",

        // XFCE
        "xfwm4", "xfce4-", "xfsettingsd", "xfdesktop",

        // LXQt
        "lxqt-",

        // Cinnamon
        "cinnamon", "csd-", "nemo",

        // MATE
        "mate-", "marco", "caja",

        // XDG portals
        "xdg-desktop-po", "xdg-document-p",

        // Audio / video pipeline (system-level)
        "pipewire", "wireplumber", "pulseaudio",

        // Session services
        "polkit", "dconf-service", "gvfs", "geoclue",
        "dbus-daemon", "dbus-broker", "at-spi-bus-lau", "at-spi2-registr",
    };

    private static bool IsSystemProcess(string comm)
    {
        foreach (var prefix in SystemCommPrefixes)
            if (comm.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>PID:comm(user)/Nfds+Ndri+Ni2c -- same format as LogHoldersSnapshot.</summary>
    private static string FormatHolderBrief(int pid, string comm, string user, int fds, int dri, int i2c)
    {
        string s = $"{pid}:{comm}({user})/{fds}fds";
        if (dri > 0)
            s += $"+{dri}dri";
        if (i2c > 0)
            s += $"+{i2c}i2c";
        return s;
    }

    public static IReadOnlyList<NvidiaHolder> ScanHolders()
    {
        var privileged = TryPrivilegedScanCached();
        if (privileged != null)
            return privileged;
        return UnprivilegedFdScan();
    }

    public static void InvalidateScanCache()
    {
        lock (_privCacheLock)
        {
            _privCacheTime = DateTime.MinValue;
            _privCacheResults = null;
        }
        // The nvidia DRM card / I2C adapter numbers are dynamically allocated
        // and shift across driver unload/reload cycles (Eco<->Standard) - the
        // aux device sets must be re-resolved with the scan cache.
        _auxDevicesResolved = false;
    }

    private static List<NvidiaHolder> UnprivilegedFdScan()
    {
        var holders = new Dictionary<int, NvidiaHolder>();
        var sysFiltered = new List<string>();

        if (!Directory.Exists("/proc"))
            return new List<NvidiaHolder>();

        IEnumerable<string> procDirs;
        try
        { procDirs = Directory.EnumerateDirectories("/proc"); }
        catch { return new List<NvidiaHolder>(); }

        foreach (var procDir in procDirs)
        {
            var name = Path.GetFileName(procDir);
            if (!int.TryParse(name, out int pid))
                continue;
            if (pid == _selfPid)
                continue;

            var fdDir = Path.Combine(procDir, "fd");
            if (!Directory.Exists(fdDir))
                continue;

            var c = CountAllGpuFds(fdDir, out _);
            var (libsMapped, devMapped) = ScanMaps(pid);
            int nvFds = c.Nvidia;
            if (nvFds == 0 && devMapped > 0)
                nvFds = 1;
            if (nvFds > 0 || libsMapped > 0 || c.Dri > 0 || c.I2c > 0)
            {
                string comm = ReadComm(pid);
                if (IsSystemProcess(comm))
                {
                    uint sysUid = ReadUid(pid);
                    sysFiltered.Add(FormatHolderBrief(pid, comm, ResolveUserName(sysUid), nvFds, c.Dri, c.I2c));
                    continue;
                }
                uint procUid = ReadUid(pid);
                string user = ResolveUserName(procUid);
                bool owned = procUid == _currentUid.Value;
                string serviceUnit = ResolveServiceUnit(pid);
                holders[pid] = new NvidiaHolder(pid, comm, user, nvFds, libsMapped, owned, serviceUnit, c.Dri, c.I2c);
            }
        }

        // Unprivileged path has no separate cache; store filtered list so
        // LogHoldersSnapshot can pick it up via GetFilteredSystemProcesses().
        lock (_privCacheLock)
            _filteredSystemCache = sysFiltered;
        if (sysFiltered.Count > 0)
            Helpers.Logger.WriteLine($"NvidiaProcessScanner: system holders (won't kill): [{string.Join(", ", sysFiltered)}]");

        return new List<NvidiaHolder>(holders.Values);
    }

    public static int CountFdHolders()
    {
        var privileged = TryPrivilegedScanCached();
        var holders = privileged ?? UnprivilegedFdScan();
        int n = 0;
        foreach (var h in holders)
            if (h.BlocksUnload)
                n++;
        return n;
    }

    public static int CountHolders()
    {
        var privileged = TryPrivilegedScanCached();
        if (privileged != null)
            return privileged.Count;
        return UnprivilegedFdScan().Count;
    }

    /// One pass over /proc/pid/maps: (libsMapped, devMapped). devMapped means
    /// a /dev/nvidia* mapping - pins the driver like an open fd. Unreadable
    /// maps (other users' processes when unprivileged) return (0, 0).
    private static (int libsMapped, int devMapped) ScanMaps(int pid)
    {
        try
        {
            int libs = 0, dev = 0;
            foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
            {
                if (dev == 0 && line.Contains("/dev/nvidia", StringComparison.Ordinal))
                    dev = 1;
                if (libs == 0 && (line.Contains("/libnvidia-", StringComparison.Ordinal)
                    || line.Contains("/libcuda.so", StringComparison.Ordinal)
                    || line.Contains("/libnvcuvid.so", StringComparison.Ordinal)))
                    libs = 1;
                if (dev == 1 && libs == 1)
                    break;
            }
            return (libs, dev);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static List<NvidiaHolder>? TryPrivilegedScanCached()
    {
        lock (_privCacheLock)
        {
            if (_privCacheResults != null
                && (DateTime.UtcNow - _privCacheTime).TotalSeconds < PrivilegedCacheSeconds)
                return _privCacheResults;
        }

        if (!EnsureHelper())
            return null;

        var sudoArgs = new[] { "-n", HelperPath, "list", _selfPid.ToString() };
        var output = SysfsHelper.RunCommandWithTimeout(SysfsHelper.SudoPath, sudoArgs, 3000);
        if (output == null)
            return null; // sudoers rejected or helper failed - fall through to unprivileged scan

        var holders = new Dictionary<int, NvidiaHolder>();
        var sysFiltered = new List<string>();
        int rawCount = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;
            if (!int.TryParse(parts[0], out int pid))
                continue;
            if (!int.TryParse(parts[1], out int fdCount))
                continue;
            if (!uint.TryParse(parts[2], out uint procUid))
                continue;
            string comm = parts[3];
            int libsMapped = (parts.Length >= 5 && int.TryParse(parts[4], out int lm)) ? lm : 0;
            string serviceUnit = (parts.Length >= 6 && parts[5] != "-") ? parts[5] : "";
            int driFds = (parts.Length >= 7 && int.TryParse(parts[6], out int df)) ? df : 0;
            int i2cFds = (parts.Length >= 8 && int.TryParse(parts[7], out int ic)) ? ic : 0;

            if (fdCount == 0 && libsMapped == 0 && driFds == 0 && i2cFds == 0)
                continue;
            rawCount++;

            // System processes (NVIDIA daemons, DE shells, portals etc.) are
            // never killed but logged so they show up in diagnostics.
            if (IsSystemProcess(comm))
            {
                sysFiltered.Add(FormatHolderBrief(pid, comm, ResolveUserName(procUid), fdCount, driFds, i2cFds));
                continue;
            }

            string user = ResolveUserName(procUid);
            bool owned = procUid == _currentUid.Value;
            holders[pid] = new NvidiaHolder(pid, comm, user, fdCount, libsMapped, owned, serviceUnit, driFds, i2cFds);
        }
        if (rawCount > 0)
            Helpers.Logger.WriteLine($"NvidiaProcessScanner: {rawCount} raw, {sysFiltered.Count} filtered system, {holders.Count} shown");
        if (sysFiltered.Count > 0)
            Helpers.Logger.WriteLine($"NvidiaProcessScanner: system holders (won't kill): [{string.Join(", ", sysFiltered)}]");

        var result = new List<NvidiaHolder>(holders.Values);
        lock (_privCacheLock)
        {
            _privCacheResults = result;
            _filteredSystemCache = sysFiltered;
            _privCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    public static bool EnsureHelper()
    {
        // Cheap re-check on every call: a later install (via the startup
        // system-files prompt or its Install/Repair button) is picked up at once.
        if (File.Exists(HelperPath))
            return true;
        if (_helperChecked)
            return false; // self-install already attempted/decided this process

        // Don't race the startup system-files prompt's pkexec: wait for the user's
        // decision first. No-op on the UI thread and once the decision is made, so
        // this never blocks the thread showing the (modal) prompt. Done OUTSIDE
        // the lock below so a long wait can't block UI-thread callers on the lock.
        Installer.WaitForStartupDecision();

        // The prompt may have just installed the helper.
        if (File.Exists(HelperPath))
            return true;

        // When the startup prompt criteria were met, that prompt owns the
        // gpu-helper install. It is still absent here => either it is mid-flight
        // or the user declined; in both cases we must NOT fire a second, competing
        // pkexec (the user's rule: the same check that shows the window cancels
        // this self-install).
        if (Installer.StartupWillPrompt)
        {
            _helperChecked = true;
            Helpers.Logger.WriteLine(
                "NvidiaProcessScanner: startup system-files prompt handles gpu-helper - skipping self-install pkexec");
            return false;
        }

        // No prompt in play (check disabled, or no other problems): self-install,
        // serialized so concurrent callers can't fire two pkexec dialogs.
        lock (_helperLock)
        {
            if (File.Exists(HelperPath))
                return true;
            if (_helperChecked)
                return false;
            _helperChecked = true;
            Helpers.Logger.WriteLine(
                $"NvidiaProcessScanner: {HelperPath} not found - attempting pkexec self-install");
            return RunPkexecInstall();
        }
    }

    public static HelperState CheckHelper()
    {
        try
        {
            if (!File.Exists(HelperPath))
                return HelperState.Missing;

            using var res = typeof(NvidiaProcessScanner).Assembly
                .GetManifestResourceStream("gpu-helper");
            if (res == null)
                return HelperState.InSync; // no embedded copy to compare against

            using var sha = SHA256.Create();
            byte[] embedded = sha.ComputeHash(res);
            byte[] installed;
            using (var fs = File.OpenRead(HelperPath))
                installed = sha.ComputeHash(fs);

            return embedded.AsSpan().SequenceEqual(installed)
                ? HelperState.InSync
                : HelperState.Stale;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"NvidiaProcessScanner: CheckHelper failed: {ex.Message}");
            return HelperState.InSync;
        }
    }

    public static bool RunPkexecInstall()
    {
        // Under an AppImage, ProcessPath is inside the per-user FUSE mount which
        // root cannot read; resolve a root-runnable copy (deleted on dispose,
        // after pkexec returns).
        using var self = LinuxSystemIntegration.ResolvePrivilegedSelf();
        if (string.IsNullOrEmpty(self.Path) || !File.Exists(self.Path))
        {
            Helpers.Logger.WriteLine(
                "NvidiaProcessScanner: cannot self-install scan helper - executable path unknown");
            return false;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pkexec",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(self.Path);
            psi.ArgumentList.Add("--install-gpu-helper");
            psi.ArgumentList.Add(Path.GetDirectoryName(HelperPath)!);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return false;

            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            var stderr = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(60000);

            bool ok = File.Exists(HelperPath);
            if (!string.IsNullOrEmpty(stdout))
                Helpers.Logger.WriteLine($"NvidiaProcessScanner: install-gpu-helper: {stdout}");
            if (!string.IsNullOrEmpty(stderr))
                Helpers.Logger.WriteLine($"NvidiaProcessScanner: install-gpu-helper stderr: {stderr}");
            Helpers.Logger.WriteLine(ok
                ? "NvidiaProcessScanner: gpu-helper installed OK"
                : $"NvidiaProcessScanner: gpu-helper install failed (exit {proc.ExitCode})");
            return ok;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"NvidiaProcessScanner: pkexec self-install exception: {ex.Message}");
            return false;
        }
    }

    private static bool IsNvidiaDevice(string path)
        => path.StartsWith("/dev/nvidia", StringComparison.Ordinal);

    // DRI card/renderD and I2C adapter numbers owned by nvidia. Re-resolved
    // whenever the scan cache is invalidated or the nvidia module presence
    // flips: the kernel allocates cardN/i2c-N numbers dynamically, so a
    // driver unload/reload (Eco<->Standard) can renumber them. A scan that
    // ran while the driver was unloaded must not pin empty sets forever.
    private static HashSet<string>? _nvDriPaths;
    private static HashSet<string>? _nvI2cPaths;
    private static volatile bool _auxDevicesResolved;
    private static bool _auxResolvedWithNvidiaLoaded;

    private static void ResolveAuxDevices()
    {
        bool nvidiaLoaded = Directory.Exists("/sys/module/nvidia");
        if (_auxDevicesResolved && nvidiaLoaded == _auxResolvedWithNvidiaLoaded)
            return;

        var driPaths = new HashSet<string>(StringComparer.Ordinal);
        var i2cPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var pciDev in Directory.EnumerateDirectories("/sys/bus/pci/devices"))
            {
                string vendorPath = Path.Combine(pciDev, "vendor");
                if (!File.Exists(vendorPath))
                    continue;
                string vendor = File.ReadAllText(vendorPath).Trim();
                if (!vendor.StartsWith("0x10de", StringComparison.OrdinalIgnoreCase))
                    continue;

                string drmDir = Path.Combine(pciDev, "drm");
                if (Directory.Exists(drmDir))
                {
                    foreach (var entry in Directory.EnumerateDirectories(drmDir))
                    {
                        string name = Path.GetFileName(entry);
                        if (name.StartsWith("card", StringComparison.Ordinal))
                            driPaths.Add("/dev/dri/" + name);
                        else if (name.StartsWith("renderD", StringComparison.Ordinal))
                            driPaths.Add("/dev/dri/" + name);
                    }
                }
            }
        }
        catch { }

        try
        {
            foreach (var i2cDev in Directory.EnumerateDirectories("/sys/bus/i2c/devices"))
            {
                string namePath = Path.Combine(i2cDev, "name");
                if (!File.Exists(namePath))
                    continue;
                string name = File.ReadAllText(namePath);
                if (!name.Contains("NVIDIA", StringComparison.Ordinal))
                    continue;
                // Adapter names ("NVIDIA i2c adapter N at b:s.f", nv-i2c.c)
                // could in principle collide; when the adapter's sysfs parent
                // is a readable PCI device, require vendor 0x10de.
                if (I2cParentIsNvidiaPci(i2cDev) == false)
                    continue;
                i2cPaths.Add("/dev/" + Path.GetFileName(i2cDev));
            }
        }
        catch { }

        // Publish fully built sets, then the resolved flag (readers never see
        // partially filled sets).
        _nvDriPaths = driPaths;
        _nvI2cPaths = i2cPaths;
        _auxResolvedWithNvidiaLoaded = nvidiaLoaded;
        _auxDevicesResolved = true;
    }

    /// <summary>
    /// The nvidia driver parents its I2C adapters directly on the GPU's PCI
    /// device (nv-i2c.c: dev.parent = nvl->dev), so the resolved sysfs path of
    /// /sys/bus/i2c/devices/i2c-N is .../0000:01:00.0/i2c-N. Returns true/false
    /// when the parent's PCI vendor is readable, null when indeterminate
    /// (non-PCI parent, e.g. SOC) - callers should fall back to name matching.
    /// </summary>
    private static bool? I2cParentIsNvidiaPci(string i2cDev)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(i2cDev, returnFinalTarget: true);
            string real = target?.FullName ?? i2cDev;
            string? parent = Path.GetDirectoryName(real);
            if (parent == null)
                return null;
            string vendorPath = Path.Combine(parent, "vendor");
            if (!File.Exists(vendorPath))
                return null;
            return File.ReadAllText(vendorPath).Trim()
                .StartsWith("0x10de", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    private struct GpuFdCounts
    {
        public int Nvidia;
        public int Dri;
        public int I2c;
    }

    private static GpuFdCounts CountAllGpuFds(string fdDir, out bool permDenied)
    {
        permDenied = false;
        var c = new GpuFdCounts();
        ResolveAuxDevices();

        IEnumerable<string> entries;
        try
        { entries = Directory.EnumerateFileSystemEntries(fdDir); }
        catch (UnauthorizedAccessException) { permDenied = true; return c; }
        catch (IOException) { permDenied = true; return c; }
        catch { return c; }

        foreach (var entry in entries)
        {
            try
            {
                var target = File.ResolveLinkTarget(entry, returnFinalTarget: false);
                if (target?.FullName is not string path)
                    continue;

                if (IsNvidiaDevice(path))
                    c.Nvidia++;
                else if (_nvDriPaths != null && _nvDriPaths.Contains(path))
                    c.Dri++;
                else if (_nvI2cPaths != null && _nvI2cPaths.Contains(path))
                    c.I2c++;
            }
            catch { }
        }
        return c;
    }

    private static string ReadComm(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch
        {
            return "?";
        }
    }

    private static uint ReadUid(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                    continue;
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && uint.TryParse(parts[1], out uint uid))
                    return uid;
            }
        }
        catch
        {
        }
        return uint.MaxValue;
    }

    private static string ResolveServiceUnit(int pid)
    {
        try
        {
            string? path = null;
            foreach (var line in File.ReadLines($"/proc/{pid}/cgroup"))
            {
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    path = line.Substring(3);
                    break;
                }
                if (path == null)
                {
                    int c = line.LastIndexOf(':');
                    if (c >= 0)
                        path = line.Substring(c + 1);
                }
            }
            if (string.IsNullOrEmpty(path))
                return "";

            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            return leaf.EndsWith(".service", StringComparison.Ordinal) ? leaf : "";
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveUserName(uint uid)
    {
        if (uid == uint.MaxValue)
            return "?";
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length >= 3 && uint.TryParse(parts[2], out uint passwdUid) && passwdUid == uid)
                    return parts[0];
            }
        }
        catch
        {
        }
        return $"uid:{uid}";
    }

    private static IReadOnlyList<int> ReadChildren(int pid)
    {
        try
        {
            string path = $"/proc/{pid}/task/{pid}/children";
            if (!File.Exists(path))
                return Array.Empty<int>();
            string content = File.ReadAllText(path).Trim();
            if (content.Length == 0)
                return Array.Empty<int>();
            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<int>(parts.Length);
            foreach (var p in parts)
                if (int.TryParse(p, out int cpid) && cpid != _selfPid)
                    result.Add(cpid);
            return result;
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    public static IReadOnlyList<int> GetDescendantPids(int rootPid)
    {
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (var child in ReadChildren(p))
                if (visited.Add(child))
                    queue.Enqueue(child);
        }
        visited.Remove(rootPid);
        return new List<int>(visited);
    }

    /// Parse /proc/<pid>/stat to extract PPID. Robust against comm-with-spaces
    /// (e.g. "/proc/123/stat" = "123 (my comm) S 1 ..."). Returns 0 on failure.
    private static int ReadParentPid(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int closeParen = stat.LastIndexOf(')');
            if (closeParen < 0 || closeParen + 2 >= stat.Length)
                return 0;
            var fields = stat.Substring(closeParen + 2).Split(' ');
            if (fields.Length < 2)
                return 0;
            return int.TryParse(fields[1], out int ppid) ? ppid : 0;
        }
        catch { return 0; }
    }

    /// Walk parent chain. Return topmost ancestor whose comm matches pid's comm.
    /// Stops at PID 1 (init) or first non-matching comm. Caps walk at 20 hops
    /// against pathological proc-tree loops. Returns pid if no same-comm ancestor.
    /// For Chrome: holder is gpu-process leaf -> walks up through zygote -> main
    /// browser process whose parent is systemd (different comm) -> stops there.
    public static int FindSameCommAncestor(int pid)
    {
        string rootComm = ReadComm(pid);
        if (rootComm == "?")
            return pid;

        int current = pid;
        for (int depth = 0; depth < 20; depth++)
        {
            int parent = ReadParentPid(current);
            if (parent <= 1)
                break;
            string parentComm = ReadComm(parent);
            if (parentComm != rootComm)
                break;
            current = parent;
        }
        return current;
    }

    private static int ReadNvidiaRefcnt()
        => SysfsHelper.ReadInt("/sys/module/nvidia/refcnt", -1);

    public static bool KillProcess(int pid, bool force, NvidiaHolder holder, out string error)
    {
        error = string.Empty;
        if (pid == _selfPid)
        {
            error = "refusing to kill self";
            Logger.WriteLine($"NvidiaProcessScanner.KillProcess: refusing self-kill of PID {pid}");
            return false;
        }

        // Walk UP to find the topmost same-comm ancestor. Holders are typically
        // leaf processes (chrome gpu-process) whose parent zygote auto-respawns
        // them on death. Killing the leaf alone does nothing - the parent
        // refills the slot within milliseconds. We must kill the tree root.
        int root = FindSameCommAncestor(pid);
        if (root != pid)
            Logger.WriteLine($"NvidiaProcessScanner.KillProcess: walked up from {pid} to root {root} (same comm {holder.Comm})");

        var descendants = GetDescendantPids(root);
        var allTargets = new List<int>(descendants.Count + 1);
        allTargets.AddRange(descendants);
        allTargets.Add(root);

        string sig = force ? "-9" : "-15";
        int beforeRefcnt = ReadNvidiaRefcnt();

        Logger.WriteLine($"NvidiaProcessScanner.KillProcess: root={root} ({holder.Comm}) descendants={descendants.Count} sig={sig} nvidia/refcnt={beforeRefcnt}");

        try
        {
            // Service-aware kill via the root helper: stops the systemd unit
            // owning each holder (so it cannot respawn) then signals the pids.
            KillViaHelper(allTargets, sig);
            if (PollAllDead(allTargets, 2000))
            {
                Logger.WriteLine($"NvidiaProcessScanner.KillProcess: tree dead after helper kill; nvidia/refcnt={ReadNvidiaRefcnt()}");
                return true;
            }

            int survivors = 0;
            foreach (var p in allTargets)
                if (IsAlive(p))
                    survivors++;
            error = $"{survivors} process(es) still alive after kill attempt";
            Logger.WriteLine($"NvidiaProcessScanner.KillProcess: {error}; nvidia/refcnt={ReadNvidiaRefcnt()}");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.WriteLine($"NvidiaProcessScanner.KillProcess({pid}, force={force}) failed: {ex.Message}");
            return false;
        }
    }

    /// Alive = /proc entry exists and the process is not a zombie. A killed
    /// but unreaped zombie has already closed its fds and released its GPU
    /// contexts - counting it as a survivor would fail the flow needlessly.
    private static bool IsAlive(int pid)
    {
        if (!Directory.Exists($"/proc/{pid}"))
            return false;
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int closeParen = stat.LastIndexOf(')');
            if (closeParen < 0 || closeParen + 2 >= stat.Length)
                return true;
            return stat[closeParen + 2] != 'Z';
        }
        catch
        {
            // Died between the Exists check and the read.
            return false;
        }
    }

    private static bool AllDead(IEnumerable<int> pids)
    {
        foreach (var p in pids)
            if (IsAlive(p))
                return false;
        return true;
    }

    private static bool PollAllDead(IEnumerable<int> pids, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (AllDead(pids))
                return true;
            Thread.Sleep(100);
            waited += 100;
        }
        return AllDead(pids);
    }

    public static int KillAllHolders(bool force, out int killed, out int failed)
    {
        killed = 0;
        failed = 0;
        var holders = ScanHolders();
        if (holders.Count == 0)
            return 0;

        var allTargets = new HashSet<int>();
        bool anyForeignOwned = false;
        foreach (var h in holders)
        {
            if (h.Pid == _selfPid)
                continue;
            // Walk up to root of same-comm tree (handles chrome zygote respawn)
            int root = FindSameCommAncestor(h.Pid);
            if (root != h.Pid)
                Logger.WriteLine($"NvidiaProcessScanner.KillAllHolders: walked {h.Pid} -> {root} (comm {h.Comm})");
            allTargets.Add(root);
            foreach (var d in GetDescendantPids(root))
                allTargets.Add(d);
            if (!h.IsOwnedByCurrentUser)
                anyForeignOwned = true;
        }
        allTargets.Remove(_selfPid);

        if (allTargets.Count == 0)
            return 0;

        string sig = force ? "-9" : "-15";
        int beforeRefcnt = ReadNvidiaRefcnt();

        Logger.WriteLine($"NvidiaProcessScanner.KillAllHolders: holders={holders.Count} totalTargets={allTargets.Count} sig={sig} foreign={anyForeignOwned} nvidia/refcnt={beforeRefcnt}");

        try
        {
            KillViaHelper(allTargets, sig);
            PollAllDead(allTargets, 2000);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NvidiaProcessScanner.KillAllHolders: {ex.Message}");
        }

        int afterRefcnt = ReadNvidiaRefcnt();
        foreach (var p in allTargets)
        {
            if (IsAlive(p))
                failed++;
            else
                killed++;
        }
        Logger.WriteLine($"NvidiaProcessScanner.KillAllHolders: killed={killed} failed={failed} nvidia/refcnt {beforeRefcnt} -> {afterRefcnt}");

        return holders.Count;
    }

    /// <summary>
    /// Graceful-then-force kill with a convergence loop, targeting the
    /// caller-supplied snapshot of holders (typically the list shown in
    /// NvidiaProcessesWindow) plus anything that appears mid-flight:
    ///   - includes lib-only holders (caller is responsible for session safety;
    ///     the dialog has already warned the user)
    ///   - wave 1 sends SIGTERM, polls 2s, then SIGKILL to survivors and polls 2s
    ///   - after each wave a fresh scan (cross-checked against the driver's own
    ///     NVML process list) picks up respawned or newly started holders;
    ///     waves 2-3 SIGKILL those directly
    ///   - reports the holders still alive after the final wave
    /// </summary>
    public static void KillHoldersGracefulThenForce(
        IReadOnlyList<NvidiaHolder> snapshot,
        out List<NvidiaHolder> survivors)
    {
        survivors = new List<NvidiaHolder>();
        if (snapshot == null || snapshot.Count == 0)
            return;

        const int MaxWaves = 3;
        int beforeRefcnt = ReadNvidiaRefcnt();
        var current = new List<NvidiaHolder>(snapshot);

        try
        {
            for (int wave = 0; wave < MaxWaves && current.Count > 0; wave++)
            {
                var allTargets = BuildKillTargets(current, out bool anyForeign);
                if (allTargets.Count == 0)
                {
                    current.Clear();
                    break;
                }

                Logger.WriteLine($"NvidiaProcessScanner.KillHoldersGracefulThenForce: wave {wave + 1}/{MaxWaves} holders={current.Count} targets={allTargets.Count} foreign={anyForeign} nvidia/refcnt={ReadNvidiaRefcnt()}");

                if (wave == 0)
                {
                    // Pass 1: SIGTERM batch
                    SendKillBatch(allTargets, "-15");
                    PollAllDead(allTargets, 2000);

                    // Pass 2: SIGKILL survivors
                    var pass2 = new HashSet<int>();
                    foreach (var p in allTargets)
                        if (IsAlive(p))
                            pass2.Add(p);
                    if (pass2.Count > 0)
                    {
                        Logger.WriteLine($"NvidiaProcessScanner.KillHoldersGracefulThenForce: {pass2.Count} survived SIGTERM, sending SIGKILL");
                        SendKillBatch(pass2, "-9");
                        PollAllDead(pass2, 2000);
                    }
                }
                else
                {
                    // Respawn wave: these reappeared after a full TERM+KILL
                    // cycle, no grace period this time.
                    SendKillBatch(allTargets, "-9");
                    PollAllDead(allTargets, 2000);
                }

                // Converge on post-kill truth: fresh scan + driver-side NVML
                // cross-check picks up respawns and late arrivals.
                InvalidateScanCache();
                current = RescanWithNvmlCrossCheck();
                if (current.Count > 0)
                    Logger.WriteLine($"NvidiaProcessScanner.KillHoldersGracefulThenForce: wave {wave + 1} left {current.Count} holder(s)");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NvidiaProcessScanner.KillHoldersGracefulThenForce: {ex.Message}");
        }

        foreach (var h in current)
            if (IsAlive(h.Pid))
                survivors.Add(h);

        int afterRefcnt = ReadNvidiaRefcnt();
        Logger.WriteLine($"NvidiaProcessScanner.KillHoldersGracefulThenForce: survivors={survivors.Count} nvidia/refcnt {beforeRefcnt} -> {afterRefcnt}");

        // Next ScanHolders() must see post-kill truth, not the pre-kill snapshot
        // cached up to 5s ago. UI refresh timers (NvidiaProcessesWindow / driver
        // dialog) also benefit from immediate convergence.
        InvalidateScanCache();
    }

    /// Walk each holder up to its same-comm ancestor, collect descendants.
    /// Dedupe via HashSet - sibling holders (chrome --type=gpu-process etc.)
    /// often share a zygote root, so distinct holders collapse to one tree.
    private static HashSet<int> BuildKillTargets(IReadOnlyList<NvidiaHolder> holders, out bool anyForeign)
    {
        anyForeign = false;
        var allTargets = new HashSet<int>();
        foreach (var h in holders)
        {
            if (h.Pid == _selfPid)
                continue;
            int root = FindSameCommAncestor(h.Pid);
            if (root != h.Pid)
                Logger.WriteLine($"NvidiaProcessScanner.BuildKillTargets: walked {h.Pid} -> {root} (comm {h.Comm})");
            allTargets.Add(root);
            foreach (var d in GetDescendantPids(root))
                allTargets.Add(d);
            if (!h.IsOwnedByCurrentUser)
                anyForeign = true;
        }
        allTargets.Remove(_selfPid);
        return allTargets;
    }

    /// Fresh /proc scan merged with the driver's own process list (gpu-helper
    /// nvml-procs). NVML knows every pid with a live GPU context, including
    /// ones the fd/maps scan can miss (e.g. MPS clients). System processes
    /// stay filtered.
    private static List<NvidiaHolder> RescanWithNvmlCrossCheck()
    {
        var holders = new List<NvidiaHolder>(ScanHolders());
        var known = new HashSet<int>();
        foreach (var h in holders)
            known.Add(h.Pid);

        foreach (int pid in TryNvmlPids())
        {
            if (pid == _selfPid || known.Contains(pid) || !IsAlive(pid))
                continue;
            string comm = ReadComm(pid);
            if (IsSystemProcess(comm))
            {
                Logger.WriteLine($"NvidiaProcessScanner: NVML cross-check system (won't kill): {pid}:{comm}");
                continue;
            }
            uint uid = ReadUid(pid);
            holders.Add(new NvidiaHolder(pid, comm, ResolveUserName(uid), 1, 0, uid == _currentUid.Value, ResolveServiceUnit(pid)));
            known.Add(pid);
            Logger.WriteLine($"NvidiaProcessScanner: NVML cross-check found pid {pid} ({comm}) missed by /proc scan");
        }
        return holders;
    }

    /// Pids with a live GPU context per NVML (graphics + compute), via the
    /// root helper. Empty on any failure (helper missing, driver unloaded).
    private static IReadOnlyList<int> TryNvmlPids()
    {
        var result = new List<int>();
        try
        {
            if (!File.Exists(HelperPath))
                return result;
            var output = SysfsHelper.RunCommandWithTimeout(
                SysfsHelper.SudoPath, new[] { "-n", HelperPath, "nvml-procs" }, 5000);
            if (output == null)
                return result;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (int.TryParse(parts[0], out int pid) && pid > 0)
                    result.Add(pid);
            }
        }
        catch
        {
        }
        return result;
    }

    private static void SendKillBatch(ICollection<int> pids, string sig)
        => KillViaHelper(pids, sig);

    private static void KillViaHelper(ICollection<int> pids, string sig)
    {
        if (pids.Count == 0)
            return;

        if (EnsureHelper())
        {
            var args = new string[pids.Count + 2];
            args[0] = "kill";
            args[1] = sig;
            int j = 2;
            foreach (var p in pids)
                args[j++] = p.ToString();
            var r = SysfsHelper.RunSudoOrPkexec(HelperPath, args, sudoTimeoutMs: 20000, pkexecTimeoutMs: 60000);
            if (r != null)
            {
                if (!string.IsNullOrWhiteSpace(r))
                {
                    foreach (var line in r.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = line.Trim();
                        Logger.WriteLine($"NvidiaProcessScanner.KillViaHelper: {trimmed}");
                        RecordStoppedUnit(trimmed);
                    }
                }
                return;
            }
            Logger.WriteLine("NvidiaProcessScanner.KillViaHelper: helper kill failed, falling back to plain kill");
        }

        string pidArgs = string.Join(' ', pids);
        SysfsHelper.RunCommandWithTimeout("kill", $"{sig} {pidArgs}", 5000);
    }

    // ---------- stopped-service bookkeeping (restart after GPU transition) ----------

    /// User services that are safe and desirable to bring back once the GPU
    /// transition is over. Conservative whitelist: powerdevil holds nvidia
    /// I2C (DDC/CI) fds, gets its unit stopped by the holder kill, and the
    /// user otherwise loses brightness keys / idle dimming until re-login
    /// (VFIO-Nvidia-dynamic-unbind README: "restart it afterwards").
    private static readonly string[] RestartableUserUnits =
    {
        "plasma-powerdevil.service",
    };

    /// Matches the gpu-helper kill output line:
    ///   stopped <unit> (user service uid <uid>, pid <pid>)
    private static readonly System.Text.RegularExpressions.Regex StoppedUserUnitRegex =
        new(@"^stopped (\S+) \(user service uid (\d+), pid \d+\)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly object _stoppedUnitsLock = new();
    private static readonly List<(string Unit, uint Uid)> _stoppedUserUnits = new();

    private static void RecordStoppedUnit(string helperOutputLine)
    {
        var m = StoppedUserUnitRegex.Match(helperOutputLine);
        if (!m.Success || !uint.TryParse(m.Groups[2].Value, out uint uid))
            return;
        string unit = m.Groups[1].Value;
        lock (_stoppedUnitsLock)
        {
            if (!_stoppedUserUnits.Contains((unit, uid)))
                _stoppedUserUnits.Add((unit, uid));
        }
    }

    /// <summary>
    /// Whitelisted user units of the CURRENT user that the kill flow stopped,
    /// cleared on read. GPUModeControl restarts them (plain
    /// `systemctl --user start`, no privilege needed) once the transition is
    /// done - restarting earlier would re-open the nvidia I2C bus and re-pin
    /// the module.
    /// </summary>
    public static IReadOnlyList<string> ConsumeStoppedRestartableUserUnits()
    {
        var result = new List<string>();
        lock (_stoppedUnitsLock)
        {
            foreach (var (unit, uid) in _stoppedUserUnits)
            {
                if (uid != _currentUid.Value)
                    continue;
                if (Array.IndexOf(RestartableUserUnits, unit) < 0)
                    continue;
                if (!result.Contains(unit))
                    result.Add(unit);
            }
            _stoppedUserUnits.Clear();
        }
        return result;
    }
}

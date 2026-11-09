using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu;

// Mirror of supergfxctl pci_device.rs:673 (iter.rev() unbind before power change).
internal record UnbindRecord(string Bdf, string DriverName);

/// <summary>The dGPU on the PCI bus: discovery, slot and bridge cache, and privileged bus operations.</summary>
internal static class DgpuPci
{
    /// <summary>
    /// Scan /sys/bus/pci/devices for the discrete GPU graphics function,
    /// regardless of whether a driver is bound: NVIDIA (vendor 0x10de) or AMD
    /// (vendor 0x1002 with boot_vga != 1 so the iGPU is excluded). Matches only
    /// VGA (0x0300xx) / 3D (0x0302xx) classes so audio/USB sibling functions are
    /// skipped. Returns (bdf, vendor) or null. Detects presence right after a
    /// rescan, before the driver binds.
    /// </summary>
    internal static (string bdf, string vendor)? Find()
    {
        try
        {
            string devDir = GPUModeControl.TestPathPrefix + "/sys/bus/pci/devices";
            if (!Directory.Exists(devDir))
                return null;
            foreach (var dev in Directory.GetDirectories(devDir))
            {
                string vendorPath = Path.Combine(dev, "vendor");
                if (!File.Exists(vendorPath))
                    continue;
                string vendor = File.ReadAllText(vendorPath).Trim();
                bool isNvidia = vendor.Equals("0x10de", StringComparison.OrdinalIgnoreCase);
                bool isAmd = vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase);
                if (!isNvidia && !isAmd)
                    continue;

                string clsPath = Path.Combine(dev, "class");
                if (!File.Exists(clsPath))
                    continue;
                string cls = File.ReadAllText(clsPath).Trim();
                if (!cls.StartsWith("0x0300", StringComparison.Ordinal)
                    && !cls.StartsWith("0x0302", StringComparison.Ordinal))
                    continue; // not the graphics function (skip audio/USB siblings)

                if (isAmd)
                {
                    string bootVgaPath = Path.Combine(dev, "boot_vga");
                    if (File.Exists(bootVgaPath) && File.ReadAllText(bootVgaPath).Trim() == "1")
                        continue;
                    // DRM check: GPU driving the internal panel is the iGPU
                    if (SysfsHelper.HasInternalDisplay(dev))
                        continue;
                }
                return (Path.GetFileName(dev), vendor);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: FindDgpuPciDevice failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Cached dGPU PCI address for AMD systems (e.g., "0000:01:00.0").</summary>
    private static string? _cachedDgpuPciAddress;
    private static bool _dgpuPciScanned;

    /// <summary>Drop the cached AMD dGPU address; each new controller scans once.</summary>
    internal static void ResetAmdCache()
    {
        _cachedDgpuPciAddress = null;
        _dgpuPciScanned = false;
    }

    /// <summary>
    /// Scan /sys/bus/pci/devices for the AMD discrete GPU.
    /// Looks for vendor=0x1002, class=0x0300xx or 0x0302xx, boot_vga=0.
    /// Caches the result.
    /// </summary>
    internal static string? FindAmd()
    {
        if (_dgpuPciScanned)
            return _cachedDgpuPciAddress;
        _dgpuPciScanned = true;

        try
        {
            string pciDir = GPUModeControl.TestPathPrefix + "/sys/bus/pci/devices";
            if (!Directory.Exists(pciDir))
                return null;

            foreach (var deviceDir in Directory.GetDirectories(pciDir))
            {
                string? vendor = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "vendor"));
                if (vendor != "0x1002")
                    continue; // Not AMD

                string? cls = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "class"));
                if (cls == null)
                    continue;
                // VGA: 0x030000, 3D controller: 0x030200
                if (!cls.StartsWith("0x0300") && !cls.StartsWith("0x0302"))
                    continue;

                string? bootVga = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "boot_vga"));
                if (bootVga == "1")
                    continue; // This is the iGPU, skip

                // Confirm it has a DRM subsystem
                if (!Directory.Exists(Path.Combine(deviceDir, "drm")))
                    continue;

                _cachedDgpuPciAddress = Path.GetFileName(deviceDir);
                Logger.WriteLine($"GPUModeControl: found AMD dGPU at {_cachedDgpuPciAddress}");
                return _cachedDgpuPciAddress;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: PCI scan failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Find NVIDIA dGPU PCI address by walking the nvidia driver's bound devices.
    /// Returns null if nvidia is unbound (e.g., dGPU in Eco mode).
    /// </summary>
    internal static string? FindNvidiaBound()
    {
        try
        {
            string driverDir = "/sys/bus/pci/drivers/nvidia";
            if (!Directory.Exists(driverDir))
                return null;

            foreach (var item in Directory.GetFileSystemEntries(driverDir))
            {
                string name = Path.GetFileName(item);
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-9]$"))
                    return name;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: FindNvidiaPciAddress failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// runtime PM status ("active"/"suspended") of the first non-boot
    /// discrete GPU (NVIDIA or AMD) on the PCI bus, or null when none is
    /// visible (no dGPU, or Eco removed it from the bus). Reading
    /// runtime_status never wakes the device.
    /// </summary>
    internal static string? RuntimeStatus()
    {
        try
        {
            string pciDir = GPUModeControl.TestPathPrefix + "/sys/bus/pci/devices";
            if (!Directory.Exists(pciDir))
                return null;
            foreach (var dev in Directory.EnumerateDirectories(pciDir))
            {
                string cls = SysfsHelper.ReadAttribute(Path.Combine(dev, "class")) ?? "";
                if (!cls.StartsWith("0x0300", StringComparison.Ordinal)
                    && !cls.StartsWith("0x0302", StringComparison.Ordinal))
                    continue;
                if (SysfsHelper.ReadInt(Path.Combine(dev, "boot_vga"), 0) == 1)
                    continue;
                string vendor = SysfsHelper.ReadAttribute(Path.Combine(dev, "vendor")) ?? "";
                if (!vendor.StartsWith("0x10de", StringComparison.OrdinalIgnoreCase)
                    && !vendor.StartsWith("0x1002", StringComparison.OrdinalIgnoreCase))
                    continue;
                return SysfsHelper.ReadAttribute(Path.Combine(dev, "power/runtime_status"));
            }
        }
        catch { }
        return null;
    }

    /// <summary>Read power/runtime_status for a PCI device. Returns "active"/"suspended"/etc.</summary>
    internal static string ReadRuntimeStatus(string pciAddr)
    {
        string path = GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{pciAddr}/power/runtime_status";
        return SysfsHelper.ReadAttribute(path) ?? "active";
    }

    /// <summary>Number of PCI functions with a display class (0x03xxxx: VGA,
    /// 3D, other display controllers). Vendor-agnostic, driver not needed.</summary>
    internal static int CountDisplayFunctions()
    {
        int count = 0;
        try
        {
            string devDir = GPUModeControl.TestPathPrefix + "/sys/bus/pci/devices";
            if (!Directory.Exists(devDir))
                return 0;
            foreach (var dev in Directory.GetDirectories(devDir))
            {
                string clsPath = Path.Combine(dev, "class");
                if (!File.Exists(clsPath))
                    continue;
                if (File.ReadAllText(clsPath).Trim()
                    .StartsWith("0x03", StringComparison.Ordinal))
                    count++;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: display-class count failed: {ex.Message}");
        }
        return count;
    }

    /// <summary>
    /// dGPU graphics function is on the bus but no driver is bound to it.
    /// Happens when an Eco release fails halfway (the PCI unbind completed
    /// after our timeout, so the rollback never rebound it).
    /// </summary>
    internal static bool PresentButDriverless()
    {
        var dev = Find();
        if (dev == null)
            return false;
        return !Directory.Exists(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{dev.Value.bdf}/driver");
    }

    /// <summary>
    /// Bind any driverless dGPU PCI function back to its driver. The normal
    /// rescan path auto-probes, so this no-ops then; it only acts in the
    /// recovery case where the device stayed on the bus unbound (modprobe
    /// alone does not re-probe an already-enumerated device).
    /// </summary>
    internal static void EnsureBound(string gfxBdf, bool isAmd)
    {
        string gfxDriver = isAmd ? "amdgpu" : "nvidia";
        foreach (var node in Functions(gfxBdf))
        {
            string devDir = GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{node}";
            if (Directory.Exists(Path.Combine(devDir, "driver")))
                continue;

            string cls = SysfsHelper.ReadAttribute(Path.Combine(devDir, "class")) ?? "";
            string? driver = null;
            if (cls.StartsWith("0x0300", StringComparison.Ordinal) || cls.StartsWith("0x0302", StringComparison.Ordinal))
                driver = gfxDriver;
            else if (cls.StartsWith("0x0403", StringComparison.Ordinal))
                driver = "snd_hda_intel";
            if (driver == null)
                continue;

            Logger.WriteLine($"GPUModeControl: {node} driverless (class {cls}) - binding to {driver}");
            bool ok = RunPciAction("pci-bind", driver, node, sudoTimeoutMs: 15000);
            Logger.WriteLine($"GPUModeControl: bind {node} -> {driver} = {(ok ? "OK" : "FAILED")}");
        }
    }

    /// <summary>Poll for the dGPU node without running the escalation ladder.</summary>
    internal static bool WaitForNode(int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (Find() != null)
                return true;
            Thread.Sleep(500);
            waited += 500;
        }
        return false;
    }

    /// <summary>
    /// Poll for the dGPU with an escalating recovery ladder. Beyond the basic
    /// slot-power + rescan loop, three increasingly aggressive steps fire while
    /// the device stays missing:
    ///   4s  wake the parent root port (power/control=on) - a runtime-suspended
    ///       bridge in D3cold enumerates nothing on rescan, the most common
    ///       cause of "NVRM: No NVIDIA GPU found" loops after Eco
    ///   8s  bounce dgpu_disable 1 -> 0 so the firmware re-runs its power-on
    ///       sequence from scratch
    ///   12s remove the bridge from the device tree and rescan - full fresh
    ///       re-enumeration of the hierarchy, clears stale config state
    /// </summary>
    internal static bool WaitForDevice(int timeoutMs, Action bounceFirmware)
    {
        int waited = 0;
        int attempt = 0;
        bool bridgeWoken = false, disableBounced = false, bridgeReset = false;
        while (waited < timeoutMs)
        {
            if (Find() != null)
            {
                Logger.WriteLine($"GPUModeControl: dGPU present after {waited}ms ({attempt} rescan(s))");
                return true;
            }

            if (waited >= 4000 && !bridgeWoken)
            {
                bridgeWoken = true;
                WakeBridge();
            }
            else if (waited >= 8000 && !disableBounced)
            {
                disableBounced = true;
                bounceFirmware();
            }
            else if (waited >= 12000 && !bridgeReset)
            {
                bridgeReset = true;
                ResetBridge();
            }

            // Re-assert slot power (idempotent) then re-trigger enumeration;
            // SetGpuEco already did the first rescan.
            PowerOnSlot();
            SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
            attempt++;
            Thread.Sleep(1000);
            waited += 1000;
        }
        return Find() != null;
    }

    /// Escalation step 1: force the dGPU's parent root port out of runtime
    /// suspend. While the bridge sits in D3cold its link is down and a bus
    /// rescan cannot see the dGPU at all.
    internal static void WakeBridge()
    {
        string? bridge = ResolveBridge();
        if (string.IsNullOrEmpty(bridge))
        {
            Logger.WriteLine("GPUModeControl: escalation - bridge unknown, cannot wake");
            return;
        }
        string status = SysfsHelper.ReadAttribute(
            GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{bridge}/power/runtime_status")?.Trim() ?? "?";
        Logger.WriteLine($"GPUModeControl: escalation - waking bridge {bridge} (runtime_status={status})");
        RunPciPower(bridge!, "on");
    }

    /// Escalation step 3 (last resort): drop the parent root port from the
    /// device tree and rescan. The rescan re-discovers the bridge from the
    /// root complex and re-enumerates everything behind it from scratch,
    /// clearing any stale state that blocked the dGPU from re-appearing.
    internal static void ResetBridge()
    {
        string? bridge = ResolveBridge();
        if (string.IsNullOrEmpty(bridge))
        {
            Logger.WriteLine("GPUModeControl: escalation - bridge unknown, cannot reset");
            return;
        }
        if (!Directory.Exists(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{bridge}"))
        {
            Logger.WriteLine($"GPUModeControl: escalation - bridge {bridge} not in device tree, rescan only");
            SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
            return;
        }
        Logger.WriteLine($"GPUModeControl: escalation - removing bridge {bridge} + full rescan");
        RunPciRemove(bridge!);
        Thread.Sleep(500);
        SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
    }

    private const string DgpuSlotKey = "dgpu_pci_slot";

    private const string DgpuBridgeKey = "dgpu_pci_bridge";

    private const string DgpuBdfKey = "dgpu_pci_bdf";

    private const string DgpuSlotMissKey = "dgpu_slot_miss";

    internal static void CacheSlot()
    {
        try
        {
            ResolveSlot();
            ResolveBridge();
            ValidateCacheAtStartup();
        }
        catch (Exception ex) { Logger.WriteLine($"GPUModeControl: CacheDgpuSlotIfPresent failed: {ex.Message}"); }
    }

    /// <summary>
    /// Once per app start: age out a dGPU cache that no longer matches this
    /// machine (config imported from another system, or hardware swap).
    /// While the dGPU is merely hidden (Eco artifacts / validated slot) the
    /// cache is legitimate and the counter resets. After 3 consecutive
    /// starts with no evidence, the cached keys are dropped so
    /// <see cref="GPUModeControl.HasSecondGpu"/> stops reporting a phantom dGPU.
    /// </summary>
    private static void ValidateCacheAtStartup()
    {
        if (string.IsNullOrEmpty(AppConfig.GetString(DgpuSlotKey))
            && string.IsNullOrEmpty(AppConfig.GetString(DgpuBdfKey)))
            return;

        bool evidence = Find() != null
            || GpuBootFiles.EcoBlocksPresent()
            || ValidatedSlotCached();
        if (evidence)
        {
            if (AppConfig.Get(DgpuSlotMissKey, 0) != 0)
                AppConfig.Set(DgpuSlotMissKey, 0);
            return;
        }

        int misses = AppConfig.Get(DgpuSlotMissKey, 0) + 1;
        if (misses < 3)
        {
            AppConfig.Set(DgpuSlotMissKey, misses);
            Logger.WriteLine($"GPUModeControl: cached dGPU slot unverified ({misses}/3)");
            return;
        }
        AppConfig.Remove(DgpuSlotKey);
        AppConfig.Remove(DgpuBridgeKey);
        AppConfig.Remove(DgpuBdfKey);
        AppConfig.Set(DgpuSlotMissKey, 0);
        Logger.WriteLine("GPUModeControl: cleared stale dGPU cache (no dGPU evidence on 3 consecutive starts)");
    }

    /// <summary>
    /// The dGPU's parent PCI bridge (root port), resolved live while the device
    /// is present and cached in config so recovery can target the bridge even
    /// after the dGPU vanished from the bus (Eco, failed re-enable).
    /// </summary>
    internal static string? ResolveBridge()
    {
        string? bdf = Find()?.bdf;
        if (!string.IsNullOrEmpty(bdf))
        {
            try
            {
                var devDir = new DirectoryInfo(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{bdf}");
                string real = devDir.ResolveLinkTarget(true)?.FullName ?? devDir.FullName;
                string parent = Path.GetFileName(Path.GetDirectoryName(real) ?? "");
                // Parent must itself be a BDF (root port); the top-level
                // "pci0000:00" host bridge node is not removable/powerable.
                if (parent.Contains(':') && parent.Contains('.'))
                {
                    if (AppConfig.GetString(DgpuBridgeKey) != parent)
                    {
                        AppConfig.Set(DgpuBridgeKey, parent);
                        Logger.WriteLine($"GPUModeControl: cached dGPU parent bridge {parent} (bdf {bdf})");
                    }
                    return parent;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: ResolveDgpuBridge failed: {ex.Message}");
            }
        }
        return AppConfig.GetString(DgpuBridgeKey);
    }

    internal static string? ResolveSlot()
    {
        string? bdf = Find()?.bdf;
        if (!string.IsNullOrEmpty(bdf))
        {
            // Remember the dGPU BDF so the slot cache can later be validated
            // against this machine (see ValidatedSlotCached).
            if (AppConfig.GetString(DgpuBdfKey) != bdf)
                AppConfig.Set(DgpuBdfKey, bdf!);
            string? slot = FindSlot(bdf!);
            if (!string.IsNullOrEmpty(slot))
            {
                if (AppConfig.GetString(DgpuSlotKey) != slot)
                {
                    AppConfig.Set(DgpuSlotKey, slot!);
                    Logger.WriteLine($"GPUModeControl: cached dGPU PCIe slot {slot} (bdf {bdf})");
                }
                return slot;
            }
        }
        return AppConfig.GetString(DgpuSlotKey);
    }

    /// <summary>
    /// True when the cached dGPU slot refers to THIS machine: the slot dir
    /// exists and its address matches the cached dGPU BDF. Old configs that
    /// predate the BDF cache fall back to slot-dir existence only.
    /// </summary>
    internal static bool ValidatedSlotCached()
    {
        string? slot = AppConfig.GetString(DgpuSlotKey);
        if (string.IsNullOrEmpty(slot))
            return false;
        string slotDir = GPUModeControl.TestPathPrefix + $"/sys/bus/pci/slots/{slot}";
        if (!Directory.Exists(slotDir))
            return false;
        string? cachedBdf = AppConfig.GetString(DgpuBdfKey);
        if (string.IsNullOrEmpty(cachedBdf))
            return true; // legacy cache: slot presence is all we have
        string addr = SysfsHelper.ReadAttribute(Path.Combine(slotDir, "address")) ?? "";
        return addr.Length > 0
            && cachedBdf.StartsWith(addr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find the PCIe slot whose address matches the dGPU BDF. Slot addresses are
    /// the function-less form (e.g. "0000:01:00"), so the dGPU BDF
    /// "0000:01:00.0" starts with it.
    /// </summary>
    private static string? FindSlot(string bdf)
    {
        try
        {
            string slotsDir = GPUModeControl.TestPathPrefix + "/sys/bus/pci/slots";
            if (!Directory.Exists(slotsDir))
                return null;
            foreach (var dir in Directory.GetDirectories(slotsDir))
            {
                string addrPath = Path.Combine(dir, "address");
                if (!File.Exists(addrPath))
                    continue;
                string addr = File.ReadAllText(addrPath).Trim();
                if (addr.Length > 0 && bdf.StartsWith(addr, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: FindDgpuSlot failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Write 1 to the dGPU PCIe slot's power file via gpu-helper (root-only), so
    /// the pciehp controller powers the slot and trains the link. Idempotent:
    /// skips when already powered, no-op (logs) when the slot is unknown.
    /// </summary>
    internal static void PowerOnSlot()
    {
        string? slot = ResolveSlot();
        if (string.IsNullOrEmpty(slot))
        {
            Logger.WriteLine("GPUModeControl: dGPU PCIe slot unknown - cannot assert slot power (rescan only)");
            return;
        }
        int cur = SysfsHelper.ReadInt(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/slots/{slot}/power", -1);
        if (cur == 1)
            return;
        bool ok = RunSlotPower(slot!, "1");
        Logger.WriteLine($"GPUModeControl: slot-power {slot} = 1 ({(ok ? "OK" : "FAILED")}) [was {cur}]");
    }

    /// <summary>Cut slot power after dgpu_disable=1 as extra insurance.</summary>
    internal static void PowerOffSlot()
    {
        string? slot = ResolveSlot();
        if (string.IsNullOrEmpty(slot))
            return;
        int cur = SysfsHelper.ReadInt(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/slots/{slot}/power", -1);
        if (cur == 0)
            return;
        bool ok = RunSlotPower(slot!, "0");
        Logger.WriteLine($"GPUModeControl: slot-power {slot} = 0 ({(ok ? "OK" : "FAILED")}) [was {cur}]");
    }

    private static bool RunSlotPower(string slot, string value)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "slot-power", slot, value },
            sudoTimeoutMs: 10000, pkexecTimeoutMs: 60000);
        return r != null;
    }

    internal static List<UnbindRecord> BoundFunctions(string dgpuBdf)
    {
        // dgpuBdf = "0000:01:00.0" -> prefix "0000:01:00"
        int dotIx = dgpuBdf.LastIndexOf('.');
        if (dotIx < 0)
            return new List<UnbindRecord>();
        string prefix = dgpuBdf.Substring(0, dotIx);

        string root = GPUModeControl.TestPathPrefix + "/sys/bus/pci/devices/";
        var results = new List<UnbindRecord>();
        if (!Directory.Exists(root))
            return results;

        foreach (var dir in Directory.GetDirectories(root))
        {
            string bdf = Path.GetFileName(dir);
            if (!bdf.StartsWith(prefix + ".", StringComparison.Ordinal))
                continue;

            string driverLink = Path.Combine(dir, "driver");
            if (!Directory.Exists(driverLink))
            {
                Logger.WriteLine($"GPUModeControl: {bdf} no driver bound, skipping");
                continue;
            }
            string? driverName = null;
            try
            { driverName = Path.GetFileName(new DirectoryInfo(driverLink).ResolveLinkTarget(true)?.FullName ?? ""); }
            catch { }
            if (string.IsNullOrEmpty(driverName))
            {
                Logger.WriteLine($"GPUModeControl: {bdf} could not resolve driver symlink, skipping");
                continue;
            }
            results.Add(new UnbindRecord(bdf, driverName));
        }
        results.Sort((a, b) => string.CompareOrdinal(a.Bdf, b.Bdf));
        return results;
    }

    /// <summary>
    /// All PCI function nodes of the dGPU (e.g. 0000:01:00.0/.1/.2/.3),
    /// regardless of whether a driver is bound. Used to apply runtime-PM
    /// (power/control) to every function after a Standard re-enable.
    /// </summary>
    private static List<string> Functions(string dgpuBdf)
    {
        var results = new List<string>();
        int dotIx = dgpuBdf.LastIndexOf('.');
        string prefix = dotIx < 0 ? dgpuBdf : dgpuBdf.Substring(0, dotIx);
        string root = GPUModeControl.TestPathPrefix + "/sys/bus/pci/devices/";
        if (!Directory.Exists(root))
            return results;
        foreach (var dir in Directory.GetDirectories(root))
        {
            string bdf = Path.GetFileName(dir);
            if (bdf.StartsWith(prefix + ".", StringComparison.Ordinal))
                results.Add(bdf);
        }
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    internal static bool RunPciAction(string action, string driver, string bdf, int sudoTimeoutMs = 10000)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { action, driver, bdf },
            sudoTimeoutMs: sudoTimeoutMs, pkexecTimeoutMs: 60000);
        return r != null;
    }

    internal static bool RunPciRemove(string bdf)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "pci-remove", bdf },
            sudoTimeoutMs: 10000, pkexecTimeoutMs: 60000);
        return r != null;
    }

    private static bool RunPciPower(string bdf, string value)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { "pci-power", bdf, value },
            sudoTimeoutMs: 5000, pkexecTimeoutMs: 30000);
        return r != null;
    }

    /// <summary>
    /// Set power/control=auto on every dGPU PCI function after Standard
    /// re-enable so the device can runtime-suspend when idle (supergfxctl
    /// set_runtime_pm Auto). Best-effort - never fails the switch.
    /// </summary>
    internal static void SetRuntimePmAuto()
    {
        string? bdf = Find()?.bdf;
        if (string.IsNullOrEmpty(bdf))
            return;
        foreach (var node in Functions(bdf!))
        {
            if (File.Exists(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{node}/power/control"))
                RunPciPower(node, "auto");
        }

        // The recovery ladder may have forced the parent bridge to power/control=on;
        // return it to the kernel default so the link can runtime-suspend again.
        string? bridge = ResolveBridge();
        if (!string.IsNullOrEmpty(bridge)
            && File.Exists(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{bridge}/power/control"))
            RunPciPower(bridge!, "auto");
    }

    /// <summary>
    /// Remove all dGPU PCI functions from the bus. Reduces power draw on
    /// systems without firmware dgpu_disable. The udev rule prevents
    /// re-enumeration on rescan; live-standard reverses this.
    /// </summary>
    internal static void RemoveFunctions()
    {
        var dev = Find();
        if (dev == null)
        {
            Logger.WriteLine("GPUModeControl: PciRemoveDgpuFunctions - no dGPU on bus");
            return;
        }

        ResolveSlot();
        ResolveBridge();

        foreach (var node in Functions(dev.Value.bdf))
        {
            Logger.WriteLine($"GPUModeControl: PCI-removing {node}");
            RunPciRemove(node);
        }
    }

    /// <summary>
    /// Drop the dGPU PCI nodes right before writing dgpu_disable=1.
    /// Firmware cuts the rail but leaves the devices enumerated, and their
    /// sysfs attributes are kernel-cached, so the next Standard switch sees a
    /// live-looking node, loads nvidia against a dead GPU and gets
    /// "fell off the bus". Removing first makes re-appearance after rescan a
    /// real signal. Best-effort - never blocks the Eco write.
    /// </summary>
    internal static void DropNodes()
    {
        if (GPUModeControl.IsTestMode)
            return;
        try
        { RemoveFunctions(); }
        catch (Exception ex)
        { Logger.WriteLine($"GPUModeControl: DropDgpuPciNodes failed: {ex.Message}"); }
    }
}

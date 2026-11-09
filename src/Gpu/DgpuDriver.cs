using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu;

/// <summary>The dGPU driver: busy check, release before Eco, load after re-enable, and the files and services tied to it.</summary>
internal static class DgpuDriver
{
    /// <summary>
    /// Check if the dGPU driver is currently active (holding the hardware).
    /// NVIDIA: check /sys/module/nvidia_drm/refcnt
    /// AMD: check dGPU PCI device power/runtime_status
    /// </summary>
    internal static bool IsActive()
    {
        if (IsNvidiaGpu())
            return IsNvidiaDriverActive();

        if (IsAmdDgpu())
            return IsAmdDriverActive();

        // No known dGPU driver loaded - safe
        Logger.WriteLine("GPUModeControl: no dGPU driver detected - safe");
        return false;
    }

    private static bool IsNvidiaDriverActive()
    {
        // The full nvidia kernel module family. nvidia_drm is the display
        // path; nvidia_uvm is the CUDA/compute path; nvidia_modeset wires
        // KMS. Any one of them in use is enough to keep nvidia loaded
        // and Eco unable to write dgpu_disable cleanly.
        string[] modules = new[] { "nvidia_drm", "nvidia_modeset", "nvidia_uvm", "nvidia" };
        bool anyModuleLoaded = false;
        foreach (var mod in modules)
        {
            string modDir = GPUModeControl.TestPathPrefix + "/sys/module/" + mod;
            if (!Directory.Exists(modDir))
                continue;
            anyModuleLoaded = true;

            int refcnt = SysfsHelper.ReadInt(modDir + "/refcnt", -1);
            if (refcnt < 0)
            {
                Logger.WriteLine($"GPUModeControl: {mod} loaded but refcnt unreadable - assuming ACTIVE");
                return true;
            }
            if (refcnt > 0)
            {
                Logger.WriteLine($"GPUModeControl: {mod} refcnt={refcnt} - driver ACTIVE");
                return true;
            }
        }

        if (!anyModuleLoaded)
        {
            Logger.WriteLine("GPUModeControl: no nvidia* modules loaded - safe");
            return false;
        }

        // Modules loaded but all refcnts are zero. Defense in depth: any
        // process holding /dev/nvidia* FDs OR mapping libnvidia/libcuda
        // counts as "active" so the user sees the blocking dialog before
        // we touch the kernel modules. Lib-mappers (rustdesk, kwin,
        // plasmashell) don't strictly block rmmod, but unloading the
        // driver under them risks silent failures or session crashes -
        // the dialog gives the user explicit control.
        int totalHolders = NvidiaProcessScanner.CountHolders();
        if (totalHolders > 0)
        {
            int fdHolders = NvidiaProcessScanner.CountFdHolders();
            Logger.WriteLine($"GPUModeControl: {totalHolders} holders ({fdHolders} active FD, {totalHolders - fdHolders} libnvidia-mapped) - driver ACTIVE");
            return true;
        }

        Logger.WriteLine("GPUModeControl: all nvidia* modules idle, no holders - driver safe");
        return false;
    }

    private static bool IsAmdDriverActive()
    {
        string? pciAddr = DgpuPci.FindAmd();
        if (pciAddr == null)
        {
            Logger.WriteLine("GPUModeControl: AMD dGPU PCI address not found - assuming safe");
            return false;
        }

        string status = DgpuPci.ReadRuntimeStatus(pciAddr);
        if (status == "suspended")
        {
            Logger.WriteLine($"GPUModeControl: AMD dGPU {pciAddr} runtime_status=suspended - safe");
            return false;
        }

        Logger.WriteLine($"GPUModeControl: AMD dGPU {pciAddr} runtime_status={status} - ACTIVE");
        return true;
    }

    /// <summary>
    /// Attempt to release the dGPU driver so dgpu_disable=1 can proceed safely.
    /// NVIDIA: pkexec rmmod nvidia stack
    /// AMD: pkexec PCI unbind+remove
    /// Returns true if driver was released.
    /// </summary>
    internal static bool TryRelease()
    {
        if (IsNvidiaGpu())
            return TryReleaseNvidiaDriver();

        if (IsAmdDgpu())
            return TryReleaseAmdDriver();

        return true; // No driver to release
    }

    private static bool TryReleaseNvidiaDriver()
    {
        Logger.WriteLine("GPUModeControl: attempting NVIDIA driver release");

        // Stop our own telemetry from spawning nvidia-smi mid-release: such a
        // process opens /dev/nvidia*, blocks the PCI unbind, and if killed on
        // its timeout can wedge in D-state and make rmmod nvidia fail forever.
        NVidia.GpuQueryGate.Pause(TimeSpan.FromSeconds(90), "driver release");

        // CRITICAL: undo any GPU/VRAM clock lock and clock offsets BEFORE powering
        // the dGPU off. Locked clocks (nvidia-smi -lgc/-lmc) pin the GPU's power
        // management on, so it can never enter the D3cold state that dgpu_disable=1
        // needs to power-gate it. Leaving a lock set makes the Eco write stall ~25s
        // (ACPI/EC timeout) and the dGPU then fails to re-enumerate on the next
        // rescan - a hard wedge that only a reboot clears. Must run while the
        // driver is still loaded.
        ResetDgpuToStock();

        // Cache the dGPU's PCIe slot + parent bridge while the device is still
        // present, so the Standard re-enable can re-power and re-enumerate it
        // even though it will be gone by then.
        DgpuPci.ResolveSlot();
        DgpuPci.ResolveBridge();

        var r1 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "stop", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r1 != null ? "GPUModeControl: stopped nvidia-powerd" : "GPUModeControl: nvidia-powerd stop failed");
        var r2 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "stop", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r2 != null ? "GPUModeControl: stopped nvidia-persistenced" : "GPUModeControl: nvidia-persistenced stop failed");
        Thread.Sleep(500);

        return ReleaseNvidiaModulesAndPurgeHolders(DgpuPci.FindNvidiaBound(), out _);
    }

    /// <summary>
    /// Poll /sys/module/nvidia/refcnt until it holds the same value for ~1s
    /// (context teardown finished) or the timeout expires. Returns immediately
    /// when the module is absent or refcnt is unreadable.
    /// </summary>
    private static void WaitForNvidiaRefcntSettle(int timeoutMs)
    {
        string path = GPUModeControl.TestPathPrefix + "/sys/module/nvidia/refcnt";
        int last = SysfsHelper.ReadInt(path, -1);
        if (last < 0)
            return;
        int stableReads = 0;
        int waited = 0;
        const int stepMs = 250;
        while (waited < timeoutMs)
        {
            Thread.Sleep(stepMs);
            waited += stepMs;
            int now = SysfsHelper.ReadInt(path, -1);
            if (now < 0)
                return;
            if (now == last)
            {
                if (++stableReads >= 4)
                {
                    Logger.WriteLine($"GPUModeControl: nvidia refcnt settled at {now} after {waited}ms");
                    return;
                }
            }
            else
            {
                stableReads = 0;
                last = now;
            }
        }
        Logger.WriteLine($"GPUModeControl: nvidia refcnt still moving after {timeoutMs}ms (refcnt={last}) - proceeding");
    }

    /// <summary>
    /// Return the dGPU to stock clocks (unlock GPU + VRAM clocks, zero core/mem
    /// offsets) before it is powered off. Best-effort with short timeouts so it
    /// never adds delay when the GPU is already unresponsive. See
    /// <see cref="TryReleaseNvidiaDriver"/> for why this is required.
    /// </summary>
    private static void ResetDgpuToStock()
    {
        try
        {
            string helper = SysfsHelper.GpuHelperPath;
            // Unlock GPU and VRAM clocks - the part that blocks D3cold.
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "smi", "-rgc" }, sudoTimeoutMs: 4000);
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "smi", "-rmc" }, sudoTimeoutMs: 4000);
            // Zero any core/mem clock offsets (modern per-pstate API in gpu-helper).
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "nvml-clocks", "0", "0" }, sudoTimeoutMs: 4000);
            Logger.WriteLine("GPUModeControl: reset dGPU clocks to stock before power-off");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: ResetDgpuToStock failed (non-fatal): {ex.Message}");
        }
    }

    private static readonly string[] NvidiaModules =
        { "nvidia_drm", "nvidia_modeset", "nvidia_uvm", "nvidia_wmi_ec_backlight", "nvidia" };

    private static bool ReleaseNvidiaModulesAndPurgeHolders(string? dgpuBdf, out List<UnbindRecord> unbindStack)
    {
        unbindStack = new List<UnbindRecord>();

        if (!string.IsNullOrEmpty(dgpuBdf))
        {
            // Signal the compositor to release the DRM device gracefully before
            // we yank the driver.
            TrySignalDrmRemove(dgpuBdf!);

            var funcs = DgpuPci.BoundFunctions(dgpuBdf!);
            funcs.Reverse(); // highest function first (.1 audio before .0 graphics)
            foreach (var rec in funcs)
            {
                Logger.WriteLine($"GPUModeControl: unbinding {rec.Bdf} from {rec.DriverName}");
                if (!TryUnbindFunction(rec))
                {
                    Logger.WriteLine($"GPUModeControl: unbind {rec.Bdf} FAILED - rolling back");
                    RollbackUnbinds(unbindStack);
                    unbindStack = new List<UnbindRecord>();
                    return false;
                }
                unbindStack.Add(rec);
            }
            if (unbindStack.Count > 0)
                Thread.Sleep(100); // settle after unbinds
        }
        else
        {
            Logger.WriteLine("GPUModeControl: dGPU BDF not resolvable - skipping sibling unbind step");
        }

        // Settle after DRM+PCI uevent and unbind so compositor/ICD have time to release.
        WaitForNvidiaRefcntSettle(10000);

        // Purge holders BEFORE touching the modules: rmmod fails with EBUSY
        // while any /dev/nvidia* fd or mapping exists, and the per-module
        // retry below only works once the holders are gone.
        NvidiaProcessScanner.InvalidateScanCache();
        NvidiaProcessScanner.KillAllHolders(force: true, out int killed, out int failed);
        if (killed + failed > 0)
        {
            Logger.WriteLine($"GPUModeControl: pre-rmmod purge killed={killed} failed={failed}");
            Thread.Sleep(200);
        }

        // Unload with a kill-then-retry convergence loop. A holder that
        // respawned (or a refcnt that drops asynchronously after the kill)
        // makes a single rmmod pass fail; later attempts purge again and
        // retry the WHOLE stack, not just the nvidia orphan.
        bool gone = false;
        const int MaxUnloadAttempts = 3;
        for (int attempt = 0; attempt < MaxUnloadAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Logger.WriteLine($"GPUModeControl: unload attempt {attempt + 1}/{MaxUnloadAttempts} - re-signaling DRM + re-purging holders");
                if (!string.IsNullOrEmpty(dgpuBdf))
                    TrySignalDrmRemove(dgpuBdf!);
                NvidiaProcessScanner.InvalidateScanCache();
                NvidiaProcessScanner.KillAllHolders(force: true, out _, out _);
                Thread.Sleep(300);
            }

            foreach (var m in NvidiaModules)
                RmmodOneModule(m);

            bool drmGone = !Directory.Exists(GPUModeControl.TestPathPrefix + "/sys/module/nvidia_drm");
            bool nvidiaGone = !Directory.Exists(GPUModeControl.TestPathPrefix + "/sys/module/nvidia");
            Logger.WriteLine($"GPUModeControl: attempt {attempt + 1}: nvidia_drm {(drmGone ? "unloaded" : "still loaded")}, nvidia {(nvidiaGone ? "unloaded" : "still loaded")}");
            if (drmGone && nvidiaGone)
            {
                gone = true;
                break;
            }
        }

        if (!gone)
        {
            LogNvidiaRefcntHolders();

            Logger.WriteLine("GPUModeControl: modules still loaded after release - rolling back unbinds");
            RollbackUnbinds(unbindStack);
            unbindStack = new List<UnbindRecord>();
        }
        return gone;
    }

    private static bool TryUnbindFunction(UnbindRecord rec)
    {
        if (DgpuPci.RunPciAction("pci-unbind", rec.DriverName, rec.Bdf, sudoTimeoutMs: 30000))
            return true;

        // The unbind write blocks in the kernel while GPU contexts of freshly
        // killed processes are torn down; our sudo timeout abandons the helper
        // but the write keeps running and usually completes moments later.
        // Declaring failure here while the kernel finishes the unbind leaves
        // the device driverless with no rollback record - poll for the late
        // completion before giving up.
        Logger.WriteLine($"GPUModeControl: unbind {rec.Bdf} timed out - polling for late completion");
        string driverLink = GPUModeControl.TestPathPrefix + $"/sys/bus/pci/devices/{rec.Bdf}/driver";
        int waited = 0;
        const int stepMs = 500;
        const int maxMs = 30000;
        while (waited < maxMs)
        {
            Thread.Sleep(stepMs);
            waited += stepMs;
            if (!Directory.Exists(driverLink))
            {
                Logger.WriteLine($"GPUModeControl: unbind {rec.Bdf} completed late after extra {waited}ms");
                return true;
            }
        }
        Logger.WriteLine($"GPUModeControl: unbind {rec.Bdf} never completed ({maxMs}ms extra wait)");
        return false;
    }

    private static bool TryRebindFunction(UnbindRecord rec)
        => DgpuPci.RunPciAction("pci-bind", rec.DriverName, rec.Bdf);

    private static void RollbackUnbinds(List<UnbindRecord> stack)
    {
        if (stack.Count == 0)
            return;
        // Rebind in reverse: graphics (.0) before audio (.1) so audio power gating works.
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            var rec = stack[i];
            string driverPath = GPUModeControl.TestPathPrefix + $"/sys/bus/pci/drivers/{rec.DriverName}";
            if (!Directory.Exists(driverPath))
            {
                Logger.WriteLine($"GPUModeControl: rollback skip {rec.Bdf} - driver {rec.DriverName} no longer registered (reboot to recover)");
                continue;
            }
            bool ok = TryRebindFunction(rec);
            Logger.WriteLine($"GPUModeControl: rollback rebind {rec.Bdf} -> {rec.DriverName} = {(ok ? "OK" : "FAILED")}");
        }
    }

    private const string NvidiaVulkanIcd = "/usr/share/vulkan/icd.d/nvidia_icd.json";

    private const string NvidiaEglVendor = "/usr/share/glvnd/egl_vendor.d/10_nvidia.json";

    /// <summary>
    /// Hide (Eco) / show (Standard) the NVIDIA Vulkan ICD and EGL vendor files
    /// so GL/Vulkan/EGL apps fall back cleanly to the iGPU when the dGPU is
    /// disabled. EGL is the main path Chromium-based browsers use to probe the
    /// GPU, and leaving it active while the dGPU is off causes them to
    /// open /dev/nvidia0 on launch (pins the driver next time around).
    /// Best-effort - never fails the switch.
    /// </summary>
    internal static void ApplyVulkanIcd(bool dgpuAvailable)
    {
        // NixOS: ICD/EGL vendor files live under /run/opengl-driver (read-only
        // store), not /usr/share. The FHS paths don't exist and can't be
        // renamed, so this is a no-op there.
        if (NixOS.IsNixOS)
            return;

        string verb = dgpuAvailable ? "show" : "hide";
        if (File.Exists(NvidiaVulkanIcd) || File.Exists(NvidiaVulkanIcd + "_inactive"))
            SysfsHelper.RunSudoOrPkexec(
                SysfsHelper.GpuHelperPath, new[] { "vulkan-icd", verb },
                sudoTimeoutMs: 5000, pkexecTimeoutMs: 30000);
        if (File.Exists(NvidiaEglVendor) || File.Exists(NvidiaEglVendor + "_inactive"))
            SysfsHelper.RunSudoOrPkexec(
                SysfsHelper.GpuHelperPath, new[] { "egl-vendor", verb },
                sudoTimeoutMs: 5000, pkexecTimeoutMs: 30000);
    }

    private static void LogNvidiaRefcntHolders()
    {
        try
        {
            var result = SysfsHelper.RunSudoOrPkexec(
                SysfsHelper.GpuHelperPath,
                new[] { "nvidia-refcnt-holders" },
                sudoTimeoutMs: 5000);
            if (!string.IsNullOrWhiteSpace(result))
            {
                foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Logger.WriteLine($"GPUModeControl: refcnt-holders: {line.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: refcnt-holders failed: {ex.Message}");
        }
    }

    private static void RmmodOneModule(string module)
    {
        const int maxTries = 7;
        for (int i = 0; i < maxTries; i++)
        {
            var (exitCode, stderr) = RunRmmod(module);
            if (exitCode == 0)
            {
                Logger.WriteLine($"GPUModeControl: rmmod {module} OK");
                return;
            }

            if (stderr.EndsWith("is not currently loaded\n", StringComparison.Ordinal)
                || stderr.EndsWith("is not currently loaded", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GPUModeControl: {module} not loaded, skipping");
                return;
            }
            if (stderr.EndsWith("is builtin.\n", StringComparison.Ordinal)
                || stderr.EndsWith("is builtin.", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GPUModeControl: {module} is builtin, cannot remove");
                return;
            }
            if (stderr.EndsWith("Permission denied\n", StringComparison.Ordinal)
                || stderr.EndsWith("Permission denied", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GPUModeControl: rmmod {module} permission denied: {stderr.Trim()}");
                return;
            }
            if (stderr.Contains($"Module {module} not found", StringComparison.Ordinal))
            {
                Logger.WriteLine($"GPUModeControl: module {module} not found");
                return;
            }

            if (i == maxTries - 1)
            {
                Logger.WriteLine($"GPUModeControl: rmmod {module} failed after {maxTries} tries: {stderr.Trim()}");
                return;
            }
            Thread.Sleep(50);
        }
    }

    private static (int exitCode, string stderr) RunRmmod(string module)
    {
        try
        {
            // Route through the root helper (helper execv's rmmod, so its
            // stderr/exit propagate verbatim and the parsing below still works).
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = SysfsHelper.SudoPath,
                Arguments = $"-n {SysfsHelper.GpuHelperPath} rmmod {module}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return (-1, "");
            if (!proc.WaitForExit(5000))
            {
                try
                { proc.Kill(); }
                catch { }
                return (-1, "timeout");
            }
            string stderr = proc.StandardError.ReadToEnd();
            return (proc.ExitCode, stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Send a synthetic DRM "remove" uevent for the dGPU so compositors
    /// (KWin, mutter) release /dev/dri/cardN gracefully before the real
    /// PCI unbind. gpu-helper refuses when no non-nvidia DRM card exists.
    /// </summary>
    private static void TrySignalDrmRemove(string dgpuBdf)
    {
        const int passes = 2;
        for (int i = 0; i < passes; i++)
        {
            try
            {
                var result = SysfsHelper.RunSudoOrPkexec(
                    SysfsHelper.GpuHelperPath,
                    new[] { "drm-notify-remove", dgpuBdf },
                    sudoTimeoutMs: 5000, pkexecTimeoutMs: 30000);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    Logger.WriteLine($"GPUModeControl: DRM uevent signaled (pass {i + 1}/{passes}): {result.Trim()}");
                    Thread.Sleep(500); // compositor needs time to release the device
                }
                else
                {
                    Logger.WriteLine($"GPUModeControl: DRM uevent signal pass {i + 1}/{passes} returned empty (no card entries or refused)");
                    break; // nothing to signal / refused - a retry won't change that
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: DRM uevent signal failed (non-fatal): {ex.Message}");
                break;
            }
        }
    }

    /// <summary>
    /// Bring back whitelisted user services (currently plasma-powerdevil) that
    /// the holder-kill flow stopped because they pinned the nvidia module via
    /// DDC/CI I2C fds. Runs unprivileged (`systemctl --user start`) and only
    /// AFTER the GPU transition has reached a terminal state - restarting
    /// earlier would re-open /dev/i2c-N and re-pin the module. No-op when the
    /// kill flow stopped nothing.
    /// </summary>
    internal static void RestartStoppedHolderServices()
    {
        try
        {
            var units = NvidiaProcessScanner.ConsumeStoppedRestartableUserUnits();
            foreach (var unit in units)
            {
                var r = SysfsHelper.RunCommandWithTimeout(
                    "systemctl", new[] { "--user", "start", unit }, 10000);
                Logger.WriteLine(r != null
                    ? $"GPUModeControl: restarted user service {unit} (stopped during holder kill)"
                    : $"GPUModeControl: restart of user service {unit} failed");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: RestartStoppedHolderServices failed: {ex.Message}");
        }
    }

    internal static void LogHoldersSnapshot(string reason)
    {
        try
        {
            var holders = NvidiaProcessScanner.ScanHolders();
            int refcnt = SysfsHelper.ReadInt("/sys/module/nvidia/refcnt", -1);
            if (holders.Count == 0)
            {
                Logger.WriteLine($"GPUModeControl: {reason} holders=0 nvidia/refcnt={refcnt}");
                var sys0 = NVidia.NvidiaProcessScanner.GetFilteredSystemProcesses();
                if (sys0.Count > 0)
                    Logger.WriteLine($"GPUModeControl: {reason} system holders (won't kill): [{string.Join(", ", sys0)}]");
                return;
            }
            var parts = new List<string>(holders.Count);
            foreach (var h in holders)
            {
                string detail = $"{h.Pid}:{h.Comm}({h.User})/{h.FdCount}fds";
                if (h.DriFdCount > 0)
                    detail += $"+{h.DriFdCount}dri";
                if (h.I2cFdCount > 0)
                    detail += $"+{h.I2cFdCount}i2c";
                parts.Add(detail);
            }
            Logger.WriteLine($"GPUModeControl: {reason} holders={holders.Count} nvidia/refcnt={refcnt} [{string.Join(", ", parts)}]");

            var sys = NVidia.NvidiaProcessScanner.GetFilteredSystemProcesses();
            if (sys.Count > 0)
                Logger.WriteLine($"GPUModeControl: {reason} system holders (won't kill): [{string.Join(", ", sys)}]");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: LogHoldersSnapshot failed: {ex.Message}");
        }
    }

    private static bool TryReleaseAmdDriver()
    {
        string? pciAddr = DgpuPci.FindAmd();
        if (pciAddr == null)
        {
            Logger.WriteLine("GPUModeControl: AMD dGPU PCI address not found");
            return false;
        }

        Logger.WriteLine($"GPUModeControl: attempting AMD dGPU PCI unbind+remove for {pciAddr}");

        // Cache the PCIe slot while the device is still present (Standard
        // re-enable re-powers it). amdgpu is NEVER rmmod'd - it also drives the
        // iGPU/display; instead each dGPU PCI function is unbound from its driver
        // and then removed from the bus (supergfxctl Device::remove()).
        DgpuPci.ResolveSlot();

        var funcs = DgpuPci.BoundFunctions(pciAddr);
        funcs.Reverse(); // highest function first (.1 audio before .0 graphics)
        foreach (var rec in funcs)
        {
            Logger.WriteLine($"GPUModeControl: unbinding {rec.Bdf} from {rec.DriverName}");
            DgpuPci.RunPciAction("pci-unbind", rec.DriverName, rec.Bdf);
            Logger.WriteLine($"GPUModeControl: removing {rec.Bdf} from PCI bus");
            DgpuPci.RunPciRemove(rec.Bdf);
        }

        // Verify: the graphics function should be gone (removed) or at least
        // have no driver bound.
        string driverLink = $"/sys/bus/pci/devices/{pciAddr}/driver";
        bool deviceGone = !Directory.Exists($"/sys/bus/pci/devices/{pciAddr}");
        if (deviceGone || (!File.Exists(driverLink) && !Directory.Exists(driverLink)))
        {
            Logger.WriteLine($"GPUModeControl: AMD dGPU {pciAddr} released ({(deviceGone ? "removed" : "unbound")})");
            return true;
        }

        Logger.WriteLine($"GPUModeControl: AMD dGPU {pciAddr} release may have failed");
        return false;
    }

    /// <summary>True if the NVIDIA kernel module is loaded.</summary>
    private static bool IsNvidiaGpu()
    {
        return Directory.Exists(GPUModeControl.TestPathPrefix + "/sys/module/nvidia");
    }

    /// <summary>True if an AMD discrete GPU is present (vendor=0x1002, boot_vga=0).</summary>
    private static bool IsAmdDgpu()
    {
        return DgpuPci.FindAmd() != null;
    }

    /// <summary>True once the nvidia driver actually owns the graphics function.</summary>
    private static bool IsNvidiaBound(string gfxBdf)
        => Directory.Exists(GPUModeControl.TestPathPrefix + $"/sys/bus/pci/drivers/nvidia/{gfxBdf}");

    private const int NvidiaLoadAttempts = 3;

    private const int NvidiaLoadSettleMs = 4000;

    // modprobe (10s) + settle (4s) + escalation + node wait (6s), with headroom.
    private const int NvidiaLoadAttemptBudgetSec = 30;

    /// <summary>
    /// modprobe nvidia and confirm it bound. A GPU whose rail is still
    /// settling answers config space but not MMIO, so NVRM reports the device
    /// "fell off the bus", probe returns -1 and module init fails with ENODEV.
    /// The module then unloads itself, so /sys/bus/pci/drivers/nvidia never
    /// exists and the pci-bind fallback cannot work either. Re-enumerate the
    /// device and retry instead of reporting a success that never happened.
    /// </summary>
    private static bool LoadNvidiaWithRetry(string gfxBdf)
    {
        for (int attempt = 1; attempt <= NvidiaLoadAttempts; attempt++)
        {
            // The whole ladder can outlast the caller's opening pause, and a
            // query landing on a GPU whose link is still down is what wedges
            // nvidia-smi in D-state. Refresh the window on every attempt.
            NVidia.GpuQueryGate.Extend(TimeSpan.FromSeconds(NvidiaLoadAttemptBudgetSec),
                $"nvidia load attempt {attempt}");

            SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath,
                new[] { "modprobe", "nvidia" }, sudoTimeoutMs: 10000);

            // A device that never left the bus is not re-probed by modprobe
            // alone - bind it explicitly.
            DgpuPci.EnsureBound(gfxBdf, isAmd: false);

            if (IsNvidiaBound(gfxBdf))
            {
                Logger.WriteLine($"GPUModeControl: nvidia bound to {gfxBdf} on attempt {attempt}");
                return true;
            }

            if (attempt == NvidiaLoadAttempts)
                break;

            Logger.WriteLine($"GPUModeControl: nvidia probe failed (attempt {attempt}/{NvidiaLoadAttempts}) - re-enumerating");
            Thread.Sleep(NvidiaLoadSettleMs);

            if (attempt == 1)
            {
                // Cheap retry: drop just the graphics function and re-add it.
                DgpuPci.WakeBridge();
                DgpuPci.RunPciRemove(gfxBdf);
                DgpuPci.PowerOnSlot();
                SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
            }
            else
            {
                // A root port left in D3cold enumerates the dGPU from stale
                // config space with the link still down, so config reads answer
                // but MMIO returns all-ones. Only a full re-enumeration of the
                // hierarchy retrains the link.
                DgpuPci.ResetBridge();
            }

            DgpuPci.WaitForNode(6000);
        }
        return false;
    }

    internal static bool HasNvidiaDaemonsInstalled()
        => File.Exists("/usr/lib/systemd/system/nvidia-powerd.service")
        || File.Exists("/etc/systemd/system/nvidia-powerd.service")
        || File.Exists("/lib/systemd/system/nvidia-powerd.service");

    /// <summary>
    /// nvidia-powerd samples the firmware TDP limits when it starts, so a
    /// change to nv_dynamic_boost / nv_temp_target / nv_base_tgp / nv_tgp is
    /// ignored until it restarts. No-op when the daemon is absent or stopped.
    /// </summary>
    internal static void RefreshNvidiaPowerd()
    {
        if (!HasNvidiaDaemonsInstalled())
            return;

        // Best-effort only. This runs on unattended AC/battery mode switches,
        // so it must never escalate to a pkexec prompt; skip when sudo says no.
        var r = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath,
            new[] { "daemon", "try-restart", "nvidia-powerd" },
            sudoTimeoutMs: 5000, allowPkexec: false);
        Logger.WriteLine(r != null
            ? "GPUModeControl: nvidia-powerd re-read GPU TDP limits"
            : "GPUModeControl: nvidia-powerd try-restart skipped (not running or not permitted)");
    }

    /// <summary>
    /// Start or stop nvidia-powerd to match the current power source, when the
    /// user opted in. On AC it is always started, so turning the option off
    /// while on battery does not leave the daemon down until the next unplug.
    /// </summary>
    internal static void ApplyNvidiaPowerdPolicy(bool onAc)
    {
        if (!HasNvidiaDaemonsInstalled())
            return;

        // ebpf filter on: the daemons would fail against hidden device nodes.
        if (AppConfig.IsBpfGpuBackend() && Block.GpuBlockControl.IsActive())
            return;

        bool stopOnBattery = AppConfig.Is("nvidia_powerd_battery");
        if (!stopOnBattery && !onAc)
            return;

        string verb = onAc ? "start" : "stop";
        SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath,
            new[] { "daemon", "reset-failed", "nvidia-powerd" },
            sudoTimeoutMs: 5000, allowPkexec: false);
        var r = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath,
            new[] { "daemon", verb, "nvidia-powerd" },
            sudoTimeoutMs: 5000, allowPkexec: false);
        Logger.WriteLine(r != null
            ? $"GPUModeControl: nvidia-powerd {verb} (AC={onAc})"
            : $"GPUModeControl: nvidia-powerd {verb} skipped (not permitted or already {verb}ed)");
    }

    private static bool WaitForNvidiaModule(int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (Directory.Exists(GPUModeControl.TestPathPrefix + "/sys/module/nvidia"))
                return true;
            Thread.Sleep(100);
            waited += 100;
        }
        return false;
    }

    private static void RestartNvidiaDaemons()
    {
        SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "reset-failed", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        var r1 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "start", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r1 != null
            ? "GPUModeControl: started nvidia-persistenced"
            : "GPUModeControl: nvidia-persistenced start failed (unit missing or rate-limited)");

        SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "reset-failed", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        var r2 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "start", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r2 != null
            ? "GPUModeControl: started nvidia-powerd"
            : "GPUModeControl: nvidia-powerd start failed (unit missing or rate-limited)");
    }

    /// <summary>Load the driver for a dGPU back on the bus; false when nvidia never binds (reboot needed).</summary>
    internal static bool Load()
    {
        // Device is back but the driver is not up yet; cover the load below
        // (the nvidia path extends this again per attempt).
        NVidia.GpuQueryGate.Extend(TimeSpan.FromSeconds(NvidiaLoadAttemptBudgetSec),
            "dGPU present - loading driver");

        var dgpuDev = DgpuPci.Find();
        bool isAmd = dgpuDev?.vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase) == true;
        if (isAmd)
        {
            // amdgpu also drives the iGPU, so it is never rmmod'd; after the
            // device re-enumerates, load it explicitly (udev coldplug is the
            // backup). Mirrors gpu-block-helper.sh live-standard.
            Logger.WriteLine("GPUModeControl: AMD dGPU present - loading amdgpu");
            SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "modprobe", "amdgpu" }, sudoTimeoutMs: 10000);
            if (dgpuDev != null)
                DgpuPci.EnsureBound(dgpuDev.Value.bdf, isAmd: true);
        }
        else
        {
            Logger.WriteLine("GPUModeControl: nvidia dGPU present - loading nvidia");
            if (dgpuDev != null && !LoadNvidiaWithRetry(dgpuDev.Value.bdf))
            {
                Logger.WriteLine("GPUModeControl: nvidia never bound to the dGPU - reboot required");
                return false;
            }

            // Eco transition stopped these daemons; Standard must restart them
            // (supergfxctl actions.rs:enable_nvidia_persistenced + enable_nvidia_powerd).
            // Wait for kernel autoload of the nvidia module (needs /dev/nvidiactl).
            if (HasNvidiaDaemonsInstalled())
            {
                if (WaitForNvidiaModule(5000))
                    RestartNvidiaDaemons();
                else
                    Logger.WriteLine("GPUModeControl: nvidia module did not load within 5s - skipping daemon restart");
            }
        }
        return true;
    }
}

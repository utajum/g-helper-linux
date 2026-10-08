using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu;

/// <summary>
/// GPU mode: the 4 user-visible modes in the UI.
/// </summary>
public enum GpuMode
{
    Eco = 0,
    Standard = 1,
    Optimized = 2,
    Ultimate = 3
}

/// <summary>
/// Result of a GPU mode switch attempt. The UI uses this to decide
/// what notification/dialog/tip to show.
/// </summary>
public enum GpuSwitchResult
{
    /// <summary>Mode applied immediately - hardware state changed.</summary>
    Applied,
    /// <summary>Hardware already in the desired state - no write needed.</summary>
    AlreadySet,
    /// <summary>MUX change latched - reboot required to take effect.</summary>
    RebootRequired,
    /// <summary>dGPU driver is active - cannot safely write dgpu_disable=1.
    /// UI should show confirmation dialog (Switch Now / After Reboot / Cancel).</summary>
    DriverBlocking,
    /// <summary>Mode saved to config for next reboot (user chose "After Reboot").</summary>
    Deferred,
    /// <summary>Write failed (sysfs error, permission denied, etc.).</summary>
    Failed,
    /// <summary>Eco mode blocked - MUX was set to 0 (Ultimate) this boot session. Reboot first.</summary>
    EcoBlocked,
    /// <summary>dgpu_disable=0 was written but the dGPU did not re-enumerate on the
    /// PCI bus after rescan (slow/stuck firmware). UI should advise a reboot.</summary>
    DgpuReenableFailed
}

/// <summary>
/// Centralized GPU mode switching controller.
///
/// Every danger in this system comes from a single operation: writing dgpu_disable=1
/// while the dGPU driver is loaded and active. Everything else is either instant or
/// just requires a reboot. The entire safety architecture exists to protect that one write.
///
/// Architecture: button handlers and tray menu call this controller. They never write
/// sysfs directly. The controller reads current hardware state, computes the delta,
/// and executes the needed operations with safety checks.
///
/// See GPU_MODE_PLAN.md for the complete scenario matrix and flows.
/// </summary>
public class GPUModeControl
{
    private readonly IHardwareControl _wmi;
    private readonly IPowerManager _power;

    /// <summary>Lock to prevent concurrent GPU mode operations.
    /// Writing dgpu_disable can block for 30-60 seconds - we must not queue multiple writes.
    /// SemaphoreSlim(1,1) provides atomic check-and-acquire, eliminating the TOCTOU race
    /// that existed with the previous volatile bool approach.</summary>
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    /// <summary>
    /// Tracks pending MUX latch value within this session.
    /// After SetGpuMuxMode(x), hardware still reports the OLD value until reboot.
    /// This field remembers what we latched so ComputeAndExecute() and ScheduleModeForReboot()
    /// use the correct effective MUX, not the stale hardware readback.
    /// -1 = no pending latch (use hardware value).
    /// </summary>
    private volatile int _pendingMuxLatch = -1;

    /// <summary>Cached dGPU PCI address for AMD systems (e.g., "0000:01:00.0").</summary>
    private string? _cachedDgpuPciAddress;
    private bool _dgpuPciScanned;

    /// <summary>
    /// Invoked after the controller mutates PCI bus topology in-process
    /// (the live PCI Eco-to-Standard transition rescans /sys/bus/pci so
    /// the dGPU reappears without a reboot).
    /// </summary>
    public static Action? OnLivePciTransition;

    /// <summary>
    /// Invoked after the dGPU is re-enabled to re-apply (or reset) the current
    /// mode's GPU tuning. Wired by App to ModeControl; left null in headless
    /// contexts (tests) so the controller stays free of UI-layer dependencies.
    /// </summary>
    public static Action? OnReapplyGpuTuning;

    public GPUModeControl(IHardwareControl wmi, IPowerManager power)
    {
        _wmi = wmi;
        _power = power;
    }

    /// <summary>True if a GPU mode switch is currently in progress (sysfs write blocking).</summary>
    public bool IsSwitchInProgress => _switchLock.CurrentCount == 0;

    /// <summary>
    /// Return the effective MUX value: if we latched a change this session, return
    /// the latched value. Otherwise return actual hardware state.
    /// Used by ComputeAndExecute(), ExecuteDisableDgpu(), ScheduleModeForReboot()
    /// methods that need to know "what MUX will be after reboot".
    /// NOT used by GetCurrentMode(), AutoGpuSwitch(), IsPendingReboot(),
    /// ApplyPendingOnStartup(), ApplyPendingOnShutdown() - those need actual hardware.
    /// </summary>
    private int GetEffectiveMux()
    {
        int pending = _pendingMuxLatch;
        return pending >= 0 ? pending : _wmi.GetGpuMuxMode();
    }

    /// <summary>
    /// Check if MUX=0 was written at any point during the current boot session.
    /// Uses persistent storage (AppConfig) keyed by boot_id, so it survives app restarts
    /// within the same boot session but auto-clears after reboot.
    /// </summary>
    private static bool IsMuxZeroLatchedThisBoot()
    {
        try
        {
            string? stored = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(stored))
                return false;
            string current = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return stored == current;
        }
        catch (IOException)
        {
            // /proc/sys/kernel/random/boot_id not readable (container/chroot) - fail-open
            return false;
        }
    }

    /// <summary>
    /// Persist the MUX=0 latch flag for the current boot session.
    /// Called after every successful SetGpuMuxMode(0) write.
    /// The flag stays set until reboot (boot_id changes).
    /// </summary>
    private static void SetMuxZeroLatchFlag()
    {
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            AppConfig.Set("mux_zero_latched_boot_id", bootId);
            Logger.WriteLine($"GPUModeControl: MUX=0 latch persisted - boot_id={bootId}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: failed to persist MUX=0 latch flag: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the persistent MUX=0 latch flag (config "mux_zero_latched_boot_id")
    /// when the stored boot_id no longer matches /proc/sys/kernel/random/boot_id.
    /// The flag is intentionally session-scoped - it exists to make
    /// WouldCreateImpossibleState resilient to app restarts within the same
    /// boot, but must NOT leak across reboots or across backend switches.
    ///
    /// Called from ApplyPendingOnStartup before any backend-specific code so
    /// PCI users also benefit; otherwise WouldCreateImpossibleState would
    /// permanently refuse Eco on any system that previously latched Ultimate.
    /// </summary>
    private static void ClearStaleMuxLatchFlag()
    {
        try
        {
            string? storedBootId = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(storedBootId))
                return;
            string currentBootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            if (storedBootId != currentBootId)
            {
                AppConfig.Set("mux_zero_latched_boot_id", "");
                Logger.WriteLine("GPUModeControl: reboot detected - cleared MUX=0 latch flag");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: MUX=0 latch reboot check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// SAFETY: Would the given target mode create an impossible post-reboot state?
    /// The ONE impossible state: MUX=0 (dGPU sole display) + dgpu_disable=1 (dGPU off)
    /// = guaranteed black screen.
    ///
    /// Defense in depth: checks BOTH the persistent boot_id flag (survives app restart)
    /// AND the in-memory effective MUX latch (fast path, same session).
    /// Either check returning true blocks Eco.
    /// </summary>
    private bool WouldCreateImpossibleState(GpuMode target)
    {
        if (target != GpuMode.Eco)
            return false;

        // Check persistent flag first (survives app restart within same boot)
        if (IsMuxZeroLatchedThisBoot())
        {
            Logger.WriteLine("GPUModeControl: IMPOSSIBLE STATE PREVENTED - MUX=0 was written this boot session (persistent flag). Eco + MUX=0 = black screen.");
            return true;
        }

        // Also check in-memory latch (fast path, same session - defense in depth)
        int effectiveMux = GetEffectiveMux();
        if (effectiveMux == 0)
        {
            Logger.WriteLine("GPUModeControl: IMPOSSIBLE STATE PREVENTED - cannot schedule Eco when MUX is latched to 0 (Ultimate). Eco + MUX=0 = black screen.");
            return true;
        }
        return false;
    }

    // Public API

    /// <summary>
    /// The single entry point for all GPU mode switches (buttons + tray menu).
    /// Determines current hardware state, computes needed operations, executes
    /// with safety checks. Returns result for UI to act on.
    ///
    /// NOTE: This may block for 30-60 seconds (dgpu_disable write).
    /// Always call from a background thread (Task.Run), never from the UI thread.
    /// </summary>
    public GpuSwitchResult RequestModeSwitch(GpuMode target)
    {
        if (!_switchLock.Wait(0))
        {
            // A hardware switch is blocking - can't start another.
            // But save the user's latest choice so it applies after reboot.
            // This ensures rapid clicks always result in the LAST choice winning.
            Logger.WriteLine($"GPUModeControl: switch in progress, scheduling {target} for reboot");
            var scheduleResult = ScheduleModeForReboot(target);
            if (scheduleResult == GpuSwitchResult.EcoBlocked)
                return GpuSwitchResult.EcoBlocked;
            return GpuSwitchResult.Deferred;
        }

        try
        {
            Logger.WriteLine($"GPUModeControl: RequestModeSwitch → {target}");

            // Let pending fan/PPT EC writes settle before dgpu_disable
            App.Mode?.WaitForApply();

            var result = ComputeAndExecute(target);

            // Sync the on-disk persistent marker with the current mode.
            // Entering Eco writes the marker so the boot script re-applies Eco.
            // Leaving Eco removes the marker so the boot script won't force Eco.
            // The config/checkbox is NOT touched here (user preference survives).
            if (IsEcoPersistentConfig())
                SyncPersistentMarkerToDisk(target == GpuMode.Eco);

            // If the user has the AURA "GPU Mode" color effect selected,
            // refresh the keyboard so the new GPU mode color is visible
            // immediately. Failure here is non-fatal - log and continue.
            if (result == GpuSwitchResult.Applied || result == GpuSwitchResult.AlreadySet)
            {
                try
                {
                    if ((USB.AuraMode)AppConfig.Get("aura_mode") == USB.AuraMode.GpuMode)
                        USB.CustomRgb.ApplyGpuColor();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GPUModeControl: AURA GpuMode refresh failed: {ex.Message}");
                }
            }

            // GPU mode switches are memory-heavy (process scanning, driver teardown).
            // Release working set once the dust settles.
            Helpers.MemoryHelper.TrimAfter();

            return result;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: RequestModeSwitch({target}) failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>
    /// Attempt to release the GPU driver (pkexec rmmod / PCI unbind) and then
    /// write dgpu_disable=1. Called from the "Switch Now" confirmation dialog button.
    ///
    /// NOTE: This may show a polkit password dialog and block for a while.
    /// Always call from a background thread.
    /// </summary>
    public GpuSwitchResult TryReleaseAndSwitch()
    {
        if (!_switchLock.Wait(0))
            return GpuSwitchResult.AlreadySet;

        try
        {
            Logger.WriteLine("GPUModeControl: TryReleaseAndSwitch - attempting driver release");
            LogHoldersSnapshot("pre-release");

            // PCI backend: no firmware dgpu_disable. After driver release,
            // write block files + PCI-remove dGPU functions from the bus.
            if (AppConfig.IsPciGpuBackend())
            {
                // MUX=0 (live/latched) → dGPU drives the panel; removing it blanks it.
                if (WouldCreateImpossibleState(GpuMode.Eco))
                    return GpuSwitchResult.EcoBlocked;

                bool pciReleased = TryReleaseGpuDriver();
                if (!pciReleased)
                {
                    Logger.WriteLine("GPUModeControl: PCI TryRelease - driver release failed, deferring to reboot");
                    SaveModeToConfig(GpuMode.Eco);
                    WriteDriverBlock(GpuMode.Eco);
                    return GpuSwitchResult.Deferred;
                }

                WriteDriverBlock(GpuMode.Eco);
                PciRemoveDgpuFunctions();
                OnLivePciTransition?.Invoke();
                ApplyVulkanIcd(dgpuAvailable: false);
                SaveModeToConfig(GpuMode.Eco);
                Logger.WriteLine("GPUModeControl: PCI Eco applied live (rmmod + PCI remove)");
                return GpuSwitchResult.Applied;
            }

            bool physicallyUltimate = _wmi.GetGpuMuxMode() == 0;
            if (physicallyUltimate)
                Logger.WriteLine("GPUModeControl: MUX=0 (Ultimate) - skipping live release, deferring Eco to reboot");
            bool released = !physicallyUltimate && TryReleaseGpuDriver();
            if (!released)
            {
                Logger.WriteLine("GPUModeControl: driver release failed - deferring to reboot");
                SaveModeToConfig(GpuMode.Eco);
                // If we're in Ultimate (MUX=0), latch MUX→1 first so next boot can apply Eco
                int effectiveMux = GetEffectiveMux();
                if (effectiveMux == 0)
                {
                    // gpu_mux_mode write fails when dgpu_disable=1 - enable dGPU first if needed
                    bool ecoActive = _wmi.GetGpuEco();
                    if (ecoActive)
                    {
                        Logger.WriteLine("GPUModeControl: TryRelease - enabling dGPU before MUX latch");
                        try
                        { _wmi.SetGpuEco(false); RemoveDriverBlock(); }
                        catch (Exception muxEx)
                        {
                            Logger.WriteLine($"GPUModeControl: TryRelease - exit Eco failed: {muxEx.Message}");
                        }
                    }
                    Logger.WriteLine("GPUModeControl: MUX=0, latching MUX→1 for Eco boot");
                    try
                    {
                        _wmi.SetGpuMuxMode(1);
                        _pendingMuxLatch = 1;
                    }
                    catch (Exception muxEx)
                    {
                        Logger.WriteLine($"GPUModeControl: TryRelease - MUX latch failed: {muxEx.Message}");
                    }
                }
                // pkexec auth is cached from rmmod attempt - write block without re-prompting
                WriteDriverBlock(GpuMode.Eco);
                return GpuSwitchResult.Deferred;
            }

            // Driver released - now write dgpu_disable=1 (should be fast)
            Logger.WriteLine("GPUModeControl: driver released, writing dgpu_disable=1");
            DropDgpuPciNodes();
            _wmi.SetGpuEco(true);

            // Verify
            if (_wmi.GetGpuEco())
            {
                SaveModeToConfig(GpuMode.Eco);
                // Eco applied live - remove block artifacts (dgpu_disable=1 is persistent)
                RemoveDriverBlock();
                // Hide the NVIDIA Vulkan ICD while the dGPU is disabled.
                ApplyVulkanIcd(dgpuAvailable: false);
                Logger.WriteLine("GPUModeControl: Eco mode applied after driver release");
                return GpuSwitchResult.Applied;
            }
            else
            {
                Logger.WriteLine("GPUModeControl: dgpu_disable write succeeded but readback != 1");
                SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.Deferred;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: TryReleaseAndSwitch failed: {ex.Message}");
            SaveModeToConfig(GpuMode.Eco);
            return GpuSwitchResult.Deferred;
        }
        finally
        {
            RestartStoppedHolderServices();
            _switchLock.Release();
        }
    }

    /// <summary>
    /// Save desired mode to config for next reboot. Latches any MUX changes.
    /// Called from the "After Reboot" confirmation dialog button.
    /// Does NOT write dgpu_disable.
    /// Returns the result so callers can distinguish EcoBlocked from RebootRequired.
    /// </summary>
    public GpuSwitchResult ScheduleModeForReboot(GpuMode target)
    {
        Logger.WriteLine($"GPUModeControl: ScheduleModeForReboot({target})");

        // SAFETY: If scheduling Eco but MUX is latched to 0, the user changed from
        // Ultimate to Eco without rebooting. After reboot MUX=0 + dgpu_disable=1 = black screen.
        // Refuse: keep config as-is, remove any stale Eco artifacts.
        if (WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GPUModeControl: ScheduleModeForReboot REFUSED - would create impossible post-reboot state (Eco + MUX=0)");
            Logger.WriteLine("GPUModeControl: user must reboot into Ultimate first, THEN switch to Eco");
            RemoveDriverBlock();
            return GpuSwitchResult.EcoBlocked;
        }

        SaveModeToConfig(target);

        // If target needs MUX change, latch it now (instant, safe)
        // Use GetEffectiveMux() - if we already latched a MUX change this session,
        // we need to know the LATCHED value, not the stale hardware readback.
        int effectiveMux = GetEffectiveMux();
        int targetMux = (target == GpuMode.Ultimate) ? 0 : 1;

        if (effectiveMux >= 0 && effectiveMux != targetMux)
        {
            // gpu_mux_mode write fails when dgpu_disable=1 - enable dGPU first
            bool ecoEnabled = _wmi.GetGpuEco();
            if (ecoEnabled)
            {
                Logger.WriteLine("GPUModeControl: ScheduleModeForReboot - enabling dGPU before MUX latch");
                try
                {
                    _wmi.SetGpuEco(false);
                    RemoveDriverBlock();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GPUModeControl: ScheduleModeForReboot - failed to exit Eco: {ex.Message}");
                    // Can't latch MUX, but config is saved - ApplyPendingOnStartup will retry
                    WriteDriverBlock(target);
                    return GpuSwitchResult.RebootRequired;
                }
            }

            Logger.WriteLine($"GPUModeControl: latching MUX {effectiveMux} → {targetMux}");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
                _pendingMuxLatch = targetMux;
                if (targetMux == 0)
                    SetMuxZeroLatchFlag();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: ScheduleModeForReboot - MUX write failed: {ex.Message}");
                // Config is saved - ApplyPendingOnStartup will retry
            }
        }

        // Write driver block so Eco can be applied safely after reboot.
        // For non-Eco targets, this removes any stale block artifacts.
        WriteDriverBlock(target);
        return GpuSwitchResult.RebootRequired;
    }

    /// <summary>
    /// On startup: check if config has a pending GPU mode that differs from
    /// current hardware state. If so, try to apply it.
    ///
    /// NOTE: This may block. Call from a background thread.
    /// </summary>
    public GpuSwitchResult ApplyPendingOnStartup()
    {
        // Clear stale MUX=0 latch flag on reboot detection. Runs FIRST,
        // before any backend-specific path, because the persistent flag is
        // session-scoped and must not leak across boots regardless of which
        // backend the user is on. Skipping this lets WouldCreateImpossibleState
        // misfire (Eco refused with "MUX=0 was written this boot session"
        // even though we are in a fresh boot or in PCI mode where MUX is
        // irrelevant).
        ClearStaleMuxLatchFlag();

        if (AppConfig.NoGpu() || AppConfig.IsAMDiGPU())
        {
            Logger.WriteLine("GPUModeControl: APU-only system (NoGpu/IsAMDiGPU) - skipping startup GPU probe");
            return GpuSwitchResult.AlreadySet;
        }

        // PCI backend: the boot service is solely responsible for applying
        // any pending mode at boot. By the time ghelper starts up, the
        // transition has already happened (or failed and been recorded in
        // /etc/ghelper/last-eco-failed). Just sync config with the actual
        // file state so the UI shows the right active mode, no firmware
        // pokes needed.
        if (AppConfig.IsPciGpuBackend())
        {
            // Do NOT touch mux_zero_latched_boot_id here. The shared
            // ClearStaleMuxLatchFlag() above already cleared it on a
            // cross-boot stale match; anything still set is from THIS
            // boot session and represents a genuine pending firmware
            // latch that must keep blocking PCI Eco until the user
            // reboots (else the next boot lands in MUX=0 + udev-removed
            // dGPU = black screen). Same logic for _pendingMuxLatch.

            GpuMode actual = GetCurrentMode();
            string? saved = AppConfig.GetString("gpu_mode");
            if (saved != actual.ToString().ToLowerInvariant())
            {
                SaveModeToConfig(actual);
                Logger.WriteLine($"GPUModeControl: PCI backend startup - synced config gpu_mode='{actual}' to match block-file state");
            }
            return GpuSwitchResult.AlreadySet;
        }

        // Boot safety check (supergfxctl pattern)
        // If MUX=0 (Ultimate/dGPU-direct) AND dgpu_disable=1, that's an impossible
        // state that causes boot hangs. Force dgpu_disable=0 to recover.
        // This shouldn't happen with the modprobe.d approach but could occur from
        // manual sysfs tinkering or stale tmpfiles from a previous version.
        BootSafetyCheck();

        string? savedMode = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(savedMode))
        {
            // No pending mode - clean up any stale block artifacts (crash, uninstall, etc.)
            RemoveDriverBlock();
            return GpuSwitchResult.AlreadySet;
        }

        GpuMode target = ParseGpuMode(savedMode);
        GpuMode current = GetCurrentMode();

        // Check if hardware matches desired mode
        bool ecoEnabled = _wmi.GetGpuEco();
        int mux = _wmi.GetGpuMuxMode();

        bool needsDgpuChange = false;
        bool needsMuxChange = false;

        // Eco half-state detection: firmware reports
        // dgpu_disable=1 but the dGPU driver is still loaded. This happens
        // when firmware fails to actually power down the dGPU. Log it so
        // diagnostics can spot the mismatch.
        if (ecoEnabled && IsDgpuDriverActive())
        {
            Logger.WriteLine("GPUModeControl: startup - Eco half-state detected (dgpu_disable=1 but dGPU driver active)");
        }

        if (target == GpuMode.Eco && !ecoEnabled)
        {
            if (mux == 0)
            {
                // MUX=0 (Ultimate) - kernel refuses dgpu_disable=1 in this mode.
                // Latch MUX=1 first, then Eco will apply on the NEXT reboot.
                needsMuxChange = true;
            }
            else
            {
                needsDgpuChange = true;
            }
        }
        else if (target == GpuMode.Ultimate && mux != 0)
            needsMuxChange = true;
        else if (target == GpuMode.Standard && mux == 0)
            needsMuxChange = true;
        else if (target == GpuMode.Optimized)
        {
            // Optimized needs MUX=1 first
            if (mux == 0)
                needsMuxChange = true;
            // Optimized with hardware in Eco - need to enable dGPU
            else if (ecoEnabled)
                needsDgpuChange = true;
        }
        else if ((target == GpuMode.Standard) && ecoEnabled)
        {
            // Config says Standard but hardware is Eco (rapid-click override scenario).
            // Need to enable dGPU (dgpu_disable=0).
            needsDgpuChange = true;
        }

        if (!needsDgpuChange && !needsMuxChange)
        {
            Logger.WriteLine($"GPUModeControl: startup - hardware matches saved mode '{savedMode}'");
            // Hardware matches. For persistent Eco, keep the modprobe+udev blocks
            // so the next boot is protected even if try_release_nvidia fails.
            // For one-shot Eco or non-Eco modes, clean up stale artifacts.
            // Check the on-disk marker (not config) because config stays true
            // even when the user is in Standard.
            if (!IsEcoPersistentOnDisk())
                RemoveDriverBlock();

            // Model-based persistent Eco: if firmware is known to forget dgpu_disable
            // across reboots, auto-enable the persistent marker so the boot service
            // re-applies Eco on every startup.
            if (target == GpuMode.Eco && AppConfig.IsEcoBootFixModel() && !IsEcoPersistentConfig())
            {
                Logger.WriteLine("GPUModeControl: startup - model requires persistent Eco, auto-enabling");
                SetEcoPersistent(true);
            }

            return GpuSwitchResult.AlreadySet;
        }

        if (needsMuxChange)
        {
            // If in Eco, must enable dGPU before MUX change
            // (firmware rejects gpu_mux_mode write when dgpu_disable=1)
            if (ecoEnabled)
            {
                Logger.WriteLine("GPUModeControl: startup - enabling dGPU before MUX change");
                try
                {
                    _wmi.SetGpuEco(false);
                    RemoveDriverBlock();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GPUModeControl: startup - failed to exit Eco: {ex.Message}");
                    return GpuSwitchResult.Failed;
                }
            }

            int targetMux = (target == GpuMode.Ultimate) ? 0 : 1;
            Logger.WriteLine($"GPUModeControl: startup - latching MUX → {targetMux} for '{savedMode}'");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: startup - MUX write failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }

        // needsDgpuChange - two directions:
        // (a) Target is Eco but hardware is not Eco → disable dGPU
        // (b) Target is Standard/Optimized but hardware is Eco → enable dGPU
        bool targetEco = (target == GpuMode.Eco);

        if (targetEco)
        {
            // Direction (a): Apply pending Eco
            Logger.WriteLine("GPUModeControl: startup - applying pending Eco mode");

            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GPUModeControl: startup - dGPU driver active, cannot apply Eco");
                // MUX is correct (1) but dGPU driver is loaded - write block so NEXT boot
                // driver won't load, then ghelper can write dgpu_disable=1 safely.
                // This breaks the infinite loop: startup → driver active → can't apply → repeat.
                WriteDriverBlock(GpuMode.Eco);
                return GpuSwitchResult.DriverBlocking;
            }

            // Driver not active - safe to write
            if (!_switchLock.Wait(0))
            {
                Logger.WriteLine("GPUModeControl: startup - switch lock contention, skipping");
                return GpuSwitchResult.Failed;
            }
            try
            {
                _wmi.SetGpuEco(true);

                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GPUModeControl: startup - Eco mode applied successfully");
                    if (!IsEcoPersistentOnDisk())
                        RemoveDriverBlock();
                    return GpuSwitchResult.Applied;
                }
                else
                {
                    Logger.WriteLine("GPUModeControl: startup - dgpu_disable write failed readback");
                    return GpuSwitchResult.Failed;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: startup apply failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
        else
        {
            // Direction (b): Enable dGPU for pending Standard/Optimized
            // Hardware is in Eco (dgpu_disable=1) but config says Standard/Optimized.
            // This happens when rapid clicks override a blocking Eco switch.
            Logger.WriteLine($"GPUModeControl: startup - enabling dGPU for pending {target} (hardware is Eco)");

            if (!_switchLock.Wait(0))
            {
                Logger.WriteLine("GPUModeControl: startup - switch lock contention, skipping");
                return GpuSwitchResult.Failed;
            }
            try
            {
                _wmi.SetGpuEco(false); // Always safe - enables dGPU
                RemoveDriverBlock();    // Clean up stale block artifacts
                Logger.WriteLine($"GPUModeControl: startup - dGPU enabled for {target}");
                return GpuSwitchResult.Applied;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: startup enable dGPU failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
    }

    /// <summary>
    /// On shutdown (SIGTERM/SIGINT): best-effort dgpu_disable=1 if Eco is pending.
    /// Still checks driver safety - SIGINT is not a system shutdown (Xorg is still running),
    /// and even during SIGTERM the display stack may not have released the GPU yet.
    /// If driver is active, skip - ApplyPendingOnStartup() will try on next boot.
    /// </summary>
    public void ApplyPendingOnShutdown()
    {
        try
        {
            // PCI backend: the boot service applies pending modes on the
            // NEXT startup, not on shutdown. There is no live dgpu_disable
            // path to take here. Skip silently so we don't accidentally
            // call into the WMI sysfs layer on non-ASUS systems where it
            // does not exist.
            if (AppConfig.IsPciGpuBackend())
                return;

            string? savedMode = AppConfig.GetString("gpu_mode");
            if (savedMode != "eco")
                return;

            bool ecoEnabled = _wmi.GetGpuEco();
            if (ecoEnabled)
                return; // Already in Eco

            // MUX=0 guard - kernel refuses dgpu_disable=1 when in Ultimate mode
            int mux = _wmi.GetGpuMuxMode();
            if (mux == 0)
            {
                Logger.WriteLine("GPUModeControl: shutdown - MUX=0 (Ultimate), cannot write dgpu_disable");
                Logger.WriteLine("GPUModeControl: Eco mode requires MUX=1 first - will handle on next startup");
                return;
            }

            // Safety check - same as everywhere else.
            // Writing dgpu_disable=1 while the driver is active triggers ACPI PCI hot-removal
            // which causes a kernel panic (NULL deref in nvidia_modeset when Xorg still has the GPU).
            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GPUModeControl: shutdown - dGPU driver still active, skipping dgpu_disable write");
                Logger.WriteLine("GPUModeControl: Eco mode will be applied on next startup instead");
                return;
            }

            Logger.WriteLine("GPUModeControl: shutdown - driver idle, writing dgpu_disable=1 for pending Eco");
            _wmi.SetGpuEco(true);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: shutdown apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Optimized mode auto Eco/Standard switch on power state change.
    /// Same safety as RequestModeSwitch but never shows a dialog - returns
    /// DriverBlocking for caller to show a notification instead.
    ///
    /// NOTE: May block for 30-60 seconds. Call from background thread.
    /// </summary>
    public GpuSwitchResult AutoGpuSwitch()
    {
        if (!AppConfig.IsOptimizedGpuModeEnabled())
            return GpuSwitchResult.AlreadySet;

        if (!AppConfig.Is("gpu_auto"))
            return GpuSwitchResult.AlreadySet;

        // PCI backend has no live switching path - Optimized auto-toggle is
        // meaningless when every transition requires a reboot. The UI hides
        // the Optimized button in PCI mode, so this is a defensive guard.
        if (AppConfig.IsPciGpuBackend())
        {
            Logger.WriteLine("GPUModeControl: AutoGpuSwitch - PCI backend, no live switching available");
            return GpuSwitchResult.AlreadySet;
        }

        // Don't auto-switch if in Ultimate (MUX=0)
        int mux = _wmi.GetGpuMuxMode();
        if (mux == 0)
        {
            Logger.WriteLine("GPUModeControl: AutoGpuSwitch - MUX=0 (Ultimate), skipping");
            return GpuSwitchResult.AlreadySet;
        }

        // Don't auto-switch to Eco if MUX=0 was latched this boot (persistent flag)
        // Hardware may still read MUX=1, but firmware has MUX=0 pending - Eco would be impossible
        if (IsMuxZeroLatchedThisBoot())
        {
            Logger.WriteLine("GPUModeControl: AutoGpuSwitch - MUX=0 latched this boot, Eco path blocked - staying in Standard");
            return GpuSwitchResult.AlreadySet;
        }

        bool onAc = _power.IsOnAcPower();
        bool ecoEnabled = _wmi.GetGpuEco();

        if (onAc && ecoEnabled)
        {
            // Plugged in → enable dGPU (always safe)
            Logger.WriteLine("GPUModeControl: AutoGpuSwitch - AC power, enabling dGPU");
            if (!_switchLock.Wait(0))
                return GpuSwitchResult.AlreadySet;

            try
            {
                _wmi.SetGpuEco(false);
                // Switching away from Eco - clean up block artifacts
                RemoveDriverBlock();
                Logger.WriteLine("GPUModeControl: AutoGpuSwitch - dGPU enabled");
                return GpuSwitchResult.Applied;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: AutoGpuSwitch Eco→Standard failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
        else if (!onAc && !ecoEnabled)
        {
            // On battery → disable dGPU (THE dangerous path)
            Logger.WriteLine("GPUModeControl: AutoGpuSwitch - battery, attempting Eco");
            if (!_switchLock.Wait(0))
                return GpuSwitchResult.AlreadySet;

            try
            {
                return ExecuteDisableDgpu();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: AutoGpuSwitch battery→Eco failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }

        return GpuSwitchResult.AlreadySet;
    }

    /// <summary>
    /// Read current hardware state + config → return the current mode enum.
    /// </summary>
    public GpuMode GetCurrentMode()
    {
        // Block files (modprobe blacklist + udev hot-remove rule) are the
        // persistent Eco state for the PCI backend. They are also written
        // briefly during an ASUS-WMI eco transition. Either way, when they
        // exist the dGPU is effectively disabled - it is hot-removed by
        // udev on every boot, so `dgpu_disable=0` is meaningless until the
        // blocks are removed. Treat their presence as the source of truth
        // for "in Eco" regardless of the configured backend, so the UI
        // doesn't claim Standard while reality is "dGPU vanished from the
        // PCI bus".
        bool blocksPresent = File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);

        // PCI backend: blocks are the only signal. No MUX, no auto, no
        // firmware sysfs to query.
        if (AppConfig.IsPciGpuBackend())
            return blocksPresent ? GpuMode.Eco : GpuMode.Standard;

        bool gpuAuto = AppConfig.Is("gpu_auto");
        bool ecoEnabled = _wmi.GetGpuEco();
        int mux = _wmi.GetGpuMuxMode();

        if (mux == 0)
            return GpuMode.Ultimate;
        if (gpuAuto)
            return GpuMode.Optimized;
        // Either dgpu_disable=1 live OR PCI-style blocks still on disk →
        // Eco. The latter happens after the user toggled the backend from
        // PCI to asus-wmi while still in eco: dgpu_disable reads 0 but the
        // dGPU is gone from the PCI bus until the user explicitly switches
        // to Standard (which removes the blocks via RemoveDriverBlock).
        if (ecoEnabled || blocksPresent)
            return GpuMode.Eco;
        return GpuMode.Standard;
    }

    /// <summary>
    /// True if config gpu_mode differs from current hardware state
    /// (mode is waiting for a reboot to take effect).
    /// </summary>
    public bool IsPendingReboot()
    {
        // PCI backend: the trigger file is the single source of truth.
        // Boot script applies and removes it; while present, a reboot is pending.
        if (AppConfig.IsPciGpuBackend())
            return File.Exists(TriggerPath);

        string? saved = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(saved))
            return false;

        GpuMode target = ParseGpuMode(saved);
        GpuMode current = GetCurrentMode();

        // Simple check: if they differ, something is pending
        if (target == current)
            return false;

        // More precise: check if the difference requires a reboot
        int mux = _wmi.GetGpuMuxMode();
        bool eco = _wmi.GetGpuEco();

        return target switch
        {
            GpuMode.Eco => !eco,        // Eco pending but not applied
            GpuMode.Ultimate => mux != 0, // MUX change pending
            GpuMode.Standard => mux == 0, // Coming from Ultimate, MUX pending
            GpuMode.Optimized => mux == 0, // Coming from Ultimate, MUX pending
            _ => false
        };
    }

    // Core logic

    /// <summary>
    /// Core logic: read current hardware, compute delta, route to Execute* methods.
    /// </summary>
    private GpuSwitchResult ComputeAndExecute(GpuMode target)
    {
        // PCI backend short-circuits the WMI matrix entirely. The
        // modprobe + udev files ARE the persistent Eco state and the boot
        // script handles the actual transition on the next reboot. We only
        // need to write the right trigger / block artifacts here.
        if (AppConfig.IsPciGpuBackend())
            return ComputeAndExecutePci(target);

        // 4×4 Transition Matrix
        // From\To     | Eco         | Standard    | Optimized   | Ultimate
        // Eco         | AlreadySet  | Applied     | Applied*    | RebootReq
        // Standard    | DANGER†     | AlreadySet  | Applied*/†  | RebootReq
        // Optimized   | (delegates) | (delegates) | AlreadySet  | RebootReq‡
        // Ultimate    | Multi-boot§ | RebootReq   | RebootReq   | AlreadySet
        //
        // * Optimized target: targetEco depends on AC power (battery=Eco hw, AC=Standard hw)
        // † DANGER: dgpu_disable=1 when driver active → DriverBlocking dialog
        // ‡ If Optimized hw=Eco, exits Eco first (200ms verify), then MUX write
        // § Ultimate→Eco: MUX latch + DriverBlocking or deferred Eco (2-boot path)
        //
        // Config is saved only on success paths (Applied, RebootRequired, AlreadySet,
        // DriverBlocking). NEVER saved on Failed - prevents stale config from rejected writes.

        // Read current hardware state
        bool currentEco = _wmi.GetGpuEco();     // true if dgpu_disable=1
        // Use effective MUX (accounts for pending latch from earlier this session)
        int currentMux = GetEffectiveMux();       // 0=Ultimate, 1=hybrid

        // Compute target hardware state
        bool targetEco;
        int targetMux;
        bool targetAuto = false;

        switch (target)
        {
            case GpuMode.Eco:
                targetEco = true;
                targetMux = 1;
                break;
            case GpuMode.Standard:
                targetEco = false;
                targetMux = 1;
                break;
            case GpuMode.Optimized:
                targetAuto = true;
                targetMux = 1;
                // On AC: want dGPU on (Standard hw). On battery: want dGPU off (Eco hw).
                targetEco = !_power.IsOnAcPower();
                break;
            case GpuMode.Ultimate:
                targetEco = false;
                targetMux = 0;
                break;
            default:
                return GpuSwitchResult.Failed;
        }

        // gpu_auto is a software flag (no hardware write) - safe to set early
        AppConfig.Set("gpu_auto", targetAuto ? 1 : 0);
        // NOTE: SaveModeToConfig is called at each SUCCESS exit point below, not here.
        // If a hardware write fails, config must NOT say the new mode.

        // Exit Eco first if MUX change is needed
        // gpu_mux_mode write FAILS when dgpu_disable=1 (firmware rejects "No such device").
        // Must enable dGPU before any MUX change.
        if (currentEco && currentMux >= 0 && currentMux != targetMux)
        {
            Logger.WriteLine("GPUModeControl: currently in Eco, enabling dGPU before MUX change");
            try
            {
                _wmi.SetGpuEco(false);
                RemoveDriverBlock();

                // Verify dGPU re-enablement - Windows G-Helper pattern: wait + readback.
                // SetGpuEco(false) includes 50ms settle + PCI rescan (Phase 1).
                // Additional 200ms here for firmware to update dgpu_disable readback.
                Thread.Sleep(200);
                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GPUModeControl: FAILED to exit Eco - dgpu_disable still reads 1 after 200ms, aborting MUX change");
                    return GpuSwitchResult.Failed;
                }

                currentEco = false;
                Logger.WriteLine("GPUModeControl: dGPU re-enabled, dgpu_disable readback confirmed 0");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: failed to exit Eco before MUX change: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
        }

        // MUX change needed?
        if (currentMux >= 0 && currentMux != targetMux)
        {
            Logger.WriteLine($"GPUModeControl: MUX change {currentMux} → {targetMux}");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
            }
            catch (InvalidOperationException ex)
            {
                // Safety guard violation (dgpu_disable=1) - shouldn't happen after Eco exit above
                Logger.WriteLine($"GPUModeControl: MUX write safety violation: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            catch (Exception ex)
            {
                // IOException from firmware rejection, or other unexpected error
                Logger.WriteLine($"GPUModeControl: MUX write failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            _pendingMuxLatch = targetMux;
            if (targetMux == 0)
                SetMuxZeroLatchFlag();

            if (currentEco != targetEco && targetEco)
            {
                // MUX change + Eco needed - BUT validate this isn't an impossible combo.
                // If MUX is latched to 0 (Ultimate), Eco block artifacts would cause black screen.
                // WriteDriverBlock already refuses, but be explicit here too.
                if (WouldCreateImpossibleState(target))
                {
                    // MUX latched to 0 + Eco = impossible. This shouldn't happen from
                    // normal UI flow (Eco has targetMux=1), but defend against it.
                    Logger.WriteLine("GPUModeControl: MUX change + Eco creates impossible state - Eco blocked");
                    RemoveDriverBlock();
                    return GpuSwitchResult.EcoBlocked;
                }

                // Check if driver is blocking.
                // If driver is active, return DriverBlocking so the UI shows the dialog
                // on the FIRST click (not silently latching MUX and forcing a second click).
                if (IsDgpuDriverActive())
                {
                    Logger.WriteLine("GPUModeControl: MUX change + Eco needed, driver active → DriverBlocking");
                    // MUX is already latched above. The dialog's "After Reboot" will call
                    // ScheduleModeForReboot() which writes the block artifacts.
                    // ScheduleModeForReboot also has the impossible-state guard.
                    SaveModeToConfig(target);
                    return GpuSwitchResult.DriverBlocking;
                }
                Logger.WriteLine("GPUModeControl: also need Eco - deferred to after MUX settles (next boot)");
            }
            else if (!targetEco)
            {
                // Switching to Standard/Ultimate/Optimized - clean up stale block artifacts
                RemoveDriverBlock();
            }

            SaveModeToConfig(target);
            return GpuSwitchResult.RebootRequired;
        }

        // dgpu change needed?
        if (currentEco == targetEco)
        {
            Logger.WriteLine($"GPUModeControl: hardware already in target state (eco={currentEco})");
            // Clean up stale block artifacts if target is not Eco
            if (!targetEco)
            {
                RemoveDriverBlock();

                // dgpu_disable=0 does not guarantee a working dGPU: a failed
                // Eco release can leave the device unbound (late unbind after
                // our timeout) with the module loaded but no driver attached.
                // Run the full enable path to rebind/reload instead of no-op.
                if (IsDgpuPresentButDriverless())
                {
                    Logger.WriteLine("GPUModeControl: eco=0 but dGPU is driverless - running recovery enable");
                    var recovery = ExecuteEnableDgpu();
                    if (recovery == GpuSwitchResult.Applied)
                        SaveModeToConfig(target);
                    return recovery;
                }
            }
            SaveModeToConfig(target);
            return GpuSwitchResult.AlreadySet;
        }

        if (!targetEco)
        {
            // Enabling dGPU - always safe, always fast
            var result = ExecuteEnableDgpu();
            if (result == GpuSwitchResult.Applied)
                SaveModeToConfig(target);
            return result;
        }
        else
        {
            // Disabling dGPU - THE dangerous path
            var result = ExecuteDisableDgpu();
            if (result == GpuSwitchResult.Applied)
                SaveModeToConfig(target);
            else if (result == GpuSwitchResult.DriverBlocking || result == GpuSwitchResult.RebootRequired)
                SaveModeToConfig(target);
            // On Failed: do NOT save config
            return result;
        }
    }

    /// <summary>
    /// PCI backend switching. There are only two effective modes - Eco
    /// (block artifacts present) and Standard (no block artifacts). The
    /// transition is always deferred to reboot; the boot script does the
    /// actual rmmod / PCI rescan work. Optimized and Ultimate fall through
    /// to Standard since they have no meaning without ASUS firmware.
    /// </summary>
    private GpuSwitchResult ComputeAndExecutePci(GpuMode target)
    {
        // Optimized / Ultimate are not meaningful in PCI mode. Treat them as
        // Standard so the dGPU is enabled at next boot. The UI should be
        // hiding these buttons but the controller stays defensive in case
        // the tray menu or a config import triggers them.
        if (target == GpuMode.Optimized || target == GpuMode.Ultimate)
        {
            Logger.WriteLine($"GPUModeControl: PCI backend - {target} not applicable, treating as Standard");
            target = GpuMode.Standard;
        }

        bool ecoBlocksPresent = File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);
        bool wantEco = (target == GpuMode.Eco);
        bool wantStandard = (target == GpuMode.Standard);

        // Already in the desired persistent state and no pending switch?
        // Mirror the WMI flow and report AlreadySet so the UI clears its
        // "reboot pending" tip.
        if (!File.Exists(TriggerPath))
        {
            if (wantEco && ecoBlocksPresent)
            {
                Logger.WriteLine("GPUModeControl: PCI backend - already in Eco (blocks present), no-op");
                SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.AlreadySet;
            }
            if (wantStandard && !ecoBlocksPresent)
            {
                Logger.WriteLine("GPUModeControl: PCI backend - already in Standard (no blocks), no-op");
                SaveModeToConfig(GpuMode.Standard);
                return GpuSwitchResult.AlreadySet;
            }
        }

        // Standard → Eco: if driver is loaded, return DriverBlocking so the
        // UI shows the Switch Now / After Reboot dialog. If driver is idle,
        // write blocks + PCI-remove the dGPU immediately.
        if (wantEco && !ecoBlocksPresent)
        {
            // MUX=0 (live or latched) → removing the dGPU blanks the display.
            // Lenovo has no MUX (reads -1) so this never blocks there.
            if (WouldCreateImpossibleState(GpuMode.Eco))
                return GpuSwitchResult.EcoBlocked;

            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GPUModeControl: PCI backend - dGPU driver active, showing dialog");
                SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.DriverBlocking;
            }

            SaveModeToConfig(GpuMode.Eco);
            WriteDriverBlock(GpuMode.Eco);
            PciRemoveDgpuFunctions();
            ApplyVulkanIcd(dgpuAvailable: false);
            Logger.WriteLine("GPUModeControl: PCI backend - live Eco applied (driver was not loaded)");
            return GpuSwitchResult.Applied;
        }

        // Eco → Standard: remove blocks, reload udev, rescan PCI bus,
        // modprobe the driver. On failure, fall through to deferred reboot.
        if (wantStandard && ecoBlocksPresent)
        {
            var live = TryLiveRemovePciBlocks();
            if (live == GpuSwitchResult.Applied)
            {
                SaveModeToConfig(GpuMode.Standard);
                Logger.WriteLine("GPUModeControl: PCI backend - live Eco→Standard applied (no reboot)");
                return GpuSwitchResult.Applied;
            }
            Logger.WriteLine("GPUModeControl: PCI backend - live transition failed, falling back to deferred reboot");
        }

        // Schedule the change for the next reboot. WriteDriverBlock knows
        // how to handle both Eco (writes blocks) and non-Eco (clears blocks
        // and writes only the trigger) in PCI mode.
        SaveModeToConfig(target);
        WriteDriverBlock(target);

        if (File.Exists(TriggerPath))
        {
            Logger.WriteLine($"GPUModeControl: PCI backend - scheduled {target} for next reboot");
            return GpuSwitchResult.RebootRequired;
        }

        // WriteDriverBlock did not produce a trigger file. Two reasons:
        //   1. Authentication was cancelled (pkexec dialog dismissed).
        //   2. The internal safety guard refused (Eco + MUX=0 latched).
        // In case (2) the earlier "WriteDriverBlock REFUSED" log line
        // explains why; the EcoBlocked result lets the UI surface a
        // friendlier reason than a generic failure toast.
        if (target == GpuMode.Eco && WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GPUModeControl: PCI backend - Eco refused (MUX=0 latched, would cause black screen)");
            return GpuSwitchResult.EcoBlocked;
        }
        Logger.WriteLine("GPUModeControl: PCI backend - trigger write failed (pkexec cancelled or write error)");
        return GpuSwitchResult.Failed;
    }

    // Atomic operations

    /// <summary>Always safe. Write dgpu_disable=0 (enable dGPU). Returns Applied.</summary>
    /// <summary>
    /// After the dGPU is re-enabled, give the driver a moment to settle then ask
    /// ModeControl to re-apply (or reset) the current mode's GPU tuning. Runs on a
    /// background task so it never blocks the switch.
    /// </summary>
    private static void ScheduleGpuTuningReapply()
    {
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            try
            {
                OnReapplyGpuTuning?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: GPU tuning reapply failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// dGPU graphics function is on the bus but no driver is bound to it.
    /// Happens when an Eco release fails halfway (the PCI unbind completed
    /// after our timeout, so the rollback never rebound it).
    /// </summary>
    private static bool IsDgpuPresentButDriverless()
    {
        var dev = FindDgpuPciDevice();
        if (dev == null)
            return false;
        return !Directory.Exists(TestPathPrefix + $"/sys/bus/pci/devices/{dev.Value.bdf}/driver");
    }

    /// <summary>
    /// Bind any driverless dGPU PCI function back to its driver. The normal
    /// rescan path auto-probes, so this no-ops then; it only acts in the
    /// recovery case where the device stayed on the bus unbound (modprobe
    /// alone does not re-probe an already-enumerated device).
    /// </summary>
    private static void EnsureDgpuFunctionsBound(string gfxBdf, bool isAmd)
    {
        string gfxDriver = isAmd ? "amdgpu" : "nvidia";
        foreach (var node in EnumerateDgpuDeviceNodes(gfxBdf))
        {
            string devDir = TestPathPrefix + $"/sys/bus/pci/devices/{node}";
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

    /// <summary>True once the nvidia driver actually owns the graphics function.</summary>
    private static bool IsNvidiaBound(string gfxBdf)
        => Directory.Exists(TestPathPrefix + $"/sys/bus/pci/drivers/nvidia/{gfxBdf}");

    /// <summary>nvidia.ko resolvable for the running kernel. modinfo honours
    /// updates/, extra/ and compressed modules, unlike a /lib/modules glob.</summary>
    private static bool IsNvidiaModuleInstalled()
        => SysfsHelper.RunCommand("modinfo", "-F filename nvidia") != null;

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
            EnsureDgpuFunctionsBound(gfxBdf, isAmd: false);

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
                WakeDgpuBridge();
                RunPciRemove(gfxBdf);
                TryPowerOnDgpuSlot();
                SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
            }
            else
            {
                // A root port left in D3cold enumerates the dGPU from stale
                // config space with the link still down, so config reads answer
                // but MMIO returns all-ones. Only a full re-enumeration of the
                // hierarchy retrains the link.
                ResetDgpuBridge();
            }

            WaitForDgpuNode(6000);
        }
        return false;
    }

    /// <summary>Poll for the dGPU node without running the escalation ladder.</summary>
    private static bool WaitForDgpuNode(int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (FindDgpuPciDevice() != null)
                return true;
            Thread.Sleep(500);
            waited += 500;
        }
        return false;
    }

    private GpuSwitchResult ExecuteEnableDgpu()
    {
        Logger.WriteLine("GPUModeControl: enabling dGPU (dgpu_disable=0) - always safe");
        // SetGpuEco(false) can take 20s+ on asus-armoury and WaitForDgpuDevice
        // another 22s, so a shorter budget can expire before the driver load
        // even starts. Extended per attempt below; Resume() ends it on success.
        NVidia.GpuQueryGate.Pause(TimeSpan.FromSeconds(60), "dGPU enable");
        try
        {
            _wmi.SetGpuEco(false);
            // Switching away from Eco - remove block artifacts (dGPU driver should be loadable)
            RemoveDriverBlock();

            if (IsTestMode)
            {
                Logger.WriteLine("GPUModeControl: test mode - skipping live dGPU hardware re-enable");
                return GpuSwitchResult.Applied;
            }

            TryPowerOnDgpuSlot();

            // Wait for the dGPU to actually re-appear on the PCI bus. The
            // dgpu_disable=0 write can be very slow on asus-armoury firmware
            // (20s+), and the single rescan in SetGpuEco often fires before the
            // device is electrically back, so it never re-enumerates. Poll for
            // the device, re-asserting slot power + rescan until it shows up. Gate
            // the nvidia daemon restart on the *device* (not the module - the
            // module can be present from a powerd respawn loop even with no GPU).
            bool present = WaitForDgpuDevice(22000);
            if (!present)
            {
                Logger.WriteLine("GPUModeControl: dGPU did not re-appear after rescan - reboot likely required; skipping daemon restart");
                NVidia.GpuQueryGate.Hold("dGPU did not re-appear");
                return GpuSwitchResult.DgpuReenableFailed;
            }

            // Device is back but the driver is not up yet; cover the load below
            // (the nvidia path extends this again per attempt).
            NVidia.GpuQueryGate.Extend(TimeSpan.FromSeconds(NvidiaLoadAttemptBudgetSec),
                "dGPU present - loading driver");

            var dgpuDev = FindDgpuPciDevice();
            bool isAmd = dgpuDev?.vendor.Equals("0x1002", StringComparison.OrdinalIgnoreCase) == true;
            if (isAmd)
            {
                // amdgpu also drives the iGPU, so it is never rmmod'd; after the
                // device re-enumerates, load it explicitly (udev coldplug is the
                // backup). Mirrors gpu-block-helper.sh live-standard.
                Logger.WriteLine("GPUModeControl: AMD dGPU present - loading amdgpu");
                SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "modprobe", "amdgpu" }, sudoTimeoutMs: 10000);
                if (dgpuDev != null)
                    EnsureDgpuFunctionsBound(dgpuDev.Value.bdf, isAmd: true);
            }
            else if (!IsNvidiaModuleInstalled())
            {
                // No nvidia.ko for the running kernel (unbuilt akmod/dkms).
                // The load ladder cannot bind and re-enumeration will not
                // change that; the dGPU is on the bus and powered. Reporting
                // failure made startup re-apply Eco every reboot (#193).
                Logger.WriteLine("GPUModeControl: nvidia.ko is not installed for this kernel - skipping nvidia load (kernel may bind nouveau)");
            }
            else
            {
                Logger.WriteLine("GPUModeControl: nvidia dGPU present - loading nvidia");
                if (dgpuDev != null && !LoadNvidiaWithRetry(dgpuDev.Value.bdf))
                {
                    Logger.WriteLine("GPUModeControl: nvidia never bound to the dGPU - reboot required");
                    NVidia.GpuQueryGate.Hold("dGPU re-enable failed");
                    return GpuSwitchResult.DgpuReenableFailed;
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

            // Allow the dGPU to autosuspend (supergfxctl set_runtime_pm Auto).
            SetDgpuRuntimePmAuto();
            // Restore the NVIDIA Vulkan ICD now the dGPU is back.
            ApplyVulkanIcd(dgpuAvailable: true);
            // Bring back whitelisted user services stopped by an earlier
            // holder kill (e.g. powerdevil pinned the nvidia I2C bus).
            RestartStoppedHolderServices();
            Logger.WriteLine("GPUModeControl: dGPU enabled");
            NVidia.LinuxNvidiaGpuControl.ResetSmiBreaker();
            NVidia.GpuQueryGate.Resume();
            // Re-apply (or reset) the current mode's GPU tuning now the dGPU is
            // back, so persistence survives an Eco->Standard toggle.
            ScheduleGpuTuningReapply();
            return GpuSwitchResult.Applied;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: enable dGPU failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    /// <summary>
    /// Scan /sys/bus/pci/devices for the discrete GPU graphics function,
    /// regardless of whether a driver is bound: NVIDIA (vendor 0x10de) or AMD
    /// (vendor 0x1002 with boot_vga != 1 so the iGPU is excluded). Matches only
    /// VGA (0x0300xx) / 3D (0x0302xx) classes so audio/USB sibling functions are
    /// skipped. Returns (bdf, vendor) or null. Detects presence right after a
    /// rescan, before the driver binds.
    /// </summary>
    private static (string bdf, string vendor)? FindDgpuPciDevice()
    {
        try
        {
            string devDir = TestPathPrefix + "/sys/bus/pci/devices";
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

    /// <summary>
    /// Poll for the dGPU PCI device to re-appear after dgpu_disable=0, re-issuing
    /// /sys/bus/pci/rescan on each attempt (the firmware may need several seconds
    /// to electrically re-expose the device). Returns true once present.
    /// </summary>
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
    private bool WaitForDgpuDevice(int timeoutMs)
    {
        int waited = 0;
        int attempt = 0;
        bool bridgeWoken = false, disableBounced = false, bridgeReset = false;
        while (waited < timeoutMs)
        {
            if (FindDgpuPciDevice() != null)
            {
                Logger.WriteLine($"GPUModeControl: dGPU present after {waited}ms ({attempt} rescan(s))");
                return true;
            }

            if (waited >= 4000 && !bridgeWoken)
            {
                bridgeWoken = true;
                WakeDgpuBridge();
            }
            else if (waited >= 8000 && !disableBounced)
            {
                disableBounced = true;
                BounceDgpuDisable();
            }
            else if (waited >= 12000 && !bridgeReset)
            {
                bridgeReset = true;
                ResetDgpuBridge();
            }

            // Re-assert slot power (idempotent) then re-trigger enumeration;
            // SetGpuEco already did the first rescan.
            TryPowerOnDgpuSlot();
            SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1");
            attempt++;
            Thread.Sleep(1000);
            waited += 1000;
        }
        return FindDgpuPciDevice() != null;
    }

    /// Escalation step 1: force the dGPU's parent root port out of runtime
    /// suspend. While the bridge sits in D3cold its link is down and a bus
    /// rescan cannot see the dGPU at all.
    private static void WakeDgpuBridge()
    {
        string? bridge = ResolveDgpuBridge();
        if (string.IsNullOrEmpty(bridge))
        {
            Logger.WriteLine("GPUModeControl: escalation - bridge unknown, cannot wake");
            return;
        }
        string status = SysfsHelper.ReadAttribute(
            TestPathPrefix + $"/sys/bus/pci/devices/{bridge}/power/runtime_status")?.Trim() ?? "?";
        Logger.WriteLine($"GPUModeControl: escalation - waking bridge {bridge} (runtime_status={status})");
        RunPciPower(bridge!, "on");
    }

    /// Escalation step 2: dgpu_disable 1 -> 0 makes the firmware repeat its
    /// whole dGPU power-up sequence instead of assuming it already succeeded.
    private void BounceDgpuDisable()
    {
        try
        {
            Logger.WriteLine("GPUModeControl: escalation - bouncing dgpu_disable 1 -> 0 (firmware power-on retry)");
            _wmi.SetGpuEco(true);
            Thread.Sleep(800);
            _wmi.SetGpuEco(false);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: escalation - dgpu_disable bounce failed: {ex.Message}");
        }
    }

    /// Escalation step 3 (last resort): drop the parent root port from the
    /// device tree and rescan. The rescan re-discovers the bridge from the
    /// root complex and re-enumerates everything behind it from scratch,
    /// clearing any stale state that blocked the dGPU from re-appearing.
    private static void ResetDgpuBridge()
    {
        string? bridge = ResolveDgpuBridge();
        if (string.IsNullOrEmpty(bridge))
        {
            Logger.WriteLine("GPUModeControl: escalation - bridge unknown, cannot reset");
            return;
        }
        if (!Directory.Exists(TestPathPrefix + $"/sys/bus/pci/devices/{bridge}"))
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

    public void CacheDgpuSlotIfPresent()
    {
        try
        {
            ResolveDgpuSlot();
            ResolveDgpuBridge();
            ValidateDgpuCacheAtStartup();
        }
        catch (Exception ex) { Logger.WriteLine($"GPUModeControl: CacheDgpuSlotIfPresent failed: {ex.Message}"); }
    }

    /// <summary>
    /// Once per app start: age out a dGPU cache that no longer matches this
    /// machine (config imported from another system, or hardware swap).
    /// While the dGPU is merely hidden (Eco artifacts / validated slot) the
    /// cache is legitimate and the counter resets. After 3 consecutive
    /// starts with no evidence, the cached keys are dropped so
    /// <see cref="HasSecondGpu"/> stops reporting a phantom dGPU.
    /// </summary>
    private static void ValidateDgpuCacheAtStartup()
    {
        if (string.IsNullOrEmpty(AppConfig.GetString(DgpuSlotKey))
            && string.IsNullOrEmpty(AppConfig.GetString(DgpuBdfKey)))
            return;

        bool evidence = FindDgpuPciDevice() != null
            || EcoBlockArtifactsPresent()
            || ValidatedDgpuSlotCached();
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
    private static string? ResolveDgpuBridge()
    {
        string? bdf = FindDgpuPciDevice()?.bdf;
        if (!string.IsNullOrEmpty(bdf))
        {
            try
            {
                var devDir = new DirectoryInfo(TestPathPrefix + $"/sys/bus/pci/devices/{bdf}");
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

    private static string? ResolveDgpuSlot()
    {
        string? bdf = FindDgpuPciDevice()?.bdf;
        if (!string.IsNullOrEmpty(bdf))
        {
            // Remember the dGPU BDF so the slot cache can later be validated
            // against this machine (see ValidatedDgpuSlotCached).
            if (AppConfig.GetString(DgpuBdfKey) != bdf)
                AppConfig.Set(DgpuBdfKey, bdf!);
            string? slot = FindDgpuSlot(bdf!);
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
    /// Universal, vendor-neutral, Eco-resilient test for whether this machine
    /// has a second GPU at all. GPU mode switching (Eco / Standard / Ultimate),
    /// the GPU backend selector and the GPU boot integration only make sense
    /// with two GPUs; with a single (integrated) GPU only tuning applies.
    ///
    ///   1. Live PCI scan: two or more display-class (0x03xxxx) functions on
    ///      the bus, or <see cref="FindDgpuPciDevice"/> finds a non-boot dGPU.
    ///   2. Our own Eco block artifacts: the dGPU was hot-removed by us, the
    ///      switching UI must stay reachable to undo it.
    ///   3. Cached dGPU slot, VALIDATED against the live slot list (address
    ///      must match the cached BDF) so a config file imported from another
    ///      machine cannot fake a dGPU. Survives Eco (slot dir persists).
    ///   4. ASUS firmware: dgpu_disable / gpu_mux_mode attributes, vetoed by
    ///      the NoGpu() model list (some iGPU-only firmwares expose the
    ///      attribute anyway).
    /// </summary>
    public static bool HasSecondGpu()
    {
        if (CountDisplayClassFunctions() >= 2)
            return true;
        if (FindDgpuPciDevice() != null)
            return true;
        if (EcoBlockArtifactsPresent())
            return true;
        // nvidia/nouveau bound with no matching device = dGPU hot-removed.
        // These drivers never load on machines without NVIDIA hardware.
        if (Platform.Linux.LinuxAsusWmi.HasNvidiaModuleLoaded())
            return true;
        if (ValidatedDgpuSlotCached())
            return true;
        if (!AppConfig.NoGpu())
        {
            var wmi = App.Wmi;
            if (wmi != null
                && (wmi.IsFeatureSupported(AsusAttributes.DgpuDisable)
                    || wmi.IsFeatureSupported(AsusAttributes.GpuMuxMode)))
                return true;
        }
        return false;
    }

    /// <summary>Alias kept for the installer gate (ghelper-gpu-boot.service
    /// applicability): identical semantics to <see cref="HasSecondGpu"/>.</summary>
    public static bool HasDiscreteGpu() => HasSecondGpu();

    /// <summary>Number of PCI functions with a display class (0x03xxxx: VGA,
    /// 3D, other display controllers). Vendor-agnostic, driver not needed.</summary>
    private static int CountDisplayClassFunctions()
    {
        int count = 0;
        try
        {
            string devDir = TestPathPrefix + "/sys/bus/pci/devices";
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

    /// <summary>Our PCI-Eco block artifacts (modprobe blacklist + udev
    /// hot-remove rule). Their presence means a dGPU exists but is hidden.</summary>
    internal static bool EcoBlockArtifactsPresent() =>
        File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);

    /// <summary>
    /// True when the cached dGPU slot refers to THIS machine: the slot dir
    /// exists and its address matches the cached dGPU BDF. Old configs that
    /// predate the BDF cache fall back to slot-dir existence only.
    /// </summary>
    private static bool ValidatedDgpuSlotCached()
    {
        string? slot = AppConfig.GetString(DgpuSlotKey);
        if (string.IsNullOrEmpty(slot))
            return false;
        string slotDir = TestPathPrefix + $"/sys/bus/pci/slots/{slot}";
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
    private static string? FindDgpuSlot(string bdf)
    {
        try
        {
            string slotsDir = TestPathPrefix + "/sys/bus/pci/slots";
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
    private static void TryPowerOnDgpuSlot()
    {
        string? slot = ResolveDgpuSlot();
        if (string.IsNullOrEmpty(slot))
        {
            Logger.WriteLine("GPUModeControl: dGPU PCIe slot unknown - cannot assert slot power (rescan only)");
            return;
        }
        int cur = SysfsHelper.ReadInt(TestPathPrefix + $"/sys/bus/pci/slots/{slot}/power", -1);
        if (cur == 1)
            return;
        bool ok = RunSlotPower(slot!, "1");
        Logger.WriteLine($"GPUModeControl: slot-power {slot} = 1 ({(ok ? "OK" : "FAILED")}) [was {cur}]");
    }

    /// <summary>Cut slot power after dgpu_disable=1 as extra insurance.</summary>
    private static void TryPowerOffDgpuSlot()
    {
        string? slot = ResolveDgpuSlot();
        if (string.IsNullOrEmpty(slot))
            return;
        int cur = SysfsHelper.ReadInt(TestPathPrefix + $"/sys/bus/pci/slots/{slot}/power", -1);
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
            if (Directory.Exists(TestPathPrefix + "/sys/module/nvidia"))
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

    /// <summary>
    /// THE one dangerous operation. Checks driver safety first.
    /// If safe → writes dgpu_disable=1 (may block 30-60s).
    /// If unsafe → returns DriverBlocking.
    /// </summary>
    private GpuSwitchResult ExecuteDisableDgpu()
    {
        // Check if in Ultimate mode (MUX=0) - kernel refuses dgpu_disable=1
        int mux = GetEffectiveMux();
        if (mux == 0)
        {
            Logger.WriteLine("GPUModeControl: MUX=0 (Ultimate) - cannot disable dGPU directly");
            // Latch MUX change - dgpu_disable must wait until MUX settles on next boot.
            // Do NOT write block here - MUX needs to settle first. ApplyPendingOnStartup()
            // will write the block after confirming MUX is correct.
            try
            {
                _wmi.SetGpuMuxMode(1);
                _pendingMuxLatch = 1;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: ExecuteDisableDgpu - MUX latch failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }


        if (IsDgpuDriverActive())
        {
            Logger.WriteLine("GPUModeControl: dGPU driver is ACTIVE - returning DriverBlocking (user confirmation required)");
            LogHoldersSnapshot("DriverBlocking");
            return GpuSwitchResult.DriverBlocking;
        }

        // Safe to write
        Logger.WriteLine("GPUModeControl: dGPU driver idle/absent - writing dgpu_disable=1");
        try
        {
            DropDgpuPciNodes();
            _wmi.SetGpuEco(true);

            // Verify the write took effect
            if (_wmi.GetGpuEco())
            {
                Logger.WriteLine("GPUModeControl: dgpu_disable=1 confirmed");
                TryPowerOffDgpuSlot();
                RemoveDriverBlock();
                ApplyVulkanIcd(dgpuAvailable: false);
                RestartStoppedHolderServices();
                return GpuSwitchResult.Applied;
            }
            else
            {
                Logger.WriteLine("GPUModeControl: dgpu_disable=1 write did not take effect");
                return GpuSwitchResult.Failed;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: dgpu_disable=1 write failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    // Driver detection

    /// <summary>
    /// Check if the dGPU driver is currently active (holding the hardware).
    /// NVIDIA: check /sys/module/nvidia_drm/refcnt
    /// AMD: check dGPU PCI device power/runtime_status
    /// </summary>
    public bool IsDgpuDriverActive()
    {
        if (IsNvidiaGpu())
            return IsNvidiaDriverActive();

        if (IsAmdDgpu())
            return IsAmdDriverActive();

        // No known dGPU driver loaded - safe
        Logger.WriteLine("GPUModeControl: no dGPU driver detected - safe");
        return false;
    }

    private bool IsNvidiaDriverActive()
    {
        // The full nvidia kernel module family. nvidia_drm is the display
        // path; nvidia_uvm is the CUDA/compute path; nvidia_modeset wires
        // KMS. Any one of them in use is enough to keep nvidia loaded
        // and Eco unable to write dgpu_disable cleanly.
        string[] modules = new[] { "nvidia_drm", "nvidia_modeset", "nvidia_uvm", "nvidia" };
        bool anyModuleLoaded = false;
        foreach (var mod in modules)
        {
            string modDir = TestPathPrefix + "/sys/module/" + mod;
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

    private bool IsAmdDriverActive()
    {
        string? pciAddr = FindDgpuPciAddress();
        if (pciAddr == null)
        {
            Logger.WriteLine("GPUModeControl: AMD dGPU PCI address not found - assuming safe");
            return false;
        }

        string status = ReadDgpuRuntimeStatus(pciAddr);
        if (status == "suspended")
        {
            Logger.WriteLine($"GPUModeControl: AMD dGPU {pciAddr} runtime_status=suspended - safe");
            return false;
        }

        Logger.WriteLine($"GPUModeControl: AMD dGPU {pciAddr} runtime_status={status} - ACTIVE");
        return true;
    }

    // Driver release

    /// <summary>
    /// Attempt to release the dGPU driver so dgpu_disable=1 can proceed safely.
    /// NVIDIA: pkexec rmmod nvidia stack
    /// AMD: pkexec PCI unbind+remove
    /// Returns true if driver was released.
    /// </summary>
    private bool TryReleaseGpuDriver()
    {
        if (IsNvidiaGpu())
            return TryReleaseNvidiaDriver();

        if (IsAmdDgpu())
            return TryReleaseAmdDriver();

        return true; // No driver to release
    }

    private bool TryReleaseNvidiaDriver()
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
        ResolveDgpuSlot();
        ResolveDgpuBridge();

        var r1 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "stop", "nvidia-powerd" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r1 != null ? "GPUModeControl: stopped nvidia-powerd" : "GPUModeControl: nvidia-powerd stop failed");
        var r2 = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "daemon", "stop", "nvidia-persistenced" }, sudoTimeoutMs: 5000);
        Logger.WriteLine(r2 != null ? "GPUModeControl: stopped nvidia-persistenced" : "GPUModeControl: nvidia-persistenced stop failed");
        Thread.Sleep(500);


        return ReleaseNvidiaModulesAndPurgeHolders(FindNvidiaPciAddress(), out _);
    }

    /// <summary>
    /// Poll /sys/module/nvidia/refcnt until it holds the same value for ~1s
    /// (context teardown finished) or the timeout expires. Returns immediately
    /// when the module is absent or refcnt is unreadable.
    /// </summary>
    private static void WaitForNvidiaRefcntSettle(int timeoutMs)
    {
        string path = TestPathPrefix + "/sys/module/nvidia/refcnt";
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

    // Mirror of supergfxctl pci_device.rs:673 (iter.rev() unbind before power change).
    private record UnbindRecord(string Bdf, string DriverName);

    private static bool ReleaseNvidiaModulesAndPurgeHolders(string? dgpuBdf, out List<UnbindRecord> unbindStack)
    {
        unbindStack = new List<UnbindRecord>();

        if (!string.IsNullOrEmpty(dgpuBdf))
        {
            // Signal the compositor to release the DRM device gracefully before
            // we yank the driver.
            TrySignalDrmRemove(dgpuBdf!);

            var funcs = EnumerateDgpuFunctions(dgpuBdf!);
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

            bool drmGone = !Directory.Exists(TestPathPrefix + "/sys/module/nvidia_drm");
            bool nvidiaGone = !Directory.Exists(TestPathPrefix + "/sys/module/nvidia");
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

    private static List<UnbindRecord> EnumerateDgpuFunctions(string dgpuBdf)
    {
        // dgpuBdf = "0000:01:00.0" -> prefix "0000:01:00"
        int dotIx = dgpuBdf.LastIndexOf('.');
        if (dotIx < 0)
            return new List<UnbindRecord>();
        string prefix = dgpuBdf.Substring(0, dotIx);

        string root = TestPathPrefix + "/sys/bus/pci/devices/";
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
    private static List<string> EnumerateDgpuDeviceNodes(string dgpuBdf)
    {
        var results = new List<string>();
        int dotIx = dgpuBdf.LastIndexOf('.');
        string prefix = dotIx < 0 ? dgpuBdf : dgpuBdf.Substring(0, dotIx);
        string root = TestPathPrefix + "/sys/bus/pci/devices/";
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

    private static bool TryUnbindFunction(UnbindRecord rec)
    {
        if (RunPciAction("pci-unbind", rec.DriverName, rec.Bdf, sudoTimeoutMs: 30000))
            return true;

        // The unbind write blocks in the kernel while GPU contexts of freshly
        // killed processes are torn down; our sudo timeout abandons the helper
        // but the write keeps running and usually completes moments later.
        // Declaring failure here while the kernel finishes the unbind leaves
        // the device driverless with no rollback record - poll for the late
        // completion before giving up.
        Logger.WriteLine($"GPUModeControl: unbind {rec.Bdf} timed out - polling for late completion");
        string driverLink = TestPathPrefix + $"/sys/bus/pci/devices/{rec.Bdf}/driver";
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
        => RunPciAction("pci-bind", rec.DriverName, rec.Bdf);

    private static void RollbackUnbinds(List<UnbindRecord> stack)
    {
        if (stack.Count == 0)
            return;
        // Rebind in reverse: graphics (.0) before audio (.1) so audio power gating works.
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            var rec = stack[i];
            string driverPath = TestPathPrefix + $"/sys/bus/pci/drivers/{rec.DriverName}";
            if (!Directory.Exists(driverPath))
            {
                Logger.WriteLine($"GPUModeControl: rollback skip {rec.Bdf} - driver {rec.DriverName} no longer registered (reboot to recover)");
                continue;
            }
            bool ok = TryRebindFunction(rec);
            Logger.WriteLine($"GPUModeControl: rollback rebind {rec.Bdf} -> {rec.DriverName} = {(ok ? "OK" : "FAILED")}");
        }
    }

    private static bool RunPciAction(string action, string driver, string bdf, int sudoTimeoutMs = 10000)
    {
        var r = SysfsHelper.RunSudoOrPkexec(
            SysfsHelper.GpuHelperPath, new[] { action, driver, bdf },
            sudoTimeoutMs: sudoTimeoutMs, pkexecTimeoutMs: 60000);
        return r != null;
    }

    private static bool RunPciRemove(string bdf)
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
    private static void SetDgpuRuntimePmAuto()
    {
        string? bdf = FindDgpuPciDevice()?.bdf;
        if (string.IsNullOrEmpty(bdf))
            return;
        foreach (var node in EnumerateDgpuDeviceNodes(bdf!))
        {
            if (File.Exists(TestPathPrefix + $"/sys/bus/pci/devices/{node}/power/control"))
                RunPciPower(node, "auto");
        }

        // The recovery ladder may have forced the parent bridge to power/control=on;
        // return it to the kernel default so the link can runtime-suspend again.
        string? bridge = ResolveDgpuBridge();
        if (!string.IsNullOrEmpty(bridge)
            && File.Exists(TestPathPrefix + $"/sys/bus/pci/devices/{bridge}/power/control"))
            RunPciPower(bridge!, "auto");
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
    private static void ApplyVulkanIcd(bool dgpuAvailable)
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
    private static void RestartStoppedHolderServices()
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

    private static void LogHoldersSnapshot(string reason)
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

    private bool TryReleaseAmdDriver()
    {
        string? pciAddr = FindDgpuPciAddress();
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
        ResolveDgpuSlot();

        var funcs = EnumerateDgpuFunctions(pciAddr);
        funcs.Reverse(); // highest function first (.1 audio before .0 graphics)
        foreach (var rec in funcs)
        {
            Logger.WriteLine($"GPUModeControl: unbinding {rec.Bdf} from {rec.DriverName}");
            RunPciAction("pci-unbind", rec.DriverName, rec.Bdf);
            Logger.WriteLine($"GPUModeControl: removing {rec.Bdf} from PCI bus");
            RunPciRemove(rec.Bdf);
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

    // Hardware detection helpers

    /// <summary>True if the NVIDIA kernel module is loaded.</summary>
    private static bool IsNvidiaGpu()
    {
        return Directory.Exists(TestPathPrefix + "/sys/module/nvidia");
    }

    /// <summary>True if an AMD discrete GPU is present (vendor=0x1002, boot_vga=0).</summary>
    private bool IsAmdDgpu()
    {
        return FindDgpuPciAddress() != null;
    }

    /// <summary>Read /sys/module/nvidia_drm/refcnt. Returns -1 if not readable.</summary>
    private static int ReadNvidiaDrmRefcount()
    {
        return SysfsHelper.ReadInt(TestPathPrefix + "/sys/module/nvidia_drm/refcnt", -1);
    }

    /// <summary>Read power/runtime_status for a PCI device. Returns "active"/"suspended"/etc.</summary>
    private static string ReadDgpuRuntimeStatus(string pciAddr)
    {
        string path = TestPathPrefix + $"/sys/bus/pci/devices/{pciAddr}/power/runtime_status";
        return SysfsHelper.ReadAttribute(path) ?? "active";
    }

    /// <summary>
    /// runtime PM status ("active"/"suspended") of the first non-boot
    /// discrete GPU (NVIDIA or AMD) on the PCI bus, or null when none is
    /// visible (no dGPU, or Eco removed it from the bus). Reading
    /// runtime_status never wakes the device.
    /// </summary>
    public static string? DgpuRuntimeStatus()
    {
        try
        {
            string pciDir = TestPathPrefix + "/sys/bus/pci/devices";
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

    /// <summary>
    /// Scan /sys/bus/pci/devices for the AMD discrete GPU.
    /// Looks for vendor=0x1002, class=0x0300xx or 0x0302xx, boot_vga=0.
    /// Caches the result.
    /// </summary>
    private string? FindDgpuPciAddress()
    {
        if (_dgpuPciScanned)
            return _cachedDgpuPciAddress;
        _dgpuPciScanned = true;

        try
        {
            string pciDir = "/sys/bus/pci/devices";
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
    private static string? FindNvidiaPciAddress()
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

    // Boot safety (supergfxctl pattern)

    /// <summary>
    /// Detect and fix the impossible state: gpu_mux_mode=0 (Ultimate) + dgpu_disable=1.
    /// This causes boot hangs because the dGPU is the sole display output in MUX=0
    /// but it's been powered off. Force dgpu_disable=0 to recover.
    ///
    /// Also removes any stale block artifacts that could prevent the dGPU driver from loading.
    ///
    /// Inspired by supergfxctl's asus_boot_safety_check().
    /// </summary>
    private void BootSafetyCheck()
    {
        try
        {
            if (AppConfig.NoGpu() || AppConfig.IsAMDiGPU())
                return;

            // PCI backend has no MUX hardware and no live dgpu_disable, so
            // the impossible-state pair this check defends against simply
            // cannot occur. Skip silently to avoid touching WMI sysfs that
            // may not exist on non-ASUS systems.
            if (AppConfig.IsPciGpuBackend())
                return;

            int mux = _wmi.GetGpuMuxMode();
            bool ecoEnabled = _wmi.GetGpuEco();

            if (mux == 0 && ecoEnabled)
            {
                Logger.WriteLine("GPUModeControl: BOOT SAFETY - MUX=0 + dgpu_disable=1 is impossible!");
                Logger.WriteLine("GPUModeControl: BOOT SAFETY - forcing dgpu_disable=0 to recover");
                _wmi.SetGpuEco(false);
                // Remove block artifacts to allow dGPU driver to load after recovery
                RemoveDriverBlock();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: BootSafetyCheck failed: {ex.Message}");
        }
    }

    // Driver block - prevent dGPU driver loading + remove PCI devices for Eco boot

    /// <summary>
    /// Optional root prefix for the four ghelper system paths. Empty in
    /// production (paths resolve under real /etc); the test harness sets
    /// <c>GHELPER_TEST_ROOT=/tmp/scenario-N</c> so writes are confined to a
    /// sandbox and the sudo / pkexec branches can be skipped.
    /// Mirrors the same env var the boot-script test harness uses.
    /// </summary>
    internal static readonly string TestPathPrefix =
        Environment.GetEnvironmentVariable("GHELPER_TEST_ROOT") ?? "";

    internal static bool IsTestMode => !string.IsNullOrEmpty(TestPathPrefix);

    /// <summary>Path to modprobe.d file that blocks dGPU driver loading (NVIDIA + AMD).</summary>
    internal static readonly string ModprobeBlockPath = TestPathPrefix + "/etc/modprobe.d/ghelper-gpu-block.conf";

    /// <summary>Path to udev rule that removes dGPU PCI devices from the bus (NVIDIA + AMD).</summary>
    internal static readonly string UdevRemovePath = TestPathPrefix + "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules";

    /// <summary>Path to trigger file read by ghelper on startup.</summary>
    internal static readonly string TriggerPath = TestPathPrefix + "/etc/ghelper/pending-gpu-mode";

    /// <summary>
    /// Persistent Eco marker. Unlike TriggerPath (consumed after one boot),
    /// this file survives boot-script cleanup. When present and TriggerPath is
    /// absent, the boot script treats its content as the pending mode, making
    /// Eco survive reboots on firmware that forgets dgpu_disable.
    /// </summary>
    internal static readonly string PersistentTriggerPath = TestPathPrefix + "/etc/ghelper/persistent-gpu-mode";

    /// <summary>Path to backend selector file. Content "asus-wmi" or "pci".</summary>
    internal static readonly string BackendPath = TestPathPrefix + "/etc/ghelper/backend";

    /// <summary>
    /// In-process replacement for the sudo/pkexec helper calls when running
    /// under the C# test harness. Performs the exact same file-system effect
    /// the helper script would, but without any privilege escalation.
    /// Mirrors gpu-block-helper.sh write/clean/set-backend semantics.
    /// </summary>
    private static void RunHelperInTestMode(string action, GpuMode? target = null, string? backend = null)
    {
        try
        {
            string? mkdir(string p)
            { var d = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d); return d; }
            switch (action)
            {
                case "write":
                    string modeStr = (target ?? GpuMode.Eco) switch
                    {
                        GpuMode.Eco => "eco",
                        GpuMode.Standard => "standard",
                        GpuMode.Optimized => "optimized",
                        GpuMode.Ultimate => "ultimate",
                        _ => "eco",
                    };
                    string be = backend ?? "asus-wmi";
                    mkdir(BackendPath);
                    File.WriteAllText(BackendPath, be);
                    if (modeStr == "eco")
                    {
                        mkdir(ModprobeBlockPath);
                        File.WriteAllText(ModprobeBlockPath, ModprobeBlockContent);
                        mkdir(UdevRemovePath);
                        File.WriteAllText(UdevRemovePath, UdevRemoveContent);
                    }
                    else
                    {
                        if (File.Exists(ModprobeBlockPath))
                            File.Delete(ModprobeBlockPath);
                        if (File.Exists(UdevRemovePath))
                            File.Delete(UdevRemovePath);
                    }
                    mkdir(TriggerPath);
                    File.WriteAllText(TriggerPath, modeStr);
                    break;
                case "clean":
                    if (File.Exists(ModprobeBlockPath))
                        File.Delete(ModprobeBlockPath);
                    if (File.Exists(UdevRemovePath))
                        File.Delete(UdevRemovePath);
                    if (File.Exists(TriggerPath))
                        File.Delete(TriggerPath);
                    break;
                case "set-backend":
                    mkdir(BackendPath);
                    File.WriteAllText(BackendPath, backend ?? "asus-wmi");
                    break;
                case "persist":
                    mkdir(PersistentTriggerPath);
                    File.WriteAllText(PersistentTriggerPath, "eco");
                    break;
                case "unpersist":
                    if (File.Exists(PersistentTriggerPath))
                        File.Delete(PersistentTriggerPath);
                    break;
                case "live-standard":
                    // Mirror the bash helper: remove blocks + trigger + persistent marker.
                    // udevadm + PCI rescan + modprobe are out-of-scope
                    // for a userland test sandbox (no real kernel).
                    if (File.Exists(ModprobeBlockPath))
                        File.Delete(ModprobeBlockPath);
                    if (File.Exists(UdevRemovePath))
                        File.Delete(UdevRemovePath);
                    if (File.Exists(TriggerPath))
                        File.Delete(TriggerPath);
                    if (File.Exists(PersistentTriggerPath))
                        File.Delete(PersistentTriggerPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"RunHelperInTestMode({action}) failed: {ex.Message}");
        }
    }

    /// <summary>Known locations for the GPU block helper script (installed by install-local.sh).</summary>
    private static readonly string[] HelperSearchPaths = new[]
    {
        "/usr/local/lib/ghelper/gpu-block-helper.sh",
        "/opt/ghelper/gpu-block-helper.sh",
    };

    /// <summary>Cached path to the helper script. Null if not found.</summary>
    private static string? _cachedHelperPath;
    private static bool _helperPathScanned;

    /// <summary>
    /// Find the GPU block helper script. Checked once and cached.
    /// Returns null if not found (falls back to pkexec).
    /// </summary>
    private static string? FindHelperScript()
    {
        if (_helperPathScanned)
            return _cachedHelperPath;
        _helperPathScanned = true;

        // NixOS: module puts gpu-block-helper.sh on PATH
        var nixPath = Platform.Linux.NixOS.ResolveGpuBlockHelper();
        if (nixPath != null)
        {
            _cachedHelperPath = nixPath;
            Logger.WriteLine($"GPUModeControl: GPU block helper found at {nixPath} (NixOS)");
            return nixPath;
        }

        foreach (var path in HelperSearchPaths)
        {
            if (File.Exists(path))
            {
                _cachedHelperPath = path;
                Logger.WriteLine($"GPUModeControl: GPU block helper found at {path}");
                return path;
            }
        }

        Logger.WriteLine("GPUModeControl: GPU block helper not found - will use pkexec fallback");
        return null;
    }

    /// <summary>Content for the modprobe.d block file (vendor-aware: NVIDIA + AMD).</summary>
    private const string ModprobeBlockContent =
        "# ghelper: block dGPU driver modules so dGPU can be safely disabled on next boot\n" +
        "# Auto-generated - will be removed after Eco mode is applied\n" +
        "# Uses 'install /bin/false' (strongest block - prevents loading by ANY means)\n" +
        "# NVIDIA modules\n" +
        "install nvidia /bin/false\n" +
        "install nvidia_drm /bin/false\n" +
        "install nvidia_modeset /bin/false\n" +
        "install nvidia_uvm /bin/false\n" +
        "install nvidia_wmi_ec_backlight /bin/false\n" +
        "# Open-source NVIDIA driver\n" +
        "install nouveau /bin/false\n" +
        "# AMD dGPU driver\n" +
        "install amdgpu /bin/false\n";

    /// <summary>Content for the udev rule that PCI-removes dGPU devices (NVIDIA + AMD) on add.</summary>
    private const string UdevRemoveContent =
        "# ghelper: remove dGPU PCI devices so no driver can bind\n" +
        "# Auto-generated - will be removed after Eco mode is applied\n" +
        "# Remove NVIDIA VGA controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA 3D controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030200\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA Audio devices (HDMI audio on dGPU)\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA USB xHCI Host Controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c0330\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA USB Type-C UCSI devices\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c8000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU VGA controller (boot_vga!=1 protects the iGPU)\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030000\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU 3D controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030200\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU Audio devices\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n";

    /// <summary>
    /// Push the backend selector marker (/etc/ghelper/backend) without
    /// touching any block artifacts. Called when the user toggles the
    /// "Use PCI dGPU disable" checkbox so the boot service sees the new
    /// backend on the next boot even if the user never schedules a mode
    /// change. Idempotent - no-op if the marker already matches.
    /// </summary>
    public void PushBackendMarker(string backend)
    {
        if (backend != "pci" && backend != "asus-wmi")
        {
            Logger.WriteLine($"GPUModeControl: PushBackendMarker rejected invalid backend '{backend}'");
            return;
        }

        // The MUX=0 latch flag and in-memory pending latch encode REAL
        // firmware state (gpu_mux_mode=0 is pending an actual reboot). The
        // backend selector is a UI preference - toggling it does not change
        // firmware state. Clearing the latch here would re-introduce the
        // exact impossible-state chain this guard is designed to prevent:
        // user clicks Ultimate -> toggles PCI -> clicks Eco -> blocks
        // written -> reboot -> MUX=0 settles + udev removes dGPU = black
        // screen. Keep the latch; rely on WouldCreateImpossibleState to
        // refuse the subsequent Eco click and surface the new toast.

        try
        {
            // Skip the privileged call when the file already reflects the
            // chosen backend. Saves a polkit prompt on every checkbox tick.
            if (File.Exists(BackendPath))
            {
                string current = File.ReadAllText(BackendPath).Trim();
                if (current == backend)
                    return;
            }

            if (IsTestMode)
            {
                RunHelperInTestMode("set-backend", backend: backend);
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Match the WriteDriverBlock invocation style - no nested
                // quoting, no `sh -c`. The helper has a dedicated
                // `set-backend` subcommand that writes only the marker.
                Logger.WriteLine($"GPUModeControl: PushBackendMarker via helper: {backend}");
                SysfsHelper.RunSudoOrPkexec(helper, new[] { "set-backend", backend }, sudoTimeoutMs: 30000, pkexecTimeoutMs: 60000);
            }
            else
            {
                // pkexec fallback uses RunPkexecBash so the script body is
                // passed as a single argument (no quoting hazards).
                Logger.WriteLine($"GPUModeControl: PushBackendMarker via pkexec: {backend}");
                SysfsHelper.RunPkexecBash(
                    $"mkdir -p /etc/ghelper\necho {backend} > {BackendPath}\nchmod 644 {BackendPath}");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: PushBackendMarker failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Live PCI Eco → Standard recovery: atomically remove the modprobe
    /// block + udev hot-remove rule + trigger file, reload udev so the
    /// rule is forgotten, rescan the PCI bus to re-enumerate the dGPU,
    /// and kick modprobe explicitly to bring the driver back. No reboot
    /// required. Mirrors the asus-wmi SetGpuEco(false) live path.
    ///
    /// Returns Applied on success (verified by block files being gone),
    /// Failed otherwise. Callers fall through to the deferred-reboot path
    /// when this returns Failed so the user can still escape Eco mode.
    /// </summary>
    private GpuSwitchResult TryLiveRemovePciBlocks()
    {
        try
        {
            if (IsTestMode)
            {
                // Honour the test-harness failure switch so the
                // C# scenario suite can exercise the fall-through path.
                if (Environment.GetEnvironmentVariable("GHELPER_TEST_FAIL_LIVE_STANDARD") == "1")
                {
                    Logger.WriteLine("TryLiveRemovePciBlocks: test harness requested failure");
                    return GpuSwitchResult.Failed;
                }
                RunHelperInTestMode("live-standard");
            }
            else
            {
                string? helper = FindHelperScript();
                if (helper != null)
                {
                    Logger.WriteLine($"GPUModeControl: live Eco→Standard via helper: {helper}");
                    SysfsHelper.RunSudoOrPkexec(helper, new[] { "live-standard" }, sudoTimeoutMs: 30000, pkexecTimeoutMs: 60000);
                }
                else
                {
                    Logger.WriteLine("GPUModeControl: live Eco→Standard via pkexec fallback");
                    // RunPkexecBash passes the script body as a single
                    // ArgumentList entry, so the interpolated path
                    // constants below cannot escape into argv parsing.
                    // The paths themselves are private const strings -
                    // not user input.
                    string script =
                        $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}\n" +
                        "udevadm control --reload-rules 2>/dev/null || true\n" +
                        "echo 1 > /sys/bus/pci/rescan 2>/dev/null || true\n" +
                        "modprobe nvidia 2>/dev/null || true\n" +
                        "modprobe amdgpu 2>/dev/null || true";
                    SysfsHelper.RunPkexecBash(script);
                }
            }

            // Verify the recovery actually happened. The helper script
            // returns 0 even when individual operations fail (the trailing
            // `|| true`); the only reliable signal is file-state.
            bool stillBlocked = File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);
            if (stillBlocked)
                return GpuSwitchResult.Failed;

            OnLivePciTransition?.Invoke();
            return GpuSwitchResult.Applied;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TryLiveRemovePciBlocks failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    /// <summary>
    /// Remove all dGPU PCI functions from the bus. Reduces power draw on
    /// systems without firmware dgpu_disable. The udev rule prevents
    /// re-enumeration on rescan; live-standard reverses this.
    /// </summary>
    private void PciRemoveDgpuFunctions()
    {
        var dev = FindDgpuPciDevice();
        if (dev == null)
        {
            Logger.WriteLine("GPUModeControl: PciRemoveDgpuFunctions - no dGPU on bus");
            return;
        }

        ResolveDgpuSlot();
        ResolveDgpuBridge();

        foreach (var node in EnumerateDgpuDeviceNodes(dev.Value.bdf))
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
    private void DropDgpuPciNodes()
    {
        if (IsTestMode)
            return;
        try
        { PciRemoveDgpuFunctions(); }
        catch (Exception ex)
        { Logger.WriteLine($"GPUModeControl: DropDgpuPciNodes failed: {ex.Message}"); }
    }

    /// <summary>
    /// True if any driver block artifacts (current or legacy) exist on disk.
    /// Used to decide if cleanup is needed - avoids unnecessary pkexec prompts.
    /// BackendPath is intentionally excluded - it is a persistent user
    /// preference managed independently of Eco state.
    /// </summary>
    private static bool DriverBlockExists()
    {
        return File.Exists(ModprobeBlockPath)
            || File.Exists(UdevRemovePath)
            || File.Exists(TriggerPath);
    }

    /// <summary>
    /// Write three artifacts that prevent dGPU drivers from loading on the next boot,
    /// allowing ghelper to safely write dgpu_disable=1 at startup.
    ///
    ///
    /// 1. modprobe.d `install /bin/false` - the STRONGEST modprobe block.
    ///    Unlike `blacklist` (which only prevents autoload and can be overridden
    ///    by dependencies), `install /bin/false` replaces `modprobe nvidia` with
    ///    a no-op. Blocks both NVIDIA and AMD dGPU modules.
    ///
    /// 2. udev rule `ATTR{remove}="1"` - belt and suspenders.
    ///    Physically removes all dGPU PCI devices from the bus when they appear.
    ///    Even if the modprobe block somehow fails (e.g. nvidia in initramfs),
    ///    there's no PCI device for the driver to bind to.
    ///
    /// 3. Trigger file `/etc/ghelper/pending-gpu-mode` - tells ghelper on
    ///    startup to write dgpu_disable=1 and clean up.
    ///
    /// Prefers the sudo helper script (installed by install-local.sh) which needs
    /// no tty/polkit and works from autostart. Falls back to pkexec if the helper
    /// is not installed.
    ///
    /// Only Eco needs the block. All other modes want the dGPU driver available.
    /// </summary>
    private void WriteDriverBlock(GpuMode target)
    {
        try
        {
            bool isPci = AppConfig.IsPciGpuBackend();
            bool isEco = (target == GpuMode.Eco);

            if (!isEco && !isPci)
            {
                // ASUS WMI backend, non-eco target: dGPU driver should be
                // available immediately, no block artifacts needed.
                RemoveDriverBlock();
                return;
            }

            // SAFETY: Never write Eco block artifacts when MUX is latched to 0 (Ultimate).
            // After reboot, MUX=0 means dGPU is the sole display - blocking dGPU driver and
            // writing dgpu_disable=1 would cause a black screen (impossible state).
            // WouldCreateImpossibleState only triggers on ASUS hardware with a real MUX;
            // PCI backend on non-ASUS naturally returns false.
            if (isEco && WouldCreateImpossibleState(target))
            {
                Logger.WriteLine("GPUModeControl: WriteDriverBlock REFUSED - Eco + MUX=0 is impossible state, removing any stale artifacts instead");
                RemoveDriverBlock();
                return;
            }

            // Convert target mode to string for trigger file (boot script reads this)
            string modeStr = target switch
            {
                GpuMode.Eco => "eco",
                GpuMode.Standard => "standard",
                GpuMode.Optimized => "optimized",
                GpuMode.Ultimate => "ultimate",
                _ => "eco"
            };

            // Pass the active backend through to the helper so it writes the
            // marker file the boot script reads. Default "asus-wmi" preserves
            // legacy behaviour for any caller that hasn't opted into PCI mode.
            string backend = AppConfig.GetGpuBackend();

            if (isEco)
                Logger.WriteLine($"GPUModeControl: writing driver block (modprobe + udev + trigger=eco, backend={backend})");
            else
                Logger.WriteLine($"GPUModeControl: PCI backend - writing trigger={modeStr} (no blocks, backend={backend})");

            if (IsTestMode)
            {
                // Bypass privilege escalation entirely under the test harness.
                RunHelperInTestMode("write", target, backend);
            }
            else
            {
                string? helper = FindHelperScript();
                if (helper != null)
                {
                    Logger.WriteLine($"GPUModeControl: using helper: {helper}");
                    SysfsHelper.RunSudoOrPkexec(helper, new[] { "write", modeStr, backend }, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
                }
                else
                {
                    // Fallback: pkexec with inline content. Eco writes the full
                    // block artifact set; non-eco (only reachable in PCI mode
                    // here) writes just the trigger + backend marker and removes
                    // any stale modprobe/udev artifacts.
                    Logger.WriteLine($"GPUModeControl: using pkexec fallback (mode={modeStr}, backend={backend})");
                    string script;
                    if (isEco)
                    {
                        script = $"mkdir -p /etc/ghelper\n" +
                            $"cat > {ModprobeBlockPath} << 'GHELPER_BLOCK'\n{ModprobeBlockContent}GHELPER_BLOCK\n" +
                            $"chmod 644 {ModprobeBlockPath}\n" +
                            $"cat > {UdevRemovePath} << 'GHELPER_BLOCK'\n{UdevRemoveContent}GHELPER_BLOCK\n" +
                            $"chmod 644 {UdevRemovePath}\n" +
                            $"echo {modeStr} > {TriggerPath}\n" +
                            $"echo {backend} > {BackendPath}\n" +
                            $"chmod 644 {BackendPath}";
                    }
                    else
                    {
                        script = $"mkdir -p /etc/ghelper\n" +
                            $"rm -f {ModprobeBlockPath} {UdevRemovePath}\n" +
                            $"echo {modeStr} > {TriggerPath}\n" +
                            $"echo {backend} > {BackendPath}\n" +
                            $"chmod 644 {BackendPath}";
                    }
                    SysfsHelper.RunPkexecBash(script);
                }
            }

            if (File.Exists(TriggerPath))
            {
                Logger.WriteLine($"GPUModeControl: trigger written successfully (mode={modeStr})");
                if (isEco)
                {
                    Logger.WriteLine($"  modprobe: {ModprobeBlockPath}");
                    Logger.WriteLine($"  udev:     {UdevRemovePath}");
                }
                Logger.WriteLine($"  trigger:  {TriggerPath} (content: {modeStr})");
                Logger.WriteLine($"  backend:  {BackendPath} (content: {backend})");
            }
            else
            {
                Logger.WriteLine("GPUModeControl: driver block write failed (pkexec cancelled or error)");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: WriteDriverBlock failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove all driver block artifacts (current + legacy from previous approaches).
    /// Called when:
    /// - Eco is applied live (dgpu_disable=1 is persistent, block no longer needed)
    /// - Switching away from Eco (dGPU driver should be loadable)
    /// - Startup confirms hardware matches config
    /// - Boot safety check (MUX=0 + dgpu_disable=1 recovery)
    ///
    /// Prefers sudo helper (no tty needed). Falls back to pkexec.
    /// No-op if no artifacts exist (avoids unnecessary sudo/pkexec calls).
    /// </summary>
    private void RemoveDriverBlock()
    {
        try
        {
            if (!DriverBlockExists())
                return; // Nothing to remove

            Logger.WriteLine("GPUModeControl: removing driver block artifacts (current + legacy)");

            if (IsTestMode)
            {
                RunHelperInTestMode("clean");
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Helper `clean` removes only the ephemeral Eco artifacts.
                // The backend marker is a persistent user preference and is
                // intentionally preserved here so the next boot still uses
                // the correct backend.
                SysfsHelper.RunSudoOrPkexec(helper, new[] { "clean" }, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
            }
            else
            {
                SysfsHelper.RunCommandWithTimeout("pkexec",
                    $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}", 120000);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: RemoveDriverBlock failed: {ex.Message}");
        }
    }
    /// <summary>
    /// Write or remove the persistent Eco marker via the helper script.
    /// When <paramref name="persistent"/> is true, the boot script will
    /// re-apply <c>dgpu_disable=1</c> on every boot even after the one-shot
    /// trigger is consumed. When false, the marker is removed and boot
    /// behaviour returns to one-shot.
    /// </summary>
    private void SyncPersistentMarkerToDisk(bool present)
    {
        try
        {
            string action = present ? "persist" : "unpersist";
            Logger.WriteLine($"GPUModeControl: sync persistent marker to disk: {action}");

            if (IsTestMode)
            {
                RunHelperInTestMode(action, GpuMode.Eco);
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                var args = present
                    ? new[] { "persist", "eco" }
                    : new[] { "unpersist" };
                SysfsHelper.RunSudoOrPkexec(helper, args, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
            }
            else
            {
                string script = present
                    ? $"mkdir -p /etc/ghelper && echo eco > {PersistentTriggerPath} && chmod 644 {PersistentTriggerPath}"
                    : $"rm -f {PersistentTriggerPath}";
                SysfsHelper.RunPkexecBash(script);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: SyncPersistentMarkerToDisk({present}) failed: {ex.Message}");
        }
    }

    internal void SetEcoPersistent(bool persistent)
    {
        SyncPersistentMarkerToDisk(persistent);
        AppConfig.Set("gpu_eco_persistent", persistent ? 1 : 0);

        if (persistent)
            Logger.WriteLine($"GPUModeControl: persistent Eco enabled ({PersistentTriggerPath})");
        else
            Logger.WriteLine("GPUModeControl: persistent Eco disabled");
    }

    /// <summary>Check whether the persistent Eco marker file exists on disk.</summary>
    internal static bool IsEcoPersistentOnDisk()
    {
        try
        { return File.Exists(PersistentTriggerPath); }
        catch { return false; }
    }

    /// <summary>Check config flag (UI state). May be true even before the file is written.</summary>
    internal static bool IsEcoPersistentConfig()
    {
        return AppConfig.Is("gpu_eco_persistent");
    }

    // Config helpers

    private static void SaveModeToConfig(GpuMode mode)
    {
        string modeStr = mode switch
        {
            GpuMode.Eco => "eco",
            GpuMode.Standard => "standard",
            GpuMode.Optimized => "optimized",
            GpuMode.Ultimate => "ultimate",
            _ => "standard"
        };
        AppConfig.Set("gpu_mode", modeStr);
    }

    private static GpuMode ParseGpuMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "eco" => GpuMode.Eco,
            "standard" => GpuMode.Standard,
            "optimized" => GpuMode.Optimized,
            "ultimate" => GpuMode.Ultimate,
            _ => GpuMode.Standard
        };
    }
}

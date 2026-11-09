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
/// sysfs directly. The controller owns the lock and the MUX guard; the configured
/// <see cref="IGpuBackend"/> adapter computes the delta and runs it with safety checks.
///
/// See GPU_MODE_PLAN.md for the complete scenario matrix and flows.
/// </summary>
public class GPUModeControl
{
    private readonly IHardwareControl _wmi;
    private readonly MuxLatch _mux;

    /// <summary>Lock to prevent concurrent GPU mode operations.
    /// Writing dgpu_disable can block for 30-60 seconds - we must not queue multiple writes.
    /// SemaphoreSlim(1,1) provides atomic check-and-acquire, eliminating the TOCTOU race
    /// that existed with the previous volatile bool approach.</summary>
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    private readonly FirmwareGpuBackend _firmware;
    private readonly PciGpuBackend _pci;
    private readonly BpfGpuBackend _bpf;

    /// <summary>The adapter for the configured gpu_backend; read per call, the user can change it live.</summary>
    private IGpuBackend Backend => AppConfig.IsPciGpuBackend() ? _pci : AppConfig.IsBpfGpuBackend() ? _bpf : _firmware;

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
        _mux = new MuxLatch(wmi);
        _firmware = new FirmwareGpuBackend(wmi, power, _mux, _switchLock);
        _pci = new PciGpuBackend(_mux);
        _bpf = new BpfGpuBackend(wmi, power, _mux, _switchLock, _firmware);
        DgpuPci.ResetAmdCache();
    }

    /// <summary>True if a GPU mode switch is currently in progress (sysfs write blocking).</summary>
    public bool IsSwitchInProgress => _switchLock.CurrentCount == 0;

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

            var result = Backend.Switch(target);

            // Sync the on-disk persistent marker with the current mode.
            // Entering Eco writes the marker so the boot script re-applies Eco.
            // Leaving Eco removes the marker so the boot script won't force Eco.
            // The config/checkbox is NOT touched here (user preference survives).
            // ebpf owns its marker (that backend's only persistent state), so skip it here.
            if (!Backend.OwnsPersistentMarker && IsEcoPersistentConfig())
                GpuBootFiles.SyncPersistent(target == GpuMode.Eco);

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
    public GpuSwitchResult TryReleaseAndSwitch(GpuMode target = GpuMode.Eco)
    {
        if (!_switchLock.Wait(0))
            return GpuSwitchResult.AlreadySet;

        try
        {
            Logger.WriteLine("GPUModeControl: TryReleaseAndSwitch - attempting driver release");
            DgpuDriver.LogHoldersSnapshot("pre-release");

            return Backend.ReleaseAndSwitch(target);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: TryReleaseAndSwitch failed: {ex.Message}");
            SaveModeToConfig(GpuMode.Eco);
            return GpuSwitchResult.Deferred;
        }
        finally
        {
            DgpuDriver.RestartStoppedHolderServices();
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
        if (_mux.WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GPUModeControl: ScheduleModeForReboot REFUSED - would create impossible post-reboot state (Eco + MUX=0)");
            Logger.WriteLine("GPUModeControl: user must reboot into Ultimate first, THEN switch to Eco");
            GpuBootFiles.Clean();
            return GpuSwitchResult.EcoBlocked;
        }

        if (Backend.ScheduleForReboot(target) is { } own)
            return own;

        SaveModeToConfig(target);

        // If target needs MUX change, latch it now (instant, safe)
        // Use _mux.Effective - if we already latched a MUX change this session,
        // we need to know the LATCHED value, not the stale hardware readback.
        int effectiveMux = _mux.Effective;
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
                    GpuBootFiles.Clean();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GPUModeControl: ScheduleModeForReboot - failed to exit Eco: {ex.Message}");
                    // Can't latch MUX, but config is saved - ApplyPendingOnStartup will retry
                    GpuBootFiles.Write(target, _mux);
                    return GpuSwitchResult.RebootRequired;
                }
            }

            Logger.WriteLine($"GPUModeControl: latching MUX {effectiveMux} → {targetMux}");
            try
            {
                _mux.Write(targetMux);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: ScheduleModeForReboot - MUX write failed: {ex.Message}");
                // Config is saved - ApplyPendingOnStartup will retry
            }
        }

        // Write driver block so Eco can be applied safely after reboot.
        // For non-Eco targets, this removes any stale block artifacts.
        GpuBootFiles.Write(target, _mux);
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
        MuxLatch.ClearStaleFlag();

        if (AppConfig.NoGpu() || AppConfig.IsAMDiGPU())
        {
            Logger.WriteLine("GPUModeControl: APU-only system (NoGpu/IsAMDiGPU) - skipping startup GPU probe");
            return GpuSwitchResult.AlreadySet;
        }

        var backend = Backend;
        var result = backend.ApplyPendingOnStartup();
        // ebpf hands over to the marker's backend when the boot service never got ebpf.
        return Backend == backend ? result : Backend.ApplyPendingOnStartup();
    }

    /// <summary>
    /// On shutdown (SIGTERM/SIGINT): best-effort dgpu_disable=1 if Eco is pending.
    /// Still checks driver safety - SIGINT is not a system shutdown (Xorg is still running),
    /// and even during SIGTERM the display stack may not have released the GPU yet.
    /// If driver is active, skip - ApplyPendingOnStartup() will try on next boot.
    /// </summary>
    public void ApplyPendingOnShutdown() => Backend.ApplyPendingOnShutdown();

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

        return Backend.AutoSwitch();
    }

    /// <summary>
    /// Read current hardware state + config → return the current mode enum.
    /// </summary>
    public GpuMode GetCurrentMode() => Backend.GetCurrentMode();

    /// <summary>
    /// True if config gpu_mode differs from current hardware state
    /// (mode is waiting for a reboot to take effect).
    /// </summary>
    public bool IsPendingReboot() => Backend.IsPendingReboot();

    /// <summary>ebpf Optimized and Eco: re-decide the filter after a display or power change. See <see cref="BpfGpuBackend.Reconcile"/>.</summary>
    public void ReconcileBpfAuto() => _bpf.Reconcile();

    /// <summary>Cleanup when the user changes the GPU backend. See <see cref="BpfGpuBackend.OnBackendChanged"/>.</summary>
    public void OnBackendChanged(string oldBackend, string newBackend) => _bpf.OnBackendChanged(oldBackend, newBackend);

    /// <summary>True when the dGPU driver holds the hardware. See <see cref="DgpuDriver.IsActive"/>.</summary>
    public bool IsDgpuDriverActive() => DgpuDriver.IsActive();

    /// <summary>Cache the dGPU slot and bridge while the device is visible; age out a stale cache.</summary>
    public void CacheDgpuSlotIfPresent() => DgpuPci.CacheSlot();

    /// <summary>runtime PM status of the dGPU, or null when none is visible. See <see cref="DgpuPci.RuntimeStatus"/>.</summary>
    public static string? DgpuRuntimeStatus() => DgpuPci.RuntimeStatus();

    /// <summary>
    /// Universal, vendor-neutral, Eco-resilient test for whether this machine
    /// has a second GPU at all. GPU mode switching (Eco / Standard / Ultimate),
    /// the GPU backend selector and the GPU boot integration only make sense
    /// with two GPUs; with a single (integrated) GPU only tuning applies.
    ///
    ///   1. Live PCI scan: two or more display-class (0x03xxxx) functions on
    ///      the bus, or <see cref="DgpuPci.Find"/> finds a non-boot dGPU.
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
        if (DgpuPci.CountDisplayFunctions() >= 2)
            return true;
        if (DgpuPci.Find() != null)
            return true;
        if (GpuBootFiles.EcoBlocksPresent())
            return true;
        // nvidia/nouveau bound with no matching device = dGPU hot-removed.
        // These drivers never load on machines without NVIDIA hardware.
        if (Platform.Linux.LinuxAsusWmi.HasNvidiaModuleLoaded())
            return true;
        if (DgpuPci.ValidatedSlotCached())
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

    // Boot file paths, kept here for callers and tests.
    internal static string ModprobeBlockPath => GpuBootFiles.ModprobeBlockPath;
    internal static string UdevRemovePath => GpuBootFiles.UdevRemovePath;
    internal static string TriggerPath => GpuBootFiles.TriggerPath;
    internal static string PersistentTriggerPath => GpuBootFiles.PersistentTriggerPath;
    internal static string BackendPath => GpuBootFiles.BackendPath;
    internal static string RecoveryMarkerPath => GpuBootFiles.RecoveryMarkerPath;

    /// <summary>Backend marker content the boot service will read, or "" when missing.</summary>
    internal static string ReadBackendMarker() => GpuBootFiles.ReadBackend();

    /// <summary>Drop the boot recovery marker once shown; true when it is gone. See <see cref="GpuBootFiles.ClearRecovery"/>.</summary>
    public static bool ClearRecoveryMarker() => GpuBootFiles.ClearRecovery();

    /// <summary>
    /// Push the backend selector marker (/etc/ghelper/backend) without
    /// touching any block artifacts. Called when the user toggles the
    /// "Use PCI dGPU disable" checkbox so the boot service sees the new
    /// backend on the next boot even if the user never schedules a mode
    /// change. Idempotent - no-op if the marker already matches.
    /// </summary>
    public bool PushBackendMarker(string backend)
    {
        if (backend != "pci" && backend != "asus-wmi" && backend != "ebpf")
        {
            Logger.WriteLine($"GPUModeControl: PushBackendMarker rejected invalid backend '{backend}'");
            return false;
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

        return GpuBootFiles.SetBackend(backend);
    }

    internal void SetEcoPersistent(bool persistent) => GpuBootFiles.SetEcoPersistent(persistent);

    /// <summary>Check config flag (UI state). May be true even before the file is written.</summary>
    internal static bool IsEcoPersistentConfig()
    {
        return AppConfig.Is("gpu_eco_persistent");
    }

    // Config helpers

    internal static void SaveModeToConfig(GpuMode mode)
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

    internal static GpuMode ParseGpuMode(string mode)
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

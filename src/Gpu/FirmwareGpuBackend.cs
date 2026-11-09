using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;

namespace GHelper.Linux.Gpu;

/// <summary>The asus-wmi backend: firmware dgpu_disable for Eco and the firmware MUX for Ultimate.</summary>
internal sealed class FirmwareGpuBackend : IGpuBackend
{
    private readonly IHardwareControl _wmi;
    private readonly IPowerManager _power;
    private readonly MuxLatch _mux;
    private readonly SemaphoreSlim _switchLock;

    /// <summary>switchLock is the controller's lock; startup and auto switch take it only around their writes.</summary>
    public FirmwareGpuBackend(IHardwareControl wmi, IPowerManager power, MuxLatch mux, SemaphoreSlim switchLock)
    {
        _wmi = wmi;
        _power = power;
        _mux = mux;
        _switchLock = switchLock;
    }

    /// <summary>
    /// Read current hardware, compute delta, route to Execute* methods.
    /// </summary>
    public GpuSwitchResult Switch(GpuMode target)
    {
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
        int currentMux = _mux.Effective;       // 0=Ultimate, 1=hybrid

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
                GpuBootFiles.Clean();

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
                _mux.Write(targetMux);
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

            if (currentEco != targetEco && targetEco)
            {
                // MUX change + Eco needed - BUT validate this isn't an impossible combo.
                // If MUX is latched to 0 (Ultimate), Eco block artifacts would cause black screen.
                // GpuBootFiles.Write already refuses, but be explicit here too.
                if (_mux.WouldCreateImpossibleState(target))
                {
                    // MUX latched to 0 + Eco = impossible. This shouldn't happen from
                    // normal UI flow (Eco has targetMux=1), but defend against it.
                    Logger.WriteLine("GPUModeControl: MUX change + Eco creates impossible state - Eco blocked");
                    GpuBootFiles.Clean();
                    return GpuSwitchResult.EcoBlocked;
                }

                // Check if driver is blocking.
                // If driver is active, return DriverBlocking so the UI shows the dialog
                // on the FIRST click (not silently latching MUX and forcing a second click).
                if (DgpuDriver.IsActive())
                {
                    Logger.WriteLine("GPUModeControl: MUX change + Eco needed, driver active → DriverBlocking");
                    // MUX is already latched above. The dialog's "After Reboot" will call
                    // ScheduleModeForReboot() which writes the block artifacts.
                    // ScheduleModeForReboot also has the impossible-state guard.
                    GPUModeControl.SaveModeToConfig(target);
                    return GpuSwitchResult.DriverBlocking;
                }
                Logger.WriteLine("GPUModeControl: also need Eco - deferred to after MUX settles (next boot)");
            }
            else if (!targetEco)
            {
                // Switching to Standard/Ultimate/Optimized - clean up stale block artifacts
                GpuBootFiles.Clean();
            }

            GPUModeControl.SaveModeToConfig(target);
            return GpuSwitchResult.RebootRequired;
        }

        // dgpu change needed?
        if (currentEco == targetEco)
        {
            Logger.WriteLine($"GPUModeControl: hardware already in target state (eco={currentEco})");
            // Clean up stale block artifacts if target is not Eco
            if (!targetEco)
            {
                GpuBootFiles.Clean();

                // dgpu_disable=0 does not guarantee a working dGPU: a failed
                // Eco release can leave the device unbound (late unbind after
                // our timeout) with the module loaded but no driver attached.
                // Run the full enable path to rebind/reload instead of no-op.
                if (DgpuPci.PresentButDriverless())
                {
                    Logger.WriteLine("GPUModeControl: eco=0 but dGPU is driverless - running recovery enable");
                    var recovery = ExecuteEnableDgpu();
                    if (recovery == GpuSwitchResult.Applied)
                        GPUModeControl.SaveModeToConfig(target);
                    return recovery;
                }
            }
            GPUModeControl.SaveModeToConfig(target);
            return GpuSwitchResult.AlreadySet;
        }

        if (!targetEco)
        {
            // Enabling dGPU - always safe, always fast
            var result = ExecuteEnableDgpu();
            if (result == GpuSwitchResult.Applied)
                GPUModeControl.SaveModeToConfig(target);
            return result;
        }
        else
        {
            // Disabling dGPU - THE dangerous path
            var result = ExecuteDisableDgpu();
            if (result == GpuSwitchResult.Applied)
                GPUModeControl.SaveModeToConfig(target);
            else if (result == GpuSwitchResult.DriverBlocking || result == GpuSwitchResult.RebootRequired)
                GPUModeControl.SaveModeToConfig(target);
            // On Failed: do NOT save config
            return result;
        }
    }

    public GpuSwitchResult ReleaseAndSwitch(GpuMode target)
    {
        bool physicallyUltimate = _wmi.GetGpuMuxMode() == 0;
        if (physicallyUltimate)
            Logger.WriteLine("GPUModeControl: MUX=0 (Ultimate) - skipping live release, deferring Eco to reboot");
        bool released = !physicallyUltimate && DgpuDriver.TryRelease();
        if (!released)
        {
            Logger.WriteLine("GPUModeControl: driver release failed - deferring to reboot");
            GPUModeControl.SaveModeToConfig(GpuMode.Eco);
            // If we're in Ultimate (MUX=0), latch MUX→1 first so next boot can apply Eco
            int effectiveMux = _mux.Effective;
            if (effectiveMux == 0)
            {
                // gpu_mux_mode write fails when dgpu_disable=1 - enable dGPU first if needed
                bool ecoActive = _wmi.GetGpuEco();
                if (ecoActive)
                {
                    Logger.WriteLine("GPUModeControl: TryRelease - enabling dGPU before MUX latch");
                    try
                    { _wmi.SetGpuEco(false); GpuBootFiles.Clean(); }
                    catch (Exception muxEx)
                    {
                        Logger.WriteLine($"GPUModeControl: TryRelease - exit Eco failed: {muxEx.Message}");
                    }
                }
                Logger.WriteLine("GPUModeControl: MUX=0, latching MUX→1 for Eco boot");
                try
                {
                    _mux.Write(1);
                }
                catch (Exception muxEx)
                {
                    Logger.WriteLine($"GPUModeControl: TryRelease - MUX latch failed: {muxEx.Message}");
                }
            }
            // pkexec auth is cached from rmmod attempt - write block without re-prompting
            GpuBootFiles.Write(GpuMode.Eco, _mux);
            return GpuSwitchResult.Deferred;
        }

        // Driver released - now write dgpu_disable=1 (should be fast)
        Logger.WriteLine("GPUModeControl: driver released, writing dgpu_disable=1");
        DgpuPci.DropNodes();
        _wmi.SetGpuEco(true);

        // Verify
        if (_wmi.GetGpuEco())
        {
            GPUModeControl.SaveModeToConfig(GpuMode.Eco);
            // Eco applied live - remove block artifacts (dgpu_disable=1 is persistent)
            GpuBootFiles.Clean();
            // Hide the NVIDIA Vulkan ICD while the dGPU is disabled.
            DgpuDriver.ApplyVulkanIcd(dgpuAvailable: false);
            Logger.WriteLine("GPUModeControl: Eco mode applied after driver release");
            return GpuSwitchResult.Applied;
        }
        else
        {
            Logger.WriteLine("GPUModeControl: dgpu_disable write succeeded but readback != 1");
            GPUModeControl.SaveModeToConfig(GpuMode.Eco);
            return GpuSwitchResult.Deferred;
        }
    }

    public GpuSwitchResult? ScheduleForReboot(GpuMode target) => null;

    public bool OwnsPersistentMarker => false;

    public GpuSwitchResult ApplyPendingOnStartup()
    {
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
            GpuBootFiles.Clean();
            return GpuSwitchResult.AlreadySet;
        }

        GpuMode target = GPUModeControl.ParseGpuMode(savedMode);
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
        if (ecoEnabled && DgpuDriver.IsActive())
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
            if (!GpuBootFiles.PersistentPresent())
                GpuBootFiles.Clean();

            // Model-based persistent Eco: if firmware is known to forget dgpu_disable
            // across reboots, auto-enable the persistent marker so the boot service
            // re-applies Eco on every startup.
            if (target == GpuMode.Eco && AppConfig.IsEcoBootFixModel() && !GPUModeControl.IsEcoPersistentConfig())
            {
                Logger.WriteLine("GPUModeControl: startup - model requires persistent Eco, auto-enabling");
                GpuBootFiles.SetEcoPersistent(true);
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
                    GpuBootFiles.Clean();
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

            if (DgpuDriver.IsActive())
            {
                Logger.WriteLine("GPUModeControl: startup - dGPU driver active, cannot apply Eco");
                // MUX is correct (1) but dGPU driver is loaded - write block so NEXT boot
                // driver won't load, then ghelper can write dgpu_disable=1 safely.
                // This breaks the infinite loop: startup → driver active → can't apply → repeat.
                GpuBootFiles.Write(GpuMode.Eco, _mux);
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
                    if (!GpuBootFiles.PersistentPresent())
                        GpuBootFiles.Clean();
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
                GpuBootFiles.Clean();    // Clean up stale block artifacts
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

    public void ApplyPendingOnShutdown()
    {
        try
        {
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
            if (DgpuDriver.IsActive())
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

    public GpuSwitchResult AutoSwitch()
    {
        // Don't auto-switch if in Ultimate (MUX=0)
        int mux = _wmi.GetGpuMuxMode();
        if (mux == 0)
        {
            Logger.WriteLine("GPUModeControl: AutoGpuSwitch - MUX=0 (Ultimate), skipping");
            return GpuSwitchResult.AlreadySet;
        }

        // Don't auto-switch to Eco if MUX=0 was latched this boot (persistent flag)
        // Hardware may still read MUX=1, but firmware has MUX=0 pending - Eco would be impossible
        if (MuxLatch.ZeroLatchedThisBoot())
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
                GpuBootFiles.Clean();
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
        bool blocksPresent = GpuBootFiles.EcoBlocksPresent();

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
        // to Standard (which removes the blocks via GpuBootFiles.Clean).
        if (ecoEnabled || blocksPresent)
            return GpuMode.Eco;
        return GpuMode.Standard;
    }

    public bool IsPendingReboot()
    {
        string? saved = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(saved))
            return false;

        GpuMode target = GPUModeControl.ParseGpuMode(saved);
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

    /// <summary>Always safe. Write dgpu_disable=0, wait for the dGPU on the bus and load its driver.</summary>
    internal GpuSwitchResult ExecuteEnableDgpu()
    {
        Logger.WriteLine("GPUModeControl: enabling dGPU (dgpu_disable=0) - always safe");
        // SetGpuEco(false) can take 20s+ on asus-armoury and DgpuPci.WaitForDevice
        // another 22s, so a shorter budget can expire before the driver load
        // even starts. DgpuDriver.Load extends it per attempt; Resume() ends it on success.
        NVidia.GpuQueryGate.Pause(TimeSpan.FromSeconds(60), "dGPU enable");
        try
        {
            _wmi.SetGpuEco(false);
            // Switching away from Eco - remove block artifacts (dGPU driver should be loadable)
            GpuBootFiles.Clean();

            if (GPUModeControl.IsTestMode)
            {
                Logger.WriteLine("GPUModeControl: test mode - skipping live dGPU hardware re-enable");
                return GpuSwitchResult.Applied;
            }

            DgpuPci.PowerOnSlot();

            // Wait for the dGPU to actually re-appear on the PCI bus. The
            // dgpu_disable=0 write can be very slow on asus-armoury firmware
            // (20s+), and the single rescan in SetGpuEco often fires before the
            // device is electrically back, so it never re-enumerates. Poll for
            // the device, re-asserting slot power + rescan until it shows up. Gate
            // the nvidia daemon restart on the *device* (not the module - the
            // module can be present from a powerd respawn loop even with no GPU).
            bool present = DgpuPci.WaitForDevice(22000, BounceDgpuDisable);
            if (!present)
            {
                Logger.WriteLine("GPUModeControl: dGPU did not re-appear after rescan - reboot likely required; skipping daemon restart");
                NVidia.GpuQueryGate.Hold("dGPU did not re-appear");
                return GpuSwitchResult.DgpuReenableFailed;
            }

            if (!DgpuDriver.Load())
            {
                NVidia.GpuQueryGate.Hold("dGPU re-enable failed");
                return GpuSwitchResult.DgpuReenableFailed;
            }

            // Allow the dGPU to autosuspend (supergfxctl set_runtime_pm Auto).
            DgpuPci.SetRuntimePmAuto();
            // Restore the NVIDIA Vulkan ICD now the dGPU is back.
            DgpuDriver.ApplyVulkanIcd(dgpuAvailable: true);
            // Bring back whitelisted user services stopped by an earlier
            // holder kill (e.g. powerdevil pinned the nvidia I2C bus).
            DgpuDriver.RestartStoppedHolderServices();
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
    /// After the dGPU is re-enabled, give the driver a moment to settle then ask
    /// ModeControl to re-apply (or reset) the current mode's GPU tuning. Runs on a
    /// background task so it never blocks the switch.
    /// </summary>
    internal static void ScheduleGpuTuningReapply()
    {
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            try
            {
                GPUModeControl.OnReapplyGpuTuning?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: GPU tuning reapply failed: {ex.Message}");
            }
        });
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

    /// <summary>
    /// THE one dangerous operation. Checks driver safety first.
    /// If safe → writes dgpu_disable=1 (may block 30-60s).
    /// If unsafe → returns DriverBlocking.
    /// </summary>
    private GpuSwitchResult ExecuteDisableDgpu()
    {
        // Check if in Ultimate mode (MUX=0) - kernel refuses dgpu_disable=1
        int mux = _mux.Effective;
        if (mux == 0)
        {
            Logger.WriteLine("GPUModeControl: MUX=0 (Ultimate) - cannot disable dGPU directly");
            // Latch MUX change - dgpu_disable must wait until MUX settles on next boot.
            // Do NOT write block here - MUX needs to settle first. ApplyPendingOnStartup()
            // will write the block after confirming MUX is correct.
            try
            {
                _mux.Write(1);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: ExecuteDisableDgpu - MUX latch failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }

        if (DgpuDriver.IsActive())
        {
            Logger.WriteLine("GPUModeControl: dGPU driver is ACTIVE - returning DriverBlocking (user confirmation required)");
            DgpuDriver.LogHoldersSnapshot("DriverBlocking");
            return GpuSwitchResult.DriverBlocking;
        }

        // Safe to write
        Logger.WriteLine("GPUModeControl: dGPU driver idle/absent - writing dgpu_disable=1");
        try
        {
            DgpuPci.DropNodes();
            _wmi.SetGpuEco(true);

            // Verify the write took effect
            if (_wmi.GetGpuEco())
            {
                Logger.WriteLine("GPUModeControl: dgpu_disable=1 confirmed");
                DgpuPci.PowerOffSlot();
                GpuBootFiles.Clean();
                DgpuDriver.ApplyVulkanIcd(dgpuAvailable: false);
                DgpuDriver.RestartStoppedHolderServices();
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

            int mux = _wmi.GetGpuMuxMode();
            bool ecoEnabled = _wmi.GetGpuEco();

            if (mux == 0 && ecoEnabled)
            {
                Logger.WriteLine("GPUModeControl: BOOT SAFETY - MUX=0 + dgpu_disable=1 is impossible!");
                Logger.WriteLine("GPUModeControl: BOOT SAFETY - forcing dgpu_disable=0 to recover");
                _wmi.SetGpuEco(false);
                // Remove block artifacts to allow dGPU driver to load after recovery
                GpuBootFiles.Clean();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: BootSafetyCheck failed: {ex.Message}");
        }
    }
}

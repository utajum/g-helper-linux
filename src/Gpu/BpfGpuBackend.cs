using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;

namespace GHelper.Linux.Gpu;

/// <summary>The ebpf backend: an LSM filter hides the dGPU's nodes for Eco; the driver stays loaded and the MUX is never touched.</summary>
internal sealed class BpfGpuBackend : IGpuBackend
{
    private readonly IHardwareControl _wmi;
    private readonly IPowerManager _power;
    private readonly MuxLatch _mux;
    private readonly SemaphoreSlim _switchLock;
    private readonly FirmwareGpuBackend _firmware;

    /// <summary>switchLock is the controller's lock; the reconcile, auto switch and backend change take it themselves.</summary>
    public BpfGpuBackend(IHardwareControl wmi, IPowerManager power, MuxLatch mux, SemaphoreSlim switchLock, FirmwareGpuBackend firmware)
    {
        _wmi = wmi;
        _power = power;
        _mux = mux;
        _switchLock = switchLock;
        _firmware = firmware;
    }

    public GpuSwitchResult Switch(GpuMode target) => Apply(target);

    /// <summary>Switch Now: nothing to release, the driver stays loaded.</summary>
    public GpuSwitchResult ReleaseAndSwitch(GpuMode target) => Apply(target, askAboutHolders: false);

    /// <summary>Eco attaches the filter, Standard detaches it, both live. Apps that hold the dGPU return DriverBlocking.</summary>
    private GpuSwitchResult Apply(GpuMode target, bool askAboutHolders = true)
    {
        _ecoForNextBoot = false;
        // Ultimate is a MUX concept and the filter never touches the MUX.
        if (target == GpuMode.Ultimate)
        {
            Logger.WriteLine("GPUModeControl: eBPF backend - Ultimate not applicable, treating as Standard");
            target = GpuMode.Standard;
        }

        // Optimized picks the leg by power source; gpu_auto re-picks on AC change.
        bool wantEco = target == GpuMode.Eco
            || (target == GpuMode.Optimized && !_power.IsOnAcPower());

        // Eco asks before anything is saved, so Cancel leaves the mode as it was.
        // Optimized is saved first, so Cancel keeps it selected with the dGPU visible.
        if (askAboutHolders && wantEco && !Block.GpuBlockControl.IsActive()
            && !Block.DgpuDisplay.HasExternalDisplay() && !_mux.WouldCreateImpossibleState(GpuMode.Eco)
            && Block.GpuBlockControl.HoldersToClose().Count > 0)
        {
            DgpuDriver.LogHoldersSnapshot("ebpf: apps hold the dGPU");
            if (target == GpuMode.Optimized)
            {
                AppConfig.Set("gpu_auto", 1);
                GPUModeControl.SaveModeToConfig(target);
                GpuBootFiles.SyncPersistent(target);
            }
            return GpuSwitchResult.DriverBlocking;
        }

        AppConfig.Set("gpu_auto", target == GpuMode.Optimized ? 1 : 0);

        // A monitor on a dGPU port keeps the card visible until the reconcile.
        if (wantEco && Block.DgpuDisplay.HasExternalDisplay())
        {
            Logger.WriteLine($"GPUModeControl: eBPF {target} - external display on the dGPU, staying visible");
            wantEco = false;
        }

        bool blocked = Block.GpuBlockControl.IsActive();
        GpuBootFiles.SetBackend("ebpf");

        if (wantEco == blocked)
        {
            Logger.WriteLine($"GPUModeControl: eBPF backend - already in {target}, no-op");
            GPUModeControl.SaveModeToConfig(target);
            // The marker still has to follow the mode name (eco vs optimized).
            GpuBootFiles.SyncPersistent(target);
            return GpuSwitchResult.AlreadySet;
        }

        if (wantEco)
        {
            // MUX=0: the dGPU drives the panel.
            if (_mux.WouldCreateImpossibleState(GpuMode.Eco))
                return GpuSwitchResult.EcoBlocked;

            // Log allowed holders: they explain a card stuck in D0.
            if (DgpuDriver.IsActive())
                DgpuDriver.LogHoldersSnapshot("ebpf block engaging with active holders");

            if (!Block.GpuBlockControl.Enable())
            {
                Logger.WriteLine("GPUModeControl: eBPF backend - could not enable the filter");
                return GpuSwitchResult.Failed;
            }
            GPUModeControl.SaveModeToConfig(target);
            // Always mark: bpffs is wiped on reboot, so the marker re-attaches the filter.
            GpuBootFiles.SyncPersistent(target);
            Logger.WriteLine($"GPUModeControl: eBPF backend - {target} applied live (filter attached)");
            return GpuSwitchResult.Applied;
        }

        if (!Block.GpuBlockControl.Disable())
        {
            Logger.WriteLine("GPUModeControl: eBPF backend - could not disable the filter");
            return GpuSwitchResult.Failed;
        }
        GPUModeControl.SaveModeToConfig(target);
        GpuBootFiles.SyncPersistent(target);
        FirmwareGpuBackend.ScheduleGpuTuningReapply();
        Logger.WriteLine($"GPUModeControl: eBPF backend - {target} applied live (filter detached)");
        return GpuSwitchResult.Applied;
    }

    /// <summary>Only the trigger: the boot service attaches the filter. No MUX latch, no block files.</summary>
    public GpuSwitchResult? ScheduleForReboot(GpuMode target)
    {
        _ecoForNextBoot = target == GpuMode.Eco;
        GPUModeControl.SaveModeToConfig(target);
        AppConfig.Set("gpu_auto", target == GpuMode.Optimized ? 1 : 0);
        if (target == GpuMode.Eco || target == GpuMode.Optimized)
        {
            // The boot script picks the Optimized leg by power source.
            GpuBootFiles.Write(target, _mux);
            GpuBootFiles.SyncPersistent(target);
        }
        else
        {
            GpuBootFiles.Clean();
            GpuBootFiles.SyncPersistent(GpuMode.Standard);
        }
        Logger.WriteLine($"GPUModeControl: eBPF backend - {target} scheduled for next boot (trigger only)");
        return GpuSwitchResult.RebootRequired;
    }

    /// <summary>The marker is this backend's only persistent state, so <see cref="Apply"/> owns it.</summary>
    public bool OwnsPersistentMarker => true;

    /// <summary>Re-attach a saved Eco when the boot service did not.</summary>
    public GpuSwitchResult ApplyPendingOnStartup()
    {
        // The boot service follows the marker, so an ebpf config it never got falls back to it.
        string marker = GpuBootFiles.ReadBackend();
        if (marker != "ebpf")
        {
            string fallback = marker is "pci" or "asus-wmi" ? marker : AppConfig.DefaultGpuBackend();
            Logger.WriteLine($"GPUModeControl: backend marker '{marker}' is not ebpf - using {fallback}");
            AppConfig.Set("gpu_backend", fallback);
            OnBackendChanged("ebpf", fallback);
            return GpuSwitchResult.AlreadySet;
        }

        string? want = AppConfig.GetString("gpu_mode");
        bool active = Block.GpuBlockControl.IsActive();

        // Optimized: AutoGpuSwitch re-picks the leg; keep the saved mode.
        if (want == "optimized" && AppConfig.Is("gpu_auto"))
        {
            if (active)
                Block.DgpuPowerMonitor.NoteBlockEnabled();
            return GpuSwitchResult.AlreadySet;
        }

        if (want == "eco" && !active)
        {
            if (Block.DgpuDisplay.HasExternalDisplay())
            {
                Logger.WriteLine("GPUModeControl: eBPF backend startup - Eco kept visible for an external display");
                return GpuSwitchResult.AlreadySet;
            }
            // No auth prompt at startup.
            if (Block.GpuBlockControl.Enable(interactive: false))
            {
                Logger.WriteLine("GPUModeControl: eBPF backend startup - re-applied Eco filter");
                return GpuSwitchResult.Applied;
            }
            Logger.WriteLine("GPUModeControl: eBPF backend startup - could not re-apply Eco, reporting Standard");
            GPUModeControl.SaveModeToConfig(GpuMode.Standard);
            return GpuSwitchResult.AlreadySet;
        }

        GpuMode actualBpf = active ? GpuMode.Eco : GpuMode.Standard;
        if (want != actualBpf.ToString().ToLowerInvariant())
        {
            GPUModeControl.SaveModeToConfig(actualBpf);
            Logger.WriteLine($"GPUModeControl: eBPF backend startup - synced config gpu_mode='{actualBpf}'");
        }
        if (active)
        {
            // The boot service may have attached before the driver made its nodes.
            Block.GpuBlockControl.Refresh();
            Block.DgpuPowerMonitor.NoteBlockEnabled();
        }
        return GpuSwitchResult.AlreadySet;
    }

    /// <summary>Nothing to do: bpffs is cleared on reboot and boot re-applies the filter.</summary>
    public void ApplyPendingOnShutdown()
    {
    }

    public GpuSwitchResult AutoSwitch() => ApplyLeg("AutoGpuSwitch");

    /// <summary>Only the filter state counts, not dgpu_disable.</summary>
    public GpuMode GetCurrentMode()
    {
        if (AppConfig.Is("gpu_auto"))
            return GpuMode.Optimized;
        return Block.GpuBlockControl.IsActive() ? GpuMode.Eco : GpuMode.Standard;
    }

    /// <summary>Both directions apply live.</summary>
    public bool IsPendingReboot() => false;

    /// <summary>Eco "After Reboot" this session: the reconcile must not apply it live.</summary>
    private bool _ecoForNextBoot;

    /// <summary>Optimized or Eco leg: blocked (Optimized: on battery, no apps to close) without external display; no prompt, never on MUX=0.</summary>
    private GpuSwitchResult ApplyLeg(string source)
    {
        if (_switchLock.CurrentCount == 0)
            return GpuSwitchResult.AlreadySet; // a switch is running, it owns the state

        bool display = Block.DgpuDisplay.HasExternalDisplay();
        bool eco = !AppConfig.Is("gpu_auto");
        bool wantBlocked = (eco || !_power.IsOnAcPower()) && !display;
        if (wantBlocked == Block.GpuBlockControl.IsActive())
            return GpuSwitchResult.AlreadySet;
        if (wantBlocked && _mux.WouldCreateImpossibleState(GpuMode.Eco))
        {
            Logger.WriteLine($"GPUModeControl: {source} (ebpf) - MUX=0, not blocking");
            return GpuSwitchResult.AlreadySet;
        }
        // Optimized waits for apps on the dGPU; the power event asks to close them.
        if (wantBlocked && !eco && Block.GpuBlockControl.HoldersToClose().Count > 0)
            return GpuSwitchResult.DriverBlocking;
        if (!_switchLock.Wait(0))
            return GpuSwitchResult.AlreadySet;
        try
        {
            bool ok = wantBlocked
                ? Block.GpuBlockControl.Enable(interactive: false)
                : Block.GpuBlockControl.Disable(interactive: false);
            if (!ok)
                return GpuSwitchResult.Failed;
            // The helper sends a drm uevent, so an external output wakes without a replug.
            if (!wantBlocked)
                FirmwareGpuBackend.ScheduleGpuTuningReapply();
            Logger.WriteLine($"GPUModeControl: {source} (ebpf) - " + (wantBlocked
                ? $"blocked ({(eco ? "Eco" : "battery")}, no external display)"
                : $"released ({(display ? "external display" : "AC")})"));
            return GpuSwitchResult.Applied;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>ebpf Optimized or Eco: release for a monitor on a dGPU port, block again when it goes.</summary>
    public void Reconcile()
    {
        if (!AppConfig.IsBpfGpuBackend()
            || (!AppConfig.Is("gpu_auto") && (AppConfig.GetString("gpu_mode") != "eco" || _ecoForNextBoot)))
            return;
        try
        {
            ApplyLeg("reconcile");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: ReconcileBpfAuto failed: {ex.Message}");
        }
    }

    /// <summary>Leaving ebpf detaches the filter; entering it undoes firmware or PCI Eco. Background thread.</summary>
    public void OnBackendChanged(string oldBackend, string newBackend)
    {
        if (oldBackend == newBackend)
            return;

        if (!_switchLock.Wait(0))
        {
            Logger.WriteLine("GPUModeControl: OnBackendChanged - switch in progress, skipping cleanup");
            return;
        }

        try
        {
            if (oldBackend == "ebpf")
            {
                if (Block.GpuBlockControl.IsActive())
                {
                    Logger.WriteLine("GPUModeControl: OnBackendChanged - detaching leftover ebpf filter");
                    Block.GpuBlockControl.Disable();
                }
                GpuBootFiles.SyncPersistent(GpuMode.Standard);
                // Auto switching is ebpf-only unless the user opted in.
                if (AppConfig.Is("gpu_auto") && !AppConfig.Is("gpu_optimized_enabled"))
                    AppConfig.Set("gpu_auto", 0);
                string? saved = AppConfig.GetString("gpu_mode");
                if (saved == "eco" || saved == "optimized")
                    GPUModeControl.SaveModeToConfig(GpuMode.Standard);
            }

            if (newBackend == "ebpf")
            {
                bool leftEco = false;

                // Firmware Eco hides the card from the bus; the filter needs it back.
                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GPUModeControl: OnBackendChanged - leaving firmware Eco for ebpf");
                    _firmware.ExecuteEnableDgpu();
                    leftEco = true;
                }

                // PCI Eco too: the live path drops the block files, rescans and rebinds.
                if (GpuBootFiles.EcoBlocksPresent())
                {
                    Logger.WriteLine("GPUModeControl: OnBackendChanged - leaving PCI Eco for ebpf");
                    if (GpuBootFiles.LiveStandard())
                        GPUModeControl.OnLivePciTransition?.Invoke();
                    else
                        Logger.WriteLine("GPUModeControl: OnBackendChanged - live PCI release failed, card returns after reboot");
                    leftEco = true;
                }
                // Any leftover trigger from the old backend.
                GpuBootFiles.Clean();

                if (leftEco)
                {
                    // The card is visible now: save Standard so the next boot does not block it.
                    GPUModeControl.SaveModeToConfig(GpuMode.Standard);
                    GpuBootFiles.SyncPersistent(GpuMode.Standard);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: OnBackendChanged failed: {ex.Message}");
        }
        finally
        {
            _switchLock.Release();
        }
    }
}

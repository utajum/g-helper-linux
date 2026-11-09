using GHelper.Linux.Helpers;

namespace GHelper.Linux.Gpu;

/// <summary>The pci backend: Eco is the boot files plus PCI remove; no MUX, so Optimized and Ultimate act as Standard.</summary>
internal sealed class PciGpuBackend : IGpuBackend
{
    private readonly MuxLatch _mux;

    public PciGpuBackend(MuxLatch mux)
    {
        _mux = mux;
    }

    /// <summary>
    /// PCI backend switching. There are only two effective modes - Eco
    /// (block artifacts present) and Standard (no block artifacts). Both
    /// apply live when possible; otherwise the boot script does the rmmod /
    /// PCI rescan work at the next boot. Optimized and Ultimate fall through
    /// to Standard since they have no meaning without ASUS firmware.
    /// </summary>
    public GpuSwitchResult Switch(GpuMode target)
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

        bool ecoBlocksPresent = GpuBootFiles.EcoBlocksPresent();
        bool wantEco = (target == GpuMode.Eco);
        bool wantStandard = (target == GpuMode.Standard);

        // Already in the desired persistent state and no pending switch?
        // Mirror the WMI flow and report AlreadySet so the UI clears its
        // "reboot pending" tip.
        if (!GpuBootFiles.TriggerPresent())
        {
            if (wantEco && ecoBlocksPresent)
            {
                Logger.WriteLine("GPUModeControl: PCI backend - already in Eco (blocks present), no-op");
                GPUModeControl.SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.AlreadySet;
            }
            if (wantStandard && !ecoBlocksPresent)
            {
                Logger.WriteLine("GPUModeControl: PCI backend - already in Standard (no blocks), no-op");
                GPUModeControl.SaveModeToConfig(GpuMode.Standard);
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
            if (_mux.WouldCreateImpossibleState(GpuMode.Eco))
                return GpuSwitchResult.EcoBlocked;

            if (DgpuDriver.IsActive())
            {
                Logger.WriteLine("GPUModeControl: PCI backend - dGPU driver active, showing dialog");
                GPUModeControl.SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.DriverBlocking;
            }

            GPUModeControl.SaveModeToConfig(GpuMode.Eco);
            GpuBootFiles.Write(GpuMode.Eco, _mux);
            DgpuPci.RemoveFunctions();
            DgpuDriver.ApplyVulkanIcd(dgpuAvailable: false);
            Logger.WriteLine("GPUModeControl: PCI backend - live Eco applied (driver was not loaded)");
            return GpuSwitchResult.Applied;
        }

        // Eco → Standard: remove blocks, reload udev, rescan PCI bus,
        // modprobe the driver. On failure, fall through to deferred reboot.
        if (wantStandard && ecoBlocksPresent)
        {
            if (GpuBootFiles.LiveStandard())
            {
                GPUModeControl.OnLivePciTransition?.Invoke();
                GPUModeControl.SaveModeToConfig(GpuMode.Standard);
                Logger.WriteLine("GPUModeControl: PCI backend - live Eco→Standard applied (no reboot)");
                return GpuSwitchResult.Applied;
            }
            Logger.WriteLine("GPUModeControl: PCI backend - live transition failed, falling back to deferred reboot");
        }

        // Schedule the change for the next reboot. GpuBootFiles.Write knows
        // how to handle both Eco (writes blocks) and non-Eco (clears blocks
        // and writes only the trigger) in PCI mode.
        GPUModeControl.SaveModeToConfig(target);
        GpuBootFiles.Write(target, _mux);

        if (GpuBootFiles.TriggerPresent())
        {
            Logger.WriteLine($"GPUModeControl: PCI backend - scheduled {target} for next reboot");
            return GpuSwitchResult.RebootRequired;
        }

        // GpuBootFiles.Write did not produce a trigger file. Two reasons:
        //   1. Authentication was cancelled (pkexec dialog dismissed).
        //   2. The internal safety guard refused (Eco + MUX=0 latched).
        // In case (2) the earlier "WriteDriverBlock REFUSED" log line
        // explains why; the EcoBlocked result lets the UI surface a
        // friendlier reason than a generic failure toast.
        if (target == GpuMode.Eco && _mux.WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GPUModeControl: PCI backend - Eco refused (MUX=0 latched, would cause black screen)");
            return GpuSwitchResult.EcoBlocked;
        }
        Logger.WriteLine("GPUModeControl: PCI backend - trigger write failed (pkexec cancelled or write error)");
        return GpuSwitchResult.Failed;
    }

    public GpuSwitchResult ReleaseAndSwitch(GpuMode target)
    {
        // PCI backend: no firmware dgpu_disable. After driver release,
        // write block files + PCI-remove dGPU functions from the bus.
        //
        // MUX=0 (live/latched) → dGPU drives the panel; removing it blanks it.
        if (_mux.WouldCreateImpossibleState(GpuMode.Eco))
            return GpuSwitchResult.EcoBlocked;

        bool pciReleased = DgpuDriver.TryRelease();
        if (!pciReleased)
        {
            Logger.WriteLine("GPUModeControl: PCI TryRelease - driver release failed, deferring to reboot");
            GPUModeControl.SaveModeToConfig(GpuMode.Eco);
            GpuBootFiles.Write(GpuMode.Eco, _mux);
            return GpuSwitchResult.Deferred;
        }

        GpuBootFiles.Write(GpuMode.Eco, _mux);
        DgpuPci.RemoveFunctions();
        GPUModeControl.OnLivePciTransition?.Invoke();
        DgpuDriver.ApplyVulkanIcd(dgpuAvailable: false);
        GPUModeControl.SaveModeToConfig(GpuMode.Eco);
        Logger.WriteLine("GPUModeControl: PCI Eco applied live (rmmod + PCI remove)");
        return GpuSwitchResult.Applied;
    }

    public GpuSwitchResult? ScheduleForReboot(GpuMode target) => null;

    public bool OwnsPersistentMarker => false;

    public GpuSwitchResult ApplyPendingOnStartup()
    {
        // PCI backend: the boot service is solely responsible for applying
        // any pending mode at boot. By the time ghelper starts up, the
        // transition has already happened (or failed and been recorded in
        // /etc/ghelper/last-eco-failed). Just sync config with the actual
        // file state so the UI shows the right active mode, no firmware
        // pokes needed.
        //
        // Do NOT touch mux_zero_latched_boot_id here. The controller's
        // MuxLatch.ClearStaleFlag() call already cleared it on a
        // cross-boot stale match; anything still set is from THIS
        // boot session and represents a genuine pending firmware
        // latch that must keep blocking PCI Eco until the user
        // reboots (else the next boot lands in MUX=0 + udev-removed
        // dGPU = black screen). Same logic for the in-memory latch.

        GpuMode actual = GetCurrentMode();
        string? saved = AppConfig.GetString("gpu_mode");
        if (saved != actual.ToString().ToLowerInvariant())
        {
            GPUModeControl.SaveModeToConfig(actual);
            Logger.WriteLine($"GPUModeControl: PCI backend startup - synced config gpu_mode='{actual}' to match block-file state");
        }
        return GpuSwitchResult.AlreadySet;
    }

    public void ApplyPendingOnShutdown()
    {
        // PCI backend: the boot service applies pending modes on the
        // NEXT startup, not on shutdown. There is no live dgpu_disable
        // path to take here. Skip silently so we don't accidentally
        // call into the WMI sysfs layer on non-ASUS systems where it
        // does not exist.
    }

    public GpuSwitchResult AutoSwitch()
    {
        // PCI backend has no live switching path - Optimized auto-toggle is
        // meaningless when every transition requires a reboot. The UI hides
        // the Optimized button in PCI mode, so this is a defensive guard.
        Logger.WriteLine("GPUModeControl: AutoGpuSwitch - PCI backend, no live switching available");
        return GpuSwitchResult.AlreadySet;
    }

    public GpuMode GetCurrentMode()
    {
        // PCI backend: blocks are the only signal. No MUX, no auto, no
        // firmware sysfs to query.
        return GpuBootFiles.EcoBlocksPresent() ? GpuMode.Eco : GpuMode.Standard;
    }

    public bool IsPendingReboot()
    {
        // PCI backend: the trigger file is the single source of truth.
        // Boot script applies and removes it; while present, a reboot is pending.
        return GpuBootFiles.TriggerPresent();
    }
}

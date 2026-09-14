// Comprehensive scenario coverage for GPUModeControl.
//
// Matrix:
//   PCI backend × raw_wmi flag × initial hardware state × button pressed
//
// Plus targeted edge cases (MUX=0 latched, blocks-present detection,
// ApplyPendingOnStartup behaviour, PushBackendMarker side effects).
//
// "Ultimate is the happy path" per the project owner - Ultimate is NOT
// supposed to require any change beyond the existing dGPU-enable + MUX
// latch + reboot prompt path. Tests assert that behaviour is preserved.

using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Mode;
using GHelper.Linux.Platform.Linux;
using static GHelper.Linux.Tests.Harness;
using Gate = GHelper.Linux.Gpu.NVidia.GpuQueryGate;

namespace GHelper.Linux.Tests;

public static class Scenarios
{
    public static void RunAll()
    {
        Console.WriteLine("\n PCI backend ");
        Pci_FromStandard_ClickEco_WritesBlocks_AndAppliesLive();
        Pci_FromEco_ClickEco_AlreadySet_NoOp();
        Pci_FromEco_ClickStandard_AppliesLive();
        Pci_FromStandard_ClickStandard_AlreadySet();
        Pci_ClickUltimate_TreatedAsStandard();
        Pci_ClickOptimized_TreatedAsStandard();
        Pci_GetCurrentMode_NoBlocks_ReturnsStandard();
        Pci_GetCurrentMode_BlocksPresent_ReturnsEco();
        Pci_IsPendingReboot_TriggerFilePresent_ReturnsTrue();
        Pci_IsPendingReboot_NoTriggerFile_ReturnsFalse();
        Pci_ApplyPendingOnStartup_SyncsConfigFromBlocks();
        Pci_ApplyPendingOnStartup_ClearsStaleLatchFlag();
        Pci_ApplyPendingOnStartup_KeepsCurrentBootLatchFlag();

        Console.WriteLine("\n ASUS-WMI backend ");
        Wmi_FromStandard_ClickEco_DriverIdle_AppliesLive();
        Wmi_FromEco_ClickStandard_EnablesDgpu();
        Wmi_FromEco_ClickEco_AlreadySet();
        Wmi_FromStandard_ClickStandard_AlreadySet();
        Wmi_FromStandard_ClickUltimate_LatchesMuxAndRequiresReboot();
        Wmi_FromUltimate_ClickStandard_LatchesMuxAndRequiresReboot();
        Wmi_FromUltimate_ClickEco_LatchesMuxFirst_RebootRequired();
        Wmi_MuxZeroLatched_ClickEco_Blocked();
        Wmi_SetGpuEcoThrows_ReturnsFailed_ConfigUnchanged();
        Wmi_BlocksPresent_GetCurrentMode_ReturnsEco();
        Wmi_FromStandard_ClickEco_ScheduledTriggerWrittenForBootScript();

        Console.WriteLine("\n Backend toggle plumbing ");
        PushBackendMarker_Pci_WritesMarkerFile();
        PushBackendMarker_AsusWmi_WritesMarkerFile();
        PushBackendMarker_Pci_PreservesMuxLatchFlag();
        PushBackendMarker_AsusWmi_DoesNotTouchLatchFlag();
        PushBackendMarker_Invalid_NoOp();
        PushBackendMarker_Idempotent_NoRewrite();

        Console.WriteLine("\n raw_wmi opt-in (ASUS-WMI backend only) ");
        RawWmi_DoesNotChangeButtonBehaviour_StandardClick();
        RawWmi_DoesNotChangeButtonBehaviour_EcoClick();

        Console.WriteLine("\n Cross-backend reality check ");
        ToggleBackendPciToWmi_LeavingBlocks_UiStillReportsEco();
        ToggleBackendWmiToPci_LeavingDgpuDisabled_UiReportsEco();

        Console.WriteLine("\n Ultimate is the happy path (must not regress) ");
        Wmi_Ultimate_FromEco_EnablesDgpuFirst();
        Wmi_Ultimate_Twice_AlreadySet();

        Console.WriteLine("\n Full button × backend × hw-state matrix ");
        RunFullMatrix();

        Console.WriteLine("\n Bug-regression tests (specific bugs we fixed) ");
        Regression_PciToggleAfterUltimate_PreservesLatch_EcoBlocked();
        Regression_PciBlocksRemainAfterToggle_GetCurrentModeIsEco();
        Regression_PciEcoAlreadyApplied_NoSpuriousReboot();
        Regression_BackendMarkerMissing_DefaultsToAsusWmi();
        Regression_StaleMuxLatchFlag_ClearedOnStartup();
        Regression_PciStartup_SyncsConfigToActualBlockState();
        Regression_WmiEcoLiveApply_DoesNotLeaveStaleTrigger();
        Regression_PciSwitchBackToStandard_LiveRemovesBlocks();
        Pci_LiveStandard_HelperFailure_FallsBackToReboot();
        Pci_MuxLatched_ClickEco_EcoBlocked();
        Pci_MuxLiveZero_ClickEco_EcoBlocked();
        Pci_FromStandard_ClickEco_DriverActive_ShowsDialog();

        Console.WriteLine("\n GPU panel visibility (LinuxAsusWmi direct, no dgpu_disable firmware) ");
        IsPciBackendUsable_PciDisabled_ReturnsFalse();
        IsPciBackendUsable_PciEnabled_DGpuOnBus_ReturnsTrue();
        IsPciBackendUsable_PciEnabled_NoDGpu_BlockArtifactsPresent_ReturnsTrue();
        IsPciBackendUsable_PciEnabled_NoDGpu_NvidiaModuleLoaded_ReturnsTrue();
        IsPciBackendUsable_PciEnabled_NoDGpu_NvidiaOnDisk_ReturnsTrue();
        IsPciBackendUsable_PciEnabled_NoEvidence_ReturnsFalse();
        InvalidateGpuPresenceCache_FlushesAfterPciRescan();
        Regression_ProArt_PciEco_PanelStaysVisible();
        Regression_DGpuLessLaptop_NoPanelEvenWithPciFlag();
        Regression_LivePciTransitionCallback_InvalidatesCache();

        Console.WriteLine("\n GPU topology (single vs dual GPU) ");
        Topo_IgpuOnly_NouveauOnDisk_NoSecondGpu_PciUnusable();
        Topo_TwoDisplayFunctions_SecondGpu();
        Topo_EcoArtifacts_SecondGpu();
        Topo_LoadedNvidiaModule_SecondGpu();
        Topo_ValidatedSlotCache_SecondGpu();
        Topo_ForeignSlotCache_NotSecondGpu();
        Topo_StaleSlotCache_ClearedAfterThreeStarts();
        Topo_NouveauOnDisk_WithValidatedSlot_PciUsable();

        Console.WriteLine("\n Power limit bounds ");
        PowerLimitBounds_NoFirmwareRange_UsesModelTable();
        PowerLimitBounds_FirmwareRange_WinsOverTable();
        PowerLimitBounds_PartialFirmwareRange_FillsFromTable();
        PowerLimitBounds_InvertedFirmwareRange_FallsBack();
        PowerLimitBounds_Sanitize_PoisonFloor_Dropped();
        PowerLimitBounds_Sanitize_BelowFirmwareMin_ClampedUp();
        PowerLimitBounds_Sanitize_AboveMax_ClampedDown();
        PowerLimitBounds_Sanitize_InRange_Unchanged();

        Console.WriteLine("\n GPU query gate ");
        Gate_Extend_NeverShortensWindow();
        Gate_Extend_DoesNotClearHold();
    }

    // 
    // Full matrix: { backend, raw_wmi, initial_mode, button } × 4 outcomes.
    // Exhaustive cross-product so any future refactor that breaks one
    // combination shows up immediately. Each row is a single Scenario.
    // 

    enum HwState { Standard, Eco, Ultimate }

    static (int mux, bool eco) HwSetup(HwState s) => s switch
    {
        HwState.Standard => (1, false),
        HwState.Eco => (1, true),
        HwState.Ultimate => (0, false),
        _ => (1, false),
    };

    static void RunFullMatrix()
    {
        // PCI backend × Standard/Eco × { Eco, Standard, Ultimate, Optimized } buttons.
        // Ultimate hw-state is not realistic in PCI mode (no MUX), so it is
        // covered only in the WMI half of the matrix.
        foreach (var rawWmi in new[] { false, true })
        foreach (var hw in new[] { HwState.Standard, HwState.Eco })
        foreach (var blocks in new[] { false, true })
        foreach (var button in new[] { GpuMode.Eco, GpuMode.Standard, GpuMode.Ultimate, GpuMode.Optimized })
        {
            // Skip nonsensical combos: PCI mode in "Eco hw" without blocks
            // is impossible (PCI mode reads blocks as the source of truth).
            // Encode that mismatch only when it would test something real.
            if (hw == HwState.Eco && !blocks) continue;
            if (hw == HwState.Standard && blocks) continue;

            string name = $"Matrix_Pci_{(rawWmi ? "rawwmi" : "norawwmi")}_{hw}_Click{button}";
            Scenario(name, sb =>
            {
                AppConfig.Set("gpu_backend", "pci");
                AppConfig.Set("raw_wmi", rawWmi ? 1 : 0);
                (sb.Wmi.MuxMode, sb.Wmi.EcoEnabled) = HwSetup(hw);
                if (blocks) sb.WriteBlockArtifacts();

                var result = sb.Controller.RequestModeSwitch(button);

                // Expectation matrix for PCI:
                // - Eco hw + click Eco → AlreadySet
                // - Eco hw + click Std/Ult/Opt → Applied (live recovery)
                // - Std hw + click Eco → Applied (live, driver idle)
                // - Std hw + click Std/Opt/Ult → AlreadySet
                bool wantEco = (button == GpuMode.Eco);
                if (hw == HwState.Eco && wantEco)
                    AssertEqual(GpuSwitchResult.AlreadySet, result, "Eco→Eco AlreadySet");
                else if (hw == HwState.Eco && !wantEco)
                    AssertEqual(GpuSwitchResult.Applied, result, "Eco→Std/Ult/Opt live Applied");
                else if (hw == HwState.Standard && wantEco)
                    AssertEqual(GpuSwitchResult.Applied, result, "Std→Eco live Applied");
                else
                    AssertEqual(GpuSwitchResult.AlreadySet, result, "Std→Std/Ult/Opt AlreadySet");
            });
        }

        // ASUS-WMI backend × all hw × all buttons.
        foreach (var rawWmi in new[] { false, true })
        foreach (var hw in new[] { HwState.Standard, HwState.Eco, HwState.Ultimate })
        foreach (var button in new[] { GpuMode.Eco, GpuMode.Standard, GpuMode.Ultimate, GpuMode.Optimized })
        {
            string name = $"Matrix_Wmi_{(rawWmi ? "rawwmi" : "norawwmi")}_{hw}_Click{button}";
            Scenario(name, sb =>
            {
                AppConfig.Set("gpu_backend", "asus-wmi");
                AppConfig.Set("raw_wmi", rawWmi ? 1 : 0);
                (sb.Wmi.MuxMode, sb.Wmi.EcoEnabled) = HwSetup(hw);

                var result = sb.Controller.RequestModeSwitch(button);

                // Build expected outcome from the same rules ComputeAndExecute follows.
                var (mux, eco) = HwSetup(hw);
                GpuSwitchResult expected;
                if (button == GpuMode.Eco)
                {
                    if (hw == HwState.Eco)
                        expected = GpuSwitchResult.AlreadySet;
                    else if (hw == HwState.Ultimate)
                        // Ultimate→Eco latches MUX→1 first, deferred.
                        expected = GpuSwitchResult.RebootRequired;
                    else
                        // Standard→Eco applies live (driver idle in test env).
                        expected = GpuSwitchResult.Applied;
                }
                else if (button == GpuMode.Standard)
                {
                    if (hw == HwState.Standard)
                        expected = GpuSwitchResult.AlreadySet;
                    else if (hw == HwState.Eco)
                        // Eco→Standard always safe, applies live.
                        expected = GpuSwitchResult.Applied;
                    else
                        // Ultimate→Standard requires MUX latch.
                        expected = GpuSwitchResult.RebootRequired;
                }
                else if (button == GpuMode.Ultimate)
                {
                    if (hw == HwState.Ultimate)
                        expected = GpuSwitchResult.AlreadySet;
                    else
                        // Switching INTO Ultimate latches MUX→0 - reboot.
                        expected = GpuSwitchResult.RebootRequired;
                }
                else // Optimized
                {
                    // Optimized targetMux=1. From Std/Eco that matches; from
                    // Ultimate it requires MUX→1 latch.
                    if (hw == HwState.Ultimate)
                        expected = GpuSwitchResult.RebootRequired;
                    else if (hw == HwState.Eco)
                        // hardware in Eco but Optimized doesn't target Eco
                        // unconditionally - depends on AC state. AC=true in
                        // FakePowerManager so target is Standard (dGPU on).
                        // ComputeAndExecute calls ExecuteEnableDgpu → Applied.
                        expected = GpuSwitchResult.Applied;
                    else // Standard
                        expected = GpuSwitchResult.AlreadySet;
                }

                AssertEqual(expected, result, $"{hw}→{button} (rawWmi={rawWmi})");
            });
        }
    }

    // 
    // Bug regressions - each test pins behaviour we recently fixed so
    // it cannot silently revert.
    // 

    static void Regression_PciToggleAfterUltimate_PreservesLatch_EcoBlocked()
        => Scenario(nameof(Regression_PciToggleAfterUltimate_PreservesLatch_EcoBlocked), sb =>
        {
            // Pin the security fix from the impossible-state task:
            //   Click Ultimate (MUX=0 latched this boot).
            //   Toggle PCI checkbox.
            //   Click Eco.
            // PushBackendMarker("pci") must NOT clear the latch flag. The
            // subsequent Eco click must be refused via EcoBlocked so the
            // user can't get into MUX=0 + udev-removed dGPU = black screen.
            AppConfig.Set("gpu_backend", "asus-wmi");
            string bootId;
            try
            {
                bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            }
            catch
            {
                return; // non-Linux runner
            }
            AppConfig.Set("mux_zero_latched_boot_id", bootId);

            sb.Controller.PushBackendMarker("pci");
            AppConfig.Set("gpu_backend", "pci");

            // Latch flag must still be there after the backend toggle.
            AssertEqual(bootId, AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "latch preserved across backend toggle");

            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.EcoBlocked, result, "Eco refused while latch is set");
            Assert(!sb.ModprobePresent(), "no block artifacts written");
        });

    static void Regression_PciBlocksRemainAfterToggle_GetCurrentModeIsEco()
        => Scenario(nameof(Regression_PciBlocksRemainAfterToggle_GetCurrentModeIsEco), sb =>
        {
            // The "checkbox toggled but dGPU still disabled, UI shows
            // Standard" bug: blocks persist as intentional persistent state.
            // GetCurrentMode must report Eco so the UI doesn't lie.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            // Toggle off.
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(),
                "blocks present override live dgpu_disable readback");
        });

    static void Regression_PciEcoAlreadyApplied_NoSpuriousReboot()
        => Scenario(nameof(Regression_PciEcoAlreadyApplied_NoSpuriousReboot), sb =>
        {
            // If the user is already in PCI eco (blocks present, config
            // gpu_mode=eco), clicking Eco again must NOT write a fresh
            // trigger and pop another reboot prompt.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            AppConfig.Set("gpu_mode", "eco");

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);
            AssertEqual(GpuSwitchResult.AlreadySet, result, "no reboot scheduled");
            Assert(!sb.TriggerPresent(), "no stale trigger");
        });

    static void Regression_BackendMarkerMissing_DefaultsToAsusWmi()
        => Scenario(nameof(Regression_BackendMarkerMissing_DefaultsToAsusWmi), sb =>
        {
            // No backend file on disk, no config flag → fall back to
            // asus-wmi (the historical default).
            AssertEqual("asus-wmi", AppConfig.GetGpuBackend(), "default backend");
            Assert(!AppConfig.IsPciGpuBackend(), "PCI not the default");
        });

    static void Regression_StaleMuxLatchFlag_ClearedOnStartup()
        => Scenario(nameof(Regression_StaleMuxLatchFlag_ClearedOnStartup), sb =>
        {
            // Cross-boot stale flag (stored boot_id != current).
            AppConfig.Set("mux_zero_latched_boot_id", "some-old-boot-id-that-doesnt-match");
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual("", AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "stale flag cleared on startup");
        });

    static void Regression_PciStartup_SyncsConfigToActualBlockState()
        => Scenario(nameof(Regression_PciStartup_SyncsConfigToActualBlockState), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            AppConfig.Set("gpu_mode", "standard"); // out of sync
            sb.WriteBlockArtifacts();

            sb.Controller.ApplyPendingOnStartup();
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config synced");
        });

    static void Regression_WmiEcoLiveApply_DoesNotLeaveStaleTrigger()
        => Scenario(nameof(Regression_WmiEcoLiveApply_DoesNotLeaveStaleTrigger), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);
            AssertEqual(GpuSwitchResult.Applied, result, "live applied");
            Assert(!sb.TriggerPresent(), "no trigger left after live apply");
            // Block artifacts may or may not have been written; either way
            // they should be cleaned up after a live apply.
            Assert(!sb.ModprobePresent(), "modprobe block cleaned after live Eco");
        });

    static void Pci_LiveStandard_HelperFailure_FallsBackToReboot()
        => Scenario(nameof(Pci_LiveStandard_HelperFailure_FallsBackToReboot), sb =>
        {
            // Simulate the sudo/pkexec call failing (user cancelled, file
            // perms, etc.). Controller must fall through to the deferred
            // path so the user still gets out of Eco eventually.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            Environment.SetEnvironmentVariable("GHELPER_TEST_FAIL_LIVE_STANDARD", "1");
            try
            {
                var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);
                AssertEqual(GpuSwitchResult.RebootRequired, result, "fell back to deferred reboot");
                AssertEqual("standard", sb.TriggerContent(), "trigger=standard for boot script");
                // Blocks are still removed because WriteDriverBlock(Standard)
                // in PCI mode also removes them defensively.
                Assert(!sb.ModprobePresent(), "blocks cleared by WriteDriverBlock");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GHELPER_TEST_FAIL_LIVE_STANDARD", null);
            }
        });

    static void Pci_MuxLatched_ClickEco_EcoBlocked()
        => Scenario(nameof(Pci_MuxLatched_ClickEco_EcoBlocked), sb =>
        {
            // Persistent flag set this boot + PCI mode + Eco click → refused.
            AppConfig.Set("gpu_backend", "pci");
            string bootId;
            try { bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(); }
            catch { return; }
            AppConfig.Set("mux_zero_latched_boot_id", bootId);

            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.EcoBlocked, result, "Eco refused due to latch");
            Assert(!sb.ModprobePresent(), "no block artifacts written");
            Assert(!sb.TriggerPresent(), "no trigger written");
        });

    static void Pci_MuxLiveZero_ClickEco_EcoBlocked()
        => Scenario(nameof(Pci_MuxLiveZero_ClickEco_EcoBlocked), sb =>
        {
            // Live MUX=0 (no persistent flag, just hardware in Ultimate) +
            // PCI mode + Eco click → refused via GetEffectiveMux check.
            AppConfig.Set("gpu_backend", "pci");
            sb.Wmi.MuxMode = 0; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.EcoBlocked, result, "Eco refused due to live MUX=0");
            Assert(!sb.ModprobePresent(), "no block artifacts written");
        });

    static void Pci_FromStandard_ClickEco_DriverActive_ShowsDialog()
        => Scenario(nameof(Pci_FromStandard_ClickEco_DriverActive_ShowsDialog), sb =>
        {
            // dGPU driver loaded: live Eco is unsafe, so the UI gets the Switch
            // Now / After Reboot dialog (DriverBlocking) - no artifacts yet.
            AppConfig.Set("gpu_backend", "pci");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            sb.WriteFakeNvidiaModule();

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.DriverBlocking, result, "driver active → dialog");
            Assert(!sb.ModprobePresent(), "no blocks written yet");
            Assert(!sb.TriggerPresent(), "no trigger written yet");
        });

    static void Regression_PciSwitchBackToStandard_LiveRemovesBlocks()
        => Scenario(nameof(Regression_PciSwitchBackToStandard_LiveRemovesBlocks), sb =>
        {
            // PCI eco state: blocks on disk. User clicks Standard → live
            // recovery removes blocks + reloads udev + rescans PCI in one
            // sudo call. No reboot, no trigger file written.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);
            AssertEqual(GpuSwitchResult.Applied, result, "live applied");
            Assert(!sb.TriggerPresent(), "no trigger written (live path bypasses boot script)");
            Assert(!sb.ModprobePresent(), "modprobe block removed");
            Assert(!sb.UdevPresent(), "udev rule removed");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    // 
    // PCI backend
    // 

    static void Pci_FromStandard_ClickEco_WritesBlocks_AndAppliesLive()
        => Scenario(nameof(Pci_FromStandard_ClickEco_WritesBlocks_AndAppliesLive), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            // Driver idle, MUX hybrid: live Eco. Blocks + trigger still written
            // (boot-script fallback) before the live PCI remove.
            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.ModprobePresent(), "modprobe block written");
            Assert(sb.UdevPresent(), "udev rule written");
            AssertEqual("eco", sb.TriggerContent(), "trigger content");
            AssertEqual("pci", sb.BackendContent(), "backend marker");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "gpu_mode config");
        });

    static void Pci_FromEco_ClickEco_AlreadySet_NoOp()
        => Scenario(nameof(Pci_FromEco_ClickEco_AlreadySet_NoOp), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
            Assert(!sb.TriggerPresent(), "no trigger written");
            Assert(sb.ModprobePresent() && sb.UdevPresent(), "blocks preserved");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "gpu_mode config");
        });

    static void Pci_FromEco_ClickStandard_AppliesLive()
        => Scenario(nameof(Pci_FromEco_ClickStandard_AppliesLive), sb =>
        {
            // Eco→Standard in PCI mode runs the live recovery path: remove
            // blocks + reload udev + PCI rescan, no reboot needed. Matches
            // the asus-wmi SetGpuEco(false) live behaviour.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.Applied, result, "live transition Applied");
            Assert(!sb.ModprobePresent(), "modprobe block removed");
            Assert(!sb.UdevPresent(), "udev rule removed");
            Assert(!sb.TriggerPresent(), "no trigger file (no reboot needed)");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "gpu_mode config");
        });

    static void Pci_FromStandard_ClickStandard_AlreadySet()
        => Scenario(nameof(Pci_FromStandard_ClickStandard_AlreadySet), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
            Assert(!sb.TriggerPresent(), "no trigger written");
            Assert(!sb.ModprobePresent(), "no blocks created");
        });

    static void Pci_ClickUltimate_TreatedAsStandard()
        => Scenario(nameof(Pci_ClickUltimate_TreatedAsStandard), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();

            var result = sb.Controller.RequestModeSwitch(GpuMode.Ultimate);

            // Ultimate has no meaning in PCI mode → controller normalises
            // to Standard. Since blocks are present that triggers the live
            // recovery path, so the result is Applied (not RebootRequired).
            AssertEqual(GpuSwitchResult.Applied, result, "live transition Applied");
            Assert(!sb.ModprobePresent(), "blocks removed by live path");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config normalized");
        });

    static void Pci_ClickOptimized_TreatedAsStandard()
        => Scenario(nameof(Pci_ClickOptimized_TreatedAsStandard), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");

            var result = sb.Controller.RequestModeSwitch(GpuMode.Optimized);

            // Optimized doesn't apply in PCI mode either. With no blocks
            // present + Standard target it normalises to AlreadySet.
            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config normalized");
        });

    static void Pci_GetCurrentMode_NoBlocks_ReturnsStandard()
        => Scenario(nameof(Pci_GetCurrentMode_NoBlocks_ReturnsStandard), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            AssertEqual(GpuMode.Standard, sb.Controller.GetCurrentMode(), "mode");
        });

    static void Pci_GetCurrentMode_BlocksPresent_ReturnsEco()
        => Scenario(nameof(Pci_GetCurrentMode_BlocksPresent_ReturnsEco), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(), "mode");
        });

    static void Pci_IsPendingReboot_TriggerFilePresent_ReturnsTrue()
        => Scenario(nameof(Pci_IsPendingReboot_TriggerFilePresent_ReturnsTrue), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteTrigger("standard");
            Assert(sb.Controller.IsPendingReboot(), "pending");
        });

    static void Pci_IsPendingReboot_NoTriggerFile_ReturnsFalse()
        => Scenario(nameof(Pci_IsPendingReboot_NoTriggerFile_ReturnsFalse), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            Assert(!sb.Controller.IsPendingReboot(), "not pending");
        });

    static void Pci_ApplyPendingOnStartup_SyncsConfigFromBlocks()
        => Scenario(nameof(Pci_ApplyPendingOnStartup_SyncsConfigFromBlocks), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            AppConfig.Set("gpu_mode", "standard"); // stale

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config synced to actual block state");
        });

    static void Pci_ApplyPendingOnStartup_ClearsStaleLatchFlag()
        => Scenario(nameof(Pci_ApplyPendingOnStartup_ClearsStaleLatchFlag), sb =>
        {
            // Stale across-boot flag: stored boot_id != current. Universal
            // ClearStaleMuxLatchFlag at the top of ApplyPendingOnStartup
            // clears it before the PCI branch even runs.
            AppConfig.Set("gpu_backend", "pci");
            AppConfig.Set("mux_zero_latched_boot_id", "stale-from-previous-boot");

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual("", AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "cross-boot stale flag is cleared");
        });

    static void Pci_ApplyPendingOnStartup_KeepsCurrentBootLatchFlag()
        => Scenario(nameof(Pci_ApplyPendingOnStartup_KeepsCurrentBootLatchFlag), sb =>
        {
            // Flag matches current boot_id - MUX=0 was latched this boot,
            // so the firmware will land in MUX=0 on next reboot. PCI Eco
            // must remain refused. The flag must NOT be cleared.
            AppConfig.Set("gpu_backend", "pci");
            string currentBootId;
            try
            {
                currentBootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            }
            catch
            {
                // /proc not available - skip on non-Linux runners.
                return;
            }
            AppConfig.Set("mux_zero_latched_boot_id", currentBootId);

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual(currentBootId, AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "current-boot flag must survive PCI startup");
        });

    // 
    // ASUS-WMI backend
    // 

    static void Wmi_FromStandard_ClickEco_DriverIdle_AppliesLive()
        => Scenario(nameof(Wmi_FromStandard_ClickEco_DriverIdle_AppliesLive), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            // No nvidia/nouveau modules in test env → driver idle → eco applies live.
            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1 written");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    static void Wmi_FromEco_ClickStandard_EnablesDgpu()
        => Scenario(nameof(Wmi_FromEco_ClickStandard_EnablesDgpu), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(!sb.Wmi.EcoEnabled, "dgpu_disable=0 written");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    static void Wmi_FromEco_ClickEco_AlreadySet()
        => Scenario(nameof(Wmi_FromEco_ClickEco_AlreadySet), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
            // SetGpuEco must NOT be called again
            int writes = 0;
            foreach (var c in sb.Wmi.Calls) if (c.Method == "SetGpuEco") writes++;
            AssertEqual(0, writes, "no redundant SetGpuEco");
        });

    static void Wmi_FromStandard_ClickStandard_AlreadySet()
        => Scenario(nameof(Wmi_FromStandard_ClickStandard_AlreadySet), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
        });

    static void Wmi_FromStandard_ClickUltimate_LatchesMuxAndRequiresReboot()
        => Scenario(nameof(Wmi_FromStandard_ClickUltimate_LatchesMuxAndRequiresReboot), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Ultimate);

            AssertEqual(GpuSwitchResult.RebootRequired, result, "result");
            AssertEqual(0, sb.Wmi.MuxMode, "MUX=0 latched");
            AssertEqual("ultimate", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    static void Wmi_FromUltimate_ClickStandard_LatchesMuxAndRequiresReboot()
        => Scenario(nameof(Wmi_FromUltimate_ClickStandard_LatchesMuxAndRequiresReboot), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 0; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.RebootRequired, result, "result");
            AssertEqual(1, sb.Wmi.MuxMode, "MUX=1 latched");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    static void Wmi_FromUltimate_ClickEco_LatchesMuxFirst_RebootRequired()
        => Scenario(nameof(Wmi_FromUltimate_ClickEco_LatchesMuxFirst_RebootRequired), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 0; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            // Ultimate→Eco is a 2-boot transition. First click latches MUX→1.
            AssertEqual(GpuSwitchResult.RebootRequired, result, "result");
            AssertEqual(1, sb.Wmi.MuxMode, "MUX=1 latched first");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "eco saved for after-reboot");
        });

    static void Wmi_MuxZeroLatched_ClickEco_Blocked()
        => Scenario(nameof(Wmi_MuxZeroLatched_ClickEco_Blocked), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            // ScheduleModeForReboot is the deferred-Eco path the dialog's
            // "After Reboot" button hits. Live ExecuteDisableDgpu has its
            // own MUX guard but does not honour the persistent latch flag
            // because once MUX=1 reads live, the next boot's BootSafetyCheck
            // recovers any impossible pair. The persistent flag specifically
            // guards the deferred path so blocks aren't laid down for a
            // boot that would land in MUX=0 + udev-removed dGPU.
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            try
            {
                string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                AppConfig.Set("mux_zero_latched_boot_id", bootId);
            }
            catch
            {
                // /proc not available (non-Linux runner) - skip silently.
                return;
            }

            var result = sb.Controller.ScheduleModeForReboot(GpuMode.Eco);
            AssertEqual(GpuSwitchResult.EcoBlocked, result, "ScheduleModeForReboot refuses Eco when MUX=0 latched");
            Assert(!sb.ModprobePresent(), "no block artifacts written");
            Assert(!sb.TriggerPresent(), "no trigger written");
        });

    static void Wmi_SetGpuEcoThrows_ReturnsFailed_ConfigUnchanged()
        => Scenario(nameof(Wmi_SetGpuEcoThrows_ReturnsFailed_ConfigUnchanged), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            AppConfig.Set("gpu_mode", "standard"); // baseline
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            sb.Wmi.NextSetGpuEcoException = new IOException("firmware EIO");

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.Failed, result, "result");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config not changed on failure");
        });

    static void Wmi_BlocksPresent_GetCurrentMode_ReturnsEco()
        => Scenario(nameof(Wmi_BlocksPresent_GetCurrentMode_ReturnsEco), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false; // hw says standard
            sb.WriteBlockArtifacts();                       // but blocks say eco

            // The fix from the previous bug: block files should make
            // GetCurrentMode report Eco even on asus-wmi mode, otherwise
            // the UI lies after a backend toggle from PCI eco.
            AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(), "blocks override dgpu_disable readback");
        });

    static void Wmi_FromStandard_ClickEco_ScheduledTriggerWrittenForBootScript()
        => Scenario(nameof(Wmi_FromStandard_ClickEco_ScheduledTriggerWrittenForBootScript), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            // Live path applies because driver isn't active in test env.
            // Confirm config is saved correctly even on a live apply (no
            // trigger expected because the boot script doesn't need to run).
            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);
            AssertEqual(GpuSwitchResult.Applied, result, "applied live");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    // 
    // Backend toggle plumbing (PushBackendMarker)
    // 

    static void PushBackendMarker_Pci_WritesMarkerFile()
        => Scenario(nameof(PushBackendMarker_Pci_WritesMarkerFile), sb =>
        {
            sb.Controller.PushBackendMarker("pci");
            AssertEqual("pci", sb.BackendContent(), "marker file content");
        });

    static void PushBackendMarker_AsusWmi_WritesMarkerFile()
        => Scenario(nameof(PushBackendMarker_AsusWmi_WritesMarkerFile), sb =>
        {
            sb.WriteBackend("pci");
            sb.Controller.PushBackendMarker("asus-wmi");
            AssertEqual("asus-wmi", sb.BackendContent(), "marker file content");
        });

    static void PushBackendMarker_Pci_PreservesMuxLatchFlag()
        => Scenario(nameof(PushBackendMarker_Pci_PreservesMuxLatchFlag), sb =>
        {
            // The MUX=0 latch flag encodes real firmware state (gpu_mux_mode=0
            // is pending until the next reboot). A backend toggle does NOT
            // change firmware. Clearing the flag here would re-introduce
            // the impossible-state chain: user Ultimate -> PCI toggle ->
            // Eco -> blocks written -> reboot lands in MUX=0 with udev
            // removing the dGPU = black screen.
            AppConfig.Set("mux_zero_latched_boot_id", "any-boot-id");
            sb.Controller.PushBackendMarker("pci");
            AssertEqual("any-boot-id", AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "latch flag preserved across PCI backend toggle");
        });

    static void PushBackendMarker_AsusWmi_DoesNotTouchLatchFlag()
        => Scenario(nameof(PushBackendMarker_AsusWmi_DoesNotTouchLatchFlag), sb =>
        {
            AppConfig.Set("mux_zero_latched_boot_id", "from-asus-wmi-session");
            sb.Controller.PushBackendMarker("asus-wmi");
            AssertEqual("from-asus-wmi-session", AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "asus-wmi backend preserves its own MUX state");
        });

    static void PushBackendMarker_Invalid_NoOp()
        => Scenario(nameof(PushBackendMarker_Invalid_NoOp), sb =>
        {
            sb.WriteBackend("pci");
            sb.Controller.PushBackendMarker("nonsense");
            AssertEqual("pci", sb.BackendContent(), "invalid backend rejected, no rewrite");
        });

    static void PushBackendMarker_Idempotent_NoRewrite()
        => Scenario(nameof(PushBackendMarker_Idempotent_NoRewrite), sb =>
        {
            sb.WriteBackend("pci");
            // Tamper the file timestamp to detect rewrite.
            var beforeWrite = File.GetLastWriteTimeUtc(sb.BackendPath);
            Thread.Sleep(20);
            sb.Controller.PushBackendMarker("pci");
            var afterWrite = File.GetLastWriteTimeUtc(sb.BackendPath);
            AssertEqual(beforeWrite, afterWrite, "file not rewritten when backend already correct");
        });

    // 
    // raw_wmi flag (must not affect mode-switching behaviour)
    // 

    static void RawWmi_DoesNotChangeButtonBehaviour_StandardClick()
        => Scenario(nameof(RawWmi_DoesNotChangeButtonBehaviour_StandardClick), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            AppConfig.Set("raw_wmi", 1);
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(!sb.Wmi.EcoEnabled, "dgpu_disable=0");
        });

    static void RawWmi_DoesNotChangeButtonBehaviour_EcoClick()
        => Scenario(nameof(RawWmi_DoesNotChangeButtonBehaviour_EcoClick), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            AppConfig.Set("raw_wmi", 1);
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
        });

    // 
    // Cross-backend reality (UI must reflect actual dGPU state)
    // 

    static void ToggleBackendPciToWmi_LeavingBlocks_UiStillReportsEco()
        => Scenario(nameof(ToggleBackendPciToWmi_LeavingBlocks_UiStillReportsEco), sb =>
        {
            // Simulate: user was in PCI eco, blocks present, then ticked off
            // the checkbox. Backend switches to asus-wmi but blocks remain
            // (intentional - per project owner: dGPU stays disabled next
            // reboot). UI must NOT claim Standard.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();

            // User toggles off.
            sb.Controller.PushBackendMarker("asus-wmi");
            AppConfig.Set("gpu_backend", "asus-wmi");

            // dgpu_disable=0 (live state), but blocks present → UI eco.
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(),
                "blocks present + asus-wmi backend = Eco (will reapply on reboot via udev)");
        });

    static void ToggleBackendWmiToPci_LeavingDgpuDisabled_UiReportsEco()
        => Scenario(nameof(ToggleBackendWmiToPci_LeavingDgpuDisabled_UiReportsEco), sb =>
        {
            // User in WMI eco (dgpu_disable=1), toggles backend to PCI.
            // In PCI mode the source of truth is block files; the firmware
            // dgpu_disable is irrelevant. With no blocks → UI = Standard.
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = true;

            sb.Controller.PushBackendMarker("pci");
            AppConfig.Set("gpu_backend", "pci");

            // No blocks present yet → PCI mode reports Standard. (User
            // would click Eco again to commit to PCI-style eco.)
            AssertEqual(GpuMode.Standard, sb.Controller.GetCurrentMode(),
                "PCI mode + no blocks = Standard (asus-wmi dgpu_disable ignored)");
        });

    // 
    // Ultimate is the happy path (must not regress)
    // 

    static void Wmi_Ultimate_FromEco_EnablesDgpuFirst()
        => Scenario(nameof(Wmi_Ultimate_FromEco_EnablesDgpuFirst), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Ultimate);

            AssertEqual(GpuSwitchResult.RebootRequired, result, "result");
            Assert(!sb.Wmi.EcoEnabled, "dgpu enabled before MUX latch (firmware requires this)");
            AssertEqual(0, sb.Wmi.MuxMode, "MUX=0 latched");
            AssertEqual("ultimate", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    static void Wmi_Ultimate_Twice_AlreadySet()
        => Scenario(nameof(Wmi_Ultimate_Twice_AlreadySet), sb =>
        {
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.Wmi.MuxMode = 0; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Ultimate);
            AssertEqual(GpuSwitchResult.AlreadySet, result, "Ultimate is the current mode → no-op");
        });

    static void IsPciBackendUsable_PciDisabled_ReturnsFalse()
        => Scenario(nameof(IsPciBackendUsable_PciDisabled_ReturnsFalse), sb =>
        {
            // User has not opted into the PCI backend (default config).
            // Even if a real NVIDIA dGPU is on the bus, IsPciBackendUsable
            // must return false - it speaks only to the PCI-backend pathway.
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.WriteFakeNvidiaPciDevice();

            Assert(!LinuxAsusWmi.IsPciBackendUsable(),
                "PCI backend disabled in config → not usable regardless of hardware");
        });

    static void IsPciBackendUsable_PciEnabled_DGpuOnBus_ReturnsTrue()
        => Scenario(nameof(IsPciBackendUsable_PciEnabled_DGpuOnBus_ReturnsTrue), sb =>
        {
            // Steady state in PCI Standard mode: user opted in and the
            // dGPU is sitting on the PCI bus normally.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteFakeNvidiaPciDevice();

            Assert(LinuxAsusWmi.IsPciBackendUsable(),
                "PCI + dGPU on bus → panel must show");
        });

    static void IsPciBackendUsable_PciEnabled_NoDGpu_BlockArtifactsPresent_ReturnsTrue()
        => Scenario(nameof(IsPciBackendUsable_PciEnabled_NoDGpu_BlockArtifactsPresent_ReturnsTrue), sb =>
        {
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            // intentionally NO WriteFakeNvidiaPciDevice() - dGPU is gone

            Assert(LinuxAsusWmi.IsPciBackendUsable(),
                "PCI + hot-removed dGPU + our block artifacts → panel must stay visible so user can click Standard");
        });

    static void IsPciBackendUsable_PciEnabled_NoDGpu_NvidiaModuleLoaded_ReturnsTrue()
        => Scenario(nameof(IsPciBackendUsable_PciEnabled_NoDGpu_NvidiaModuleLoaded_ReturnsTrue), sb =>
        {
            // PCI mode active, dGPU not on bus, blocks not yet on disk
            // (e.g., user toggled PCI mode in Extra Settings but has not
            // clicked Eco yet). Module-loaded fallback keeps panel visible.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteFakeNvidiaModule();

            Assert(LinuxAsusWmi.IsPciBackendUsable(),
                "PCI + nvidia module loaded → panel visible (driver is present, dGPU expected)");
        });

    static void IsPciBackendUsable_PciEnabled_NoDGpu_NvidiaOnDisk_ReturnsTrue()
        => Scenario(nameof(IsPciBackendUsable_PciEnabled_NoDGpu_NvidiaOnDisk_ReturnsTrue), sb =>
        {
            // Driver installed for a future kernel boot but not yet
            // loaded in the running kernel. Disk-side module check is the
            // final fallback before we give up.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteFakeNvidiaModuleOnDisk();

            Assert(LinuxAsusWmi.IsPciBackendUsable(),
                "PCI + nvidia.ko on disk → panel visible (driver installed, dGPU expected after reload)");
        });

    static void IsPciBackendUsable_PciEnabled_NoEvidence_ReturnsFalse()
        => Scenario(nameof(IsPciBackendUsable_PciEnabled_NoEvidence_ReturnsFalse), sb =>
        {
            // dGPU-less laptop where someone (or imported config) flipped
            // gpu_backend=pci. No dGPU on bus, no block artifacts, no
            // nvidia/nouveau modules. We must NOT show the GPU panel
            // because there is nothing to manage.
            AppConfig.Set("gpu_backend", "pci");
            // No fakes written - sandbox wipes everything in ctor.

            Assert(!LinuxAsusWmi.IsPciBackendUsable(),
                "PCI flag but zero dGPU evidence → panel hidden (matches requirement: don't show on dGPU-less laptops)");
        });

    static void InvalidateGpuPresenceCache_FlushesAfterPciRescan()
        => Scenario(nameof(InvalidateGpuPresenceCache_FlushesAfterPciRescan), sb =>
        {
            // Live PCI Eco→Standard transition: before the rescan the bus
            // shows no dGPU, after the rescan the dGPU is back. Without
            // cache invalidation the second probe would return the stale
            // "not present" reading and the panel could mis-render.
            AppConfig.Set("gpu_backend", "pci");

            // First probe: no device, cache primes to false.
            Assert(!LinuxAsusWmi.HasDiscreteNvidiaGpu(),
                "cache primed: no dGPU yet on the sandbox bus");

            // Simulate the rescan adding the dGPU back. WriteFakeNvidiaPciDevice
            // calls InvalidateGpuPresenceCache itself; this scenario asserts
            // that the next probe sees the new state.
            sb.WriteFakeNvidiaPciDevice();

            Assert(LinuxAsusWmi.HasDiscreteNvidiaGpu(),
                "after invalidation + fake device written, probe sees dGPU on bus");
        });

    static void Regression_ProArt_PciEco_PanelStaysVisible()
        => Scenario(nameof(Regression_ProArt_PciEco_PanelStaysVisible), sb =>
        {
            // End-to-end regression for issue #84: ProArt P16-class
            // hardware (no dgpu_disable firmware attribute) running PCI
            // backend. User clicks Eco, reboots, dGPU is hot-removed by
            // udev. Pre-fix the GPU panel disappeared because the only
            // PCI-mode condition was HasDiscreteNvidiaGpu() which is now
            // false. Post-fix the eco-block-artifact condition keeps the
            // panel visible.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteBlockArtifacts();
            sb.RemoveFakeNvidiaPciDevice();          // dGPU is gone from bus

            Assert(LinuxAsusWmi.IsPciBackendUsable(),
                "ProArt PCI-Eco state must keep panel visible (issue #84 regression)");

            // Also assert the controller agrees this is an Eco state so
            // the next button click is Standard.
            AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(),
                "blocks present + PCI backend = Eco");
        });

    static void Regression_DGpuLessLaptop_NoPanelEvenWithPciFlag()
        => Scenario(nameof(Regression_DGpuLessLaptop_NoPanelEvenWithPciFlag), sb =>
        {
            // A truly dGPU-less laptop (e.g., AMD APU only, Intel Iris).
            // Even with the stale gpu_backend=pci config, no dGPU, no
            // blocks we installed, no nvidia/nouveau anything = no panel.
            // Requirement #3 from the project owner.
            AppConfig.Set("gpu_backend", "pci");
            // No fakes - empty sandbox state.

            Assert(!LinuxAsusWmi.IsPciBackendUsable(),
                "dGPU-less laptop must never see the GPU panel even with PCI flag set");
        });

    static void Regression_LivePciTransitionCallback_InvalidatesCache()
        => Scenario(nameof(Regression_LivePciTransitionCallback_InvalidatesCache), sb =>
        {
            // The live Eco→Standard transition rescans /sys/bus/pci and
            // brings the dGPU back. GPUModeControl fires
            // OnLivePciTransition which production wires to
            // LinuxAsusWmi.InvalidateGpuPresenceCache. This scenario
            // asserts the callback contract is honoured end-to-end so the
            // panel reflects post-rescan state without an app restart.
            AppConfig.Set("gpu_backend", "pci");

            // Prime the cache as if we were post-Eco (no dGPU on bus).
            Assert(!LinuxAsusWmi.HasDiscreteNvidiaGpu(), "cache primed to no-dGPU");

            // Wire the callback the way App.axaml.cs does in production
            // and fire it after simulating the rescan effect.
            GPUModeControl.OnLivePciTransition = LinuxAsusWmi.InvalidateGpuPresenceCache;
            try
            {
                Directory.CreateDirectory(Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", "0000:01:00.0"));
                File.WriteAllText(Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", "0000:01:00.0", "vendor"), "0x10de\n");
                File.WriteAllText(Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", "0000:01:00.0", "class"), "0x030000\n");

                // Without firing the callback the probe would still return false (stale cache).
                Assert(!LinuxAsusWmi.HasDiscreteNvidiaGpu(),
                    "before callback fires: cache still says no-dGPU");

                GPUModeControl.OnLivePciTransition?.Invoke();

                Assert(LinuxAsusWmi.HasDiscreteNvidiaGpu(),
                    "after callback fires: probe re-scans and sees the dGPU");
            }
            finally
            {
                GPUModeControl.OnLivePciTransition = null;
            }
        });

    //
    // GPU topology: HasSecondGpu decides whether the switching UI (panel,
    // tray items, backend selector) exists at all. Single-GPU machines
    // must never see it; a dGPU hidden by our own Eco must keep it.
    //

    static void Topo_IgpuOnly_NouveauOnDisk_NoSecondGpu_PciUnusable()
        => Scenario(nameof(Topo_IgpuOnly_NouveauOnDisk_NoSecondGpu_PciUnusable), sb =>
        {
            // Legion Go S class machine: AMD iGPU only, gpu_backend
            // auto-defaulted to pci, nouveau shipped by the distro kernel.
            // The old module-on-disk fallback used to unhide the whole
            // switching UI here.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeNouveauOnDisk();

            Assert(!GPUModeControl.HasSecondGpu(),
                "iGPU + nouveau.ko on disk is not a second GPU");
            Assert(!LinuxAsusWmi.IsPciBackendUsable(),
                "PCI backend unusable without second-GPU evidence");
        });

    static void Topo_TwoDisplayFunctions_SecondGpu()
        => Scenario(nameof(Topo_TwoDisplayFunctions_SecondGpu), sb =>
        {
            // Vendor-agnostic count: AMD iGPU + Intel Arc dGPU. The
            // NVIDIA/AMD-specific dGPU scan does not know Arc; the
            // display-class count still reports two GPUs.
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeIntelDisplayDevice();

            Assert(GPUModeControl.HasSecondGpu(),
                "two display-class functions = second GPU");
        });

    static void Topo_EcoArtifacts_SecondGpu()
        => Scenario(nameof(Topo_EcoArtifacts_SecondGpu), sb =>
        {
            // dGPU hot-removed by our own PCI Eco: only the block
            // artifacts prove it exists. The switching UI must survive
            // so the user can undo Eco.
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteBlockArtifacts();

            Assert(GPUModeControl.HasSecondGpu(),
                "eco block artifacts = hidden dGPU");
        });

    static void Topo_LoadedNvidiaModule_SecondGpu()
        => Scenario(nameof(Topo_LoadedNvidiaModule_SecondGpu), sb =>
        {
            // nvidia bound with no matching PCI device: the device was
            // hot-removed after the driver loaded. Never happens on
            // machines without NVIDIA hardware.
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeNvidiaModule();

            Assert(GPUModeControl.HasSecondGpu(),
                "loaded nvidia module = hidden dGPU");
        });

    static void Topo_ValidatedSlotCache_SecondGpu()
        => Scenario(nameof(Topo_ValidatedSlotCache_SecondGpu), sb =>
        {
            // Cached slot whose address matches the cached dGPU BDF:
            // this machine's dGPU, currently invisible (deep Eco).
            sb.WriteFakeAmdIgpuDevice();
            AppConfig.Set("dgpu_pci_slot", "1");
            AppConfig.Set("dgpu_pci_bdf", "0000:01:00.0");
            sb.WriteFakeSlot("1", "0000:01:00");

            Assert(GPUModeControl.HasSecondGpu(),
                "validated slot cache = second GPU");
        });

    static void Topo_ForeignSlotCache_NotSecondGpu()
        => Scenario(nameof(Topo_ForeignSlotCache_NotSecondGpu), sb =>
        {
            // Config imported from another machine: slot name collides
            // with a local slot but the address does not match the cached
            // BDF. Must not fake a dGPU.
            sb.WriteFakeAmdIgpuDevice();
            AppConfig.Set("dgpu_pci_slot", "1");
            AppConfig.Set("dgpu_pci_bdf", "0000:01:00.0");
            sb.WriteFakeSlot("1", "0000:c4:00");

            Assert(!GPUModeControl.HasSecondGpu(),
                "foreign slot cache (address mismatch) is not a second GPU");
        });

    static void Topo_StaleSlotCache_ClearedAfterThreeStarts()
        => Scenario(nameof(Topo_StaleSlotCache_ClearedAfterThreeStarts), sb =>
        {
            // Imported config on an iGPU-only machine: no slot dir at all.
            // Three consecutive startups with no dGPU evidence age the
            // cache out; the fourth sees a clean config.
            sb.WriteFakeAmdIgpuDevice();
            AppConfig.Set("dgpu_pci_slot", "9");
            AppConfig.Set("dgpu_pci_bdf", "0000:99:00.0");

            for (int i = 1; i <= 3; i++)
                sb.Controller.CacheDgpuSlotIfPresent();

            Assert(string.IsNullOrEmpty(AppConfig.GetString("dgpu_pci_slot")),
                "slot cache cleared after 3 startups without evidence");
            Assert(string.IsNullOrEmpty(AppConfig.GetString("dgpu_pci_bdf")),
                "bdf cache cleared with it");
            Assert(!GPUModeControl.HasSecondGpu(),
                "no phantom dGPU after cache aged out");
        });

    static void Topo_NouveauOnDisk_WithValidatedSlot_PciUsable()
        => Scenario(nameof(Topo_NouveauOnDisk_WithValidatedSlot_PciUsable), sb =>
        {
            // Weak module evidence + validated slot = real dual-GPU
            // machine mid-Eco with artifacts already removed: the panel
            // must stay reachable.
            AppConfig.Set("gpu_backend", "pci");
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeNouveauOnDisk();
            AppConfig.Set("dgpu_pci_slot", "1");
            AppConfig.Set("dgpu_pci_bdf", "0000:01:00.0");
            sb.WriteFakeSlot("1", "0000:01:00");

            Assert(GPUModeControl.HasSecondGpu(),
                "validated slot outweighs weak module evidence");
            Assert(LinuxAsusWmi.IsPciBackendUsable(),
                "PCI backend usable: nouveau on disk + validated slot");
        });

    // A dGPU enable can outrun its opening pause, so the recovery ladder
    // extends the window as it goes. Extending must never pull the deadline
    // in, or a long recovery would un-pause itself mid-flight.
    static void Gate_Extend_NeverShortensWindow()
        => Scenario(nameof(Gate_Extend_NeverShortensWindow), sb =>
        {
            Gate.Resume();

            Gate.Pause(TimeSpan.FromSeconds(60), "test");
            Gate.Extend(TimeSpan.Zero, "test");
            Assert(Gate.IsPaused, "a shorter Extend does not shorten an open window");

            Gate.Resume();
            Gate.Pause(TimeSpan.Zero, "test");
            Assert(!Gate.IsPaused, "zero pause is already expired");
            Gate.Extend(TimeSpan.FromSeconds(60), "test");
            Assert(Gate.IsPaused, "Extend reopens an expired window");

            Gate.Resume();
        });

    static void Gate_Extend_DoesNotClearHold()
        => Scenario(nameof(Gate_Extend_DoesNotClearHold), sb =>
        {
            Gate.Resume();

            Gate.Hold("test");
            Gate.Extend(TimeSpan.Zero, "test");
            Assert(Gate.IsPaused, "Hold survives a zero Extend");

            Gate.Resume();
            Assert(!Gate.IsPaused, "Resume clears Hold");
        });

    // ── Power limit bounds ───────────────────────────────────────────────
    //
    // asus-armoury publishes min_value/max_value per PPT attribute and the
    // kernel EINVALs anything outside. The model table only knows the
    // Windows floor (5W), so a saved 20W on a 28W-minimum board (G614JU)
    // passed validation and was silently refused. Firmware bounds must win
    // when present and the table must still cover legacy-only kernels.

    const int TableMin = 5;
    const int TableMax = 175;

    static void PowerLimitBounds_NoFirmwareRange_UsesModelTable()
        => Scenario(nameof(PowerLimitBounds_NoFirmwareRange_UsesModelTable), _ =>
        {
            var b = PowerLimitBounds.Resolve(null, TableMin, TableMax);
            AssertEqual(TableMin, b.Min, "min from table");
            AssertEqual(TableMax, b.Max, "max from table");
            Assert(!b.FromFirmware, "not flagged as firmware");
        });

    static void PowerLimitBounds_FirmwareRange_WinsOverTable()
        => Scenario(nameof(PowerLimitBounds_FirmwareRange_WinsOverTable), _ =>
        {
            // G614JU ppt_pl1_spl: 28-140 while the Intel HX table says 5-175.
            var b = PowerLimitBounds.Resolve(new AttrRange(28, 140, 1, 0), TableMin, TableMax);
            AssertEqual(28, b.Min, "min from firmware");
            AssertEqual(140, b.Max, "max from firmware");
            Assert(b.FromFirmware, "flagged as firmware");
        });

    static void PowerLimitBounds_PartialFirmwareRange_FillsFromTable()
        => Scenario(nameof(PowerLimitBounds_PartialFirmwareRange_FillsFromTable), _ =>
        {
            // AsusAttributeRange reports -1 for a bound it could not read.
            var b = PowerLimitBounds.Resolve(new AttrRange(28, -1, 1, 0), TableMin, TableMax);
            AssertEqual(28, b.Min, "min from firmware");
            AssertEqual(TableMax, b.Max, "missing max filled from table");
            Assert(b.FromFirmware, "still flagged as firmware");
        });

    static void PowerLimitBounds_InvertedFirmwareRange_FallsBack()
        => Scenario(nameof(PowerLimitBounds_InvertedFirmwareRange_FallsBack), _ =>
        {
            var b = PowerLimitBounds.Resolve(new AttrRange(140, 28, 1, 0), TableMin, TableMax);
            AssertEqual(TableMin, b.Min, "garbage range ignored: min from table");
            AssertEqual(TableMax, b.Max, "garbage range ignored: max from table");
            Assert(!b.FromFirmware, "garbage range not flagged as firmware");
        });

    static void PowerLimitBounds_Sanitize_PoisonFloor_Dropped()
        => Scenario(nameof(PowerLimitBounds_Sanitize_PoisonFloor_Dropped), _ =>
        {
            // issue #151: a persisted 5W is a legacy readback, never user intent.
            var b = PowerLimitBounds.Resolve(new AttrRange(28, 140, 1, 0), TableMin, TableMax);
            AssertEqual(-1, b.Sanitize(5, TableMin), "5W dropped as poison");
            AssertEqual(-1, b.Sanitize(0, TableMin), "0W dropped as poison");
        });

    static void PowerLimitBounds_Sanitize_BelowFirmwareMin_ClampedUp()
        => Scenario(nameof(PowerLimitBounds_Sanitize_BelowFirmwareMin_ClampedUp), _ =>
        {
            var b = PowerLimitBounds.Resolve(new AttrRange(28, 140, 1, 0), TableMin, TableMax);
            AssertEqual(28, b.Sanitize(20, TableMin), "20W raised to the 28W firmware floor");
        });

    static void PowerLimitBounds_Sanitize_AboveMax_ClampedDown()
        => Scenario(nameof(PowerLimitBounds_Sanitize_AboveMax_ClampedDown), _ =>
        {
            var b = PowerLimitBounds.Resolve(new AttrRange(28, 140, 1, 0), TableMin, TableMax);
            AssertEqual(140, b.Sanitize(175, TableMin), "table max lowered to the 140W firmware ceiling");
        });

    static void PowerLimitBounds_Sanitize_InRange_Unchanged()
        => Scenario(nameof(PowerLimitBounds_Sanitize_InRange_Unchanged), _ =>
        {
            var b = PowerLimitBounds.Resolve(new AttrRange(28, 140, 1, 0), TableMin, TableMax);
            AssertEqual(28, b.Sanitize(28, TableMin), "firmware minimum itself is legal");
            AssertEqual(65, b.Sanitize(65, TableMin), "mid-range untouched");
            Assert(b.Contains(140) && !b.Contains(141), "Contains follows the bounds");
        });
}

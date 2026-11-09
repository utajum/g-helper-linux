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
using Block = GHelper.Linux.Gpu.Block;

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
        PushBackendMarker_Ebpf_WritesMarkerFile();
        Ebpf_ScheduleForReboot_NoBlockArtifacts();
        Ebpf_ScheduleForReboot_Standard_CleansUp();

        Console.WriteLine("\n eBPF GPU block ");
        GpuBlock_ParseCaps_ReadsEveryField();
        GpuBlock_ParseCaps_EmptyOutputMeansHelperFailed();
        GpuBlock_ParseCaps_IgnoresJunkLines();
        GpuBlock_ParseStatus_ReadsAttachedModeAndCount();
        GpuBlock_ParseStatus_DetachedIsNotActive();
        GpuBlock_ParseStatus_UnknownModeDoesNotThrow();
        GpuBlock_ParsePowerState_MapsEveryDState();
        GpuBlock_KernelRow_State();
        GpuBlock_LibbpfRow_State();
        GpuBlock_LibbpfInstallCommand_ByPackageManager();
        GpuBlock_HardBlock_OnlyUnfixableReasons();
        GpuBlock_Backend_ConfigRoundTrip();
        GpuBlock_Backend_UnknownValueFallsBackToWmi();
        GpuBlock_AllowedTree_MatchesSelfAndAncestors();
        HolderScan_SystemdIsNeverKilled();
        GpuBlock_RuntimeD3Off_ReadsNvidiaStatus();
        GpuBlock_AllowedTree_MatchesEnvironment();
        GpuBlock_Validate_EnvEntries();
        GpuBlock_ParseRefused_ReadsRows();
        GpuBlock_Launcher_BuildsEnv();
        GpuBlock_Constants_MatchFilterAndHelper();
        Ebpf_IsPendingReboot_AlwaysFalse();

        Console.WriteLine("\n eBPF backend switching (mirrors wmi/pci matrices) ");
        Ebpf_FromStandard_ClickEco_DriverIdle_AppliesLive();
        Ebpf_FromStandard_ClickEco_DriverActive_AttachesAnyway();
        Ebpf_FromStandard_ClickEco_AppsHold_AsksFirst();
        Ebpf_AppsHold_SwitchNow_Applies();
        Ebpf_ClickOptimized_AppsHold_AsksFirst();
        Ebpf_FromEco_ClickStandard_AppliesLive();
        Ebpf_FromEco_ClickEco_AlreadySet();
        Ebpf_FromStandard_ClickStandard_AlreadySet();
        Ebpf_MuxLiveZero_ClickEco_EcoBlocked();
        Ebpf_EnableFails_ReturnsFailed_ConfigUnchanged();
        Ebpf_DisableFails_ReturnsFailed();
        Ebpf_ClickUltimate_TreatedAsStandard();
        Ebpf_GetCurrentMode_FollowsFilterState();

        Console.WriteLine("\n eBPF Optimized (auto AC/battery) ");
        Ebpf_OptimizedCheckbox_GatesAutoSwitch();
        Ebpf_ClickOptimized_OnAc_LeavesDgpuVisible();
        Ebpf_ClickOptimized_OnBattery_BlocksLive();
        Ebpf_ClickOptimized_OnBattery_DriverActive_NoDialog();
        Ebpf_ClickOptimized_OnBattery_ExternalDisplay_StaysVisible();
        Ebpf_Auto_Battery_ExternalDisplay_DoesNotBlock();
        Ebpf_Reconcile_DisplayAppears_ReleasesCard();
        Ebpf_Reconcile_DisplayGoneOnBattery_BlocksAgain();
        Ebpf_Reconcile_NotOptimized_NoOp();
        Ebpf_ClickEco_ExternalDisplay_StaysVisible();
        Ebpf_Reconcile_Eco_DisplayGone_Blocks();
        Ebpf_Reconcile_Eco_DisplayAppears_Releases();
        Ebpf_Reconcile_EcoAfterReboot_NotAppliedLive();
        Ebpf_Optimized_GetCurrentMode_ReportsOptimized();
        Ebpf_Auto_Unplug_BlocksNonInteractive();
        Ebpf_Auto_Unplug_AppsHold_Asks();
        Ebpf_Reconcile_Optimized_AppsHold_WaitsForThem();
        Ebpf_Reconcile_Eco_AppsHold_StillBlocks();
        Ebpf_Auto_Plug_UnblocksNonInteractive();
        Ebpf_Auto_AlreadyOnCorrectLeg_NoOp();
        Ebpf_Auto_MuxZero_DoesNotBlock();
        Ebpf_Auto_WithoutAutoFlag_DoesNothing();
        Ebpf_ManualEco_ClearsAutoFlag();
        Ebpf_ScheduleForReboot_Optimized_WritesOptimizedMarker();

        Console.WriteLine("\n eBPF startup + backend toggle ");
        Ebpf_Startup_EcoWanted_FilterOff_ReattachesQuiet();
        Ebpf_Startup_Optimized_NotClobbered();
        Ebpf_Startup_SyncsConfigFromFilterState();
        Ebpf_Startup_Active_RefreshesNodes();
        Ebpf_Startup_Eco_ExternalDisplay_StaysVisible();
        Ebpf_Startup_StaleBackendMarker_UnticksEbpf();
        Ebpf_Startup_MissingBackendMarker_UnticksEbpf();
        PushBackendMarker_WriteFails_ReturnsFalse();
        Ebpf_LiveEco_StaleBackendMarker_Fixed();
        Ebpf_BackendToggle_LeavingEbpf_DetachesFilter();
        Ebpf_BackendToggle_EnteringEbpf_ExitsFirmwareEco();
        Ebpf_BackendToggle_EnteringEbpf_ExitsPciEco();
        Ebpf_EcoPersistentConfig_DoesNotClobberOptimizedMarker();
        GpuPolicy_ValidatesProcessNames();
        GpuPolicy_AllowDenyRoundTrip();
        GpuPolicy_IgnoresCommentsAndJunkInFile();
        UdevMonitor_ParsesRealMonitorLines();
        UdevMonitor_IgnoresBannerAndJunk();
        DgpuDisplay_InternalPanelsAndVirtualIgnored();
        DgpuDisplay_DetectsMonitorOnDgpuPortOnly();
        DgpuDisplay_ConnectedInternalPanelOnDgpuDoesNotCount();
        RecoveryMarker_ClearedWhenWritable();
        RecoveryMarker_AbsentIsSuccess();

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
        IsBpfBackendUsable_BpfDisabled_ReturnsFalse();
        IsBpfBackendUsable_BpfEnabled_DGpuOnBus_ReturnsTrue();
        IsBpfBackendUsable_BpfEnabled_NoSecondGpu_ReturnsFalse();
        IsActiveFast_FollowsPmStateFile();
        InvalidateGpuPresenceCache_FlushesAfterPciRescan();
        Regression_ProArt_PciEco_PanelStaysVisible();
        Regression_DGpuLessLaptop_NoPanelEvenWithPciFlag();
        Regression_LivePciTransitionCallback_InvalidatesCache();

        Console.WriteLine("\n GPU process list (which app on which GPU) ");
        GpuList_SplitNodes_ByPciAddress();
        GpuList_SplitNodes_UnknownBdf_CountsAsIgpu();
        GpuList_Classify_NvidiaNodeAlwaysDgpu();
        GpuList_Classify_DriNodesFollowSplit();
        GpuList_Classify_NoGpuNodes_NeitherFlag();
        GpuList_Merge_AllowedNotRunning_StillListed();
        GpuList_Merge_RunningFirst_ThenDgpu();
        GpuList_Merge_MarksAllowedRunningApps();
        GpuList_Merge_RefusedFirst_TitlesNamed();
        GpuApps_Catalog_ParsesDesktopEntries();
        GpuApps_Catalog_ParsesSteam();

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
        PowerLimitBounds_Sanitize_AboveTableMax_Dropped();
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

    static void IsBpfBackendUsable_BpfDisabled_ReturnsFalse()
        => Scenario(nameof(IsBpfBackendUsable_BpfDisabled_ReturnsFalse), sb =>
        {
            // Probe speaks only to the eBPF pathway: other backends must
            // not light it up even with a real dGPU on the bus.
            AppConfig.Set("gpu_backend", "asus-wmi");
            sb.WriteFakeNvidiaPciDevice();

            Assert(!LinuxAsusWmi.IsBpfBackendUsable(),
                "ebpf backend not selected → not usable regardless of hardware");
        });

    static void IsBpfBackendUsable_BpfEnabled_DGpuOnBus_ReturnsTrue()
        => Scenario(nameof(IsBpfBackendUsable_BpfEnabled_DGpuOnBus_ReturnsTrue), sb =>
        {
            // Non-ASUS machine (no dgpu_disable attribute) on the eBPF
            // backend: the panel must show. This is the Lenovo/desktop
            // case where the old probe hid every GPU button.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteFakeNvidiaPciDevice();

            Assert(LinuxAsusWmi.IsBpfBackendUsable(),
                "ebpf + dGPU on bus → panel must show without any firmware");
        });

    static void IsBpfBackendUsable_BpfEnabled_NoSecondGpu_ReturnsFalse()
        => Scenario(nameof(IsBpfBackendUsable_BpfEnabled_NoSecondGpu_ReturnsFalse), sb =>
        {
            // Imported config on an iGPU-only machine: nothing to manage,
            // panel stays hidden.
            AppConfig.Set("gpu_backend", "ebpf");
            // No fakes written - sandbox wipes everything in ctor.

            Assert(!LinuxAsusWmi.IsBpfBackendUsable(),
                "ebpf flag but no second GPU → panel hidden");
        });

    static void GpuList_SplitNodes_ByPciAddress()
        => Scenario(nameof(GpuList_SplitNodes_ByPciAddress), sb =>
        {
            var nodes = new[]
            {
                ("/dev/dri/card1", "0000:65:00.0"),
                ("/dev/dri/renderD128", "0000:65:00.0"),
                ("/dev/dri/card2", "0000:01:00.0"),
                ("/dev/dri/renderD129", "0000:01:00.0"),
            };
            var (dgpu, igpu) = Gpu.Block.GpuProcessList.SplitNodes(nodes, "0000:01:00.0");

            Assert(dgpu.Contains("/dev/dri/card2") && dgpu.Contains("/dev/dri/renderD129"),
                "nodes on the dGPU address land in the dGPU set");
            Assert(igpu.Contains("/dev/dri/card1") && igpu.Contains("/dev/dri/renderD128"),
                "nodes on any other address land in the iGPU set");
        });

    static void GpuList_SplitNodes_UnknownBdf_CountsAsIgpu()
        => Scenario(nameof(GpuList_SplitNodes_UnknownBdf_CountsAsIgpu), sb =>
        {
            // No discrete BDF known (booted without the dGPU): nothing may
            // be shown as dGPU, or the view claims the block failed.
            var nodes = new[] { ("/dev/dri/card1", "0000:65:00.0") };
            var (dgpu, igpu) = Gpu.Block.GpuProcessList.SplitNodes(nodes, null);

            Assert(dgpu.Count == 0, "no dGPU address → dGPU set empty");
            Assert(igpu.Contains("/dev/dri/card1"), "unknown nodes fall back to iGPU");
        });

    static void GpuList_Classify_NvidiaNodeAlwaysDgpu()
        => Scenario(nameof(GpuList_Classify_NvidiaNodeAlwaysDgpu), sb =>
        {
            // /dev/nvidia* is dGPU evidence even when the DRM split failed:
            // this subsystem only ever blocks NVIDIA discrete cards.
            var none = new HashSet<string>();
            var (d, i) = Gpu.Block.GpuProcessList.Classify(
                new[] { "/dev/nvidia0", "/dev/nvidiactl" }, none, none);

            Assert(d, "nvidia device node → on dGPU");
            Assert(!i, "no iGPU node held → not on iGPU");
        });

    static void GpuList_Classify_DriNodesFollowSplit()
        => Scenario(nameof(GpuList_Classify_DriNodesFollowSplit), sb =>
        {
            var dgpu = new HashSet<string> { "/dev/dri/renderD129" };
            var igpu = new HashSet<string> { "/dev/dri/card1" };
            var (d, i) = Gpu.Block.GpuProcessList.Classify(
                new[] { "/dev/dri/card1", "/dev/dri/renderD129" }, dgpu, igpu);

            Assert(d && i, "one node from each set → app is on both GPUs");
        });

    static void GpuList_Classify_NoGpuNodes_NeitherFlag()
        => Scenario(nameof(GpuList_Classify_NoGpuNodes_NeitherFlag), sb =>
        {
            var dgpu = new HashSet<string> { "/dev/dri/renderD129" };
            var igpu = new HashSet<string> { "/dev/dri/card1" };
            var (d, i) = Gpu.Block.GpuProcessList.Classify(
                new[] { "/dev/null", "/dev/urandom" }, dgpu, igpu);

            Assert(!d && !i, "unrelated device fds → not a GPU user");
        });

    static void GpuList_Merge_AllowedNotRunning_StillListed()
        => Scenario(nameof(GpuList_Merge_AllowedNotRunning_StillListed), sb =>
        {
            // An allowed app that is not running must still get a row, or
            // there would be no way to take it back off the list.
            var running = new[] { new Gpu.Block.GpuAppUse("chrome", 3, false, true) };
            var rows = Gpu.Block.GpuProcessList.MergeWithPolicy(running, new[] { "steam" });

            var steam = rows.Find(r => r.Comm == "steam");
            Assert(steam != null, "allowed app that is not running is still listed");
            Assert(steam!.Pids == 0 && steam.Allowed,
                "not-running allowed entry has no pids and stays marked allowed");
        });

    static void GpuList_Merge_RunningFirst_ThenDgpu()
        => Scenario(nameof(GpuList_Merge_RunningFirst_ThenDgpu), sb =>
        {
            // Order is what makes the window readable: live rows first, and
            // the dGPU users at the very top since those are the actionable
            // ones.
            var running = new[]
            {
                new Gpu.Block.GpuAppUse("chrome", 1, false, true),
                new Gpu.Block.GpuAppUse("blender", 1, true, false),
            };
            var rows = Gpu.Block.GpuProcessList.MergeWithPolicy(running, new[] { "steam" });

            AssertEqual("blender", rows[0].Comm, "dGPU user sorts first");
            AssertEqual("chrome", rows[1].Comm, "other running apps follow");
            AssertEqual("steam", rows[2].Comm, "not-running entries sort last");
        });

    static void GpuList_Merge_MarksAllowedRunningApps()
        => Scenario(nameof(GpuList_Merge_MarksAllowedRunningApps), sb =>
        {
            // A running app that is on the list must come back marked, so the
            // row offers Remove rather than Allow.
            var running = new[] { new Gpu.Block.GpuAppUse("blender", 1, true, false) };
            var rows = Gpu.Block.GpuProcessList.MergeWithPolicy(running, new[] { "blender" });

            AssertEqual(1, rows.Count, "an allowed app that is running is one row, not two");
            Assert(rows[0].Allowed, "running app present on the list comes back allowed");
        });

    static void GpuList_Merge_RefusedFirst_TitlesNamed()
        => Scenario(nameof(GpuList_Merge_RefusedFirst_TitlesNamed), sb =>
        {
            var running = new[] { new Gpu.Block.GpuAppUse("blender", 1, true, false) };
            var titles = new Dictionary<string, string> { ["SteamAppId=570"] = "Dota 2" };
            var rows = Gpu.Block.GpuProcessList.MergeWithPolicy(
                running, new[] { "SteamAppId=570", "steam" }, new[] { "game", "steam" }, titles);

            AssertEqual("game", rows[0].Comm, "refused and not allowed sorts first");
            Assert(rows[0].Refused && rows[0].Pids == 0, "refused row that is not running");
            var dota = rows.Find(r => r.Comm == "SteamAppId=570");
            AssertEqual("Dota 2", dota?.Title ?? "", "list entry named by its title");
            AssertEqual(1, rows.FindAll(r => r.Comm == "steam").Count, "refused name on the list is one row");
        });

    static void GpuApps_Catalog_ParsesDesktopEntries()
        => Scenario(nameof(GpuApps_Catalog_ParsesDesktopEntries), sb =>
        {
            Gpu.Block.CatalogApp? Parse(params string[] lines) => Gpu.Block.GpuAppCatalog.ParseDesktop(lines);

            var blender = Parse("[Desktop Entry]", "Type=Application", "Name=Blender", "Exec=blender %f");
            AssertEqual("blender", blender?.Entry ?? "", "program name from Exec");
            var envd = Parse("[Desktop Entry]", "Type=Application", "Name=App", "Exec=env FOO=1 \"/opt/My App/run\" --x");
            AssertEqual("run", envd?.Entry ?? "", "env prefix and quoted path");
            var game = Parse("[Desktop Entry]", "Type=Application", "Name=Dota 2", "Exec=steam steam://rungameid/570");
            AssertEqual("SteamAppId=570", game?.Entry ?? "", "steam shortcut maps to the app id");
            Assert(Parse("[Desktop Entry]", "Type=Application", "Name=F", "Exec=flatpak run org.x.Y") == null, "flatpak cannot be named");
            Assert(Parse("[Desktop Entry]", "Type=Application", "Name=H", "NoDisplay=true", "Exec=h") == null, "hidden entry skipped");
            Assert(Parse("[Desktop Action new]", "Name=X", "Exec=x") == null, "only the main group counts");
            AssertEqual("averyveryverylo", Gpu.Block.GpuAppCatalog.CommFromExec("/usr/bin/averyveryverylongname") ?? "",
                "cut to the kernel's 15 characters");
        });

    static void GpuApps_Catalog_ParsesSteam()
        => Scenario(nameof(GpuApps_Catalog_ParsesSteam), sb =>
        {
            var game = Gpu.Block.GpuAppCatalog.ParseSteamManifest("\"AppState\"\n{\n\t\"appid\"\t\t\"570\"\n\t\"name\"\t\t\"Dota 2\"\n}");
            AssertEqual("Dota 2", game?.Title ?? "", "title");
            AssertEqual("SteamAppId=570", game?.Entry ?? "", "entry");
            Assert(Gpu.Block.GpuAppCatalog.ParseSteamManifest("\"appid\" \"1493710\"\n\"name\" \"Proton Experimental\"") == null,
                "Proton is a tool, not a game");
            var libs = Gpu.Block.GpuAppCatalog.ParseLibraryFolders("\"0\"\n{\n\"path\"\t\"/home/u/.local/share/Steam\"\n}\n\"1\"\n{\n\"path\"\t\"/mnt/games\"\n}");
            AssertEqual(2, libs.Count, "both libraries");
            AssertEqual("/mnt/games", libs[1], "second path");
        });

    static void IsActiveFast_FollowsPmStateFile()
        => Scenario(nameof(IsActiveFast_FollowsPmStateFile), sb =>
        {
            // The 2 s indicator tick must not spawn a privileged helper;
            // it reads the pm state file the helper creates after the
            // filter engages and removes on every off.
            var savedOps = Gpu.Block.GpuBlockControl.TestOps;
            Gpu.Block.GpuBlockControl.TestOps = null;
            try
            {
                string dir = Path.Combine(sb.TempRoot, "run", "ghelper");
                string file = Path.Combine(dir, "bpf-block-pm.state");
                Directory.CreateDirectory(dir);
                File.Delete(file);

                Assert(!Gpu.Block.GpuBlockControl.IsActiveFast(),
                    "no state file → not blocked");

                File.WriteAllText(file, "");
                Assert(Gpu.Block.GpuBlockControl.IsActiveFast(),
                    "state file present → blocked (empty file counts, boot case)");

                File.Delete(file);
                Assert(!Gpu.Block.GpuBlockControl.IsActiveFast(),
                    "state file removed → not blocked again");
            }
            finally
            {
                Gpu.Block.GpuBlockControl.TestOps = savedOps;
            }
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

    static void PowerLimitBounds_Sanitize_AboveTableMax_Dropped()
        => Scenario(nameof(PowerLimitBounds_Sanitize_AboveTableMax_Dropped), _ =>
        {
            // No firmware range: the ceiling is the model table's guess, so an
            // over-max value is dropped as master did rather than clamped to a
            // number the app made up.
            var b = PowerLimitBounds.Resolve(null, TableMin, TableMax);
            Assert(!b.FromFirmware, "table-only bounds");
            AssertEqual(-1, b.Sanitize(TableMax + 1, TableMin), "above table max dropped, not clamped");
            AssertEqual(TableMax, b.Sanitize(TableMax, TableMin), "at the table ceiling still applied");
            AssertEqual(65, b.Sanitize(65, TableMin), "in-range untouched");
        });

    //
    // eBPF GPU block: parsers and backend plumbing.
    //
    // The filter itself needs a kernel booted with bpf in the LSM list, which no
    // CI runner has, so these cover the parts that are pure logic: helper output
    // parsing, exit-code mapping, and the backend marker.
    //

    static void GpuBlock_AllowedTree_MatchesSelfAndAncestors()
        => Scenario(nameof(GpuBlock_AllowedTree_MatchesSelfAndAncestors), sb =>
        {
            int self = Environment.ProcessId;
            string comm = File.ReadAllText($"/proc/{self}/comm").Trim();
            string stat = File.ReadAllText($"/proc/{self}/stat");
            int ppid = int.Parse(stat.Substring(stat.LastIndexOf(')') + 2).Split(' ')[1]);
            string parent = File.ReadAllText($"/proc/{ppid}/comm").Trim();

            Assert(Block.GpuBlockPolicy.IsAllowedTree(self, new[] { comm }), "own name allows");
            Assert(Block.GpuBlockPolicy.IsAllowedTree(self, new[] { parent }), "parent name allows the child");
            Assert(!Block.GpuBlockPolicy.IsAllowedTree(self, new[] { "no-such-app" }), "unrelated name does not");
            Assert(!Block.GpuBlockPolicy.IsAllowedTree(int.MaxValue, new[] { comm }), "missing pid is not allowed");
        });

    static void HolderScan_SystemdIsNeverKilled()
        => Scenario(nameof(HolderScan_SystemdIsNeverKilled), sb =>
        {
            // KWin 6.8 keeps the dGPU open through logind, which stores a copy in PID 1.
            Assert(Gpu.NVidia.NvidiaProcessScanner.IsSystemProcess("systemd"), "PID 1 and the user manager are system");
            Assert(Gpu.NVidia.NvidiaProcessScanner.IsSystemProcess("systemd-logind"), "logind is system");
            Assert(!Gpu.NVidia.NvidiaProcessScanner.IsSystemProcess("slack"), "apps are not system");
        });

    static void GpuBlock_RuntimeD3Off_ReadsNvidiaStatus()
        => Scenario(nameof(GpuBlock_RuntimeD3Off_ReadsNvidiaStatus), sb =>
        {
            Assert(Block.DgpuPowerMonitor.RuntimeD3Off("Runtime D3 status:          Disabled by default\nVideo Memory:               Active"), "pre-Turing default is off");
            Assert(!Block.DgpuPowerMonitor.RuntimeD3Off("Runtime D3 status:          Enabled (fine-grained)"), "enabled is on");
            Assert(!Block.DgpuPowerMonitor.RuntimeD3Off(null), "no NVIDIA driver, no verdict");
        });

    static void GpuBlock_AllowedTree_MatchesEnvironment()
        => Scenario(nameof(GpuBlock_AllowedTree_MatchesEnvironment), sb =>
        {
            // The filter allows a process by an environment entry at exec;
            // the close prompt must not offer to close such a process.
            var psi = new System.Diagnostics.ProcessStartInfo("sleep", "5");
            psi.Environment["GHELPER_DGPU"] = "1";
            using var allowed = System.Diagnostics.Process.Start(psi)!;
            using var plain = System.Diagnostics.Process.Start("sleep", "5");
            try
            {
                var entries = Block.GpuBlockPolicy.EnvEntries(null);
                Assert(Block.GpuBlockPolicy.IsAllowedTree(allowed.Id, entries), "marker in environment allows");
                Assert(!Block.GpuBlockPolicy.IsAllowedTree(plain.Id, entries), "no marker, no match");
            }
            finally
            {
                allowed.Kill();
                plain.Kill();
            }
        });

    static void GpuBlock_Validate_EnvEntries()
        => Scenario(nameof(GpuBlock_Validate_EnvEntries), sb =>
        {
            Assert(Block.GpuBlockPolicy.Validate("SteamAppId=570") == null, "steam entry is valid");
            Assert(Block.GpuBlockPolicy.Validate("DRI_PRIME=pci-0000_01_00_0") == null, "DRI_PRIME form is valid");
            Assert(Block.GpuBlockPolicy.Validate("=1") != null, "empty key is rejected");
            Assert(Block.GpuBlockPolicy.Validate("A=") != null, "empty value is rejected");
            Assert(Block.GpuBlockPolicy.Validate("A B=1") != null, "space is rejected");
            Assert(Block.GpuBlockPolicy.Validate("A=" + new string('x', 46)) != null, "too long for the key");
            Assert(Block.GpuBlockPolicy.Validate("SteamAppIdIsLong=1") == null, "long env key is not a comm");
        });

    static void GpuBlock_ParseRefused_ReadsRows()
        => Scenario(nameof(GpuBlock_ParseRefused_ReadsRows), sb =>
        {
            var rows = Block.GpuBlockControl.ParseRefused("4\t9\tprobe\n12\t1\tWeb Content\njunk\n");
            AssertEqual(2, rows.Count, "junk line skipped");
            AssertEqual("probe", rows[0].Comm, "name");
            AssertEqual(9, rows[0].Count, "count");
            AssertEqual("Web Content", rows[1].Comm, "names may hold spaces");
            AssertEqual(12L, rows[1].AgeSeconds, "age");
            AssertEqual(0, Block.GpuBlockControl.ParseRefused(null).Count, "no output, no rows");
        });

    static void GpuBlock_Launcher_BuildsEnv()
        => Scenario(nameof(GpuBlock_Launcher_BuildsEnv), sb =>
        {
            var current = new System.Collections.Hashtable { ["PATH"] = "/usr/bin", ["DRI_PRIME"] = "0" };
            var nv = Cli.DgpuLauncher.BuildEnv(current, "0x10de", false);
            AssertEqual("1", nv["__NV_PRIME_RENDER_OFFLOAD"], "nvidia offload");
            AssertEqual("1", nv["GHELPER_DGPU"], "block marker");
            AssertEqual("/usr/bin", nv["PATH"], "environment kept");
            var amd = Cli.DgpuLauncher.BuildEnv(current, "0x1002", true);
            AssertEqual("1", amd["DRI_PRIME"], "mesa offload overrides 0");
            Assert(amd.ContainsKey("GHELPER_FORCE_DGPU") && !amd.ContainsKey("GHELPER_DGPU"), "--only uses the forced marker");
        });

    static void GpuBlock_Constants_MatchFilterAndHelper()
        => Scenario(nameof(GpuBlock_Constants_MatchFilterAndHelper), sb =>
        {
            // The filter, gpu-helper and the app each carry these; drift turns lookups into silent misses.
            string dir = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(dir, "vendor/gpu-helper/bpf_ops.c")))
                dir = Path.GetDirectoryName(dir) ?? throw new Exception("repo root not found");
            string helper = File.ReadAllText(Path.Combine(dir, "vendor/gpu-helper/bpf_ops.c"));
            string filter = File.ReadAllText(Path.Combine(dir, "vendor/gpu-helper/bpf/ghelper_gpublock.bpf.c"));
            int Define(string src, string name)
                => int.Parse(System.Text.RegularExpressions.Regex.Match(src, $@"#define {name} (\d+)").Groups[1].Value);

            foreach (var name in new[] { "GH_COMM_LEN", "GH_COMM_USER", "GH_ENV_LEN", "GH_NAME_LEN",
                                         "GH_INO_DGPU", "GH_INO_IGPU", "GH_PID_ALLOW", "GH_PID_FORCE" })
                AssertEqual(Define(filter, name), Define(helper, name), name);
            AssertEqual(Define(helper, "GH_ENV_LEN"), Block.GpuBlockPolicy.MaxEnvLength + 1, "env length");
            AssertEqual(Define(helper, "GH_COMM_LEN"), Block.GpuBlockPolicy.MaxNameLength + 1, "name length");
            AssertEqual(Define(helper, "USER_COMMS_MAX"), Block.GpuBlockPolicy.MaxEntries, "list size");

            var envs = new List<string>();
            foreach (var array in new[] { "ALLOWED_ENVS", "FORCED_ENVS" })
            {
                var body = System.Text.RegularExpressions.Regex.Match(helper, array + @"\[\] = \{(.*?)\};",
                    System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, "\"([^\"]+)\""))
                    envs.Add(m.Groups[1].Value);
            }
            AssertEqual(string.Join(",", Block.GpuBlockPolicy.BuiltinEnvs), string.Join(",", envs), "built-in environment entries");
        });

    static void GpuBlock_ParseCaps_ReadsEveryField()
        => Scenario(nameof(GpuBlock_ParseCaps_ReadsEveryField), sb =>
        {
            var caps = Block.GpuBlockCapability.ParseCaps(
                "lsm_active=1\nkernel_config=y\nbpffs=1\nlibbpf=1\n" +
                "filter=1\nbtf=1\nlockdown=none\nattached=0\n");

            Assert(caps.HelperRan, "helper output recognised");
            Assert(caps.LsmActive, "lsm_active parsed");
            AssertEqual("y", caps.KernelConfig, "kernel_config parsed");
            Assert(caps.Bpffs, "bpffs parsed");
            Assert(caps.Libbpf, "libbpf parsed");
            Assert(caps.Filter, "filter parsed");
            Assert(caps.Btf, "btf parsed");
            AssertEqual("none", caps.Lockdown, "lockdown parsed");
            Assert(!caps.Attached, "attached parsed");
        });

    static void GpuBlock_ParseCaps_EmptyOutputMeansHelperFailed()
        => Scenario(nameof(GpuBlock_ParseCaps_EmptyOutputMeansHelperFailed), sb =>
        {
            var caps = Block.GpuBlockCapability.ParseCaps(null);
            Assert(!caps.HelperRan, "null output means the helper did not run");
            Assert(!caps.LsmActive, "no capability is assumed present");
            AssertEqual("unknown", caps.KernelConfig, "kernel config unknown");
        });

    static void GpuBlock_ParseCaps_IgnoresJunkLines()
        => Scenario(nameof(GpuBlock_ParseCaps_IgnoresJunkLines), sb =>
        {
            var caps = Block.GpuBlockCapability.ParseCaps(
                "warning: something\nlsm_active=1\n=novalue\nbpffs=1\n");
            Assert(caps.LsmActive, "valid line still parsed after junk");
            Assert(caps.Bpffs, "valid line parsed after a malformed one");
        });

    static void GpuBlock_ParseStatus_ReadsAttachedModeAndCount()
        => Scenario(nameof(GpuBlock_ParseStatus_ReadsAttachedModeAndCount), sb =>
        {
            var st = Block.GpuBlockControl.ParseStatus("attached=1 mode=1 blocked=12");
            Assert(st.Attached, "attached parsed");
            AssertEqual(1, st.Mode, "mode parsed");
            AssertEqual(12, st.BlockedFiles, "blocked count parsed");
        });

    static void GpuBlock_ParseStatus_DetachedIsNotActive()
        => Scenario(nameof(GpuBlock_ParseStatus_DetachedIsNotActive), sb =>
        {
            var st = Block.GpuBlockControl.ParseStatus("attached=0 mode=0 blocked=0");
            Assert(!st.Attached, "detached parsed");
            AssertEqual(0, st.Mode, "mode zero parsed");
        });

    static void GpuBlock_ParseStatus_UnknownModeDoesNotThrow()
        => Scenario(nameof(GpuBlock_ParseStatus_UnknownModeDoesNotThrow), sb =>
        {
            // The helper prints mode=unknown when libbpf is missing but pins exist.
            var st = Block.GpuBlockControl.ParseStatus("attached=1 mode=unknown blocked=unknown");
            Assert(st.Attached, "attached still parsed");
            AssertEqual(0, st.Mode, "unparseable mode falls back to zero, not active");
        });

    static void GpuBlock_ParsePowerState_MapsEveryDState()
        => Scenario(nameof(GpuBlock_ParsePowerState_MapsEveryDState), sb =>
        {
            var m = Block.DgpuPowerMonitor.Parse;
            AssertEqual(Block.DgpuPowerMonitor.PowerState.D0, m("D0"), "D0");
            AssertEqual(Block.DgpuPowerMonitor.PowerState.D1, m("D1"), "D1");
            AssertEqual(Block.DgpuPowerMonitor.PowerState.D2, m("D2"), "D2");
            // sysfs spells the deep states lowercase, unlike D0..D2.
            AssertEqual(Block.DgpuPowerMonitor.PowerState.D3Hot, m("D3hot"), "D3hot");
            AssertEqual(Block.DgpuPowerMonitor.PowerState.D3Cold, m("D3cold"), "D3cold");
            AssertEqual(Block.DgpuPowerMonitor.PowerState.D3Cold, m(" D3cold\n"), "whitespace trimmed");
            AssertEqual(Block.DgpuPowerMonitor.PowerState.Unknown, m("nonsense"), "unrecognised");
            AssertEqual(Block.DgpuPowerMonitor.PowerState.Unknown, m(null), "null");
        });

    static void GpuBlock_KernelRow_State()
        => Scenario(nameof(GpuBlock_KernelRow_State), sb =>
        {
            var ok = Block.GpuBlockSetup.RowState.Ok;
            bool asked = false;
            bool Can()
            {
                asked = true;
                return true;
            }
            AssertEqual(Block.GpuBlockSetup.RowState.NotApplicable,
                Block.GpuBlockSetup.KernelRowState(false, true, false, Can), "off, not ours: not applicable");
            AssertEqual(ok, Block.GpuBlockSetup.KernelRowState(false, false, true, Can), "off, ours: listed to remove");
            AssertEqual(ok, Block.GpuBlockSetup.KernelRowState(true, true, false, Can), "on, active");
            AssertEqual(ok, Block.GpuBlockSetup.KernelRowState(true, false, true, Can), "on, set for next boot");
            Assert(!asked, "helper not asked when the answer is known");
            AssertEqual(Block.GpuBlockSetup.RowState.Missing,
                Block.GpuBlockSetup.KernelRowState(true, false, false, Can), "on, missing");
            AssertEqual(Block.GpuBlockSetup.RowState.NotApplicable,
                Block.GpuBlockSetup.KernelRowState(true, false, false, () => false), "kernel without CONFIG_BPF_LSM");
        });

    static void GpuBlock_LibbpfRow_State()
        => Scenario(nameof(GpuBlock_LibbpfRow_State), sb =>
        {
            AssertEqual(Block.GpuBlockSetup.RowState.NotApplicable, Block.GpuBlockSetup.LibbpfRowState(false, false), "off");
            AssertEqual(Block.GpuBlockSetup.RowState.Ok, Block.GpuBlockSetup.LibbpfRowState(true, true), "loads");
            AssertEqual(Block.GpuBlockSetup.RowState.Missing, Block.GpuBlockSetup.LibbpfRowState(true, false), "missing");
            AssertEqual(Block.GpuBlockSetup.RowState.Unknown, Block.GpuBlockSetup.LibbpfRowState(true, null), "helper not run");
        });

    static void GpuBlock_LibbpfInstallCommand_ByPackageManager()
        => Scenario(nameof(GpuBlock_LibbpfInstallCommand_ByPackageManager), sb =>
        {
            string? Cmd(params string[] present) => Block.GpuBlockSetup.LibbpfInstallCommand(p => Array.IndexOf(present, p) >= 0);
            AssertEqual("sudo apt install libbpf1", Cmd("/usr/bin/apt") ?? "", "apt");
            AssertEqual("sudo dnf install libbpf", Cmd("/usr/bin/dnf") ?? "", "dnf");
            AssertEqual("sudo rpm-ostree install libbpf", Cmd("/run/ostree-booted", "/usr/bin/dnf") ?? "", "atomic before dnf");
            AssertEqual("sudo pacman -S libbpf", Cmd("/usr/bin/pacman") ?? "", "pacman");
            AssertEqual("sudo zypper install libbpf1", Cmd("/usr/bin/zypper") ?? "", "zypper");
            Assert(Cmd() == null, "unknown package manager");
        });

    static void GpuBlock_HardBlock_OnlyUnfixableReasons()
        => Scenario(nameof(GpuBlock_HardBlock_OnlyUnfixableReasons), sb =>
        {
            // The checkbox stays usable for anything the Updates rows add.
            foreach (var key in new[] { "gpu_block_unavail_lsm", "gpu_block_unavail_libbpf", "gpu_block_unavail_helper", "gpu_block_unavail_reboot" })
                Assert(!Block.GpuBlockCapability.IsHardBlock(key), key);
            foreach (var key in new[] { "gpu_block_unavail_kernel", "gpu_block_unavail_filter", "gpu_block_unavail_lockdown" })
                Assert(Block.GpuBlockCapability.IsHardBlock(key), key);
        });

    static void GpuBlock_Backend_ConfigRoundTrip()
        => Scenario(nameof(GpuBlock_Backend_ConfigRoundTrip), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AssertEqual("ebpf", AppConfig.GetGpuBackend(), "ebpf backend accepted");
            Assert(AppConfig.IsBpfGpuBackend(), "IsBpfGpuBackend true");
            Assert(!AppConfig.IsPciGpuBackend(), "ebpf is not the PCI backend");

            AppConfig.Set("gpu_backend", "asus-wmi");
            Assert(!AppConfig.IsBpfGpuBackend(), "switching away clears it");
        });

    static void GpuBlock_Backend_UnknownValueFallsBackToWmi()
        => Scenario(nameof(GpuBlock_Backend_UnknownValueFallsBackToWmi), sb =>
        {
            AppConfig.Set("gpu_backend", "ebfp");
            AssertEqual("asus-wmi", AppConfig.GetGpuBackend(), "typo falls back to the safe default");
            Assert(!AppConfig.IsBpfGpuBackend(), "typo does not select the filter");
        });

    static void PushBackendMarker_Ebpf_WritesMarkerFile()
        => Scenario(nameof(PushBackendMarker_Ebpf_WritesMarkerFile), sb =>
        {
            sb.WriteBackend("asus-wmi");
            sb.Controller.PushBackendMarker("ebpf");
            AssertEqual("ebpf", sb.BackendContent(), "ebpf marker written for the boot service");
        });

    static void Ebpf_IsPendingReboot_AlwaysFalse()
        => Scenario(nameof(Ebpf_IsPendingReboot_AlwaysFalse), sb =>
        {
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            // Both directions apply live, so nothing is ever waiting on a reboot
            // even with a trigger file left behind by a previous backend.
            sb.WriteTrigger("eco");
            Assert(!sb.Controller.IsPendingReboot(), "ebpf never reports a pending reboot");
        });

    static void Ebpf_ScheduleForReboot_NoBlockArtifacts()
        => Scenario(nameof(Ebpf_ScheduleForReboot_NoBlockArtifacts), sb =>
        {
            // Regression: the dialog's After Reboot button used to lay down the
            // modprobe block and the udev remove rule for the ebpf backend too.
            // The udev rule removed the dGPU during coldplug, so the boot
            // service found no device to attach the filter to and the box came
            // up with the dGPU gone instead of hidden.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.ScheduleModeForReboot(GpuMode.Eco);
            AssertEqual(GpuSwitchResult.RebootRequired, result, "deferred ebpf eco is a reboot-required result");
            AssertEqual("eco", sb.TriggerContent(), "trigger written for the boot service");
            AssertEqual("ebpf", sb.BackendContent(), "backend marker says ebpf");
            Assert(!sb.ModprobePresent(), "no modprobe block for ebpf");
            Assert(!sb.UdevPresent(), "no udev remove rule for ebpf");
            Assert(File.Exists(GPUModeControl.PersistentTriggerPath),
                "persistent marker written so eco survives later reboots");
        });

    static void Ebpf_ScheduleForReboot_Standard_CleansUp()
        => Scenario(nameof(Ebpf_ScheduleForReboot_Standard_CleansUp), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            // Leftovers from a previous asus-wmi/pci session.
            sb.WriteBlockArtifacts();
            sb.WriteTrigger("eco");
            File.WriteAllText(GPUModeControl.PersistentTriggerPath, "eco");

            var result = sb.Controller.ScheduleModeForReboot(GpuMode.Standard);
            AssertEqual(GpuSwitchResult.RebootRequired, result, "deferred ebpf standard reported");
            Assert(!sb.ModprobePresent(), "stale modprobe block removed");
            Assert(!sb.UdevPresent(), "stale udev rule removed");
            Assert(!File.Exists(GPUModeControl.PersistentTriggerPath),
                "persistent marker removed when leaving eco");
        });

    //
    // eBPF backend switching - mirrors the asus-wmi / PCI matrices above.
    // Every branch the other two backends have a test for has one here, so a
    // UI click or a boot-service path cannot regress unnoticed.
    //

    static void Ebpf_FromStandard_ClickEco_DriverIdle_AppliesLive()
        => Scenario(nameof(Ebpf_FromStandard_ClickEco_DriverIdle_AppliesLive), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.BlockOps.ActiveState, "filter attached");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config saved");
            AssertEqual("eco", sb.PersistentContent(), "persistent marker says eco");
            Assert(!sb.Wmi.EcoEnabled, "firmware dgpu_disable untouched");
            Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "no block artifacts");
            Assert(!sb.TriggerPresent(), "no reboot trigger for a live switch");
        });

    static void Ebpf_FromStandard_ClickEco_DriverActive_AttachesAnyway()
        => Scenario(nameof(Ebpf_FromStandard_ClickEco_DriverActive_AttachesAnyway), sb =>
        {
            // The driver stays loaded, so a busy driver alone is no reason to
            // ask: only apps holding the card are. None here, so no dialog.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1; sb.Wmi.EcoEnabled = false;
            sb.WriteFakeNvidiaModule();

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.Applied, result, "applied without a dialog");
            Assert(sb.BlockOps.ActiveState, "filter attached");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config saved");
            AssertEqual("eco", sb.PersistentContent(), "marker written");
            Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "no block artifacts");
        });

    static void Ebpf_FromStandard_ClickEco_AppsHold_AsksFirst()
        => Scenario(nameof(Ebpf_FromStandard_ClickEco_AppsHold_AsksFirst), sb =>
        {
            // Apps holding the card keep it awake, so ask with the same dialog as the other backends.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "standard");
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.HolderList.Add(new Gpu.NVidia.NvidiaHolder(4242, "blender", "user", 2, 0, true));

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.DriverBlocking, result, "dialog first");
            Assert(!sb.BlockOps.ActiveState, "nothing hidden yet");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "Cancel leaves the mode as it was");
        });

    static void Ebpf_AppsHold_SwitchNow_Applies()
        => Scenario(nameof(Ebpf_AppsHold_SwitchNow_Applies), sb =>
        {
            // Switch Now comes back through TryReleaseAndSwitch and must not ask again.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.HolderList.Add(new Gpu.NVidia.NvidiaHolder(4242, "blender", "user", 2, 0, true));

            AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.RequestModeSwitch(GpuMode.Eco), "asked");
            AssertEqual(GpuSwitchResult.Applied, sb.Controller.TryReleaseAndSwitch(), "applied after the dialog");
            Assert(sb.BlockOps.ActiveState, "filter attached");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config saved");
        });

    static void Ebpf_ClickOptimized_AppsHold_AsksFirst()
        => Scenario(nameof(Ebpf_ClickOptimized_AppsHold_AsksFirst), sb =>
        {
            // Like Windows G-Helper: the battery leg asks to close the apps.
            // Optimized is saved first, so Cancel keeps it selected.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.HolderList.Add(new Gpu.NVidia.NvidiaHolder(4242, "blender", "user", 2, 0, true));

            AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.RequestModeSwitch(GpuMode.Optimized), "asked");
            Assert(!sb.BlockOps.ActiveState, "nothing hidden yet");
            Assert(AppConfig.Is("gpu_auto"), "Optimized stays selected on Cancel");
            AssertEqual("optimized", AppConfig.GetString("gpu_mode") ?? "", "mode saved");
            AssertEqual("optimized", sb.PersistentContent(), "marker says optimized");

            AssertEqual(GpuSwitchResult.Applied, sb.Controller.TryReleaseAndSwitch(GpuMode.Optimized), "Switch Now");
            Assert(sb.BlockOps.ActiveState, "filter attached");
            Assert(AppConfig.Is("gpu_auto"), "still Optimized, not Eco");
        });

    static void Ebpf_FromEco_ClickStandard_AppliesLive()
        => Scenario(nameof(Ebpf_FromEco_ClickStandard_AppliesLive), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.BlockOps.ActiveState = true;
            File.WriteAllText(sb.PersistentPath, "eco");

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(!sb.BlockOps.ActiveState, "filter detached");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config saved");
            Assert(!sb.PersistentPresent(), "persistent marker removed");
        });

    static void Ebpf_FromEco_ClickEco_AlreadySet()
        => Scenario(nameof(Ebpf_FromEco_ClickEco_AlreadySet), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.BlockOps.ActiveState = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
            AssertEqual(0, sb.BlockOps.Calls.Count, "no redundant helper call");
            AssertEqual("eco", sb.PersistentContent(), "marker still eco");
        });

    static void Ebpf_FromStandard_ClickStandard_AlreadySet()
        => Scenario(nameof(Ebpf_FromStandard_ClickStandard_AlreadySet), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.BlockOps.ActiveState = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.AlreadySet, result, "result");
            AssertEqual(0, sb.BlockOps.Calls.Count, "no redundant helper call");
            Assert(!sb.PersistentPresent(), "no marker for standard");
        });

    static void Ebpf_MuxLiveZero_ClickEco_EcoBlocked()
        => Scenario(nameof(Ebpf_MuxLiveZero_ClickEco_EcoBlocked), sb =>
        {
            // MUX=0 means the dGPU drives the panel: hiding its nodes blanks it.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 0; sb.Wmi.EcoEnabled = false;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.EcoBlocked, result, "Eco refused with MUX=0");
            Assert(!sb.BlockOps.ActiveState, "filter not attached");
        });

    static void Ebpf_EnableFails_ReturnsFailed_ConfigUnchanged()
        => Scenario(nameof(Ebpf_EnableFails_ReturnsFailed_ConfigUnchanged), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "standard");
            sb.BlockOps.FailEnable = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Eco);

            AssertEqual(GpuSwitchResult.Failed, result, "result");
            Assert(!sb.BlockOps.ActiveState, "filter still off");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "config not moved to eco");
        });

    static void Ebpf_DisableFails_ReturnsFailed()
        => Scenario(nameof(Ebpf_DisableFails_ReturnsFailed), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.BlockOps.ActiveState = true;
            sb.BlockOps.FailDisable = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Standard);

            AssertEqual(GpuSwitchResult.Failed, result, "result");
            Assert(sb.BlockOps.ActiveState, "filter still attached");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "config unchanged");
        });

    static void Ebpf_ClickUltimate_TreatedAsStandard()
        => Scenario(nameof(Ebpf_ClickUltimate_TreatedAsStandard), sb =>
        {
            // Ultimate is a MUX write and this backend never touches the MUX.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.ActiveState = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Ultimate);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(!sb.BlockOps.ActiveState, "filter detached");
            AssertEqual(1, sb.Wmi.MuxMode, "MUX untouched");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "saved as standard");
        });

    static void Ebpf_ClickOptimized_OnAc_LeavesDgpuVisible()
        => Scenario(nameof(Ebpf_ClickOptimized_OnAc_LeavesDgpuVisible), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Power.OnAc = true;
            sb.BlockOps.ActiveState = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Optimized);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(!sb.BlockOps.ActiveState, "AC leg leaves the dGPU visible");
            AssertEqual("optimized", AppConfig.GetString("gpu_mode") ?? "", "config saved");
            Assert(AppConfig.Is("gpu_auto"), "auto flag set");
            AssertEqual("optimized", sb.PersistentContent(), "marker says optimized");
        });

    static void Ebpf_ClickOptimized_OnBattery_BlocksLive()
        => Scenario(nameof(Ebpf_ClickOptimized_OnBattery_BlocksLive), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Optimized);

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.BlockOps.ActiveState, "battery leg blocks");
            AssertEqual("optimized", AppConfig.GetString("gpu_mode") ?? "", "config saved");
            AssertEqual("optimized", sb.PersistentContent(), "marker says optimized");
            Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "no block artifacts");
        });

    static void Ebpf_ClickOptimized_OnBattery_DriverActive_NoDialog()
        => Scenario(nameof(Ebpf_ClickOptimized_OnBattery_DriverActive_NoDialog), sb =>
        {
            // A loaded driver alone does not prompt; only apps to close do.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.WriteFakeNvidiaModule();

            var result = sb.Controller.RequestModeSwitch(GpuMode.Optimized);

            AssertEqual(GpuSwitchResult.Applied, result, "applied without a dialog");
            Assert(sb.BlockOps.ActiveState, "filter attached anyway");
        });

    static void Ebpf_ClickOptimized_OnBattery_ExternalDisplay_StaysVisible()
        => Scenario(nameof(Ebpf_ClickOptimized_OnBattery_ExternalDisplay_StaysVisible), sb =>
        {
            // Hiding the dGPU's nodes blanks a monitor plugged into its port.
            // Optimized is automatic, so it must never do that.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.ExternalDisplay = true;

            var result = sb.Controller.RequestModeSwitch(GpuMode.Optimized);

            Assert(!sb.BlockOps.ActiveState, "dGPU stays visible for the monitor");
            AssertEqual("optimized", AppConfig.GetString("gpu_mode") ?? "", "mode still saved");
            Assert(result == GpuSwitchResult.Applied || result == GpuSwitchResult.AlreadySet,
                "reported as a success, not a failure");
        });

    static void Ebpf_Auto_Battery_ExternalDisplay_DoesNotBlock()
        => Scenario(nameof(Ebpf_Auto_Battery_ExternalDisplay_DoesNotBlock), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.ExternalDisplay = true;

            AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "no switch");
            Assert(!sb.BlockOps.ActiveState, "unplugging the charger must not kill the monitor");
        });

    static void Ebpf_Reconcile_DisplayAppears_ReleasesCard()
        => Scenario(nameof(Ebpf_Reconcile_DisplayAppears_ReleasesCard), sb =>
        {
            // Blocked on battery, then a monitor is plugged into a dGPU port.
            // The reconcile tick has to release the card or it stays dark.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.BlockOps.ActiveState = true;
            sb.ExternalDisplay = true;

            sb.Controller.ReconcileBpfAuto();

            Assert(!sb.BlockOps.ActiveState, "card released for the monitor");
        });

    static void Ebpf_Reconcile_DisplayGoneOnBattery_BlocksAgain()
        => Scenario(nameof(Ebpf_Reconcile_DisplayGoneOnBattery_BlocksAgain), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.ActiveState = false;
            sb.ExternalDisplay = false;

            sb.Controller.ReconcileBpfAuto();

            Assert(sb.BlockOps.ActiveState, "monitor unplugged on battery blocks again");
        });

    static void Ebpf_Reconcile_NotOptimized_NoOp()
        => Scenario(nameof(Ebpf_Reconcile_NotOptimized_NoOp), sb =>
        {
            // Manual Eco must not be undone by the reconcile tick.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 0);
            sb.Power.OnAc = true;
            sb.BlockOps.ActiveState = true;

            sb.Controller.ReconcileBpfAuto();

            Assert(sb.BlockOps.ActiveState, "manual Eco left alone on AC");
        });

    static void Ebpf_ClickEco_ExternalDisplay_StaysVisible()
        => Scenario(nameof(Ebpf_ClickEco_ExternalDisplay_StaysVisible), sb =>
        {
            // Like Cardwire's integrated mode: a monitor on a dGPU port keeps the card visible.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1;
            sb.ExternalDisplay = true;

            sb.Controller.RequestModeSwitch(GpuMode.Eco);

            Assert(!sb.BlockOps.ActiveState, "dGPU stays visible for the monitor");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "Eco still saved");
        });

    static void Ebpf_Reconcile_Eco_DisplayGone_Blocks()
        => Scenario(nameof(Ebpf_Reconcile_Eco_DisplayGone_Blocks), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            AppConfig.Set("gpu_auto", 0);
            sb.Power.OnAc = true;
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.ActiveState = false;

            sb.Controller.ReconcileBpfAuto();

            Assert(sb.BlockOps.ActiveState, "Eco hides the card once the monitor is gone, even on AC");
            Assert(sb.BlockOps.Calls.Contains("enable-quiet"), "no prompt");
        });

    static void Ebpf_Reconcile_Eco_DisplayAppears_Releases()
        => Scenario(nameof(Ebpf_Reconcile_Eco_DisplayAppears_Releases), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            AppConfig.Set("gpu_auto", 0);
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.ActiveState = true;
            sb.ExternalDisplay = true;

            sb.Controller.ReconcileBpfAuto();

            Assert(!sb.BlockOps.ActiveState, "card released for the monitor");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "mode stays Eco");
        });

    static void Ebpf_Reconcile_EcoAfterReboot_NotAppliedLive()
        => Scenario(nameof(Ebpf_Reconcile_EcoAfterReboot_NotAppliedLive), sb =>
        {
            // "After Reboot" in the apps dialog saves Eco for the next boot only.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1;
            sb.Controller.ScheduleModeForReboot(GpuMode.Eco);

            sb.Controller.ReconcileBpfAuto();

            Assert(!sb.BlockOps.ActiveState, "not hidden before the reboot");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "Eco stays scheduled");
        });

    static void Ebpf_Optimized_GetCurrentMode_ReportsOptimized()
        => Scenario(nameof(Ebpf_Optimized_GetCurrentMode_ReportsOptimized), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 1);
            sb.BlockOps.ActiveState = true;

            AssertEqual(GpuMode.Optimized, sb.Controller.GetCurrentMode(),
                "auto flag wins over the filter state");
        });

    static void Ebpf_GetCurrentMode_FollowsFilterState()
        => Scenario(nameof(Ebpf_GetCurrentMode_FollowsFilterState), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 0);

            sb.BlockOps.ActiveState = true;
            AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(), "filter on -> eco");
            sb.BlockOps.ActiveState = false;
            AssertEqual(GpuMode.Standard, sb.Controller.GetCurrentMode(), "filter off -> standard");
        });

    static void Ebpf_Auto_Unplug_BlocksNonInteractive()
        => Scenario(nameof(Ebpf_Auto_Unplug_BlocksNonInteractive), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;

            var result = sb.Controller.AutoGpuSwitch();

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.BlockOps.ActiveState, "blocked on battery");
            Assert(sb.BlockOps.Calls.Contains("enable-quiet"),
                "power events must never pop an auth prompt");
        });

    static void Ebpf_Auto_Unplug_AppsHold_Asks()
        => Scenario(nameof(Ebpf_Auto_Unplug_AppsHold_Asks), sb =>
        {
            // The power event shows the apps dialog instead of hiding the card under them.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.HolderList.Add(new Gpu.NVidia.NvidiaHolder(4242, "blender", "user", 2, 0, true));

            AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.AutoGpuSwitch(), "asked");
            Assert(!sb.BlockOps.ActiveState, "not hidden yet");
            Assert(AppConfig.Is("gpu_auto"), "still Optimized");
        });

    static void Ebpf_Reconcile_Optimized_AppsHold_WaitsForThem()
        => Scenario(nameof(Ebpf_Reconcile_Optimized_AppsHold_WaitsForThem), sb =>
        {
            // After Cancel the tick must not hide the card under the apps,
            // but it hides it once they are gone.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.HolderList.Add(new Gpu.NVidia.NvidiaHolder(4242, "blender", "user", 2, 0, true));

            sb.Controller.ReconcileBpfAuto();
            Assert(!sb.BlockOps.ActiveState, "waits while apps hold the dGPU");

            sb.BlockOps.HolderList.Clear();
            sb.Controller.ReconcileBpfAuto();
            Assert(sb.BlockOps.ActiveState, "Eco once the apps closed");
            Assert(sb.BlockOps.Calls.Contains("enable-quiet"), "no prompt");
        });

    static void Ebpf_Reconcile_Eco_AppsHold_StillBlocks()
        => Scenario(nameof(Ebpf_Reconcile_Eco_AppsHold_StillBlocks), sb =>
        {
            // Manual Eco was already confirmed, so a monitor unplug hides the card at once.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            AppConfig.Set("gpu_auto", 0);
            sb.Wmi.MuxMode = 1;
            sb.BlockOps.HolderList.Add(new Gpu.NVidia.NvidiaHolder(4242, "blender", "user", 2, 0, true));

            sb.Controller.ReconcileBpfAuto();

            Assert(sb.BlockOps.ActiveState, "Eco does not wait for the apps");
        });

    static void Ebpf_OptimizedCheckbox_GatesAutoSwitch()
        => Scenario(nameof(Ebpf_OptimizedCheckbox_GatesAutoSwitch), sb =>
        {
            // On ebpf only the Extra checkbox enables Optimized; the asus-wmi opt-in does not count.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_optimized_enabled", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 1;

            Assert(!AppConfig.IsOptimizedGpuModeEnabled(), "checkbox off");
            AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "no auto switch");
            Assert(!sb.BlockOps.ActiveState, "not hidden");

            AppConfig.Set("gpu_bpf_optimized", 1);
            Assert(AppConfig.IsOptimizedGpuModeEnabled(), "checkbox on");
            AssertEqual(GpuSwitchResult.Applied, sb.Controller.AutoGpuSwitch(), "auto switch");

            // The ebpf checkbox must not enable Optimized on the firmware backend.
            AppConfig.Set("gpu_backend", "asus-wmi");
            AppConfig.Set("gpu_optimized_enabled", 0);
            Assert(!AppConfig.IsOptimizedGpuModeEnabled(), "asus-wmi ignores gpu_bpf_optimized");
        });

    static void Ebpf_Auto_Plug_UnblocksNonInteractive()
        => Scenario(nameof(Ebpf_Auto_Plug_UnblocksNonInteractive), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = true;
            sb.BlockOps.ActiveState = true;

            var result = sb.Controller.AutoGpuSwitch();

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(!sb.BlockOps.ActiveState, "unblocked on AC");
            Assert(sb.BlockOps.Calls.Contains("disable-quiet"), "non-interactive");
        });

    static void Ebpf_Auto_AlreadyOnCorrectLeg_NoOp()
        => Scenario(nameof(Ebpf_Auto_AlreadyOnCorrectLeg_NoOp), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.BlockOps.ActiveState = true;

            AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            AssertEqual(0, sb.BlockOps.Calls.Count, "no helper call");
        });

    static void Ebpf_Auto_MuxZero_DoesNotBlock()
        => Scenario(nameof(Ebpf_Auto_MuxZero_DoesNotBlock), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            sb.Wmi.MuxMode = 0;

            AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            Assert(!sb.BlockOps.ActiveState, "never block while the dGPU drives the panel");
        });

    static void Ebpf_Auto_WithoutAutoFlag_DoesNothing()
        => Scenario(nameof(Ebpf_Auto_WithoutAutoFlag_DoesNothing), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_bpf_optimized", 1);
            AppConfig.Set("gpu_auto", 0);
            sb.Power.OnAc = false;

            AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            Assert(!sb.BlockOps.ActiveState, "manual Eco/Standard is not auto-switched");
        });

    static void Ebpf_ManualEco_ClearsAutoFlag()
        => Scenario(nameof(Ebpf_ManualEco_ClearsAutoFlag), sb =>
        {
            // Leaving Optimized for a manual mode must stop auto switching,
            // or the next AC change would override the user's choice.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_auto", 1);
            sb.Wmi.MuxMode = 1;

            sb.Controller.RequestModeSwitch(GpuMode.Eco);

            Assert(!AppConfig.Is("gpu_auto"), "auto flag cleared");
            AssertEqual("eco", sb.PersistentContent(), "marker back to eco");
        });

    static void Ebpf_ScheduleForReboot_Optimized_WritesOptimizedMarker()
        => Scenario(nameof(Ebpf_ScheduleForReboot_Optimized_WritesOptimizedMarker), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1;

            var result = sb.Controller.ScheduleModeForReboot(GpuMode.Optimized);

            AssertEqual(GpuSwitchResult.RebootRequired, result, "result");
            AssertEqual("optimized", sb.TriggerContent(), "trigger for the boot service");
            AssertEqual("optimized", sb.PersistentContent(), "persistent marker");
            Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "no block artifacts");
            AssertEqual(1, sb.Wmi.MuxMode, "MUX untouched");
        });

    static void Ebpf_Startup_EcoWanted_FilterOff_ReattachesQuiet()
        => Scenario(nameof(Ebpf_Startup_EcoWanted_FilterOff_ReattachesQuiet), sb =>
        {
            // bpffs is wiped on reboot; if the boot service did not run, the
            // saved Eco session has to be honoured here.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.BlockOps.ActiveState = false;

            var result = sb.Controller.ApplyPendingOnStartup();

            AssertEqual(GpuSwitchResult.Applied, result, "result");
            Assert(sb.BlockOps.ActiveState, "filter re-attached");
            Assert(sb.BlockOps.Calls.Contains("enable-quiet"), "no prompt at startup");
        });

    static void Ebpf_Startup_Optimized_NotClobbered()
        => Scenario(nameof(Ebpf_Startup_Optimized_NotClobbered), sb =>
        {
            // Startup used to sync config to eco/standard from the filter
            // state, which silently threw Optimized away.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_mode", "optimized");
            AppConfig.Set("gpu_auto", 1);
            sb.BlockOps.ActiveState = false;

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual("optimized", AppConfig.GetString("gpu_mode") ?? "", "mode preserved");
            Assert(AppConfig.Is("gpu_auto"), "auto flag preserved");
        });

    static void Ebpf_Startup_SyncsConfigFromFilterState()
        => Scenario(nameof(Ebpf_Startup_SyncsConfigFromFilterState), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_mode", "standard");
            sb.BlockOps.ActiveState = true;

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "",
                "config follows the attached filter");
        });

    static void Ebpf_Startup_Active_RefreshesNodes()
        => Scenario(nameof(Ebpf_Startup_Active_RefreshesNodes), sb =>
        {
            // The boot service may attach before the driver creates its nodes.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.BlockOps.ActiveState = true;

            sb.Controller.ApplyPendingOnStartup();

            Assert(sb.BlockOps.Calls.Contains("refresh"), "node list re-resolved at startup");
        });

    static void Ebpf_Startup_Eco_ExternalDisplay_StaysVisible()
        => Scenario(nameof(Ebpf_Startup_Eco_ExternalDisplay_StaysVisible), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.BlockOps.ActiveState = false;
            sb.ExternalDisplay = true;

            sb.Controller.ApplyPendingOnStartup();

            Assert(!sb.BlockOps.ActiveState, "not re-attached over a monitor");
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "Eco kept, not synced to Standard");
        });

    static void Ebpf_Startup_StaleBackendMarker_UnticksEbpf()
        => Scenario(nameof(Ebpf_Startup_StaleBackendMarker_UnticksEbpf), sb =>
        {
            // The boot service runs what the marker says, so the config follows the marker.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.WriteBackend("asus-wmi");
            sb.BlockOps.ActiveState = true;
            File.WriteAllText(sb.PersistentPath, "eco");

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual("asus-wmi", AppConfig.GetGpuBackend(), "config follows the marker");
            Assert(!sb.BlockOps.ActiveState, "no filter left without an owner");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "ebpf Eco dropped");
            Assert(!sb.PersistentPresent(), "no persistent Eco for the other backend");
        });

    static void Ebpf_Startup_MissingBackendMarker_UnticksEbpf()
        => Scenario(nameof(Ebpf_Startup_MissingBackendMarker_UnticksEbpf), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.BlockOps.ActiveState = true;

            sb.Controller.ApplyPendingOnStartup();

            AssertEqual(AppConfig.DefaultGpuBackend(), AppConfig.GetGpuBackend(), "no marker, default backend");
            Assert(!sb.BlockOps.ActiveState, "filter detached");
        });

    static void PushBackendMarker_WriteFails_ReturnsFalse()
        => Scenario(nameof(PushBackendMarker_WriteFails_ReturnsFalse), sb =>
        {
            // The eBPF checkbox reverts on false, so a failed write must not report success.
            sb.WriteBackend("asus-wmi");
            File.Delete(sb.BackendPath);
            Directory.CreateDirectory(sb.BackendPath);
            try
            { Assert(!sb.Controller.PushBackendMarker("ebpf"), "write failed"); }
            finally { Directory.Delete(sb.BackendPath); }
            Assert(sb.Controller.PushBackendMarker("ebpf"), "write ok");
        });

    static void Ebpf_LiveEco_StaleBackendMarker_Fixed()
        => Scenario(nameof(Ebpf_LiveEco_StaleBackendMarker_Fixed), sb =>
        {
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.MuxMode = 1;
            sb.WriteBackend("asus-wmi");

            AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "applied");
            AssertEqual("ebpf", sb.BackendContent(), "boot marker follows the config");
        });

    static void Ebpf_BackendToggle_LeavingEbpf_DetachesFilter()
        => Scenario(nameof(Ebpf_BackendToggle_LeavingEbpf_DetachesFilter), sb =>
        {
            // A filter left attached with no owner keeps the dGPU hidden and
            // nothing in the UI would explain why.
            AppConfig.Set("gpu_backend", "asus-wmi");
            AppConfig.Set("gpu_mode", "eco");
            AppConfig.Set("gpu_auto", 1);
            sb.BlockOps.ActiveState = true;
            File.WriteAllText(sb.PersistentPath, "optimized");

            sb.Controller.OnBackendChanged("ebpf", "asus-wmi");

            Assert(!sb.BlockOps.ActiveState, "filter detached");
            Assert(!sb.PersistentPresent(), "marker dropped");
            Assert(!AppConfig.Is("gpu_auto"), "auto switching stopped");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "mode reset");
        });

    static void Ebpf_BackendToggle_EnteringEbpf_ExitsFirmwareEco()
        => Scenario(nameof(Ebpf_BackendToggle_EnteringEbpf_ExitsFirmwareEco), sb =>
        {
            // Firmware Eco hides the card from the bus, leaving the filter
            // nothing to attach to.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.Wmi.EcoEnabled = true;
            sb.Wmi.MuxMode = 1;

            sb.Controller.OnBackendChanged("asus-wmi", "ebpf");

            Assert(!sb.Wmi.EcoEnabled, "dgpu_disable=0 written");
        });

    static void Ebpf_BackendToggle_EnteringEbpf_ExitsPciEco()
        => Scenario(nameof(Ebpf_BackendToggle_EnteringEbpf_ExitsPciEco), sb =>
        {
            // Live PCI Eco: block files on disk, card hot-removed from the
            // bus, marker says eco. Deleting the files alone would leave the
            // card gone until reboot and the marker would make the boot
            // service re-apply an Eco the user never chose on this backend.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.WriteBlockArtifacts();
            File.WriteAllText(sb.PersistentPath, "eco");

            sb.Controller.OnBackendChanged("pci", "ebpf");

            Assert(!sb.ModprobePresent(), "modprobe block removed");
            Assert(!sb.UdevPresent(), "udev remove rule removed");
            Assert(!sb.TriggerPresent(), "no trigger left behind");
            Assert(!sb.PersistentPresent(), "marker dropped");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "mode reset to standard");
        });

    static void GpuPolicy_ValidatesProcessNames()
        => Scenario(nameof(GpuPolicy_ValidatesProcessNames), sb =>
        {
            Assert(Gpu.Block.GpuBlockPolicy.Validate("steam") == null, "plain name ok");
            Assert(Gpu.Block.GpuBlockPolicy.Validate("gnome-shell") == null, "hyphen ok");
            // comm is TASK_COMM_LEN, so 15 usable characters. Reject rather
            // than silently truncate into a name that never matches.
            Assert(Gpu.Block.GpuBlockPolicy.Validate("123456789012345") == null, "15 chars ok");
            AssertEqual("gpu_apps_too_long",
                Gpu.Block.GpuBlockPolicy.Validate("1234567890123456") ?? "", "16 chars rejected");
            AssertEqual("gpu_apps_invalid",
                Gpu.Block.GpuBlockPolicy.Validate("/usr/bin/steam") ?? "", "paths rejected");
            AssertEqual("gpu_apps_invalid",
                Gpu.Block.GpuBlockPolicy.Validate("two words") ?? "", "spaces rejected");
            AssertEqual("gpu_apps_invalid",
                Gpu.Block.GpuBlockPolicy.Validate("") ?? "", "empty rejected");
            AssertEqual("gpu_apps_invalid",
                Gpu.Block.GpuBlockPolicy.Validate(null) ?? "", "null rejected");
        });

    static void GpuPolicy_AllowDenyRoundTrip()
        => Scenario(nameof(GpuPolicy_AllowDenyRoundTrip), sb =>
        {
            AssertEqual(0, Gpu.Block.GpuBlockPolicy.List().Count, "starts empty");

            Assert(Gpu.Block.GpuBlockPolicy.Allow("steam"), "allow steam");
            Assert(Gpu.Block.GpuBlockPolicy.Allow("blender"), "allow blender");
            AssertEqual(2, Gpu.Block.GpuBlockPolicy.List().Count, "two entries");

            // Adding twice must not duplicate.
            Assert(Gpu.Block.GpuBlockPolicy.Allow("steam"), "allow again is fine");
            AssertEqual(2, Gpu.Block.GpuBlockPolicy.List().Count, "still two");

            Assert(Gpu.Block.GpuBlockPolicy.Deny("steam"), "deny steam");
            var left = Gpu.Block.GpuBlockPolicy.List();
            AssertEqual(1, left.Count, "one left");
            AssertEqual("blender", left[0], "the right one survived");

            // Removing something absent is a no-op, not a failure.
            Assert(Gpu.Block.GpuBlockPolicy.Deny("steam"), "deny absent is fine");
        });

    static void GpuPolicy_IgnoresCommentsAndJunkInFile()
        => Scenario(nameof(GpuPolicy_IgnoresCommentsAndJunkInFile), sb =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Gpu.Block.GpuBlockPolicy.PolicyPath)!);
            File.WriteAllLines(Gpu.Block.GpuBlockPolicy.PolicyPath, new[]
            {
                "# managed by ghelper",
                "",
                "steam",
                "/usr/bin/bad",
                "way-too-long-a-name-here",
                "steam",
                "blender",
            });

            var list = Gpu.Block.GpuBlockPolicy.List();
            AssertEqual(2, list.Count, "junk, dupes and over-long names dropped");
            AssertEqual("steam", list[0], "first");
            AssertEqual("blender", list[1], "second");
        });

    static void UdevMonitor_ParsesRealMonitorLines()
        => Scenario(nameof(UdevMonitor_ParsesRealMonitorLines), sb =>
        {
            // Captured from udevadm on the test machine.
            var ev = Platform.Linux.UdevMonitor.ParseEvent(
                "UDEV  [3134.782276] change   /devices/pci0000:00/0000:00:01.0/0000:01:00.0/drm/card2/card2-HDMI-A-3 (drm)");
            Assert(ev != null, "parsed");
            AssertEqual("change", ev!.Action, "action");
            AssertEqual("drm", ev.Subsystem, "subsystem");
            AssertEqual("card2-HDMI-A-3", ev.SysName, "sysname is the last segment");

            var pci = Platform.Linux.UdevMonitor.ParseEvent(
                "UDEV  [100.5] bind   /devices/pci0000:00/0000:00:01.0/0000:01:00.0 (pci)");
            Assert(pci != null && pci.Action == "bind" && pci.Subsystem == "pci", "pci bind parsed");
        });

    static void UdevMonitor_IgnoresBannerAndJunk()
        => Scenario(nameof(UdevMonitor_IgnoresBannerAndJunk), sb =>
        {
            Assert(Platform.Linux.UdevMonitor.ParseEvent(null) == null, "null");
            Assert(Platform.Linux.UdevMonitor.ParseEvent("") == null, "empty");
            Assert(Platform.Linux.UdevMonitor.ParseEvent(
                "monitor will print the received events for:") == null, "banner line 1");
            Assert(Platform.Linux.UdevMonitor.ParseEvent(
                "UDEV - the event which udev sends out after rule processing") == null, "banner line 2");
            Assert(Platform.Linux.UdevMonitor.ParseEvent(
                "UDEV  [1.0] change   /devices/foo drm") == null, "subsystem must be parenthesised");
        });

    static void DgpuDisplay_InternalPanelsAndVirtualIgnored()
        => Scenario(nameof(DgpuDisplay_InternalPanelsAndVirtualIgnored), sb =>
        {
            // The dGPU on a hybrid laptop lists its own eDP even though the
            // panel runs off the iGPU. Counting it would block the feature
            // forever, so only real external outputs count.
            Assert(Gpu.Block.DgpuDisplay.IsExternalConnector("HDMI-A-3"), "HDMI is external");
            Assert(Gpu.Block.DgpuDisplay.IsExternalConnector("DP-3"), "DP is external");
            Assert(!Gpu.Block.DgpuDisplay.IsExternalConnector("eDP-2"), "eDP is internal");
            Assert(!Gpu.Block.DgpuDisplay.IsExternalConnector("LVDS-1"), "LVDS is internal");
            Assert(!Gpu.Block.DgpuDisplay.IsExternalConnector("Virtual-1"), "virtual is not physical");
            Assert(!Gpu.Block.DgpuDisplay.IsExternalConnector("Writeback-1"), "writeback is not physical");
        });

    static void DgpuDisplay_DetectsMonitorOnDgpuPortOnly()
        => Scenario(nameof(DgpuDisplay_DetectsMonitorOnDgpuPortOnly), sb =>
        {
            // Real layout from a Strix G16: card1 = iGPU (panel), card2 = dGPU.
            var topology = new[]
            {
                ("card1-eDP-1", "connected"),
                ("card1-HDMI-A-1", "disconnected"),
                ("card2-DP-3", "disconnected"),
                ("card2-eDP-2", "disconnected"),
                ("card2-HDMI-A-3", "disconnected"),
            };
            Assert(!Gpu.Block.DgpuDisplay.AnyExternalConnected(2, topology),
                "nothing plugged into the dGPU");
            // The panel on the iGPU must never count as a dGPU display.
            Assert(!Gpu.Block.DgpuDisplay.AnyExternalConnected(2,
                new[] { ("card1-eDP-1", "connected") }), "iGPU panel is not a dGPU display");

            var plugged = new[]
            {
                ("card1-eDP-1", "connected"),
                ("card2-HDMI-A-3", "connected"),
            };
            Assert(Gpu.Block.DgpuDisplay.AnyExternalConnected(2, plugged),
                "monitor on the dGPU HDMI port is detected");
        });

    static void DgpuDisplay_ConnectedInternalPanelOnDgpuDoesNotCount()
        => Scenario(nameof(DgpuDisplay_ConnectedInternalPanelOnDgpuDoesNotCount), sb =>
        {
            // MUX=0 routes the panel to the dGPU, but that case is refused by
            // the impossible-state guard, so it must not also veto blocking.
            Assert(!Gpu.Block.DgpuDisplay.AnyExternalConnected(2,
                new[] { ("card2-eDP-2", "connected") }), "connected eDP on the dGPU is still internal");
        });

    static void RecoveryMarker_ClearedWhenWritable()
        => Scenario(nameof(RecoveryMarker_ClearedWhenWritable), sb =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GPUModeControl.RecoveryMarkerPath)!);
            File.WriteAllText(GPUModeControl.RecoveryMarkerPath, "2026-01-01 recovered to Standard");

            Assert(GPUModeControl.ClearRecoveryMarker(), "reports cleared");
            Assert(!File.Exists(GPUModeControl.RecoveryMarkerPath), "marker gone");
        });

    static void RecoveryMarker_AbsentIsSuccess()
        => Scenario(nameof(RecoveryMarker_AbsentIsSuccess), sb =>
        {
            Assert(GPUModeControl.ClearRecoveryMarker(), "no marker is already clear");
        });

    static void Ebpf_EcoPersistentConfig_DoesNotClobberOptimizedMarker()
        => Scenario(nameof(Ebpf_EcoPersistentConfig_DoesNotClobberOptimizedMarker), sb =>
        {
            // RequestModeSwitch used to re-sync the marker from the eco
            // persistence preference after the ebpf path had already written
            // "optimized", turning it back into an unpersist.
            AppConfig.Set("gpu_backend", "ebpf");
            AppConfig.Set("gpu_eco_persistent", 1);
            sb.Power.OnAc = true;

            sb.Controller.RequestModeSwitch(GpuMode.Optimized);

            AssertEqual("optimized", sb.PersistentContent(), "marker survives");
        });
}

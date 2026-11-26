// Comprehensive scenario coverage for GPUModeControl.
//
// Matrix:
//   PCI backend × raw_wmi flag × initial hardware state × button pressed
//
// Plus targeted edge cases (MUX=0 latched, blocks-present detection,
// ApplyPendingOnStartup behaviour, GpuBackendChoice.Select side effects).
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
        SelectBackend_Pci_WritesMarkerFile();
        SelectBackend_AsusWmi_WritesMarkerFile();
        SelectBackend_Pci_PreservesMuxLatchFlag();
        SelectBackend_AsusWmi_DoesNotTouchLatchFlag();
        SelectBackend_UnknownMarker_ReadsNull();
        SelectBackend_Idempotent_NoRewrite();
        SelectBackend_Ebpf_WritesMarkerFile();
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
        Ebpf_Startup_ReattachFails_ReportsStandard();
        Ebpf_Startup_MissingBackendMarker_UnticksEbpf();
        SelectBackend_WriteFails_ReturnsFalse();
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

        Console.WriteLine("\n eBPF default (new installs, later starts, updates, backend boxes) ");
        NewInstall_GenericIntelIgpuOnly_Pci();
        NewInstall_AsusAmdIgpuOnly_Mux();
        NewInstall_AmdIgpuNoBootVga_Pci();
        NewInstall_IgpuOnlyFirmwareDgpuAttr_NoBpf();
        NewInstall_AsusAmdIgpuAmdDgpu_PicksBpf();
        NewInstall_GenericIntelAmdDgpu_PicksBpf();
        NewInstall_GenericIntelAmdDgpu_NoNouveau_PicksBpf();
        NewInstall_AmdDgpu_FirmwareEco_NoBpf();
        NewInstall_AmdDgpu_Ultimate_NoBpf();
        NewInstall_AmdDgpu_BpfUnavailable_NoBpf();
        NewInstall_AmdDgpu_PciMarker_Kept();
        ExistingInstall_AmdDgpu_BackendKept();
        NewInstall_SystemFilesLater_RetryPicksBpf();
        NewInstall_UserPickedBeforeRetry_Kept();
        ExistingInstall_Retry_NoOp();
        AutoDefault_BpfLater_NextStartPicksBpf();
        AutoDefault_InEco_WaitsForStandard();
        AutoDefault_OwnMarker_NextStartPicksBpf();
        AutoDefault_UserPick_NeverReplaced();
        Eco_AmdIgpuAmdDgpu_TargetsDgpu();
        Eco_AmdIgpuPanelOnly_NoTarget();
        LeaveBpf_StaleEbpfMarker_Fallback();
        LeaveBpf_Asus_MarkerElseMux();
        NewInstall_AsusIntelNvidia_PicksBpf();
        NewInstall_AsusAmdNvidia_PicksBpf();
        NewInstall_GenericIntelNvidia_PicksBpf();
        NewInstall_LenovoNoMuxAttr_PicksBpf();
        NewInstall_TwoDgpus_PicksBpf();
        NewInstall_SystemFilesDeclined_StaysOnFallback();
        NewInstall_MarkerSaveFails_RetriesNextStart();
        Capability_AutoPickAgreesWithBackendBox();
        WhenUnticked_EbpfUnlessKernelNever();
        NewInstall_AsusWmiMarker_Kept();
        NewInstall_EbpfEcoLeft_PicksBpf_KeepsEco();
        NewInstall_EbpfMarkerLeft_BpfUnavailable_PicksLater();
        NewInstall_PciEcoLeft_NoBpf_FilesKept();
        NewInstall_UnknownMarker_PicksBpf();
        NewInstall_OnlyBackupLeft_PicksBpf();
        AutoDefault_UltimateEcoOptimized_WaitForStandard();
        AutoDefault_AsusOwnMuxMarker_NextStartPicksBpf();
        AutoDefault_DgpuOffBus_Waits();
        AutoDefault_NeverAvailable_StaysOnFallback();
        ExistingInstall_GenericUnset_Pci();
        ExistingInstall_InEco_BackendKept();

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
            // Select("pci") must NOT clear the latch flag. The
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

            GpuBackendChoice.Select(GpuBackendKind.Pci);

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
            AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), "default backend");
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
    // Backend toggle plumbing (GpuBackendChoice.Select)
    // 

    static void SelectBackend_Pci_WritesMarkerFile()
        => Scenario(nameof(SelectBackend_Pci_WritesMarkerFile), sb =>
        {
            GpuBackendChoice.Select(GpuBackendKind.Pci);
            AssertEqual("pci", sb.BackendContent(), "marker file content");
        });

    static void SelectBackend_AsusWmi_WritesMarkerFile()
        => Scenario(nameof(SelectBackend_AsusWmi_WritesMarkerFile), sb =>
        {
            sb.WriteBackend("pci");
            GpuBackendChoice.Select(GpuBackendKind.AsusWmi);
            AssertEqual("asus-wmi", sb.BackendContent(), "marker file content");
        });

    static void SelectBackend_Pci_PreservesMuxLatchFlag()
        => Scenario(nameof(SelectBackend_Pci_PreservesMuxLatchFlag), sb =>
        {
            // The MUX=0 latch flag encodes real firmware state (gpu_mux_mode=0
            // is pending until the next reboot). A backend toggle does NOT
            // change firmware. Clearing the flag here would re-introduce
            // the impossible-state chain: user Ultimate -> PCI toggle ->
            // Eco -> blocks written -> reboot lands in MUX=0 with udev
            // removing the dGPU = black screen.
            AppConfig.Set("mux_zero_latched_boot_id", "any-boot-id");
            GpuBackendChoice.Select(GpuBackendKind.Pci);
            AssertEqual("any-boot-id", AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "latch flag preserved across PCI backend toggle");
        });

    static void SelectBackend_AsusWmi_DoesNotTouchLatchFlag()
        => Scenario(nameof(SelectBackend_AsusWmi_DoesNotTouchLatchFlag), sb =>
        {
            AppConfig.Set("mux_zero_latched_boot_id", "from-asus-wmi-session");
            GpuBackendChoice.Select(GpuBackendKind.AsusWmi);
            AssertEqual("from-asus-wmi-session", AppConfig.GetString("mux_zero_latched_boot_id") ?? "",
                "asus-wmi backend preserves its own MUX state");
        });

    static void SelectBackend_UnknownMarker_ReadsNull()
        => Scenario(nameof(SelectBackend_UnknownMarker_ReadsNull), sb =>
        {
            sb.WriteBackend("nonsense");
            AssertEqual(null, GpuBootFiles.ReadBackend(), "unknown marker text is no backend");
        });

    static void SelectBackend_Idempotent_NoRewrite()
        => Scenario(nameof(SelectBackend_Idempotent_NoRewrite), sb =>
        {
            sb.WriteBackend("pci");
            // Tamper the file timestamp to detect rewrite.
            var beforeWrite = File.GetLastWriteTimeUtc(sb.BackendPath);
            Thread.Sleep(20);
            GpuBackendChoice.Select(GpuBackendKind.Pci);
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
            GpuBackendChoice.Select(GpuBackendKind.AsusWmi);

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

            GpuBackendChoice.Select(GpuBackendKind.Pci);

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

    //
    // New installs: eBPF only with a dGPU on the PCI bus, on every vendor.
    //

    static void ResolveNewInstall(Harness.Sandbox sb, bool bpfAvailable = true)
        => GpuBackendChoice.ResolveAtStartup(sb.Wmi, () => bpfAvailable);

    static void AssertNoBpf(Harness.Sandbox sb, GpuBackendKind backend)
    {
        AssertEqual(backend, AppConfig.GetGpuBackend(), "backend");
        Assert(!sb.BackendPresent(), "no backend marker written");
    }

    static void AssertBpf(Harness.Sandbox sb)
    {
        AssertEqual("ebpf", AppConfig.GetString("gpu_backend"), "gpu_backend");
        AssertEqual("ebpf", sb.BackendContent(), "backend marker");
    }

    // AMD iGPU + AMD dGPU on an ASUS laptop with dgpu_disable (G14 class).
    static void AsusAmdAmd(Harness.Sandbox sb)
    {
        sb.WriteFakeAsusPlatform();
        sb.WriteFakeAmdIgpuDevice();
        sb.WriteFakeAmdDgpuDevice();
    }

    // Intel iGPU + AMD dGPU on a generic laptop (HP OMEN class).
    static void GenericIntelAmd(Harness.Sandbox sb, bool nouveauOnDisk = true)
    {
        sb.WriteFakeIntelIgpuDevice();
        sb.WriteFakeAmdDgpuDevice();
        if (nouveauOnDisk)
            sb.WriteFakeNouveauOnDisk();
    }

    static void NewInstall_GenericIntelIgpuOnly_Pci()
        => Scenario(nameof(NewInstall_GenericIntelIgpuOnly_Pci), sb =>
        {
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNouveauOnDisk();

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.Pci);
        });

    static void NewInstall_AsusAmdIgpuOnly_Mux()
        => Scenario(nameof(NewInstall_AsusAmdIgpuOnly_Mux), sb =>
        {
            sb.WriteFakeAsusPlatform();
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeNouveauOnDisk();

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    static void NewInstall_AmdIgpuNoBootVga_Pci()
        => Scenario(nameof(NewInstall_AmdIgpuNoBootVga_Pci), sb =>
        {
            sb.WriteFakeAmdIgpuPanelOnly();

            Assert(DgpuPci.Find() == null, "iGPU with the panel is not a dGPU");
            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.Pci);
        });

    static void NewInstall_IgpuOnlyFirmwareDgpuAttr_NoBpf()
        => Scenario(nameof(NewInstall_IgpuOnlyFirmwareDgpuAttr_NoBpf), sb =>
        {
            // Some iGPU-only ASUS firmwares expose dgpu_disable; only a dGPU on the bus enables eBPF.
            sb.WriteFakeAsusPlatform();
            sb.WriteFakeIntelIgpuDevice();
            sb.Wmi.DgpuDisableSupported = true;

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    static void NewInstall_AsusAmdIgpuAmdDgpu_PicksBpf()
        => Scenario(nameof(NewInstall_AsusAmdIgpuAmdDgpu_PicksBpf), sb =>
        {
            AsusAmdAmd(sb);

            AssertEqual("0000:03:00.0", DgpuPci.Find()?.bdf, "dGPU, not the AMD iGPU");
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_GenericIntelAmdDgpu_PicksBpf()
        => Scenario(nameof(NewInstall_GenericIntelAmdDgpu_PicksBpf), sb =>
        {
            GenericIntelAmd(sb);

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_GenericIntelAmdDgpu_NoNouveau_PicksBpf()
        => Scenario(nameof(NewInstall_GenericIntelAmdDgpu_NoNouveau_PicksBpf), sb =>
        {
            // No NVIDIA or nouveau module on disk: the AMD dGPU on the bus is enough.
            GenericIntelAmd(sb, nouveauOnDisk: false);

            Assert(GPUModeControl.HasSecondGpu(), "backend selector shows");
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_AmdDgpu_FirmwareEco_NoBpf()
        => Scenario(nameof(NewInstall_AmdDgpu_FirmwareEco_NoBpf), sb =>
        {
            // dgpu_disable=1 from another OS: only the MUX backend can undo it.
            AsusAmdAmd(sb);
            sb.Wmi.EcoEnabled = true;

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    static void NewInstall_AmdDgpu_Ultimate_NoBpf()
        => Scenario(nameof(NewInstall_AmdDgpu_Ultimate_NoBpf), sb =>
        {
            AsusAmdAmd(sb);
            sb.Wmi.MuxMode = 0;

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    static void NewInstall_AmdDgpu_BpfUnavailable_NoBpf()
        => Scenario(nameof(NewInstall_AmdDgpu_BpfUnavailable_NoBpf), sb =>
        {
            AsusAmdAmd(sb);

            ResolveNewInstall(sb, bpfAvailable: false);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    static void NewInstall_AmdDgpu_PciMarker_Kept()
        => Scenario(nameof(NewInstall_AmdDgpu_PciMarker_Kept), sb =>
        {
            // Config reset with a PCI marker left behind: the marker stays.
            GenericIntelAmd(sb);
            sb.WriteBackend("pci");

            ResolveNewInstall(sb);
            AssertEqual(GpuBackendKind.Pci, AppConfig.GetGpuBackend(), "backend");
            AssertEqual("pci", sb.BackendContent(), "marker kept");
        });

    static void ExistingInstall_AmdDgpu_BackendKept()
        => Scenario(nameof(ExistingInstall_AmdDgpu_BackendKept), sb =>
        {
            File.WriteAllText(Path.Combine(sb.ConfigDir, "config.json"), "{}");
            AppConfig.ResetForTest(sb.ConfigDir);
            AsusAmdAmd(sb);

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    static void NewInstall_SystemFilesLater_RetryPicksBpf()
        => Scenario(nameof(NewInstall_SystemFilesLater_RetryPicksBpf), sb =>
        {
            // First AppImage start: gpu-helper arrives with the system files prompt.
            GenericIntelAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            AssertNoBpf(sb, GpuBackendKind.Pci);

            Assert(GpuBackendChoice.RetryAutoDefault(sb.Wmi, () => true, () => { }), "retry picks eBPF");
            AssertBpf(sb);
        });

    static void NewInstall_UserPickedBeforeRetry_Kept()
        => Scenario(nameof(NewInstall_UserPickedBeforeRetry_Kept), sb =>
        {
            AsusAmdAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            Assert(GpuBackendChoice.Select(GpuBackendKind.Pci), "user ticks PCI");
            bool waited = false;

            Assert(!GpuBackendChoice.RetryAutoDefault(sb.Wmi, () => true, () => waited = true), "retry leaves the user choice");
            Assert(!waited, "no wait for the prompt");
            AssertEqual(GpuBackendKind.Pci, AppConfig.GetGpuBackend(), "backend");
            AssertEqual("pci", sb.BackendContent(), "marker");
        });

    static void ExistingInstall_Retry_NoOp()
        => Scenario(nameof(ExistingInstall_Retry_NoOp), sb =>
        {
            File.WriteAllText(Path.Combine(sb.ConfigDir, "config.json"), "{}");
            AppConfig.ResetForTest(sb.ConfigDir);
            AsusAmdAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);

            Assert(!GpuBackendChoice.RetryAutoDefault(sb.Wmi, () => true, () => { }), "existing install: no retry");
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);
        });

    // Next app start: the config file exists, so it is no longer a new install.
    static void Restart(Harness.Sandbox sb)
    {
        AppConfig.Flush();
        AppConfig.ResetForTest(sb.ConfigDir);
    }

    static void AutoDefault_BpfLater_NextStartPicksBpf()
        => Scenario(nameof(AutoDefault_BpfLater_NextStartPicksBpf), sb =>
        {
            // bpf joins the LSM list after a reboot: the next start still applies the default.
            GenericIntelAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            AssertNoBpf(sb, GpuBackendKind.Pci);

            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_InEco_WaitsForStandard()
        => Scenario(nameof(AutoDefault_InEco_WaitsForStandard), sb =>
        {
            // Eco state belongs to the current backend: no switch until Standard.
            AsusAmdAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            AppConfig.Set("gpu_mode", "eco");
            Restart(sb);
            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.AsusWmi);

            AppConfig.Set("gpu_mode", "standard");
            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_OwnMarker_NextStartPicksBpf()
        => Scenario(nameof(AutoDefault_OwnMarker_NextStartPicksBpf), sb =>
        {
            // A PCI mode switch wrote the marker of the automatic pick: that marker is ours.
            GenericIntelAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            sb.WriteBackend("pci");

            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_UserPick_NeverReplaced()
        => Scenario(nameof(AutoDefault_UserPick_NeverReplaced), sb =>
        {
            AsusAmdAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            Assert(GpuBackendChoice.Select(GpuBackendKind.AsusWmi), "user ticks MUX");

            Restart(sb);
            ResolveNewInstall(sb);
            AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), "user pick kept");
            AssertEqual("asus-wmi", sb.BackendContent(), "marker");
        });

    static void Eco_AmdIgpuAmdDgpu_TargetsDgpu()
        => Scenario(nameof(Eco_AmdIgpuAmdDgpu_TargetsDgpu), sb =>
        {
            // Both functions are vendor 0x1002; the filter must take the dGPU.
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeAmdDgpuDevice();

            AssertEqual("0000:03:00.0", Block.DgpuPowerMonitor.DiscreteBdf(rescan: true), "block target");
        });

    static void Eco_AmdIgpuPanelOnly_NoTarget()
        => Scenario(nameof(Eco_AmdIgpuPanelOnly_NoTarget), sb =>
        {
            // No boot_vga (Ally class): the eDP panel alone must keep the iGPU visible.
            sb.WriteFakeAmdIgpuPanelOnly();

            AssertEqual(null, Block.DgpuPowerMonitor.DiscreteBdf(rescan: true), "never hide the panel GPU");
        });

    static void LeaveBpf_StaleEbpfMarker_Fallback()
        => Scenario(nameof(LeaveBpf_StaleEbpfMarker_Fallback), sb =>
        {
            // In-app uninstall removes gpu-helper but leaves the ebpf marker.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");

            AssertEqual(GpuBackendKind.Pci, GpuBackendChoice.LeaveBpf(), "no ASUS platform: pci");
            AssertEqual(GpuBackendKind.Pci, AppConfig.GetGpuBackend(), "config left ebpf");
        });

    static void LeaveBpf_Asus_MarkerElseMux()
        => Scenario(nameof(LeaveBpf_Asus_MarkerElseMux), sb =>
        {
            sb.WriteFakeAsusPlatform();
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AssertEqual(GpuBackendKind.AsusWmi, GpuBackendChoice.LeaveBpf(), "stale ebpf marker: MUX");

            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("pci");
            AssertEqual(GpuBackendKind.Pci, GpuBackendChoice.LeaveBpf(), "pci marker wins");
            AssertEqual(GpuBackendKind.Pci, AppConfig.GetGpuBackend(), "config follows");
        });

    static void NewInstall_AsusIntelNvidia_PicksBpf()
        => Scenario(nameof(NewInstall_AsusIntelNvidia_PicksBpf), sb =>
        {
            sb.WriteFakeAsusPlatform();
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNvidiaPciDevice();

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_AsusAmdNvidia_PicksBpf()
        => Scenario(nameof(NewInstall_AsusAmdNvidia_PicksBpf), sb =>
        {
            sb.WriteFakeAsusPlatform();
            sb.WriteFakeAmdIgpuDevice();
            sb.WriteFakeNvidiaPciDevice();

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_GenericIntelNvidia_PicksBpf()
        => Scenario(nameof(NewInstall_GenericIntelNvidia_PicksBpf), sb =>
        {
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNvidiaPciDevice();

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_LenovoNoMuxAttr_PicksBpf()
        => Scenario(nameof(NewInstall_LenovoNoMuxAttr_PicksBpf), sb =>
        {
            // Lenovo has no MUX attribute (reads -1) and no firmware Eco; only MUX 0 means Ultimate.
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNvidiaPciDevice();
            sb.Wmi.MuxModeSupported = false;
            sb.Wmi.DgpuDisableSupported = false;

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_TwoDgpus_PicksBpf()
        => Scenario(nameof(NewInstall_TwoDgpus_PicksBpf), sb =>
        {
            // An eGPU or XG Mobile next to the internal dGPU: any dGPU on the bus is enough.
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNvidiaPciDevice();
            sb.WriteFakeAmdDgpuDevice();

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_SystemFilesDeclined_StaysOnFallback()
        => Scenario(nameof(NewInstall_SystemFilesDeclined_StaysOnFallback), sb =>
        {
            // Prompt declined, closed or timed out: eBPF still cannot run after the wait.
            GenericIntelAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            bool waited = false;

            Assert(!GpuBackendChoice.RetryAutoDefault(sb.Wmi, () => false, () => waited = true), "no eBPF");
            Assert(waited, "retry waits for the prompt");
            AssertNoBpf(sb, GpuBackendKind.Pci);
        });

    static void NewInstall_MarkerSaveFails_RetriesNextStart()
        => Scenario(nameof(NewInstall_MarkerSaveFails_RetriesNextStart), sb =>
        {
            // Polkit cancelled: without the boot marker the config must not say ebpf.
            GenericIntelAmd(sb);
            Directory.CreateDirectory(sb.BackendPath);
            try
            {
                ResolveNewInstall(sb);
                AssertEqual(GpuBackendKind.Pci, AppConfig.GetGpuBackend(), "fallback");
            }
            finally { Directory.Delete(sb.BackendPath); }

            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    // bpf-caps output of a kernel and helper that can run the filter.
    const string AllCaps = "lsm_active=1\nkernel_config=y\nbpffs=1\nlibbpf=1\nfilter=1\nlockdown=none\n";

    // Kernel LSM list has bpf, bpf-caps output, Extra window reason, kernel or build can never run it.
    static readonly (bool lsm, string caps, string key, bool never)[] CapCases =
    {
        (true, AllCaps, "", false),
        (true, AllCaps.Replace("bpffs=1", "bpffs=0"), "", false), // gpu-helper mounts bpffs
        (true, AllCaps.Replace("lockdown=none", "lockdown=integrity"), "", false),
        (true, AllCaps.Replace("lockdown=none", "lockdown=confidentiality"), "gpu_block_unavail_lockdown", true),
        (true, AllCaps.Replace("libbpf=1", "libbpf=0"), "gpu_block_unavail_libbpf", false),
        (true, AllCaps.Replace("filter=1", "filter=0"), "gpu_block_unavail_filter", true),
        (true, "", "gpu_block_unavail_helper", false),
        (false, AllCaps.Replace("lsm_active=1", "lsm_active=0"), "gpu_block_unavail_lsm", false),
        (false, AllCaps.Replace("lsm_active=1\nkernel_config=y", "lsm_active=0\nkernel_config=n"), "gpu_block_unavail_kernel", true),
    };

    static void SetCaps(Harness.Sandbox sb, bool lsmBpf, string caps)
    {
        string dir = Path.Combine(sb.TempRoot, "sys", "kernel", "security");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "lsm"), lsmBpf ? "lockdown,capability,landlock,bpf\n" : "lockdown,capability,landlock\n");
        Block.GpuBlockCapability.TestCaps = () => caps;
    }

    static void Capability_AutoPickAgreesWithBackendBox()
        => Scenario(nameof(Capability_AutoPickAgreesWithBackendBox), sb =>
        {
            // The automatic pick and the Extra window must agree whether eBPF can run.
            foreach (var (lsm, caps, key, _) in CapCases)
            {
                SetCaps(sb, lsm, caps);
                string what = $"lsm bpf={lsm} caps [{caps.Replace('\n', ' ').Trim()}]";
                AssertEqual(key, Block.GpuBlockCapability.UnavailableKey(), $"reason, {what}");
                AssertEqual(key.Length == 0, Block.GpuBlockCapability.IsAvailable(), $"available, {what}");
            }
        });

    static void WhenUnticked_EbpfUnlessKernelNever()
        => Scenario(nameof(WhenUnticked_EbpfUnlessKernelNever), sb =>
        {
            // Unticking MUX or PCI goes to eBPF, also when an Updates row adds what is missing.
            // Only a kernel or build that can never run it falls back: PCI, or MUX on ASUS.
            foreach (bool asus in new[] { false, true })
            {
                if (asus)
                    sb.WriteFakeAsusPlatform();
                var fallback = asus ? GpuBackendKind.AsusWmi : GpuBackendKind.Pci;
                foreach (var (lsm, caps, key, never) in CapCases)
                {
                    SetCaps(sb, lsm, caps);
                    AssertEqual(never ? fallback : GpuBackendKind.Ebpf, GpuBackendChoice.WhenUnticked(),
                        $"{(asus ? "ASUS" : "generic")}, reason '{key}'");
                }
            }
        });

    static void NewInstall_AsusWmiMarker_Kept()
        => Scenario(nameof(NewInstall_AsusWmiMarker_Kept), sb =>
        {
            AsusAmdAmd(sb);
            sb.WriteBackend("asus-wmi");

            ResolveNewInstall(sb);
            AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), "backend");
            AssertEqual("asus-wmi", sb.BackendContent(), "marker kept");
        });

    static void NewInstall_EbpfEcoLeft_PicksBpf_KeepsEco()
        => Scenario(nameof(NewInstall_EbpfEcoLeft_PicksBpf_KeepsEco), sb =>
        {
            // Config deleted in eBPF Eco: the filter allows ghelper, so the app still sees the dGPU.
            sb.WriteFakeAsusPlatform();
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNvidiaPciDevice();
            sb.WriteBackend("ebpf");
            sb.BlockOps.ActiveState = true;

            ResolveNewInstall(sb);
            AssertBpf(sb);
            sb.Controller.ApplyPendingOnStartup();
            AssertEqual("eco", AppConfig.GetString("gpu_mode") ?? "", "Eco from the filter state");
            Assert(sb.BlockOps.ActiveState, "filter kept");
            Gate.Resume();
        });

    static void NewInstall_EbpfMarkerLeft_BpfUnavailable_PicksLater()
        => Scenario(nameof(NewInstall_EbpfMarkerLeft_BpfUnavailable_PicksLater), sb =>
        {
            // The kernel lost bpf in its LSM list since the earlier install.
            AsusAmdAmd(sb);
            sb.WriteBackend("ebpf");
            ResolveNewInstall(sb, bpfAvailable: false);
            AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), "fallback");

            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_PciEcoLeft_NoBpf_FilesKept()
        => Scenario(nameof(NewInstall_PciEcoLeft_NoBpf_FilesKept), sb =>
        {
            // PCI Eco from an earlier install keeps the dGPU off the bus.
            sb.WriteFakeAsusPlatform();
            sb.WriteFakeIntelIgpuDevice();
            sb.WriteFakeNvidiaModuleOnDisk();
            sb.WriteBlockArtifacts();
            sb.WriteBackend("pci");

            ResolveNewInstall(sb);
            Assert(!AppConfig.IsBpfGpuBackend(), "no eBPF");
            AssertEqual("pci", sb.BackendContent(), "marker kept");
            Assert(sb.ModprobePresent() && sb.UdevPresent(), "Eco files kept");
        });

    static void NewInstall_UnknownMarker_PicksBpf()
        => Scenario(nameof(NewInstall_UnknownMarker_PicksBpf), sb =>
        {
            GenericIntelAmd(sb);
            sb.WriteBackend("cardwire");

            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void NewInstall_OnlyBackupLeft_PicksBpf()
        => Scenario(nameof(NewInstall_OnlyBackupLeft_PicksBpf), sb =>
        {
            // Writes go through a temp file, so a missing config.json means the user deleted it.
            File.WriteAllText(Path.Combine(sb.ConfigDir, "config.json.bak"), "{\"gpu_backend\":\"asus-wmi\"}");
            AppConfig.ResetForTest(sb.ConfigDir);
            GenericIntelAmd(sb);

            Assert(AppConfig.IsNewInstall, "new install");
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_UltimateEcoOptimized_WaitForStandard()
        => Scenario(nameof(AutoDefault_UltimateEcoOptimized_WaitForStandard), sb =>
        {
            // Each state belongs to the current backend: no switch while it lasts.
            AsusAmdAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            foreach (var (name, set) in new (string, Action<bool>)[]
            {
                ("Ultimate", on => sb.Wmi.MuxMode = on ? 0 : 1),
                ("firmware Eco", on => sb.Wmi.EcoEnabled = on),
                ("Optimized", on =>
                {
                    AppConfig.Set("gpu_mode", on ? "optimized" : "standard");
                    AppConfig.Set("gpu_auto", on ? 1 : 0);
                }),
            })
            {
                set(true);
                Restart(sb);
                ResolveNewInstall(sb);
                AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), $"{name}: backend");
                Assert(!sb.BackendPresent(), $"{name}: no marker");
                set(false);
            }

            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_AsusOwnMuxMarker_NextStartPicksBpf()
        => Scenario(nameof(AutoDefault_AsusOwnMuxMarker_NextStartPicksBpf), sb =>
        {
            // A MUX mode switch wrote the marker while gpu_backend is unset, which reads as MUX.
            AsusAmdAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            sb.WriteBackend("asus-wmi");

            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_DgpuOffBus_Waits()
        => Scenario(nameof(AutoDefault_DgpuOffBus_Waits), sb =>
        {
            // PCI Standard before the reboot: the dGPU is not back on the bus yet.
            GenericIntelAmd(sb);
            ResolveNewInstall(sb, bpfAvailable: false);
            Directory.Delete(Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", "0000:03:00.0"), recursive: true);

            Restart(sb);
            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.Pci);

            sb.WriteFakeAmdDgpuDevice();
            Restart(sb);
            ResolveNewInstall(sb);
            AssertBpf(sb);
        });

    static void AutoDefault_NeverAvailable_StaysOnFallback()
        => Scenario(nameof(AutoDefault_NeverAvailable_StaysOnFallback), sb =>
        {
            // A kernel that can never run eBPF: every start keeps PCI and writes no marker.
            GenericIntelAmd(sb);
            for (int start = 1; start <= 3; start++)
            {
                ResolveNewInstall(sb, bpfAvailable: false);
                Assert(!GpuBackendChoice.RetryAutoDefault(sb.Wmi, () => false, () => { }), $"start {start}: retry");
                AssertNoBpf(sb, GpuBackendKind.Pci);
                Restart(sb);
            }
        });

    static void ExistingInstall_GenericUnset_Pci()
        => Scenario(nameof(ExistingInstall_GenericUnset_Pci), sb =>
        {
            // Update from a version without the eBPF default: non-ASUS gets PCI, as before.
            File.WriteAllText(Path.Combine(sb.ConfigDir, "config.json"), "{}");
            AppConfig.ResetForTest(sb.ConfigDir);
            GenericIntelAmd(sb);

            ResolveNewInstall(sb);
            AssertNoBpf(sb, GpuBackendKind.Pci);
        });

    static void ExistingInstall_InEco_BackendKept()
        => Scenario(nameof(ExistingInstall_InEco_BackendKept), sb =>
        {
            AsusAmdAmd(sb);
            foreach (var (json, want) in new[]
            {
                ("{\"gpu_mode\":\"eco\"}", GpuBackendKind.AsusWmi),
                ("{\"gpu_backend\":\"pci\",\"gpu_mode\":\"eco\"}", GpuBackendKind.Pci),
            })
            {
                File.WriteAllText(Path.Combine(sb.ConfigDir, "config.json"), json);
                AppConfig.ResetForTest(sb.ConfigDir);

                ResolveNewInstall(sb);
                Assert(!GpuBackendChoice.RetryAutoDefault(sb.Wmi, () => true, () => { }), $"{json}: no retry");
                AssertNoBpf(sb, want);
            }
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
            AssertEqual(GpuBackendKind.Ebpf, AppConfig.GetGpuBackend(), "ebpf backend accepted");
            Assert(AppConfig.IsBpfGpuBackend(), "IsBpfGpuBackend true");
            Assert(!AppConfig.IsPciGpuBackend(), "ebpf is not the PCI backend");

            AppConfig.Set("gpu_backend", "asus-wmi");
            Assert(!AppConfig.IsBpfGpuBackend(), "switching away clears it");
        });

    static void GpuBlock_Backend_UnknownValueFallsBackToWmi()
        => Scenario(nameof(GpuBlock_Backend_UnknownValueFallsBackToWmi), sb =>
        {
            AppConfig.Set("gpu_backend", "ebfp");
            AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), "typo falls back to the safe default");
            Assert(!AppConfig.IsBpfGpuBackend(), "typo does not select the filter");
        });

    static void SelectBackend_Ebpf_WritesMarkerFile()
        => Scenario(nameof(SelectBackend_Ebpf_WritesMarkerFile), sb =>
        {
            sb.WriteBackend("asus-wmi");
            GpuBackendChoice.Select(GpuBackendKind.Ebpf);
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
            Assert(File.Exists(GpuBootFiles.PersistentTriggerPath),
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
            File.WriteAllText(GpuBootFiles.PersistentTriggerPath, "eco");

            var result = sb.Controller.ScheduleModeForReboot(GpuMode.Standard);
            AssertEqual(GpuSwitchResult.RebootRequired, result, "deferred ebpf standard reported");
            Assert(!sb.ModprobePresent(), "stale modprobe block removed");
            Assert(!sb.UdevPresent(), "stale udev rule removed");
            Assert(!File.Exists(GpuBootFiles.PersistentTriggerPath),
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
            Gate.Resume();

            sb.Controller.ApplyPendingOnStartup();

            Assert(sb.BlockOps.Calls.Contains("refresh"), "node list re-resolved at startup");
            Assert(Gate.IsPaused, "telemetry held, it would wake the dGPU");
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

            AssertEqual(GpuBackendKind.AsusWmi, AppConfig.GetGpuBackend(), "config follows the marker");
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

            AssertEqual(GpuBackendKind.Pci, AppConfig.GetGpuBackend(), "no marker, no ASUS platform: pci");
            Assert(!sb.BlockOps.ActiveState, "filter detached");
        });

    static void Ebpf_Startup_ReattachFails_ReportsStandard()
        => Scenario(nameof(Ebpf_Startup_ReattachFails_ReportsStandard), sb =>
        {
            // A kernel update dropped bpf from the LSM list: Eco cannot come back, the backend stays.
            AppConfig.Set("gpu_backend", "ebpf");
            sb.WriteBackend("ebpf");
            AppConfig.Set("gpu_mode", "eco");
            sb.BlockOps.FailEnable = true;

            AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.ApplyPendingOnStartup(), "result");
            AssertEqual("standard", AppConfig.GetString("gpu_mode") ?? "", "reports Standard");
            AssertEqual(GpuBackendKind.Ebpf, AppConfig.GetGpuBackend(), "backend kept");
            AssertEqual("ebpf", sb.BackendContent(), "marker kept");
        });

    static void SelectBackend_WriteFails_ReturnsFalse()
        => Scenario(nameof(SelectBackend_WriteFails_ReturnsFalse), sb =>
        {
            // A failed marker save must not report success or touch the config.
            sb.WriteBackend("asus-wmi");
            File.Delete(sb.BackendPath);
            Directory.CreateDirectory(sb.BackendPath);
            try
            { Assert(!GpuBackendChoice.Select(GpuBackendKind.Ebpf), "write failed"); }
            finally { Directory.Delete(sb.BackendPath); }
            Assert(string.IsNullOrEmpty(AppConfig.GetString("gpu_backend")), "config unchanged");
            Assert(GpuBackendChoice.Select(GpuBackendKind.Ebpf), "write ok");
            AssertEqual("ebpf", AppConfig.GetString("gpu_backend"), "config follows the marker");
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

            sb.Controller.OnBackendChanged(GpuBackendKind.Ebpf, GpuBackendKind.AsusWmi);

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

            sb.Controller.OnBackendChanged(GpuBackendKind.AsusWmi, GpuBackendKind.Ebpf);

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

            sb.Controller.OnBackendChanged(GpuBackendKind.Pci, GpuBackendKind.Ebpf);

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
            Directory.CreateDirectory(Path.GetDirectoryName(GpuBootFiles.RecoveryMarkerPath)!);
            File.WriteAllText(GpuBootFiles.RecoveryMarkerPath, "2026-01-01 recovered to Standard");

            Assert(GpuBootFiles.ClearRecovery(), "reports cleared");
            Assert(!File.Exists(GpuBootFiles.RecoveryMarkerPath), "marker gone");
        });

    static void RecoveryMarker_AbsentIsSuccess()
        => Scenario(nameof(RecoveryMarker_AbsentIsSuccess), sb =>
        {
            Assert(GpuBootFiles.ClearRecovery(), "no marker is already clear");
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

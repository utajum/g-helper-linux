// GPU switching paths outside the click matrix: dialog buttons, startup, shutdown,
// Optimized auto switch, persistent Eco, MUX latch and hardware without a MUX.

using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Tests;

public static class GpuSwitchTests
{
    private static string? BootId()
    {
        try { return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(); }
        catch { return null; }
    }

    private static void Wmi(Harness.Sandbox sb, int mux, bool eco)
    {
        AppConfig.Set("gpu_backend", "asus-wmi");
        sb.Wmi.MuxMode = mux;
        sb.Wmi.EcoEnabled = eco;
    }

    private static void Pci(Harness.Sandbox sb)
    {
        AppConfig.Set("gpu_backend", "pci");
        sb.Wmi.MuxMode = 1;
    }

    private static void Optimized(bool onAc, Harness.Sandbox sb)
    {
        AppConfig.Set("gpu_optimized_enabled", 1);
        AppConfig.Set("gpu_auto", 1);
        sb.Power.OnAc = onAc;
    }

    // Fake PCI function under the sandbox /sys/bus/pci/devices.
    private static string Device(Harness.Sandbox sb, string bdf, string vendor, string cls,
        string? bootVga = null, string? runtime = null, bool drm = false, bool driver = false, bool panel = false)
    {
        string dev = Path.Combine(sb.TempRoot, "sys", "bus", "pci", "devices", bdf);
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "vendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(dev, "class"), cls + "\n");
        if (bootVga != null)
            File.WriteAllText(Path.Combine(dev, "boot_vga"), bootVga + "\n");
        if (runtime != null)
        {
            Directory.CreateDirectory(Path.Combine(dev, "power"));
            File.WriteAllText(Path.Combine(dev, "power", "runtime_status"), runtime + "\n");
        }
        if (drm || panel)
            Directory.CreateDirectory(Path.Combine(dev, "drm", "card1"));
        if (panel)
        {
            string conn = Path.Combine(dev, "drm", "card1", "card1-eDP-1");
            Directory.CreateDirectory(conn);
            File.WriteAllText(Path.Combine(conn, "status"), "connected\n");
        }
        if (driver)
            Directory.CreateDirectory(Path.Combine(dev, "driver"));
        LinuxAsusWmi.InvalidateGpuPresenceCache();
        return bdf;
    }

    private static void AmdDgpu(Harness.Sandbox sb, string? runtime, bool drm = true) =>
        Device(sb, "0000:03:00.0", "0x1002", "0x030000", bootVga: "0", runtime: runtime, drm: drm);

    private static void Module(Harness.Sandbox sb, string name, int? refcnt)
    {
        string dir = Path.Combine(sb.TempRoot, "sys", "module", name);
        Directory.CreateDirectory(dir);
        if (refcnt != null)
            File.WriteAllText(Path.Combine(dir, "refcnt"), refcnt + "\n");
        LinuxAsusWmi.InvalidateGpuPresenceCache();
    }

    private static string Saved() => AppConfig.GetString("gpu_mode") ?? "";
    private static bool EcoWritten(Harness.Sandbox sb) => sb.Wmi.Calls.Any(c => c.Method == "SetGpuEco" && c.Arg is true);
    private static bool MuxWritten(Harness.Sandbox sb) => sb.Wmi.Calls.Any(c => c.Method == "SetGpuMuxMode");

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- Switch Now (TryReleaseAndSwitch) ---");

        Harness.Scenario("SwitchNow_Wmi_DriverReleased_EcoApplied", sb =>
        {
            Wmi(sb, 1, false);
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
            Harness.AssertEqual("eco", Saved(), "config");
            Harness.Assert(!sb.ModprobePresent() && !sb.TriggerPresent(), "no block artifacts after live Eco");
        });

        Harness.Scenario("SwitchNow_Wmi_Ultimate_LatchesMuxAndDefersEco", sb =>
        {
            Wmi(sb, 0, false);
            Harness.AssertEqual(GpuSwitchResult.Deferred, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.AssertEqual(1, sb.Wmi.MuxMode, "MUX latched to 1");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while MUX=0");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger for the boot script");
            Harness.Assert(sb.ModprobePresent(), "driver block written");
        });

        Harness.Scenario("SwitchNow_Wmi_UltimateAndEco_EnablesDgpuBeforeMuxLatch", sb =>
        {
            Wmi(sb, 0, true);
            Harness.AssertEqual(GpuSwitchResult.Deferred, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled first");
            Harness.AssertEqual(1, sb.Wmi.MuxMode, "MUX latched to 1");
        });

        Harness.Scenario("SwitchNow_Wmi_EcoWriteThrows_Deferred", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Deferred, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU still on");
            Harness.AssertEqual("eco", Saved(), "Eco kept for next boot");
        });

        Harness.Scenario("SwitchNow_Pci_DriverReleased_BlocksWritten", sb =>
        {
            Pci(sb);
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "PCI Eco blocks written");
            Harness.AssertEqual("eco", Saved(), "config");
            Harness.Assert(!EcoWritten(sb), "PCI backend never writes dgpu_disable");
        });

        Harness.Scenario("SwitchNow_Pci_MuxZero_EcoBlocked", sb =>
        {
            Pci(sb);
            sb.Wmi.MuxMode = 0;
            Harness.AssertEqual(GpuSwitchResult.EcoBlocked, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.Assert(!sb.ModprobePresent() && !sb.TriggerPresent(), "no block artifacts");
        });

        Console.WriteLine();
        Console.WriteLine("--- After Reboot (ScheduleModeForReboot) ---");

        Harness.Scenario("AfterReboot_Wmi_Eco_WritesBlocksOnly", sb =>
        {
            Wmi(sb, 1, false);
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Eco), "result");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "driver block written");
            Harness.Assert(!sb.Wmi.Calls.Any(c => c.Method == "SetGpuEco"), "no live dgpu_disable write");
        });

        Harness.Scenario("AfterReboot_Wmi_Ultimate_LatchesMuxAndFlag", sb =>
        {
            Wmi(sb, 1, false);
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Ultimate), "result");
            Harness.AssertEqual(0, sb.Wmi.MuxMode, "MUX latched to 0");
            Harness.Assert(!sb.ModprobePresent() && !sb.TriggerPresent(), "no block artifacts");
            string? bootId = BootId();
            if (bootId != null)
                Harness.AssertEqual(bootId, AppConfig.GetString("mux_zero_latched_boot_id") ?? "", "latch flag");
        });

        Harness.Scenario("AfterReboot_Wmi_UltimateFromEco_EnablesDgpuFirst", sb =>
        {
            Wmi(sb, 1, true);
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Ultimate), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled before MUX write");
            Harness.AssertEqual(0, sb.Wmi.MuxMode, "MUX latched to 0");
        });

        Harness.Scenario("AfterReboot_Wmi_EcoExitFails_NoMuxWrite_ConfigKept", sb =>
        {
            Wmi(sb, 1, true);
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Ultimate), "result");
            Harness.Assert(!MuxWritten(sb), "MUX not written while dgpu_disable=1");
            Harness.AssertEqual("ultimate", Saved(), "startup retries from config");
        });

        Harness.Scenario("AfterReboot_Pci_NoMux_StandardFromEco_TriggerOnly", sb =>
        {
            Pci(sb);
            sb.Wmi.MuxModeSupported = false;
            sb.WriteBlockArtifacts();
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Standard), "result");
            Harness.AssertEqual("standard", sb.TriggerContent(), "trigger");
            Harness.Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "blocks removed");
            Harness.Assert(!MuxWritten(sb), "no MUX write without MUX hardware");
        });

        Harness.Scenario("DriverActive_ClickEco_DialogThenAfterReboot", sb =>
        {
            Wmi(sb, 1, false);
            sb.WriteFakeNvidiaModule();
            Harness.AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.RequestModeSwitch(GpuMode.Eco), "click");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while driver active");
            Harness.Assert(!sb.ModprobePresent(), "no blocks before the dialog answer");
            Harness.AssertEqual("eco", Saved(), "config");

            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Eco), "After Reboot");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "driver block written");
            Harness.Assert(!EcoWritten(sb), "still no dgpu_disable=1");
        });

        Console.WriteLine();
        Console.WriteLine("--- Busy switch lock ---");

        Harness.Scenario("Busy_SecondRequestDeferred_SwitchNowRefused", sb =>
        {
            Wmi(sb, 1, false);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            sb.Wmi.OnSetGpuEco = () => { entered.Set(); release.Wait(5000); };
            var first = Task.Run(() => sb.Controller.RequestModeSwitch(GpuMode.Eco));
            try
            {
                Harness.Assert(entered.Wait(5000), "first switch is writing dgpu_disable");
                Harness.Assert(sb.Controller.IsSwitchInProgress, "switch in progress");
                Harness.AssertEqual(GpuSwitchResult.Deferred, sb.Controller.RequestModeSwitch(GpuMode.Standard), "second click");
                Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.TryReleaseAndSwitch(), "Switch Now while busy");
            }
            finally
            {
                release.Set();
            }
            Harness.AssertEqual(GpuSwitchResult.Applied, first.Result, "first switch");
            Harness.Assert(!sb.Controller.IsSwitchInProgress, "lock released");
        });

        Console.WriteLine();
        Console.WriteLine("--- Startup (ApplyPendingOnStartup, ASUS-WMI) ---");

        Harness.Scenario("Startup_Wmi_PendingEco_DriverIdle_Applied", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
        });

        Harness.Scenario("Startup_Wmi_PendingEco_DriverActive_WritesBlock", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.WriteFakeNvidiaModule();
            Harness.AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while driver active");
            Harness.Assert(sb.ModprobePresent(), "block written so the next boot can apply Eco");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger");
        });

        Harness.Scenario("Startup_Wmi_PendingEco_MuxZero_LatchesMuxFirst", sb =>
        {
            Wmi(sb, 0, false);
            AppConfig.Set("gpu_mode", "eco");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.AssertEqual(1, sb.Wmi.MuxMode, "MUX latched to 1");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while MUX=0");
        });

        Harness.Scenario("Startup_Wmi_PendingUltimate_FromEco_EnablesDgpuThenMux", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "ultimate");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled");
            Harness.AssertEqual(0, sb.Wmi.MuxMode, "MUX latched to 0");
        });

        Harness.Scenario("Startup_Wmi_PendingStandard_HwEco_EnablesDgpu", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "standard");
            sb.WriteBlockArtifacts();
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled");
            Harness.Assert(!sb.ModprobePresent(), "stale blocks removed");
        });

        Harness.Scenario("Startup_Wmi_PendingOptimized_HwEco_EnablesDgpu", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "optimized");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled");
        });

        Harness.Scenario("Startup_Wmi_BootSafety_MuxZeroAndEco_ForcesDgpuOn", sb =>
        {
            Wmi(sb, 0, true);
            AppConfig.Set("gpu_mode", "ultimate");
            sb.WriteBlockArtifacts();
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dgpu_disable forced to 0");
            Harness.Assert(!sb.ModprobePresent(), "blocks removed");
        });

        Harness.Scenario("Startup_Wmi_NoSavedMode_RemovesStaleBlocks", sb =>
        {
            Wmi(sb, 1, false);
            sb.WriteBlockArtifacts();
            sb.WriteTrigger("eco");
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.ModprobePresent() && !sb.UdevPresent() && !sb.TriggerPresent(), "stale artifacts removed");
        });

        Harness.Scenario("Startup_NoGpu_TouchesNothing", sb =>
        {
            Wmi(sb, 0, true);
            AppConfig.Set("no_gpu", 1);
            AppConfig.Set("gpu_mode", "eco");
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.AssertEqual(0, sb.Wmi.Calls.Count, "no GPU firmware calls");
        });

        Console.WriteLine();
        Console.WriteLine("--- Shutdown (ApplyPendingOnShutdown) ---");

        Harness.Scenario("Shutdown_Wmi_PendingEco_DriverIdle_WritesEco", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.Controller.ApplyPendingOnShutdown();
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
        });

        Harness.Scenario("Shutdown_Wmi_PendingEco_MuxZero_Skips", sb =>
        {
            Wmi(sb, 0, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.Controller.ApplyPendingOnShutdown();
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while MUX=0");
        });

        Harness.Scenario("Shutdown_Wmi_PendingEco_DriverActive_Skips", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.WriteFakeNvidiaModule();
            sb.Controller.ApplyPendingOnShutdown();
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while driver active");
        });

        Harness.Scenario("Shutdown_Wmi_PendingStandard_Skips", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "standard");
            sb.Controller.ApplyPendingOnShutdown();
            Harness.Assert(!EcoWritten(sb), "no Eco write");
        });

        Harness.Scenario("Shutdown_Pci_TouchesNothing", sb =>
        {
            Pci(sb);
            AppConfig.Set("gpu_mode", "eco");
            sb.Controller.ApplyPendingOnShutdown();
            Harness.AssertEqual(0, sb.Wmi.Calls.Count, "no GPU firmware calls");
        });

        Console.WriteLine();
        Console.WriteLine("--- Optimized (AutoGpuSwitch) ---");

        Harness.Scenario("Auto_OptimizedDisabled_NoOp", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_auto", 1);
            sb.Power.OnAc = false;
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            Harness.AssertEqual(0, sb.Wmi.Calls.Count, "no GPU firmware calls");
        });

        Harness.Scenario("Auto_AcFromEco_EnablesDgpu", sb =>
        {
            Wmi(sb, 1, true);
            Optimized(onAc: true, sb);
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.AutoGpuSwitch(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled");
        });

        Harness.Scenario("Auto_BatteryFromStandard_DriverIdle_Eco", sb =>
        {
            Wmi(sb, 1, false);
            Optimized(onAc: false, sb);
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.AutoGpuSwitch(), "result");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
        });

        Harness.Scenario("Auto_Battery_DriverActive_DriverBlocking", sb =>
        {
            Wmi(sb, 1, false);
            Optimized(onAc: false, sb);
            sb.WriteFakeNvidiaModule();
            Harness.AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.AutoGpuSwitch(), "result");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while driver active");
        });

        Harness.Scenario("Auto_Battery_MuxZero_NoOp", sb =>
        {
            Wmi(sb, 0, false);
            Optimized(onAc: false, sb);
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 in Ultimate");
        });

        Harness.Scenario("Auto_Battery_MuxZeroLatchedThisBoot_NoOp", sb =>
        {
            string? bootId = BootId();
            if (bootId == null)
                return;
            Wmi(sb, 1, false);
            Optimized(onAc: false, sb);
            AppConfig.Set("mux_zero_latched_boot_id", bootId);
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 with MUX=0 pending");
        });

        Harness.Scenario("Auto_Pci_NoOp", sb =>
        {
            Pci(sb);
            Optimized(onAc: false, sb);
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.AutoGpuSwitch(), "result");
            Harness.AssertEqual(0, sb.Wmi.Calls.Count, "no GPU firmware calls");
        });

        Harness.Scenario("ClickOptimized_OnBattery_DisablesDgpu", sb =>
        {
            Wmi(sb, 1, false);
            sb.Power.OnAc = false;
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Optimized), "result");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1 on battery");
            Harness.Assert(AppConfig.Is("gpu_auto"), "gpu_auto set");
            Harness.AssertEqual("optimized", Saved(), "config");
        });

        Console.WriteLine();
        Console.WriteLine("--- Current mode and pending reboot (ASUS-WMI) ---");

        Harness.Scenario("CurrentMode_GpuAuto_OptimizedUnlessMuxZero", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_auto", 1);
            Harness.AssertEqual(GpuMode.Optimized, sb.Controller.GetCurrentMode(), "gpu_auto, MUX=1");
            sb.Wmi.MuxMode = 0;
            Harness.AssertEqual(GpuMode.Ultimate, sb.Controller.GetCurrentMode(), "MUX=0 wins");
        });

        Harness.Scenario("Pending_Wmi_UltimateSaved_MuxOne_True", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "ultimate");
            Harness.Assert(sb.Controller.IsPendingReboot(), "MUX change pending");
        });

        Harness.Scenario("Pending_Wmi_StandardSaved_MuxZero_True", sb =>
        {
            Wmi(sb, 0, false);
            AppConfig.Set("gpu_mode", "standard");
            Harness.Assert(sb.Controller.IsPendingReboot(), "MUX change pending");
        });

        Harness.Scenario("Pending_Wmi_EcoSaved_HwEco_False", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "eco");
            Harness.Assert(!sb.Controller.IsPendingReboot(), "nothing pending");
        });

        Console.WriteLine();
        Console.WriteLine("--- Persistent Eco marker ---");

        Harness.Scenario("Persistent_Toggle_WritesAndRemovesMarker", sb =>
        {
            sb.Controller.SetEcoPersistent(true);
            Harness.Assert(File.Exists(GPUModeControl.PersistentTriggerPath), "marker written");
            Harness.Assert(AppConfig.Is("gpu_eco_persistent"), "config on");
            sb.Controller.SetEcoPersistent(false);
            Harness.Assert(!File.Exists(GPUModeControl.PersistentTriggerPath), "marker removed");
            Harness.Assert(!AppConfig.Is("gpu_eco_persistent"), "config off");
        });

        Harness.Scenario("Persistent_ClickStandard_RemovesMarker_KeepsSetting", sb =>
        {
            Wmi(sb, 1, true);
            sb.Controller.SetEcoPersistent(true);
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
            Harness.Assert(!File.Exists(GPUModeControl.PersistentTriggerPath), "boot script must not force Eco");
            Harness.Assert(AppConfig.Is("gpu_eco_persistent"), "user setting kept");
        });

        Harness.Scenario("Persistent_ClickEco_WritesMarker", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_eco_persistent", 1);
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.Assert(File.Exists(GPUModeControl.PersistentTriggerPath), "marker written");
        });

        Harness.Scenario("Persistent_StartupEcoMatches_KeepsBlocks", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "eco");
            sb.Controller.SetEcoPersistent(true);
            sb.WriteBlockArtifacts();
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "blocks kept for the next boot");
        });

        Harness.Scenario("OneShot_StartupEcoMatches_RemovesBlocks", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "eco");
            sb.WriteBlockArtifacts();
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "blocks removed");
        });

        Console.WriteLine();
        Console.WriteLine("--- MUX latched until reboot ---");

        Harness.Scenario("Latch_UltimateThenEco_SameBoot_EcoBlocked", sb =>
        {
            if (BootId() == null)
                return;
            Wmi(sb, 1, false);
            sb.Wmi.MuxLatchedUntilReboot = true;
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.RequestModeSwitch(GpuMode.Ultimate), "Ultimate");
            Harness.AssertEqual(1, sb.Wmi.MuxMode, "readback still 1");
            Harness.AssertEqual(GpuSwitchResult.EcoBlocked, sb.Controller.RequestModeSwitch(GpuMode.Eco), "Eco");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1");
            Harness.Assert(!sb.ModprobePresent() && !sb.TriggerPresent(), "no Eco artifacts for a MUX=0 boot");
        });

        Harness.Scenario("Latch_UltimateToStandardAndBack_UsesLatchedMux", sb =>
        {
            Wmi(sb, 0, false);
            sb.Wmi.MuxLatchedUntilReboot = true;
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.RequestModeSwitch(GpuMode.Standard), "Standard");
            Harness.AssertEqual(1, sb.Wmi.PendingMux, "MUX 1 latched");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.RequestModeSwitch(GpuMode.Ultimate), "Ultimate again");
            Harness.AssertEqual(0, sb.Wmi.PendingMux, "MUX 0 latched again");
            Harness.AssertEqual("ultimate", Saved(), "config");
        });

        Console.WriteLine();
        Console.WriteLine("--- No MUX hardware (raw WMI, Lenovo, non-ASUS) ---");

        Harness.Scenario("NoMux_EcoThenStandard_LiveWithoutMuxWrites", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.MuxModeSupported = false;
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "Eco");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Standard), "Standard");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dgpu_disable=0");
            Harness.Assert(!MuxWritten(sb), "no MUX write");
        });

        Harness.Scenario("NoMux_StartupPendingEco_Applied", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.MuxModeSupported = false;
            AppConfig.Set("gpu_mode", "eco");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
            Harness.Assert(!MuxWritten(sb), "no MUX write");
        });

        Harness.Scenario("NoMux_AfterRebootEco_WritesBlocks", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.MuxModeSupported = false;
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Eco), "result");
            Harness.Assert(sb.ModprobePresent(), "driver block written");
            Harness.Assert(!MuxWritten(sb), "no MUX write");
        });

        Harness.Scenario("NoMux_CurrentMode_EcoAndOptimized", sb =>
        {
            Wmi(sb, 1, true);
            sb.Wmi.MuxModeSupported = false;
            Harness.AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(), "dgpu_disable=1");
            AppConfig.Set("gpu_auto", 1);
            Harness.AssertEqual(GpuMode.Optimized, sb.Controller.GetCurrentMode(), "gpu_auto");
        });

        Console.WriteLine();
        Console.WriteLine("--- dGPU driver detection (IsDgpuDriverActive) ---");

        Harness.Scenario("Driver_NoModulesNoAmd_Idle", sb =>
            Harness.Assert(!sb.Controller.IsDgpuDriverActive(), "idle"));

        Harness.Scenario("Driver_NvidiaDrmInUse_Active", sb =>
        {
            Module(sb, "nvidia", 0);
            Module(sb, "nvidia_drm", 1);
            Harness.Assert(sb.Controller.IsDgpuDriverActive(), "nvidia_drm refcnt 1");
        });

        Harness.Scenario("Driver_NvidiaUvmInUse_Active", sb =>
        {
            Module(sb, "nvidia", 0);
            Module(sb, "nvidia_uvm", 2);
            Harness.Assert(sb.Controller.IsDgpuDriverActive(), "CUDA holds nvidia_uvm");
        });

        Harness.Scenario("Driver_NvidiaModesetInUse_Active", sb =>
        {
            Module(sb, "nvidia", 0);
            Module(sb, "nvidia_modeset", 1);
            Harness.Assert(sb.Controller.IsDgpuDriverActive(), "nvidia_modeset refcnt 1");
        });

        Harness.Scenario("Driver_NvidiaRefcntUnreadable_AssumedActive", sb =>
        {
            Module(sb, "nvidia", null);
            Harness.Assert(sb.Controller.IsDgpuDriverActive(), "unreadable refcnt counts as active");
        });

        Harness.Scenario("Driver_AmdDgpuActive_Active", sb =>
        {
            AmdDgpu(sb, "active");
            Harness.Assert(sb.Controller.IsDgpuDriverActive(), "runtime_status=active");
        });

        Harness.Scenario("Driver_AmdDgpuSuspended_Idle", sb =>
        {
            AmdDgpu(sb, "suspended");
            Harness.Assert(!sb.Controller.IsDgpuDriverActive(), "runtime_status=suspended");
        });

        Harness.Scenario("Driver_AmdDgpuNoRuntimeStatus_AssumedActive", sb =>
        {
            AmdDgpu(sb, null);
            Harness.Assert(sb.Controller.IsDgpuDriverActive(), "unknown status counts as active");
        });

        Harness.Scenario("Driver_AmdIgpuOnly_Idle", sb =>
        {
            Device(sb, "0000:06:00.0", "0x1002", "0x030000", bootVga: "1", runtime: "active", drm: true);
            Harness.Assert(!sb.Controller.IsDgpuDriverActive(), "boot GPU is not the dGPU");
        });

        Harness.Scenario("Driver_AmdDgpuWithoutDrm_Idle", sb =>
        {
            AmdDgpu(sb, "active", drm: false);
            Harness.Assert(!sb.Controller.IsDgpuDriverActive(), "no DRM node, no amdgpu driver");
        });

        Harness.Scenario("Driver_AmdActive_ClickEco_DriverBlocking", sb =>
        {
            Wmi(sb, 1, false);
            AmdDgpu(sb, "active");
            Harness.AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1 while amdgpu is active");
        });

        Harness.Scenario("Driver_AmdSuspended_ClickEco_Applied", sb =>
        {
            Wmi(sb, 1, false);
            AmdDgpu(sb, "suspended");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.Assert(sb.Wmi.EcoEnabled, "dgpu_disable=1");
        });

        Harness.Scenario("Driver_Pci_AmdSuspended_ClickEco_Applied", sb =>
        {
            Pci(sb);
            AmdDgpu(sb, "suspended");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "PCI Eco blocks written");
        });

        Harness.Scenario("Driver_Pci_AmdActive_ClickEco_DriverBlocking", sb =>
        {
            Pci(sb);
            AmdDgpu(sb, "active");
            Harness.AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.Assert(!sb.ModprobePresent(), "no blocks before the dialog answer");
        });

        Console.WriteLine();
        Console.WriteLine("--- dGPU runtime status ---");

        Harness.Scenario("Runtime_NoDevices_Null", sb =>
            Harness.AssertEqual<string?>(null, GPUModeControl.DgpuRuntimeStatus(), "status"));

        Harness.Scenario("Runtime_NvidiaDgpu_Status", sb =>
        {
            Device(sb, "0000:01:00.0", "0x10de", "0x030000", runtime: "suspended");
            Harness.AssertEqual<string?>("suspended", GPUModeControl.DgpuRuntimeStatus(), "status");
        });

        Harness.Scenario("Runtime_Nvidia3dController_Status", sb =>
        {
            Device(sb, "0000:01:00.0", "0x10de", "0x030200", runtime: "active");
            Harness.AssertEqual<string?>("active", GPUModeControl.DgpuRuntimeStatus(), "status");
        });

        Harness.Scenario("Runtime_AmdDgpu_Status", sb =>
        {
            Device(sb, "0000:06:00.0", "0x1002", "0x030000", bootVga: "1", runtime: "active");
            Device(sb, "0000:03:00.0", "0x1002", "0x030000", bootVga: "0", runtime: "suspended");
            Harness.AssertEqual<string?>("suspended", GPUModeControl.DgpuRuntimeStatus(), "status of the non-boot GPU");
        });

        Harness.Scenario("Runtime_OnlyBootGpuAudioAndIntel_Null", sb =>
        {
            Device(sb, "0000:06:00.0", "0x1002", "0x030000", bootVga: "1", runtime: "active");
            Device(sb, "0000:01:00.1", "0x10de", "0x040300", runtime: "active");
            Device(sb, "0000:00:02.0", "0x8086", "0x030000", runtime: "active");
            Harness.AssertEqual<string?>(null, GPUModeControl.DgpuRuntimeStatus(), "status");
        });

        Console.WriteLine();
        Console.WriteLine("--- Driverless dGPU recovery ---");

        Harness.Scenario("Driverless_NvidiaOnBus_ClickStandard_RunsEnable", sb =>
        {
            Wmi(sb, 1, false);
            Device(sb, "0000:01:00.0", "0x10de", "0x030000");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
            Harness.Assert(sb.Wmi.Calls.Any(c => c.Method == "SetGpuEco" && c.Arg is false), "enable path ran");
            Harness.AssertEqual("standard", Saved(), "config");
        });

        Harness.Scenario("Driverless_NvidiaBound_ClickStandard_AlreadySet", sb =>
        {
            Wmi(sb, 1, false);
            Device(sb, "0000:01:00.0", "0x10de", "0x030000", driver: true);
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
            Harness.Assert(!sb.Wmi.Calls.Any(c => c.Method == "SetGpuEco"), "no enable path");
        });

        Harness.Scenario("Driverless_NvidiaAudioOnly_AlreadySet", sb =>
        {
            Wmi(sb, 1, false);
            Device(sb, "0000:01:00.1", "0x10de", "0x040300");
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
        });

        Harness.Scenario("Driverless_AmdDgpu_ClickStandard_RunsEnable", sb =>
        {
            Wmi(sb, 1, false);
            Device(sb, "0000:03:00.0", "0x1002", "0x030000", bootVga: "0");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
        });

        Harness.Scenario("Driverless_AmdDrivingPanel_NotTheDgpu", sb =>
        {
            Wmi(sb, 1, false);
            Device(sb, "0000:03:00.0", "0x1002", "0x030000", bootVga: "0", panel: true);
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.RequestModeSwitch(GpuMode.Standard), "GPU with the eDP panel is the iGPU");
        });

        Harness.Scenario("Driverless_AmdBootVga_NotTheDgpu", sb =>
        {
            Wmi(sb, 1, false);
            Device(sb, "0000:06:00.0", "0x1002", "0x030000", bootVga: "1");
            Harness.AssertEqual(GpuSwitchResult.AlreadySet, sb.Controller.RequestModeSwitch(GpuMode.Standard), "boot GPU is the iGPU");
        });

        Console.WriteLine();
        Console.WriteLine("--- Failed hardware writes ---");

        Harness.Scenario("Fail_EcoReadbackStaysZero_FailedConfigKept", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "standard");
            sb.Wmi.IgnoreSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.AssertEqual("standard", Saved(), "config not changed");
        });

        Harness.Scenario("Fail_EcoExitDoesNotTake_NoMuxWrite", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "eco");
            sb.Wmi.IgnoreSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.RequestModeSwitch(GpuMode.Ultimate), "result");
            Harness.Assert(!MuxWritten(sb), "no MUX write while dgpu_disable=1");
            Harness.AssertEqual("eco", Saved(), "config not changed");
        });

        Harness.Scenario("Fail_EcoExitThrows_NoMuxWrite", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "eco");
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.RequestModeSwitch(GpuMode.Ultimate), "result");
            Harness.Assert(!MuxWritten(sb), "no MUX write");
            Harness.AssertEqual("eco", Saved(), "config not changed");
        });

        Harness.Scenario("Fail_MuxWriteThrows_FailedNoLatch", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "standard");
            sb.Wmi.ThrowOnSetGpuMuxMode = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.RequestModeSwitch(GpuMode.Ultimate), "result");
            Harness.AssertEqual("standard", Saved(), "config not changed");
            Harness.AssertEqual("", AppConfig.GetString("mux_zero_latched_boot_id") ?? "", "no MUX=0 latch flag");
        });

        Harness.Scenario("Fail_MuxWriteThrows_LaterEcoStillAllowed", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.ThrowOnSetGpuMuxMode = true;
            sb.Controller.RequestModeSwitch(GpuMode.Ultimate);
            sb.Wmi.ThrowOnSetGpuMuxMode = false;
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "Eco after a failed MUX write");
        });

        Harness.Scenario("Fail_AfterRebootUltimateMuxThrows_NoLatchFlag", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.ThrowOnSetGpuMuxMode = true;
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Ultimate), "result");
            Harness.AssertEqual("ultimate", Saved(), "startup retries from config");
            Harness.AssertEqual("", AppConfig.GetString("mux_zero_latched_boot_id") ?? "", "no MUX=0 latch flag");
        });

        Harness.Scenario("Fail_SwitchNowReadbackStaysZero_Deferred", sb =>
        {
            Wmi(sb, 1, false);
            sb.Wmi.IgnoreSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Deferred, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.AssertEqual("eco", Saved(), "Eco kept for next boot");
        });

        Harness.Scenario("Fail_SwitchNowUltimateMuxThrows_NoEcoBlocks", sb =>
        {
            Wmi(sb, 0, false);
            sb.Wmi.ThrowOnSetGpuMuxMode = true;
            Harness.AssertEqual(GpuSwitchResult.Deferred, sb.Controller.TryReleaseAndSwitch(), "result");
            Harness.Assert(!sb.ModprobePresent() && !sb.UdevPresent(), "no Eco blocks for a MUX=0 boot");
            Harness.Assert(!EcoWritten(sb), "no dgpu_disable=1");
        });

        Harness.Scenario("Fail_StartupEcoReadbackStaysZero_Failed", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.Wmi.IgnoreSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.ApplyPendingOnStartup(), "result");
        });

        Harness.Scenario("Fail_StartupEcoWriteThrows_Failed", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.ApplyPendingOnStartup(), "result");
        });

        Harness.Scenario("Fail_StartupMuxWriteThrows_Failed", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "ultimate");
            sb.Wmi.ThrowOnSetGpuMuxMode = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.ApplyPendingOnStartup(), "result");
        });

        Harness.Scenario("Fail_StartupEcoExitThrows_NoMuxWrite", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "ultimate");
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!MuxWritten(sb), "no MUX write while dgpu_disable=1");
        });

        Harness.Scenario("Fail_StartupEnableThrows_Failed", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "standard");
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.ApplyPendingOnStartup(), "result");
        });

        Harness.Scenario("Fail_AutoAcEnableThrows_Failed", sb =>
        {
            Wmi(sb, 1, true);
            Optimized(onAc: true, sb);
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.AutoGpuSwitch(), "result");
        });

        Harness.Scenario("Fail_AutoBatteryReadbackStaysZero_Failed", sb =>
        {
            Wmi(sb, 1, false);
            Optimized(onAc: false, sb);
            sb.Wmi.IgnoreSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.AutoGpuSwitch(), "result");
        });

        Harness.Scenario("Fail_ShutdownEcoThrows_NoCrash", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("gpu_mode", "eco");
            sb.Wmi.ThrowOnSetGpuEco = true;
            sb.Controller.ApplyPendingOnShutdown();
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU still on");
        });

        Harness.Scenario("Fail_EnableThrows_Failed", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "eco");
            sb.Wmi.ThrowOnSetGpuEco = true;
            Harness.AssertEqual(GpuSwitchResult.Failed, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
            Harness.AssertEqual("eco", Saved(), "config not changed");
        });

        Console.WriteLine();
        Console.WriteLine("--- PCI backend: pending switch and callbacks ---");

        Harness.Scenario("PciPending_EcoScheduled_ClickStandard_LiveUndo", sb =>
        {
            Pci(sb);
            sb.WriteBlockArtifacts();
            sb.WriteTrigger("eco");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
            Harness.Assert(!sb.TriggerPresent() && !sb.ModprobePresent() && !sb.UdevPresent(), "pending Eco undone");
        });

        Harness.Scenario("PciPending_EcoScheduled_ClickEco_StillPending", sb =>
        {
            Pci(sb);
            sb.WriteBlockArtifacts();
            sb.WriteTrigger("eco");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger");
            Harness.Assert(sb.ModprobePresent(), "blocks kept");
        });

        Harness.Scenario("PciPending_StandardScheduled_ClickEco_LiveEco", sb =>
        {
            Pci(sb);
            sb.WriteTrigger("standard");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.RequestModeSwitch(GpuMode.Eco), "result");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "blocks written");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger now eco");
        });

        Harness.Scenario("PciPending_StandardScheduled_ClickStandard_StillPending", sb =>
        {
            Pci(sb);
            sb.WriteTrigger("standard");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.RequestModeSwitch(GpuMode.Standard), "result");
            Harness.AssertEqual("standard", sb.TriggerContent(), "trigger");
            Harness.Assert(sb.Controller.IsPendingReboot(), "pending reboot");
        });

        Harness.Scenario("Pci_DriverActive_DialogThenAfterReboot", sb =>
        {
            Pci(sb);
            sb.WriteFakeNvidiaModule();
            Harness.AssertEqual(GpuSwitchResult.DriverBlocking, sb.Controller.RequestModeSwitch(GpuMode.Eco), "click");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Eco), "After Reboot");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "blocks written");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger");
            Harness.Assert(sb.Controller.IsPendingReboot(), "pending reboot");
            Harness.AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(), "UI shows Eco");
        });

        Harness.Scenario("Pci_CurrentMode_IgnoresMuxAndFirmwareEco", sb =>
        {
            Pci(sb);
            sb.Wmi.MuxMode = 0;
            sb.Wmi.EcoEnabled = true;
            AppConfig.Set("gpu_auto", 1);
            Harness.AssertEqual(GpuMode.Standard, sb.Controller.GetCurrentMode(), "no blocks = Standard");
        });

        Harness.Scenario("Pci_SwitchNow_InvokesLiveTransitionCallback", sb =>
        {
            Pci(sb);
            int calls = 0;
            GPUModeControl.OnLivePciTransition = () => calls++;
            try
            {
                Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.TryReleaseAndSwitch(), "result");
                Harness.AssertEqual(1, calls, "callback calls");
            }
            finally
            {
                GPUModeControl.OnLivePciTransition = null;
            }
        });

        Harness.Scenario("PushBackendMarker_KeepsBlocksAndTrigger", sb =>
        {
            sb.WriteBlockArtifacts();
            sb.WriteTrigger("eco");
            sb.Controller.PushBackendMarker("pci");
            Harness.Assert(sb.ModprobePresent() && sb.UdevPresent(), "blocks kept");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger kept");
        });

        Console.WriteLine();
        Console.WriteLine("--- After Reboot: more targets ---");

        Harness.Scenario("AfterReboot_Wmi_OptimizedFromUltimate_LatchesMuxOne", sb =>
        {
            Wmi(sb, 0, false);
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Optimized), "result");
            Harness.AssertEqual(1, sb.Wmi.MuxMode, "MUX latched to 1");
            Harness.AssertEqual("optimized", Saved(), "config");
        });

        Harness.Scenario("AfterReboot_Wmi_Standard_RemovesStaleBlocks", sb =>
        {
            Wmi(sb, 1, false);
            sb.WriteBlockArtifacts();
            sb.WriteTrigger("eco");
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Standard), "result");
            Harness.Assert(!sb.ModprobePresent() && !sb.UdevPresent() && !sb.TriggerPresent(), "stale Eco artifacts removed");
            Harness.Assert(!MuxWritten(sb), "no MUX write");
        });

        Harness.Scenario("AfterReboot_Wmi_EcoTwice_SameArtifacts", sb =>
        {
            Wmi(sb, 1, false);
            sb.Controller.ScheduleModeForReboot(GpuMode.Eco);
            Harness.AssertEqual(GpuSwitchResult.RebootRequired, sb.Controller.ScheduleModeForReboot(GpuMode.Eco), "second time");
            Harness.AssertEqual("eco", sb.TriggerContent(), "trigger");
        });

        Console.WriteLine();
        Console.WriteLine("--- Mode changes that touch gpu_auto ---");

        Harness.Scenario("Optimized_ThenStandard_ClearsGpuAuto", sb =>
        {
            Wmi(sb, 1, false);
            sb.Controller.RequestModeSwitch(GpuMode.Optimized);
            Harness.Assert(AppConfig.Is("gpu_auto"), "gpu_auto set");
            sb.Controller.RequestModeSwitch(GpuMode.Standard);
            Harness.Assert(!AppConfig.Is("gpu_auto"), "gpu_auto cleared");
            Harness.AssertEqual(GpuMode.Standard, sb.Controller.GetCurrentMode(), "UI shows Standard");
        });

        Harness.Scenario("Optimized_ThenEco_ClearsGpuAuto", sb =>
        {
            Wmi(sb, 1, false);
            sb.Controller.RequestModeSwitch(GpuMode.Optimized);
            sb.Controller.RequestModeSwitch(GpuMode.Eco);
            Harness.Assert(!AppConfig.Is("gpu_auto"), "gpu_auto cleared");
            Harness.AssertEqual(GpuMode.Eco, sb.Controller.GetCurrentMode(), "UI shows Eco");
        });

        Harness.Scenario("Pending_Wmi_OptimizedSaved_MuxZero_True", sb =>
        {
            Wmi(sb, 0, false);
            AppConfig.Set("gpu_mode", "optimized");
            Harness.Assert(sb.Controller.IsPendingReboot(), "MUX change pending");
        });

        Harness.Scenario("Pending_Wmi_NothingSaved_False", sb =>
        {
            Wmi(sb, 0, true);
            Harness.Assert(!sb.Controller.IsPendingReboot(), "nothing pending");
        });

        Harness.Scenario("Startup_UnknownSavedMode_TreatedAsStandard", sb =>
        {
            Wmi(sb, 1, true);
            AppConfig.Set("gpu_mode", "turbo");
            Harness.AssertEqual(GpuSwitchResult.Applied, sb.Controller.ApplyPendingOnStartup(), "result");
            Harness.Assert(!sb.Wmi.EcoEnabled, "dGPU enabled");
        });

        Console.WriteLine();
        Console.WriteLine("--- GPU backend setting ---");

        Harness.Scenario("Backend_Values", _ =>
        {
            foreach (var (raw, want) in new[] { ("pci", "pci"), (" PCI ", "pci"), ("asus-wmi", "asus-wmi"), ("ASUS-WMI", "asus-wmi"), ("garbage", "asus-wmi"), ("", "asus-wmi") })
            {
                AppConfig.Set("gpu_backend", raw);
                Harness.AssertEqual(want, AppConfig.GetGpuBackend(), $"gpu_backend '{raw}'");
                Harness.AssertEqual(want == "pci", AppConfig.IsPciGpuBackend(), $"IsPciGpuBackend '{raw}'");
            }
        });

        Harness.Scenario("Backend_Unset_AsusWmi", _ =>
            Harness.AssertEqual("asus-wmi", AppConfig.GetGpuBackend(), "default"));

        Harness.Scenario("Backend_AutoDetectKeepsUserChoice", _ =>
        {
            foreach (var choice in new[] { "asus-wmi", "pci" })
            {
                AppConfig.Set("gpu_backend", choice);
                AppConfig.AutoDetectGpuBackendIfUnset();
                Harness.AssertEqual(choice, AppConfig.GetGpuBackend(), "user choice kept");
            }
        });

        Console.WriteLine();
        Console.WriteLine("--- Keyboard GPU-mode color ---");

        Harness.Scenario("AuraGpuColor_RefreshedOnAppliedAndAlreadySet", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("aura_mode", (int)USB.AuraMode.GpuMode);
            USB.CustomRgb.Calls = 0;
            sb.Controller.RequestModeSwitch(GpuMode.Eco);
            Harness.AssertEqual(1, USB.CustomRgb.Calls, "after Applied");
            sb.Controller.RequestModeSwitch(GpuMode.Eco);
            Harness.AssertEqual(2, USB.CustomRgb.Calls, "after AlreadySet");
        });

        Harness.Scenario("AuraGpuColor_NotRefreshedWhenNothingChanged", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("aura_mode", (int)USB.AuraMode.GpuMode);
            USB.CustomRgb.Calls = 0;
            sb.WriteFakeNvidiaModule();
            sb.Controller.RequestModeSwitch(GpuMode.Eco);
            Harness.AssertEqual(0, USB.CustomRgb.Calls, "after DriverBlocking");
            sb.Wmi.ThrowOnSetGpuMuxMode = true;
            sb.Controller.RequestModeSwitch(GpuMode.Ultimate);
            Harness.AssertEqual(0, USB.CustomRgb.Calls, "after Failed");
        });

        Harness.Scenario("AuraGpuColor_OtherModeUntouched", sb =>
        {
            Wmi(sb, 1, false);
            AppConfig.Set("aura_mode", (int)USB.AuraMode.Static);
            USB.CustomRgb.Calls = 0;
            sb.Controller.RequestModeSwitch(GpuMode.Eco);
            Harness.AssertEqual(0, USB.CustomRgb.Calls, "static color stays");
        });
    }
}

// DgpuIdleBackoff and nvml-temp parsing tests. Pure functions, no sandbox state.

using GHelper.Linux.Gpu.NVidia;

namespace GHelper.Linux.Tests;

public static class DgpuIdleBackoffTests
{
    private const long T0 = 1_000_000;

    private static long At(double seconds) => T0 + (long)(seconds * 1000);

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- DgpuIdleBackoff ---");

        Harness.Scenario("Backoff_Startup_PollsFirst", _ =>
        {
            DgpuIdleBackoff.Reset();
            Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(0)), "first call polls");
            Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(59)), "polls through first window");
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(60)), "quiet after first window");
        });

        Harness.Scenario("Backoff_IdleGpu_SuspendsDuringQuietWindow", _ =>
        {
            DgpuIdleBackoff.Reset();
            DgpuIdleBackoff.ShouldSkip(At(0));
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(60)), "quiet at 60s");
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(80)), "still quiet at 80s");
            DgpuIdleBackoff.NoteSuspended(At(81));
            DgpuIdleBackoff.NoteSuspended(At(300));
            Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(301)), "polls right after wake");
            Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(359)), "polls until 360s");
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(360)), "quiet after base poll window");
        });

        Harness.Scenario("Backoff_BusyReadings_NeverEnterQuietWindow", _ =>
        {
            DgpuIdleBackoff.Reset();
            for (int t = 0; t <= 900; t += 3)
            {
                Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(t)), $"polls at {t}s while busy");
                DgpuIdleBackoff.NoteBusy(At(t));
            }
        });

        Harness.Scenario("Backoff_IdleAfterBusy_QuietWindowAfterPollWindow", _ =>
        {
            DgpuIdleBackoff.Reset();
            DgpuIdleBackoff.ShouldSkip(At(0));
            DgpuIdleBackoff.NoteBusy(At(100));
            Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(159)), "still polling 59s after last busy");
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(160)), "quiet 60s after last busy");
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(184)), "quiet until 185s");
        });

        Harness.Scenario("Backoff_HeldAwake_PollWindowDoublesToCap", _ =>
        {
            // dGPU never suspends and reports util=0 (display or idle CUDA context).
            DgpuIdleBackoff.Reset();
            DgpuIdleBackoff.ShouldSkip(At(0));
            double t = 60;
            long expected = DgpuIdleBackoff.BasePollWindowMs;
            for (int i = 0; i < 6; i++)
            {
                Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(t)), $"quiet window {i} starts");
                t += DgpuIdleBackoff.QuietWindowMs / 1000.0;
                Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(t)), $"quiet window {i} ends awake");
                expected = Math.Min(expected * 2, DgpuIdleBackoff.MaxPollWindowMs);
                Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(t + expected / 1000.0 - 1)), $"poll window {i} is {expected} ms");
                t += expected / 1000.0;
            }
            Harness.AssertEqual(DgpuIdleBackoff.MaxPollWindowMs, expected, "poll window capped");
        });

        Harness.Scenario("Backoff_SuspendResetsGrownPollWindow", _ =>
        {
            DgpuIdleBackoff.Reset();
            DgpuIdleBackoff.ShouldSkip(At(0));
            DgpuIdleBackoff.ShouldSkip(At(60));
            DgpuIdleBackoff.ShouldSkip(At(85)); // held awake: window grows to 120 s
            DgpuIdleBackoff.NoteSuspended(At(300));
            Harness.Assert(!DgpuIdleBackoff.ShouldSkip(At(359)), "polls until base window ends");
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(360)), "back to base poll window");
        });

        Harness.Scenario("Backoff_BusyDuringQuietWindow_DoesNotCutItShort", _ =>
        {
            DgpuIdleBackoff.Reset();
            DgpuIdleBackoff.ShouldSkip(At(0));
            DgpuIdleBackoff.ShouldSkip(At(60));
            DgpuIdleBackoff.NoteBusy(At(70));
            Harness.Assert(DgpuIdleBackoff.ShouldSkip(At(80)), "quiet window kept");
        });

        Harness.Scenario("ParseNvmlTemp_IdleOutput", _ =>
        {
            int temp = LinuxNvidiaGpuControl.ParseNvmlTemp(
                "temp=41 util=0 clock=1890 mem-clock=8001 pstate=0 power=16547 throttle=0x1\n", out bool busy);
            Harness.AssertEqual(41, temp, "temp");
            Harness.Assert(!busy, "util=0 is not busy");
        });

        Harness.Scenario("ParseNvmlTemp_BusyOutput", _ =>
        {
            int temp = LinuxNvidiaGpuControl.ParseNvmlTemp("temp=63 util=72 clock=2400\n", out bool busy);
            Harness.AssertEqual(63, temp, "temp");
            Harness.Assert(busy, "util=72 is busy");
        });

        Harness.Scenario("ParseNvmlTemp_UtilLastToken", _ =>
        {
            LinuxNvidiaGpuControl.ParseNvmlTemp("temp=50 util=5\n", out bool busy);
            Harness.Assert(busy, "trailing newline after util");
        });

        Harness.Scenario("ParseNvmlTemp_MissingTemp", _ =>
        {
            int temp = LinuxNvidiaGpuControl.ParseNvmlTemp("util=0\n", out bool busy);
            Harness.AssertEqual(-1, temp, "temp absent");
            Harness.Assert(!busy, "not busy");
        });

        DgpuIdleBackoff.Reset();
    }
}

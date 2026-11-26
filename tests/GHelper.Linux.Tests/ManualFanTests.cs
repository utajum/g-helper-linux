// Manual fan follower math tests. Pure functions, no sandbox state.

using GHelper.Linux.Fan;

namespace GHelper.Linux.Tests;

public static class ManualFanTests
{
    // 40C->20%, 50C->40%, then flat 40% to the end
    private static byte[] Curve() => new byte[]
    {
        30, 40, 50, 60, 70, 80, 90, 100,
        10, 20, 40, 40, 40, 40, 40, 40,
    };

    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- ManualFanMath ---");

        Harness.Scenario("Interpolate_BelowFirstPoint_UsesFirstPwm", _ =>
            Harness.AssertEqual(10, ManualFanMath.Interpolate(Curve(), 20), "duty at 20C"));

        Harness.Scenario("Interpolate_AboveLastPoint_UsesLastPwm", _ =>
            Harness.AssertEqual(40, ManualFanMath.Interpolate(Curve(), 120), "duty at 120C"));

        Harness.Scenario("Interpolate_ExactPoint", _ =>
            Harness.AssertEqual(20, ManualFanMath.Interpolate(Curve(), 40), "duty at 40C"));

        Harness.Scenario("Interpolate_Midpoint_IsLinear", _ =>
            Harness.AssertEqual(30, ManualFanMath.Interpolate(Curve(), 45), "duty at 45C"));

        Harness.Scenario("Interpolate_DuplicateTempPoints_NoDivByZero", _ =>
        {
            var c = new byte[] { 30, 40, 40, 60, 70, 80, 90, 100, 10, 20, 35, 40, 40, 40, 40, 40 };
            // exact hit resolves in the first segment
            Harness.AssertEqual(20, ManualFanMath.Interpolate(c, 40), "duty at duplicated 40C");
            // just past the duplicate uses the second segment
            Harness.AssertEqual(35, ManualFanMath.Interpolate(c, 41), "duty at 41C");
        });

        Harness.Scenario("Interpolate_WrongLength_ReturnsMinusOne", _ =>
            Harness.AssertEqual(-1, ManualFanMath.Interpolate(new byte[3], 50), "bad curve"));

        Harness.Scenario("ApplyShift_ZeroFollowsOwn", _ =>
            Harness.AssertEqual(50.0, ManualFanMath.ApplyShift(50, 80, 0), "shift 0"));

        Harness.Scenario("ApplyShift_HundredFollowsMax", _ =>
            Harness.AssertEqual(80.0, ManualFanMath.ApplyShift(50, 80, 100), "shift 100"));

        Harness.Scenario("ApplyShift_FiftyIsHalfway", _ =>
            Harness.AssertEqual(65.0, ManualFanMath.ApplyShift(50, 80, 50), "shift 50"));

        Harness.Scenario("ApplyShift_OwnHotterThanOther_NoChange", _ =>
            Harness.AssertEqual(80.0, ManualFanMath.ApplyShift(80, 50, 50), "own is max"));

        Harness.Scenario("ApplyAvg_Blend", _ =>
        {
            Harness.AssertEqual(40.0, ManualFanMath.ApplyAvg(40, 90, 0), "avg 0 = cpu");
            Harness.AssertEqual(90.0, ManualFanMath.ApplyAvg(40, 90, 100), "avg 100 = gpu");
            Harness.AssertEqual(65.0, ManualFanMath.ApplyAvg(40, 90, 50), "avg 50 = mid");
        });

        Harness.Scenario("ClampDuty_ZeroStaysOff", _ =>
            Harness.AssertEqual(0, ManualFanMath.ClampDuty(0), "off stays off"));

        Harness.Scenario("ClampDuty_FloorTwenty_CapHundred", _ =>
        {
            Harness.AssertEqual(20, ManualFanMath.ClampDuty(1), "1 -> 20");
            Harness.AssertEqual(20, ManualFanMath.ClampDuty(19), "19 -> 20");
            Harness.AssertEqual(55, ManualFanMath.ClampDuty(55), "55 passes");
            Harness.AssertEqual(100, ManualFanMath.ClampDuty(150), "150 -> 100");
        });

        Harness.Scenario("ToDuty255_Scale", _ =>
        {
            Harness.AssertEqual(0, ManualFanMath.ToDuty255(0), "0%");
            Harness.AssertEqual(127, ManualFanMath.ToDuty255(50), "50%");
            Harness.AssertEqual(255, ManualFanMath.ToDuty255(100), "100%");
        });

        // duty bytes of the MyASUS fan test (AsusSystemDiagnosis.exe)
        Harness.Scenario("ToDuty255_MatchesMyAsusFanTest", _ =>
        {
            Harness.AssertEqual(0x4C, ManualFanMath.ToDuty255(30), "30%");
            Harness.AssertEqual(0x59, ManualFanMath.ToDuty255(35), "35%");
            Harness.AssertEqual(0x99, ManualFanMath.ToDuty255(60), "60%");
            Harness.AssertEqual(0xFF, ManualFanMath.ToDuty255(100), "100%");
        });

        Harness.Scenario("Average_EmptyIsMinusOne", _ =>
            Harness.AssertEqual(-1.0, ManualFanMath.Average(new Queue<int>()), "empty window"));

        Harness.Scenario("Average_Rolling", _ =>
        {
            var q = new Queue<int>(new[] { 40, 50, 60 });
            Harness.AssertEqual(50.0, ManualFanMath.Average(q), "avg of 40,50,60");
        });

        Console.WriteLine();
        Console.WriteLine("--- ManualFanPolicy ---");

        var own = new FanBlend(1, 0, -1);

        Harness.Scenario("Policy_FanFollowsOwnSensor", _ =>
        {
            var step = new ManualFanPolicy().Next(45, 50, 2, own, _ => Curve());
            Harness.AssertEqual("30,40", string.Join(",", step.Duty!), "cpu 45C, gpu 50C");
        });

        Harness.Scenario("Policy_GpuAsleep_GpuFanFollowsCpu", _ =>
        {
            var step = new ManualFanPolicy().Next(45, 0, 2, own, _ => Curve());
            Harness.AssertEqual("30,30", string.Join(",", step.Duty!), "gpu temp 0");
        });

        Harness.Scenario("Policy_RollingWindow", _ =>
        {
            var p = new ManualFanPolicy();
            p.Next(40, 0, 1, new FanBlend(2, 0, -1), _ => Curve());
            var step = p.Next(50, 0, 1, new FanBlend(2, 0, -1), _ => Curve());
            Harness.AssertEqual(30, step.Duty![0], "avg of 40C and 50C = 45C");
        });

        Harness.Scenario("Policy_OverheatFullUntilBothUnder90", _ =>
        {
            var p = new ManualFanPolicy();
            Harness.AssertEqual(100, p.Next(95, 50, 1, own, _ => Curve()).Duty![0], "95C on");
            Harness.AssertEqual(100, p.Next(60, 92, 1, own, _ => Curve()).Duty![0], "gpu 92C holds");
            Harness.AssertEqual(20, p.Next(40, 89, 1, own, _ => Curve()).Duty![0], "both under 90C");
            Harness.Assert(!p.Overheat, "latch off");
        });

        Harness.Scenario("Policy_CpuTempLost_GiveBackOnThird", _ =>
        {
            var p = new ManualFanPolicy();
            var s1 = p.Next(0, 50, 1, own, _ => Curve());
            var s2 = p.Next(-1, 50, 1, own, _ => Curve());
            var s3 = p.Next(0, 50, 1, own, _ => Curve());
            Harness.Assert(s1.Duty == null && !s1.GiveBack && !s2.GiveBack, "two misses hold");
            Harness.Assert(s3.GiveBack, "third miss gives back");
        });

        Console.WriteLine();
        Console.WriteLine("--- EcProbe ---");

        Harness.Scenario("Probe_Ok", _ =>
        {
            var p = EcProbe.Parse("probe ok lpc 18 2 3100 2900");
            Harness.Assert(p.Ok && p.Link == "lpc" && p.Version == 18 && p.Fans == 2 && p.Rpm0 == 3100, "fields");
            Harness.AssertEqual<string?>(null, p.Reject(3000), "tach within 15%");
            Harness.AssertEqual<string?>(null, p.Reject(0), "no hwmon tach");
        });

        Harness.Scenario("Probe_HidLinkThreeFans", _ =>
        {
            var p = EcProbe.Parse("probe ok hid 5 3 900 800 700");
            Harness.Assert(p.Ok && p.Link == "hid" && p.Version == 5 && p.Fans == 3 && p.Rpm0 == 900, "fields");
        });

        Harness.Scenario("Probe_TachMismatchRejected", _ =>
            Harness.AssertEqual<string?>("tach ec=3100 hwmon=5000",
                EcProbe.Parse("probe ok lpc 18 2 3100 2900").Reject(5000), "foreign EC"));

        Harness.Scenario("Probe_NoReason", _ =>
            Harness.AssertEqual<string?>("odm", EcProbe.Parse("probe no odm").Reject(3000), "reason"));

        Harness.Scenario("Probe_OldHelperRejected", _ =>
            Harness.AssertEqual<string?>("helper", EcProbe.Parse("probe 18 2 3100 2900").Reject(3000), "old reply"));
    }
}

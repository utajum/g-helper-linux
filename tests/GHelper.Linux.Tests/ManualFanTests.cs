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

        Harness.Scenario("Average_EmptyIsMinusOne", _ =>
            Harness.AssertEqual(-1.0, ManualFanMath.Average(new Queue<int>()), "empty window"));

        Harness.Scenario("Average_Rolling", _ =>
        {
            var q = new Queue<int>(new[] { 40, 50, 60 });
            Harness.AssertEqual(50.0, ManualFanMath.Average(q), "avg of 40,50,60");
        });
    }
}

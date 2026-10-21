// Custom performance modes: per-mode settings copied on Add, cleaned on Remove.

using GHelper.Linux.Helpers;
using GHelper.Linux.Mode;

namespace GHelper.Linux.Tests;

public static class ModesTests
{
    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("--- Custom modes ---");

        Harness.Scenario("Modes_Add_CopiesCurrentModeSettings", _ =>
        {
            AppConfig.Set("performance_mode", 1);
            AppConfig.Set("performance_1", 1);
            AppConfig.Set("limit_total_1", 90);
            AppConfig.Set("limit_ppt_apu_sppt_1", 50);
            AppConfig.Set("fan_profile_cpu_1", "30,40,50,60,70,80,90,100,10,20,30,40,50,60,70,80");
            AppConfig.Set("gpu_boost_1", 10);
            AppConfig.Set("auto_apply_power_1", 1);
            AppConfig.Set("limit_total_0", 40);

            int mode = Modes.Add();

            Harness.AssertEqual(3, mode, "new mode index");
            Harness.AssertEqual(1, Modes.GetBase(3), "base of new mode");
            Harness.AssertEqual(90, AppConfig.Get("limit_total_3"), "limit_total copied");
            Harness.AssertEqual(50, AppConfig.Get("limit_ppt_apu_sppt_3"), "firmware limit copied");
            Harness.AssertEqual(AppConfig.GetString("fan_profile_cpu_1"), AppConfig.GetString("fan_profile_cpu_3"), "fan curve copied");
            Harness.AssertEqual(10, AppConfig.Get("gpu_boost_3"), "gpu_boost copied");
            Harness.AssertEqual(1, AppConfig.Get("auto_apply_power_3"), "auto_apply_power copied");
            Harness.Assert(!AppConfig.Exists("performance_3"), "non-mode key performance_1 must not be copied");
            Harness.AssertEqual(1, Modes.GetList().Count(m => m == 3), "new mode listed once");
        });

        Harness.Scenario("Modes_Remove_DeletesOnlyThatMode", _ =>
        {
            AppConfig.Set("mode_base_3", 0);
            AppConfig.Set("mode_name_3", "Game");
            AppConfig.Set("limit_total_3", 60);
            AppConfig.Set("fan_profile_gpu_3", "1,2");
            AppConfig.Set("mode_command_3", "echo hi");
            AppConfig.Set("mode_base_13", 2);
            AppConfig.Set("limit_total_13", 30);

            Modes.Remove(3);

            Harness.Assert(!Modes.Exists(3), "mode 3 gone");
            foreach (var key in new[] { "mode_name_3", "limit_total_3", "fan_profile_gpu_3", "mode_command_3" })
                Harness.Assert(!AppConfig.Exists(key), $"{key} removed");
            Harness.AssertEqual(2, Modes.GetBase(13), "mode 13 untouched");
            Harness.AssertEqual(30, AppConfig.Get("limit_total_13"), "limit_total_13 untouched");
        });

        Harness.Scenario("Modes_DefaultCurve_UsesBaseMode", _ =>
        {
            AppConfig.Set("performance_mode", 1);
            string turbo = BitConverter.ToString(AppConfig.GetDefaultCurve(0));
            AppConfig.Set("mode_base_3", 1);
            AppConfig.Set("performance_mode", 3);
            Harness.AssertEqual(turbo, BitConverter.ToString(AppConfig.GetDefaultCurve(0)), "custom mode on Turbo gets the Turbo curve");
        });

        Harness.Scenario("Modes_Add_WhenFull_ReturnsMinusOne", _ =>
        {
            for (int i = 3; i < 20; i++)
                AppConfig.Set($"mode_base_{i}", 0);
            Harness.AssertEqual(-1, Modes.Add(), "no free slot");
        });
    }
}

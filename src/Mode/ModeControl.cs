namespace GHelper.Linux.Mode;

/// <summary>
/// Performance mode controller — the core business logic orchestrator.
/// Ported from G-Helper's ModeControl.cs.
/// 
/// When a mode change occurs, this class:
///   1. Sets the thermal policy via asus-wmi
///   2. Applies fan curves (if configured)
///   3. Applies power limits (if configured)
///   4. Sets CPU boost (if configured)
///   5. Sets platform profile
///   6. Shows notification
/// </summary>
public class ModeControl
{
    public ModeControl()
    {
    }

    /// <summary>
    /// Set performance mode and apply all associated settings.
    /// </summary>
    public void SetPerformanceMode(int mode = -1, bool notify = false)
    {
        if (mode < 0) mode = Modes.GetCurrent();
        if (!Modes.Exists(mode)) mode = 0;

        Modes.SetCurrent(mode);
        int baseMode = Modes.GetBase(mode);

        Helpers.Logger.WriteLine($"SetPerformanceMode: {Modes.GetName(mode)} (base={baseMode})");

        // 1. Set thermal policy
        App.Wmi?.SetThrottleThermalPolicy(baseMode);

        // 2. Set platform profile to match
        string profile = baseMode switch
        {
            0 => "balanced",
            1 => "performance",
            2 => "low-power",
            _ => "balanced"
        };
        App.Power?.SetPlatformProfile(profile);

        // 3. Apply fan curves if auto-apply is enabled
        Task.Run(async () =>
        {
            await Task.Delay(100); // Let thermal policy settle

            AutoFans(mode);

            await Task.Delay(500);

            AutoPower(mode);

            // CPU Boost override
            int autoBoost = Helpers.AppConfig.GetMode("auto_boost");
            if (autoBoost >= 0)
            {
                App.Power?.SetCpuBoost(autoBoost == 1);
            }

            // ASPM
            if (Helpers.AppConfig.Is("aspm"))
            {
                App.Power?.SetAspmPolicy(baseMode == 2 ? "powersave" : "default");
            }
        });

        if (notify)
        {
            App.System?.ShowNotification("G-Helper", $"Mode: {Modes.GetName(mode)}");
        }
    }

    /// <summary>Cycle to the next performance mode.</summary>
    public void CyclePerformanceMode(bool back = false)
    {
        int nextMode = Modes.GetNext(back);
        SetPerformanceMode(nextMode, notify: true);
    }

    /// <summary>Auto-select mode based on AC/battery status.</summary>
    public void AutoPerformance(bool powerChanged = false)
    {
        bool onAc = App.Power?.IsOnAcPower() ?? true;
        int mode = Helpers.AppConfig.Get($"performance_{(onAc ? 1 : 0)}", -1);

        if (mode >= 0)
            SetPerformanceMode(mode, powerChanged);
        else
            SetPerformanceMode(Modes.GetCurrent());
    }

    /// <summary>Apply saved fan curves for the given mode.</summary>
    private void AutoFans(int mode)
    {
        if (!Helpers.AppConfig.IsMode("auto_apply_fans")) return;

        var wmi = App.Wmi;
        if (wmi == null) return;

        for (int fan = 0; fan < 3; fan++)
        {
            byte[] curve = Helpers.AppConfig.GetFanConfig(fan);
            if (curve.Length == 16)
            {
                wmi.SetFanCurve(fan, curve);
                Helpers.Logger.WriteLine($"AutoFans: Applied fan {fan} curve for mode {mode}");
            }
        }
    }

    /// <summary>Apply saved power limits for the given mode.</summary>
    private void AutoPower(int mode)
    {
        if (!Helpers.AppConfig.IsMode("auto_apply_power")) return;

        var wmi = App.Wmi;
        if (wmi == null) return;

        int pl1 = Helpers.AppConfig.GetMode("limit_slow");
        int pl2 = Helpers.AppConfig.GetMode("limit_fast");

        if (pl1 > 0)
        {
            wmi.SetPptLimit("ppt_pl1_spl", pl1);
            Helpers.Logger.WriteLine($"AutoPower: PL1 = {pl1}W");
        }

        if (pl2 > 0)
        {
            wmi.SetPptLimit("ppt_pl2_sppt", pl2);
            Helpers.Logger.WriteLine($"AutoPower: PL2 = {pl2}W");
        }

        // NVIDIA dynamic boost
        int nvBoost = Helpers.AppConfig.GetMode("gpu_boost");
        if (nvBoost > 0 && wmi.IsFeatureSupported("nv_dynamic_boost"))
        {
            wmi.SetPptLimit("nv_dynamic_boost", nvBoost);
        }

        // NVIDIA temp target
        int nvTemp = Helpers.AppConfig.GetMode("gpu_temp");
        if (nvTemp > 0 && wmi.IsFeatureSupported("nv_temp_target"))
        {
            wmi.SetPptLimit("nv_temp_target", nvTemp);
        }
    }
}

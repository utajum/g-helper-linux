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
    // Track whether custom power limits were applied (for IsResetRequired workaround)
    private int _customPower;

    // ── Power limit bounds (matches Windows G-Helper AsusACPI constructor) ──

    private const int MinTotal = 5;
    private const int MinGpuBoost = 5;

    private static int GetMaxTotal()
    {
        if (Helpers.AppConfig.IsAdvantageEdition()) return 250;
        if (Helpers.AppConfig.IsX13()) return 75;
        if (Helpers.AppConfig.IsAlly()) return 50;
        if (Helpers.AppConfig.IsIntelHX()) return 175;
        if (Helpers.AppConfig.IsAMDLight()) return 90;
        if (Helpers.AppConfig.IsZ1325()) return 93;
        if (Helpers.AppConfig.IsFA401EA()) return 115;
        return 150; // default
    }

    private static int GetMaxCpu()
    {
        if (Helpers.AppConfig.IsFA401EA()) return 115;
        return 100; // default
    }

    private static int GetMaxGpuBoost()
    {
        if (Helpers.AppConfig.DynamicBoost5()) return 5;
        if (Helpers.AppConfig.DynamicBoost15()) return 15;
        if (Helpers.AppConfig.DynamicBoost20()) return 20;
        return 25; // default
    }

    public ModeControl()
    {
    }

    /// <summary>
    /// Set performance mode and apply all associated settings.
    /// </summary>
    public void SetPerformanceMode(int mode = -1, bool notify = false)
    {
        int oldMode = Modes.GetCurrent();
        if (mode < 0) mode = oldMode;
        if (!Modes.Exists(mode)) mode = 0;

        Modes.SetCurrent(mode);
        int baseMode = Modes.GetBase(mode);
        int oldBaseMode = Modes.GetBase(oldMode);

        Helpers.Logger.WriteLine($"SetPerformanceMode: {Modes.GetName(mode)} (base={baseMode})");

        // 1. Set thermal policy
        // Workaround for GA403/FA507XV: firmware doesn't properly reset power limits
        // when switching between custom modes with the same base. Briefly bounce to a
        // different base mode first, then switch to the target.
        bool needsReset = Helpers.AppConfig.IsResetRequired()
            && oldBaseMode == baseMode
            && _customPower > 0
            && !Helpers.AppConfig.IsMode("auto_apply_power");

        if (needsReset)
        {
            int resetBase = (oldBaseMode != 1) ? 1 : 0; // bounce to Turbo or Balanced
            Helpers.Logger.WriteLine($"IsResetRequired: bouncing {oldBaseMode} → {resetBase} → {baseMode}");
            App.Wmi?.SetThrottleThermalPolicy(resetBase);
        }

        _customPower = 0;

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

        // 3. Apply fan curves and power limits
        Task.Run(async () =>
        {
            // If reset was needed, wait for firmware to process the bounce
            if (needsReset)
                await Task.Delay(1500);
            else
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
            App.System?.ShowNotification("Performance", Modes.GetName(mode), "preferences-system-performance");
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

        int maxTotal = GetMaxTotal();
        int maxGpuBoost = GetMaxGpuBoost();

        int pl1 = Helpers.AppConfig.GetMode("limit_slow");
        int pl2 = Helpers.AppConfig.GetMode("limit_fast");

        // Validate against model-specific bounds (matches Windows G-Helper)
        if (pl1 > maxTotal || pl1 < MinTotal) pl1 = -1;
        if (pl2 > maxTotal || pl2 < MinTotal) pl2 = -1;

        if (pl1 > 0)
        {
            wmi.SetPptLimit("ppt_pl1_spl", pl1);
            _customPower = pl1;
            Helpers.Logger.WriteLine($"AutoPower: PL1 = {pl1}W (max={maxTotal}W)");
        }

        if (pl2 > 0)
        {
            wmi.SetPptLimit("ppt_pl2_sppt", pl2);
            if (pl2 > _customPower) _customPower = pl2;
            Helpers.Logger.WriteLine($"AutoPower: PL2 = {pl2}W (max={maxTotal}W)");
        }

        // NVIDIA dynamic boost
        int nvBoost = Helpers.AppConfig.GetMode("gpu_boost");
        if (nvBoost > maxGpuBoost) nvBoost = maxGpuBoost;
        if (nvBoost > 0 && wmi.IsFeatureSupported("nv_dynamic_boost"))
        {
            wmi.SetPptLimit("nv_dynamic_boost", nvBoost);
            Helpers.Logger.WriteLine($"AutoPower: GPU boost = {nvBoost}W (max={maxGpuBoost}W)");
        }

        // NVIDIA temp target
        int nvTemp = Helpers.AppConfig.GetMode("gpu_temp");
        if (nvTemp > 0 && wmi.IsFeatureSupported("nv_temp_target"))
        {
            wmi.SetPptLimit("nv_temp_target", nvTemp);
        }
    }
}

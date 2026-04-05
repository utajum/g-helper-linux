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
        // IsZ1325 must be checked before IsCPULight: GZ302E matches both,
        // but the Z13 2025 (GZ302EA) needs 93W max, not 90W.
        if (Helpers.AppConfig.IsZ1325()) return 93;
        if (Helpers.AppConfig.IsCPULight()) return 90;
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

        // 3. Verify: on some kernels, throttle_thermal_policy and platform_profile
        // are coupled — writing platform_profile may reset throttle_thermal_policy.
        // Read back and re-apply if needed.
        //
        // On newer kernels with asus-armoury firmware-attributes, throttle_thermal_policy
        // may not exist as a separate sysfs file. In that case GetThrottleThermalPolicy()
        // derives its value from platform_profile — so if platform_profile is correct,
        // there's nothing to re-apply.
        int verifyPolicy = App.Wmi?.GetThrottleThermalPolicy() ?? -1;
        string verifyProfile = App.Power?.GetPlatformProfile() ?? "unknown";
        Helpers.Logger.WriteLine($"SetPerformanceMode verify: thermal_policy={verifyPolicy} (expected {baseMode}), platform_profile={verifyProfile} (expected {profile})");

        // Only re-apply if we got a definite wrong answer (not -1 = "unavailable")
        if (verifyPolicy >= 0 && verifyPolicy != baseMode)
        {
            // Check if platform_profile is already correct — if so, the "mismatch" is just
            // because thermal_policy is derived from platform_profile on this kernel.
            bool profileCorrect = verifyProfile == profile ||
                (profile == "low-power" && verifyProfile == "quiet");

            if (!profileCorrect)
            {
                Helpers.Logger.WriteLine($"WARNING: throttle_thermal_policy was overridden ({verifyPolicy} != {baseMode}), re-applying");
                App.Wmi?.SetThrottleThermalPolicy(baseMode);
                // Brief delay then verify again
                Thread.Sleep(100);
                int recheck = App.Wmi?.GetThrottleThermalPolicy() ?? -1;
                Helpers.Logger.WriteLine($"SetPerformanceMode re-verify: thermal_policy={recheck}");
            }
        }

        // 4. Apply fan curves and power limits
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

            // ASPM — on by default (synced with upstream IsAutoASPM/IsNotFalse behavior)
            if (Helpers.AppConfig.IsNotFalse("aspm"))
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
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPl1Spl, pl1);
            _customPower = pl1;
            Helpers.Logger.WriteLine($"AutoPower: PL1 = {pl1}W (max={maxTotal}W)");
        }

        if (pl2 > 0)
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPl2Sppt, pl2);
            if (pl2 > _customPower) _customPower = pl2;
            Helpers.Logger.WriteLine($"AutoPower: PL2 = {pl2}W (max={maxTotal}W)");
        }

        // APU SPPT / Platform SPPT — secondary AMD power tracking limits.
        //
        // On Windows, setting the ACPI thermal policy propagates internally to all PPT
        // registers. On Linux with dual-backend kernels (asus-nb-wmi + asus-armoury),
        // writing throttle_thermal_policy does NOT reliably update ppt_apu_sppt and
        // ppt_platform_sppt — they can remain stuck at the previous mode's values
        // (e.g. 5W from Silent mode) even after switching to Turbo.
        //
        // Since AMD firmware enforces min(all PPT limits), a 5W APU SPPT hard-caps
        // performance regardless of PL1/PL2 being 93W. Mirror PL1/PL2 values here to
        // ensure these secondary limits don't act as hidden bottlenecks.
        //
        // This matches asusctl's behavior of writing ALL PPT firmware-attributes.
        int apuPlatCeiling = Math.Max(pl1, pl2);  // never less than either primary limit
        if (apuPlatCeiling > 0)
        {
            if (wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptApuSppt))
            {
                wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptApuSppt, apuPlatCeiling);
                Helpers.Logger.WriteLine($"AutoPower: APU SPPT = {apuPlatCeiling}W (mirrored from max(PL1,PL2))");
            }

            if (wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptPlatformSppt))
            {
                wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPlatformSppt, apuPlatCeiling);
                Helpers.Logger.WriteLine($"AutoPower: Platform SPPT = {apuPlatCeiling}W (mirrored from max(PL1,PL2))");
            }
        }

        // fPPT (fast boost)
        int fppt = Helpers.AppConfig.GetMode("limit_fppt");
        if (fppt > maxTotal || fppt < MinTotal) fppt = -1;
        if (fppt > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptFppt))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptFppt, fppt);
            if (fppt > _customPower) _customPower = fppt;
            Helpers.Logger.WriteLine($"AutoPower: fPPT = {fppt}W (max={maxTotal}W)");
        }

        // NVIDIA dynamic boost
        int nvBoost = Helpers.AppConfig.GetMode("gpu_boost");
        if (nvBoost > maxGpuBoost) nvBoost = maxGpuBoost;
        if (nvBoost > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvDynamicBoost))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvDynamicBoost, nvBoost);
            Helpers.Logger.WriteLine($"AutoPower: GPU boost = {nvBoost}W (max={maxGpuBoost}W)");
        }

        // NVIDIA temp target
        int nvTemp = Helpers.AppConfig.GetMode("gpu_temp");
        if (nvTemp > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvTempTarget))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvTempTarget, nvTemp);
        }

        // Verify PPT writes took effect — read back and warn on mismatches
        VerifyPptLimits(wmi, pl1, pl2, fppt, apuPlatCeiling > 0 ? apuPlatCeiling : -1);
    }

    /// <summary>
    /// Read back PPT values after writing to confirm they took effect.
    /// Logs all current values and warns if any expected value doesn't match.
    /// This helps diagnose dual-backend issues and permission problems.
    /// </summary>
    private static void VerifyPptLimits(Platform.IAsusWmi wmi, int expectedPl1, int expectedPl2, int expectedFppt, int expectedApuPlat)
    {
        try
        {
            // Brief delay to let writes settle through firmware
            Thread.Sleep(200);

            var checks = new List<(string name, Platform.Linux.AttrDef attr, int expected)>();
            if (expectedPl1 > 0)
                checks.Add(("PL1", Platform.Linux.AsusAttributes.PptPl1Spl, expectedPl1));
            if (expectedPl2 > 0)
                checks.Add(("PL2", Platform.Linux.AsusAttributes.PptPl2Sppt, expectedPl2));
            if (expectedFppt > 0)
                checks.Add(("fPPT", Platform.Linux.AsusAttributes.PptFppt, expectedFppt));
            if (expectedApuPlat > 0)
            {
                checks.Add(("APU_SPPT", Platform.Linux.AsusAttributes.PptApuSppt, expectedApuPlat));
                checks.Add(("PLAT_SPPT", Platform.Linux.AsusAttributes.PptPlatformSppt, expectedApuPlat));
            }

            var parts = new List<string>();
            bool anyMismatch = false;

            foreach (var (name, attr, expected) in checks)
            {
                int actual = wmi.GetPptLimit(attr);
                string status;
                if (actual < 0)
                    status = "?";  // could not read back
                else if (actual == expected)
                    status = $"{actual}W";
                else
                {
                    status = $"{actual}W (expected {expected}W!)";
                    anyMismatch = true;
                }
                parts.Add($"{name}={status}");
            }

            string prefix = anyMismatch ? "WARNING AutoPower verify" : "AutoPower verify";
            Helpers.Logger.WriteLine($"{prefix}: {string.Join(", ", parts)}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("AutoPower verify failed", ex);
        }
    }
}

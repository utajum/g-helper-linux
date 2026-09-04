using GHelper.Linux.I18n;

namespace GHelper.Linux.Mode;

/// <summary>
/// Performance mode controller - the core business logic orchestrator.
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

    // Power-limit reapply timer. Off when reapply_time <= 0. Periodically re-writes
    // PPT/CPU-temp/GPU values to fight BIOS clobber on some models.
    private System.Timers.Timer? _reapplyTimer;

    /// <summary>
    /// Fired after a mode change has fully landed (thermal policy + power limits +
    /// fan curves all applied). Subscribers should refresh any UI showing
    /// per-mode state (FansWindow charts, power sliders, boost button, UV).
    /// Always raised from a background thread - handlers must marshal to the UI
    /// thread themselves if they touch widgets.
    /// </summary>
    public event Action<int>? ModeApplied;

    // Power limit bounds (matches Windows G-Helper AsusACPI constructor)

    internal const int MinTotal = 5;

    internal static int GetMaxTotal()
    {
        if (Helpers.AppConfig.IsAdvantageEdition())
            return 250;
        if (Helpers.AppConfig.IsX13())
            return 75;
        if (Helpers.AppConfig.IsAlly())
            return 50;
        if (Helpers.AppConfig.IsIntelHX())
            return 175;
        // IsZ1325 must be checked before IsCPULight: GZ302E matches both,
        // but the Z13 2025 (GZ302EA) needs 93W max, not 90W.
        if (Helpers.AppConfig.IsZ1325())
            return 93;
        if (Helpers.AppConfig.IsCPULight())
            return 90;
        if (Helpers.AppConfig.IsOnlyAIMAX())
            return 115;
        return 150; // default
    }

    private static int GetMaxCpu()
    {
        if (Helpers.AppConfig.IsOnlyAIMAX())
            return 115;
        return 100; // default
    }

    private static int GetMaxGpuBoost()
    {
        if (Helpers.AppConfig.DynamicBoost5())
            return 5;
        if (Helpers.AppConfig.DynamicBoost15())
            return 15;
        if (Helpers.AppConfig.DynamicBoost20())
            return 20;
        return 25; // default
    }

    public ModeControl()
    {
    }

    private static Task _modeTask = Task.CompletedTask;

    /// <summary>Blocks until the async mode-apply pipeline finishes (max 5s).
    /// GPU switches call this so dgpu_disable writes do not hit the EC while
    /// fan/PPT writes are in flight (upstream bab71419).</summary>
    public void WaitForApply()
    {
        try
        { _modeTask.Wait(5000); }
        catch { }
    }

    /// <summary>
    /// Set performance mode and apply all associated settings.
    /// </summary>
    public void SetPerformanceMode(int mode = -1, bool notify = false)
    {
        int oldMode = Modes.GetCurrent();
        if (mode < 0)
            mode = oldMode;
        if (!Modes.Exists(mode))
            mode = 0;

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

        // 2. Set platform profile to match.
        // User can override per-mode (per base mode 0/1/2) via Extra Settings → Power
        // Management. Stored as platform_profile_<baseMode> with kernel-native token
        // (read from platform_profile_choices), so it's known to be supported. Falls
        // back to canonical defaults when unset (mapped to firmware-supported names
        // by SetPlatformProfile's synonym table for legacy firmware).
        string profile = Helpers.AppConfig.GetString($"platform_profile_{baseMode}") ?? baseMode switch
        {
            0 => "balanced",
            1 => "performance",
            2 => "low-power",
            _ => "balanced"
        };
        App.Power?.SetPlatformProfile(profile);

        // Apply per-mode EPP override if set. Done after platform_profile
        // because changing the profile can reset EPP on amd/intel pstate.
        string? epp = Helpers.AppConfig.GetString($"epp_{baseMode}");
        if (epp != null && Platform.Linux.CpuEpp.IsSupported())
            Platform.Linux.CpuEpp.SetAll(epp);

        // 3. Verify: on some kernels, throttle_thermal_policy and platform_profile
        // are coupled - writing platform_profile may reset throttle_thermal_policy.
        // Read back and re-apply if needed.
        //
        // On newer kernels with asus-armoury firmware-attributes, throttle_thermal_policy
        // may not exist as a separate sysfs file. In that case GetThrottleThermalPolicy()
        // derives its value from platform_profile - so if platform_profile is correct,
        // there's nothing to re-apply.
        int verifyPolicy = App.Wmi?.GetThrottleThermalPolicy() ?? -1;
        string verifyProfile = App.Power?.GetPlatformProfile() ?? "unknown";
        Helpers.Logger.WriteLine($"SetPerformanceMode verify: thermal_policy={verifyPolicy} (expected {baseMode}), platform_profile={verifyProfile} (expected {profile})");

        // Only re-apply if we got a definite wrong answer (not -1 = "unavailable")
        if (verifyPolicy >= 0 && verifyPolicy != baseMode)
        {
            // Check if platform_profile is already correct - if so, the "mismatch" is just
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

        // 4. Apply fan curves, power limits, ASPM, then fan curves again.
        //
        // Fan curves are written FIRST because some firmware requires manual
        // fan mode (FANM=4, set by pwm_enable=1) before accepting PPT writes
        // via asus-armoury firmware-attributes. Without this, PPT writes may
        // be silently ignored on affected models. (asusctl 6.3.8 documents
        // this: "re-apply fan curves before PPT writes".)
        //
        // Fan curves are then written AGAIN after PPT/ASPM because:
        // - nv_dynamic_boost/nv_temp_target can cause the EC to recalculate
        //   GPU fan strategy and override custom curves
        // - ASPM policy changes trigger PCIe link renegotiation which can
        //   reset curves
        // The final write ensures curves are authoritative after all other
        // EC interactions settle.
        _modeTask = Task.Run(async () =>
        {
            // If reset was needed, wait for firmware to process the bounce
            if (needsReset)
                await Task.Delay(1500);
            else
                await Task.Delay(100); // Let thermal policy settle

            // Phase 1: fan curves first (sets FANM=4 for PPT acceptance)
            AutoFans(mode);
            Fan.FanHysteresis.ApplyForCurrentMode();
            await Task.Delay(100);

            // Phase 2: power limits, undervolt, boost, ASPM
            AutoCpuPower(mode);
            AutoGpuPower(mode);

            // CPU undervolt (independent of auto_apply_power - uses its own auto_uv flag)
            AutoCpuUndervolt();

            // G614F / G814F / G733P rescue: EC overwrites freshly-applied
            // PPT/CO values ~3-4 s after a mode switch. Schedule a single
            // delayed re-apply 5 s later to defeat the overwrite.
            if (Helpers.AppConfig.IsReapplyRyzen())
            {
                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    try
                    {
                        Helpers.Logger.WriteLine("IsReapplyRyzen: 5 s rescue CPU undervolt pass");
                        AutoCpuUndervolt();
                    }
                    catch (Exception ex)
                    {
                        Helpers.Logger.WriteLine("IsReapplyRyzen rescue failed", ex);
                    }
                });
            }

            // CPU Boost override
            int autoBoost = Helpers.AppConfig.GetMode("auto_boost");
            if (autoBoost >= 0)
            {
                App.Power?.SetCpuBoost(autoBoost == 1);
            }

            // ASPM - on by default. No UI (kernel writes often blocked by
            // built-in pcie_aspm config). Auto-derived: powersave for Silent,
            // default elsewhere. When leaving powersave, NVMe devices are woken
            // first to prevent D3cold resume failures.
            if (Helpers.AppConfig.IsAutoASPM() && App.Power != null)
            {
                await App.Power.SetAspmPolicy(baseMode == 2 ? "powersave" : "default");
            }

            await Task.Delay(100); // Let EC settle after power/ASPM changes

            // Phase 3: re-apply fan curves to recover from PPT/ASPM-induced
            // EC resets. This is the authoritative final write.
            AutoFans(mode);

            // 5. Mode-change shell command hook (per-mode, optional). Runs on every
            // switch including auto AC/DC. Empty string = no-op. Fire-and-forget.
            RunModeCommand(mode);

            // 6. Refresh the reapply timer for the new mode.
            RefreshReapplyTimer();

            // 7. Notify UI subscribers (FansWindow etc.) so they can re-read
            //    fan curves, PPT, and CPU boost state after the kernel/EC has
            //    settled. Raised from the background thread - handlers must
            //    Dispatcher.UIThread.Post if they touch widgets.
            try
            { ModeApplied?.Invoke(mode); }
            catch (Exception ex) { Helpers.Logger.WriteLine("ModeApplied handler threw", ex); }

            // 8. Lenovo firmware dims the keyboard backlight when entering the
            //    quiet/low-power profile. Restore the configured level once the
            //    EC has settled so Silent doesn't leave the keyboard dark.
            if (baseMode == 2 && Helpers.AppConfig.IsLenovoDevice()
                && (App.Wmi?.GetKeyboardBrightness() ?? -1) >= 0)
            {
                await Task.Delay(700);
                int kbd = Helpers.AppConfig.Get(USB.Aura.GetBrightnessConfigKey(), -1);
                if (kbd < 0)
                    kbd = Helpers.AppConfig.Get("keyboard_brightness", -1);
                if (kbd > 0)
                {
                    App.Wmi?.SetKeyboardBrightness(kbd);
                    Helpers.Logger.WriteLine($"Lenovo: re-asserted kbd backlight={kbd} after low-power");
                }
            }
        });

        if (notify)
        {
            App.System?.ShowNotification(Labels.Get("performance"), Modes.GetName(mode), "preferences-system-performance");
        }
    }

    /// <summary>
    /// (Re)configure the power-limit reapply timer based on <c>reapply_time</c>
    /// (seconds; 0 = disabled). Safe to call repeatedly.
    /// </summary>
    public void RefreshReapplyTimer()
    {
        // Default 30 s for models whose EC silently resets the temp limit under load 
        int defaultSeconds = Helpers.AppConfig.IsReapplyTempRequired() ? 30 : 0;
        int seconds = Helpers.AppConfig.Get("reapply_time", defaultSeconds);

        if (seconds <= 0)
        {
            if (_reapplyTimer != null)
            {
                _reapplyTimer.Stop();
                _reapplyTimer.Elapsed -= ReapplyTimer_Elapsed;
                _reapplyTimer.Dispose();
                _reapplyTimer = null;
                Helpers.Logger.WriteLine("ReapplyTimer: disabled");
            }
            return;
        }

        // (Re)create timer at requested interval
        if (_reapplyTimer != null)
        {
            _reapplyTimer.Stop();
            _reapplyTimer.Elapsed -= ReapplyTimer_Elapsed;
            _reapplyTimer.Dispose();
        }
        _reapplyTimer = new System.Timers.Timer(seconds * 1000.0) { AutoReset = true };
        _reapplyTimer.Elapsed += ReapplyTimer_Elapsed;
        _reapplyTimer.Start();
        Helpers.Logger.WriteLine($"ReapplyTimer: every {seconds}s");
    }

    private void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            // Re-apply fan curves first so FANM=4 is set before PPT writes.
            // Some firmware silently ignores PPT writes when not in manual
            // fan mode. AutoFans sets FANM=4 when auto_apply_fans is ON;
            // AutoCpuPower/AutoGpuPower call EnsureManualFanMode() themselves
            // when auto_apply_fans is OFF.
            int mode = Modes.GetCurrent();
            AutoFans(mode);
            AutoCpuPower(mode);
            AutoGpuPower(mode);
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("ReapplyTimer tick failed", ex);
        }
    }

    /// <summary>
    /// Run the user-configured shell command for this mode, if any.
    /// Persisted as <c>mode_command_&lt;mode&gt;</c>.
    /// </summary>
    private static void RunModeCommand(int mode)
    {
        string? cmd = Helpers.AppConfig.GetString($"mode_command_{mode}");
        if (string.IsNullOrWhiteSpace(cmd))
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", cmd },
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            Helpers.Logger.WriteLine($"RunModeCommand[{mode}]: started pid={proc?.Id} cmd={cmd}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"RunModeCommand[{mode}] failed: cmd={cmd}", ex);
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
        // experimental EC follower tracks the same per-mode switch
        Fan.ManualFanService.Sync();

        if (!Helpers.AppConfig.IsMode("auto_apply_fans"))
            return;

        var wmi = App.Wmi;
        if (wmi == null)
            return;

        int fanCount = wmi.FanCount;
        for (int fan = 0; fan < fanCount; fan++)
        {
            byte[] curve = Helpers.AppConfig.GetFanConfig(fan);
            if (curve.Length == 16)
            {
                wmi.SetFanCurve(fan, curve);
                Helpers.Logger.WriteLine($"AutoFans: Applied fan {fan} curve for mode {mode}");
            }
        }

        // XG Mobile dock GPU fan (fan index 3) lives on the dock's HID bus,
        // not the laptop's WMI. Push our per-mode curve when the dock is
        // present. Dock firmware caps PWM at 72%, so clamp before sending.
        //
        // Mirrors Windows g-helper AutoFans (post-ef4385a3): SetFan is
        // pushed directly without an intervening Reset. The earlier
        // Reset-before-SetFan pattern was a bug - it briefly reverted the
        // dock to firmware-default in the same tick the new curve was
        // applied, causing a transient fan glitch. Reset is now reserved
        // for "stop managing" (dock disable), not "before each apply".
        try
        {
            if (USB.XGM.IsConnected())
            {
                byte[] xgm = Helpers.AppConfig.GetFanConfig(3);
                if (xgm.Length == 16)
                {
                    var clamped = new byte[16];
                    Array.Copy(xgm, clamped, 16);
                    for (int i = 8; i < 16; i++)
                    {
                        if (clamped[i] > 72)
                            clamped[i] = 72;
                    }
                    USB.XGM.SetFan(clamped);
                    Helpers.Logger.WriteLine($"AutoFans: Applied XGM dock curve for mode {mode}");
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"AutoFans: XGM apply failed: {ex.Message}");
        }
    }

    /// <summary>Apply saved CPU power limits for the given mode (gated by per-mode auto_apply_power).</summary>
    private void AutoCpuPower(int mode)
    {
        if (!Helpers.AppConfig.IsMode("auto_apply_power"))
            return;

        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // Some firmware (SPLX ACPI path) silently ignores PPT writes unless
        // the EC is in manual fan mode (FANM=4). When auto_apply_fans is ON,
        // Phase 1 already called AutoFans() which sets FANM=4 via
        // pwm_enable=1. When auto_apply_fans is OFF, ensure FANM=4 here
        // before writing any PPT values.
        if (!Helpers.AppConfig.IsMode("auto_apply_fans"))
            wmi.EnsureManualFanMode();

        int maxTotal = GetMaxTotal();

        int pl1 = Helpers.AppConfig.GetMode("limit_slow");
        int pl2 = Helpers.AppConfig.GetMode("limit_fast");

        // Validate against model-specific bounds (matches Windows G-Helper).
        // <= MinTotal: a floor value is never real user intent, it is a
        // poisoned config entry from a bogus firmware readback (issue #151);
        // applying it hard-caps the CPU at its lowest clock.
        if (pl1 > maxTotal || pl1 <= MinTotal)
            pl1 = -1;
        if (pl2 > maxTotal || pl2 <= MinTotal)
            pl2 = -1;

        if (pl1 > 0)
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPl1Spl, pl1);
            _customPower = pl1;
            Helpers.Logger.WriteLine($"AutoPower: PL1 = {pl1}W (max={maxTotal}W)");
        }

        if (pl2 > 0)
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptPl2Sppt, pl2);
            if (pl2 > _customPower)
                _customPower = pl2;
            Helpers.Logger.WriteLine($"AutoPower: PL2 = {pl2}W (max={maxTotal}W)");
        }

        // APU SPPT / Platform SPPT - secondary AMD power tracking limits.
        //
        // On Windows, setting the ACPI thermal policy propagates internally to all PPT
        // registers. On Linux with dual-backend kernels (asus-nb-wmi + asus-armoury),
        // writing throttle_thermal_policy does NOT reliably update ppt_apu_sppt and
        // ppt_platform_sppt - they can remain stuck at the previous mode's values
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
        if (fppt > maxTotal || fppt <= MinTotal)
            fppt = -1;
        if (fppt > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptFppt))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.PptFppt, fppt);
            if (fppt > _customPower)
                _customPower = fppt;
            Helpers.Logger.WriteLine($"AutoPower: fPPT = {fppt}W (max={maxTotal}W)");
        }

        VerifyPptLimits(wmi, pl1, pl2, fppt, apuPlatCeiling > 0 ? apuPlatCeiling : -1);

        if (Helpers.AppConfig.IsAlly())
        {
            int total = Helpers.AppConfig.GetMode("limit_total");
            if (total > 0)
                Ally.AllyControl.SetTDP(total, $"AutoCpuPower mode {mode}");
        }
    }

    /// <summary>
    /// Per-mode GPU policy. When auto-apply is ON for the mode, re-apply the saved
    /// tuning (boost/temp/TGP, power, clock lock, VRAM lock, clock offsets) so it
    /// persists across mode switches, Eco->Standard and reboots. When OFF, return
    /// the dGPU to stock so each non-persisted mode runs at defaults.
    /// </summary>
    // True after this session applied GPU tuning; gates the reset-to-stock
    // path so a cold start with auto_apply_gpu off never clobbers GPU state
    // set by external tools (issue #151).
    private static bool _gpuTuningApplied;

    private void AutoGpuPower(int mode)
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        var nvCtl = App.GpuControl as Gpu.NVidia.LinuxNvidiaGpuControl;

        if (!Helpers.AppConfig.IsMode("auto_apply_gpu"))
        {
            if (_gpuTuningApplied)
            {
                ResetGpuTuning(wmi, nvCtl);
                _gpuTuningApplied = false;
            }
            return;
        }

        // Ensure FANM=4 for GPU PPT writes (nv_dynamic_boost, nv_temp_target,
        // etc.) when fan curves are not auto-applied. See AutoCpuPower comment.
        if (!Helpers.AppConfig.IsMode("auto_apply_fans"))
            wmi.EnsureManualFanMode();

        int maxGpuBoost = GetMaxGpuBoost();

        // Restart nvidia-powerd once after the batch, not per attribute.
        bool nvAttrWritten = false;

        int nvBoost = Helpers.AppConfig.GetMode("gpu_boost");
        if (nvBoost > maxGpuBoost)
            nvBoost = maxGpuBoost;
        if (nvBoost > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvDynamicBoost))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvDynamicBoost, nvBoost);
            nvAttrWritten = true;
            Helpers.Logger.WriteLine($"AutoGpuPower: GPU boost = {nvBoost}W (max={maxGpuBoost}W)");
        }

        int nvTemp = Helpers.AppConfig.GetMode("gpu_temp");
        if (nvTemp > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvTempTarget))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvTempTarget, nvTemp);
            nvAttrWritten = true;
        }

        int nvBaseTgp = Helpers.AppConfig.GetMode("gpu_base_tgp");
        if (nvBaseTgp > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvBaseTgp))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvBaseTgp, nvBaseTgp);
            nvAttrWritten = true;
            Helpers.Logger.WriteLine($"AutoGpuPower: nv_base_tgp = {nvBaseTgp}W");
        }

        int nvTgp = Helpers.AppConfig.GetMode("gpu_tgp");
        if (nvTgp > 0 && wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.NvTgp))
        {
            wmi.SetPptLimit(Platform.Linux.AsusAttributes.NvTgp, nvTgp);
            nvAttrWritten = true;
            Helpers.Logger.WriteLine($"AutoGpuPower: nv_tgp = {nvTgp}W");
        }

        if (nvAttrWritten)
            Gpu.GPUModeControl.RefreshNvidiaPowerd();

        if (nvCtl != null && nvCtl.IsAvailable())
        {
            int gpuPowerLim = Helpers.AppConfig.GetMode("gpu_power_lim");
            int gpuClockLock = Helpers.AppConfig.GetMode("gpu_clock_lock");
            int gpuMemLock = Helpers.AppConfig.GetMode("gpu_mem_clock_lock");
            int gpuClockCore = Helpers.AppConfig.GetMode("gpu_clock_core");
            int gpuClockMem = Helpers.AppConfig.GetMode("gpu_clock_mem");
            bool nvmlOk = nvCtl.IsClockOffsetSupported();
            bool haveOffsets = nvmlOk && (gpuClockCore != 0 || gpuClockMem != 0);
            if (gpuPowerLim > 0 || haveOffsets || gpuClockLock > 0)
            {
                nvCtl.ApplyAll(
                    gpuPowerLim > 0 ? gpuPowerLim : null,
                    gpuClockLock > 0 ? gpuClockLock : 0,
                    haveOffsets ? gpuClockCore : (int?)null,
                    haveOffsets ? gpuClockMem : (int?)null,
                    interactive: false);
            }
            nvCtl.ApplyMemClockLock(gpuMemLock > 0 ? gpuMemLock : 0, interactive: false);
        }

        _gpuTuningApplied = true;
    }

    /// <summary>Return the dGPU to stock: default boost/temp/TGP, default power
    /// limit, unlocked GPU + VRAM clocks, zero clock offsets.</summary>
    private static void ResetGpuTuning(Platform.IHardwareControl wmi, Gpu.NVidia.LinuxNvidiaGpuControl? nv)
    {
        bool nvAttrWritten = WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvDynamicBoost);
        nvAttrWritten |= WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvTempTarget);
        nvAttrWritten |= WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvBaseTgp);
        nvAttrWritten |= WriteFwAttrDefault(wmi, Platform.Linux.AsusAttributes.NvTgp);

        if (nvAttrWritten)
            Gpu.GPUModeControl.RefreshNvidiaPowerd();

        if (nv != null && nv.IsAvailable())
        {
            int? defaultW = nv.GetPowerLimits()?.defaultW;
            bool nvmlOk = nv.IsClockOffsetSupported();
            nv.ApplyAll(defaultW, 0, nvmlOk ? 0 : (int?)null, nvmlOk ? 0 : (int?)null, interactive: false);
            nv.ApplyMemClockLock(0, interactive: false);
        }
    }

    /// <summary>Returns true when the attribute was actually written.</summary>
    private static bool WriteFwAttrDefault(Platform.IHardwareControl wmi, Platform.Linux.AttrDef attr)
    {
        if (!wmi.IsFeatureSupported(attr))
            return false;
        var range = wmi.GetAttributeRange(attr);
        if (range == null || range.Default <= 0)
            return false;
        wmi.SetPptLimit(attr, range.Default);
        return true;
    }

    /// <summary>
    /// Re-apply (or reset, per the auto-apply flag) the current mode's GPU tuning.
    /// Called when the dGPU is re-enabled (Eco -> Standard) so persistence survives
    /// a GPU-mode toggle without a full performance-mode re-apply.
    /// </summary>
    public void ReapplyGpuForCurrentMode()
    {
        try
        {
            App.RefreshGpuControlIfMissing();
            AutoGpuPower(Modes.GetCurrent());
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("ReapplyGpuForCurrentMode failed", ex);
        }
    }

    // CPU undervolt - vendor dispatch. AMD uses the Ryzen Curve Optimizer
    // (RyzenSmu, CO steps); Intel uses the OC voltage-offset mailbox
    // (IntelUndervolt, millivolts). Exactly one backend is available per machine.

    /// <summary>Apply or reset CPU undervolt for the current mode (the "auto_uv" flag).</summary>
    public void AutoCpuUndervolt()
    {
        if (App.IntelUv?.IsAvailable == true)
        {
            if (Helpers.AppConfig.IsMode("auto_uv"))
                SetIntelUv();
            else
                App.IntelUv.Reset();
            return;
        }
        AutoRyzen();
    }

    /// <summary>Apply the current mode's saved CPU undervolt (UI "Apply" button).</summary>
    public void ApplyCpuUndervolt()
    {
        if (App.IntelUv?.IsAvailable == true)
            SetIntelUv();
        else
            SetRyzen();
    }

    /// <summary>Reset CPU undervolt to stock voltage (UI "Reset" button).</summary>
    public void ResetCpuUndervolt()
    {
        if (App.IntelUv?.IsAvailable == true)
            App.IntelUv.Reset();
        else
            ResetRyzen();
    }

    /// <summary>Apply the current mode's saved cpu_uv_mv offset via the Intel mailbox.</summary>
    private void SetIntelUv()
    {
        int mv = Helpers.AppConfig.GetMode("cpu_uv_mv", 0);
        mv = Math.Clamp(mv, Platform.Linux.IntelUndervolt.MinOffsetMv, Platform.Linux.IntelUndervolt.MaxOffsetMv);
        if (App.IntelUv?.Apply(mv) == true)
            Helpers.Logger.WriteLine($"Intel UV: cpu_uv_mv={mv} applied");
        else
            Helpers.Logger.WriteLine($"Intel UV: cpu_uv_mv={mv} apply FAILED (locked or unsupported)");
    }

    // Ryzen Curve Optimizer undervolt (mirrors Windows ModeControl.AutoRyzen/SetRyzen/ResetRyzen/SetUV)

    /// <summary>Apply or reset CPU undervolt for the current mode, based on "auto_uv" flag.</summary>
    public void AutoRyzen()
    {
        if (App.Smu == null || !App.Smu.IsAvailable)
            return;
        if (Helpers.AppConfig.IsMode("auto_uv"))
            SetRyzen();
        else
            ResetRyzen();
    }

    /// <summary>Apply the current mode's saved cpu_uv value to the SMU.</summary>
    public void SetRyzen()
    {
        int cpuUV = Helpers.AppConfig.GetMode("cpu_uv", 0);
        SetUV(cpuUV);
        if (App.Smu?.IsIGpuSupported == true)
        {
            int igpuUV = Helpers.AppConfig.GetMode("igpu_uv", 0);
            if (igpuUV != 0)
            {
                if (App.Smu.SetIGpuCoAll(igpuUV))
                    Helpers.Logger.WriteLine($"Ryzen iGPU UV: igpu_uv={igpuUV} applied");
                else
                    Helpers.Logger.WriteLine($"Ryzen iGPU UV: igpu_uv={igpuUV} apply FAILED");
            }
        }
    }

    /// <summary>Reset CPU undervolt to 0 (stock voltage).</summary>
    public void ResetRyzen()
    {
        SetUV(0);
        if (App.Smu?.IsIGpuSupported == true)
            App.Smu.SetIGpuCoAll(0);
    }

    private static void SetUV(int cpuUV)
    {
        cpuUV = Math.Clamp(cpuUV, Platform.Linux.RyzenSmu.MinCPUUV, Platform.Linux.RyzenSmu.MaxCPUUV);
        if (App.Smu?.SetCoAll(cpuUV) == true)
            Helpers.Logger.WriteLine($"Ryzen UV: cpu_uv={cpuUV} applied");
        else
            Helpers.Logger.WriteLine($"Ryzen UV: cpu_uv={cpuUV} apply FAILED");
    }

    /// <summary>
    /// Read back PPT values after writing to confirm they took effect.
    /// Logs all current values and warns if any expected value doesn't match.
    /// This helps diagnose dual-backend issues and permission problems.
    /// </summary>
    private static void VerifyPptLimits(Platform.IHardwareControl wmi, int expectedPl1, int expectedPl2, int expectedFppt, int expectedApuPlat)
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

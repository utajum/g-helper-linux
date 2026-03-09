using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Fan curve editor and power limits window.
/// Linux port of G-Helper's Fans form.
/// </summary>
public partial class FansWindow : Window
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    private readonly DispatcherTimer _sensorTimer;

    public FansWindow()
    {
        InitializeComponent();

        // Wire up curve change events
        chartCPU.CurveChanged += (_, curve) => OnCurveChanged(0, curve);
        chartGPU.CurveChanged += (_, curve) => OnCurveChanged(1, curve);
        chartMid.CurveChanged += (_, curve) => OnCurveChanged(2, curve);

        _sensorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sensorTimer.Tick += (_, _) => RefreshSensors();

        Loaded += (_, _) =>
        {
            LoadFanCurves();
            LoadPowerLimits();
            RefreshBoostButton();
            RefreshSensors();
            _sensorTimer.Start();
        };

        Closing += (_, _) => _sensorTimer.Stop();
    }

    // ── Fan Curves ──

    private void LoadFanCurves()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        // Try reading current curves from hardware
        byte[]? cpuCurve = wmi.GetFanCurve(0);
        byte[]? gpuCurve = wmi.GetFanCurve(1);

        // Fall back to config or defaults if hardware returned no usable data
        if (!IsValidCurve(cpuCurve))
        {
            cpuCurve = Helpers.AppConfig.GetFanConfig(0);
            if (!IsValidCurve(cpuCurve))
                cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        }

        if (!IsValidCurve(gpuCurve))
        {
            gpuCurve = Helpers.AppConfig.GetFanConfig(1);
            if (!IsValidCurve(gpuCurve))
                gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        }

        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;

        // Mid fan detection — show chart if curve is valid or RPM is readable
        // (matches Windows G-Helper's InitFans logic)
        byte[]? midCurve = wmi.GetFanCurve(2);
        bool hasMidFan = IsValidCurve(midCurve) || wmi.GetFanRpm(2) > 0;

        if (hasMidFan)
        {
            if (!IsValidCurve(midCurve))
            {
                midCurve = Helpers.AppConfig.GetFanConfig(2);
                if (!IsValidCurve(midCurve))
                    midCurve = Helpers.AppConfig.GetDefaultCurve(2);
            }

            chartMid.CurveData = midCurve;
            chartMid.IsVisible = true;
            // Change third row from Auto to Star so all 3 charts share space equally
            chartGrid.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
            this.Height = 820;

            Helpers.AppConfig.Set("mid_fan", 1);
        }
        else
        {
            Helpers.AppConfig.Set("mid_fan", 0);
        }

        // Update mode label
        int mode = App.Wmi?.GetThrottleThermalPolicy() ?? -1;
        string modeName = mode switch
        {
            0 => "Balanced",
            1 => "Turbo",
            2 => "Silent",
            _ => "Unknown"
        };
        labelMode.Text = $"Mode: {modeName}";

        checkApplyFans.IsChecked = Helpers.AppConfig.IsMode("auto_apply_fans");

        UpdateDisabledState();
    }

    private void OnCurveChanged(int fanIndex, byte[] curve)
    {
        // Save to config
        Helpers.AppConfig.SetFanConfig(fanIndex, curve);

        // Auto-apply if enabled
        if (checkApplyFans.IsChecked == true)
        {
            App.Wmi?.SetFanCurve(fanIndex, curve);
        }
    }

    private void ButtonApplyFans_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        if (chartCPU.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(0, chartCPU.CurveData);
            Helpers.AppConfig.SetFanConfig(0, chartCPU.CurveData);
        }

        if (chartGPU.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(1, chartGPU.CurveData);
            Helpers.AppConfig.SetFanConfig(1, chartGPU.CurveData);
        }

        if (chartMid.IsVisible && chartMid.CurveData is { Length: 16 })
        {
            wmi.SetFanCurve(2, chartMid.CurveData);
            Helpers.AppConfig.SetFanConfig(2, chartMid.CurveData);
        }

        UpdateDisabledState();
        Helpers.Logger.WriteLine("Fan curves applied");
    }

    private void ButtonReset_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;

        // Phase 1: Reset ALL fans to factory defaults (pwm_enable=3).
        // Must do all resets before any re-apply because the kernel quirk
        // causes pwm_enable=3 on one fan to reset ALL fans.
        byte[]? cpuCurve = wmi?.ResetFanCurveToDefaults(0);
        byte[]? gpuCurve = wmi?.ResetFanCurveToDefaults(1);
        byte[]? midCurve = chartMid.IsVisible ? wmi?.ResetFanCurveToDefaults(2) : null;

        // Fall back to hardcoded defaults if kernel reset unsupported
        if (!IsValidCurve(cpuCurve))
            cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        if (!IsValidCurve(gpuCurve))
            gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        if (chartMid.IsVisible && !IsValidCurve(midCurve))
            midCurve = Helpers.AppConfig.GetDefaultCurve(2);

        // Phase 2: Update UI and save config
        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;
        Helpers.AppConfig.SetFanConfig(0, cpuCurve!);
        Helpers.AppConfig.SetFanConfig(1, gpuCurve!);

        if (chartMid.IsVisible)
        {
            chartMid.CurveData = midCurve;
            Helpers.AppConfig.SetFanConfig(2, midCurve!);
        }

        // Phase 3: Re-apply ALL curves as active custom curves (pwm_enable=1).
        // Done after all resets so no subsequent pwm_enable=3 undoes them.
        if (cpuCurve is { Length: 16 }) wmi?.SetFanCurve(0, cpuCurve);
        if (gpuCurve is { Length: 16 }) wmi?.SetFanCurve(1, gpuCurve);
        if (chartMid.IsVisible && midCurve is { Length: 16 }) wmi?.SetFanCurve(2, midCurve);

        UpdateDisabledState();
        Helpers.Logger.WriteLine("Fan curves reset to firmware defaults and re-applied");
    }

    private void ButtonDisable_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        wmi.DisableFanCurve(0);
        wmi.DisableFanCurve(1);
        if (chartMid.IsVisible) wmi.DisableFanCurve(2);
        UpdateDisabledState();

        Helpers.Logger.WriteLine("Custom fan curves disabled, using firmware defaults");
    }

    private void UpdateDisabledState()
    {
        var wmi = App.Wmi;
        bool cpuEnabled = wmi?.IsFanCurveEnabled(0) ?? false;
        bool gpuEnabled = wmi?.IsFanCurveEnabled(1) ?? false;
        bool midEnabled = !chartMid.IsVisible || (wmi?.IsFanCurveEnabled(2) ?? false);
        bool anyDisabled = !cpuEnabled || !gpuEnabled || !midEnabled;

        chartCPU.Disabled = !cpuEnabled;
        chartGPU.Disabled = !gpuEnabled;
        if (chartMid.IsVisible) chartMid.Disabled = !midEnabled;

        // Toggle button visual — accent border when disabled (active state)
        buttonDisable.BorderBrush = anyDisabled ? AccentBrush : TransparentBrush;
        buttonDisable.BorderThickness = new Avalonia.Thickness(2);
    }

    private void CheckApplyFans_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyFans.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_fans", enabled ? 1 : 0);
    }

    private void CheckApplyPower_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyPower.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_power", enabled ? 1 : 0);
    }

    // ── Power Limits ──

    private void LoadPowerLimits()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        // Read from hardware, fall back to saved config
        int pl1 = wmi.GetPptLimit(Platform.Linux.AsusAttributes.PptPl1Spl);
        if (pl1 <= 0) pl1 = Helpers.AppConfig.GetMode("limit_slow");

        int pl2 = wmi.GetPptLimit(Platform.Linux.AsusAttributes.PptPl2Sppt);
        if (pl2 <= 0) pl2 = Helpers.AppConfig.GetMode("limit_fast");

        if (pl1 > 0)
        {
            sliderPL1.Value = pl1;
            labelPL1.Text = $"{pl1}W";
        }

        if (pl2 > 0)
        {
            sliderPL2.Value = pl2;
            labelPL2.Text = $"{pl2}W";
        }

        // fPPT (fast boost) — only show if supported
        bool hasFppt = wmi.IsFeatureSupported(Platform.Linux.AsusAttributes.PptFppt);
        gridFppt.IsVisible = hasFppt;
        if (hasFppt)
        {
            int fppt = wmi.GetPptLimit(Platform.Linux.AsusAttributes.PptFppt);
            if (fppt <= 0) fppt = Helpers.AppConfig.GetMode("limit_fppt");
            if (fppt > 0)
            {
                sliderFppt.Value = fppt;
                labelFppt.Text = $"{fppt}W";
            }
        }

        checkApplyPower.IsChecked = Helpers.AppConfig.IsMode("auto_apply_power");
    }

    private void SliderPL1_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int watts = (int)e.NewValue;
        labelPL1.Text = $"{watts}W";
        App.Wmi?.SetPptLimit(Platform.Linux.AsusAttributes.PptPl1Spl, watts);
        Helpers.AppConfig.SetMode("limit_slow", watts);
    }

    private void SliderPL2_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int watts = (int)e.NewValue;
        labelPL2.Text = $"{watts}W";
        App.Wmi?.SetPptLimit(Platform.Linux.AsusAttributes.PptPl2Sppt, watts);
        Helpers.AppConfig.SetMode("limit_fast", watts);
    }

    private void SliderFppt_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int watts = (int)e.NewValue;
        labelFppt.Text = $"{watts}W";
        App.Wmi?.SetPptLimit(Platform.Linux.AsusAttributes.PptFppt, watts);
        Helpers.AppConfig.SetMode("limit_fppt", watts);
    }

    // ── CPU Boost ──

    private void RefreshBoostButton()
    {
        var power = App.Power;
        if (power == null) return;

        bool boostEnabled = power.GetCpuBoost();
        SetBoostButtonState(boostEnabled);
    }

    private void SetBoostButtonState(bool boostOn)
    {
        buttonBoostOn.BorderBrush = boostOn ? AccentBrush : TransparentBrush;
        buttonBoostOn.BorderThickness = new Avalonia.Thickness(2);
        buttonBoostOff.BorderBrush = !boostOn ? AccentBrush : TransparentBrush;
        buttonBoostOff.BorderThickness = new Avalonia.Thickness(2);
    }

    private void ButtonBoostOn_Click(object? sender, RoutedEventArgs e)
    {
        App.Power?.SetCpuBoost(true);
        SetBoostButtonState(true);
    }

    private void ButtonBoostOff_Click(object? sender, RoutedEventArgs e)
    {
        App.Power?.SetCpuBoost(false);
        SetBoostButtonState(false);
    }

    // ── Sensor refresh ──

    private void RefreshSensors()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null) return;

            int cpuTemp = wmi.DeviceGet(0x00120094);
            int gpuTemp = wmi.DeviceGet(0x00120097);
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);
            int midFan = wmi.GetFanRpm(2);

            // GPU load: only show when dGPU is active (not in Eco mode)
            string gpuLoadStr = "";
            bool isEcoMode = wmi.GetGpuEco();
            if (!isEcoMode && App.GpuControl?.IsAvailable() == true)
            {
                try
                {
                    int? gpuLoad = App.GpuControl.GetGpuUse();
                    if (gpuLoad.HasValue && gpuLoad.Value >= 0)
                        gpuLoadStr = $" Load: {gpuLoad.Value}%";
                }
                catch (Exception)
                {
                    // Silently ignore GPU query errors during transitions
                    Helpers.Logger.WriteLine("FansWindow: GPU load query failed");
                }
            }

            string info = $"CPU: {(cpuTemp > 0 ? $"{cpuTemp}°C" : "--")} / {(cpuFan > 0 ? $"{cpuFan} RPM" : "--")}   " +
                          $"GPU: {(gpuTemp > 0 ? $"{gpuTemp}°C" : "--")}{gpuLoadStr} / {(gpuFan > 0 ? $"{gpuFan} RPM" : "--")}";

            if (midFan > 0)
                info += $"   Mid: {midFan} RPM";

            labelSensors.Text = info;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("FansWindow sensor refresh error", ex);
        }
    }

    /// <summary>
    /// Validate a fan curve read from hardware or config.
    /// Rejects null, wrong length, and completely-zero curves.
    /// Matches Windows G-Helper's IsEmptyCurve: a curve is invalid only if ALL 16 bytes are 0.
    /// Note: CPU/GPU fan curves from the Linux kernel often have all-zero temperatures but
    /// valid PWM duty cycles — these are valid curves (GetFanCurve synthesizes a temp ramp).
    /// </summary>
    private static bool IsValidCurve(byte[]? curve)
    {
        if (curve == null || curve.Length != 16) return false;

        // Reject only if every byte is zero (no useful data at all)
        for (int i = 0; i < 16; i++)
        {
            if (curve[i] > 0) return true;
        }
        return false;
    }
}

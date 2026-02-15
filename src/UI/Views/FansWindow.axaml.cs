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

        // Fall back to config or defaults
        if (cpuCurve == null || cpuCurve.Length != 16)
        {
            cpuCurve = Helpers.AppConfig.GetFanConfig(0);
            if (cpuCurve.Length != 16)
                cpuCurve = Helpers.AppConfig.GetDefaultCurve(0);
        }

        if (gpuCurve == null || gpuCurve.Length != 16)
        {
            gpuCurve = Helpers.AppConfig.GetFanConfig(1);
            if (gpuCurve.Length != 16)
                gpuCurve = Helpers.AppConfig.GetDefaultCurve(1);
        }

        chartCPU.CurveData = cpuCurve;
        chartGPU.CurveData = gpuCurve;

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

        Helpers.Logger.WriteLine("Fan curves applied");
    }

    private void ButtonReset_Click(object? sender, RoutedEventArgs e)
    {
        chartCPU.CurveData = Helpers.AppConfig.GetDefaultCurve(0);
        chartGPU.CurveData = Helpers.AppConfig.GetDefaultCurve(1);
        Helpers.Logger.WriteLine("Fan curves reset to defaults");
    }

    private void CheckApplyFans_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkApplyFans.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_fans", enabled ? 1 : 0);
    }

    // ── Power Limits ──

    private void LoadPowerLimits()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        int pl1 = wmi.GetPptLimit("ppt_pl1_spl");
        int pl2 = wmi.GetPptLimit("ppt_pl2_sppt");

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
    }

    private void SliderPL1_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int watts = (int)e.NewValue;
        labelPL1.Text = $"{watts}W";
        App.Wmi?.SetPptLimit("ppt_pl1_spl", watts);
    }

    private void SliderPL2_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int watts = (int)e.NewValue;
        labelPL2.Text = $"{watts}W";
        App.Wmi?.SetPptLimit("ppt_pl2_sppt", watts);
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
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Main settings window — Linux port of G-Helper's SettingsForm.
/// Mirrors the panel layout: Performance → GPU → Screen → Keyboard → Battery → Footer
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private int _currentPerfMode = -1;
    private int _currentGpuMode = -1;  // 0=Eco, 1=Standard, 2=Optimized

    // Accent colors matching G-Helper's RForm.cs
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush EcoBrush = new SolidColorBrush(Color.Parse("#06B48A"));
    private static readonly IBrush StandardBrush = new SolidColorBrush(Color.Parse("#3AAEEF"));
    private static readonly IBrush TurboBrush = new SolidColorBrush(Color.Parse("#FF2020"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    public MainWindow()
    {
        InitializeComponent();

        // Hide from taskbar when closing (tray behavior)
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        // Refresh timer for live sensor data
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => RefreshSensorData();

        // Initial load
        Loaded += (_, _) =>
        {
            RefreshAll();
            _refreshTimer.Start();
        };
    }

    // ── Refresh / Init ──

    private void RefreshAll()
    {
        RefreshPerformanceMode();
        RefreshGpuMode();
        RefreshScreen();
        RefreshBattery();
        RefreshKeyboard();
        RefreshSensorData();
        RefreshFooter();
    }

    private void RefreshSensorData()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null) return;

            int cpuTemp = wmi.DeviceGet(0x00120094); // Temp_CPU
            int gpuTemp = wmi.DeviceGet(0x00120097); // Temp_GPU
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);

            string cpuTempStr = cpuTemp > 0 ? $"{cpuTemp}°C" : "--";
            string gpuTempStr = gpuTemp > 0 ? $"{gpuTemp}°C" : "--";
            string cpuFanStr = cpuFan > 0 ? $"{cpuFan} RPM" : "--";
            string gpuFanStr = gpuFan > 0 ? $"{gpuFan} RPM" : "--";

            labelCPUFan.Text = $"CPU: {cpuTempStr}  Fan: {cpuFanStr}";
            labelGPUFan.Text = $"GPU: {gpuTempStr}  Fan: {gpuFanStr}";

            // Mid fan if available
            int midFan = wmi.GetFanRpm(2);
            if (midFan > 0)
                labelMidFan.Text = $"Mid fan: {midFan} RPM";
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("RefreshSensorData error", ex);
        }
    }

    // ── Performance Mode ──

    private void RefreshPerformanceMode()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        _currentPerfMode = wmi.GetThrottleThermalPolicy();

        string modeName = _currentPerfMode switch
        {
            0 => "Balanced",
            1 => "Turbo",
            2 => "Silent",
            _ => "Unknown"
        };

        labelPerfMode.Text = modeName;
        UpdatePerfButtons();
    }

    private void UpdatePerfButtons()
    {
        SetButtonActive(buttonSilent, _currentPerfMode == 2);
        SetButtonActive(buttonBalanced, _currentPerfMode == 0);
        SetButtonActive(buttonTurbo, _currentPerfMode == 1);
    }

    private void SetPerformanceMode(int mode)
    {
        App.Wmi?.SetThrottleThermalPolicy(mode);
        _currentPerfMode = mode;
        RefreshPerformanceMode();
        Helpers.Logger.WriteLine($"Performance mode → {mode}");
    }

    private void ButtonSilent_Click(object? sender, RoutedEventArgs e) => SetPerformanceMode(2);
    private void ButtonBalanced_Click(object? sender, RoutedEventArgs e) => SetPerformanceMode(0);
    private void ButtonTurbo_Click(object? sender, RoutedEventArgs e) => SetPerformanceMode(1);

    private FansWindow? _fansWindow;

    private void ButtonFans_Click(object? sender, RoutedEventArgs e)
    {
        if (_fansWindow == null || !_fansWindow.IsVisible)
        {
            _fansWindow = new FansWindow();
            _fansWindow.Show();
        }
        else
        {
            _fansWindow.Activate();
        }
    }

    // ── GPU Mode ──

    private void RefreshGpuMode()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        bool ecoEnabled = wmi.GetGpuEco();
        int muxMode = wmi.GetGpuMuxMode();

        if (ecoEnabled)
            _currentGpuMode = 0; // Eco
        else if (muxMode == 0) // dGPU direct
            _currentGpuMode = 2; // Optimized/Ultimate
        else
            _currentGpuMode = 1; // Standard (hybrid)

        string modeName = _currentGpuMode switch
        {
            0 => "Eco (iGPU)",
            1 => "Standard",
            2 => "Optimized",
            _ => "Unknown"
        };

        labelGPUMode.Text = modeName;
        UpdateGpuButtons();

        // GPU tip
        labelTipGPU.Text = _currentGpuMode switch
        {
            0 => "dGPU is off — maximum battery life",
            1 => "Hybrid mode — dGPU powers on when needed",
            2 => "dGPU direct — bypass iGPU for best performance (requires reboot)",
            _ => ""
        };
    }

    private void UpdateGpuButtons()
    {
        SetButtonActive(buttonEco, _currentGpuMode == 0);
        SetButtonActive(buttonStandard, _currentGpuMode == 1);
        SetButtonActive(buttonOptimized, _currentGpuMode == 2);
    }

    private void ButtonEco_Click(object? sender, RoutedEventArgs e)
    {
        App.Wmi?.SetGpuEco(true);
        _currentGpuMode = 0;
        RefreshGpuMode();
    }

    private void ButtonStandard_Click(object? sender, RoutedEventArgs e)
    {
        App.Wmi?.SetGpuEco(false);
        // Don't change MUX — leave it in hybrid mode
        _currentGpuMode = 1;
        RefreshGpuMode();
    }

    private void ButtonOptimized_Click(object? sender, RoutedEventArgs e)
    {
        App.Wmi?.SetGpuEco(false);
        // MUX switch requires reboot notification
        if (App.Wmi?.GetGpuMuxMode() != 0)
        {
            App.Wmi?.SetGpuMuxMode(0);
            labelTipGPU.Text = "MUX switch to dGPU direct — reboot required!";
        }
        _currentGpuMode = 2;
        RefreshGpuMode();
    }

    // ── Screen ──

    private void RefreshScreen()
    {
        var display = App.Display;
        if (display == null) return;

        int hz = display.GetRefreshRate();
        if (hz > 0)
        {
            labelScreenHz.Text = $"{hz} Hz";

            // Update max refresh button label
            var rates = display.GetAvailableRefreshRates();
            if (rates.Count > 0)
            {
                int maxHz = rates[0];
                labelHighRefresh.Text = $"{maxHz}Hz";
            }
        }

        // Check for MiniLED support
        bool hasMiniLed = App.Wmi?.IsFeatureSupported("mini_led_mode") ?? false;
        buttonMiniled.IsVisible = hasMiniLed;
    }

    private void ButtonScreenAuto_Click(object? sender, RoutedEventArgs e)
    {
        // TODO: Toggle auto screen refresh rate
        Helpers.Logger.WriteLine("Screen auto not yet implemented");
    }

    private void Button60Hz_Click(object? sender, RoutedEventArgs e)
    {
        App.Display?.SetRefreshRate(60);
        RefreshScreen();
    }

    private void Button120Hz_Click(object? sender, RoutedEventArgs e)
    {
        var rates = App.Display?.GetAvailableRefreshRates();
        if (rates != null && rates.Count > 0)
            App.Display?.SetRefreshRate(rates[0]); // Use max available
        else
            App.Display?.SetRefreshRate(120);
        RefreshScreen();
    }

    private void ButtonMiniled_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        int current = wmi.GetMiniLedMode();
        int next = current switch
        {
            0 => 1,
            1 => 2,
            _ => 0
        };
        wmi.SetMiniLedMode(next);
        Helpers.Logger.WriteLine($"MiniLED mode → {next}");
    }

    // ── Keyboard ──

    private void RefreshKeyboard()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        int brightness = wmi.GetKeyboardBrightness();
        if (brightness >= 0)
        {
            string level = brightness switch
            {
                0 => "Off",
                1 => "Low",
                2 => "Medium",
                3 => "High",
                _ => $"Level {brightness}"
            };
            labelBacklight.Text = $"Backlight: {level}";
        }
    }

    private void ButtonKeyboard_Click(object? sender, RoutedEventArgs e)
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        int current = wmi.GetKeyboardBrightness();
        int next = (current + 1) % 4; // Cycle 0→1→2→3→0
        wmi.SetKeyboardBrightness(next);
        RefreshKeyboard();
    }

    private void ButtonKeyboardColor_Click(object? sender, RoutedEventArgs e)
    {
        // TODO: Open color picker for RGB keyboards
        Helpers.Logger.WriteLine("Keyboard color picker not yet implemented");
    }

    private void ButtonFnLock_Click(object? sender, RoutedEventArgs e)
    {
        // TODO: Toggle Fn lock
        Helpers.Logger.WriteLine("Fn lock toggle not yet implemented");
    }

    // ── Battery ──

    private void RefreshBattery()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        int limit = wmi.GetBatteryChargeLimit();
        if (limit > 0)
        {
            sliderBattery.Value = limit;
            labelBatteryLimit.Text = $"{limit}%";
            labelBattery.Text = $"Charge limit: {limit}%";
        }

        // Show current charge info from power manager
        var power = App.Power;
        if (power != null)
        {
            int level = power.GetBatteryPercentage();
            bool acPlugged = power.IsOnAcPower();
            if (level >= 0)
            {
                string status = acPlugged ? (level < 100 ? "Charging" : "Plugged in") : "On battery";
                labelCharge.Text = $"{level}% {status}";
            }
        }
    }

    private void SliderBattery_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int limit = (int)e.NewValue;
        labelBatteryLimit.Text = $"{limit}%";
        labelBattery.Text = $"Charge limit: {limit}%";

        // Debounce: only set after user stops dragging
        // For now, set immediately (G-Helper does too)
        App.Wmi?.SetBatteryChargeLimit(limit);
    }

    private void ButtonBattery60_Click(object? sender, RoutedEventArgs e)
    {
        sliderBattery.Value = 60;
        App.Wmi?.SetBatteryChargeLimit(60);
        RefreshBattery();
    }

    private void ButtonBattery80_Click(object? sender, RoutedEventArgs e)
    {
        sliderBattery.Value = 80;
        App.Wmi?.SetBatteryChargeLimit(80);
        RefreshBattery();
    }

    private void ButtonBattery100_Click(object? sender, RoutedEventArgs e)
    {
        sliderBattery.Value = 100;
        App.Wmi?.SetBatteryChargeLimit(100);
        RefreshBattery();
    }

    // ── Footer ──

    private void RefreshFooter()
    {
        var sys = App.System;
        if (sys == null) return;

        string model = sys.GetModelName() ?? "Unknown ASUS";
        labelVersion.Text = $"G-Helper Linux v1.0.0 — {model}";

        // Check autostart status
        checkStartup.IsChecked = sys.IsAutostartEnabled();
    }

    private void CheckStartup_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkStartup.IsChecked ?? false;
        App.System?.SetAutostart(enabled);
    }

    private void ButtonUpdates_Click(object? sender, RoutedEventArgs e)
    {
        // TODO: Open updates check
        Helpers.Logger.WriteLine("Updates check not yet implemented");
    }

    private void ButtonQuit_Click(object? sender, RoutedEventArgs e)
    {
        // Clean shutdown
        App.Input?.Dispose();
        App.Wmi?.Dispose();

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // ── UI Helpers ──

    private static void SetButtonActive(Button button, bool active)
    {
        if (active)
        {
            button.BorderBrush = AccentBrush;
            button.BorderThickness = new Avalonia.Thickness(2);
        }
        else
        {
            button.BorderBrush = TransparentBrush;
            button.BorderThickness = new Avalonia.Thickness(2);
        }
    }
}

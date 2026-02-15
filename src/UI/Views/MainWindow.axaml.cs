using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.USB;
using System.Linq;
using System.Threading.Tasks;

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
        RefreshBattery();
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
            
            // GPU fan: might be RPM or percentage from nvidia-smi
            string gpuFanStr;
            if (gpuFan > 0)
                gpuFanStr = $"{gpuFan} RPM";
            else if (gpuFan <= -2)
            {
                // Encoded percentage: -2 - percent
                int percent = -(gpuFan + 2);
                gpuFanStr = $"{percent}%";
            }
            else
                gpuFanStr = "--";

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
                    // Silently ignore GPU query errors (e.g., GPU being disabled)
                    Helpers.Logger.WriteLine("GPU load query failed (GPU may be transitioning)");
                }
            }

            labelCPUFan.Text = $"CPU: {cpuTempStr}  Fan: {cpuFanStr}";
            labelGPUFan.Text = $"GPU: {gpuTempStr}{gpuLoadStr}  Fan: {gpuFanStr}";

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
        App.Mode?.SetPerformanceMode(mode);
        _currentPerfMode = mode;
        RefreshPerformanceMode();
        App.UpdateTrayIcon();
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
        // Update UI immediately
        _currentGpuMode = 0;
        RefreshGpuMode();
        
        // Fire-and-forget sysfs write
        Task.Run(() =>
        {
            try
            {
                App.Wmi?.SetGpuEco(true);
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Eco mode switch failed: {ex.Message}");
            }
        });
        
        // Show reboot notification immediately
        App.System?.ShowNotification("G-Helper", 
            "Eco mode set — You must reboot for changes to take effect");
        labelTipGPU.Text = "You must reboot for changes to take effect";
    }

    private void ButtonStandard_Click(object? sender, RoutedEventArgs e)
    {
        // Update UI immediately
        _currentGpuMode = 1;
        RefreshGpuMode();
        
        // Fire-and-forget sysfs write
        Task.Run(() =>
        {
            try
            {
                App.Wmi?.SetGpuEco(false);
                // MUX=1 for hybrid mode
                if (App.Wmi?.GetGpuMuxMode() != 1)
                {
                    App.Wmi?.SetGpuMuxMode(1);
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Standard mode switch failed: {ex.Message}");
            }
        });
        
        // Show reboot notification immediately
        App.System?.ShowNotification("G-Helper", 
            "Standard mode set — You must reboot for changes to take effect");
        labelTipGPU.Text = "You must reboot for changes to take effect";
    }

    private void ButtonOptimized_Click(object? sender, RoutedEventArgs e)
    {
        // Update UI immediately
        _currentGpuMode = 2;
        RefreshGpuMode();
        
        // Fire-and-forget sysfs write
        Task.Run(() =>
        {
            try
            {
                App.Wmi?.SetGpuEco(false);
                // MUX=0 for dGPU direct
                if (App.Wmi?.GetGpuMuxMode() != 0)
                {
                    App.Wmi?.SetGpuMuxMode(0);
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Optimized mode switch failed: {ex.Message}");
            }
        });
        
        // Show reboot notification immediately
        App.System?.ShowNotification("G-Helper", 
            "Optimized mode set — You must reboot for changes to take effect");
        labelTipGPU.Text = "You must reboot for changes to take effect";
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
        labelVersion.Text = $"G-Helper Linux v{Helpers.AppConfig.AppVersion} — {model}";

        // Check autostart status
        checkStartup.IsChecked = sys.IsAutostartEnabled();
    }

    private void CheckStartup_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkStartup.IsChecked ?? false;
        App.System?.SetAutostart(enabled);
    }

    private ExtraWindow? _extraWindow;

    private void ButtonExtra_Click(object? sender, RoutedEventArgs e)
    {
        if (_extraWindow == null || !_extraWindow.IsVisible)
        {
            _extraWindow = new ExtraWindow();
            _extraWindow.Show();
        }
        else
        {
            _extraWindow.Activate();
        }
    }

    private UpdatesWindow? _updatesWindow;

    private void ButtonUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (_updatesWindow == null || !_updatesWindow.IsVisible)
        {
            _updatesWindow = new UpdatesWindow();
            _updatesWindow.Show();
        }
        else
        {
            _updatesWindow.Activate();
        }
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

    // ── Stub Event Handlers (TODO: Implement) ──

    private void ButtonScreenAuto_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.Logger.WriteLine("Screen Auto mode clicked — not yet implemented");
    }

    private void Button60Hz_Click(object? sender, RoutedEventArgs e)
    {
        App.Display?.SetRefreshRate(60);
        Helpers.Logger.WriteLine("Screen refresh rate set to 60Hz");
    }

    private void Button120Hz_Click(object? sender, RoutedEventArgs e)
    {
        App.Display?.SetRefreshRate(165); // Or get max from display
        Helpers.Logger.WriteLine("Screen refresh rate set to 165Hz");
    }

    private void ButtonMiniled_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.Logger.WriteLine("MiniLED mode clicked — not yet implemented");
    }

    private void ButtonKeyboard_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            int current = App.Wmi?.GetKeyboardBrightness() ?? 0;
            int next = (current + 1) % 4; // Cycle 0->1->2->3->0
            App.Wmi?.SetKeyboardBrightness(next);
            Helpers.Logger.WriteLine($"Keyboard backlight: {current} -> {next}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("Failed to cycle keyboard brightness", ex);
        }
    }

    private void ComboAuraMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // TODO: Implement AURA mode change
    }

    private void ComboAuraSpeed_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // TODO: Implement AURA speed change
    }

    private void ButtonColor1_Click(object? sender, RoutedEventArgs e)
    {
        // TODO: Show color picker for primary color
    }

    private void ButtonColor2_Click(object? sender, RoutedEventArgs e)
    {
        // TODO: Show color picker for secondary color
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

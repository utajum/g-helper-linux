using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.USB;

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

    // ── Keyboard / AURA ──

    private bool _auraInitialized = false;
    private bool _suppressAuraEvents = false;

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

        // Init FnLock button state
        UpdateFnLockButton(Helpers.AppConfig.Is("fn_lock"));

        InitAura();
    }

    private void InitAura()
    {
        if (_auraInitialized) return;
        _auraInitialized = true;

        bool hasAura = Aura.IsAvailable();
        panelAura.IsVisible = hasAura;

        if (!hasAura)
        {
            Helpers.Logger.WriteLine("No AURA HID device found — RGB controls hidden");
            return;
        }

        Helpers.Logger.WriteLine("AURA HID device found — initializing RGB controls");

        // Load saved values
        Aura.Mode = (AuraMode)Helpers.AppConfig.Get("aura_mode");
        Aura.Speed = (AuraSpeed)Helpers.AppConfig.Get("aura_speed");
        Aura.SetColor(Helpers.AppConfig.Get("aura_color", unchecked((int)0xFFFFFFFF)));
        Aura.SetColor2(Helpers.AppConfig.Get("aura_color2", 0));

        _suppressAuraEvents = true;

        // Populate mode combo
        var modes = Aura.GetModes();
        comboAuraMode.Items.Clear();
        int selectedModeIdx = 0;
        int idx = 0;
        foreach (var kv in modes)
        {
            comboAuraMode.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if (kv.Key == Aura.Mode) selectedModeIdx = idx;
            idx++;
        }
        comboAuraMode.SelectedIndex = selectedModeIdx;

        // Populate speed combo
        var speeds = Aura.GetSpeeds();
        comboAuraSpeed.Items.Clear();
        int selectedSpeedIdx = 0;
        idx = 0;
        foreach (var kv in speeds)
        {
            comboAuraSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if (kv.Key == Aura.Speed) selectedSpeedIdx = idx;
            idx++;
        }
        comboAuraSpeed.SelectedIndex = selectedSpeedIdx;

        _suppressAuraEvents = false;

        // Update color button backgrounds and second color visibility
        UpdateColorButtons();
    }

    private void UpdateColorButtons()
    {
        buttonColor1.Background = new SolidColorBrush(
            Color.FromRgb(Aura.ColorR, Aura.ColorG, Aura.ColorB));
        buttonColor2.Background = new SolidColorBrush(
            Color.FromRgb(Aura.Color2R, Aura.Color2G, Aura.Color2B));
        buttonColor2.IsVisible = Aura.HasSecondColor();

        // Hide color buttons for modes that don't use color
        buttonColor1.IsVisible = Aura.UsesColor();
    }

    private void ComboAuraMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuraEvents) return;
        if (comboAuraMode.SelectedItem is ComboBoxItem item && item.Tag is int modeVal)
        {
            Helpers.AppConfig.Set("aura_mode", modeVal);
            Aura.Mode = (AuraMode)modeVal;
            UpdateColorButtons();
            ApplyAuraAsync();
        }
    }

    private void ComboAuraSpeed_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuraEvents) return;
        if (comboAuraSpeed.SelectedItem is ComboBoxItem item && item.Tag is int speedVal)
        {
            Helpers.AppConfig.Set("aura_speed", speedVal);
            Aura.Speed = (AuraSpeed)speedVal;
            ApplyAuraAsync();
        }
    }

    private void ButtonColor1_Click(object? sender, RoutedEventArgs e)
    {
        ShowColorPicker("aura_color", Aura.ColorR, Aura.ColorG, Aura.ColorB, (r, g, b) =>
        {
            Aura.ColorR = r;
            Aura.ColorG = g;
            Aura.ColorB = b;
            Helpers.AppConfig.Set("aura_color", Aura.GetColorArgb());
            UpdateColorButtons();
            ApplyAuraAsync();
        });
    }

    private void ButtonColor2_Click(object? sender, RoutedEventArgs e)
    {
        ShowColorPicker("aura_color2", Aura.Color2R, Aura.Color2G, Aura.Color2B, (r, g, b) =>
        {
            Aura.Color2R = r;
            Aura.Color2G = g;
            Aura.Color2B = b;
            Helpers.AppConfig.Set("aura_color2", Aura.GetColor2Argb());
            UpdateColorButtons();
            ApplyAuraAsync();
        });
    }

    /// <summary>
    /// Shows a simple color picker window.
    /// Avalonia doesn't have a built-in color dialog, so we use a popup with sliders.
    /// </summary>
    private void ShowColorPicker(string configKey, byte initR, byte initG, byte initB, Action<byte, byte, byte> onColorSet)
    {
        var pickerWindow = new Window
        {
            Title = "Pick Color",
            Width = 320,
            Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
            CanResize = false,
            SystemDecorations = SystemDecorations.Full,
        };

        var preview = new Border
        {
            Width = 280,
            Height = 50,
            CornerRadius = new Avalonia.CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(initR, initG, initB)),
            Margin = new Avalonia.Thickness(0, 8, 0, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        var sliderR = new Slider { Minimum = 0, Maximum = 255, Value = initR, Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)) };
        var sliderG = new Slider { Minimum = 0, Maximum = 255, Value = initG, Foreground = new SolidColorBrush(Color.FromRgb(80, 255, 80)) };
        var sliderB = new Slider { Minimum = 0, Maximum = 255, Value = initB, Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 255)) };

        var labelR = new TextBlock { Text = $"R: {initR}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 0) };
        var labelG = new TextBlock { Text = $"G: {initG}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 0) };
        var labelB = new TextBlock { Text = $"B: {initB}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 0) };

        void UpdatePreview()
        {
            byte r = (byte)sliderR.Value;
            byte g = (byte)sliderG.Value;
            byte b = (byte)sliderB.Value;
            preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            labelR.Text = $"R: {r}";
            labelG.Text = $"G: {g}";
            labelB.Text = $"B: {b}";
        }

        sliderR.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderG.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderB.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };

        var btnOk = new Button { Content = "Apply", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 8, 0, 0), MinWidth = 100 };
        btnOk.Click += (_, _) =>
        {
            onColorSet((byte)sliderR.Value, (byte)sliderG.Value, (byte)sliderB.Value);
            pickerWindow.Close();
        };

        // Quick preset colors
        var presetPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Spacing = 4, Margin = new Avalonia.Thickness(0, 4) };
        var presets = new (byte R, byte G, byte B)[]
        {
            (255, 255, 255), (255, 0, 0), (0, 255, 0), (0, 0, 255),
            (255, 255, 0), (0, 255, 255), (255, 0, 255), (255, 128, 0),
        };
        foreach (var (pr, pg, pb) in presets)
        {
            var btn = new Button
            {
                Width = 28, Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(pr, pg, pb)),
                Margin = new Avalonia.Thickness(1),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            };
            byte cr = pr, cg = pg, cb = pb;
            btn.Click += (_, _) =>
            {
                sliderR.Value = cr;
                sliderG.Value = cg;
                sliderB.Value = cb;
            };
            presetPanel.Children.Add(btn);
        }

        var stack = new StackPanel { Margin = new Avalonia.Thickness(16, 8) };
        stack.Children.Add(preview);
        stack.Children.Add(presetPanel);
        stack.Children.Add(labelR);
        stack.Children.Add(sliderR);
        stack.Children.Add(labelG);
        stack.Children.Add(sliderG);
        stack.Children.Add(labelB);
        stack.Children.Add(sliderB);
        stack.Children.Add(btnOk);

        pickerWindow.Content = stack;
        pickerWindow.ShowDialog(this);
    }

    private void ApplyAuraAsync()
    {
        Task.Run(() =>
        {
            try
            {
                Aura.ApplyAura();
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("ApplyAura error", ex);
            }
        });
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

    private void ButtonFnLock_Click(object? sender, RoutedEventArgs e)
    {
        bool current = Helpers.AppConfig.Is("fn_lock");
        bool newState = !current;

        Helpers.AppConfig.Set("fn_lock", newState ? 1 : 0);

        // Try hardware FnLock via sysfs
        var fnLockPath = "/sys/devices/platform/asus-nb-wmi/fn_lock";
        bool hasSysfs = Platform.Linux.SysfsHelper.Exists(fnLockPath);

        if (hasSysfs)
        {
            bool inverted = Helpers.AppConfig.IsInvertedFNLock();
            int writeVal = (newState ^ inverted) ? 1 : 0;
            Platform.Linux.SysfsHelper.WriteInt(fnLockPath, writeVal);
            Helpers.Logger.WriteLine($"FnLock sysfs → {writeVal}");
        }

        // Also try HID path for devices that need it
        // [0x5A, 0xD0, 0x4E, 0x00=locked, 0x01=unlocked]
        try
        {
            AsusHid.WriteInput(new byte[]
            {
                AsusHid.INPUT_ID, 0xD0, 0x4E, newState ? (byte)0x00 : (byte)0x01
            }, "FnLock");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"FnLock HID write failed: {ex.Message}");
        }

        // Update button visual
        UpdateFnLockButton(newState);

        App.System?.ShowNotification("G-Helper", newState ? "Fn Lock ON" : "Fn Lock OFF");
        Helpers.Logger.WriteLine($"FnLock toggled → {(newState ? "ON" : "OFF")}");
    }

    private void UpdateFnLockButton(bool locked)
    {
        if (locked)
        {
            buttonFnLock.BorderBrush = AccentBrush;
            buttonFnLock.BorderThickness = new Avalonia.Thickness(2);
        }
        else
        {
            buttonFnLock.BorderBrush = TransparentBrush;
            buttonFnLock.BorderThickness = new Avalonia.Thickness(2);
        }
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

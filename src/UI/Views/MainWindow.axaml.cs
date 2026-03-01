using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Gpu;
using GHelper.Linux.USB;
using System.Collections.Generic;
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
    private int _currentGpuMode = -1;  // 0=Eco, 1=Standard, 2=Optimized (auto), 3=Ultimate (MUX=0)

    // Accent colors matching G-Helper's RForm.cs
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush EcoBrush = new SolidColorBrush(Color.Parse("#06B48A"));
    private static readonly IBrush StandardBrush = new SolidColorBrush(Color.Parse("#3AAEEF"));
    private static readonly IBrush TurboBrush = new SolidColorBrush(Color.Parse("#FF2020"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    public MainWindow()
    {
        InitializeComponent();

        // Refresh timer for live sensor data
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => RefreshSensorData();

        // On close: let the window actually close (dispose).
        // Don't cancel — this allows KDE logout/reboot to proceed.
        // The app stays alive via ShutdownMode.OnExplicitShutdown + tray icon.
        // A new MainWindow is created on tray click (see App.ToggleMainWindow).
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            App.MainWindowInstance = null;
        };

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
            string cpuFanStr = cpuFan > 0 ? $"{cpuFan}RPM" : "0RPM";
            
            // GPU fan: might be RPM or percentage from nvidia-smi
            string gpuFanStr;
            if (gpuFan > 0)
                gpuFanStr = $"{gpuFan}RPM";
            else if (gpuFan <= -2)
            {
                // Encoded percentage: -2 - percent
                int percent = -(gpuFan + 2);
                gpuFanStr = $"{percent}%";
            }
            else
                gpuFanStr = "0RPM";

            // Match Windows layout: "CPU: 32°C Fan: 0RPM" on the right
            labelCPUFan.Text = $"CPU: {cpuTempStr} Fan: {cpuFanStr}";

            // GPU fan info — compact for right-aligned display
            string gpuTempStr = gpuTemp > 0 ? $"{gpuTemp}°C" : "";

            // GPU load: only show when dGPU is active (not in Eco mode)
            string gpuLoadStr = "";
            bool isEcoMode = wmi.GetGpuEco();
            if (!isEcoMode && App.GpuControl?.IsAvailable() == true)
            {
                try
                {
                    int? gpuLoad = App.GpuControl.GetGpuUse();
                    if (gpuLoad.HasValue && gpuLoad.Value >= 0)
                        gpuLoadStr = $" {gpuLoad.Value}%";
                }
                catch (Exception)
                {
                    Helpers.Logger.WriteLine("GPU load query failed (GPU may be transitioning)");
                }
            }

            labelGPUFan.Text = gpuTempStr.Length > 0
                ? $"GPU: {gpuTempStr}{gpuLoadStr}  Fan: {gpuFanStr}"
                : $"GPU Fan: {gpuFanStr}";

            // Mid fan if available
            int midFan = wmi.GetFanRpm(2);
            if (midFan > 0)
                labelMidFan.Text = $"Mid Fan: {midFan}RPM";
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("RefreshSensorData error", ex);
        }
    }

    // ── Performance Mode ──

    public void RefreshPerformanceMode()
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

        // Combined header: "Mode: Balanced" (matches Windows layout)
        labelPerf.Text = $"Mode: {modeName}";
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
            if (Helpers.AppConfig.Is("topmost")) _fansWindow.Topmost = true;
            _fansWindow.Show();
        }
        else
        {
            _fansWindow.Activate();
        }
    }

    // ── GPU Mode ──

    /// <summary>Public wrapper for RefreshGpuMode — called from App.cs on power state changes.</summary>
    public void RefreshGpuModePublic() => RefreshGpuMode();

    private void RefreshGpuMode()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        // No discrete GPU → disable all GPU mode buttons, show message
        if (!wmi.IsFeatureSupported("dgpu_disable"))
        {
            buttonEco.IsEnabled = false;
            buttonStandard.IsEnabled = false;
            buttonOptimized.IsEnabled = false;
            buttonUltimate.IsEnabled = false;
            labelGPU.Text = "GPU Mode: N/A";
            labelTipGPU.Text = "No discrete GPU detected";
            return;
        }

        var gpu = App.GpuModeCtrl;

        if (gpu != null)
        {
            var mode = gpu.GetCurrentMode();
            _currentGpuMode = (int)mode;
        }
        else
        {
            // Fallback if controller not initialized
            bool gpuAuto = Helpers.AppConfig.Is("gpu_auto");
            bool ecoEnabled = wmi.GetGpuEco();
            int muxMode = wmi.GetGpuMuxMode();

            if (muxMode == 0)
                _currentGpuMode = 3;
            else if (gpuAuto)
                _currentGpuMode = 2;
            else if (ecoEnabled)
                _currentGpuMode = 0;
            else
                _currentGpuMode = 1;
        }

        string modeName = _currentGpuMode switch
        {
            0 => "Eco",
            1 => "Standard",
            2 => "Optimized",
            3 => "Ultimate",
            _ => "Unknown"
        };

        labelGPU.Text = $"GPU Mode: {modeName}";
        labelGPUMode.Text = modeName;
        UpdateGpuButtons();

        // GPU tip — check for pending reboot first
        if (gpu?.IsPendingReboot() == true)
        {
            string? pending = Helpers.AppConfig.GetString("gpu_mode");
            labelTipGPU.Text = $"{pending?.ToUpperInvariant() ?? "Mode"} pending — reboot to apply";
        }
        else
        {
            labelTipGPU.Text = _currentGpuMode switch
            {
                0 => "dGPU is off — maximum battery life",
                1 => "Hybrid mode — dGPU powers on when needed",
                2 => "Auto Eco on battery, Standard on AC power",
                3 => "dGPU direct — bypass iGPU for best performance (requires reboot)",
                _ => ""
            };
        }

        buttonUltimate.IsVisible = wmi.IsFeatureSupported("gpu_mux_mode");
    }

    private void UpdateGpuButtons()
    {
        SetButtonActive(buttonEco, _currentGpuMode == 0);
        SetButtonActive(buttonStandard, _currentGpuMode == 1);
        SetButtonActive(buttonOptimized, _currentGpuMode == 2);
        SetButtonActive(buttonUltimate, _currentGpuMode == 3);
    }

    /// <summary>
    /// Lock GPU mode buttons during a switch operation (like Windows G-Helper's LockGPUModes).
    /// Writing dgpu_disable can block in the kernel for 30-60 seconds while the GPU powers down.
    /// </summary>
    private void LockGpuButtons(string statusText)
    {
        buttonEco.IsEnabled = false;
        buttonStandard.IsEnabled = false;
        buttonOptimized.IsEnabled = false;
        buttonUltimate.IsEnabled = false;
        labelTipGPU.Text = statusText;
    }

    private void UnlockGpuButtons()
    {
        buttonEco.IsEnabled = true;
        buttonStandard.IsEnabled = true;
        buttonOptimized.IsEnabled = true;
        buttonUltimate.IsEnabled = true;
    }

    /// <summary>
    /// Common handler for all 4 GPU mode buttons.
    /// Locks buttons, calls GpuModeController on background thread,
    /// handles the result on UI thread.
    /// </summary>
    private void RequestGpuModeSwitch(GpuMode target, string switchingText)
    {
        var gpu = App.GpuModeCtrl;
        if (gpu == null) return;

        if (gpu.IsSwitchInProgress)
        {
            // A hardware switch is blocking (buttons should be locked, but be defensive).
            // Save the user's latest choice so it wins after reboot.
            gpu.ScheduleModeForReboot(target);
            _currentGpuMode = (int)target;
            UpdateGpuButtons();
            return;
        }

        // Optimistic UI: highlight the target button immediately
        _currentGpuMode = (int)target;
        UpdateGpuButtons();
        LockGpuButtons(switchingText);

        Task.Run(() =>
        {
            var result = gpu.RequestModeSwitch(target);

            Dispatcher.UIThread.Post(() =>
            {
                UnlockGpuButtons();
                HandleGpuSwitchResult(result, target);
            });
        });
    }

    /// <summary>
    /// Handle GpuSwitchResult on the UI thread — show notifications, update tips, show dialogs.
    /// </summary>
    private void HandleGpuSwitchResult(GpuSwitchResult result, GpuMode target)
    {
        RefreshGpuMode();

        switch (result)
        {
            case GpuSwitchResult.Applied:
                string appliedText = target switch
                {
                    GpuMode.Eco => "Eco mode — dGPU disabled",
                    GpuMode.Standard => "Standard mode — hybrid dGPU",
                    GpuMode.Optimized => "Optimized — auto Eco/Standard based on power",
                    GpuMode.Ultimate => "Ultimate mode — dGPU direct",
                    _ => "GPU mode changed"
                };
                App.System?.ShowNotification("GPU Mode", appliedText, "video-display");
                break;

            case GpuSwitchResult.AlreadySet:
                // No notification needed
                break;

            case GpuSwitchResult.RebootRequired:
                string rebootText = target switch
                {
                    GpuMode.Ultimate => "Ultimate mode set — reboot required",
                    GpuMode.Standard => "Standard mode set — reboot required for MUX change",
                    GpuMode.Optimized => "Optimized mode — reboot required for MUX change",
                    GpuMode.Eco => "Eco mode requires reboot — MUX and GPU changes will apply",
                    _ => "Reboot required for GPU mode change"
                };
                labelTipGPU.Text = target == GpuMode.Optimized
                    ? "MUX switch changed — reboot required, then auto-switching will begin"
                    : "You must reboot for changes to take effect";
                App.System?.ShowNotification("GPU Mode", rebootText, "system-reboot");
                break;

            case GpuSwitchResult.EcoBlocked:
                labelTipGPU.Text = "Eco mode blocked — MUX was changed to Ultimate this session. Reboot first.";
                App.System?.ShowNotification("GPU Mode",
                    "Eco mode blocked: MUX was changed to Ultimate this session. Reboot first, then switch to Eco.",
                    "dialog-warning");
                break;

            case GpuSwitchResult.DriverBlocking:
                ShowDriverBlockingDialog(target);
                break;

            case GpuSwitchResult.Deferred:
                labelTipGPU.Text = "Eco mode pending — reboot to apply";
                App.System?.ShowNotification("GPU Mode",
                    "Eco mode will activate after reboot", "system-reboot");
                break;

            case GpuSwitchResult.Failed:
                App.System?.ShowNotification("GPU Mode",
                    "GPU mode switch failed — check logs", "dialog-error");
                break;
        }
    }

    /// <summary>
    /// Show the "GPU Driver Active" confirmation dialog with three choices.
    /// All button properties set directly — no CSS classes — for full control
    /// over styling. The ghelper class is designed for grid-stretched main window
    /// buttons and fights with dialog layout (HorizontalAlignment=Stretch, hover
    /// state overrides accent color).
    /// </summary>
    private void ShowDriverBlockingDialog(GpuMode target)
    {
        var dialog = new Window
        {
            Title = "GPU Driver Active",
            Width = 490,
            Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SystemDecorations = SystemDecorations.Full,
        };

        // ── Content card — matches main window panel style (#262626) ──
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#262626")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(20, 16),
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
        };

        var titleIcon = new TextBlock
        {
            Text = "\u26a0",  // ⚠
            FontSize = 20,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };

        var titleText = new TextBlock
        {
            Text = "GPU Driver Active",
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var titleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
        };
        titleRow.Children.Add(titleIcon);
        titleRow.Children.Add(titleText);

        var body = new TextBlock
        {
            Text = "The GPU is currently in use by the display system.\n" +
                   "Switching to Eco mode requires releasing the driver first.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        };

        var cardContent = new StackPanel();
        cardContent.Children.Add(titleRow);
        cardContent.Children.Add(body);
        card.Child = cardContent;

        // ── Buttons — all properties set directly, no CSS class ──
        // Shared properties applied via helper
        Button MakeDialogButton(string text, string bg, string fg, bool bold = false)
        {
            return new Button
            {
                Content = text,
                Background = new SolidColorBrush(Color.Parse(bg)),
                Foreground = new SolidColorBrush(Color.Parse(fg)),
                FontWeight = bold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                FontSize = 13,
                MinWidth = 130,
                MinHeight = 38,
                Padding = new Avalonia.Thickness(14, 8),
                CornerRadius = new Avalonia.CornerRadius(5),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
        }

        var btnSwitchNow   = MakeDialogButton("Switch Now",    "#4CC2FF", "#000000", bold: true);
        var btnAfterReboot = MakeDialogButton("After Reboot",  "#373737", "#F0F0F0");
        var btnCancel      = MakeDialogButton("Cancel",        "#2A2A2A", "#888888");

        btnSwitchNow.Margin = new Avalonia.Thickness(0, 0, 8, 0);
        btnAfterReboot.Margin = new Avalonia.Thickness(0, 0, 8, 0);

        // ── Button click handlers ──
        btnSwitchNow.Click += (_, _) =>
        {
            dialog.Close();
            LockGpuButtons("Releasing GPU driver, please wait...");

            Task.Run(() =>
            {
                var gpu = App.GpuModeCtrl;
                var result = gpu?.TryReleaseAndSwitch() ?? GpuSwitchResult.Failed;

                Dispatcher.UIThread.Post(() =>
                {
                    UnlockGpuButtons();

                    if (result == GpuSwitchResult.Deferred)
                    {
                        // Driver release failed (rmmod failed, pkexec cancelled, etc.)
                        labelTipGPU.Text = "Eco mode pending — reboot to apply";
                        RefreshGpuMode();
                        App.System?.ShowNotification("GPU Mode",
                            "GPU held by display system — Eco mode scheduled for reboot",
                            "dialog-warning");
                        return;
                    }

                    HandleGpuSwitchResult(result, target);
                });
            });
        };

        btnAfterReboot.Click += (_, _) =>
        {
            dialog.Close();
            App.GpuModeCtrl?.ScheduleModeForReboot(target);
            labelTipGPU.Text = "Eco mode pending — reboot to apply";
            RefreshGpuMode();
            App.System?.ShowNotification("GPU Mode",
                "Eco mode will activate after reboot", "system-reboot");
        };

        btnCancel.Click += (_, _) =>
        {
            dialog.Close();
            RefreshGpuMode();
        };

        // ── Button row ──
        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        buttonPanel.Children.Add(btnSwitchNow);
        buttonPanel.Children.Add(btnAfterReboot);
        buttonPanel.Children.Add(btnCancel);

        // ── Footer help text ──
        var footer = new TextBlock
        {
            Text = "Switch Now attempts to unload the GPU driver (admin password\n" +
                   "may be required). After Reboot saves for next startup.",
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
            LineHeight = 16,
            Margin = new Avalonia.Thickness(2, 14, 0, 0),
        };

        // ── Layout ──
        var outerStack = new StackPanel { Margin = new Avalonia.Thickness(24, 20, 24, 16) };
        outerStack.Children.Add(card);
        outerStack.Children.Add(buttonPanel);
        outerStack.Children.Add(footer);

        dialog.Content = outerStack;
        dialog.ShowDialog(this);
    }

    private void ButtonEco_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Eco, "Switching to Eco mode, please wait...");

    private void ButtonStandard_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Standard, "Switching to Standard mode...");

    private void ButtonOptimized_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Optimized, "Switching GPU mode...");

    private void ButtonUltimate_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Ultimate, "Switching to Ultimate mode...");

    // ── Screen ──

    private void RefreshScreen()
    {
        var display = App.Display;
        if (display == null) return;

        int hz = display.GetRefreshRate();
        if (hz > 0)
        {
            // Combined header: "Laptop Screen: 60Hz" (matches Windows layout)
            labelScreen.Text = $"Laptop Screen: {hz}Hz";
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

    public void RefreshKeyboard()
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
            Helpers.Logger.WriteLine($"AURA mode changed → {(AuraMode)modeVal}");
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
            Height = 420,
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

        var labelR = new TextBlock { Text = $"R: {initR}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };
        var labelG = new TextBlock { Text = $"G: {initG}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };
        var labelB = new TextBlock { Text = $"B: {initB}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };

        // Hex color input
        var hexLabel = new TextBlock { Text = "Hex:", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 6, 0, 0), FontSize = 11 };
        var hexInput = new TextBox
        {
            Text = $"#{initR:X2}{initG:X2}{initB:X2}",
            Width = 100,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(4, 2, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#262626")),
            Foreground = Brushes.White,
        };
        bool _suppressHexUpdate = false;

        void UpdatePreview()
        {
            byte r = (byte)sliderR.Value;
            byte g = (byte)sliderG.Value;
            byte b = (byte)sliderB.Value;
            preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            labelR.Text = $"R: {r}";
            labelG.Text = $"G: {g}";
            labelB.Text = $"B: {b}";
            if (!_suppressHexUpdate)
                hexInput.Text = $"#{r:X2}{g:X2}{b:X2}";
        }

        sliderR.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderG.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderB.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };

        // Parse hex input when user types
        hexInput.TextChanged += (_, _) =>
        {
            var text = hexInput.Text?.Trim() ?? "";
            if (!text.StartsWith("#")) text = "#" + text;
            if (text.Length == 7)
            {
                try
                {
                    var c = Color.Parse(text);
                    _suppressHexUpdate = true;
                    sliderR.Value = c.R;
                    sliderG.Value = c.G;
                    sliderB.Value = c.B;
                    _suppressHexUpdate = false;
                    preview.Background = new SolidColorBrush(c);
                    labelR.Text = $"R: {c.R}";
                    labelG.Text = $"G: {c.G}";
                    labelB.Text = $"B: {c.B}";
                }
                catch { }
            }
        };

        var btnOk = new Button
        {
            Content = "Apply",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            MinWidth = 120,
            MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#4CC2FF")),
            Foreground = Brushes.Black,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        };
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

        var hexRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4) };
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(hexInput);

        var stack = new StackPanel { Margin = new Avalonia.Thickness(16, 8) };
        stack.Children.Add(preview);
        stack.Children.Add(presetPanel);
        stack.Children.Add(hexRow);
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

    // ── Battery ──

    private void RefreshBattery()
    {
        var wmi = App.Wmi;
        if (wmi == null) return;

        // For models that only accept 60/80/100, snap slider to valid values
        if (Helpers.AppConfig.IsChargeLimit6080())
        {
            sliderBattery.TickFrequency = 20;
            sliderBattery.IsSnapToTickEnabled = true;
        }

        int limit = wmi.GetBatteryChargeLimit();
        if (limit > 0)
        {
            sliderBattery.Value = limit;
            labelBatteryLimit.Text = $"{limit}%";
            // Combined header: "Battery Charge Limit: 80%" (matches Windows)
            labelBattery.Text = $"Battery Charge Limit: {limit}%";
        }

        // Show discharge/charge rate in battery section header (right side)
        // and charge percentage in footer (like Windows "Charge: 71.5%")
        var power = App.Power;
        if (power != null)
        {
            int level = power.GetBatteryPercentage();
            bool acPlugged = power.IsOnAcPower();
            int drainMw = power.GetBatteryDrainRate();

            // Discharge/charge rate in battery header right column
            if (drainMw != 0)
            {
                double watts = Math.Abs(drainMw) / 1000.0;
                string rateStr = drainMw > 0
                    ? $"Discharging: {watts:F1}W"
                    : $"Charging: {watts:F1}W";
                labelCharge.Text = rateStr;
            }
            else if (acPlugged)
            {
                labelCharge.Text = "Plugged in";
            }

            // Charge level in footer
            if (level >= 0)
            {
                labelChargeFooter.Text = $"Charge: {level}%";
            }
        }
    }

    private void SliderBattery_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int limit = (int)e.NewValue;
        App.Wmi?.SetBatteryChargeLimit(limit);

        // Re-read actual value (may have been clamped by 6080 firmware constraint)
        int actual = App.Wmi?.GetBatteryChargeLimit() ?? limit;
        labelBatteryLimit.Text = $"{actual}%";
        labelBattery.Text = $"Battery Charge Limit: {actual}%";
        Helpers.AppConfig.Set("charge_limit", actual);

        // Snap slider to actual value if clamped
        if (actual != limit && actual > 0)
            sliderBattery.Value = actual;
    }

    private void ButtonBattery60_Click(object? sender, RoutedEventArgs e)
    {
        sliderBattery.Value = 60;
        App.Wmi?.SetBatteryChargeLimit(60);
        Helpers.AppConfig.Set("charge_limit", 60);
        RefreshBattery();
    }

    private void ButtonBattery80_Click(object? sender, RoutedEventArgs e)
    {
        sliderBattery.Value = 80;
        App.Wmi?.SetBatteryChargeLimit(80);
        Helpers.AppConfig.Set("charge_limit", 80);
        RefreshBattery();
    }

    private void ButtonBattery100_Click(object? sender, RoutedEventArgs e)
    {
        sliderBattery.Value = 100;
        App.Wmi?.SetBatteryChargeLimit(100);
        Helpers.AppConfig.Set("charge_limit", 100);
        RefreshBattery();
    }

    // ── Footer ──

    private void RefreshFooter()
    {
        var sys = App.System;
        if (sys == null) return;

        string model = sys.GetModelName() ?? "Unknown ASUS";

        // Show model in window title (like Windows G-Helper)
        Title = $"G-Helper — {model}";

        // Version + model in footer
        labelVersion.Text = $"v{Helpers.AppConfig.AppVersion} — {model}";

        // Check autostart status
        checkStartup.IsChecked = sys.IsAutostartEnabled();

        // System info (same as ExtraWindow)
        labelSysModel.Text = $"Model: {model}";
        labelSysBios.Text = $"BIOS: {sys.GetBiosVersion()}";
        labelSysKernel.Text = $"Kernel: {sys.GetKernelVersion()}";

        bool wmiLoaded = sys.IsAsusWmiLoaded();
        labelSysWmi.Text = $"asus-wmi: {(wmiLoaded ? "\u2713 Loaded" : "\u2717 Not loaded")}";

        var features = new List<string>();
        var wmi = App.Wmi;
        if (wmi != null)
        {
            if (wmi.IsFeatureSupported("throttle_thermal_policy")) features.Add("Performance Modes");
            if (wmi.IsFeatureSupported("dgpu_disable")) features.Add("GPU Eco");
            if (wmi.IsFeatureSupported("gpu_mux_mode")) features.Add("MUX Switch");
            if (wmi.IsFeatureSupported("panel_od")) features.Add("Panel Overdrive");
            if (wmi.IsFeatureSupported("mini_led_mode")) features.Add("MiniLED");
            if (wmi.IsFeatureSupported("ppt_pl1_spl")) features.Add("PPT Limits");
            if (wmi.IsFeatureSupported("nv_dynamic_boost")) features.Add("NVIDIA Dynamic Boost");
        }

        labelSysFeatures.Text = features.Count > 0
            ? $"Features: {string.Join(", ", features)}"
            : "No ASUS-specific features detected";
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
            if (Helpers.AppConfig.Is("topmost")) _extraWindow.Topmost = true;
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
            if (Helpers.AppConfig.Is("topmost")) _updatesWindow.Topmost = true;
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

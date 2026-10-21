using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Gpu;
using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.USB;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Main settings window - Linux port of G-Helper's SettingsForm.
/// Mirrors the panel layout: Performance → GPU → Screen → Keyboard → Battery → Footer
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private bool _layoutReady;
    private int _batteryRefreshCounter;
    private int _lastKbdBrightness = -1;
    private int _currentPerfMode = -1;
    private int _currentGpuMode = -1;  // 0=Eco, 1=Standard, 2=Optimized (auto), 3=Ultimate (MUX=0)

    // Easter egg: click version label 7 times → arcade game
    private int _versionClickCount;
    private DateTime _versionClickStart;
    private ArcadeWindow? _arcadeWindow;

    // Donate button state
    private int _coinClickCount;
    private bool _coinMuted;
    private DispatcherTimer? _coinDebounceTimer;
    private DispatcherTimer? _coinShakeTimer;
    private int _coinShakeFrame;
    private TranslateTransform? _coinTransform;

    // Accent colors matching G-Helper's RForm.cs
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static readonly IBrush CoinGoldBrush = new SolidColorBrush(Color.Parse("#FFD700"));
    private static readonly IBrush CoinDarkBrush = new SolidColorBrush(Color.Parse("#8B6914"));

    public MainWindow()
    {
        InitializeComponent();
        panelAudio.IsVisible = !Helpers.AppConfig.Is("disable_audio");
        Labels.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLabels());
        InitDonate();

        // Refresh timer for live sensor data
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshSensorData();
            // Every ~20s: refresh battery, prune dead peripherals, re-scan.
            if (++_batteryRefreshCounter >= 10)
            {
                _batteryRefreshCounter = 0;
                RefreshBattery();
                Task.Run(() =>
                {
                    Peripherals.PeripheralsProvider.RefreshBatteryForAllDevices();
                    // Re-scan for BT devices (HidSharp misses BT hidraw).
                    try
                    { Peripherals.PeripheralsProvider.DetectAllMice(); }
                    catch { }
                    Avalonia.Threading.Dispatcher.UIThread.Post(VisualizePeripherals);
                });
            }
            PollXgmIfChanged();
        };

        // Tray-icon model: hide on user-initiated close, dispose only when the
        // app is really shutting down. Hiding instead of disposing keeps the
        // entire control tree warm so the next tray-toggle reopen is instant
        // (~1-5 ms vs ~100-500 ms for a full reconstruction + AXAML inflate).
        // KDE logout/reboot still proceed cleanly: ShutdownRequested sets
        // App.IsShuttingDown=true before windows are walked for closing.
        Closing += (_, e) =>
        {
            if (!App.IsShuttingDown)
            {
                e.Cancel = true;
                Hide();
                _refreshTimer.Stop();
                return;
            }
            _refreshTimer.Stop();
            App.MainWindowInstance = null;
        };

        // ASUS mice: show a button per connected mouse, update on hot-plug,
        // refresh battery readings whenever the window regains focus.
        Peripherals.PeripheralsProvider.DeviceChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(VisualizePeripherals);
        Activated += (_, _) => Task.Run(() =>
        {
            Peripherals.PeripheralsProvider.RefreshBatteryForAllDevices();
            Avalonia.Threading.Dispatcher.UIThread.Post(VisualizePeripherals);
        });

        // Start sensor refresh + populate UI on every show. Opened fires on the
        // first show AND on Show() after a Hide(), unlike Loaded which fires
        // only once per Window lifetime. Pairing Opened with the Closing-Hide
        // flow above keeps sensor data fresh exactly when the user is looking.
        Opened += (_, _) =>
        {
            _layoutReady = true;
            _refreshTimer.Start();
            RefreshAll();
            HookFnLockChanged();
        };
    }

    /// <summary>
    /// Subscribe to FnLockChanged. Idempotent (un/re-subscribes safely so
    /// repeated calls during App.RestartFnLock don't double-fire).
    /// </summary>
    private void HookFnLockChanged()
    {
        if (App.FnLock == null)
            return;
        App.FnLock.FnLockChanged -= OnFnLockChangedFromMain;
        App.FnLock.FnLockChanged += OnFnLockChangedFromMain;
    }

    private void OnFnLockChangedFromMain(bool _) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshFnLockButton);

    // Refresh / Init

    private void RefreshAll()
    {
        // Also refreshes mode, GPU, screen, battery, keyboard and footer.
        ApplyLabels();
        RefreshFnLockButton();
        RefreshAudioToggle();
        RefreshAnimeMatrix();
        RefreshSensorData();
        RefreshAllyPanel();

        VisualizePeripherals();

        // OSK header button: touch-first devices in desktop sessions only.
        // Game mode has Steam's overlay keyboard; desktops use the tray item.
        buttonOsk.IsVisible =
            (Helpers.AppConfig.IsHandheldDevice() || Platform.Linux.ImmutableOs.IsSteamOs)
            && !Platform.Linux.SteamShortcuts.IsSteamDeckGameMode;

        // Arcade launcher: handhelds + SteamOS (gamepad-playable).
        buttonArcade.IsVisible =
            Helpers.AppConfig.IsHandheldDevice() || Platform.Linux.ImmutableOs.IsSteamOs;

        // Dev-only launchers for UI checks on machines without the
        // matching hardware: every app window, and force-show toggles
        // for hardware-gated panels.
        bool devMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHELPER_DEV"));
        buttonDevWindows.IsVisible = devMode;
        buttonDevPanels.IsVisible = devMode;
    }

    // Peripherals (ASUS + Logitech mice)

    /// <summary>One button per connected mouse (up to 3). Text only:
    /// display name plus battery state. The whole panel is hidden when
    /// no mouse is connected so the main window stays compact.</summary>
    public void VisualizePeripherals()
    {
        var mice = Peripherals.PeripheralsProvider.ConnectedMice;
        var buttons = new[] { buttonPeripheral1, buttonPeripheral2, buttonPeripheral3 };
        var labels = new[] { labelPeripheral1, labelPeripheral2, labelPeripheral3 };

        bool shouldShow = mice.Count > 0;
        bool changed = panelPeripherals.IsVisible != shouldShow;
        panelPeripherals.IsVisible = shouldShow;

        if (shouldShow)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i >= mice.Count)
                {
                    buttons[i].IsVisible = false;
                    continue;
                }

                var mouse = mice[i];
                string text = mouse.GetDisplayName();
                if (mouse.IsDeviceReady && mouse.HasBattery() && mouse.Battery >= 0)
                    text += mouse.Charging ? $"  {mouse.Battery}% +" : $"  {mouse.Battery}%";

                labels[i].Text = text;
                buttons[i].IsVisible = true;
            }
        }

        if (changed && _layoutReady)
        {
            Height = double.NaN;
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.Height;
            Dispatcher.UIThread.Post(
                () => WindowPositioner.BottomRight(this),
                DispatcherPriority.Loaded);
        }
    }

    private MouseWindow? _mouseWindow;

    private void ButtonPeripheral_Click(object? sender, RoutedEventArgs e)
    {
        int index = 0;
        if (sender == buttonPeripheral2)
            index = 1;
        if (sender == buttonPeripheral3)
            index = 2;

        var mice = Peripherals.PeripheralsProvider.ConnectedMice;
        var mouse = index < mice.Count ? mice[index] : null;

        if (_mouseWindow != null && _mouseWindow.IsVisible)
        {
            _mouseWindow.Close();
            _mouseWindow = null;
            return;
        }

        _mouseWindow = new MouseWindow(mouse);
        if (Helpers.AppConfig.Is("topmost"))
            _mouseWindow.Topmost = true;
        WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_mouseWindow);
        _mouseWindow.Show();
    }

    private DevWindowsWindow? _devWindowsWindow;
    private DevPanelsWindow? _devPanelsWindow;

    private void ButtonDevWindows_Click(object? sender, RoutedEventArgs e)
    {
        if (_devWindowsWindow == null || !_devWindowsWindow.IsVisible)
        {
            _devWindowsWindow = new DevWindowsWindow();
            _devWindowsWindow.Show(this);
        }
        else
        {
            _devWindowsWindow.Activate();
        }
    }

    private void ButtonDevPanels_Click(object? sender, RoutedEventArgs e)
    {
        if (_devPanelsWindow == null || !_devPanelsWindow.IsVisible)
        {
            _devPanelsWindow = new DevPanelsWindow();
            _devPanelsWindow.Show(this);
        }
        else
        {
            _devPanelsWindow.Activate();
        }
    }

    private void ApplyLabels()
    {
        // Button text (find the TextBlock children inside each button's StackPanel)
        SetButtonText(buttonSilent, Labels.Get("mode_silent"));
        SetButtonText(buttonBalanced, Labels.Get("mode_balanced"));
        SetButtonText(buttonTurbo, Labels.Get("mode_turbo"));

        ToolTip.SetTip(buttonOsk, Labels.Get("osk_tray_label"));

        // Lenovo: the power button LED mirrors the firmware thermal mode -
        // surface the color mapping as tooltips for discoverability.
        if (Helpers.AppConfig.IsLenovoDevice())
        {
            ToolTip.SetTip(buttonSilent, "Quiet - power button LED: blue");
            ToolTip.SetTip(buttonBalanced, "Balanced - power button LED: white");
            ToolTip.SetTip(buttonTurbo,
                Helpers.AppConfig.GetString("platform_profile_1") == "max-power"
                    ? "Extreme (max-power) - power button LED: purple"
                    : "Performance - power button LED: red");
        }
        SetButtonText(buttonFans, Labels.Get("fans_power"));
        SetButtonText(buttonEco, Labels.Get("gpu_eco"));
        SetButtonText(buttonStandard, Labels.Get("gpu_standard"));
        SetButtonText(buttonUltimate, Labels.Get("gpu_ultimate"));
        SetButtonText(buttonOptimized, Labels.Get("gpu_optimized"));
        SetButtonText(buttonKeyboard, Labels.Get("backlight"));
        SetButtonText(buttonExtra, Labels.Get("extra"));
        SetButtonText(buttonUpdates, Labels.Get("updates"));
        SetButtonText(buttonQuit, Labels.Get("quit"));
        labelDonateText.Text = Labels.Get("donate");

        buttonColor1.SetValue(Avalonia.Controls.ToolTip.TipProperty, Labels.Get("color_primary"));
        buttonColor2.SetValue(Avalonia.Controls.ToolTip.TipProperty, Labels.Get("color_secondary"));

        labelKeyboard.Text = Labels.Get("keyboard_header");
        labelPeripherals.Text = Labels.Get("peripherals_header");
        checkStartup.Content = Labels.Get("run_on_startup");

        // Audio panel localisation.
        labelAudio.Text = Labels.Get("audio_header_microphone");
        Avalonia.Controls.ToolTip.SetTip(buttonAudio,
            Labels.Get("audio_configure_chain"));
        Avalonia.Controls.ToolTip.SetTip(buttonAudioToggle,
            Labels.Get("audio_toggle_tooltip"));

        // ROG Ally panel - static labels. Dynamic ones (mode, backlight %)
        // are refreshed by RefreshAllyPanel.
        labelAllyHeader.Text = Labels.Get("ally_handheld_section");
        labelOpenHandheld.Text = Labels.Get("ally_open_handheld");

        // Refresh dynamic labels
        RefreshPerformanceMode();
        RefreshGpuMode();
        RefreshScreen();
        RefreshBattery();
        RefreshKeyboard();
        RefreshFooter();
        RefreshAuraCombos();
    }

    /// <summary>Helper: set text of the last TextBlock inside a Button's StackPanel content.</summary>
    private static void SetButtonText(Button button, string text)
    {
        // Buttons use StackPanel > TextBlock (icon) + TextBlock (text)
        // We want the LAST TextBlock child
        if (button.Content is StackPanel sp)
        {
            var textBlock = sp.Children.OfType<Avalonia.Controls.TextBlock>().LastOrDefault();
            if (textBlock != null)
                textBlock.Text = text;
        }
        else if (button.Content is Avalonia.Controls.TextBlock tb)
        {
            tb.Text = text;
        }
    }

    private void RefreshSensorData()
    {
        try
        {
            var wmi = App.Wmi;
            if (wmi == null)
                return;

            int cpuTemp = wmi.DeviceGet(0x00120094); // Temp_CPU
            int gpuTemp = wmi.DeviceGet(0x00120097); // Temp_GPU
            int cpuFan = wmi.GetFanRpm(0);
            int gpuFan = wmi.GetFanRpm(1);

            string cpuTempStr = cpuTemp > 0 ? Helpers.TempHelper.FormatTemp(cpuTemp) : "--";
            string cpuFanStr = Fan.FanSensorControl.FormatFan(0, cpuFan);

            // GPU fan: might be RPM or percentage from nvidia-smi
            string gpuFanStr;
            if (gpuFan <= -2)
            {
                // Encoded percentage: -2 - percent (nvidia-smi returns this)
                int percent = -(gpuFan + 2);
                gpuFanStr = $"{percent}%";
            }
            else
                gpuFanStr = Fan.FanSensorControl.FormatFan(1, gpuFan);

            // Right-aligned compact format: "CPU: 32°C Fan: 0RPM"
            labelCPUFan.Text = Labels.Format("cpu_fan_info", cpuTempStr, cpuFanStr);

            // GPU fan info - compact for right-aligned display
            string gpuTempStr = gpuTemp > 0 ? Helpers.TempHelper.FormatTemp(gpuTemp) : "";

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
                ? Labels.Format("gpu_fan_full_info", gpuTempStr, gpuLoadStr, gpuFanStr)
                : Labels.Format("gpu_fan_only", gpuFanStr);

            // Mid fan if available
            int midFan = wmi.GetFanRpm(2);
            if (midFan > 0)
                labelMidFan.Text = Labels.Format("mid_fan_info", Fan.FanSensorControl.FormatFan(2, midFan));

            // Keyboard brightness, detect external changes (physical keys, kernel driver)
            int kbdBrightness = wmi.GetKeyboardBrightness();
            if (kbdBrightness >= 0 && kbdBrightness != _lastKbdBrightness)
            {
                _lastKbdBrightness = kbdBrightness;
                RefreshKeyboard();
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("RefreshSensorData error", ex);
        }
    }

    // Performance Mode

    public void RefreshPerformanceMode()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        _currentPerfMode = wmi.GetThrottleThermalPolicy();

        // Custom mode running on its base: show it on the Fans + Power button
        int mode = Mode.Modes.GetCurrent();
        if (mode > 2 && Mode.Modes.GetBase(mode) == _currentPerfMode)
            _currentPerfMode = mode;

        string modeName = _currentPerfMode switch
        {
            0 => Labels.Get("mode_balanced"),
            1 => Labels.Get("mode_turbo"),
            2 => Labels.Get("mode_silent"),
            > 2 => Mode.Modes.GetName(_currentPerfMode),
            _ => Labels.Get("mode_unknown")
        };

        // Combined header: "Mode: Balanced".
        labelPerf.Text = Labels.Format("mode_prefix", modeName);
        labelPerfMode.Text = modeName;
        UpdatePerfButtons();
    }

    private void UpdatePerfButtons()
    {
        SetButtonActive(buttonSilent, _currentPerfMode == 2);
        SetButtonActive(buttonBalanced, _currentPerfMode == 0);
        SetButtonActive(buttonTurbo, _currentPerfMode == 1);
        SetButtonActive(buttonFans, _currentPerfMode > 2);
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
            if (Helpers.AppConfig.Is("topmost"))
                _fansWindow.Topmost = true;
            WindowPositioner.LeftOf(_fansWindow, this);
            _fansWindow.Show();
        }
        else
        {
            _fansWindow.Activate();
        }
    }

    // GPU Mode

    /// <summary>Public wrappers for refresh methods - called from App.cs on power state changes.</summary>
    public void RefreshGpuModePublic() => RefreshGpuMode();
    public void RefreshBatteryPublic() => RefreshBattery();
    public void RefreshScreenPublic() => RefreshScreen();

    /// <summary>Forward keyboard brightness refresh to ExtraWindow if open.</summary>
    public void RefreshExtraKeyboardBrightness()
        => _extraWindow?.RefreshKeyboardBrightness();

    private void RefreshGpuMode()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // No GPU Eco support → hide entire GPU panel
        if (!wmi.IsGpuEcoAvailable() && !Helpers.AppConfig.Is("show_gpu_dev"))
        {
            panelGPU.IsVisible = false;
            return;
        }
        panelGPU.IsVisible = true;

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
            0 => Labels.Get("gpu_eco"),
            1 => Labels.Get("gpu_standard"),
            2 => Labels.Get("gpu_optimized"),
            3 => Labels.Get("gpu_ultimate"),
            _ => Labels.Get("gpu_unknown")
        };

        labelGPU.Text = Labels.Format("gpu_mode_prefix", modeName);
        labelGPUMode.Text = modeName;

        // Lenovo: append the dGPU's runtime PM status (active/suspended) -
        // there is no firmware Eco state to show, but runtime_status tells
        // the user whether the dGPU is actually powered right now.
        if (Helpers.AppConfig.IsLenovoDevice())
        {
            string? runtime = ReadDgpuRuntimeStatus();
            if (runtime != null)
                labelGPU.Text += $" (dGPU {runtime})";
        }

        UpdateGpuButtons();

        // GPU tip - check for pending reboot first
        if (gpu?.IsPendingReboot() == true)
        {
            string? pending = Helpers.AppConfig.GetString("gpu_mode");
            labelTipGPU.Text = Labels.Format("gpu_pending_reboot", pending?.ToUpperInvariant() ?? Labels.Get("gpu_pending_mode"));
        }
        else
        {
            labelTipGPU.Text = _currentGpuMode switch
            {
                0 => Labels.Get("gpu_tip_eco"),
                1 => Labels.Get("gpu_tip_standard"),
                2 => Labels.Get("gpu_tip_optimized"),
                3 => Labels.Get("gpu_tip_ultimate"),
                _ => ""
            };
        }

        bool pciBackend = Helpers.AppConfig.IsPciGpuBackend();
        bool gpuDev = Helpers.AppConfig.Is("show_gpu_dev");
        buttonUltimate.IsVisible = (!pciBackend && wmi.IsFeatureSupported(AsusAttributes.GpuMuxMode)) || gpuDev;
        buttonOptimized.IsVisible = (!pciBackend && Helpers.AppConfig.IsOptimizedGpuModeEnabled()) || gpuDev;

        int visibleGpuButtons = 2
            + (buttonUltimate.IsVisible ? 1 : 0)
            + (buttonOptimized.IsVisible ? 1 : 0);
        panelGpuModes.Columns = visibleGpuButtons;
    }

    /// <summary>runtime PM status ("active"/"suspended") of the first non-boot
    /// discrete GPU on the PCI bus, or null when no dGPU is present.</summary>
    private static string? ReadDgpuRuntimeStatus()
        => Gpu.GPUModeControl.DgpuRuntimeStatus();

    private void UpdateGpuButtons()
    {
        SetButtonActive(buttonEco, _currentGpuMode == 0);
        SetButtonActive(buttonStandard, _currentGpuMode == 1);
        SetButtonActive(buttonOptimized, _currentGpuMode == 2);
        SetButtonActive(buttonUltimate, _currentGpuMode == 3);
    }

    private NvidiaProcessesWindow? _nvidiaProcessesWindow;

    /// <summary>
    /// Lock GPU mode buttons during a switch operation (like upstream's LockGPUModes).
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
        // Only re-enable Optimized if the config flag enables it; otherwise it
        // stays disabled to match its hidden state.
        buttonOptimized.IsEnabled = Helpers.AppConfig.IsOptimizedGpuModeEnabled();
        buttonUltimate.IsEnabled = true;
    }

    /// <summary>
    /// Common handler for all 4 GPU mode buttons.
    /// Locks buttons, calls GPUModeControl on background thread,
    /// handles the result on UI thread.
    /// </summary>
    private void RequestGpuModeSwitch(GpuMode target, string switchingText)
    {
        var gpu = App.GpuModeCtrl;
        if (gpu == null)
            return;

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
    /// Handle GpuSwitchResult on the UI thread - show notifications, update tips, show dialogs.
    /// </summary>
    private void HandleGpuSwitchResult(GpuSwitchResult result, GpuMode target)
    {
        RefreshGpuMode();

        switch (result)
        {
            case GpuSwitchResult.Applied:
                if (target != GpuMode.Eco)
                    Task.Run(() =>
                    {
                        App.RefreshGpuControlIfMissing();
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_fansWindow != null && _fansWindow.IsVisible)
                                _fansWindow.RefreshGpuPublic();
                        });
                    });
                string appliedText = target switch
                {
                    GpuMode.Eco => Labels.Get("gpu_notify_eco"),
                    GpuMode.Standard => Labels.Get("gpu_notify_standard"),
                    GpuMode.Optimized => Labels.Get("gpu_notify_optimized"),
                    GpuMode.Ultimate => Labels.Get("gpu_notify_ultimate"),
                    _ => Labels.Get("gpu_notify_changed")
                };
                App.System?.ShowNotification(Labels.Get("gpu_mode"), appliedText, "video-display");
                break;

            case GpuSwitchResult.AlreadySet:
                // No notification needed
                break;

            case GpuSwitchResult.RebootRequired:
                string rebootText = target switch
                {
                    GpuMode.Ultimate => Labels.Get("gpu_reboot_ultimate"),
                    GpuMode.Standard => Labels.Get("gpu_reboot_standard"),
                    GpuMode.Optimized => Labels.Get("gpu_reboot_optimized"),
                    GpuMode.Eco => Labels.Get("gpu_reboot_eco"),
                    _ => Labels.Get("gpu_reboot_generic")
                };
                labelTipGPU.Text = target == GpuMode.Optimized
                    ? Labels.Get("gpu_mux_reboot_auto")
                    : Labels.Get("gpu_mux_reboot");
                App.System?.ShowNotification(Labels.Get("gpu_mode"), rebootText, "system-reboot");
                break;

            case GpuSwitchResult.EcoBlocked:
                labelTipGPU.Text = Labels.Get("gpu_eco_blocked_mux");
                App.System?.ShowNotification(Labels.Get("gpu_mode"),
                    Labels.Get("gpu_eco_blocked_mux"),
                    "dialog-warning");
                break;

            case GpuSwitchResult.DriverBlocking:
                ShowDriverBlockingDialog(target);
                break;

            case GpuSwitchResult.Deferred:
                labelTipGPU.Text = Labels.Get("gpu_eco_pending");
                App.System?.ShowNotification(Labels.Get("gpu_mode"),
                    Labels.Get("gpu_eco_after_reboot"), "system-reboot");
                break;

            case GpuSwitchResult.Failed:
                App.System?.ShowNotification(Labels.Get("gpu_mode"),
                    Labels.Get("gpu_switch_failed"), "dialog-error");
                break;

            case GpuSwitchResult.DgpuReenableFailed:
                labelTipGPU.Text = Labels.Get("gpu_reenable_failed");
                ShowDgpuReenableFailedDialog();
                break;
        }
    }

    /// <summary>
    /// Shown when dgpu_disable=0 was written but the dGPU never re-appeared on
    /// the PCI bus after repeated rescans (slow/stuck ASUS firmware). The only
    /// reliable recovery is a reboot.
    /// </summary>
    private void ShowDgpuReenableFailedDialog()
    {
        var dialog = new Window
        {
            Title = Labels.Get("gpu_reenable_failed_title"),
            Width = 460,
            MaxHeight = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#262626")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(20, 16),
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
        };

        var titleIcon = new GHelper.Linux.UI.Controls.Icon
        {
            IconName = "warning",
            Width = 22,
            Height = 22,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };
        var titleText = new TextBlock
        {
            Text = Labels.Get("gpu_reenable_failed_title"),
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var titleRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 0, 0, 12) };
        titleRow.Children.Add(titleIcon);
        titleRow.Children.Add(titleText);

        var body = new TextBlock
        {
            Text = Labels.Get("gpu_reenable_failed_body"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        };

        var cardContent = new StackPanel();
        cardContent.Children.Add(titleRow);
        cardContent.Children.Add(body);
        card.Child = cardContent;

        var btnClose = new Button
        {
            Content = Labels.Get("cancel"),
            Background = new SolidColorBrush(Color.Parse("#4CC2FF")),
            Foreground = new SolidColorBrush(Color.Parse("#000000")),
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 13,
            MinWidth = 130,
            MinHeight = 38,
            Padding = new Avalonia.Thickness(14, 8),
            CornerRadius = new Avalonia.CornerRadius(5),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        btnClose.Click += (_, _) => dialog.Close();

        var outerStack = new StackPanel { Margin = new Avalonia.Thickness(24, 20, 24, 16) };
        outerStack.Children.Add(card);
        outerStack.Children.Add(btnClose);

        dialog.Content = outerStack;
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Show the "GPU Driver Active" confirmation dialog with three choices.
    /// All button properties set directly - no CSS classes - for full control
    /// over styling. The ghelper class is designed for grid-stretched main window
    /// buttons and fights with dialog layout (HorizontalAlignment=Stretch, hover
    /// state overrides accent color).
    /// </summary>
    private void ShowDriverBlockingDialog(GpuMode target)
    {
        bool dgpuSoleDisplay = (App.Wmi?.GetGpuMuxMode() ?? -1) == 0;
        // X11: Xorg holds nvidia_drm for PRIME offload, rmmod always fails
        bool x11Session = !Display.DisplayBackendFactory.IsWaylandSession();
        bool canSwitchNow = !dgpuSoleDisplay && !x11Session;

        var dialog = new Window
        {
            Title = Labels.Get("gpu_driver_title"),
            Width = 540,
            MaxHeight = 600,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
        };

        // Content card - matches main window panel style (#262626)
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#262626")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(20, 16),
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
        };

        var titleIcon = new GHelper.Linux.UI.Controls.Icon
        {
            IconName = "warning",
            Width = 22,
            Height = 22,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };

        var titleText = new TextBlock
        {
            Text = Labels.Get("gpu_driver_title"),
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
            Text = Labels.Get("gpu_driver_body"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        };

        string ComposeHolderSummary()
        {
            int fdN = Gpu.NVidia.NvidiaProcessScanner.CountFdHolders();
            int totalN = Gpu.NVidia.NvidiaProcessScanner.CountHolders();
            int libN = totalN - fdN;
            string text = Labels.Format("gpu_dgpu_users_count", fdN);
            if (libN > 0)
                text += "\n" + Labels.Format("gpu_dgpu_libs_loaded_count", libN);
            return text;
        }

        var dgpuProcessRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        var dgpuCountLabel = new TextBlock
        {
            Text = ComposeHolderSummary(),
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#E0A040")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        Grid.SetColumn(dgpuCountLabel, 0);

        // Periodic holder-count refresh: when user kills processes via the
        // NvidiaProcessesWindow, this dialog's count would otherwise stay
        // stale. Tick every 1s, stop when dialog closes.
        var holderRefreshTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        holderRefreshTimer.Tick += (_, _) =>
        {
            dgpuCountLabel.Text = ComposeHolderSummary();
        };
        var dgpuViewButton = new Button
        {
            Content = Labels.Get("gpu_dgpu_users_view"),
            Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Padding = new Avalonia.Thickness(12, 6),
            CornerRadius = new Avalonia.CornerRadius(4),
            BorderThickness = new Avalonia.Thickness(0),
            FontSize = 11,
            MinWidth = 70,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        dgpuViewButton.Click += (_, _) =>
        {
            if (_nvidiaProcessesWindow == null || !_nvidiaProcessesWindow.IsVisible)
            {
                _nvidiaProcessesWindow = new NvidiaProcessesWindow();
                if (Helpers.AppConfig.Is("topmost"))
                    _nvidiaProcessesWindow.Topmost = true;
                WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_nvidiaProcessesWindow);
                _nvidiaProcessesWindow.Show();
            }
            else
            {
                _nvidiaProcessesWindow.Activate();
            }
        };
        Grid.SetColumn(dgpuViewButton, 1);
        dgpuProcessRow.Children.Add(dgpuCountLabel);
        dgpuProcessRow.Children.Add(dgpuViewButton);

        var cardContent = new StackPanel();
        cardContent.Children.Add(titleRow);
        cardContent.Children.Add(body);
        cardContent.Children.Add(dgpuProcessRow);
        card.Child = cardContent;

        // Buttons - all properties set directly, no CSS class
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

        var btnAfterReboot = MakeDialogButton(Labels.Get("gpu_driver_after_reboot"), "#4CC2FF", "#000000", bold: true);
        var btnCancel = MakeDialogButton(Labels.Get("cancel"), "#2A2A2A", "#888888");
        btnAfterReboot.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        btnCancel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

        Button? btnSwitchNow = null;
        TextBlock? switchNowWarning = null;
        if (canSwitchNow)
        {
            btnSwitchNow = MakeDialogButton(Labels.Get("gpu_driver_switch_now"), "#A82A2A", "#FFFFFF", bold: true);
            btnSwitchNow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            switchNowWarning = new TextBlock
            {
                Text = Labels.Get("gpu_driver_switch_now_warning"),
                Foreground = new SolidColorBrush(Color.Parse("#E0A040")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 11,
                LineHeight = 16,
                Margin = new Avalonia.Thickness(2, 12, 2, 0),
            };
        }

        // Button click handlers
        if (btnSwitchNow != null)
            btnSwitchNow.Click += (_, _) =>
            {
                dialog.Close();
                RunSwitchNowKillFlow(target);
            };

        btnAfterReboot.Click += (_, _) =>
        {
            dialog.Close();
            App.GpuModeCtrl?.ScheduleModeForReboot(target);
            labelTipGPU.Text = Labels.Get("gpu_eco_pending");
            RefreshGpuMode();
            App.System?.ShowNotification(Labels.Get("gpu_mode"),
                Labels.Get("gpu_eco_after_reboot"), "system-reboot");
        };

        btnCancel.Click += (_, _) =>
        {
            dialog.Close();
            RefreshGpuMode();
        };

        // Button row: equal columns spanning full width, with equal vertical padding.
        // 3 columns (Switch Now + After Reboot + Cancel) normally; 2 columns
        // (After Reboot + Cancel) in Ultimate where Switch Now is omitted.
        var buttonPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(btnSwitchNow != null ? "*,*,*" : "*,*"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 16, 0, 16),
        };
        if (btnSwitchNow != null)
        {
            btnSwitchNow.Margin = new Avalonia.Thickness(0, 0, 6, 0);
            btnAfterReboot.Margin = new Avalonia.Thickness(6, 0, 6, 0);
            btnCancel.Margin = new Avalonia.Thickness(6, 0, 0, 0);
            Grid.SetColumn(btnSwitchNow, 0);
            Grid.SetColumn(btnAfterReboot, 1);
            Grid.SetColumn(btnCancel, 2);
            buttonPanel.Children.Add(btnSwitchNow);
            buttonPanel.Children.Add(btnAfterReboot);
            buttonPanel.Children.Add(btnCancel);
        }
        else
        {
            btnAfterReboot.Margin = new Avalonia.Thickness(0, 0, 6, 0);
            btnCancel.Margin = new Avalonia.Thickness(6, 0, 0, 0);
            Grid.SetColumn(btnAfterReboot, 0);
            Grid.SetColumn(btnCancel, 1);
            buttonPanel.Children.Add(btnAfterReboot);
            buttonPanel.Children.Add(btnCancel);
        }

        // Footer help text (no top margin - buttonPanel provides equal vertical padding)
        var footer = new TextBlock
        {
            Text = Labels.Get("gpu_driver_footer"),
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
            LineHeight = 16,
            Margin = new Avalonia.Thickness(2, 0, 0, 0),
        };

        // Layout
        var outerStack = new StackPanel { Margin = new Avalonia.Thickness(24, 20, 24, 16) };
        outerStack.Children.Add(card);
        if (switchNowWarning != null)
            outerStack.Children.Add(switchNowWarning);
        outerStack.Children.Add(buttonPanel);
        outerStack.Children.Add(footer);

        dialog.Content = outerStack;
        dialog.Opened += (_, _) => holderRefreshTimer.Start();
        dialog.Closed += (_, _) => holderRefreshTimer.Stop();
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Switch Now flow: fresh-scan holders, send SIGTERM, escalate to SIGKILL,
    /// then either proceed with <see cref="GPUModeControl.TryReleaseAndSwitch"/>
    /// or - if any holder refused to die - abort and show the failure dialog.
    /// Called by the Switch Now button and by Retry in the failure dialog.
    /// </summary>
    private void RunSwitchNowKillFlow(GpuMode target)
    {
        // MUX=0 (Ultimate): dGPU is the sole display - killing holders + releasing
        // the driver live would black-screen. Refuse the live switch and defer to
        // reboot (mirrors the "After Reboot" button). Also covers the kill-failure
        // dialog's Retry path, which re-enters here.
        if ((App.Wmi?.GetGpuMuxMode() ?? -1) == 0)
        {
            Logger.WriteLine("RunSwitchNowKillFlow: MUX=0 (Ultimate) - refusing live switch, reboot required");
            App.GpuModeCtrl?.ScheduleModeForReboot(target);
            labelTipGPU.Text = Labels.Get("gpu_eco_pending");
            RefreshGpuMode();
            App.System?.ShowNotification(Labels.Get("gpu_mode"),
                Labels.Get("gpu_eco_after_reboot"), "system-reboot");
            return;
        }

        LockGpuButtons(Labels.Get("gpu_switching_eco"));
        Task.Run(() =>
        {
            // Fresh scan at click time (the privileged scan caches up to 5s -
            // drop it so holders started after the dialog appeared are included).
            Gpu.NVidia.NvidiaProcessScanner.InvalidateScanCache();
            var snapshot = Gpu.NVidia.NvidiaProcessScanner.ScanHolders();
            Logger.WriteLine($"RunSwitchNowKillFlow: snapshot={snapshot.Count}");
            Gpu.NVidia.NvidiaProcessScanner.KillHoldersGracefulThenForce(snapshot, out var survivors);
            Logger.WriteLine($"RunSwitchNowKillFlow: snapshot-accounting survivors={survivors.Count}");

            System.Threading.Thread.Sleep(300);
            var stillAlive = Gpu.NVidia.NvidiaProcessScanner.ScanHolders();
            Logger.WriteLine($"RunSwitchNowKillFlow: verify-scan stillAlive={stillAlive.Count}");

            if (stillAlive.Count > 0)
            {
                var stillAliveList = new List<NvidiaHolder>(stillAlive);
                Dispatcher.UIThread.Post(() =>
                {
                    UnlockGpuButtons();
                    RefreshGpuMode();
                    ShowKillFailureDialog(stillAliveList, target);
                });
                return;
            }

            var result = App.GpuModeCtrl?.TryReleaseAndSwitch() ?? GpuSwitchResult.Failed;
            Dispatcher.UIThread.Post(() =>
            {
                UnlockGpuButtons();
                HandleGpuSwitchResult(result, target);
            });
        });
    }

    /// <summary>
    /// Modal shown when <see cref="RunSwitchNowKillFlow"/> could not stop one
    /// or more processes from the snapshot. Lists each survivor as comm (pid)
    /// and offers Retry (re-runs the whole flow) or Close.
    /// </summary>
    private void ShowKillFailureDialog(List<NvidiaHolder> survivors, GpuMode target)
    {
        var dialog = new Window
        {
            Title = Labels.Get("gpu_kill_failure_title"),
            Width = 460,
            MaxHeight = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#262626")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(20, 16),
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
        };

        var titleIcon = new GHelper.Linux.UI.Controls.Icon
        {
            IconName = "warning",
            Width = 22,
            Height = 22,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };

        var titleText = new TextBlock
        {
            Text = Labels.Get("gpu_kill_failure_title"),
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
            Text = Labels.Get("gpu_kill_failure_body"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
        };

        // Survivor list inside a max-height ScrollViewer (handles long lists).
        var listStack = new StackPanel { Spacing = 4 };
        foreach (var h in survivors)
        {
            listStack.Children.Add(new TextBlock
            {
                Text = $"  • {h.Comm} ({h.Pid})",
                FontSize = 12,
                FontFamily = new Avalonia.Media.FontFamily("monospace"),
                Foreground = new SolidColorBrush(Color.Parse("#E0A040")),
            });
        }
        var listScroll = new ScrollViewer
        {
            MaxHeight = 180,
            Content = listStack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var cardContent = new StackPanel();
        cardContent.Children.Add(titleRow);
        cardContent.Children.Add(body);
        cardContent.Children.Add(listScroll);
        card.Child = cardContent;

        Button MakeBtn(string text, string bg, string fg, bool bold = false)
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
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
        }

        var btnRetry = MakeBtn(Labels.Get("gpu_kill_failure_retry"), "#A82A2A", "#FFFFFF", bold: true);
        var btnClose = MakeBtn(Labels.Get("cancel"), "#2A2A2A", "#888888");
        btnRetry.Margin = new Avalonia.Thickness(0, 0, 6, 0);
        btnClose.Margin = new Avalonia.Thickness(6, 0, 0, 0);

        btnRetry.Click += (_, _) =>
        {
            dialog.Close();
            RunSwitchNowKillFlow(target);
        };

        btnClose.Click += (_, _) =>
        {
            dialog.Close();
            RefreshGpuMode();
        };

        var buttonPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        };
        Grid.SetColumn(btnRetry, 0);
        Grid.SetColumn(btnClose, 1);
        buttonPanel.Children.Add(btnRetry);
        buttonPanel.Children.Add(btnClose);

        var outerStack = new StackPanel { Margin = new Avalonia.Thickness(24, 20, 24, 16) };
        outerStack.Children.Add(card);
        outerStack.Children.Add(buttonPanel);

        dialog.Content = outerStack;
        dialog.ShowDialog(this);
    }

    private void ButtonEco_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Eco, Labels.Get("gpu_switching_eco"));

    private void ButtonStandard_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Standard, Labels.Get("gpu_switching_standard"));

    private void ButtonOptimized_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Optimized, Labels.Get("gpu_switching_generic"));

    private void ButtonUltimate_Click(object? sender, RoutedEventArgs e)
        => RequestGpuModeSwitch(GpuMode.Ultimate, Labels.Get("gpu_switching_ultimate"));

    // Screen

    private void RefreshScreen()
    {
        var display = App.Display;
        if (display == null)
            return;

        int hz = display.GetRefreshRate();
        var rates = display.GetAvailableRefreshRates();
        int maxHz = rates.Count > 0 ? rates[0] : 120;
        bool isAuto = Helpers.AppConfig.Is("screen_auto");

        if (hz > 0)
        {
            labelScreen.Text = Labels.Format(isAuto ? "screen_prefix_auto" : "screen_prefix", hz);
            labelScreenHz.Text = $"{hz} Hz";
        }

        // Update max refresh button label
        labelHighRefresh.Text = $"{maxHz}Hz";

        // Highlight active button
        SetButtonActive(buttonScreenAuto, isAuto);
        SetButtonActive(button60Hz, !isAuto && hz == 60);
        SetButtonActive(button120Hz, !isAuto && hz > 60);

        // Check for MiniLED support
        bool hasMiniLed = App.Wmi?.IsFeatureSupported(AsusAttributes.MiniLedMode) ?? false;
        buttonMiniled.IsVisible = hasMiniLed || Helpers.AppConfig.Is("show_miniled_dev");
    }

    private void ButtonScreenAuto_Click(object? sender, RoutedEventArgs e)
    {
        bool wasAuto = Helpers.AppConfig.Is("screen_auto");
        if (wasAuto)
        {
            // Toggle off, keep current rate, just disable auto
            Helpers.AppConfig.Set("screen_auto", 0);
            Helpers.Logger.WriteLine("Screen auto: disabled");
        }
        else
        {
            // Enable auto and apply immediately
            Helpers.AppConfig.Set("screen_auto", 1);
            (App.Current as App)?.AutoScreen();
        }
        RefreshScreen();
    }

    private void Button60Hz_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.AppConfig.Set("screen_auto", 0);
        App.Display?.SetRefreshRate(60);
        RefreshScreen();
    }

    private void Button120Hz_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.AppConfig.Set("screen_auto", 0);
        var rates = App.Display?.GetAvailableRefreshRates();
        if (rates != null && rates.Count > 0)
            App.Display?.SetRefreshRate(rates[0]); // Use max available
        else
            App.Display?.SetRefreshRate(120);
        RefreshScreen();
    }

    private void ButtonMiniled_Click(object? sender, RoutedEventArgs e)
    {
        int next = Display.MiniLed.CycleMode();
        if (next >= 0)
            Helpers.Logger.WriteLine($"MiniLED mode → {next}");
    }

    // Keyboard / AURA

    private bool _auraInitialized;
    private static bool _auraHardwareInitialized;
    private bool _suppressAuraEvents;
    private bool _suppressEvents;

    public void RefreshKeyboard()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        int brightness = wmi.GetKeyboardBrightness();
        if (brightness >= 0)
        {
            string level = brightness switch
            {
                0 => Labels.Get("backlight_off"),
                1 => Labels.Get("backlight_low"),
                2 => Labels.Get("backlight_medium"),
                3 => Labels.Get("backlight_high"),
                _ => Labels.Format("backlight_level", brightness)
            };
            labelBacklight.Text = Labels.Format("backlight_prefix", level);
        }

        InitAura();
        RefreshAuraCombos();
        UpdateColorButtons();
    }

    /// <summary>
    /// Initialize AURA hardware - HID handshake, load config, apply RGB.
    /// On Lenovo the ITE 4-zone / Spectrum backend takes this role instead.
    /// </summary>
    public static bool InitAuraHardware()
    {
        if (_auraHardwareInitialized)
            return Helpers.AppConfig.IsLenovoDevice() ? USB.LenovoRgb.IsAvailable() : Aura.IsAvailable();
        _auraHardwareInitialized = true;

        if (Helpers.AppConfig.IsLenovoDevice())
        {
            if (!USB.LenovoRgb.IsAvailable())
            {
                Helpers.Logger.WriteLine("No Lenovo RGB HID device found - RGB controls hidden");
                return false;
            }
            Helpers.Logger.WriteLine($"Lenovo RGB keyboard found ({USB.LenovoRgb.Kind}) - initializing RGB controls");
            USB.LenovoRgb.Init();
            // Init() runs Apply(); IsAvailable() now reflects whether the
            // firmware accepted the write. False on SKUs that detect but
            // reject SetFeature.
            if (!USB.LenovoRgb.IsAvailable())
            {
                Helpers.Logger.WriteLine("Lenovo RGB apply failed - RGB controls hidden");
                return false;
            }
            return true;
        }

        if (!Aura.IsAvailable())
        {
            Helpers.Logger.WriteLine("No AURA HID device found - RGB controls hidden");
            return false;
        }

        Helpers.Logger.WriteLine("AURA HID device found - initializing RGB controls");

        // Send AURA HID init handshake (wake up the LED controller).
        // This is critical for I2C-HID keyboards (e.g., FA608PP) that need
        // the handshake before they respond to any RGB commands.
        // Upstream G-Helper's Aura.Init() does this in InputDispatcher.AutoKeyboard().
        Aura.Init();

        // Initialize the slash LED bar (A-cover) if this is a slash model.
        // Slash uses the same HID bus but a different command protocol (0xD2-D8
        // family vs AURA's 0xB3-B5). Safe to call on non-slash models (no-op).
        Slash.Init();

        // Load saved values into static fields BEFORE applying to hardware
        Aura.Mode = (AuraMode)Helpers.AppConfig.Get("aura_mode");
        Aura.Speed = (AuraSpeed)Helpers.AppConfig.Get("aura_speed");
        Aura.SetColor(Helpers.AppConfig.Get("aura_color", unchecked((int)0xFFFFFFFF)));
        Aura.SetColor2(Helpers.AppConfig.Get("aura_color2", 0));

        // Apply saved power state + mode so the keyboard lights up on startup
        Aura.ApplyPower();
        Aura.ApplyConfiguredBrightness("Startup");
        Aura.ApplyAura();

        Task.Run(GHelper.Linux.Input.MKeyControl.ApplyAll);

        return true;
    }

    /// <summary>True when the RGB panel drives the Lenovo ITE backend
    /// (LenovoRgb) instead of ASUS Aura.</summary>
    private bool IsLenovoRgb => Helpers.AppConfig.IsLenovoDevice() && USB.LenovoRgb.IsAvailable();

    private void InitAura()
    {
        if (_auraInitialized)
            return;

        // The HID handshake runs on the startup worker (App posts
        // RefreshKeyboard when it finishes); never do it inline on the UI
        // thread - it used to stall the first window paint on ASUS.
        if (!_auraHardwareInitialized)
        {
            panelAura.IsVisible = Helpers.AppConfig.Is("show_aura_dev");
            return;
        }

        // Hardware init already ran; this is a cache-hit availability check.
        bool hasAura = InitAuraHardware();
        panelAura.IsVisible = hasAura || Helpers.AppConfig.Is("show_aura_dev");
        if (!hasAura)
            return;

        if (IsLenovoRgb)
        {
            InitLenovoRgbCombos();
            _auraInitialized = true;
            UpdateColorButtons();
            return;
        }

        _auraInitialized = true;

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
            if (kv.Key == Aura.Mode)
                selectedModeIdx = idx;
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
            if (kv.Key == Aura.Speed)
                selectedSpeedIdx = idx;
            idx++;
        }
        comboAuraSpeed.SelectedIndex = selectedSpeedIdx;

        _suppressAuraEvents = false;

        // Update color button backgrounds and second color visibility
        UpdateColorButtons();
    }

    private void UpdateColorButtons()
    {
        if (IsLenovoRgb)
        {
            buttonColor1.Background = new SolidColorBrush(
                Color.FromRgb(USB.LenovoRgb.ColorR, USB.LenovoRgb.ColorG, USB.LenovoRgb.ColorB));
            buttonColor2.Background = new SolidColorBrush(
                Color.FromRgb(USB.LenovoRgb.Color2R, USB.LenovoRgb.Color2G, USB.LenovoRgb.Color2B));
            buttonColor1.IsVisible = USB.LenovoRgb.UsesColor();
            buttonColor2.IsVisible = USB.LenovoRgb.HasSecondColor();
            comboAuraSpeed.IsVisible = USB.LenovoRgb.UsesSpeed();
            return;
        }

        buttonColor1.Background = new SolidColorBrush(
            Color.FromRgb(Aura.ColorR, Aura.ColorG, Aura.ColorB));
        buttonColor2.Background = new SolidColorBrush(
            Color.FromRgb(Aura.Color2R, Aura.Color2G, Aura.Color2B));

        // White-only keyboards (probe FEAT2_ONE_ZONE_RED_EFFECT or
        // AppConfig.IsWhite model list) ignore G/B channels - hide color
        // pickers entirely so the user can't pick blue and get white instead.
        buttonColor1.IsVisible = !Aura.isWhite && Aura.UsesColor();
        buttonColor2.IsVisible = !Aura.isWhite && Aura.HasSecondColor();

        // Speed dropdown - hide for modes where it has no effect (Static / the
        // software-driven modes Heatmap/GpuMode/Battery / static paints
        // Gradient/ZoneTest). Cleaner than always showing a non-functional
        // dropdown; diverges from upstream which always shows speed.
        comboAuraSpeed.IsVisible = Aura.UsesSpeed();
    }

    /// <summary>Populate the mode/speed combos from the Lenovo RGB backend.</summary>
    private void InitLenovoRgbCombos()
    {
        _suppressAuraEvents = true;

        comboAuraMode.Items.Clear();
        int selectedIdx = 0, idx = 0;
        foreach (var kv in USB.LenovoRgb.GetModes())
        {
            comboAuraMode.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
            if (kv.Key == (int)USB.LenovoRgb.Mode)
                selectedIdx = idx;
            idx++;
        }
        comboAuraMode.SelectedIndex = selectedIdx;

        comboAuraSpeed.Items.Clear();
        selectedIdx = 0;
        idx = 0;
        foreach (var kv in USB.LenovoRgb.GetSpeeds())
        {
            comboAuraSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
            if (kv.Key == USB.LenovoRgb.Speed)
                selectedIdx = idx;
            idx++;
        }
        comboAuraSpeed.SelectedIndex = selectedIdx;

        _suppressAuraEvents = false;
    }

    /// <summary>Rebuild Aura mode/speed combo items with current language strings.</summary>
    private void RefreshAuraCombos()
    {
        if (!_auraInitialized)
            return;

        if (IsLenovoRgb)
        {
            InitLenovoRgbCombos();
            return;
        }

        _suppressAuraEvents = true;

        // Mode combo, select based on current Aura.Mode (always up-to-date)
        int savedMode = (int)Aura.Mode;
        comboAuraMode.Items.Clear();
        int selectedIdx = 0, idx = 0;
        foreach (var kv in Aura.GetModes())
        {
            comboAuraMode.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if ((int)kv.Key == savedMode)
                selectedIdx = idx;
            idx++;
        }
        comboAuraMode.SelectedIndex = selectedIdx;

        // Speed combo, select based on current Aura.Speed (always up-to-date)
        int savedSpeed = (int)Aura.Speed;
        comboAuraSpeed.Items.Clear();
        selectedIdx = 0;
        idx = 0;
        foreach (var kv in Aura.GetSpeeds())
        {
            comboAuraSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if ((int)kv.Key == savedSpeed)
                selectedIdx = idx;
            idx++;
        }
        comboAuraSpeed.SelectedIndex = selectedIdx;

        _suppressAuraEvents = false;
    }

    private void ComboAuraMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuraEvents)
            return;
        if (comboAuraMode.SelectedItem is ComboBoxItem item && item.Tag is int modeVal)
        {
            if (IsLenovoRgb)
            {
                Helpers.Logger.WriteLine($"Lenovo RGB mode changed → {(USB.LenovoRgbMode)modeVal}");
                Helpers.AppConfig.Set("lenovo_rgb_mode", modeVal);
                USB.LenovoRgb.Mode = (USB.LenovoRgbMode)modeVal;
                UpdateColorButtons();
                ApplyAuraAsync();
                return;
            }
            Helpers.Logger.WriteLine($"AURA mode changed → {(AuraMode)modeVal}");
            Helpers.AppConfig.Set("aura_mode", modeVal);
            Aura.Mode = (AuraMode)modeVal;
            UpdateColorButtons();
            ApplyAuraAsync();
        }
    }

    private void ComboAuraSpeed_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAuraEvents)
            return;
        if (comboAuraSpeed.SelectedItem is ComboBoxItem item && item.Tag is int speedVal)
        {
            if (IsLenovoRgb)
            {
                Helpers.AppConfig.Set("lenovo_rgb_speed", speedVal);
                USB.LenovoRgb.Speed = speedVal;
                ApplyAuraAsync();
                return;
            }
            Helpers.AppConfig.Set("aura_speed", speedVal);
            Aura.Speed = (AuraSpeed)speedVal;
            ApplyAuraAsync();
        }
    }

    private void ButtonColor1_Click(object? sender, RoutedEventArgs e)
    {
        if (IsLenovoRgb)
        {
            ShowColorPicker("lenovo_rgb_color", USB.LenovoRgb.ColorR, USB.LenovoRgb.ColorG, USB.LenovoRgb.ColorB, (r, g, b) =>
            {
                USB.LenovoRgb.ColorR = r;
                USB.LenovoRgb.ColorG = g;
                USB.LenovoRgb.ColorB = b;
                Helpers.AppConfig.Set("lenovo_rgb_color", USB.LenovoRgb.GetColorArgb());
                UpdateColorButtons();
                ApplyAuraAsync();
            });
            return;
        }

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
        if (IsLenovoRgb)
        {
            ShowColorPicker("lenovo_rgb_color2", USB.LenovoRgb.Color2R, USB.LenovoRgb.Color2G, USB.LenovoRgb.Color2B, (r, g, b) =>
            {
                USB.LenovoRgb.Color2R = r;
                USB.LenovoRgb.Color2G = g;
                USB.LenovoRgb.Color2B = b;
                Helpers.AppConfig.Set("lenovo_rgb_color2", USB.LenovoRgb.GetColor2Argb());
                UpdateColorButtons();
                ApplyAuraAsync();
            });
            return;
        }

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
    /// Thin wrapper around <see cref="ColorPicker.Show(Window, byte, byte, byte, Action{byte, byte, byte})"/>.
    /// The actual picker UI lives in <c>Helpers/ColorPicker.cs</c> so it can be
    /// shared with ExtraWindow's tray-icon color buttons. <paramref name="configKey"/>
    /// is unused by the picker itself - it is kept on the call signature so the
    /// caller can clearly mark which config key the chosen color will land in.
    /// </summary>
    private void ShowColorPicker(string configKey, byte initR, byte initG, byte initB, Action<byte, byte, byte> onColorSet)
    {
        bool allowRandom = configKey == "aura_color" && Aura.HasRandomColor();
        ColorPicker.Show(this, initR, initG, initB, onColorSet, allowRandom);
    }

    private void ApplyAuraAsync()
    {
        bool lenovo = IsLenovoRgb;
        Task.Run(() =>
        {
            try
            {
                if (lenovo)
                    USB.LenovoRgb.Apply();
                else
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
        if (wmi == null)
            return;

        // Cycle 0..max then wrap. Driver-reported max prevents overshoot on
        // Lenovo on/off (1) and tristate (2) backlights.
        int max = Math.Max(1, wmi.KbdMaxBrightness);
        int current = wmi.GetKeyboardBrightness();
        if (current < 0)
            current = 0;
        int next = (current + 1) % (max + 1);

        // HID command + sysfs write (no-op HID on Lenovo, sysfs still works).
        USB.Aura.ApplyBrightness(next, "Toggle");

        // Persist so keep-on timer and AC/DC transitions use the new value.
        Helpers.AppConfig.Set(USB.Aura.GetBrightnessConfigKey(), next);

        RefreshKeyboard();
    }

    // AnimeMatrix / Slash LED

    /// <summary>
    /// Show the AnimeMatrix panel if a device is detected or the dev
    /// checkbox (<c>show_anime_matrix_dev</c>) is enabled.
    /// </summary>
    public void RefreshAnimeMatrix()
    {
        bool devMode = Helpers.AppConfig.Is("show_anime_matrix_dev");
        bool hasDevice = App.AnimeMatrix?.IsValid == true;
        bool show = devMode || hasDevice || Helpers.AppConfig.IsAnimeMatrix() || Helpers.AppConfig.IsSlash();

        panelAnimeMatrix.IsVisible = show;

        if (!show)
            return;

        // Sync combos to saved config
        int mode = Helpers.AppConfig.Get("matrix_running", 0);
        if (mode >= 0 && mode < comboMatrixMode.ItemCount)
            comboMatrixMode.SelectedIndex = mode;

        int bright = Helpers.AppConfig.Get("matrix_brightness", 2);
        if (bright >= 0 && bright < comboMatrixBrightness.ItemCount)
            comboMatrixBrightness.SelectedIndex = bright;
    }

    private void ComboMatrixMode_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (!_layoutReady)
            return;
        int mode = comboMatrixMode.SelectedIndex;
        Helpers.AppConfig.Set("matrix_running", mode);
        App.AnimeMatrix?.SetDevice();
    }

    private void ComboMatrixBrightness_SelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (!_layoutReady)
            return;
        int bright = comboMatrixBrightness.SelectedIndex;
        Helpers.AppConfig.Set("matrix_brightness", bright);
        App.AnimeMatrix?.SetDevice();
    }

    private MatrixWindow? _matrixWindow;

    private void ButtonMatrixSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_matrixWindow == null || !_matrixWindow.IsVisible)
        {
            _matrixWindow = new MatrixWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _matrixWindow.Topmost = true;
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_matrixWindow);
            _matrixWindow.Show();
        }
        else
        {
            _matrixWindow.Activate();
        }
    }

    // Battery

    private void RefreshBattery()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return;

        // For models that only accept 60/80/100, snap slider to valid values
        if (Helpers.AppConfig.IsChargeLimit6080())
        {
            sliderBattery.TickFrequency = 5;
            sliderBattery.IsSnapToTickEnabled = true;
        }

        int limit = wmi.GetBatteryChargeLimit();
        if (limit > 0)
        {
            _updatingBatterySlider = true;
            sliderBattery.Value = limit;
            _updatingBatterySlider = false;
            labelBatteryLimit.Text = $"{limit}%";
            // Combined header: "Battery Charge Limit: 80%".
            labelBattery.Text = Labels.Format("battery_limit_prefix", limit);
        }

        // Show discharge/charge rate in battery section header (right side)
        // and charge percentage in footer ("Charge: 71.5%").
        var power = App.Power;
        if (power != null)
        {
            int level = power.GetBatteryPercentage();
            bool acPlugged = power.IsOnAcPower();
            int drainMw = power.GetBatteryDrainRate();

            if (Math.Abs(drainMw) < 1500)
                drainMw = 0;

            // Discharge/charge rate in battery header right column
            if (drainMw != 0)
            {
                double watts = Math.Abs(drainMw) / 1000.0;
                string rateStr = drainMw > 0
                    ? Labels.Format("discharging_watts", $"{watts:F1}")
                    : Labels.Format("charging_watts", $"{watts:F1}");
                labelCharge.Text = rateStr;
            }
            else if (acPlugged)
            {
                labelCharge.Text = Labels.Get("plugged_in");
            }

            // Charge level in footer
            if (level >= 0)
            {
                labelChargeFooter.Text = Labels.Format("charge_prefix", level);
            }
        }
    }

    private bool _updatingBatterySlider;
    private System.Timers.Timer? _batteryDebounce;

    private void SliderBattery_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingBatterySlider)
            return;

        int limit = (int)e.NewValue;

        // Update labels immediately (responsive UI)
        labelBatteryLimit.Text = $"{limit}%";
        labelBattery.Text = Labels.Format("battery_limit_prefix", limit);

        // Debounce the sysfs write - only write 300ms after the user stops dragging
        _batteryDebounce?.Stop();
        _batteryDebounce ??= new System.Timers.Timer(300) { AutoReset = false };
        _batteryDebounce.Elapsed -= BatteryDebounce_Elapsed;
        _batteryDebounce.Elapsed += BatteryDebounce_Elapsed;
        _batteryDebounce.Start();
    }

    private void BatteryDebounce_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int limit = (int)sliderBattery.Value;
            int actual = Battery.BatteryControl.SetBatteryChargeLimit(limit);

            labelBatteryLimit.Text = $"{actual}%";
            labelBattery.Text = Labels.Format("battery_limit_prefix", actual);

            if (actual != limit && actual > 0)
            {
                _updatingBatterySlider = true;
                sliderBattery.Value = actual;
                _updatingBatterySlider = false;
            }
        });
    }

    private void SetBatteryLimit(int percent)
    {
        _batteryDebounce?.Stop();
        int actual = Battery.BatteryControl.SetBatteryChargeLimit(percent);
        _updatingBatterySlider = true;
        sliderBattery.Value = actual;
        _updatingBatterySlider = false;
        RefreshBattery();
    }

    private void ButtonBattery60_Click(object? sender, RoutedEventArgs e)
    {
        SetBatteryLimit(60);
    }

    private void ButtonBattery80_Click(object? sender, RoutedEventArgs e)
    {
        SetBatteryLimit(80);
    }

    private void ButtonBattery100_Click(object? sender, RoutedEventArgs e)
    {
        SetBatteryLimit(100);
    }

    // Footer

    private void RefreshFooter()
    {
        var sys = App.System;
        if (sys == null)
            return;

        string model = sys.GetModelName() ?? Labels.Get("unknown_asus");

        // Show model in window title (like upstream)
        Title = Labels.Format("title_prefix", model);

        // Version + model in footer
        labelVersion.Text = Labels.Format("version_prefix", Helpers.AppConfig.AppVersion, model);

        // Play glyph + localized game title.
        labelArcade.Text = "\u25B6  " + Labels.Get("arcade_game_title");

        // Check autostart status from config (suppress to avoid re-writing .desktop file)
        _suppressEvents = true;
        checkStartup.IsChecked = Helpers.AppConfig.IsNotFalse("autostart");
        _suppressEvents = false;

        // System info (same as ExtraWindow)
        labelSysModel.Text = Labels.Format("model_prefix", model);
        labelSysBios.Text = Labels.Format("bios_prefix", sys.GetBiosVersion());
        labelSysKernel.Text = Labels.Format("kernel_prefix", sys.GetKernelVersion());

        bool wmiLoaded = sys.IsPlatformDriverLoaded();
        if (Helpers.AppConfig.IsLenovoDevice())
        {
            var (driver, loaded) = Platform.Linux.Lenovo.LenovoDetection.PlatformDriver();
            labelSysWmi.Text = $"{driver}: {(loaded ? "loaded" : "not loaded")}";
        }
        else
        {
            labelSysWmi.Text = wmiLoaded ? Labels.Get("asus_wmi_loaded") : Labels.Get("asus_wmi_not_loaded");
        }

        var features = new List<string>();
        var wmi = App.Wmi;
        if (wmi != null)
        {
            if (wmi.IsFeatureSupported(AsusAttributes.ThrottleThermalPolicy))
                features.Add(Labels.Get("feature_perf_modes"));
            if (wmi.IsFeatureSupported(AsusAttributes.DgpuDisable))
                features.Add(Labels.Get("feature_gpu_eco"));
            if (wmi.IsFeatureSupported(AsusAttributes.GpuMuxMode))
                features.Add(Labels.Get("feature_mux"));
            if (wmi.IsFeatureSupported(AsusAttributes.PanelOd))
                features.Add(Labels.Get("feature_overdrive"));
            if (wmi.IsFeatureSupported(AsusAttributes.MiniLedMode))
                features.Add(Labels.Get("feature_miniled"));
            if (wmi.IsFeatureSupported(AsusAttributes.PptPl1Spl))
                features.Add(Labels.Get("feature_ppt"));
            if (wmi.IsFeatureSupported(AsusAttributes.NvDynamicBoost))
                features.Add(Labels.Get("feature_nv_boost"));
        }

        labelSysFeatures.Text = features.Count > 0
            ? Labels.Format("features_prefix", string.Join(", ", features))
            : Labels.Get("no_features");
    }

    private void CheckStartup_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool enabled = checkStartup.IsChecked ?? false;
        Helpers.AppConfig.Set("autostart", enabled ? 1 : 0);
        App.System?.SetAutostart(enabled);
    }

    private ExtraWindow? _extraWindow;

    private void ButtonExtra_Click(object? sender, RoutedEventArgs e)
    {
        if (_extraWindow == null || !_extraWindow.IsVisible)
        {
            _extraWindow = new ExtraWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _extraWindow.Topmost = true;
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_extraWindow);
            _extraWindow.Show();
        }
        else
        {
            _extraWindow.Activate();
        }
    }

    private AudioWindow? _audioWindow;

    /// <summary>
    /// Click on the FN-Lock-style toggle at the right of the Microphone
    /// header: starts or stops the audio helper. Persists the user's
    /// intent so the state survives across launches.
    /// </summary>
    private void ButtonAudioToggle_Click(object? sender, RoutedEventArgs e)
    {
        Helpers.AudioHelper.Instance.ToggleMaster();
        RefreshAudioToggle();
        _audioWindow?.RefreshFromMain();
    }

    /// <summary>
    /// Click on the chain-configuration button below the toggle: opens the
    /// dedicated audio window. Never changes the helper's running state.
    /// </summary>
    private void ButtonAudio_Click(object? sender, RoutedEventArgs e)
    {
        if (_audioWindow == null || !_audioWindow.IsVisible)
        {
            _audioWindow = new AudioWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _audioWindow.Topmost = true;
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_audioWindow);
            _audioWindow.Show();
        }
        else
        {
            _audioWindow.Activate();
        }
    }

    /// <summary>
    /// Sync the FN-Lock-style toggle's label + style class to the helper's
    /// actual running state. Called after toggles and from external paths
    /// (hotkey, helper crash) to keep the visual honest.
    /// </summary>
    public void RefreshAudioToggle()
    {
        bool audioVisible = !Helpers.AppConfig.Is("disable_audio");
        bool changed = panelAudio.IsVisible != audioVisible;
        panelAudio.IsVisible = audioVisible;

        bool on = Helpers.AudioHelper.Instance.IsRunning;
        labelAudioToggle.Text = on ? Labels.Get("audio_toggle_on") : Labels.Get("audio_toggle_off");
        if (on && !buttonAudioToggle.Classes.Contains("audio-on"))
            buttonAudioToggle.Classes.Add("audio-on");
        else if (!on && buttonAudioToggle.Classes.Contains("audio-on"))
            buttonAudioToggle.Classes.Remove("audio-on");

        if (changed && _layoutReady)
        {
            Height = double.NaN;
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.Height;
            Dispatcher.UIThread.Post(
                () => WindowPositioner.BottomRight(this),
                DispatcherPriority.Loaded);
        }
    }

    private HandheldWindow? _handheldWindow;

    /// <summary>Show the Ally panel + prime its labels when on RC71L/RC72L.</summary>
    public void RefreshAllyPanel()
    {
        bool isAlly = Helpers.AppConfig.IsAlly();
        panelAlly.IsVisible = isAlly || Helpers.AppConfig.Is("show_ally_dev");

        // Hide the laptop GPU mode buttons (Eco / Standard / Optimized /
        // Ultimate) on Ally - there's no MUX and no dGPU. Match Windows
        // g-helper Settings.cs:1810-1821 which does the same thing.
        panelGpuModes.IsVisible = !isAlly;

        // Show / hide the GPU section title differently on Ally - the simple
        // "GPU Mode: …" text is replaced by a fixed "GPU" so we don't show a
        // status that doesn't apply.
        if (isAlly && labelGPU != null)
        {
            labelGPU.Text = "GPU";
        }

        // XG Mobile (eGPU dock)
        RefreshXgMobile();

        if (!isAlly)
            return;

        // Controller mode label - localized via i18n.
        var saved = (Ally.ControllerMode)Helpers.AppConfig.Get(
            "controller_mode", (int)Ally.ControllerMode.Auto);
        string modeLabel = saved switch
        {
            Ally.ControllerMode.Auto => Labels.Get("controller_mode_auto"),
            Ally.ControllerMode.Gamepad => Labels.Get("controller_mode_gamepad"),
            Ally.ControllerMode.Mouse => Labels.Get("controller_mode_mouse"),
            Ally.ControllerMode.WASD => Labels.Get("controller_mode_wasd"),
            Ally.ControllerMode.Skip => Labels.Get("controller_mode_skip"),
            _ => "?",
        };
        labelControllerMode.Text = Labels.Format("ally_mode_label_format", modeLabel);

        // Backlight label (kbd brightness 0..3 → 0/33/66/100%).
        int br = App.Wmi?.GetKeyboardBrightness() ?? 0;
        labelAllyBacklight.Text = Labels.Format("ally_backlight_label_format",
            (int)Math.Round(br * 33.33));

        // iGPU sensors (refreshed lazily; the existing tray sensor timer
        // could also call this, but for now a one-shot on panel show is
        // enough to verify rendering).
        int? busy = Gpu.AMD.LinuxAmdGpuMetrics.GetIgpuBusyPercent();
        float? power = Gpu.AMD.LinuxAmdGpuMetrics.GetIgpuPowerWatts();
        if (busy != null || power != null)
        {
            labelAllyMetrics.Text =
                (busy != null ? $"{busy}% " : "") +
                (power != null ? $"{power.Value:0.0}W" : "");
        }
    }

    private void ButtonControllerMode_Click(object? sender, RoutedEventArgs e)
    {
        var ally = App.Ally;
        if (ally == null)
            return;
        ally.ToggleMode();
        RefreshAllyPanel();
    }

    private void ButtonAllyBacklight_Click(object? sender, RoutedEventArgs e)
    {
        var ally = App.Ally;
        if (ally == null)
            return;
        ally.ToggleBacklight();
        RefreshAllyPanel();
    }

    private void ButtonOpenHandheld_Click(object? sender, RoutedEventArgs e)
    {
        if (_handheldWindow == null || !_handheldWindow.IsVisible)
        {
            _handheldWindow = new HandheldWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _handheldWindow.Topmost = true;
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_handheldWindow);
            _handheldWindow.Show();
        }
        else
        {
            _handheldWindow.Activate();
        }
    }

    /// <summary>
    /// Last-known snapshot of the kernel-reported dock state, used by the
    /// hot-plug poll timer to decide when to call <see cref="RefreshXgMobile"/>.
    /// "0", "1", or null when the attribute is unreadable.
    /// </summary>
    private string? _xgmConnectedSnapshot;
    private string? _xgmEnabledSnapshot;

    /// <summary>True while a toggle is in flight (15 s settle window).</summary>
    private bool _xgmToggling;

    /// <summary>
    /// Refresh the XG Mobile dock button. Reads <c>egpu_connected</c> from
    /// either the legacy asus-nb-wmi sysfs node or the asus-armoury fw-attr
    /// (whichever is exposed by the running kernel). When the value is 1
    /// the dock is physically attached and we expose a button that toggles
    /// <c>egpu_enable</c>. Visibility is NOT gated on the model - any ROG
    /// laptop with the XG Mobile receptacle (Flow X13/X16/Z13, Strix XG,
    /// ROG Ally) is supported. Whether a reboot is required depends on
    /// which backend handles the toggle write - see <see cref="ButtonXGM_Click"/>.
    /// </summary>
    private void RefreshXgMobile()
    {
        // egpu_connected: 1 = dock attached, 0 = standalone, missing = no XGM
        // path present at all.
        var connectedPath = SysfsHelper.ResolveAttrPath(
            Platform.Linux.AsusAttributes.EgpuConnected);
        string? connectedRaw = connectedPath != null
            ? SysfsHelper.ReadAttribute(connectedPath)?.Trim()
            : null;
        _xgmConnectedSnapshot = connectedRaw;

        bool connected = connectedRaw == "1";
        buttonXGM.IsVisible = connected || Helpers.AppConfig.Is("show_xgm_dev");
        if (!connected)
        {
            _xgmEnabledSnapshot = null;
            // Make sure we don't leave the GPU mode buttons disabled if the
            // dock was yanked while it was active - re-enable them so the
            // user can switch back to a normal laptop GPU mode.
            if (buttonEco != null)
                buttonEco.IsEnabled = true;
            if (buttonStandard != null)
                buttonStandard.IsEnabled = true;
            if (buttonUltimate != null)
                buttonUltimate.IsEnabled = true;
            if (buttonOptimized != null)
                buttonOptimized.IsEnabled = true;
            return;
        }

        // Reflect current egpu_enable state.
        var enablePath = SysfsHelper.ResolveAttrPath(
            Platform.Linux.AsusAttributes.EgpuEnable);
        string? enabledRaw = enablePath != null
            ? SysfsHelper.ReadAttribute(enablePath)?.Trim()
            : null;
        _xgmEnabledSnapshot = enabledRaw;
        bool enabled = !string.IsNullOrEmpty(enabledRaw) && enabledRaw != "0";

        labelXGM.Text = _xgmToggling
            ? Labels.Get("xgm_locking")
            : (enabled ? Labels.Get("xgm_button_disable") : Labels.Get("xgm_button_enable"));

        var classes = "ghelper" + (enabled ? " ghelper-active" : "");
        if (buttonXGM.Classes.ToString() != classes)
        {
            buttonXGM.Classes.Clear();
            foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                buttonXGM.Classes.Add(c);
        }

        bool blockedByEco = false;
        if (App.GpuControl is Gpu.NVidia.LinuxNvidiaGpuControl
            && !Helpers.AppConfig.IsAMDiGPU()
            && App.Wmi?.GetGpuEco() == true)
        {
            blockedByEco = true;
        }
        buttonXGM.IsEnabled = !_xgmToggling && !blockedByEco;

        bool gpuModeButtonsEnabled = !enabled && !_xgmToggling;
        if (buttonEco != null)
            buttonEco.IsEnabled = gpuModeButtonsEnabled;
        if (buttonStandard != null)
            buttonStandard.IsEnabled = gpuModeButtonsEnabled;
        if (buttonUltimate != null)
            buttonUltimate.IsEnabled = gpuModeButtonsEnabled;
        if (buttonOptimized != null)
            buttonOptimized.IsEnabled = gpuModeButtonsEnabled;

        // Tooltip mirrors Windows g-helper:
        //   active   -> "Click to disable"
        //   eco-grey -> "Switch out of Eco mode first"
        //   else     -> "Click to enable"
        string tip = blockedByEco
            ? Labels.Get("xgm_tooltip_eco")
            : (enabled ? Labels.Get("xgm_tooltip_disable") : Labels.Get("xgm_tooltip_enable"));
        ToolTip.SetTip(buttonXGM, tip);
    }

    /// <summary>
    /// Hot-plug detection: re-read <c>egpu_connected</c> and
    /// <c>egpu_enable</c> on every refresh tick (every 2 s while the
    /// MainWindow is visible) and call <see cref="RefreshXgMobile"/> only
    /// when something changed. Keeps the button in sync when the user
    /// docks / undocks the XG Mobile without restarting the app.
    /// </summary>
    private void PollXgmIfChanged()
    {
        // Skip the poll while a toggle is in flight - the click handler
        // owns the UI state during the 15 s settle window.
        if (_xgmToggling)
            return;

        try
        {
            var connectedPath = SysfsHelper.ResolveAttrPath(
                Platform.Linux.AsusAttributes.EgpuConnected);
            string? connected = connectedPath != null
                ? SysfsHelper.ReadAttribute(connectedPath)?.Trim()
                : null;

            string? enabled = null;
            if (connected == "1")
            {
                var enablePath = SysfsHelper.ResolveAttrPath(
                    Platform.Linux.AsusAttributes.EgpuEnable);
                if (enablePath != null)
                    enabled = SysfsHelper.ReadAttribute(enablePath)?.Trim();
            }

            if (connected != _xgmConnectedSnapshot || enabled != _xgmEnabledSnapshot)
            {
                Helpers.Logger.WriteLine(
                    $"XGMobile: poll detected change connected={_xgmConnectedSnapshot}->{connected} enabled={_xgmEnabledSnapshot}->{enabled}");
                RefreshXgMobile();
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"PollXgmIfChanged: {ex.Message}");
        }
    }

    private async void ButtonXGM_Click(object? sender, RoutedEventArgs e)
    {
        if (_xgmToggling)
            return;

        var enablePath = SysfsHelper.ResolveAttrPath(
            Platform.Linux.AsusAttributes.EgpuEnable);
        if (enablePath == null)
        {
            App.System?.ShowNotification(Labels.Get("xgm_label"),
                Labels.Get("xgm_unavailable"), "dialog-warning");
            return;
        }

        var raw = SysfsHelper.ReadAttribute(enablePath)?.Trim();
        bool currentlyEnabled = !string.IsNullOrEmpty(raw) && raw != "0";
        string targetValue = currentlyEnabled ? "0" : "1";

        if (currentlyEnabled)
        {
            bool yes = await Dialogs.ConfirmDialog.ShowAsync(
                this,
                Labels.Get("xgm_disable_title"),
                Labels.Get("xgm_disable_message"));
            if (!yes)
                return;

            // On disable, also restore the dock fan to firmware-default to
            // avoid the dock spinning a fan against a powered-down rail
            // mid-transition. Mirrors Windows g-helper's pre-disable XGM.Reset.
            try
            { USB.XGM.Reset(); }
            catch (Exception ex) { Helpers.Logger.WriteLine($"XGM.Reset (pre-disable): {ex.Message}"); }
        }

        _xgmToggling = true;
        RefreshXgMobile();

        bool enabling = targetValue == "1";
        await Task.Run(async () =>
        {
            var result = SysfsHelper.WriteToAllBackendsDetailed(
                Platform.Linux.AsusAttributes.EgpuEnable, targetValue);

            // Optional 6850M raw_wmi fallback: if the user has the special
            // RX 6850M dock and only the fw-attr path took the write, also
            // try the ACPI 0x101 magic value via raw debugfs. See
            // Phase 9 in the plan.
            if (Helpers.AppConfig.Is("xgm_special") && enabling && !result.Legacy)
            {
                try
                {
                    bool rawOk = Platform.Linux.AsusWmiDebugfs.WriteRaw(
                        Platform.Linux.AsusWmiDebugfs.DEVID_EGPU, 0x101u);
                    Helpers.Logger.WriteLine($"XGMobile: 6850M raw_wmi 0x101 fallback ok={rawOk}");
                }
                catch (Exception ex)
                {
                    Helpers.Logger.WriteLine($"XGMobile: raw_wmi 0x101 fallback failed: {ex.Message}");
                }
            }

            Helpers.Logger.WriteLine(
                $"XGMobile: egpu_enable {raw} -> {targetValue} (legacy={result.Legacy} fwAttr={result.FwAttr})");

            if (enabling && result.Any)
            {
                try
                { USB.XGM.Init(); }
                catch (Exception ex) { Helpers.Logger.WriteLine($"XGMobile post-enable Init: {ex.Message}"); }
            }

            // Smart user message:
            //   legacy succeeded -> ACPI applies immediately, ~15 s settle
            //   only fw-attr     -> asus-armoury stages until next reboot
            //   nothing wrote    -> hard error, surface "unavailable"
            string body;
            string icon;
            if (!result.Any)
            {
                body = Labels.Get("xgm_unavailable");
                icon = "dialog-warning";
            }
            else if (result.Legacy)
            {
                body = Labels.Get("xgm_toggled_immediate");
                icon = "preferences-system";
            }
            else
            {
                body = Labels.Get("xgm_toggled_reboot");
                icon = "system-reboot";
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                App.System?.ShowNotification(Labels.Get("xgm_label"), body, icon);
            });

            // 15 s settle - match Windows g-helper. After this we refresh
            // the UI to reflect the new state regardless of which backend
            // handled the write.
            await Task.Delay(TimeSpan.FromSeconds(15));

            // Post-enable steps that must run after the dock GPU has had
            // time to enumerate on PCIe (mirrors Windows g-helper
            // GPUModeControl.cs:320-323):
            //
            //   1. Push the saved fan curve when auto_apply_fans is on -
            //      otherwise the dock runs the firmware default until the
            //      next mode switch even if the user has a custom curve.
            //   2. Probe for the RX 6850M and persist xgm_special so the
            //      next enable cycle uses the ACPI 0x101 path automatically.
            if (enabling && result.Any)
            {
                try
                {
                    if (Helpers.AppConfig.IsMode("auto_apply_fans") && USB.XGM.IsConnected())
                    {
                        byte[] curve = Helpers.AppConfig.GetFanConfig(3);
                        if (curve.Length == 16)
                        {
                            // Apply the same 72% PWM clamp FansWindow uses.
                            var clamped = new byte[16];
                            Array.Copy(curve, clamped, 16);
                            for (int i = 8; i < 16; i++)
                            {
                                if (clamped[i] > 72)
                                    clamped[i] = 72;
                            }
                            USB.XGM.SetFan(clamped);
                            Helpers.Logger.WriteLine("XGMobile post-enable: pushed saved fan curve");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Helpers.Logger.WriteLine($"XGMobile post-enable SetFan: {ex.Message}");
                }
            }

            try
            { Gpu.AMD.LinuxAmdDgpuDetect.RefreshXgmSpecialFlag(); }
            catch (Exception ex) { Helpers.Logger.WriteLine($"XGM RX6850M probe: {ex.Message}"); }
        });

        _xgmToggling = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshXgMobile);
    }

    /// <summary>
    /// FN-Lock master toggle. Click flips between two states:
    ///   OFF (default, grayed) - software remapper stopped, F1..F12 emit
    ///                          natively.
    ///   ON  (blue accent)     - software remapper running with media keys
    ///                          mode active.
    /// State is held by FnLockRemapper and persisted by App (fnlock_enabled,
    /// fnlock_on). Delegates to <see cref="App.SetFnLockEnabled"/> so the
    /// tray menu and any other UI surface go through the same authoritative
    /// path.
    /// </summary>
    private void ButtonFnLock_Click(object? sender, RoutedEventArgs e)
    {
        bool currentlyOn = (App.FnLock?.IsActive ?? false) && App.FnLock!.FnLockOn;
        App.SetFnLockEnabled(!currentlyOn);
        RefreshFnLockButton();
    }

    /// <summary>Show/hide the on-screen keyboard (same path as the tray item).
    /// A visible button matters on handhelds where the tray is hard to reach
    /// by touch.</summary>
    private void ButtonOsk_Click(object? sender, RoutedEventArgs e)
        => App.ToggleOskWindow();

    /// <summary>
    /// Update the FN-Lock title-row button visual to reflect remapper state.
    /// Always visible; styling flips between the default ghelper button (OFF /
    /// grayed) and the fnlock-on accent class (solid blue, ON).
    /// </summary>
    public void RefreshFnLockButton()
    {
        // Source of truth: the remapper itself. IsActive means we're grabbing
        // input devices; FnLockOn means media-keys mode is currently active.
        bool active = (App.FnLock?.IsActive ?? false) && App.FnLock!.FnLockOn;

        // Toggle the fnlock-on style class on/off without disturbing the
        // base "ghelper" class.
        const string accent = "fnlock-on";
        if (active && !buttonFnLock.Classes.Contains(accent))
            buttonFnLock.Classes.Add(accent);
        else if (!active && buttonFnLock.Classes.Contains(accent))
            buttonFnLock.Classes.Remove(accent);

        ToolTip.SetTip(buttonFnLock, active
            ? Labels.Get("fnlock_osd_on")
            : Labels.Get("fnlock_osd_off"));
    }


    private const string DonateUrl = "https://buymeacoffee.com/utajum";

    private void InitDonate()
    {
        Helpers.CoinSound.EnsureReady();

        _coinMuted = Helpers.AppConfig.Get("donate_muted", 1) == 1;
        UpdateMuteVisual();

        _coinTransform = new TranslateTransform();
        buttonDonate.RenderTransform = _coinTransform;

        // Easter egg: 7 clicks on version label within 3 seconds
        labelVersion.PointerPressed += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if ((now - _versionClickStart).TotalSeconds > 3)
            {
                _versionClickCount = 0;
                _versionClickStart = now;
            }
            _versionClickCount++;
            if (_versionClickCount == 5)
                Helpers.Logger.WriteLine("Easter egg: 🎮 getting close...");
            if (_versionClickCount >= 7)
            {
                _versionClickCount = 0;
                OpenArcade();
            }
        };

        // Hover events
        buttonDonate.PointerEntered += ButtonDonate_PointerEntered;
        buttonDonate.PointerExited += ButtonDonate_PointerExited;

        // Mute toggle - intercept click on the mute badge (Border + TextBlock)
        borderCoinMute.PointerPressed += LabelCoinMute_PointerPressed;
        labelCoinMute.PointerPressed += LabelCoinMute_PointerPressed;

        // Debounce timer: fires 2s after last click → opens URL
        _coinDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _coinDebounceTimer.Tick += CoinDebounce_Tick;

        // Shake timer: runs at ~30ms for oscillation frames
        _coinShakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _coinShakeTimer.Tick += CoinShake_Tick;
    }

    private void ButtonArcade_Click(object? sender, RoutedEventArgs e) => OpenArcade();

    private void OpenArcade()
    {
        if (_arcadeWindow == null || !_arcadeWindow.IsVisible)
        {
            _arcadeWindow = new ArcadeWindow();
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_arcadeWindow);
            _arcadeWindow.Show();
        }
        else
        {
            _arcadeWindow.Activate();
        }
    }

    private void ButtonDonate_Click(object? sender, RoutedEventArgs e)
    {
        _coinClickCount++;
        labelCoinCount.Text = $"\u00D7{_coinClickCount}";

        if (!_coinMuted)
            Helpers.CoinSound.Play();
        StartCoinShake();

        // Restart 2-second debounce
        _coinDebounceTimer?.Stop();
        _coinDebounceTimer?.Start();
    }

    private void ButtonDonate_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_coinMuted)
            Helpers.CoinSound.Play();
        StartCoinShake();
    }

    private void ButtonDonate_PointerExited(object? sender, PointerEventArgs e)
    {
        StopCoinShake();
    }

    private void LabelCoinMute_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true; // Don't trigger parent button click
        _coinMuted = !_coinMuted;
        UpdateMuteVisual();
        Helpers.AppConfig.Set("donate_muted", _coinMuted ? 1 : 0);
    }

    private void UpdateMuteVisual()
    {
        labelCoinMute.Text = _coinMuted ? "M" : "\u266A";
        labelCoinMute.Foreground = _coinMuted ? CoinDarkBrush : CoinGoldBrush;
    }

    private void CoinDebounce_Tick(object? sender, EventArgs e)
    {
        _coinDebounceTimer?.Stop();

        try
        {
            string url = $"{DonateUrl}?coins={_coinClickCount}";
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            Helpers.Logger.WriteLine($"Donate: opened {url}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Donate: failed to open URL: {ex.Message}");
        }

        _coinClickCount = 0;
        labelCoinCount.Text = "";
    }

    private void StartCoinShake()
    {
        _coinShakeFrame = 0;
        _coinShakeTimer?.Start();
    }

    private void StopCoinShake()
    {
        _coinShakeTimer?.Stop();
        if (_coinTransform != null)
            _coinTransform.X = 0;
    }

    // Shake pattern: decaying oscillation over ~10 frames (300ms)
    private static readonly double[] ShakeOffsets = { 3, -3, 2.5, -2.5, 2, -2, 1.5, -1.5, 1, 0 };

    private void CoinShake_Tick(object? sender, EventArgs e)
    {
        if (_coinTransform == null || _coinShakeFrame >= ShakeOffsets.Length)
        {
            StopCoinShake();
            return;
        }
        _coinTransform.X = ShakeOffsets[_coinShakeFrame++];
    }

    // Updates + Quit

    private UpdatesWindow? _updatesWindow;

    private void ButtonUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (_updatesWindow == null || !_updatesWindow.IsVisible)
        {
            _updatesWindow = new UpdatesWindow();
            if (Helpers.AppConfig.Is("topmost"))
                _updatesWindow.Topmost = true;
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_updatesWindow);
            _updatesWindow.Show();
        }
        else
        {
            _updatesWindow.Activate();
        }
    }

    private void ButtonQuit_Click(object? sender, RoutedEventArgs e)
    {
        // Mark shutdown so MainWindow's Closing handler allows the window to
        // dispose instead of hide. ShutdownRequested also sets this, but
        // setting it here is defensive in case Avalonia's request flow is
        // skipped on a particular platform.
        App.IsShuttingDown = true;

        // Clean shutdown
        App.Input?.Dispose();
        App.Wmi?.Dispose();

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // UI Helpers

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

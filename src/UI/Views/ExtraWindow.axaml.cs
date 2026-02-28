using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GHelper.Linux.Platform.Linux;
using GHelper.Linux.USB;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Extra settings window — keyboard backlight power zones,
/// display, power management, system info, advanced options.
/// Linux port of G-Helper's Extra form.
/// </summary>
public partial class ExtraWindow : Window
{
    private bool _suppressEvents = true;

    /// <summary>PID of the systemd-inhibit process for clamshell mode, or -1 if inactive.</summary>
    private static int _clamshellInhibitPid = -1;

    public ExtraWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _suppressEvents = true;
            InitKeyboardBacklight();
            InitKeyBindings();
            RefreshDisplay();
            RefreshOther();
            RefreshPower();
            RefreshSystemInfo();
            RefreshAdvanced();
            _suppressEvents = false;
        };
    }

    // ═══════════════════ KEYBOARD BACKLIGHT ═══════════════════

    private void InitKeyboardBacklight()
    {
        // Brightness (0-3)
        int brightness = App.Wmi?.GetKeyboardBrightness() ?? 3;
        sliderKbdBrightness.Value = brightness;
        labelKbdBrightness.Text = brightness.ToString();

        // Speed combo
        var speeds = Aura.GetSpeeds();
        comboKbdSpeed.Items.Clear();
        int selectedSpeedIdx = 0;
        int idx = 0;
        foreach (var kv in speeds)
        {
            comboKbdSpeed.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = (int)kv.Key });
            if (kv.Key == Aura.Speed) selectedSpeedIdx = idx;
            idx++;
        }
        comboKbdSpeed.SelectedIndex = selectedSpeedIdx;

        // Power zones
        bool hasZones = Helpers.AppConfig.IsBacklightZones();
        bool isLimited = Helpers.AppConfig.IsStrixLimitedRGB() || Helpers.AppConfig.IsARCNM();

        // Keyboard
        checkAwake.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake");
        checkBoot.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot");
        checkSleep.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep");
        checkShutdown.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown");
        checkBattery.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake");

        // Logo
        checkAwakeLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake_logo");
        checkBootLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot_logo");
        checkSleepLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep_logo");
        checkShutdownLogo.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown_logo");
        checkBatteryLogo.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake_logo");

        // Lightbar
        checkAwakeBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake_bar");
        checkBootBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot_bar");
        checkSleepBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep_bar");
        checkShutdownBar.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown_bar");
        checkBatteryBar.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake_bar");

        // Lid
        checkAwakeLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_awake_lid");
        checkBootLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_boot_lid");
        checkSleepLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_sleep_lid");
        checkShutdownLid.IsChecked = Helpers.AppConfig.IsNotFalse("keyboard_shutdown_lid");
        checkBatteryLid.IsChecked = Helpers.AppConfig.IsOnBattery("keyboard_awake_lid");

        // Visibility rules from original
        if (!hasZones || isLimited)
        {
            if (!Helpers.AppConfig.IsStrixLimitedRGB())
            {
                rowPowerBar.IsVisible = false;
                rowPowerKeyboard.FindControl<CheckBox>("checkBattery")!.IsVisible =
                    Helpers.AppConfig.IsBacklightZones();
            }

            rowPowerLid.IsVisible = false;
            rowPowerLogo.IsVisible = false;
        }

        if (Helpers.AppConfig.IsZ13())
        {
            rowPowerBar.IsVisible = false;
            rowPowerLid.IsVisible = false;
        }
    }

    private void SliderKbdBrightness_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int level = (int)e.NewValue;
        labelKbdBrightness.Text = level.ToString();
        Helpers.AppConfig.Set("keyboard_brightness", level);
        Aura.ApplyBrightness(level, "KbdSlider");
    }

    private void ComboKbdSpeed_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (comboKbdSpeed.SelectedItem is ComboBoxItem item && item.Tag is int speedVal)
        {
            Helpers.AppConfig.Set("aura_speed", speedVal);
            Aura.Speed = (AuraSpeed)speedVal;
            Task.Run(() => Aura.ApplyAura());
        }
    }

    private void CheckPower_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        // Save all power zone states
        Helpers.AppConfig.Set("keyboard_awake", (checkAwake.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot", (checkBoot.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep", (checkSleep.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown", (checkShutdown.IsChecked ?? false) ? 1 : 0);

        Helpers.AppConfig.Set("keyboard_awake_bar", (checkAwakeBar.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot_bar", (checkBootBar.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep_bar", (checkSleepBar.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown_bar", (checkShutdownBar.IsChecked ?? false) ? 1 : 0);

        Helpers.AppConfig.Set("keyboard_awake_lid", (checkAwakeLid.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot_lid", (checkBootLid.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep_lid", (checkSleepLid.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown_lid", (checkShutdownLid.IsChecked ?? false) ? 1 : 0);

        Helpers.AppConfig.Set("keyboard_awake_logo", (checkAwakeLogo.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_boot_logo", (checkBootLogo.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_sleep_logo", (checkSleepLogo.IsChecked ?? false) ? 1 : 0);
        Helpers.AppConfig.Set("keyboard_shutdown_logo", (checkShutdownLogo.IsChecked ?? false) ? 1 : 0);

        // Battery variants
        if (Helpers.AppConfig.IsBacklightZones())
        {
            Helpers.AppConfig.Set("keyboard_awake_bat", (checkBattery.IsChecked ?? false) ? 1 : 0);
            Helpers.AppConfig.Set("keyboard_awake_bar_bat", (checkBatteryBar.IsChecked ?? false) ? 1 : 0);
            Helpers.AppConfig.Set("keyboard_awake_lid_bat", (checkBatteryLid.IsChecked ?? false) ? 1 : 0);
            Helpers.AppConfig.Set("keyboard_awake_logo_bat", (checkBatteryLogo.IsChecked ?? false) ? 1 : 0);
        }

        // Apply via HID
        Task.Run(() => Aura.ApplyPower());
    }

    // ═══════════════════ KEY BINDINGS ═══════════════════

    /// <summary>Maps combo box controls to their config key names.</summary>
    private readonly Dictionary<ComboBox, string> _keyBindingCombos = new();

    private void InitKeyBindings()
    {
        _keyBindingCombos[comboKeyM4] = "m4";
        _keyBindingCombos[comboKeyFnF4] = "fnf4";
        _keyBindingCombos[comboKeyFnF5] = "fnf5";

        foreach (var (combo, bindingName) in _keyBindingCombos)
        {
            PopulateKeyBindingCombo(combo, bindingName);
        }
    }

    private void PopulateKeyBindingCombo(ComboBox combo, string bindingName)
    {
        combo.Items.Clear();

        string currentAction = App.GetKeyAction(bindingName);
        int selectedIdx = 0;
        int idx = 0;

        foreach (var (actionId, displayName) in App.AvailableKeyActions)
        {
            combo.Items.Add(new ComboBoxItem { Content = displayName, Tag = actionId });
            if (actionId == currentAction) selectedIdx = idx;
            idx++;
        }

        combo.SelectedIndex = selectedIdx;
    }

    private void ComboKeyBinding_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not ComboBox combo) return;
        if (!_keyBindingCombos.TryGetValue(combo, out string? bindingName)) return;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string actionId) return;

        Helpers.AppConfig.Set(bindingName, actionId);
        Helpers.Logger.WriteLine($"Key binding: {bindingName} → {actionId}");
    }

    // ═══════════════════ DISPLAY ═══════════════════

    private void RefreshDisplay()
    {
        var display = App.Display;
        if (display == null) return;

        int brightness = display.GetBrightness();
        if (brightness >= 0)
        {
            sliderBrightness.Value = brightness;
            labelBrightness.Text = $"{brightness}%";
        }

        bool overdrive = App.Wmi?.GetPanelOverdrive() ?? false;
        checkOverdrive.IsChecked = overdrive;

        sliderGamma.Value = 100; // Default gamma
    }

    private void SliderBrightness_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int percent = (int)e.NewValue;
        labelBrightness.Text = $"{percent}%";
        App.Display?.SetBrightness(percent);
    }

    private void CheckOverdrive_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = checkOverdrive.IsChecked ?? false;
        App.Wmi?.SetPanelOverdrive(enabled);
    }

    private void SliderGamma_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        float gamma = (float)(e.NewValue / 100.0);
        labelGamma.Text = $"{gamma:F2}";
        App.Display?.SetGamma(gamma, gamma, gamma);
    }

    // ═══════════════════ OTHER ═══════════════════

    private void RefreshOther()
    {
        // Boot sound
        int bootSound = Helpers.AppConfig.Get("boot_sound", 0);
        checkBootSound.IsChecked = bootSound == 1;

        // Per-key RGB (only visible if 4-zone is possible)
        checkPerKeyRGB.IsVisible = Helpers.AppConfig.IsPossible4ZoneRGB();
        checkPerKeyRGB.IsChecked = Helpers.AppConfig.Is("per_key_rgb");

        // Window always on top
        checkTopmost.IsChecked = Helpers.AppConfig.Is("topmost");
        if (Helpers.AppConfig.Is("topmost"))
            this.Topmost = true;

        // B&W tray icon
        checkBWIcon.IsChecked = Helpers.AppConfig.IsBWIcon();

        // Clamshell mode
        checkClamshell.IsChecked = Helpers.AppConfig.Is("toggle_clamshell_mode");

        // Camera
        checkCamera.IsChecked = LinuxSystemIntegration.IsCameraEnabled();

        // Touchpad (hide if not found)
        var touchpadState = LinuxSystemIntegration.IsTouchpadEnabled();
        if (touchpadState == null)
        {
            checkTouchpad.IsVisible = false;
        }
        else
        {
            checkTouchpad.IsVisible = true;
            checkTouchpad.IsChecked = touchpadState.Value;
        }

        // Touchscreen (hide if not found)
        var touchscreenState = LinuxSystemIntegration.IsTouchscreenEnabled();
        if (touchscreenState == null)
        {
            checkTouchscreen.IsVisible = false;
        }
        else
        {
            checkTouchscreen.IsVisible = true;
            checkTouchscreen.IsChecked = touchscreenState.Value;
        }
    }

    private void CheckBootSound_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        int val = (checkBootSound.IsChecked ?? false) ? 1 : 0;
        Helpers.AppConfig.Set("boot_sound", val);

        // Try to set via sysfs (asus-nb-wmi or asus-armoury firmware-attributes)
        try
        {
            var path = Platform.Linux.SysfsHelper.ResolveAttrPath("boot_sound");
            if (path != null)
                Platform.Linux.SysfsHelper.WriteAttribute(path, val.ToString());
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Boot sound write failed: {ex.Message}");
        }

        Helpers.Logger.WriteLine($"Boot sound → {val}");
    }

    private void CheckPerKeyRGB_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        Helpers.AppConfig.Set("per_key_rgb", (checkPerKeyRGB.IsChecked ?? false) ? 1 : 0);
        Helpers.Logger.WriteLine($"Per-key RGB → {checkPerKeyRGB.IsChecked}");
        // Re-apply aura so the mode change takes effect immediately
        Task.Run(() => Aura.ApplyAura());
    }

    private void CheckTopmost_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = checkTopmost.IsChecked ?? false;
        Helpers.AppConfig.Set("topmost", on ? 1 : 0);

        // Apply to ALL open windows, not just this one
        App.SetTopmostAll(on);
    }

    private void CheckBWIcon_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        Helpers.AppConfig.Set("bw_icon", (checkBWIcon.IsChecked ?? false) ? 1 : 0);
        Helpers.Logger.WriteLine($"B&W tray icon → {checkBWIcon.IsChecked}");
        // Update the tray icon immediately
        App.UpdateTrayIcon();
    }

    private void CheckClamshell_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = checkClamshell.IsChecked ?? false;
        Helpers.AppConfig.Set("toggle_clamshell_mode", on ? 1 : 0);

        // Toggle lid switch handling via systemd-inhibit (runs as current user, no root needed)
        try
        {
            if (on)
            {
                StartClamshellInhibit();
            }
            else
            {
                StopClamshellInhibit();
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Clamshell mode toggle failed: {ex.Message}");
        }
    }

    /// <summary>Start a systemd-inhibit process that prevents lid-close suspend.</summary>
    public static void StartClamshellInhibit()
    {
        StopClamshellInhibit(); // Kill any existing one first

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemd-inhibit",
                Arguments = "--what=handle-lid-switch --who=\"G-Helper\" --why=\"Clamshell mode\" sleep infinity",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _clamshellInhibitPid = proc.Id;
                Helpers.Logger.WriteLine($"Clamshell mode ON (inhibit PID {_clamshellInhibitPid})");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Failed to start systemd-inhibit: {ex.Message}");
        }
    }

    /// <summary>Kill the systemd-inhibit process, restoring normal lid behavior.</summary>
    public static void StopClamshellInhibit()
    {
        if (_clamshellInhibitPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(_clamshellInhibitPid);
                proc.Kill();
                proc.WaitForExit(2000);
                Helpers.Logger.WriteLine($"Clamshell mode OFF (killed PID {_clamshellInhibitPid})");
            }
            catch
            {
                // Process already exited
            }

            _clamshellInhibitPid = -1;
        }
    }

    private void CheckCamera_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = checkCamera.IsChecked ?? true;
        LinuxSystemIntegration.SetCameraEnabled(enabled);
        App.System?.ShowNotification("Camera",
            enabled ? "Enabled" : "Disabled",
            enabled ? "camera-on" : "camera-off");
    }

    private void CheckTouchpad_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = checkTouchpad.IsChecked ?? true;
        LinuxSystemIntegration.SetTouchpadEnabled(enabled);
        App.System?.ShowNotification("Touchpad",
            enabled ? "Enabled" : "Disabled",
            enabled ? "input-touchpad-on" : "input-touchpad-off");
    }

    private void CheckTouchscreen_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = checkTouchscreen.IsChecked ?? true;
        LinuxSystemIntegration.SetTouchscreenEnabled(enabled);
        App.System?.ShowNotification("Touchscreen",
            enabled ? "Enabled" : "Disabled",
            "preferences-desktop-touchscreen");
    }

    // ═══════════════════ POWER MANAGEMENT ═══════════════════

    private void RefreshPower()
    {
        var power = App.Power;
        if (power == null) return;

        // Platform profile
        string profile = power.GetPlatformProfile();
        for (int i = 0; i < comboPlatformProfile.Items.Count; i++)
        {
            if (comboPlatformProfile.Items[i] is ComboBoxItem item &&
                item.Content?.ToString() == profile)
            {
                comboPlatformProfile.SelectedIndex = i;
                break;
            }
        }

        // ASPM
        string aspm = power.GetAspmPolicy();
        for (int i = 0; i < comboAspm.Items.Count; i++)
        {
            if (comboAspm.Items[i] is ComboBoxItem item &&
                item.Content?.ToString() == aspm)
            {
                comboAspm.SelectedIndex = i;
                break;
            }
        }

        // Battery health
        int health = power.GetBatteryHealth();
        if (health >= 0)
            labelBatteryHealth.Text = $"Battery health: {health}%";

        int drain = power.GetBatteryDrainRate();
        if (drain != 0)
            labelPowerDraw.Text = drain > 0
                ? $"Power draw: {drain} mW (discharging)"
                : $"Power draw: {-drain} mW (charging)";
    }

    private void ComboPlatformProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (comboPlatformProfile.SelectedItem is ComboBoxItem item && item.Content is string profile)
        {
            App.Power?.SetPlatformProfile(profile);
            Helpers.Logger.WriteLine($"Platform profile → {profile}");
        }
    }

    private void ComboAspm_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (comboAspm.SelectedItem is ComboBoxItem item && item.Content is string policy)
        {
            App.Power?.SetAspmPolicy(policy);
            Helpers.Logger.WriteLine($"ASPM policy → {policy}");
        }
    }

    // ═══════════════════ SYSTEM INFO ═══════════════════

    private void RefreshSystemInfo()
    {
        var sys = App.System;
        if (sys == null) return;

        labelModel.Text = $"Model: {sys.GetModelName()}";
        labelBios.Text = $"BIOS: {sys.GetBiosVersion()}";
        labelKernel.Text = $"Kernel: {sys.GetKernelVersion()}";

        bool wmiLoaded = sys.IsAsusWmiLoaded();
        labelAsusWmi.Text = $"asus-wmi: {(wmiLoaded ? "\u2713 Loaded" : "\u2717 Not loaded")}";

        // Feature detection
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

        labelFeatures.Text = features.Count > 0
            ? $"Features: {string.Join(", ", features)}"
            : "No ASUS-specific features detected";

        // Kernel version check
        var kernelVer = sys.GetKernelVersionParsed();
        if (kernelVer < new Version(6, 2))
        {
            labelFeatures.Text += "\n\u26A0 Kernel 6.2+ recommended for full feature support";
        }
    }

    // ═══════════════════ ADVANCED ═══════════════════

    private void RefreshAdvanced()
    {
        checkAutoApplyPower.IsChecked = Helpers.AppConfig.IsMode("auto_apply_power");
        checkScreenAuto.IsChecked = Helpers.AppConfig.Is("screen_auto");

        // CPU cores
        int total = LinuxSystemIntegration.GetCpuCount();
        int online = LinuxSystemIntegration.GetOnlineCpuCount();

        if (total > 1)
        {
            panelCpuCores.IsVisible = true;
            sliderCpuCores.Maximum = total;
            sliderCpuCores.Value = online;
            labelCpuCores.Text = $"{online}/{total}";
            labelCpuCoresInfo.Text = $"{online} of {total} threads active";
        }
        else
        {
            panelCpuCores.IsVisible = false;
        }
    }

    private void CheckAutoApplyPower_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = checkAutoApplyPower.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_power", enabled ? 1 : 0);
    }

    private void CheckScreenAuto_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = checkScreenAuto.IsChecked ?? false;
        Helpers.AppConfig.Set("screen_auto", enabled ? 1 : 0);
        Helpers.Logger.WriteLine($"Screen auto refresh → {enabled}");
    }

    private void SliderCpuCores_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int target = (int)e.NewValue;
        int total = LinuxSystemIntegration.GetCpuCount();
        labelCpuCores.Text = $"{target}/{total}";
        labelCpuCoresInfo.Text = $"{target} of {total} threads active";

        // Apply in background to avoid UI stall
        Task.Run(() => LinuxSystemIntegration.SetOnlineCpuCount(target));
    }

    private void ButtonOpenLog_Click(object? sender, RoutedEventArgs e)
    {
        // Logger is stdout-only; open a terminal showing the app's output
        try
        {
            // Try to find the config dir for any saved logs
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "ghelper");
            if (Directory.Exists(configDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = configDir,
                    UseShellExecute = false,
                });
            }
            else
            {
                Helpers.Logger.WriteLine("Logs are written to stdout — run the app from a terminal to see output");
                App.System?.ShowNotification("G-Helper", "Logs are written to stdout — run from terminal to see output", "dialog-information");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("Failed to open config dir", ex);
        }
    }
}

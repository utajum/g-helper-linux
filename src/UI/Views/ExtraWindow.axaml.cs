using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GHelper.Linux.USB;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Extra settings window — key bindings, keyboard backlight power zones,
/// display, power management, system info, advanced options.
/// Linux port of G-Helper's Extra form.
/// </summary>
public partial class ExtraWindow : Window
{
    private bool _suppressEvents = true;

    // Key binding action definitions
    private static readonly Dictionary<string, string> BaseActions = new()
    {
        { "", "--------------" },
        { "mute", "Volume Mute" },
        { "screenshot", "Print Screen" },
        { "play", "Play/Pause" },
        { "aura", "Toggle Aura" },
        { "performance", "Performance Mode" },
        { "screen", "Toggle Screen" },
        { "lock", "Lock Screen" },
        { "miniled", "Toggle MiniLED" },
        { "brightness_down", "Brightness Down" },
        { "brightness_up", "Brightness Up" },
        { "micmute", "Mute Mic" },
        { "ghelper", "Open G-Helper" },
        { "custom", "Custom" },
    };

    public ExtraWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _suppressEvents = true;
            InitKeyBindings();
            InitKeyboardBacklight();
            RefreshDisplay();
            RefreshOther();
            RefreshPower();
            RefreshSystemInfo();
            RefreshAdvanced();
            _suppressEvents = false;
        };
    }

    // ═══════════════════ KEY BINDINGS ═══════════════════

    private void InitKeyBindings()
    {
        // Model-specific visibility
        if (Helpers.AppConfig.IsARCNM())
        {
            labelM3.Text = "FN+F6";
            rowM1.IsVisible = false;
            rowM2.IsVisible = false;
            rowM4.IsVisible = false;
            rowFnF4.IsVisible = false;
        }

        if (Helpers.AppConfig.NoMKeys())
        {
            labelM1.Text = "FN+F2";
            labelM2.Text = "FN+F3";
            labelM3.Text = "FN+F4";
            rowM4.IsVisible = Helpers.AppConfig.IsM4Button();
            rowFnF4.IsVisible = false;
        }

        if (Helpers.AppConfig.IsVivoZenPro())
        {
            rowM1.IsVisible = false;
            rowM2.IsVisible = false;
            rowM3.IsVisible = false;
            rowFnF4.IsVisible = false;
            labelM4.Text = "FN+F12";
        }

        if (Helpers.AppConfig.MediaKeys())
            rowFnF4.IsVisible = false;

        if (Helpers.AppConfig.IsTUF())
            rowFnE.IsVisible = true;

        if (Helpers.AppConfig.IsNoFNV())
            rowFnV.IsVisible = false;

        if (Helpers.AppConfig.IsStrix())
            labelM4.Text = "M5/ROG";

        // Set up each key binding combo
        SetKeyCombo(comboM1, textM1, "m1", "Volume Down");
        SetKeyCombo(comboM2, textM2, "m2", "Volume Up");
        SetKeyCombo(comboM3, textM3, "m3", "Mute Mic");
        SetKeyCombo(comboM4, textM4, "m4", "Open G-Helper");
        SetKeyCombo(comboFnF4, textFnF4, "fnf4", "Toggle Aura");
        SetKeyCombo(comboFnC, textFnC, "fnc", "Toggle FnLock");
        SetKeyCombo(comboFnV, textFnV, "fnv", "Visual Mode");
        SetKeyCombo(comboFnE, textFnE, "fne", "Calculator");
    }

    private void SetKeyCombo(ComboBox combo, TextBox textBox, string name, string defaultLabel)
    {
        var actions = new Dictionary<string, string>(BaseActions);

        // Replace the empty entry with the default action label
        actions[""] = defaultLabel;

        // Remove duplicates (if default matches an existing action)
        switch (name)
        {
            case "m3": actions.Remove("micmute"); break;
            case "m4": actions.Remove("ghelper"); break;
            case "fnf4": actions.Remove("aura"); break;
            case "fnc": actions.Remove("fnlock"); break;
        }

        combo.Items.Clear();
        string savedAction = Helpers.AppConfig.GetString(name) ?? "";
        int selectedIdx = 0;
        int idx = 0;

        foreach (var kv in actions)
        {
            combo.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
            if (kv.Key == savedAction) selectedIdx = idx;
            idx++;
        }

        combo.SelectedIndex = selectedIdx;

        combo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string actionKey)
            {
                Helpers.AppConfig.Set(name, actionKey);
                Helpers.Logger.WriteLine($"Key binding {name} → {actionKey}");
            }
        };

        // Custom command text
        textBox.Text = Helpers.AppConfig.GetString(name + "_custom") ?? "";
        textBox.TextChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            Helpers.AppConfig.Set(name + "_custom", textBox.Text ?? "");
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

        // Backlight timeouts
        int timeout = Helpers.AppConfig.Get("keyboard_timeout", 60);
        sliderBacklightTimeout.Value = timeout;
        labelBacklightTimeout.Text = timeout == 0 ? "Off" : $"{timeout}s";

        int timeoutAC = Helpers.AppConfig.Get("keyboard_ac_timeout", 0);
        sliderBacklightTimeoutAC.Value = timeoutAC;
        labelBacklightTimeoutAC.Text = timeoutAC == 0 ? "Off" : $"{timeoutAC}s";

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

    private void SliderBacklightTimeout_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int val = (int)e.NewValue;
        labelBacklightTimeout.Text = val == 0 ? "Off" : $"{val}s";
        Helpers.AppConfig.Set("keyboard_timeout", val);
    }

    private void SliderBacklightTimeoutAC_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int val = (int)e.NewValue;
        labelBacklightTimeoutAC.Text = val == 0 ? "Off" : $"{val}s";
        Helpers.AppConfig.Set("keyboard_ac_timeout", val);
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
    }

    private void CheckBootSound_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        int val = (checkBootSound.IsChecked ?? false) ? 1 : 0;
        Helpers.AppConfig.Set("boot_sound", val);

        // Try to set via asus-wmi sysfs (boot_sound attribute)
        try
        {
            var path = Path.Combine("/sys/devices/platform/asus-nb-wmi", "boot_sound");
            if (File.Exists(path))
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
    }

    private void CheckTopmost_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = checkTopmost.IsChecked ?? false;
        Helpers.AppConfig.Set("topmost", on ? 1 : 0);
        this.Topmost = on;
    }

    private void CheckBWIcon_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        Helpers.AppConfig.Set("bw_icon", (checkBWIcon.IsChecked ?? false) ? 1 : 0);
        Helpers.Logger.WriteLine($"B&W tray icon → {checkBWIcon.IsChecked}");
    }

    private void CheckClamshell_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = checkClamshell.IsChecked ?? false;
        Helpers.AppConfig.Set("toggle_clamshell_mode", on ? 1 : 0);

        // Toggle lid switch handling via systemd-logind
        try
        {
            if (on)
            {
                // Inhibit lid switch close action
                Platform.Linux.SysfsHelper.WriteAttribute(
                    "/etc/systemd/logind.conf.d/ghelper-clamshell.conf",
                    "[Login]\nHandleLidSwitch=ignore\nHandleLidSwitchExternalPower=ignore\n");
            }
            else
            {
                var confPath = "/etc/systemd/logind.conf.d/ghelper-clamshell.conf";
                if (File.Exists(confPath))
                    File.Delete(confPath);
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"Clamshell mode toggle: {ex.Message} (may need root)");
        }
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
    }

    private void ButtonOpenLog_Click(object? sender, RoutedEventArgs e)
    {
        string logFile = Helpers.Logger.LogFile;
        if (File.Exists(logFile))
        {
            try
            {
                Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"Failed to open log file: {ex.Message}");
                // Fallback: try xdg-open
                try
                {
                    Process.Start("xdg-open", logFile);
                }
                catch { }
            }
        }
        else
        {
            Helpers.Logger.WriteLine("Log file does not exist yet");
        }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Extra settings window — display, power management, system info, advanced options.
/// Linux port of G-Helper's Extra form.
/// </summary>
public partial class ExtraWindow : Window
{
    public ExtraWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshDisplay();
        RefreshPower();
        RefreshSystemInfo();
        RefreshAdvanced();
    }

    // ── Display ──

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
        int percent = (int)e.NewValue;
        labelBrightness.Text = $"{percent}%";
        App.Display?.SetBrightness(percent);
    }

    private void CheckOverdrive_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkOverdrive.IsChecked ?? false;
        App.Wmi?.SetPanelOverdrive(enabled);
    }

    private void SliderGamma_ValueChanged(object? sender,
        Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        float gamma = (float)(e.NewValue / 100.0);
        labelGamma.Text = $"{gamma:F2}";
        App.Display?.SetGamma(gamma, gamma, gamma);
    }

    // ── Power ──

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
        if (comboPlatformProfile.SelectedItem is ComboBoxItem item && item.Content is string profile)
        {
            App.Power?.SetPlatformProfile(profile);
            Helpers.Logger.WriteLine($"Platform profile → {profile}");
        }
    }

    private void ComboAspm_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (comboAspm.SelectedItem is ComboBoxItem item && item.Content is string policy)
        {
            App.Power?.SetAspmPolicy(policy);
            Helpers.Logger.WriteLine($"ASPM policy → {policy}");
        }
    }

    // ── System Info ──

    private void RefreshSystemInfo()
    {
        var sys = App.System;
        if (sys == null) return;

        labelModel.Text = $"Model: {sys.GetModelName()}";
        labelBios.Text = $"BIOS: {sys.GetBiosVersion()}";
        labelKernel.Text = $"Kernel: {sys.GetKernelVersion()}";

        bool wmiLoaded = sys.IsAsusWmiLoaded();
        labelAsusWmi.Text = $"asus-wmi: {(wmiLoaded ? "✓ Loaded" : "✗ Not loaded")}";

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
            labelFeatures.Text += "\n⚠ Kernel 6.2+ recommended for full feature support";
        }
    }

    // ── Advanced ──

    private void RefreshAdvanced()
    {
        checkAutoApplyPower.IsChecked = Helpers.AppConfig.IsMode("auto_apply_power");
        checkScreenAuto.IsChecked = Helpers.AppConfig.Is("screen_auto");
    }

    private void CheckAutoApplyPower_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkAutoApplyPower.IsChecked ?? false;
        Helpers.AppConfig.SetMode("auto_apply_power", enabled ? 1 : 0);
    }

    private void CheckScreenAuto_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = checkScreenAuto.IsChecked ?? false;
        Helpers.AppConfig.Set("screen_auto", enabled ? 1 : 0);
    }

    private void ButtonOpenLog_Click(object? sender, RoutedEventArgs e)
    {
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "ghelper-linux", "log.txt");

        if (System.IO.File.Exists(logPath))
        {
            // Open with system text editor
            Platform.Linux.SysfsHelper.RunCommand("xdg-open", logPath);
        }
    }
}

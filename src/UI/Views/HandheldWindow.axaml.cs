using System;
using System;
using System.Collections.Generic;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GHelper.Linux.Ally;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// ROG Ally controller binding configurator. Avalonia port of Windows
/// g-helper Handheld.cs (~381 LoC). Lets the user:
///   - Bind primary/secondary actions per physical button (M1/M2/ABXY/D-Pad/
///     LT/RT/LB/RB/LS/RS/View/Menu)
///   - Configure turbo (autorepeat hold ms) per binding slot
///   - Set stick / trigger deadzones (min/max %) and vibration intensity
///   - Disable the Xbox controller endpoint entirely
///   - Reset all of the above to defaults
///
/// All persistence goes through AppConfig with verbatim Windows key names
/// (bind_a, bind2_dl, turbo_lt, turbo2_rs, ls_min, ls_max, vibra,
/// controller_disabled) so config files stay portable across the OS divide.
///
/// Inert on non-Ally hardware: the window can still be opened (e.g. by a
/// developer testing UI bindings) but every HID write short-circuits in
/// AllyControl on AppConfig.IsAlly() == false.
/// </summary>
public partial class HandheldWindow : Window
{
    /// <summary>
    /// Lightweight DTO for the binding combobox. Avalonia's ComboBox doesn't
    /// natively support separators / disabled rows, so we render group
    /// headers as <see cref="IsHeader"/>=true items and filter selection.
    /// <see cref="Display"/> is the user-facing string already resolved
    /// through <c>Labels.Get</c> at the time of population, so it stays
    /// stable for the lifetime of the dropdown.
    /// </summary>
    private sealed class BindingItem
    {
        public string Code { get; }
        public string Display { get; }
        public bool IsHeader { get; }
        public BindingItem(string code, string display, bool isHeader = false)
        {
            Code = code;
            Display = display;
            IsHeader = isHeader;
        }
        public override string ToString() => IsHeader ? "── " + Display + " ──" : Display;
    }

    private sealed class TurboItem
    {
        public int Ms { get; }
        public string Display { get; }
        public TurboItem(int ms, string display) { Ms = ms; Display = display; }
        public override string ToString() => Display;
    }

    // Active button binding key (e.g. "m1", "a", "lt") and the button widget.
    private string? _activeBinding;
    private Button? _activeButton;

    // Suppress the SelectionChanged event re-entry while we programmatically
    // populate the active-binding combos.
    private bool _suppressBindingEvents;

    public HandheldWindow()
    {
        InitializeComponent();

        ApplyLabels();

        // 18 binding buttons → "logical key" used in AppConfig (matches Windows
        // Handheld.cs identifiers verbatim).
        // M1/M2 + ABXY are universal hardware names - kept as Window-bound
        // literals via XAML defaults, no Labels.Get needed.
        WireButton(buttonM1, "m1");
        WireButton(buttonM2, "m2");
        WireButton(buttonA, "a");
        WireButton(buttonB, "b");
        WireButton(buttonX, "x");
        WireButton(buttonY, "y");
        WireButton(buttonDPU, "du");
        WireButton(buttonDPD, "dd");
        WireButton(buttonDPL, "dl");
        WireButton(buttonDPR, "dr");
        WireButton(buttonLT, "lt");
        WireButton(buttonRT, "rt");
        WireButton(buttonLB, "lb");
        WireButton(buttonRB, "rb");
        // NB: Windows g-helper has a long-standing bug where the L-Stick
        // button stores its binding under "bind_ll" but BindZone(StickClick)
        // reads "bind_ls" - so the L-Stick UI is functionally a no-op on
        // Windows. We use "ls" so reads and writes line up, accepting the
        // small cross-OS config-sync divergence for the L-Stick slot only.
        // (Windows R-Stick already works because it uses "rs" both ways.)
        WireButton(buttonLS, "ls");
        WireButton(buttonRS, "rs");
        WireButton(buttonView, "vb");
        WireButton(buttonMenu, "mb");

        PopulateBindingComboItems(comboPrimary);
        PopulateBindingComboItems(comboSecondary);
        PopulateTurboCombo(comboTurboPrimary);
        PopulateTurboCombo(comboTurboSecondary);

        // Initial state of the deadzone sliders + checkbox.
        InitDeadzones();

        // Stick calibration defaults - values from asusctl example
        // (rog-platform/examples/ally-gamepad-calibration.rs); these are
        // verified against an Ally 2023 unit. Persisted via cal_ls_* keys.
        InitStickCalibration();

        checkController.IsChecked = AppConfig.Is("controller_disabled");
    }

    /// <summary>
    /// Apply localized text to every static control. Called once from the
    /// constructor; if we ever support live language switching for this
    /// window we can call this again from a Labels.LanguageChanged hook.
    /// </summary>
    private void ApplyLabels()
    {
        Title = Labels.Get("ally_window_title");

        // Button labels for the descriptive ones (4 D-Pad + 4 trigger/bumper +
        // 2 stick-click + View/Menu). Hardware-letter buttons stay literal.
        buttonDPU.Content = Labels.Get("btn_dpad_up");
        buttonDPD.Content = Labels.Get("btn_dpad_down");
        buttonDPL.Content = Labels.Get("btn_dpad_left");
        buttonDPR.Content = Labels.Get("btn_dpad_right");
        buttonLT.Content = Labels.Get("btn_l_trigger");
        buttonRT.Content = Labels.Get("btn_r_trigger");
        buttonLB.Content = Labels.Get("btn_l_bumper");
        buttonRB.Content = Labels.Get("btn_r_bumper");
        buttonLS.Content = Labels.Get("btn_l_stick_click");
        buttonRS.Content = Labels.Get("btn_r_stick_click");
        buttonView.Content = Labels.Get("btn_view");
        buttonMenu.Content = Labels.Get("btn_menu");

        // Sectional headers and chrome.
        labelButtonBindingsHeader.Text = Labels.Get("ally_button_bindings");
        labelClickToBind.Text = Labels.Get("ally_click_to_bind");
        labelPrimary.Text = Labels.Get("ally_primary");
        labelSecondary.Text = Labels.Get("ally_secondary");
        labelTurboPrimary.Text = Labels.Get("ally_turbo_primary");
        labelTurboSecondary.Text = Labels.Get("ally_turbo_secondary");
        labelDeadzonesHeader.Text = Labels.Get("ally_deadzones_vibration");
        labelLSRow.Text = Labels.Get("ally_l_stick");
        labelRSRow.Text = Labels.Get("ally_r_stick");
        labelLTRow.Text = Labels.Get("ally_l_trigger_short");
        labelRTRow.Text = Labels.Get("ally_r_trigger_short");
        labelVibraRow.Text = Labels.Get("ally_vibration");
        labelControllerHeader.Text = Labels.Get("ally_controller");
        checkController.Content = Labels.Get("ally_disable_controller");
        buttonReset.Content = Labels.Get("ally_reset_defaults");

        // Stick calibration panel labels.
        labelStickCalHeader.Text = Labels.Get("ally_stickcal_header");
        labelStickCalHint.Text = Labels.Get("ally_stickcal_hint");
        labelStickCalReset.Text = Labels.Get("ally_stickcal_reset");
        labelStickCalAdvanced.Text = Labels.Get("ally_stickcal_advanced");
        labelCalAxisX.Text = Labels.Get("ally_stickcal_axis_x");
        labelCalAxisY.Text = Labels.Get("ally_stickcal_axis_y");
        labelCalStable.Text = Labels.Get("ally_stickcal_stable");
        labelCalMin.Text = Labels.Get("ally_stickcal_min");
        labelCalMax.Text = Labels.Get("ally_stickcal_max");
        labelStickCalApply.Text = Labels.Get("ally_stickcal_apply");
        labelStickCalAdvancedR.Text = Labels.Get("ally_stickcal_advanced_r");
        labelStickCalApplyR.Text = Labels.Get("ally_stickcal_apply_r");
        labelTriggerCalAdvanced.Text = Labels.Get("ally_triggercal_advanced");
        labelTriggerCalApply.Text = Labels.Get("ally_triggercal_apply");
        labelStickCalCapture.Text = Labels.Get("ally_stickcal_capture");

        // Default header for the active-binding panel (overwritten when a
        // button is selected).
        labelBindingHeader.Text = Labels.Format("ally_binding_active", "M1");
    }

    /// <summary>
    /// Hook a binding button: clicking it makes it the "active" target whose
    /// primary/secondary/turbo combos drive AppConfig writes. Also paints the
    /// button border to visually indicate "binding set" vs "binding empty".
    /// </summary>
    private void WireButton(Button button, string key)
    {
        button.Click += (_, _) => SelectBinding(button, key);
        UpdateButtonVisual(button, key);
    }

    private void SelectBinding(Button button, string key)
    {
        _activeButton = button;
        _activeBinding = key;
        labelBindingHeader.Text = Labels.Format("ally_binding_active",
            button.Content?.ToString() ?? string.Empty);
        panelBinding.IsVisible = true;

        _suppressBindingEvents = true;
        try
        {
            SetComboToCode(comboPrimary, AppConfig.GetString("bind_" + key, "") ?? "");
            SetComboToCode(comboSecondary, AppConfig.GetString("bind2_" + key, "") ?? "");
            SetComboToTurbo(comboTurboPrimary, AppConfig.Get("turbo_" + key, 0));
            SetComboToTurbo(comboTurboSecondary, AppConfig.Get("turbo2_" + key, 0));
        }
        finally
        {
            _suppressBindingEvents = false;
        }
    }

    /// <summary>Visually mark the button with its current binding state.</summary>
    private void UpdateButtonVisual(Button button, string key)
    {
        bool hasBinding = !string.IsNullOrEmpty(AppConfig.GetString("bind_" + key, ""))
                       || !string.IsNullOrEmpty(AppConfig.GetString("bind2_" + key, ""));
        button.Tag = hasBinding ? "set" : "empty";
        // (Optional - can be hooked into XAML style triggers via Tag selector.)
    }

    // ---------------------------------------------------------------------
    // Combo population.
    // ---------------------------------------------------------------------

    private void PopulateBindingComboItems(ComboBox combo)
    {
        var items = new List<BindingItem>();
        foreach (var (groupLabelKey, entries) in BindingGroups.Groups)
        {
            if (!string.IsNullOrEmpty(groupLabelKey))
                items.Add(new BindingItem("", Labels.Get(groupLabelKey), isHeader: true));
            foreach (var entry in entries)
            {
                // IsLiteral entries already hold their final display text;
                // others carry an i18n key resolved via Labels.Get.
                string display = entry.IsLiteral ? entry.Display : Labels.Get(entry.Display);
                items.Add(new BindingItem(entry.Code, display));
            }
        }
        combo.ItemsSource = items;
    }

    private static void PopulateTurboCombo(ComboBox combo)
    {
        var items = new List<TurboItem>
        {
            new(0,   Labels.Get("ally_turbo_off")),
            new(50,  "50ms"),
            new(100, "100ms"),
            new(150, "150ms"),
            new(200, "200ms"),
            new(250, "250ms"),
            new(300, "300ms"),
            new(400, "400ms"),
            new(500, "500ms"),
        };
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
    }

    private static void SetComboToCode(ComboBox combo, string code)
    {
        if (combo.ItemsSource is not IEnumerable<BindingItem> items)
            return;
        int idx = 0;
        foreach (var it in items)
        {
            if (!it.IsHeader && it.Code == code)
            {
                combo.SelectedIndex = idx;
                return;
            }
            idx++;
        }
        combo.SelectedIndex = 0;
    }

    private static void SetComboToTurbo(ComboBox combo, int ms)
    {
        if (combo.ItemsSource is not IEnumerable<TurboItem> items)
            return;
        int idx = 0;
        foreach (var it in items)
        {
            if (it.Ms == ms)
            { combo.SelectedIndex = idx; return; }
            idx++;
        }
        combo.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------------
    // Combo handlers - both primary/secondary feed the same writer.
    // ---------------------------------------------------------------------

    private void ComboBinding_Primary_Changed(object? sender, SelectionChangedEventArgs e)
        => OnBindingComboChanged(comboPrimary, slot2: false);

    private void ComboBinding_Secondary_Changed(object? sender, SelectionChangedEventArgs e)
        => OnBindingComboChanged(comboSecondary, slot2: true);

    private void OnBindingComboChanged(ComboBox combo, bool slot2)
    {
        if (_suppressBindingEvents || _activeBinding == null)
            return;

        if (combo.SelectedItem is not BindingItem item)
            return;

        // If user picked a header row by accident, snap to the next non-header.
        if (item.IsHeader)
        {
            int next = combo.SelectedIndex + 1;
            if (combo.ItemsSource is IList<BindingItem> list && next < list.Count)
                combo.SelectedIndex = next;
            return;
        }

        string key = (slot2 ? "bind2_" : "bind_") + _activeBinding;
        if (string.IsNullOrEmpty(item.Code))
            AppConfig.Remove(key);
        else
            AppConfig.Set(key, item.Code);

        if (_activeButton != null)
            UpdateButtonVisual(_activeButton, _activeBinding);
        AllyControl.ApplyMode();   // re-flash every zone
    }

    private void ComboTurbo_Primary_Changed(object? sender, SelectionChangedEventArgs e)
        => OnTurboComboChanged(comboTurboPrimary, slot2: false);

    private void ComboTurbo_Secondary_Changed(object? sender, SelectionChangedEventArgs e)
        => OnTurboComboChanged(comboTurboSecondary, slot2: true);

    private void OnTurboComboChanged(ComboBox combo, bool slot2)
    {
        if (_suppressBindingEvents || _activeBinding == null)
            return;
        if (combo.SelectedItem is not TurboItem t)
            return;

        string key = (slot2 ? "turbo2_" : "turbo_") + _activeBinding;
        if (t.Ms <= 0)
            AppConfig.Remove(key);
        else
            AppConfig.Set(key, t.Ms);

        AllyControl.ApplyMode();
    }

    // ---------------------------------------------------------------------
    // Deadzone sliders - debounced via Avalonia's built-in continuous events.
    // Every value-change writes AppConfig + sends one HID deadzone packet,
    // matching Windows behavior.
    // ---------------------------------------------------------------------

    private void InitDeadzones()
    {
        trackLSMin.Value = AppConfig.Get("ls_min", 0);
        trackLSMax.Value = AppConfig.Get("ls_max", 100);
        trackRSMin.Value = AppConfig.Get("rs_min", 0);
        trackRSMax.Value = AppConfig.Get("rs_max", 100);
        trackLTMin.Value = AppConfig.Get("lt_min", 0);
        trackLTMax.Value = AppConfig.Get("lt_max", 100);
        trackRTMin.Value = AppConfig.Get("rt_min", 0);
        trackRTMax.Value = AppConfig.Get("rt_max", 100);
        trackVibra.Value = AppConfig.Get("vibra", 100);

        RefreshDeadzoneLabels();
    }

    private void RefreshDeadzoneLabels()
    {
        labelLS.Text = $"{(int)trackLSMin.Value}-{(int)trackLSMax.Value}%";
        labelRS.Text = $"{(int)trackRSMin.Value}-{(int)trackRSMax.Value}%";
        labelLT.Text = $"{(int)trackLTMin.Value}-{(int)trackLTMax.Value}%";
        labelRT.Text = $"{(int)trackRTMin.Value}-{(int)trackRTMax.Value}%";
        labelVibra.Text = $"{(int)trackVibra.Value}%";
    }

    private void Track_LS_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Set("ls_min", (int)trackLSMin.Value);
        AppConfig.Set("ls_max", (int)trackLSMax.Value);
        RefreshDeadzoneLabels();
        AllyControl.SetDeadzones();
    }

    private void Track_RS_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Set("rs_min", (int)trackRSMin.Value);
        AppConfig.Set("rs_max", (int)trackRSMax.Value);
        RefreshDeadzoneLabels();
        AllyControl.SetDeadzones();
    }

    private void Track_LT_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Set("lt_min", (int)trackLTMin.Value);
        AppConfig.Set("lt_max", (int)trackLTMax.Value);
        RefreshDeadzoneLabels();
        AllyControl.SetDeadzones();
    }

    private void Track_RT_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Set("rt_min", (int)trackRTMin.Value);
        AppConfig.Set("rt_max", (int)trackRTMax.Value);
        RefreshDeadzoneLabels();
        AllyControl.SetDeadzones();
    }

    private void Track_Vibra_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Set("vibra", (int)trackVibra.Value);
        RefreshDeadzoneLabels();
        AllyControl.SetDeadzones();
    }

    // ---------------------------------------------------------------------
    // Footer controls.
    // ---------------------------------------------------------------------

    private void CheckController_Changed(object? sender, RoutedEventArgs e)
    {
        bool disabled = checkController.IsChecked == true;
        AppConfig.Set("controller_disabled", disabled ? 1 : 0);
        AllyControl.DisableXBoxController(disabled);
    }

    private void ButtonReset_Click(object? sender, RoutedEventArgs e)
    {
        // Wipe every stick / trigger / vibration knob.
        foreach (var key in new[] { "ls_min", "ls_max", "rs_min", "rs_max",
                                    "lt_min", "lt_max", "rt_min", "rt_max", "vibra" })
            AppConfig.Remove(key);

        // Wipe every binding + turbo (covers all 18 logical buttons × 2 slots
        // × 2 storage families). Also clear the legacy "ll" key for users
        // upgrading from the version that had the L-Stick "ll/ls" bug.
        foreach (var key in new[] { "m1", "m2", "a", "b", "x", "y",
                                    "du", "dd", "dl", "dr",
                                    "lt", "rt", "lb", "rb",
                                    "ls", "rs", "vb", "mb",
                                    "ll" /* legacy L-Stick key */ })
        {
            AppConfig.Remove("bind_" + key);
            AppConfig.Remove("bind2_" + key);
            AppConfig.Remove("turbo_" + key);
            AppConfig.Remove("turbo2_" + key);
        }

        InitDeadzones();
        AllyControl.ApplyMode();
        AllyControl.SetDeadzones();
    }

    // ---------------------------------------------------------------------
    // Stick calibration handlers.
    //
    // We persist the user's last-applied values (cal_ls_*) so re-opening
    // the window shows them populated - handy for tweaking iteratively.
    // No write to AppConfig happens until the user clicks Apply.
    //
    // Defaults match asusctl/rog-platform/examples/ally-gamepad-calibration.rs
    // verified-working numbers for an Ally 2023 unit.
    // ---------------------------------------------------------------------

    private void InitStickCalibration()
    {
        // Left stick (asusctl example defaults - verified Ally 2023).
        numCalXStable.Value = AppConfig.Get("cal_ls_x_stable", 2107);
        numCalXMin.Value = AppConfig.Get("cal_ls_x_min", 815);
        numCalXMax.Value = AppConfig.Get("cal_ls_x_max", 3399);
        numCalYStable.Value = AppConfig.Get("cal_ls_y_stable", 2223);
        numCalYMin.Value = AppConfig.Get("cal_ls_y_min", 1020);
        numCalYMax.Value = AppConfig.Get("cal_ls_y_max", 3427);

        // Right stick (no published reference - defaults match the same
        // shape as left until a tested set surfaces).
        numCalRXStable.Value = AppConfig.Get("cal_rs_x_stable", 2107);
        numCalRXMin.Value = AppConfig.Get("cal_rs_x_min", 815);
        numCalRXMax.Value = AppConfig.Get("cal_rs_x_max", 3399);
        numCalRYStable.Value = AppConfig.Get("cal_rs_y_stable", 2223);
        numCalRYMin.Value = AppConfig.Get("cal_rs_y_min", 1020);
        numCalRYMax.Value = AppConfig.Get("cal_rs_y_max", 3427);

        // Triggers: rest typically 0, max ~4095 for a fully pressed analog
        // trigger but kernel reports them in a smaller range that the EC
        // remaps internally. Defaults assume rest=0 / max=4095.
        numCalLTStable.Value = AppConfig.Get("cal_lt_stable", 0);
        numCalLTMax.Value = AppConfig.Get("cal_lt_max", 4095);
        numCalRTStable.Value = AppConfig.Get("cal_rt_stable", 0);
        numCalRTMax.Value = AppConfig.Get("cal_rt_max", 4095);
    }

    private void ButtonStickCalReset_Click(object? sender, RoutedEventArgs e)
    {
        // Wipe persisted values and fire the firmware reset packet - the EC
        // restores its factory-saved calibration which is what new users want.
        foreach (var key in new[] {
            "cal_ls_x_stable", "cal_ls_x_min", "cal_ls_x_max",
            "cal_ls_y_stable", "cal_ls_y_min", "cal_ls_y_max",
            "cal_rs_x_stable", "cal_rs_x_min", "cal_rs_x_max",
            "cal_rs_y_stable", "cal_rs_y_min", "cal_rs_y_max",
            "cal_lt_stable", "cal_lt_max",
            "cal_rt_stable", "cal_rt_max" })
            AppConfig.Remove(key);

        InitStickCalibration();
        AllyControl.ResetStickCalibration();
    }

    private void ButtonStickCalApply_Click(object? sender, RoutedEventArgs e)
        => ApplyStick(AllyControl.CalStick.Left,
            "ls", numCalXStable, numCalXMin, numCalXMax, numCalYStable, numCalYMin, numCalYMax);

    private void ButtonStickCalApplyR_Click(object? sender, RoutedEventArgs e)
        => ApplyStick(AllyControl.CalStick.Right,
            "rs", numCalRXStable, numCalRXMin, numCalRXMax, numCalRYStable, numCalRYMin, numCalRYMax);

    /// <summary>Shared write path for left- and right-stick Apply buttons.</summary>
    private void ApplyStick(AllyControl.CalStick stick, string keyPrefix,
        NumericUpDown xs, NumericUpDown xn, NumericUpDown xx,
        NumericUpDown ys, NumericUpDown yn, NumericUpDown yx)
    {
        int sx = (int)(xs.Value ?? 2107);
        int nx = (int)(xn.Value ?? 815);
        int mx = (int)(xx.Value ?? 3399);
        int sy = (int)(ys.Value ?? 2223);
        int ny = (int)(yn.Value ?? 1020);
        int my = (int)(yx.Value ?? 3427);

        AppConfig.Set($"cal_{keyPrefix}_x_stable", sx);
        AppConfig.Set($"cal_{keyPrefix}_x_min", nx);
        AppConfig.Set($"cal_{keyPrefix}_x_max", mx);
        AppConfig.Set($"cal_{keyPrefix}_y_stable", sy);
        AppConfig.Set($"cal_{keyPrefix}_y_min", ny);
        AppConfig.Set($"cal_{keyPrefix}_y_max", my);

        AllyControl.ApplyStickCalibration(stick, sx, nx, mx, sy, ny, my);
    }

    private void ButtonTriggerCalApply_Click(object? sender, RoutedEventArgs e)
    {
        int lts = (int)(numCalLTStable.Value ?? 0);
        int ltx = (int)(numCalLTMax.Value ?? 4095);
        int rts = (int)(numCalRTStable.Value ?? 0);
        int rtx = (int)(numCalRTMax.Value ?? 4095);

        AppConfig.Set("cal_lt_stable", lts);
        AppConfig.Set("cal_lt_max", ltx);
        AppConfig.Set("cal_rt_stable", rts);
        AppConfig.Set("cal_rt_max", rtx);

        AllyControl.ApplyTriggerCalibration(lts, ltx, rts, rtx);
    }

    /// <summary>
    /// Run a 3-second evdev capture window and fill all numeric inputs with
    /// observed min/max/stable. Runs on a worker so the UI stays responsive
    /// during the read; results are marshalled back via Dispatcher.
    /// </summary>
    private void ButtonStickCalCapture_Click(object? sender, RoutedEventArgs e)
    {
        labelStickCalCapture.Text = Labels.Get("ally_stickcal_capture_running");
        buttonStickCalCapture.IsEnabled = false;

        System.Threading.Tasks.Task.Run(() =>
        {
            var r = AllyControl.CaptureAxes(3000);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                buttonStickCalCapture.IsEnabled = true;
                labelStickCalCapture.Text = Labels.Get("ally_stickcal_capture");

                if (!r.DeviceFound)
                {
                    Helpers.Logger.WriteLine("Ally stick auto-capture: no controller evdev node found");
                    return;
                }

                // Map kernel raw values (signed s16 sticks, unsigned 0..255
                // triggers - well, kernel usually reports both differently;
                // we use the captured min/max as the input range.)
                static void Apply(NumericUpDown dst, AllyControl.AxisRange r,
                    int chooseFor /* 0=stable,1=min,2=max */)
                {
                    if (!r.IsValid)
                        return;
                    int val = chooseFor switch { 1 => r.Min, 2 => r.Max, _ => r.Stable };
                    int mapped = AllyControl.MapKernelAxisToEc(val, r.Min, r.Max);
                    dst.Value = mapped;
                }

                Apply(numCalXStable, r.LeftX, 0);
                Apply(numCalXMin, r.LeftX, 1);
                Apply(numCalXMax, r.LeftX, 2);
                Apply(numCalYStable, r.LeftY, 0);
                Apply(numCalYMin, r.LeftY, 1);
                Apply(numCalYMax, r.LeftY, 2);

                Apply(numCalRXStable, r.RightX, 0);
                Apply(numCalRXMin, r.RightX, 1);
                Apply(numCalRXMax, r.RightX, 2);
                Apply(numCalRYStable, r.RightY, 0);
                Apply(numCalRYMin, r.RightY, 1);
                Apply(numCalRYMax, r.RightY, 2);

                Apply(numCalLTStable, r.TriggerL, 0);
                Apply(numCalLTMax, r.TriggerL, 2);
                Apply(numCalRTStable, r.TriggerR, 0);
                Apply(numCalRTMax, r.TriggerR, 2);
            });
        });
    }
}

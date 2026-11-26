using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Peripherals;
using GHelper.Linux.Peripherals.Logitech;
using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Settings window for a connected mouse (ASUS or Logitech).
/// Sections appear based on the capabilities the mouse class reports.
/// With no mouse connected, all sections are shown disabled as a dev preview.
/// </summary>
public partial class MouseWindow : Window
{
    private static readonly PowerOffSetting[] PowerOffValues =
    [
        PowerOffSetting.Minutes1, PowerOffSetting.Minutes2, PowerOffSetting.Minutes3,
        PowerOffSetting.Minutes5, PowerOffSetting.Minutes10, PowerOffSetting.Never,
    ];

    private static readonly DebounceTime[] DebounceValues =
    [
        DebounceTime.OFF, DebounceTime.MS8, DebounceTime.MS12, DebounceTime.MS16,
        DebounceTime.MS20, DebounceTime.MS24, DebounceTime.MS28, DebounceTime.MS32,
    ];

    private IMousePeripheral? _mouse;
    private PollingRate[] _pollingRates = [];
    private LightingMode[] _lightingModes = [];
    private LightingZone[] _lightingZones = [];
    private int _zoneIndex;
    private bool _loading = true;

    /// <summary>Persist Logitech mouse settings after a user change.</summary>
    private void PersistIfLogitech()
    {
        if (_mouse is LogitechMouse logi)
            logi.SaveSettings();
    }

    private readonly IMousePeripheral? _requestedMouse;

    public MouseWindow() : this(null) { }

    public MouseWindow(IMousePeripheral? mouse)
    {
        _requestedMouse = mouse;
        InitializeComponent();
        InitAsusHandlers();

        PeripheralsProvider.DeviceChanged += OnDeviceChanged;
        Closing += (_, _) => PeripheralsProvider.DeviceChanged -= OnDeviceChanged;
        Loaded += (_, _) => Refresh();
    }

    private void OnDeviceChanged()
    {
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        _loading = true;
        ApplyLabels();

        // Requested mouse wins while still connected, otherwise first available.
        _mouse = _requestedMouse != null && PeripheralsProvider.ConnectedMice.Contains(_requestedMouse)
            ? _requestedMouse
            : PeripheralsProvider.ConnectedMice.FirstOrDefault();

        if (_mouse is null)
            ShowPreview();
        else
            ShowMouse(_mouse);

        _loading = false;
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("mouse_title");
        lblHeaderDevice.Text = Labels.Get("mouse_section_device");
        lblBattery.Text = Labels.Get("mouse_battery");
        lblHeaderDpi.Text = Labels.Get("mouse_section_dpi");
        lblActiveProfile.Text = Labels.Get("mouse_active_profile");
        lblDpi.Text = Labels.Get("mouse_section_dpi");
        lblSensitivitySwitch.Text = Labels.Get("mouse_sensitivity_switch");
        lblHeaderPerformance.Text = Labels.Get("mouse_section_performance");
        lblPollingRate.Text = Labels.Get("mouse_polling_rate");
        lblAngleSnapping.Text = Labels.Get("mouse_angle_snapping");
        lblDebounce.Text = Labels.Get("mouse_debounce");
        lblLiftOff.Text = Labels.Get("mouse_liftoff");
        lblOnboardProfiles.Text = Labels.Get("mouse_onboard");
        lblHaptic.Text = Labels.Get("mouse_haptic_feedback");
        lblHapticWaveform.Text = Labels.Get("mouse_haptic_waveform");
        buttonHapticPlay.Content = Labels.Get("mouse_haptic_play");
        lblHeaderEnergy.Text = Labels.Get("mouse_section_energy");
        lblPowerOff.Text = Labels.Get("mouse_auto_poweroff");
        lblLowBattery.Text = Labels.Get("mouse_low_battery");
        lblSleepTimeout.Text = Labels.Get("mouse_sleep_timeout");
        lblHeaderScroll.Text = Labels.Get("mouse_section_scroll");
        lblSmartShift.Text = Labels.Get("mouse_smartshift");
        lblRatchetSpeed.Text = Labels.Get("mouse_ratchet_speed");
        lblHiResScroll.Text = Labels.Get("mouse_hires_scroll");
        lblHiResInvert.Text = Labels.Get("mouse_invert_scroll");
        lblHiResDivert.Text = Labels.Get("mouse_divert_scroll");
        lblCrownSmooth.Text = Labels.Get("mouse_crown_smooth");
        lblCrownDivert.Text = Labels.Get("mouse_divert_crown");
        lblThumbInvert.Text = Labels.Get("mouse_invert_thumb");
        lblThumbDivert.Text = Labels.Get("mouse_divert_thumb");
        lblPointerSpeed.Text = Labels.Get("mouse_pointer_speed");
        lblLowresMode.Text = Labels.Get("mouse_lowres_mode");
        lblHeaderConnection.Text = Labels.Get("mouse_section_connection");
        lblChangeHost.Text = Labels.Get("mouse_active_host");
        lblHeaderButtons.Text = Labels.Get("mouse_section_buttons");
        lblHeaderGestures.Text = Labels.Get("mouse_section_gestures");
        lblGestureDiversion.Text = Labels.Get("mouse_gesture_diversion");
        lblGestureParams.Text = Labels.Get("mouse_gesture_params");
        lblForceSensing.Text = Labels.Get("mouse_force_sensing");
        lblAnalogButtons.Text = Labels.Get("mouse_analog_buttons");
        lblKeyboard.Text = Labels.Get("mouse_section_keyboard");
        lblFnInversion.Text = Labels.Get("mouse_fn_swap");
        lblOsPlatform.Text = Labels.Get("mouse_os_platform");
        lblGKey.Text = Labels.Get("mouse_gkey_divert");
        lblMKey.Text = Labels.Get("mouse_mkey_leds");
        lblMrKey.Text = Labels.Get("mouse_mr_key");
        lblDisableKeys.Text = Labels.Get("mouse_disable_keys");
        lblBacklightDelayHandsIn.Text = Labels.Get("mouse_backlight_delay_hands_in");
        lblBacklightDelayHandsOut.Text = Labels.Get("mouse_backlight_delay_hands_out");
        lblBacklightDelayPowered.Text = Labels.Get("mouse_backlight_delay_powered");
        lblHandDetection.Text = Labels.Get("mouse_hand_detection");
        lblSideScrolling.Text = Labels.Get("mouse_side_scrolling");
        lblHeadset.Text = Labels.Get("mouse_section_headset");
        lblSidetone.Text = Labels.Get("mouse_sidetone");
        lblMicGain.Text = Labels.Get("mouse_mic_gain");
        lblMicMute.Text = Labels.Get("mouse_mic_mute");
        lblMicSnr.Text = Labels.Get("mouse_mic_snr");
        lblAiNoise.Text = Labels.Get("mouse_ai_noise");
        lblAiNoiseLevel.Text = Labels.Get("mouse_ai_noise_level");
        lblDoNotDisturb.Text = Labels.Get("mouse_dnd");
        lblEcoMode.Text = Labels.Get("mouse_eco_mode");
        lblAudioMix.Text = Labels.Get("mouse_audio_mix");
        lblHeadsetEq.Text = Labels.Get("mouse_equalizer");
        lblEqPreset.Text = Labels.Get("mouse_eq_preset");
        lblAdvancedEq.Text = Labels.Get("mouse_advanced_eq");
        lblAutoSleep.Text = Labels.Get("mouse_auto_sleep");
        lblPowerMgmt.Text = Labels.Get("mouse_power_mgmt");
        lblOnboardEffect.Text = Labels.Get("mouse_onboard_effect");
        lblPerZoneLighting.Text = Labels.Get("mouse_per_zone");
        lblHeaderLighting.Text = Labels.Get("mouse_section_lighting");
        lblZone.Text = Labels.Get("mouse_zone");
        lblMode.Text = Labels.Get("mouse_mode");
        lblBrightness.Text = Labels.Get("mouse_brightness");
        lblColor.Text = Labels.Get("mouse_color");
        buttonColor.Content = Labels.Get("mouse_pick_color");
        lblIdleEffect.Text = Labels.Get("mouse_idle_effect");
        lblIdleTimeout.Text = Labels.Get("mouse_idle_timeout");
        ApplyAsusLabels();
    }

    /// <summary>Preview when no device is present: all sections visible but disabled.</summary>
    private void ShowPreview()
    {
        Title = Labels.Get("mouse_title");
        labelDevice.Text = Labels.Get("mouse_no_device");
        labelBattery.Text = "--";

        rowBattery.IsVisible = true;
        panelDpi.IsVisible = true;
        panelPerformance.IsVisible = true;
        rowAngleSnapping.IsVisible = true;
        rowOnboardProfiles.IsVisible = false;
        rowHaptic.IsVisible = false;
        rowDebounce.IsVisible = true;
        rowLiftOff.IsVisible = true;
        panelEnergy.IsVisible = true;
        rowPowerOff.IsVisible = true;
        rowLowBattery.IsVisible = true;
        panelScroll.IsVisible = false;
        panelConnection.IsVisible = false;
        panelButtons.IsVisible = false;
        panelGestures.IsVisible = false;
        panelForceSensing.IsVisible = false;
        panelAnalogButtons.IsVisible = false;
        panelKeyboard.IsVisible = false;
        panelHeadset.IsVisible = false;
        panelLighting.IsVisible = true;
        rowZone.IsVisible = true;

        _pollingRates = Enum.GetValues<PollingRate>();
        _lightingModes = [LightingMode.Static, LightingMode.Breathing, LightingMode.ColorCycle,
            LightingMode.Rainbow, LightingMode.React, LightingMode.Comet, LightingMode.BatteryState, LightingMode.Off];
        _lightingZones = [LightingZone.All];
        _zoneIndex = 0;

        FillCombo(comboDpiProfile, Enumerable.Range(1, 4).Select(i => Labels.Format("mouse_profile_fmt", i)), 0);
        sliderDpi.Minimum = 100;
        sliderDpi.Maximum = 36000;
        sliderDpi.TickFrequency = 50;
        sliderDpi.Value = 800;
        labelDpiValue.Text = "800";

        FillCombo(comboPollingRate, _pollingRates.Select(PollingRateName), 3);
        checkAngleSnapping.IsChecked = false;
        FillCombo(comboDebounce, DebounceValues.Select(DebounceName), 2);
        FillCombo(comboLiftOff, [Labels.Get("mouse_liftoff_low"), Labels.Get("mouse_liftoff_high")], 0);
        FillCombo(comboPowerOff, PowerOffValues.Select(PowerOffName), 2);
        sliderLowBattery.Value = 25;
        labelLowBattery.Text = "25%";

        FillCombo(comboLightingZone, _lightingZones.Select(ZoneName), 0);
        FillCombo(comboLightingMode, _lightingModes.Select(LightingModeName), 0);
        sliderBrightness.Maximum = 100;
        sliderBrightness.Value = 100;
        labelBrightness.Text = "100";
        borderColor.Background = Brushes.White;

        ShowAsus(null);
        SetSectionsEnabled(false);
    }

    private void ShowMouse(IMousePeripheral mouse)
    {
        Title = mouse.GetDisplayName();
        labelDevice.Text = mouse.GetDisplayName();

        mouse.ReadBattery();
        rowBattery.IsVisible = mouse.HasBattery();
        labelBattery.Text = BatteryText(mouse);

        // DPI
        panelDpi.IsVisible = true;
        int profileCount = mouse.DpiSettings.Length;
        int profile = Math.Clamp(mouse.DpiProfile, 0, Math.Max(profileCount - 1, 0));
        FillCombo(comboDpiProfile, Enumerable.Range(1, profileCount).Select(i => Labels.Format("mouse_profile_fmt", i)), profile);
        sliderDpi.Minimum = mouse.MinDPI();
        sliderDpi.Maximum = mouse.MaxDPI();
        sliderDpi.TickFrequency = mouse.DPIIncrement();
        sliderDpi.Value = profileCount > 0 ? mouse.DpiSettings[profile]?.DPI ?? 800 : 800;
        labelDpiValue.Text = ((int)sliderDpi.Value).ToString();

        rowSensitivitySwitch.IsVisible = mouse.HasSensitivitySwitch();
        if (rowSensitivitySwitch.IsVisible)
            checkSensitivitySwitch.IsChecked = mouse.SensitivitySwitch;

        // Performance
        _pollingRates = mouse.SupportedPollingrates();
        int rateIndex = Math.Max(0, Array.IndexOf(_pollingRates, mouse.PollingRate));
        FillCombo(comboPollingRate, _pollingRates.Select(PollingRateName), rateIndex);

        rowAngleSnapping.IsVisible = mouse.HasAngleSnapping();
        checkAngleSnapping.IsChecked = mouse.AngleSnapping;

        rowOnboardProfiles.IsVisible = mouse.HasOnboardProfiles();
        if (mouse.HasOnboardProfiles())
            checkOnboardProfiles.IsChecked = mouse.OnboardProfileEnabled;

        rowHaptic.IsVisible = mouse.HasHaptic();
        if (mouse.HasHaptic())
        {
            FillCombo(comboHaptic, [Labels.Get("mouse_off"), Labels.Get("mouse_low"), Labels.Get("mouse_medium"), Labels.Get("mouse_high"), Labels.Get("mouse_max")],
                mouse.HapticEnabled ? Math.Clamp(mouse.HapticLevel / 25, 1, 4) : 0);
        }

        rowHapticWaveform.IsVisible = mouse.HasHapticWaveform();
        if (rowHapticWaveform.IsVisible)
        {
            FillCombo(comboHapticWaveform, Enumerable.Range(1, 16).Select(i => Labels.Format("mouse_waveform_fmt", i)), Math.Clamp(mouse.HapticWaveformIndex - 1, 0, 15));
        }

        rowDebounce.IsVisible = mouse.HasDebounce();
        int debounceIndex = Math.Max(0, Array.IndexOf(DebounceValues, mouse.Debounce));
        FillCombo(comboDebounce, DebounceValues.Select(DebounceName), debounceIndex);

        rowLiftOff.IsVisible = true;
        FillCombo(comboLiftOff, [Labels.Get("mouse_liftoff_low"), Labels.Get("mouse_liftoff_high")], (int)mouse.LiftOff);

        // Energy
        rowPowerOff.IsVisible = mouse.HasAutoPowerOff();
        rowLowBattery.IsVisible = mouse.HasLowBatteryWarning();
        int powerOffIndex = Math.Max(0, Array.IndexOf(PowerOffValues, mouse.PowerOff));
        FillCombo(comboPowerOff, PowerOffValues.Select(PowerOffName), powerOffIndex);
        sliderLowBattery.Value = mouse.LowBatteryWarning;
        labelLowBattery.Text = $"{mouse.LowBatteryWarning}%";

        rowSleepTimeout.IsVisible = mouse.HasSleepTimeout();
        if (rowSleepTimeout.IsVisible)
        {
            sliderSleepTimeout.Value = mouse.SleepTimeoutSeconds;
            labelSleepTimeout.Text = mouse.SleepTimeoutSeconds.ToString();
        }
        panelEnergy.IsVisible = rowPowerOff.IsVisible || rowLowBattery.IsVisible || rowSleepTimeout.IsVisible;

        // Scroll & Wheel (Logitech features)
        bool hasScroll = mouse.HasSmartShift() || mouse.HasHiResScroll()
                      || mouse.HasThumbWheel() || mouse.HasPointerSpeed()
                      || mouse.HasCrown();
        panelScroll.IsVisible = hasScroll;
        if (hasScroll)
        {
            rowSmartShift.IsVisible = mouse.HasSmartShift();
            rowSmartShiftThreshold.IsVisible = mouse.HasSmartShift();
            rowHiResScroll.IsVisible = mouse.HasHiResScroll();
            rowHiResInvert.IsVisible = mouse.HasHiResScroll();
            rowHiResDivert.IsVisible = mouse.HasHiResScroll();
            rowCrownSmooth.IsVisible = mouse.HasCrown();
            rowCrownDivert.IsVisible = mouse.HasCrown();
            rowThumbInvert.IsVisible = mouse.HasThumbWheel();
            rowThumbDivert.IsVisible = mouse.HasThumbWheel();

            if (mouse.HasSmartShift())
            {
                FillCombo(comboSmartShift, [Labels.Get("mouse_ratchet_mode"), Labels.Get("mouse_freespin")],
                    mouse.SmartShiftRatchet ? 0 : 1);
                sliderSmartShiftThreshold.Value = Math.Clamp(mouse.SmartShiftThreshold, 1, 50);
                labelSmartShiftThreshold.Text = mouse.SmartShiftThreshold.ToString();
            }

            if (mouse.HasHiResScroll())
            {
                checkHiResScroll.IsChecked = mouse.HiResScrollEnabled;
                checkHiResInvert.IsChecked = mouse.HiResScrollInvert;
                checkHiResDivert.IsChecked = mouse.HiResScrollDivert;
            }

            if (mouse.HasCrown())
            {
                checkCrownSmooth.IsChecked = mouse.CrownSmooth;
                checkCrownDivert.IsChecked = mouse.CrownDivert;
            }

            if (mouse.HasThumbWheel())
            {
                checkThumbInvert.IsChecked = mouse.ThumbWheelInvert;
                checkThumbDivert.IsChecked = mouse.ThumbWheelDivert;
            }

            rowPointerSpeed.IsVisible = mouse.HasPointerSpeed();
            if (mouse.HasPointerSpeed())
            {
                sliderPointerSpeed.Value = mouse.PointerSpeed;
                labelPointerSpeed.Text = $"{mouse.PointerSpeed / 256.0:F1}x";
            }

            rowLowresMode.IsVisible = mouse.HasLowresMode();
            if (rowLowresMode.IsVisible)
            {
                FillCombo(comboLowresMode, ["HID", "HID++"], Math.Clamp(mouse.LowresModeIndex, 0, 1));
            }
        }

        // Connection (Logitech features)
        panelConnection.IsVisible = mouse.HasChangeHost();
        if (mouse.HasChangeHost())
        {
            FillCombo(comboChangeHost,
                Enumerable.Range(1, mouse.HostCount).Select(i => Labels.Format("mouse_host_fmt", i)),
                mouse.CurrentHost);
        }

        // Buttons & Gestures (Logitech)
        var logi = mouse as LogitechMouse;
        panelButtons.IsVisible = logi is not null && logi.HasReprogControls() && logi.ReprogButtons.Count > 0;
        if (panelButtons.IsVisible)
        {
            stackButtons.Children.Clear();
            foreach (var btn in logi!.ReprogButtons)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var label = new TextBlock { Text = btn.Name, VerticalAlignment = VerticalAlignment.Center };
                label.Classes.Add("label-dim");
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                if (btn.Divertable)
                {
                    var cb = new CheckBox { Name = "checkMouseButtonDivert", IsChecked = btn.Diverted, Tag = btn.Cid };
                    cb.IsCheckedChanged += OnButtonDivertToggled;
                    Grid.SetColumn(cb, 1);
                    row.Children.Add(cb);
                }
                stackButtons.Children.Add(row);
            }
        }

        panelGestures.IsVisible = logi is not null && logi.HasGesture()
                                  && (logi.Gestures.Count > 0 || logi.GestureDiverts.Count > 0 || logi.GestureParams.Count > 0);
        if (panelGestures.IsVisible)
        {
            stackGestures.Children.Clear();
            foreach (var g in logi!.Gestures)
            {
                var cb = new CheckBox
                {
                    Name = "checkMouseGesture",
                    Content = g.Name,
                    IsChecked = g.Enabled,
                    Tag = g.EnableIndex,
                };
                cb.IsCheckedChanged += OnGestureToggled;
                stackGestures.Children.Add(cb);
            }

            stackGestureDiverts.Children.Clear();
            lblGestureDiversion.IsVisible = logi.GestureDiverts.Count > 0;
            foreach (var g in logi.GestureDiverts)
            {
                var cb = new CheckBox
                {
                    Name = "checkMouseGestureDivert",
                    Content = g.Name,
                    IsChecked = g.Diverted,
                    Tag = g.DivertIndex,
                };
                cb.IsCheckedChanged += OnGestureDivertToggled;
                stackGestureDiverts.Children.Add(cb);
            }

            stackGestureParams.Children.Clear();
            lblGestureParams.IsVisible = logi.GestureParams.Count > 0;
            foreach (var p in logi.GestureParams)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,60"), Margin = new Avalonia.Thickness(0, 4, 0, 0) };
                string paramLabel = p.Name == "Scale Factor" ? Labels.Get("mouse_gesture_scale") : p.Name;
                // Per-button labels emitted by HID++ feature reports remain as device strings.
                var lbl = new TextBlock { Text = paramLabel, VerticalAlignment = VerticalAlignment.Center };
                lbl.Classes.Add("label-dim");
                Grid.SetColumn(lbl, 0);
                var slider = new Slider { Name = "sliderMouseGestureParam", Minimum = 0, Maximum = Math.Max(p.Max, p.Value), Value = p.Value, Tag = p.Index };
                Grid.SetColumn(slider, 1);
                var val = new TextBlock { Text = p.Value.ToString(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                val.Classes.Add("value");
                Grid.SetColumn(val, 2);
                slider.ValueChanged += (_, _) =>
                {
                    val.Text = ((int)slider.Value).ToString();
                    if (_loading || _mouse is null)
                        return;
                    (_mouse as LogitechMouse)?.WriteGestureParam((int)slider.Tag!, (int)slider.Value);
                };
                grid.Children.Add(lbl);
                grid.Children.Add(slider);
                grid.Children.Add(val);
                stackGestureParams.Children.Add(grid);
            }
        }

        // Force Sensing Buttons (Logitech)
        panelForceSensing.IsVisible = logi is not null && logi.HasForceSensing() && logi.ForceSensingButtons.Count > 0;
        if (panelForceSensing.IsVisible)
        {
            stackForceSensing.Children.Clear();
            foreach (var b in logi!.ForceSensingButtons)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,60"), Margin = new Avalonia.Thickness(0, 4, 0, 0) };
                var lbl = new TextBlock { Text = $"{Labels.Get("mouse_force")} {b.Index + 1}", VerticalAlignment = VerticalAlignment.Center };
                lbl.Classes.Add("label-dim");
                Grid.SetColumn(lbl, 0);
                var slider = new Slider { Name = "sliderMouseForce", Minimum = b.Min, Maximum = b.Max, Value = b.Current, Tag = b.Index };
                Grid.SetColumn(slider, 1);
                var val = new TextBlock { Text = b.Current.ToString(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                val.Classes.Add("value");
                Grid.SetColumn(val, 2);
                slider.ValueChanged += (_, _) =>
                {
                    val.Text = ((int)slider.Value).ToString();
                    if (_loading || _mouse is null)
                        return;
                    (_mouse as LogitechMouse)?.WriteForceSensingButton((byte)slider.Tag!, (int)slider.Value);
                };
                grid.Children.Add(lbl);
                grid.Children.Add(slider);
                grid.Children.Add(val);
                stackForceSensing.Children.Add(grid);
            }
        }

        // Analog Buttons (Logitech)
        panelAnalogButtons.IsVisible = logi is not null && logi.HasAnalogButtons() && logi.AnalogButtons.Count > 0;
        if (panelAnalogButtons.IsVisible)
        {
            stackAnalogButtons.Children.Clear();
            foreach (var ab in logi!.AnalogButtons)
            {
                var header = new TextBlock { Text = Labels.Format("mouse_button_fmt", ab.Index + 1), Margin = new Avalonia.Thickness(0, 8, 0, 4) };
                header.Classes.Add("label-dim");
                stackAnalogButtons.Children.Add(header);
                stackAnalogButtons.Children.Add(MakeAnalogSlider(name: "sliderAnalogActuation", Labels.Get("mouse_actuation"), ab.Index, "a", ab.Actuation, logi.AnalogMaxActuation));
                stackAnalogButtons.Children.Add(MakeAnalogSlider(name: "sliderAnalogRapidTrigger", Labels.Get("mouse_rapid_trigger"), ab.Index, "r", ab.RapidTrigger, logi.AnalogMaxRapidTrigger));
                stackAnalogButtons.Children.Add(MakeAnalogSlider(name: "sliderAnalogHaptics", Labels.Get("mouse_haptics"), ab.Index, "h", ab.Haptics, logi.AnalogMaxHaptics));
            }
        }

        // Keyboard (Logitech)
        panelKeyboard.IsVisible = logi is not null && logi.HasKeyboardFeatures();
        if (panelKeyboard.IsVisible)
        {
            rowFnInversion.IsVisible = logi!.HasFnInversion();
            if (rowFnInversion.IsVisible)
                checkFnInversion.IsChecked = logi.FnInversion;

            rowOsPlatform.IsVisible = logi.HasOsPlatform();
            if (rowOsPlatform.IsVisible)
            {
                int count = Math.Max(1, logi.OsPlatformCount);
                FillCombo(comboOsPlatform, Enumerable.Range(1, count).Select(i => Labels.Format("mouse_os_fmt", i)), Math.Clamp(logi.OsPlatform, 0, count - 1));
            }

            rowGKey.IsVisible = logi.HasGKey();
            if (rowGKey.IsVisible)
                checkGKey.IsChecked = logi.GKeyDivert;

            rowMKey.IsVisible = logi.HasMKey();
            if (rowMKey.IsVisible)
                sliderMKey.Value = Math.Clamp(logi.MKeyLeds, 0, 7);

            rowMrKey.IsVisible = logi.HasMrKey();
            if (rowMrKey.IsVisible)
                checkMrKey.IsChecked = logi.MrKeyLed;

            rowDisableKeys.IsVisible = logi.HasDisableKeys();
            if (rowDisableKeys.IsVisible)
                labelDisableKeys.Text = logi.DisabledKeysSummary;

            rowBacklightDelayHandsIn.IsVisible = logi.HasBacklightDelay();
            rowBacklightDelayHandsOut.IsVisible = logi.HasBacklightDelay();
            rowBacklightDelayPowered.IsVisible = logi.HasBacklightDelay();
            if (logi.HasBacklightDelay())
            {
                sliderBacklightDelayHandsIn.Value = logi.BacklightDelayHandsIn;
                labelBacklightDelayHandsIn.Text = logi.BacklightDelayHandsIn.ToString();
                sliderBacklightDelayHandsOut.Value = logi.BacklightDelayHandsOut;
                labelBacklightDelayHandsOut.Text = logi.BacklightDelayHandsOut.ToString();
                sliderBacklightDelayPowered.Value = logi.BacklightDelayPowered;
                labelBacklightDelayPowered.Text = logi.BacklightDelayPowered.ToString();
            }

            rowHandDetection.IsVisible = logi.HasHandDetection();
            if (rowHandDetection.IsVisible)
                checkHandDetection.IsChecked = logi.HandDetection;

            rowSideScrolling.IsVisible = logi.HasSideScrolling();
            if (rowSideScrolling.IsVisible)
                checkSideScrolling.IsChecked = logi.SideScrolling;
        }

        // Headset (Logitech)
        panelHeadset.IsVisible = logi is not null && logi.HasHeadsetFeatures();
        if (panelHeadset.IsVisible)
        {
            rowSidetone.IsVisible = logi!.HasSidetone();
            if (rowSidetone.IsVisible)
            { sliderSidetone.Value = logi.HeadsetSidetone; labelSidetoneValue.Text = logi.HeadsetSidetone.ToString(); }

            rowMicGain.IsVisible = logi.HasMicGain();
            if (rowMicGain.IsVisible)
            { sliderMicGain.Value = logi.HeadsetMicGain; labelMicGainValue.Text = logi.HeadsetMicGain.ToString(); }

            rowMicMute.IsVisible = logi.HasMicMute();
            if (rowMicMute.IsVisible)
                checkMicMute.IsChecked = logi.HeadsetMicMute;

            rowMicSnr.IsVisible = logi.HasMicSnr();
            if (rowMicSnr.IsVisible)
                checkMicSnr.IsChecked = logi.HeadsetMicSnrEnabled;

            rowAiNoise.IsVisible = logi.HasAiNoise();
            rowAiNoiseLevel.IsVisible = logi.HasAiNoise();
            if (rowAiNoise.IsVisible)
            {
                checkAiNoise.IsChecked = logi.HeadsetAiNoise;
                sliderAiNoiseLevel.Value = logi.HeadsetAiNoiseLevel;
                labelAiNoiseLevel.Text = logi.HeadsetAiNoiseLevel.ToString();
            }

            rowDoNotDisturb.IsVisible = logi.HasDoNotDisturb();
            if (rowDoNotDisturb.IsVisible)
                checkDoNotDisturb.IsChecked = logi.HeadsetDoNotDisturb;

            rowEcoMode.IsVisible = logi.HasEcoMode();
            if (rowEcoMode.IsVisible)
                checkEcoMode.IsChecked = logi.HeadsetEcoMode;

            rowAudioMix.IsVisible = logi.HasAudioMix();
            if (rowAudioMix.IsVisible)
            { sliderAudioMix.Value = logi.HeadsetAudioMix; labelAudioMix.Text = logi.HeadsetAudioMix.ToString(); }

            rowHeadsetEq.IsVisible = logi.HasHeadsetEq();
            if (rowHeadsetEq.IsVisible)
            {
                FillCombo(comboHeadsetEq, [Labels.Get("mouse_eq_default"), Labels.Get("mouse_eq_bass"), Labels.Get("mouse_eq_treble"), Labels.Get("mouse_eq_vocal"), Labels.Get("mouse_eq_custom")], Math.Clamp(logi.HeadsetEqIndex, 0, 4));
            }

            rowEqPreset.IsVisible = logi.HasAdvancedEq();
            if (rowEqPreset.IsVisible)
            {
                FillCombo(comboEqPreset, [Labels.Get("mouse_preset_flat"), Labels.Get("mouse_preset_bass_boost"), Labels.Get("mouse_preset_treble_boost"), Labels.Get("mouse_preset_vocal"), Labels.Get("mouse_preset_fps"), Labels.Get("mouse_preset_custom")], Math.Clamp(logi.HeadsetEqPresetIndex, 0, 5));
            }

            rowAdvancedEq.IsVisible = logi.HasAdvancedEq();
            if (rowAdvancedEq.IsVisible)
                checkAdvancedEq.IsChecked = logi.HeadsetAdvancedEq;

            rowAutoSleep.IsVisible = logi.HasAutoSleep();
            if (rowAutoSleep.IsVisible)
            { sliderAutoSleep.Value = logi.HeadsetAutoSleepMinutes; labelAutoSleep.Text = logi.HeadsetAutoSleepMinutes.ToString(); }

            rowPowerMgmt.IsVisible = logi.HasPowerMgmt();
            if (rowPowerMgmt.IsVisible)
                checkPowerMgmt.IsChecked = logi.HeadsetPowerMgmt;

            rowOnboardEffect.IsVisible = logi.HasOnboardEffect();
            if (rowOnboardEffect.IsVisible)
            {
                FillCombo(comboOnboardEffect, [Labels.Get("mouse_off"), Labels.Get("mouse_effect_static"), Labels.Get("mouse_effect_breathing"), Labels.Get("mouse_effect_cycle"), Labels.Get("mouse_effect_wave")], Math.Clamp(logi.HeadsetOnboardEffectIndex, 0, 4));
            }

            rowPerZoneLighting.IsVisible = logi.HasPerZoneLighting();
            if (rowPerZoneLighting.IsVisible)
                checkPerZoneLighting.IsChecked = logi.HeadsetPerZoneLighting;
        }

        // Lighting
        _lightingZones = mouse.SupportedLightingZones();
        _lightingModes = mouse.SupportedLightingModes();
        panelLighting.IsVisible = _lightingZones.Length > 0;
        if (panelLighting.IsVisible)
        {
            _zoneIndex = Math.Clamp(_zoneIndex, 0, _lightingZones.Length - 1);
            rowZone.IsVisible = _lightingZones.Length > 1;
            FillCombo(comboLightingZone, _lightingZones.Select(ZoneName), _zoneIndex);
            sliderBrightness.Maximum = mouse.MaxBrightness();
            RefreshLightingControls(mouse);

            rowIdleEffect.IsVisible = mouse.HasIdleEffect();
            if (rowIdleEffect.IsVisible)
            {
                FillCombo(comboIdleEffect, _lightingModes.Select(LightingModeName), Math.Clamp(mouse.IdleEffectIndex, 0, Math.Max(0, _lightingModes.Length - 1)));
            }

            rowIdleTimeout.IsVisible = mouse.HasIdleTimeout();
            if (rowIdleTimeout.IsVisible)
            {
                sliderIdleTimeout.Value = mouse.IdleTimeoutSeconds;
                labelIdleTimeout.Text = mouse.IdleTimeoutSeconds.ToString();
            }
        }

        ShowAsus(mouse as AsusMouse);
        SetSectionsEnabled(true);
    }

    private void RefreshLightingControls(IMousePeripheral mouse)
    {
        var setting = mouse.LightingSettings[_zoneIndex];
        int modeIndex = Math.Max(0, Array.IndexOf(_lightingModes, setting.LightingMode));
        FillCombo(comboLightingMode, _lightingModes.Select(LightingModeName), modeIndex);
        sliderBrightness.Value = Math.Min(setting.Brightness, (byte)mouse.MaxBrightness());
        labelBrightness.Text = ((int)sliderBrightness.Value).ToString();
        borderColor.Background = new SolidColorBrush(Color.FromRgb(setting.RGBColor.R, setting.RGBColor.G, setting.RGBColor.B));
    }

    private static string BatteryText(IPeripheral mouse) => mouse.Battery < 0
        ? "--"
        : $"{mouse.Battery}%{(mouse.Charging ? $" ({Labels.Get("mouse_charging")})" : "")}";

    private void SetSectionsEnabled(bool enabled)
    {
        panelDpi.IsEnabled = enabled;
        panelPerformance.IsEnabled = enabled;
        panelEnergy.IsEnabled = enabled;
        panelScroll.IsEnabled = enabled;
        panelConnection.IsEnabled = enabled;
        panelButtons.IsEnabled = enabled;
        panelGestures.IsEnabled = enabled;
        panelForceSensing.IsEnabled = enabled;
        panelAnalogButtons.IsEnabled = enabled;
        panelLighting.IsEnabled = enabled;
    }

    private static void FillCombo(ComboBox combo, IEnumerable<string> items, int selectedIndex)
    {
        combo.Items.Clear();
        foreach (var item in items)
            combo.Items.Add(new ComboBoxItem { Content = item });
        if (combo.Items.Count > 0)
            combo.SelectedIndex = Math.Clamp(selectedIndex, 0, combo.Items.Count - 1);
    }

    // Event handlers

    private void ComboDpiProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboDpiProfile.SelectedIndex < 0)
            return;

        if (_mouse is AsusMouse am)
        {
            AsusDpiProfile_Changed(am);
            return;
        }

        _mouse.DpiProfile = comboDpiProfile.SelectedIndex;
        _mouse.WriteDPI();
        PersistIfLogitech();

        _loading = true;
        sliderDpi.Value = _mouse.DpiSettings[_mouse.DpiProfile].DPI;
        labelDpiValue.Text = _mouse.DpiSettings[_mouse.DpiProfile].DPI.ToString();
        _loading = false;
    }

    private void SliderDpi_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelDpiValue.Text = ((int)sliderDpi.Value).ToString();
        if (_loading || _mouse is null)
            return;

        if (_mouse is AsusMouse)
            return;

        int profile = Math.Clamp(_mouse.DpiProfile, 0, _mouse.DpiSettings.Length - 1);
        _mouse.DpiSettings[profile].DPI = (uint)sliderDpi.Value;
        _mouse.WriteDPI();
        PersistIfLogitech();
    }

    private void ComboPollingRate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboPollingRate.SelectedIndex < 0)
            return;

        if (_mouse is AsusMouse am)
        {
            AsusPollingRate_Changed(am);
            return;
        }

        _mouse.PollingRate = _pollingRates[comboPollingRate.SelectedIndex];
        _mouse.WritePollingRate();
        PersistIfLogitech();
    }

    private void CheckAngleSnapping_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.AngleSnapping = checkAngleSnapping.IsChecked == true;
        _mouse.WriteAngleSnapping();
    }

    private void ComboDebounce_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboDebounce.SelectedIndex < 0)
            return;

        if (_mouse is AsusMouse am)
        {
            AsusDebounce_Changed(am);
            return;
        }

        _mouse.Debounce = DebounceValues[comboDebounce.SelectedIndex];
        _mouse.WriteDebounce();
    }

    private void ComboLiftOff_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboLiftOff.SelectedIndex < 0)
            return;

        _mouse.LiftOff = (LiftOffDistance)comboLiftOff.SelectedIndex;
        _mouse.WriteLiftOffDistance();
    }

    private void ComboPowerOff_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboPowerOff.SelectedIndex < 0)
            return;

        _mouse.PowerOff = PowerOffValues[comboPowerOff.SelectedIndex];
        _mouse.WritePowerOff();
    }

    private void SliderLowBattery_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelLowBattery.Text = $"{(int)sliderLowBattery.Value}%";
        if (_loading || _mouse is null)
            return;

        if (_mouse is AsusMouse)
            return;

        _mouse.LowBatteryWarning = (byte)sliderLowBattery.Value;
        _mouse.WriteLowBatteryWarning();
    }

    private void ComboLightingZone_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboLightingZone.SelectedIndex < 0)
            return;

        if (_mouse is AsusMouse am)
        {
            AsusLightingZone_Changed(am);
            return;
        }

        _zoneIndex = comboLightingZone.SelectedIndex;
        _loading = true;
        RefreshLightingControls(_mouse);
        _loading = false;
    }

    private void ComboLightingMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboLightingMode.SelectedIndex < 0)
            return;

        if (_mouse is AsusMouse am)
        {
            AsusLightingMode_Changed(am);
            return;
        }

        _mouse.LightingSettings[_zoneIndex].LightingMode = _lightingModes[comboLightingMode.SelectedIndex];
        _mouse.WriteLightingSetting();
        PersistIfLogitech();
    }

    private void SliderBrightness_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelBrightness.Text = ((int)sliderBrightness.Value).ToString();
        if (_loading || _mouse is null)
            return;

        if (_mouse is AsusMouse)
            return;

        _mouse.LightingSettings[_zoneIndex].Brightness = (byte)sliderBrightness.Value;
        _mouse.WriteLightingSetting();
        PersistIfLogitech();
    }

    private void ButtonColor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mouse is null)
            return;
        if (_mouse is AsusMouse am)
        {
            AsusLightingColor_Click(am);
            return;
        }

        var setting = _mouse.LightingSettings[_zoneIndex];
        Helpers.ColorPicker.Show(this, setting.RGBColor.R, setting.RGBColor.G, setting.RGBColor.B, (r, g, b) =>
        {
            setting.RGBColor = System.Drawing.Color.FromArgb(r, g, b);
            _mouse.WriteLightingSetting();
            PersistIfLogitech();
            borderColor.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        });
    }

    // Scroll & Wheel event handlers

    private void ComboSmartShift_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboSmartShift.SelectedIndex < 0)
            return;

        _mouse.SmartShiftRatchet = comboSmartShift.SelectedIndex == 0;
        _mouse.WriteSmartShift();
        PersistIfLogitech();
    }

    private void SliderSmartShiftThreshold_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelSmartShiftThreshold.Text = ((int)sliderSmartShiftThreshold.Value).ToString();
        if (_loading || _mouse is null)
            return;

        _mouse.SmartShiftThreshold = (int)sliderSmartShiftThreshold.Value;
        _mouse.WriteSmartShift();
        PersistIfLogitech();
    }

    private void CheckHiResScroll_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.HiResScrollEnabled = checkHiResScroll.IsChecked == true;
        _mouse.WriteHiResScroll();
        PersistIfLogitech();
    }

    private void CheckHiResInvert_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.HiResScrollInvert = checkHiResInvert.IsChecked == true;
        _mouse.WriteHiResScroll();
        PersistIfLogitech();
    }

    private void CheckHiResDivert_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.HiResScrollDivert = checkHiResDivert.IsChecked == true;
        _mouse.WriteHiResScroll();
        PersistIfLogitech();
    }

    private void CheckCrownSmooth_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.CrownSmooth = checkCrownSmooth.IsChecked == true;
        _mouse.WriteCrown();
        PersistIfLogitech();
    }

    private void CheckCrownDivert_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.CrownDivert = checkCrownDivert.IsChecked == true;
        _mouse.WriteCrown();
        PersistIfLogitech();
    }

    private void CheckThumbInvert_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.ThumbWheelInvert = checkThumbInvert.IsChecked == true;
        _mouse.WriteThumbWheel();
        PersistIfLogitech();
    }

    private void CheckThumbDivert_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.ThumbWheelDivert = checkThumbDivert.IsChecked == true;
        _mouse.WriteThumbWheel();
        PersistIfLogitech();
    }

    private void SliderPointerSpeed_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelPointerSpeed.Text = $"{sliderPointerSpeed.Value / 256.0:F1}x";
        if (_loading || _mouse is null)
            return;

        _mouse.PointerSpeed = (int)sliderPointerSpeed.Value;
        _mouse.WritePointerSpeed();
        PersistIfLogitech();
    }

    private async void ComboChangeHost_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboChangeHost.SelectedIndex < 0)
            return;

        int hostIndex = comboChangeHost.SelectedIndex;
        if (hostIndex == _mouse.CurrentHost)
            return;

        bool confirmed = await ConfirmHostSwitch(hostIndex);
        if (!confirmed)
        {
            _loading = true;
            comboChangeHost.SelectedIndex = _mouse.CurrentHost;
            _loading = false;
            return;
        }

        _mouse.CurrentHost = hostIndex;
        _mouse.WriteChangeHost();
    }

    private void CheckOnboardProfiles_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;

        _mouse.OnboardProfileEnabled = checkOnboardProfiles.IsChecked == true;
        _mouse.WriteOnboardMode();
    }

    private void ComboHaptic_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboHaptic.SelectedIndex < 0)
            return;

        int idx = comboHaptic.SelectedIndex;
        _mouse.HapticEnabled = idx > 0;
        _mouse.HapticLevel = idx * 25; // 0=Off, 25=Low, 50=Medium, 75=High, 100=Max
        _mouse.WriteHaptic();
    }

    private void OnGestureToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null || sender is not CheckBox cb || cb.Tag is not int enableIndex)
            return;
        (_mouse as LogitechMouse)?.WriteGesture(enableIndex, cb.IsChecked == true);
    }

    private void OnGestureDivertToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null || sender is not CheckBox cb || cb.Tag is not int divertIndex)
            return;
        (_mouse as LogitechMouse)?.WriteGestureDivert(divertIndex, cb.IsChecked == true);
    }

    private void OnButtonDivertToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null || sender is not CheckBox cb || cb.Tag is not ushort cid)
            return;
        (_mouse as LogitechMouse)?.WriteReprogDivert(cid, cb.IsChecked == true);
    }

    private Grid MakeAnalogSlider(string name, string label, byte buttonIndex, string kind, int value, int max)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,60"), Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        lbl.Classes.Add("label-dim");
        Grid.SetColumn(lbl, 0);
        int sliderMax = Math.Max(max, value);
        if (sliderMax <= 0)
            sliderMax = 10;
        var slider = new Slider { Name = name, Minimum = 0, Maximum = sliderMax, Value = value, Tag = $"{buttonIndex}:{kind}" };
        Grid.SetColumn(slider, 1);
        var val = new TextBlock { Text = value.ToString(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        val.Classes.Add("value");
        Grid.SetColumn(val, 2);
        slider.ValueChanged += (_, _) =>
        {
            val.Text = ((int)slider.Value).ToString();
            if (_loading || _mouse is not LogitechMouse logi)
                return;
            WriteAnalogButtonFromUI(logi, buttonIndex);
        };
        grid.Children.Add(lbl);
        grid.Children.Add(slider);
        grid.Children.Add(val);
        return grid;
    }

    private void WriteAnalogButtonFromUI(LogitechMouse logi, byte buttonIndex)
    {
        int act = 0, rt = 0, hap = 0;
        foreach (var child in stackAnalogButtons.Children)
        {
            if (child is not Grid g)
                continue;
            foreach (var c in g.Children)
            {
                if (c is Slider s && s.Tag is string tag)
                {
                    var parts = tag.Split(':');
                    if (parts.Length == 2 && byte.Parse(parts[0]) == buttonIndex)
                    {
                        if (parts[1] == "a")
                            act = (int)s.Value;
                        else if (parts[1] == "r")
                            rt = (int)s.Value;
                        else if (parts[1] == "h")
                            hap = (int)s.Value;
                    }
                }
            }
        }
        logi.WriteAnalogButton(buttonIndex, act, rt, hap);
    }

    private async Task<bool> ConfirmHostSwitch(int hostIndex)
    {
        var dialog = new Window
        {
            Title = Labels.Get("mouse_host_switch_title"),
            Width = 320,
            Height = 130,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
        };

        var msg = new TextBlock
        {
            Text = Labels.Format("mouse_host_switch_msg_fmt", hostIndex + 1),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16, 16, 16, 8),
        };

        var btnSwitch = new Button
        {
            Name = "buttonHostSwitchConfirm",
            Content = Labels.Get("mouse_switch"),
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
        };
        var btnCancel = new Button { Name = "buttonHostSwitchCancel", Content = Labels.Get("mouse_cancel") };
        btnSwitch.Click += (_, _) => dialog.Close(true);
        btnCancel.Click += (_, _) => dialog.Close(false);

        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(16, 0, 16, 16),
        };
        btnPanel.Children.Add(btnSwitch);
        btnPanel.Children.Add(btnCancel);

        var root = new StackPanel();
        root.Children.Add(msg);
        root.Children.Add(btnPanel);
        dialog.Content = root;

        return await dialog.ShowDialog<bool>(this);
    }

    // Keyboard handlers

    private void CheckFnInversion_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.FnInversion = checkFnInversion.IsChecked == true;
        lm.WriteFnInversion();
    }

    private void ComboOsPlatform_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm || comboOsPlatform.SelectedIndex < 0)
            return;
        lm.OsPlatform = comboOsPlatform.SelectedIndex;
        lm.WriteOsPlatform();
    }

    private void CheckGKey_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.GKeyDivert = checkGKey.IsChecked == true;
        lm.WriteGKeyDivert();
    }

    private void SliderMKey_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.MKeyLeds = (int)sliderMKey.Value;
        lm.WriteMKeyLeds();
    }

    private void CheckMrKey_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.MrKeyLed = checkMrKey.IsChecked == true;
        lm.WriteMrKeyLed();
    }

    // Headset handlers

    private void SliderSidetone_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelSidetoneValue.Text = ((int)sliderSidetone.Value).ToString();
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetSidetone = (int)sliderSidetone.Value;
        lm.WriteHeadsetSidetone();
    }

    private void SliderMicGain_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelMicGainValue.Text = ((int)sliderMicGain.Value).ToString();
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetMicGain = (int)sliderMicGain.Value;
        lm.WriteHeadsetMicGain();
    }

    private void CheckMicMute_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetMicMute = checkMicMute.IsChecked == true;
        lm.WriteHeadsetMicMute();
    }

    private void CheckMicSnr_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetMicSnrEnabled = checkMicSnr.IsChecked == true;
        lm.WriteHeadsetMicSnr();
    }

    private void CheckAiNoise_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetAiNoise = checkAiNoise.IsChecked == true;
        lm.WriteHeadsetAiNoise();
    }

    private void SliderAiNoiseLevel_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelAiNoiseLevel.Text = ((int)sliderAiNoiseLevel.Value).ToString();
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetAiNoiseLevel = (int)sliderAiNoiseLevel.Value;
        lm.WriteHeadsetAiNoise();
    }

    private void CheckDoNotDisturb_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetDoNotDisturb = checkDoNotDisturb.IsChecked == true;
        lm.WriteHeadsetDoNotDisturb();
    }

    private void CheckEcoMode_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetEcoMode = checkEcoMode.IsChecked == true;
        lm.WriteHeadsetEcoMode();
    }

    private void SliderAudioMix_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelAudioMix.Text = ((int)sliderAudioMix.Value).ToString();
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetAudioMix = (int)sliderAudioMix.Value;
        lm.WriteHeadsetAudioMix();
    }

    private void ComboHeadsetEq_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm || comboHeadsetEq.SelectedIndex < 0)
            return;
        lm.HeadsetEqIndex = comboHeadsetEq.SelectedIndex;
        lm.WriteHeadsetEq();
    }

    private void ComboEqPreset_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm || comboEqPreset.SelectedIndex < 0)
            return;
        lm.HeadsetEqPresetIndex = comboEqPreset.SelectedIndex;
        // ponytail: preset writes via the same HeadsetEq feature with the index value
        lm.WriteHeadsetEq();
    }

    private void CheckAdvancedEq_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetAdvancedEq = checkAdvancedEq.IsChecked == true;
        lm.WriteHeadsetAdvancedEq();
    }

    private void SliderAutoSleep_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelAutoSleep.Text = ((int)sliderAutoSleep.Value).ToString();
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetAutoSleepMinutes = (int)sliderAutoSleep.Value;
        lm.WriteHeadsetAutoSleep();
    }

    private void CheckPowerMgmt_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetPowerMgmt = checkPowerMgmt.IsChecked == true;
        lm.WriteHeadsetPowerMgmt();
    }

    private void ComboOnboardEffect_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm || comboOnboardEffect.SelectedIndex < 0)
            return;
        lm.HeadsetOnboardEffectIndex = comboOnboardEffect.SelectedIndex;
        lm.WriteHeadsetOnboardEffect();
    }

    private void CheckPerZoneLighting_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is not LogitechMouse lm)
            return;
        lm.HeadsetPerZoneLighting = checkPerZoneLighting.IsChecked == true;
        lm.WriteHeadsetPerZoneLighting();
    }

    // Deferred-set handlers (Sensitivity, Idle, Sleep, Haptic Waveform,
    // Backlight Delays, Hand Detection, Side Scrolling, LowresMode)

    private void CheckSensitivitySwitch_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;
        _mouse.SensitivitySwitch = checkSensitivitySwitch.IsChecked == true;
        _mouse.WriteSensitivitySwitch();
    }

    private void ComboIdleEffect_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboIdleEffect.SelectedIndex < 0)
            return;
        _mouse.IdleEffectIndex = comboIdleEffect.SelectedIndex;
        _mouse.WriteIdleEffect();
    }

    private void SliderIdleTimeout_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelIdleTimeout.Text = ((int)sliderIdleTimeout.Value).ToString();
        if (_loading || _mouse is null)
            return;
        _mouse.IdleTimeoutSeconds = (int)sliderIdleTimeout.Value;
        _mouse.WriteIdleTimeout();
    }

    private void SliderSleepTimeout_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelSleepTimeout.Text = ((int)sliderSleepTimeout.Value).ToString();
        if (_loading || _mouse is null)
            return;
        _mouse.SleepTimeoutSeconds = (int)sliderSleepTimeout.Value;
        _mouse.WriteSleepTimeout();
    }

    private void ComboHapticWaveform_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboHapticWaveform.SelectedIndex < 0)
            return;
        _mouse.HapticWaveformIndex = comboHapticWaveform.SelectedIndex + 1;
    }

    private void ButtonHapticPlay_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _mouse?.PlayHapticWaveform();
    }

    private void SliderBacklightDelayHandsIn_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelBacklightDelayHandsIn.Text = ((int)sliderBacklightDelayHandsIn.Value).ToString();
        if (_loading || _mouse is null)
            return;
        _mouse.BacklightDelayHandsIn = (int)sliderBacklightDelayHandsIn.Value;
        _mouse.WriteBacklightDelays();
    }

    private void SliderBacklightDelayHandsOut_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelBacklightDelayHandsOut.Text = ((int)sliderBacklightDelayHandsOut.Value).ToString();
        if (_loading || _mouse is null)
            return;
        _mouse.BacklightDelayHandsOut = (int)sliderBacklightDelayHandsOut.Value;
        _mouse.WriteBacklightDelays();
    }

    private void SliderBacklightDelayPowered_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        labelBacklightDelayPowered.Text = ((int)sliderBacklightDelayPowered.Value).ToString();
        if (_loading || _mouse is null)
            return;
        _mouse.BacklightDelayPowered = (int)sliderBacklightDelayPowered.Value;
        _mouse.WriteBacklightDelays();
    }

    private void CheckHandDetection_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;
        _mouse.HandDetection = checkHandDetection.IsChecked == true;
        _mouse.WriteHandDetection();
    }

    private void CheckSideScrolling_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _mouse is null)
            return;
        _mouse.SideScrolling = checkSideScrolling.IsChecked == true;
        _mouse.WriteSideScrolling();
    }

    private void ComboLowresMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mouse is null || comboLowresMode.SelectedIndex < 0)
            return;
        _mouse.LowresModeIndex = comboLowresMode.SelectedIndex;
        _mouse.WriteLowresMode();
    }

    // Display name helpers

    private static string PollingRateName(PollingRate rate) => rate switch
    {
        PollingRate.PR125Hz => "125 Hz",
        PollingRate.PR250Hz => "250 Hz",
        PollingRate.PR500Hz => "500 Hz",
        PollingRate.PR1000Hz => "1000 Hz",
        PollingRate.PR2000Hz => "2000 Hz",
        PollingRate.PR4000Hz => "4000 Hz",
        PollingRate.PR8000Hz => "8000 Hz",
        PollingRate.PR16000Hz => "16000 Hz",
        _ => rate.ToString(),
    };

    private static string DebounceName(DebounceTime time) => time switch
    {
        DebounceTime.OFF => "Off",
        DebounceTime.MS8 => "8 ms",
        DebounceTime.MS12 => "12 ms",
        DebounceTime.MS16 => "16 ms",
        DebounceTime.MS20 => "20 ms",
        DebounceTime.MS24 => "24 ms",
        DebounceTime.MS28 => "28 ms",
        DebounceTime.MS32 => "32 ms",
        _ => time.ToString(),
    };

    private static string PowerOffName(PowerOffSetting setting) => setting switch
    {
        PowerOffSetting.Minutes1 => Labels.Format("mouse_minutes_fmt", 1),
        PowerOffSetting.Minutes2 => Labels.Format("mouse_minutes_fmt", 2),
        PowerOffSetting.Minutes3 => Labels.Format("mouse_minutes_fmt", 3),
        PowerOffSetting.Minutes5 => Labels.Format("mouse_minutes_fmt", 5),
        PowerOffSetting.Minutes10 => Labels.Format("mouse_minutes_fmt", 10),
        PowerOffSetting.Never => Labels.Get("mouse_never"),
        _ => setting.ToString(),
    };

    private static string LightingModeName(LightingMode mode) => mode switch
    {
        LightingMode.Off => Labels.Get("mouse_lightmode_off"),
        LightingMode.Static => Labels.Get("mouse_lightmode_static"),
        LightingMode.Breathing => Labels.Get("mouse_lightmode_breathing"),
        LightingMode.ColorCycle => Labels.Get("mouse_lightmode_color_cycle"),
        LightingMode.Rainbow => Labels.Get("mouse_lightmode_rainbow"),
        LightingMode.React => Labels.Get("mouse_lightmode_react"),
        LightingMode.Comet => Labels.Get("mouse_lightmode_comet"),
        LightingMode.BatteryState => Labels.Get("mouse_lightmode_battery"),
        _ => mode.ToString(),
    };

    private static string ZoneName(LightingZone zone) => zone switch
    {
        LightingZone.Logo => Labels.Get("mouse_zone_logo"),
        LightingZone.Scrollwheel => Labels.Get("mouse_zone_scrollwheel"),
        LightingZone.Underglow => Labels.Get("mouse_zone_underglow"),
        LightingZone.All => Labels.Get("mouse_zone_all"),
        LightingZone.Dock => Labels.Get("mouse_zone_dock"),
        _ => zone.ToString(),
    };
}

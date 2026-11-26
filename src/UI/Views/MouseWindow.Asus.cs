using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Peripherals;
using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// ASUS mouse part of the mouse window. Follows Windows G-Helper AsusMouseSettings.cs:
/// the same checks, the same calls on the mouse, and sliders write when released.
/// </summary>
public partial class MouseWindow
{
    private static readonly PollingRate[] ZoneModePollingRates =
        [PollingRate.PR1000Hz, PollingRate.PR2000Hz, PollingRate.PR4000Hz, PollingRate.PR8000Hz];

    private AsusMouse? _asus;
    private LightingZone _visibleZone = LightingZone.All;
    private LightingZone[] _asusZones = [LightingZone.All];
    private readonly List<LightingMode> _supportedLightingModes = new();
    private DebounceTime[] _asusDebounce = [];
    private bool _shiftLayer;
    private bool _updatingBindings;
    private readonly List<ComboBox> _bindingCombos = new();
    private readonly List<ushort?> _bindingCodes = new();

    private void InitAsusHandlers()
    {
        OnRelease(sliderDpi, () => Asus(UpdateMouseDPISettings));
        OnRelease(sliderAngleTuning, () => Asus(am => am.SetAngleAdjustment((short)sliderAngleTuning.Value)));
        OnRelease(sliderAcceleration, () => Asus(am => am.SetAcceleration((int)sliderAcceleration.Value)));
        OnRelease(sliderDeceleration, () => Asus(am => am.SetDeceleration((int)sliderDeceleration.Value)));
        OnRelease(sliderZoneModeDpi, () => Asus(am =>
        {
            am.ZoneModeDPI = (int)sliderZoneModeDpi.Value;
            if (am.ZoneMode)
                am.UpdateZoneModeDPI(am.ZoneModeDPI);
        }));
        OnRelease(sliderLowBattery, () => Asus(am => am.SetEnergySettings((int)sliderLowBattery.Value, am.PowerOffSetting)));
        OnRelease(sliderBrightness, () => Asus(am =>
        {
            LightingSetting ls = am.LightingSettingForZone(_visibleZone);
            ls.Brightness = (int)sliderBrightness.Value;
            UpdateLightingSettings(am, ls);
        }));
        Closing += (_, _) => AttachAsus(null);
    }

    // Windows writes on MouseUp and KeyUp, not on every step of a drag.
    private static void OnRelease(Slider slider, Action write)
    {
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => write(), RoutingStrategies.Bubble, handledEventsToo: true);
        slider.KeyUp += (_, _) => write();
    }

    private void Asus(Action<AsusMouse> action)
    {
        if (!_loading && _mouse is AsusMouse am)
            action(am);
    }

    private void ApplyAsusLabels()
    {
        lblAsusProfile.Text = Labels.Get("mouse_profile");
        buttonAsusSync.Content = Labels.Get("mouse_synchronize");
        buttonAsusImport.Content = Labels.Get("mouse_import");
        buttonAsusExport.Content = Labels.Get("mouse_export");
        lblDpiColor.Text = Labels.Get("mouse_color");
        buttonDpiColor.Content = Labels.Get("mouse_pick_color");
        buttonDpiAdd.Content = Labels.Get("mouse_dpi_add");
        buttonDpiRemove.Content = Labels.Get("mouse_dpi_remove");
        lblAngleTuning.Text = Labels.Get("mouse_angle_tuning");
        lblAcceleration.Text = Labels.Get("mouse_acceleration");
        lblDeceleration.Text = Labels.Get("mouse_deceleration");
        lblMotionSync.Text = Labels.Get("mouse_motion_sync");
        lblZoneMode.Text = Labels.Get("mouse_zone_mode");
        lblZoneModeDpi.Text = Labels.Get("mouse_section_dpi");
        lblZoneModePolling.Text = Labels.Get("mouse_polling_rate");
        lblRandomColor.Text = Labels.Get("aura_random_color");
        lblAnimationSpeed.Text = Labels.Get("mouse_animation_speed");
        lblAnimationDirection.Text = Labels.Get("mouse_animation_direction");
        lblSyncAura.Text = Labels.Get("mouse_sync_aura");
        buttonBindingsNormal.Content = Labels.Get("mouse_section_buttons");
        buttonBindingsReset.Content = Labels.Get("reset");
    }

    /// <summary>Shows the ASUS-only rows for an ASUS mouse and hides them for any other.</summary>
    private void ShowAsus(AsusMouse? am)
    {
        AttachAsus(am);
        bool asus = am is not null;
        rowAsusProfile.IsVisible = asus && am!.HasProfiles();
        rowAsusActions.IsVisible = asus && am!.HasProfiles();
        rowDpiColor.IsVisible = asus && am!.HasDPIColors();
        rowDpiCount.IsVisible = asus && am!.CanChangeDPICount();
        rowAngleTuning.IsVisible = asus && am!.HasAngleTuning();
        rowAcceleration.IsVisible = asus && am!.HasAcceleration();
        rowDeceleration.IsVisible = asus && am!.HasDeceleration();
        rowMotionSync.IsVisible = asus && am!.HasMotionSync();
        rowZoneMode.IsVisible = rowZoneModeDpi.IsVisible = rowZoneModePolling.IsVisible = asus && am!.HasZoneMode();
        rowRandomColor.IsVisible = rowAnimationSpeed.IsVisible = rowAnimationDirection.IsVisible = rowSyncAura.IsVisible = asus;
        rowBindingLayer.IsVisible = buttonBindingsReset.IsVisible = asus;
        rowPollingRate.IsVisible = rowColor.IsVisible = true;
        if (am is null)
            return;

        panelDpi.IsVisible = am.DPIProfileCount() > 0;
        InitMouseCapabilities(am);
        VisualizeMouseSettings(am);
    }

    private void AttachAsus(AsusMouse? am)
    {
        if (ReferenceEquals(_asus, am))
            return;
        if (_asus is not null)
        {
            _asus.StopEventListener();
            _asus.ProfileChanged -= Mouse_ProfileChanged;
            _asus.BatteryUpdated -= Mouse_BatteryUpdated;
            _asus.MouseReadyChanged -= Mouse_MouseReadyChanged;
        }
        _asus = am;
        _shiftLayer = false;
        _visibleZone = LightingZone.All;
        if (am is null)
            return;

        am.ProfileChanged += Mouse_ProfileChanged;
        am.BatteryUpdated += Mouse_BatteryUpdated;
        am.MouseReadyChanged += Mouse_MouseReadyChanged;
        am.StartEventListener();
        RefreshMouseData(am);
    }

    // Windows reads the whole mouse each time its window opens.
    private void RefreshMouseData(AsusMouse am)
    {
        Task.Run(() =>
        {
            am.SynchronizeDevice();
            Logger.WriteLine(am.GetDisplayName() + " (GUI): Mouse data synchronized");
            Dispatcher.UIThread.Post(Refresh);
        });
    }

    private void Mouse_ProfileChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
    private void Mouse_BatteryUpdated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_asus is { } am)
            labelBattery.Text = BatteryText(am);
    });
    private void Mouse_MouseReadyChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void InitMouseCapabilities(AsusMouse am)
    {
        FillCombo(comboAsusProfile, Enumerable.Range(1, am.ProfileCount()).Select(i => Labels.Format("mouse_profile_fmt", i)), am.Profile);

        sliderDpi.Minimum = am.MinDPI();
        sliderDpi.Maximum = am.MaxDPI();
        sliderDpi.TickFrequency = am.DPIIncrements();
        sliderZoneModeDpi.Maximum = am.MaxDPI();
        sliderZoneModeDpi.TickFrequency = am.DPIIncrements();

        rowPollingRate.IsVisible = am.CanSetPollingRate();
        _pollingRates = am.SupportedPollingrates();
        FillCombo(comboPollingRate, _pollingRates.Select(am.PollingRateDisplayString), Math.Max(0, am.PollingRateIndex(am.PollingRate)));

        if (am.HasAngleTuning())
        {
            sliderAngleTuning.Minimum = am.AngleTuningMin();
            sliderAngleTuning.Maximum = am.AngleTuningMax();
            sliderAngleTuning.TickFrequency = am.AngleTuningStep();
        }

        rowDebounce.IsVisible = am.HasDebounceSetting();
        _asusDebounce = Enum.GetValues<DebounceTime>().Where(d => d >= am.MinDebounce() && d <= am.MaxDebounce()).ToArray();
        FillCombo(comboDebounce, _asusDebounce.Select(d => am.DebounceTimeInMS(d) + " ms"), Math.Max(0, Array.IndexOf(_asusDebounce, am.Debounce)));

        rowLiftOff.IsVisible = am.HasLiftOffSetting();
        sliderAcceleration.Maximum = am.MaxAcceleration();
        sliderDeceleration.Maximum = am.MaxDeceleration();
        FillCombo(comboZoneModePolling, ZoneModePollingRates.Select(am.PollingRateDisplayString), 2);

        sliderLowBattery.Minimum = 0;
        sliderLowBattery.TickFrequency = am.LowBatteryWarningStep();
        sliderLowBattery.Maximum = am.LowBatteryWarningMax();

        panelLighting.IsVisible = am.HasRGB();
        if (am.HasRGB())
        {
            var zones = am.SupportedLightingZones();
            _asusZones = zones.Length > 1 ? [LightingZone.All, .. zones] : [LightingZone.All];
            rowZone.IsVisible = zones.Length > 1;
            checkSyncAura.IsChecked = PeripheralsProvider.IsAuraSync;
            ApplyAuraSyncUIState();
            sliderBrightness.Maximum = am.MaxBrightness();
            FillCombo(comboAnimationDirection, [Labels.Get("mouse_clockwise"), Labels.Get("mouse_counter_clockwise")], 0);
            FillCombo(comboAnimationSpeed, [Labels.Get("speed_slow"), Labels.Get("speed_normal"), Labels.Get("speed_fast")], 1);
        }

        panelButtons.IsVisible = am.HasButtonBindings() && am.ButtonBindingsReady;
        if (panelButtons.IsVisible)
            InitBindingCombos(am);
    }

    private void InitLightingModes(AsusMouse am)
    {
        _supportedLightingModes.Clear();
        foreach (LightingMode lm in Enum.GetValues<LightingMode>())
        {
            if (am.IsLightingModeSupported(lm) && am.IsLightingModeSupportedForZone(lm, _visibleZone))
                _supportedLightingModes.Add(lm);
        }
        FillCombo(comboLightingMode, _supportedLightingModes.Select(LightingModeName), 0);
    }

    private void VisualizeMouseSettings(AsusMouse am)
    {
        if (am.HasRGB() && am.IsLightingZoned() && _visibleZone == LightingZone.All)
            _visibleZone = am.SupportedLightingZones()[0];

        VisualizeCurrentDPIProfile(am);
        VisusalizeLightingSettings(am);

        if (am.HasAngleTuning())
        {
            sliderAngleTuning.Value = am.AngleAdjustmentDegrees;
            labelAngleTuning.Text = am.AngleAdjustmentDegrees + "\u00B0";
        }
        if (am.HasLowBatteryWarning())
            sliderLowBattery.Value = am.LowBatteryWarning;
        if (am.HasAcceleration())
            sliderAcceleration.Value = am.Acceleration;
        if (am.HasDeceleration())
            sliderDeceleration.Value = am.Deceleration;
        if (am.HasMotionSync())
            UpdateMotionSyncState(am);
        if (am.HasZoneMode())
        {
            checkZoneMode.IsChecked = am.ZoneMode;
            sliderZoneModeDpi.Value = am.ZoneModeDPI;
            int prIndex = Array.IndexOf(ZoneModePollingRates, am.ZoneModePollingRate);
            if (prIndex >= 0)
                comboZoneModePolling.SelectedIndex = prIndex;
            UpdateZoneModeUIState(am);
        }
        if (am.HasButtonBindings())
            VisualizeButtonBindings(am);
    }

    private void UpdateMotionSyncState(AsusMouse am)
    {
        bool is8000Hz = am.PollingRate == PollingRate.PR8000Hz;
        checkMotionSync.IsEnabled = !is8000Hz;
        checkMotionSync.IsChecked = !is8000Hz && am.MotionSync;
    }

    private void UpdateZoneModeUIState(AsusMouse am)
    {
        bool zoneModeEnabled = am.ZoneMode;
        sliderZoneModeDpi.IsEnabled = zoneModeEnabled;
        comboZoneModePolling.IsEnabled = zoneModeEnabled;
        comboDpiProfile.IsEnabled = sliderDpi.IsEnabled = !zoneModeEnabled;
        rowDpiColor.IsEnabled = rowDpiCount.IsEnabled = !zoneModeEnabled;
        comboPollingRate.IsEnabled = !zoneModeEnabled;
    }

    // DPI: the DPI combo lists the DPI levels; Windows counts them from 1.
    private void VisualizeCurrentDPIProfile(AsusMouse am)
    {
        if (am.DpiSettings.Length == 0)
            return;
        int count = Math.Clamp(am.CurrentDPIProfileCount, 1, am.DpiSettings.Length);
        FillCombo(comboDpiProfile, Enumerable.Range(0, count).Select(i => $"DPI {i + 1}: {am.DpiSettings[i]?.DPI}"), am.DpiProfile - 1);
        buttonDpiAdd.IsEnabled = am.CurrentDPIProfileCount < 4;
        buttonDpiRemove.IsEnabled = am.CurrentDPIProfileCount > 2;

        if (CurrentDpi(am) is not { } dpi)
        {
            Logger.WriteLine($"Wrong mouse DPI: {am.DpiProfile}");
            return;
        }
        sliderDpi.Value = dpi.DPI;
        labelDpiValue.Text = dpi.DPI.ToString();
        borderDpiColor.Background = new SolidColorBrush(Color.FromRgb(dpi.Color.R, dpi.Color.G, dpi.Color.B));
    }

    private static AsusMouseDPI? CurrentDpi(AsusMouse am) =>
        am.DpiProfile >= 1 && am.DpiProfile <= am.DpiSettings.Length ? am.DpiSettings[am.DpiProfile - 1] : null;

    private void UpdateMouseDPISettings(AsusMouse am)
    {
        if (CurrentDpi(am) is not { } dpi)
            return;
        dpi.DPI = (uint)sliderDpi.Value;
        am.SetDPIForProfile(dpi, am.DpiProfile);
        RedrawAsus(() => VisualizeCurrentDPIProfile(am));
    }

    private void AsusDpiProfile_Changed(AsusMouse am)
    {
        am.SetDPIProfile(comboDpiProfile.SelectedIndex + 1);
        RedrawAsus(() => VisualizeCurrentDPIProfile(am));
    }

    private void RedrawAsus(Action redraw)
    {
        _loading = true;
        redraw();
        _loading = false;
    }

    private void ButtonDpiColor_Click(object? sender, RoutedEventArgs e) => Asus(am =>
    {
        if (CurrentDpi(am) is not { } dpi)
            return;
        ColorPicker.Show(this, dpi.Color.R, dpi.Color.G, dpi.Color.B, (r, g, b) =>
        {
            dpi.Color = System.Drawing.Color.FromArgb(r, g, b);
            am.SetDPIForProfile(dpi, am.DpiProfile);
            RedrawAsus(() => VisualizeCurrentDPIProfile(am));
        });
    });

    private void ButtonDpiAdd_Click(object? sender, RoutedEventArgs e) => Asus(am =>
    {
        am.AddDPIProfile();
        RedrawAsus(() => VisualizeCurrentDPIProfile(am));
    });

    private void ButtonDpiRemove_Click(object? sender, RoutedEventArgs e) => Asus(am =>
    {
        am.DeleteDPIProfile(am.DpiProfile - 1);
        RedrawAsus(() => VisualizeCurrentDPIProfile(am));
    });

    private void ComboAsusProfile_Changed(object? sender, SelectionChangedEventArgs e) => Asus(am =>
    {
        if (comboAsusProfile.SelectedIndex < 0 || am.Profile == comboAsusProfile.SelectedIndex)
            return;
        am.SetProfile(comboAsusProfile.SelectedIndex);
        RefreshMouseData(am);
    });

    private void AsusPollingRate_Changed(AsusMouse am)
    {
        am.SetPollingRate(_pollingRates[comboPollingRate.SelectedIndex]);
        RedrawAsus(() => UpdateMotionSyncState(am));
    }

    private void AsusDebounce_Changed(AsusMouse am) => am.SetDebounce(_asusDebounce[comboDebounce.SelectedIndex]);

    private void SliderAngleTuning_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => labelAngleTuning.Text = (int)sliderAngleTuning.Value + "\u00B0";

    private void SliderAcceleration_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => labelAcceleration.Text = ((int)sliderAcceleration.Value).ToString();

    private void SliderDeceleration_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => labelDeceleration.Text = ((int)sliderDeceleration.Value).ToString();

    private void SliderZoneModeDpi_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => labelZoneModeDpi.Text = ((int)sliderZoneModeDpi.Value).ToString();

    private void CheckMotionSync_Changed(object? sender, RoutedEventArgs e)
        => Asus(am => am.SetMotionSync(checkMotionSync.IsChecked == true));

    private void CheckZoneMode_Changed(object? sender, RoutedEventArgs e) => Asus(am =>
    {
        am.SetZoneMode(checkZoneMode.IsChecked == true);
        UpdateZoneModeUIState(am);
    });

    private void ComboZoneModePolling_Changed(object? sender, SelectionChangedEventArgs e) => Asus(am =>
    {
        if (comboZoneModePolling.SelectedIndex < 0 || comboZoneModePolling.SelectedIndex >= ZoneModePollingRates.Length)
            return;
        am.ZoneModePollingRate = ZoneModePollingRates[comboZoneModePolling.SelectedIndex];
        if (am.ZoneMode)
            am.UpdateZoneModePollingRate(am.ZoneModePollingRate);
    });

    // Lighting: "All" first, then the zones; Windows reads "All" from the first zone.
    private void AsusLightingZone_Changed(AsusMouse am)
    {
        _visibleZone = _asusZones[comboLightingZone.SelectedIndex];
        RedrawAsus(() => VisusalizeLightingSettings(am));
    }

    private void AsusLightingMode_Changed(AsusMouse am)
    {
        int index = comboLightingMode.SelectedIndex;
        LightingMode lm = _supportedLightingModes[index >= 0 && index < _supportedLightingModes.Count ? index : 0];
        LightingSetting ls = am.LightingSettingForZone(_visibleZone);
        if (ls.LightingMode == lm)
            return;
        ls.LightingMode = lm;
        UpdateLightingSettings(am, ls);
    }

    private void AsusLightingColor_Click(AsusMouse am)
    {
        LightingSetting ls = am.LightingSettingForZone(_visibleZone);
        ColorPicker.Show(this, ls.RGBColor.R, ls.RGBColor.G, ls.RGBColor.B, (r, g, b) =>
        {
            ls.RGBColor = System.Drawing.Color.FromArgb(r, g, b);
            UpdateLightingSettings(am, ls);
        });
    }

    private void UpdateLightingSettings(AsusMouse am, LightingSetting ls)
    {
        am.SetLightingSetting(ls, _visibleZone);
        RedrawAsus(() => VisusalizeLightingSettings(am));
    }

    private void CheckRandomColor_Changed(object? sender, RoutedEventArgs e) => Asus(am =>
    {
        LightingSetting ls = am.LightingSettingForZone(_visibleZone);
        ls.RandomColor = checkRandomColor.IsChecked == true;
        UpdateLightingSettings(am, ls);
    });

    private void ComboAnimationSpeed_Changed(object? sender, SelectionChangedEventArgs e) => Asus(am =>
    {
        if (comboAnimationSpeed.SelectedIndex < 0)
            return;
        LightingSetting ls = am.LightingSettingForZone(_visibleZone);
        // 0 => 0x9, 1 => 0x7, 2 => 0x5
        ls.AnimationSpeed = (AnimationSpeed)(0x9 - (comboAnimationSpeed.SelectedIndex * 0x2));
        UpdateLightingSettings(am, ls);
    });

    private void ComboAnimationDirection_Changed(object? sender, SelectionChangedEventArgs e) => Asus(am =>
    {
        if (comboAnimationDirection.SelectedIndex < 0)
            return;
        LightingSetting ls = am.LightingSettingForZone(_visibleZone);
        ls.AnimationDirection = (AnimationDirection)comboAnimationDirection.SelectedIndex;
        UpdateLightingSettings(am, ls);
    });

    private void CheckSyncAura_Changed(object? sender, RoutedEventArgs e)
    {
        ApplyAuraSyncUIState();
        Asus(am =>
        {
            PeripheralsProvider.IsAuraSync = checkSyncAura.IsChecked == true;
            if (checkSyncAura.IsChecked == true)
            {
                am.SyncFromKeyboardAura();
                RedrawAsus(() => VisusalizeLightingSettings(am));
            }
        });
    }

    private void ApplyAuraSyncUIState()
    {
        bool syncOn = checkSyncAura.IsChecked == true;
        comboLightingMode.IsEnabled = !syncOn;
        buttonColor.IsEnabled = !syncOn;
        checkRandomColor.IsEnabled = !syncOn;
        comboAnimationSpeed.IsEnabled = !syncOn;
        comboAnimationDirection.IsEnabled = !syncOn;
    }

    private void VisusalizeLightingSettings(AsusMouse am)
    {
        if (!am.HasRGB())
            return;

        InitLightingModes(am);
        comboLightingZone.Items.Clear();
        foreach (var zone in _asusZones)
            comboLightingZone.Items.Add(new ComboBoxItem { Content = ZoneName(zone) });
        comboLightingZone.SelectedIndex = Math.Max(0, Array.IndexOf(_asusZones, _visibleZone));

        LightingSetting? ls = am.LightingSettingForZone(_visibleZone);
        if (ls is null)
            return;

        sliderBrightness.Value = ls.Brightness;
        labelBrightness.Text = ls.Brightness.ToString();
        rowRandomColor.IsVisible = am.SupportsRandomColor(ls.LightingMode);
        rowColor.IsVisible = am.SupportsColorSetting(ls.LightingMode);
        rowAnimationSpeed.IsVisible = am.SupportsAnimationSpeed(ls.LightingMode);
        rowAnimationDirection.IsVisible = am.SupportsAnimationDirection(ls.LightingMode);
        comboLightingMode.SelectedIndex = _supportedLightingModes.IndexOf(ls.LightingMode);

        if (am.SupportsRandomColor(ls.LightingMode))
        {
            checkRandomColor.IsChecked = ls.RandomColor;
            rowColor.IsVisible = !ls.RandomColor;
        }

        borderColor.Background = new SolidColorBrush(Color.FromRgb(ls.RGBColor.R, ls.RGBColor.G, ls.RGBColor.B));
        comboAnimationSpeed.SelectedIndex = Math.Clamp(2 - ((((int)ls.AnimationSpeed) - 5) / 2), 0, 2);
        int directionIndex = (int)ls.AnimationDirection;
        comboAnimationDirection.SelectedIndex = directionIndex >= 0 && directionIndex < comboAnimationDirection.ItemCount ? directionIndex : 0;
    }

    // Button bindings: one combo per button, the binding groups as headers.
    private void InitBindingCombos(AsusMouse am)
    {
        stackButtons.Children.Clear();
        _bindingCombos.Clear();
        _bindingCodes.Clear();
        rowBindingLayer.IsVisible = am.HasSpeedShift;

        var items = new List<ComboBoxItem>();
        foreach (var (groupLabel, groupItems) in am.BindingGroups)
        {
            items.Add(new ComboBoxItem { Content = groupLabel, IsEnabled = false, FontWeight = FontWeight.Bold });
            _bindingCodes.Add(null);
            foreach (var (code, name) in groupItems)
            {
                items.Add(new ComboBoxItem { Content = name });
                _bindingCodes.Add(code);
            }
        }

        foreach (var (slot, (_, name)) in am.ButtonSlots)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), Margin = new Thickness(0, 2) };
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            label.Classes.Add("label-dim");
            row.Children.Add(label);

            var combo = new ComboBox { Name = "comboAsusBinding", Tag = slot, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var item in items)
                combo.Items.Add(new ComboBoxItem { Content = item.Content, IsEnabled = item.IsEnabled, FontWeight = item.FontWeight });
            combo.SelectionChanged += BindingCombo_Changed;
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            stackButtons.Children.Add(row);
            _bindingCombos.Add(combo);
        }
        SetBindingLayer(am, _shiftLayer);
    }

    private void ButtonBindingsNormal_Click(object? sender, RoutedEventArgs e) => Asus(am => SetBindingLayer(am, false));

    private void ButtonBindingsShift_Click(object? sender, RoutedEventArgs e) => Asus(am => SetBindingLayer(am, true));

    private void SetBindingLayer(AsusMouse am, bool shift)
    {
        _shiftLayer = shift;
        buttonBindingsNormal.Classes.Set("ghelper-active", !shift);
        buttonBindingsShift.Classes.Set("ghelper-active", shift);
        VisualizeButtonBindings(am);
    }

    private void ButtonBindingsReset_Click(object? sender, RoutedEventArgs e) => Asus(am =>
    {
        am.ResetButtonBindings();
        VisualizeButtonBindings(am);
        Input.MouseCombos.RegisterKeys();
    });

    private void BindingCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingBindings || sender is not ComboBox { Tag: int slot } combo || combo.SelectedIndex < 0)
            return;
        Asus(am =>
        {
            if (_bindingCodes[combo.SelectedIndex] is not { } code)
                return;
            if (_shiftLayer)
            {
                if (code == AsusMouse.SpeedShiftCode)
                    VisualizeButtonBindings(am);
                else
                    am.SetSpeedShiftBinding(slot, code);
                return;
            }
            am.SetButtonBinding(slot, code);
            Input.MouseCombos.RegisterKeys();
        });
    }

    private void VisualizeButtonBindings(AsusMouse am)
    {
        _updatingBindings = true;
        int row = 0;
        foreach (var slot in am.ButtonSlots.Keys)
        {
            if (row >= _bindingCombos.Count)
                break;
            var combo = _bindingCombos[row];
            bool locked = _shiftLayer && am.ButtonBindings[slot] == AsusMouse.SpeedShiftCode;
            ushort code = _shiftLayer && !locked ? am.GetSpeedShiftBinding(slot) : am.ButtonBindings[slot];
            combo.IsEnabled = !locked;
            combo.SelectedIndex = _bindingCodes.IndexOf(code);
            row++;
        }
        _updatingBindings = false;
    }

    // Profile files use the Windows format (*.gmp1).
    private static readonly FilePickerFileType MouseProfileType = new("G-Helper Mouse Profile V1") { Patterns = ["*.gmp1"] };

    private void ButtonAsusSync_Click(object? sender, RoutedEventArgs e) => Asus(RefreshMouseData);

    private async void ButtonAsusImport_Click(object? sender, RoutedEventArgs e)
    {
        if (_mouse is not AsusMouse am)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [MouseProfileType] });
        if (files.Count == 0)
            return;
        byte[] data = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
        if (!am.Import(data))
        {
            Logger.WriteLine("Failed to import mouse profile");
            labelDevice.Text = Labels.Get("mouse_import_failed");
            return;
        }
        if (!am.IsLightingZoned())
            _visibleZone = LightingZone.All;
        RefreshMouseData(am);
    }

    private async void ButtonAsusExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_mouse is not AsusMouse am)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { DefaultExtension = "gmp1", FileTypeChoices = [MouseProfileType] });
        if (file is not null)
            await File.WriteAllBytesAsync(file.Path.LocalPath, am.Export());
    }
}

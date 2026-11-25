using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Input;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Standalone window for configuring the software fn-lock remapper. Opens
/// from the Extra window's Key Bindings section. Provides hotkey settings,
/// device picker, per-key map, and a debug logging toggle.
///
/// The master enable/disable toggle and the on/off state of the remapper
/// are owned by the MainWindow's FN-Lock title-row button. This window only
/// configures behavior; it does NOT have its own enable checkbox.
/// </summary>
public partial class FnLockWindow : Window
{
    private bool _suppressEvents = true;

    public FnLockWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _suppressEvents = true;
            ApplyLabels();
            _suppressEvents = false;
        });

        Loaded += (_, _) =>
        {
            _suppressEvents = true;
            InitAll();
            ApplyLabels();
            _suppressEvents = false;
        };
    }

    private void InitAll()
    {
        InitHotkeyCombos();
        BuildDeviceRows();
        BuildMapRows();
        checkDebug.IsChecked = AppConfig.Is("fnlock_debug");
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("fnlock_window_title");
        headerBehavior.Text = Labels.Get("fnlock_behavior_section");
        labelHotkey.Text = Labels.Get("fnlock_toggle_hotkey");
        labelHotkeyHint.Text = Labels.Get("fnlock_hotkey_hint");
        headerDevices.Text = Labels.Get("fnlock_devices_header");
        labelDevicesIntro.Text = Labels.Get("fnlock_devices_intro");
        labelRescan.Text = Labels.Get("fnlock_rescan_short");
        headerMap.Text = Labels.Get("fnlock_map_header");
        labelMapIntro.Text = Labels.Get("fnlock_map_intro");
        labelReset.Text = Labels.Get("fnlock_reset_defaults");
        headerAdvanced.Text = Labels.Get("fnlock_advanced_section");
        checkDebug.Content = Labels.Get("fnlock_debug_label");
        labelDebugHint.Text = Labels.Get("fnlock_debug_hint");
        RefreshComboLabels();
        // Rebuild the per-key map combos to pick up the new translations of
        // the FnLockTarget display names (Volume Down → Lautstärke verringern, etc.).
        BuildMapRows();
    }

    // HOTKEY

    private void InitHotkeyCombos()
    {
        comboModifier.Items.Clear();
        comboModifier.Items.Add(new ComboBoxItem
        {
            Content = Labels.Get("fnlock_modifier_super"),
            Tag = (int)EvdevInterop.KEY_LEFTMETA,
        });
        comboModifier.Items.Add(new ComboBoxItem
        {
            Content = Labels.Get("fnlock_modifier_ctrl"),
            Tag = 29,
        });
        comboModifier.Items.Add(new ComboBoxItem
        {
            Content = Labels.Get("fnlock_modifier_alt"),
            Tag = 56,
        });
        int savedMod = AppConfig.Get("fnlock_modifier", EvdevInterop.KEY_LEFTMETA);
        comboModifier.SelectedIndex = savedMod switch
        {
            29 => 1,
            56 => 2,
            _ => 0,
        };

        comboKey.Items.Clear();
        for (int i = 0; i < EvdevInterop.FunctionKeys.Length; i++)
        {
            comboKey.Items.Add(new ComboBoxItem
            {
                Content = $"F{i + 1}",
                Tag = (int)EvdevInterop.FunctionKeys[i],
            });
        }
        int savedKey = AppConfig.Get("fnlock_key", EvdevInterop.KEY_F2);
        int keyIdx = 1; // Default to F2 (FunctionKeys[1])
        for (int i = 0; i < EvdevInterop.FunctionKeys.Length; i++)
            if (EvdevInterop.FunctionKeys[i] == savedKey)
                keyIdx = i;
        comboKey.SelectedIndex = keyIdx;
    }

    private void RefreshComboLabels()
    {
        if (comboModifier.Items.Count >= 3)
        {
            if (comboModifier.Items[0] is ComboBoxItem m0)
                m0.Content = Labels.Get("fnlock_modifier_super");
            if (comboModifier.Items[1] is ComboBoxItem m1)
                m1.Content = Labels.Get("fnlock_modifier_ctrl");
            if (comboModifier.Items[2] is ComboBoxItem m2)
                m2.Content = Labels.Get("fnlock_modifier_alt");
        }
    }

    private void ComboHotkey_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        ushort mod = EvdevInterop.KEY_LEFTMETA;
        ushort key = EvdevInterop.KEY_F2;
        if (comboModifier.SelectedItem is ComboBoxItem m && m.Tag is int mTag)
            mod = (ushort)mTag;
        if (comboKey.SelectedItem is ComboBoxItem k && k.Tag is int kTag)
            key = (ushort)kTag;
        AppConfig.Set("fnlock_modifier", mod);
        AppConfig.Set("fnlock_key", key);
        App.FnLock?.SetToggleHotkey(mod, key);
    }

    // DEVICES

    private void BuildDeviceRows()
    {
        panelDevices.Children.Clear();
        var candidates = FnLockRemapper.EnumerateCandidates();

        if (candidates.Count == 0)
        {
            panelDevices.Children.Add(new TextBlock
            {
                Text = Labels.Get("fnlock_devices_empty"),
                Classes = { "label-dim" },
                FontSize = 11,
            });
            return;
        }

        foreach (var c in candidates)
        {
            int explicitChoice = AppConfig.Get(c.ConfigKey, -1);
            bool ticked = explicitChoice == 1
                          || (explicitChoice < 0 && c.LooksIntegrated && !c.IsOurOwn);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(0, 1, 0, 1),
            };

            var cb = new CheckBox
            {
                Name = "checkFnLockDevice",
                IsChecked = ticked,
                IsEnabled = !c.IsOurOwn,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cb, 0);
            row.Children.Add(cb);

            var nameLabel = new TextBlock
            {
                Text = c.Name,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameLabel, 1);
            row.Children.Add(nameLabel);

            string tagStr = $"{c.BusName} {c.Vendor:x4}:{c.Product:x4}";
            if (c.HasFKeys)
                tagStr += " · F1-F12";
            if (c.IsOurOwn)
                tagStr += " · own";
            else if (c.IsKeydVirtual)
                tagStr += " · keyd";
            var meta = new TextBlock
            {
                Text = tagStr,
                FontSize = 10,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#707070")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(meta, 2);
            row.Children.Add(meta);

            string capturedKey = c.ConfigKey;
            cb.IsCheckedChanged += (_, _) =>
            {
                if (_suppressEvents)
                    return;
                AppConfig.Set(capturedKey, (cb.IsChecked ?? false) ? 1 : 0);
                // Restart only when the remapper is currently grabbing devices;
                // changing the device picker requires regrab.
                if (App.FnLock?.IsActive == true)
                    App.RestartFnLock();
            };

            panelDevices.Children.Add(row);
        }
    }

    private void ButtonRescan_Click(object? sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        BuildDeviceRows();
        _suppressEvents = false;
    }

    // PER-KEY MAP
    private void BuildMapRows()
    {
        panelMap.Children.Clear();
        // Force a fresh AllChoices read so newly-translated display names show up
        // when the user changes language while the window is already open.
        FnLockKeymap.InvalidateCache();
        var map = FnLockKeymap.ResolveActiveMap();
        var choices = FnLockKeymap.AllChoices;

        for (int i = 0; i < EvdevInterop.FunctionKeys.Length; i++)
        {
            ushort fkey = EvdevInterop.FunctionKeys[i];

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("60,*"),
                Margin = new Thickness(0, 1, 0, 1),
            };

            var lbl = new TextBlock
            {
                Text = $"F{i + 1}",
                FontSize = 11,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0")),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var combo = new ComboBox { Name = "comboFnLockKey", Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch };
            string? selectedTag = map.TryGetValue(fkey, out var t) ? t.Tag : null;
            int selIdx = 0;
            int idx = 0;
            foreach (var choice in choices)
            {
                combo.Items.Add(new ComboBoxItem { Content = choice.DisplayName, Tag = choice.Tag });
                if (choice.Tag == selectedTag)
                    selIdx = idx;
                idx++;
            }
            combo.SelectedIndex = selIdx;
            ushort capturedFkey = fkey;
            combo.SelectionChanged += (_, _) =>
            {
                if (_suppressEvents)
                    return;
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                {
                    AppConfig.Set($"fnlock_map_{capturedFkey}", tag);
                    // Hot-reload the keymap on the live remapper - no need to
                    // tear down device grabs + uinput just for a per-key change.
                    App.FnLock?.ReloadKeymap();
                }
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            panelMap.Children.Add(row);
        }
    }

    private void ButtonReset_Click(object? sender, RoutedEventArgs e)
    {
        // Wipe user overrides; the model-aware defaults from ResolveActiveMap
        // will populate the rebuilt rows.
        foreach (ushort fkey in EvdevInterop.FunctionKeys)
            AppConfig.Remove($"fnlock_map_{fkey}");
        _suppressEvents = true;
        BuildMapRows();
        _suppressEvents = false;
        // Hot-reload on the live remapper instead of restarting (which would
        // ungrab + regrab all input devices).
        App.FnLock?.ReloadKeymap();
    }

    private void CheckDebug_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        bool on = checkDebug.IsChecked ?? false;
        AppConfig.Set("fnlock_debug", on ? 1 : 0);
        // Hot-swap on the live remapper - no restart needed for a flag flip.
        App.FnLock?.SetDebug(on);
    }
}

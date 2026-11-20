using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Main hub for the embedded microphone DSP chain: denoise -> EQ -> delay
/// -> reverb. Each chain tile uses a CheckBox to enable/disable the effect
/// and a separate click target (the body) to open its detail window.
///
/// The top control strip lets the user pick an input source (PipeWire
/// node), refresh the list, and toggle "monitor" playback so they can
/// hear their own processed voice. The window does NOT have a start/stop
/// button anymore - master enable lives on the main window's Microphone
/// panel (FN-Lock-style toggle). Opening this window is purely for
/// fine-grained chain control.
/// </summary>
public partial class AudioWindow : Window
{
    private readonly DispatcherTimer _redraw;
    private volatile AudioFrame? _latest;
    private bool _suppressEvents = true;
    private string? _selectedSourceName = AudioState.Instance.SelectedSource;

    private EqWindow? _eqWindow;
    private DelayWindow? _delayWindow;
    private ReverbWindow? _reverbWindow;
    private NoiseWindow? _noiseWindow;
    private VocoderWindow? _vocoderWindow;

    public AudioWindow()
    {
        InitializeComponent();

        AudioHelper.Instance.FrameReceived += OnFrame;
        AudioHelper.Instance.ErrorReported += OnError;
        Labels.LanguageChanged += OnLanguageChanged;

        // Wire master volume potentiometer: UI exposes 0..1000 per-mille
        // (0..100%) with 1000 = unity as the maximum, attenuation-only.
        // The helper IPC layer keeps a 0..2000 range so a future "boost"
        // mode can re-enable headroom without breaking the protocol.
        // Label shows whole percent so non-engineers grok it.
        knobMaster.Style = Controls.KnobStyle.LedRing;
        knobMaster.AccentColor = Avalonia.Media.Color.Parse("#4CC2FF");
        knobMaster.Minimum = 0;
        knobMaster.Maximum = 1000;
        knobMaster.DefaultValue = 1000;
        knobMaster.WheelStep = 25;
        knobMaster.LabelFormatter = v => $"{v / 10}%";
        // If a persisted value above the new UI cap exists, clamp on load.
        int initial = AudioState.Instance.MasterVolume;
        if (initial > 1000)
            initial = 1000;
        knobMaster.Value = initial;
        knobMaster.ValueChanged += v => AudioHelper.Instance.SetMasterVolume(v);

        ApplyLabels();

        // Restore the persisted source selection before populating so the
        // dropdown auto-selects it. Empty = system default.
        _selectedSourceName = string.IsNullOrEmpty(AudioState.Instance.SelectedSource)
            ? null
            : AudioState.Instance.SelectedSource;

        // Populate the source dropdown synchronously - cheap shell-out.
        PopulateSources();

        // Hydrate checkboxes from the persistent AudioState so opening the
        // window doesn't flip user-toggled effects back to their XAML
        // defaults. Same problem the mini-windows had.
        var s = AudioState.Instance;
        checkRnn.IsChecked = s.RnnoiseOn;
        checkVocoder.IsChecked = s.VocoderOn;
        checkEq.IsChecked = s.EqOn;
        checkDelay.IsChecked = s.DelayOn;
        checkReverb.IsChecked = s.ReverbOn;
        toggleMonitor.IsChecked = s.MonitorOn;
        _suppressEvents = false;

        UpdateTileStyles(s.RnnoiseOn, s.VocoderOn, s.EqOn, s.DelayOn, s.ReverbOn);
        s.NoiseModelChanged += OnNoiseModelChanged;
        SetControlsEnabled(AudioHelper.Instance.IsRunning);

        _redraw = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _redraw.Tick += (_, _) => Redraw();
        _redraw.Start();

        Closed += (_, _) =>
        {
            _redraw.Stop();
            AudioHelper.Instance.FrameReceived -= OnFrame;
            AudioHelper.Instance.ErrorReported -= OnError;
            Labels.LanguageChanged -= OnLanguageChanged;
            AudioState.Instance.NoiseModelChanged -= OnNoiseModelChanged;
        };

        RefreshStatus();
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyLabels);

    /// <summary>
    /// Localise every static label in the window. Called once at startup
    /// and again whenever <see cref="Labels.LanguageChanged"/> fires so the
    /// user can swap UI language without restarting.
    /// </summary>
    private void ApplyLabels()
    {
        Title = Labels.Get("audio_window_title");
        labelInputSource.Text = Labels.Get("audio_input_source");
        ToolTip.SetTip(buttonSourceRefresh, Labels.Get("audio_refresh_tooltip"));
        ToolTip.SetTip(toggleMonitor, Labels.Get("audio_monitor_tooltip"));
        labelMonitor.Text = Labels.Get("audio_monitor");
        knobMaster.Caption = Labels.Get("audio_master_volume");
        ToolTip.SetTip(knobMaster, Labels.Get("audio_master_volume_tooltip"));
        knobMaster.InvalidateVisual();

        checkRnn.Content = Labels.Get("audio_effect_denoise");
        checkVocoder.Content = Labels.Get("audio_effect_vocoder");
        checkEq.Content = Labels.Get("audio_effect_eq");
        checkDelay.Content = Labels.Get("audio_effect_delay");
        checkReverb.Content = Labels.Get("audio_effect_reverb");

        ShowNoiseModel();
        labelVocoderSubtitle.Text = Labels.Get("audio_effect_vocoder_sub");
        labelEqSubtitle.Text = Labels.Get("audio_effect_eq_sub");
        labelDelaySubtitle.Text = Labels.Get("audio_effect_delay_sub");
        labelReverbSubtitle.Text = Labels.Get("audio_effect_reverb_sub");

        labelMeterIn.Text = Labels.Get("audio_meter_in");
        labelMeterOut.Text = Labels.Get("audio_meter_out");
        labelMeterRed.Text = Labels.Get("audio_meter_reduction");
        labelMeterVad.Text = Labels.Get("audio_meter_vad");

        labelWaveIn.Text = Labels.Get("audio_chart_wave_in");
        labelWaveOut.Text = Labels.Get("audio_chart_wave_out");
        labelSpecIn.Text = Labels.Get("audio_chart_spec_in");
        labelSpecOut.Text = Labels.Get("audio_chart_spec_out");

        labelHint.Text = Labels.Get("audio_hint");

        // Repopulate the source dropdown so the "System default" entry's
        // label reflects the new language without losing user selection.
        PopulateSources();
        RefreshStatus();
    }

    private void PopulateSources()
    {
        var sources = AudioSources.Enumerate();
        _suppressEvents = true;
        comboSource.Items.Clear();
        // First entry: "System default" with null tag.
        var def = new ComboBoxItem { Content = Labels.Get("audio_source_default"), Tag = null };
        comboSource.Items.Add(def);
        ComboBoxItem? toSelect = def;
        foreach (var s in sources)
        {
            var item = new ComboBoxItem { Content = s.Description, Tag = s.NodeName };
            comboSource.Items.Add(item);
            if (s.NodeName == _selectedSourceName)
                toSelect = item;
        }
        comboSource.SelectedItem = toSelect;
        _suppressEvents = false;
    }

    private void OnFrame(AudioFrame f) => _latest = f;

    private void OnNoiseModelChanged() => Dispatcher.UIThread.Post(ShowNoiseModel);

    private void ShowNoiseModel() => labelRnnSubtitle.Text = AudioState.NoiseModelNames[AudioState.Instance.NoiseModel];

    private void OnError(string msg)
    {
        Dispatcher.UIThread.Post(() => labelStatus.Text = msg);
    }

    /// <summary>
    /// Called by MainWindow after the audio toggle flips so this window can
    /// re-sync its status text and (if just started) reflect any state
    /// changes that arrived via ReapplyAllState.
    /// </summary>
    public void RefreshFromMain()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshStatus();
            var s = AudioState.Instance;
            SyncCheckBoxes(s.RnnoiseOn, s.VocoderOn, s.EqOn, s.DelayOn, s.ReverbOn, s.MonitorOn);
            UpdateTileStyles(s.RnnoiseOn, s.VocoderOn, s.EqOn, s.DelayOn, s.ReverbOn);
            SetControlsEnabled(AudioHelper.Instance.IsRunning);
        });
    }

    private void RefreshStatus()
    {
        bool running = AudioHelper.Instance.IsRunning;
        if (running)
        {
            labelStatus.Text = AudioState.Instance.MonitorOn
                ? Labels.Get("audio_status_running_monitor")
                : Labels.Get("audio_status_running");
        }
        else
        {
            labelStatus.Text = Labels.Get("audio_status_offline");
        }
        SetControlsEnabled(running);
    }

    private void Redraw()
    {
        var f = _latest;

        // Waveforms / spectra / meters only update when frames arrive.
        if (f != null)
        {
            waveIn.Samples = f.WaveformIn;
            waveOut.Samples = f.WaveformOut;
            waveIn.InvalidateVisual();
            waveOut.InvalidateVisual();

            specIn.Bins = f.SpectrumIn;
            specOut.Bins = f.SpectrumOut;
            specIn.InvalidateVisual();
            specOut.InvalidateVisual();

            meterIn.Value = Math.Clamp((f.RmsInDb + 80.0) / 80.0 * 100.0, 0, 100);
            meterOut.Value = Math.Clamp((f.RmsOutDb + 80.0) / 80.0 * 100.0, 0, 100);
            // NoiseReductionDb is the real noise removed by the denoiser (0..~40
            // positive dB scale); 0 when denoise is bypassed. Replaces the
            // old whole-chain rms delta which polluted with EQ/reverb/etc.
            meterRed.Value = Math.Clamp(f.NoiseReductionDb, 0, 40);
            meterVad.Value = f.VadProb * 100.0;
            labelInDb.Text = float.IsFinite(f.RmsInDb) ? $"{f.RmsInDb:F1} dB" : "-inf dB";
            labelOutDb.Text = float.IsFinite(f.RmsOutDb) ? $"{f.RmsOutDb:F1} dB" : "-inf dB";
            labelRedDb.Text = $"{f.NoiseReductionDb:F1} dB";
            labelVad.Text = $"{f.VadProb * 100:F0}%";

            UpdateTileStyles(f.RnnoiseOn, f.VocoderOn, f.EqOn, f.DelayOn, f.ReverbOn);
            SyncCheckBoxes(f.RnnoiseOn, f.VocoderOn, f.EqOn, f.DelayOn, f.ReverbOn, f.MonitorOn);
        }

        RefreshStatus();
    }

    private void UpdateTileStyles(bool rn, bool voc, bool eq, bool dl, bool rv)
    {
        SetTileOn(tileRnn, rn);
        SetTileOn(tileVocoder, voc);
        SetTileOn(tileEq, eq);
        SetTileOn(tileDelay, dl);
        SetTileOn(tileReverb, rv);
    }

    private static void SetTileOn(Border tile, bool on)
    {
        if (on && !tile.Classes.Contains("on"))
            tile.Classes.Add("on");
        else if (!on && tile.Classes.Contains("on"))
            tile.Classes.Remove("on");
    }

    private void SetControlsEnabled(bool enabled)
    {
        tileRnn.IsEnabled = enabled;
        tileVocoder.IsEnabled = enabled;
        tileEq.IsEnabled = enabled;
        tileDelay.IsEnabled = enabled;
        tileReverb.IsEnabled = enabled;
        toggleMonitor.IsEnabled = enabled;
        comboSource.IsEnabled = enabled;
        buttonSourceRefresh.IsEnabled = enabled;
        knobMaster.IsEnabled = enabled;
    }

    private void SyncCheckBoxes(bool rn, bool voc, bool eq, bool dl, bool rv, bool mon)
    {
        _suppressEvents = true;
        if (checkRnn.IsChecked != rn)
            checkRnn.IsChecked = rn;
        if (checkVocoder.IsChecked != voc)
            checkVocoder.IsChecked = voc;
        if (checkEq.IsChecked != eq)
            checkEq.IsChecked = eq;
        if (checkDelay.IsChecked != dl)
            checkDelay.IsChecked = dl;
        if (checkReverb.IsChecked != rv)
            checkReverb.IsChecked = rv;
        if (toggleMonitor.IsChecked != mon)
            toggleMonitor.IsChecked = mon;
        _suppressEvents = false;
    }

    // --- Source dropdown ----------------------------------------------------

    private void ComboSource_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;
        var item = comboSource.SelectedItem as ComboBoxItem;
        var name = item?.Tag as string;
        _selectedSourceName = name;
        AudioHelper.Instance.SetSource(name);
    }

    private void ButtonSourceRefresh_Click(object? sender, RoutedEventArgs e)
    {
        PopulateSources();
    }

    // --- Monitor ------------------------------------------------------------

    private void ToggleMonitor_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        AudioHelper.Instance.SetMonitor(toggleMonitor.IsChecked == true);
    }

    // --- Chain enable/disable (CheckBox.IsCheckedChanged) -------------------

    private void CheckRnn_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        AudioHelper.Instance.SetRnnoise(checkRnn.IsChecked == true);
    }

    private void CheckVocoder_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        AudioHelper.Instance.SetVocoder(checkVocoder.IsChecked == true);
    }

    private void CheckEq_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        AudioHelper.Instance.SetEq(checkEq.IsChecked == true);
    }

    private void CheckDelay_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        AudioHelper.Instance.SetDelay(checkDelay.IsChecked == true);
    }

    private void CheckReverb_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;
        AudioHelper.Instance.SetReverb(checkReverb.IsChecked == true);
    }

    // --- Tile body click: open detail window (no toggle side-effect) -------

    private void ButtonRnnOpen_Click(object? sender, RoutedEventArgs e)
        => OpenOrFocus(ref _noiseWindow, () => new NoiseWindow());

    private void ButtonVocoderOpen_Click(object? sender, RoutedEventArgs e)
        => OpenOrFocus(ref _vocoderWindow, () => new VocoderWindow());

    private void ButtonEqOpen_Click(object? sender, RoutedEventArgs e)
        => OpenOrFocus(ref _eqWindow, () => new EqWindow());

    private void ButtonDelayOpen_Click(object? sender, RoutedEventArgs e)
        => OpenOrFocus(ref _delayWindow, () => new DelayWindow());

    private void ButtonReverbOpen_Click(object? sender, RoutedEventArgs e)
        => OpenOrFocus(ref _reverbWindow, () => new ReverbWindow());

    private void OpenOrFocus<T>(ref T? slot, Func<T> create) where T : Window
    {
        if (slot == null || !slot.IsVisible)
        {
            slot = create();
            WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(slot);
            slot.Show();
        }
        else
        {
            slot.Activate();
        }
    }
}

using Avalonia.Controls;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

public partial class NoiseWindow : Window
{
    public NoiseWindow()
    {
        InitializeComponent();
        comboModel.ItemsSource = AudioState.NoiseModelNames;
        comboModel.SelectedIndex = AudioState.Instance.NoiseModel;
        ApplyLabels();
        AudioHelper.Instance.FrameReceived += OnFrame;
        Labels.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            AudioHelper.Instance.FrameReceived -= OnFrame;
            Labels.LanguageChanged -= OnLanguageChanged;
        };
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyLabels);

    private void ApplyLabels()
    {
        // No Reset button here: the noise-suppression stage has only the
        // on/off bit, and that bit lives on the main window's chain tile.
        // A "Reset" inside this mini-window had nothing meaningful to do.
        Title = Labels.Get("audio_noise_title");
        labelDescription.Text = Labels.Get("audio_noise_description");
        labelVadHeader.Text = Labels.Get("audio_noise_vad");
        labelRedHeader.Text = Labels.Get("audio_noise_reduction");
    }

    private void ComboModel_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (comboModel.SelectedIndex >= 0 && comboModel.SelectedIndex != AudioState.Instance.NoiseModel)
            AudioHelper.Instance.SetNoiseModel(comboModel.SelectedIndex);
    }

    private void OnFrame(AudioFrame f)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            meterVad.Value = f.VadProb * 100.0;
            // Real noise reduction (positive scale, 0 when bypassed).
            meterRed.Value = Math.Clamp(f.NoiseReductionDb, 0, 40);
            labelVad.Text = $"{f.VadProb * 100:F0}%";
            labelRed.Text = $"{f.NoiseReductionDb:F1} dB";
        });
    }
}

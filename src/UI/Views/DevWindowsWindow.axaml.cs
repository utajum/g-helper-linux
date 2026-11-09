using Avalonia.Controls;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Dev-only launcher that opens any window of the app regardless of detected
/// hardware, so every view can be checked on unsupported machines.
/// Reachable from the main window button shown when GHELPER_DEV is set.
/// </summary>
public partial class DevWindowsWindow : Window
{
    private static readonly (string Name, Func<Window> Factory)[] Windows =
    {
        ("Extra Settings", () => new ExtraWindow()),
        ("Fans + Power", () => new FansWindow()),
        ("Updates", () => new UpdatesWindow()),
        ("Matrix", () => new MatrixWindow()),
        ("Battery Info", () => new BatteryInfoWindow()),
        ("System Info", () => new SystemInfoWindow()),
        ("Hardware Monitor", () => new MonitorWindow()),
        ("Nvidia Processes", () => new NvidiaProcessesWindow()),
        ("dGPU Apps", () => new GpuAppsWindow()),
        ("Fn Lock Remap", () => new FnLockWindow()),
        ("Handheld (Ally)", () => new HandheldWindow()),
        ("NumberPad", () => new NumberPadWindow()),
        ("Mouse", () => new MouseWindow()),
        ("Audio", () => new AudioWindow()),
        ("Equalizer", () => new EqWindow()),
        ("Noise Suppression", () => new NoiseWindow()),
        ("Vocoder", () => new VocoderWindow()),
        ("Delay", () => new DelayWindow()),
        ("Reverb", () => new ReverbWindow()),
        ("Changelog", () => new ChangelogWindow()),
        ("Arcade", () => new ArcadeWindow()),
    };

    public DevWindowsWindow()
    {
        InitializeComponent();

        foreach (var (name, factory) in Windows)
        {
            var button = new Button
            {
                Content = name,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Height = 30,
            };
            button.Classes.Add("ghelper");
            button.Click += (_, _) => OpenWindow(name, factory);
            panelButtons.Children.Add(button);
        }
    }

    private void OpenWindow(string name, Func<Window> factory)
    {
        try
        {
            var window = factory();
            window.Show();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"DevWindows: '{name}' failed to open: {ex.Message}");
        }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using GHelper.Linux.Input;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// NumberPad settings window - master enable toggle (config "numberpad"),
/// hardware detection status with actionable hints, and layout info.
/// </summary>
public partial class NumberPadWindow : Window
{
    private bool _suppressEvents;

    public NumberPadWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _suppressEvents = true;
        checkEnabled.IsChecked = Helpers.AppConfig.Is("numberpad");
        _suppressEvents = false;

        var probe = NumberPad.Probe();
        switch (probe.Status)
        {
            case NumberPad.ProbeStatus.Ok:
                labelStatus.Text = NumberPad.IsRunning
                    ? $"Running on {probe.TouchpadName}"
                    : $"Detected: {probe.TouchpadName}";
                labelHint.Text = "";
                break;
            case NumberPad.ProbeStatus.NoHardware:
                labelStatus.Text = "No compatible touchpad found";
                labelHint.Text = "Requires an ASUS touchpad with an illuminated NumberPad (ELAN / ASUE / ASUP / ASUF controller).";
                break;
            case NumberPad.ProbeStatus.I2cUnavailable:
                labelStatus.Text = $"{probe.Detail} is missing";
                labelHint.Text = "Load the i2c-dev kernel module: sudo modprobe i2c-dev. Add i2c-dev to /etc/modules-load.d/ to persist across reboots.";
                break;
            case NumberPad.ProbeStatus.PermissionDenied:
                labelStatus.Text = $"No write access to {probe.Detail}";
                labelHint.Text = "Install the bundled udev rules (install/90-ghelper.rules) and reload them, or chmod the device manually.";
                break;
        }

        string model = Helpers.AppConfig.GetModel();
        var layout = NumberPadLayouts.Resolve(model, probe.TouchpadName);
        labelLayout.Text = $"{layout.Cols}x{layout.Rows} ({layout.Name})";
        labelModel.Text = model.Length > 0 ? model : "Unknown";
    }

    private void CheckEnabled_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        bool enabled = checkEnabled.IsChecked ?? false;
        Helpers.AppConfig.Set("numberpad", enabled ? 1 : 0);

        if (enabled)
            NumberPad.Start();
        else
            NumberPad.Stop();

        Refresh();
    }
}

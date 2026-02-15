namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux input handler — forwards ASUS evdev events to the application layer.
/// The asus-nb-wmi kernel module and asus HID driver create input devices that report
/// ASUS-specific hotkey events (Fn+F5, ROG key, Fn+F4, etc.).
///
/// Events come from two sources:
///   - USB HID "Asus Keyboard" (event8) — most Fn keys on newer kernels with asus HID driver
///   - WMI "Asus WMI hotkeys" (event9) — fallback for some models/keys
/// </summary>
public class LinuxInputHandler : IInputHandler
{
    public event Action<int>? HotkeyPressed;
    public event Action<string>? KeyBindingPressed;
    private volatile bool _listening;

    public void StartListening()
    {
        _listening = true;
        if (App.Wmi != null)
        {
            App.Wmi.WmiEvent += OnWmiEvent;
            App.Wmi.KeyBindingEvent += OnKeyBindingEvent;
            App.Wmi.SubscribeEvents();
        }
        Helpers.Logger.WriteLine("Input handler started");
    }

    public void StopListening()
    {
        _listening = false;
        if (App.Wmi != null)
        {
            App.Wmi.WmiEvent -= OnWmiEvent;
            App.Wmi.KeyBindingEvent -= OnKeyBindingEvent;
        }
        Helpers.Logger.WriteLine("Input handler stopped");
    }

    private void OnWmiEvent(int eventCode)
    {
        if (!_listening) return;
        HotkeyPressed?.Invoke(eventCode);
    }

    private void OnKeyBindingEvent(string bindingName)
    {
        if (!_listening) return;
        KeyBindingPressed?.Invoke(bindingName);
    }

    public void Dispose()
    {
        StopListening();
    }
}

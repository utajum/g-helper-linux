namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux input event handler for ASUS hotkeys via evdev.
/// The asus-nb-wmi kernel module creates an input device that reports
/// Fn-key combos as KEY_* events. We listen on /dev/input/eventN.
/// 
/// Note: The actual event listening is done in LinuxAsusWmi.SubscribeEvents()
/// which reads the evdev device. This class provides the higher-level
/// hotkey registration interface.
/// </summary>
public class LinuxInputHandler : IInputHandler
{
    public event Action<int>? HotkeyPressed;
    private volatile bool _listening;

    public void StartListening()
    {
        _listening = true;
        // Events are received from LinuxAsusWmi.WmiEvent
        // Wire them through here
        if (App.Wmi != null)
        {
            App.Wmi.WmiEvent += OnWmiEvent;
            App.Wmi.SubscribeEvents();
        }
        Helpers.Logger.WriteLine("Input handler started");
    }

    public void StopListening()
    {
        _listening = false;
        if (App.Wmi != null)
            App.Wmi.WmiEvent -= OnWmiEvent;
        Helpers.Logger.WriteLine("Input handler stopped");
    }

    private void OnWmiEvent(int eventCode)
    {
        if (!_listening) return;
        HotkeyPressed?.Invoke(eventCode);
    }

    public void Dispose()
    {
        StopListening();
    }
}

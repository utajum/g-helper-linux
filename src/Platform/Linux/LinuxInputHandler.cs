namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux input event handler for ASUS hotkeys via evdev.
/// The asus-nb-wmi kernel module creates an input device that reports
/// Fn-key combos as KEY_* events. We listen on /dev/input/eventN.
/// 
/// Note: The actual event listening is done in LinuxAsusWmi.SubscribeEvents()
/// which reads the evdev device. This class provides the higher-level
/// hotkey registration interface.
/// 
/// Also manages SoftwareFnLock for laptops without hardware FnLock support.
/// </summary>
public class LinuxInputHandler : IInputHandler
{
    public event Action<int>? HotkeyPressed;
    private volatile bool _listening;
    private SoftwareFnLock? _softwareFnLock;

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

        // Initialize software FnLock if needed
        InitializeSoftwareFnLock();

        Helpers.Logger.WriteLine("Input handler started");
    }

    private void InitializeSoftwareFnLock()
    {
        // Check if we need software FnLock (no hardware support)
        bool needsSoftwareFnLock = !Helpers.AppConfig.IsHardwareFnLock();
        
        if (needsSoftwareFnLock)
        {
            Helpers.Logger.WriteLine("Initializing software FnLock (no hardware support on this model)");
            _softwareFnLock = new SoftwareFnLock();
            
            if (_softwareFnLock.Init())
            {
                // Set initial state from config
                _softwareFnLock.IsEnabled = Helpers.AppConfig.Is("fn_lock");
                Helpers.Logger.WriteLine($"Software FnLock initialized, state: {_softwareFnLock.IsEnabled}");
            }
            else
            {
                Helpers.Logger.WriteLine("WARNING: Failed to initialize software FnLock");
                _softwareFnLock.Dispose();
                _softwareFnLock = null;
            }
        }
    }

    public void SetFnLock(bool enabled)
    {
        if (_softwareFnLock != null)
        {
            _softwareFnLock.IsEnabled = enabled;
            Helpers.Logger.WriteLine($"Software FnLock set to: {enabled}");
        }
    }

    public void StopListening()
    {
        _listening = false;
        if (App.Wmi != null)
            App.Wmi.WmiEvent -= OnWmiEvent;
        
        _softwareFnLock?.Dispose();
        _softwareFnLock = null;
        
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

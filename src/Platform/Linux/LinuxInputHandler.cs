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
/// Also manages Software FnLock for laptops without hardware FnLock support.
/// Uses keyd (preferred) or falls back to evdev grab implementation.
/// </summary>
public class LinuxInputHandler : IInputHandler
{
    public event Action<int>? HotkeyPressed;
    private volatile bool _listening;
    private SoftwareFnLock? _softwareFnLock;
    private KeydFnLock? _keydFnLock;
    private bool _useKeyd = false;

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
        
        if (!needsSoftwareFnLock)
            return;

        Helpers.Logger.WriteLine("Initializing software FnLock (no hardware support on this model)");

        // Try keyd first (works on X11, Wayland, and console)
        if (KeydFnLock.IsInstalled())
        {
            Helpers.Logger.WriteLine("keyd detected, attempting to use it for FN Lock");
            _keydFnLock = new KeydFnLock();
            
            if (_keydFnLock.Init())
            {
                _useKeyd = true;
                // Set initial state from config
                _keydFnLock.SetEnabled(Helpers.AppConfig.Is("fn_lock"));
                Helpers.Logger.WriteLine($"keyd FN Lock initialized successfully");
                return;
            }
            else
            {
                Helpers.Logger.WriteLine("keyd init failed, falling back to evdev implementation");
                _keydFnLock.Dispose();
                _keydFnLock = null;
            }
        }
        else
        {
            Helpers.Logger.WriteLine("keyd not installed. For best FN Lock support, run: sudo apt install keyd");
        }

        // Fall back to evdev grab implementation
        Helpers.Logger.WriteLine("Using evdev-based Software FnLock (may not work on X11)");
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

    public void SetFnLock(bool enabled)
    {
        if (_useKeyd && _keydFnLock != null)
        {
            _keydFnLock.SetEnabled(enabled);
            Helpers.Logger.WriteLine($"keyd FN Lock set to: {enabled}");
        }
        else if (_softwareFnLock != null)
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
        
        _keydFnLock?.Dispose();
        _keydFnLock = null;
        
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

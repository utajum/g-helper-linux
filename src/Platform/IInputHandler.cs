namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over input event handling (ASUS Fn keys, hotkeys).
/// Windows: WMI AsusAtkWmiEvent + RegisterHotKey
/// Linux: evdev /dev/input/* for asus-nb-wmi input device
/// </summary>
public interface IInputHandler : IDisposable
{
    /// <summary>Fired when an ASUS hotkey event is received.
    /// Event codes match G-Helper convention:
    ///   174 = Fn+F5 (performance cycle)
    ///   196 = Fn+F3 (backlight up)
    ///   197 = Fn+F2 (backlight down)
    ///   56 = ROG key
    ///   107 = Fn+F10 (touchpad toggle)
    ///   108 = Fn+F11 (sleep)
    ///   133 = Camera toggle
    ///   136 = Fn+F12 (airplane)
    /// </summary>
    event Action<int>? HotkeyPressed;

    /// <summary>Start listening for input events.</summary>
    void StartListening();

    /// <summary>Stop listening for input events.</summary>
    void StopListening();

    /// <summary>Set software FnLock state (for laptops without hardware support).</summary>
    void SetFnLock(bool enabled);
}

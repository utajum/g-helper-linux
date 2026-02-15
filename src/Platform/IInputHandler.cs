namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over input event handling (ASUS Fn keys, hotkeys).
/// Windows: WMI AsusAtkWmiEvent + RegisterHotKey
/// Linux: evdev /dev/input/* for ASUS USB keyboard + asus-nb-wmi input devices
/// </summary>
public interface IInputHandler : IDisposable
{
    /// <summary>Fired when a non-configurable ASUS hotkey event is received.
    /// Event codes match G-Helper convention:
    ///   196 = Fn+F3 (backlight up)
    ///   197 = Fn+F2 (backlight down)
    ///   107 = Fn+F10 (touchpad toggle)
    ///   108 = Fn+F11 (sleep)
    ///   133 = Camera toggle
    ///   136 = Fn+F12 (airplane)
    /// </summary>
    event Action<int>? HotkeyPressed;

    /// <summary>Fired for configurable key bindings.
    /// String is the binding name matching AppConfig keys:
    ///   "m4"   = ROG/M5 key (default: toggle G-Helper)
    ///   "fnf4" = Fn+F4 (default: cycle aura mode)
    ///   "fnf5" = Fn+F5 / M4 (default: cycle performance mode)
    /// </summary>
    event Action<string>? KeyBindingPressed;

    /// <summary>Start listening for input events.</summary>
    void StartListening();

    /// <summary>Stop listening for input events.</summary>
    void StopListening();
}

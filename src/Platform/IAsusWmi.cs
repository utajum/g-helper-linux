namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over ASUS WMI hardware interface.
/// Windows: \\.\ATKACPI via DeviceIoControl (DSTS/DEVS)
/// Linux: asus-wmi kernel module via sysfs + evdev
/// </summary>
public interface IAsusWmi : IDisposable
{
    // ── Core ACPI methods (equivalent to DSTS/DEVS) ──

    /// <summary>Read a device value. Returns -1 if unsupported.</summary>
    int DeviceGet(int deviceId);

    /// <summary>Set a device value. Returns 1 on success.</summary>
    int DeviceSet(int deviceId, int value);

    /// <summary>Read a buffer response (e.g., fan curves).</summary>
    byte[]? DeviceGetBuffer(int deviceId, int args = 0);

    // ── Performance mode ──

    /// <summary>Get current thermal policy. 0=Balanced, 1=Turbo, 2=Silent</summary>
    int GetThrottleThermalPolicy();

    /// <summary>Set thermal policy.</summary>
    void SetThrottleThermalPolicy(int mode);

    // ── Fan control ──

    /// <summary>Get fan speed in RPM. fanIndex: 0=CPU, 1=GPU, 2=Mid</summary>
    int GetFanRpm(int fanIndex);

    /// <summary>Get fan curve (8 temp + 8 duty bytes). Returns null if unsupported.</summary>
    byte[]? GetFanCurve(int fanIndex);

    /// <summary>Set fan curve (8 temp + 8 duty bytes).</summary>
    void SetFanCurve(int fanIndex, byte[] curve);

    // ── Battery ──

    /// <summary>Get charge limit (40-100).</summary>
    int GetBatteryChargeLimit();

    /// <summary>Set charge limit (40-100).</summary>
    void SetBatteryChargeLimit(int percent);

    // ── GPU ──

    /// <summary>Get GPU Eco mode state. true = dGPU disabled.</summary>
    bool GetGpuEco();

    /// <summary>Set GPU Eco mode.</summary>
    void SetGpuEco(bool enabled);

    /// <summary>Get MUX switch mode. 0=dGPU direct, 1=hybrid.</summary>
    int GetGpuMuxMode();

    /// <summary>Set MUX switch mode (requires reboot).</summary>
    void SetGpuMuxMode(int mode);

    // ── Display ──

    /// <summary>Get panel overdrive state.</summary>
    bool GetPanelOverdrive();

    /// <summary>Set panel overdrive.</summary>
    void SetPanelOverdrive(bool enabled);

    /// <summary>Get MiniLED mode.</summary>
    int GetMiniLedMode();

    /// <summary>Set MiniLED mode.</summary>
    void SetMiniLedMode(int mode);

    // ── PPT / Power limits ──

    /// <summary>Set PPT limit by sysfs attribute name and value in watts.</summary>
    void SetPptLimit(string attribute, int watts);

    /// <summary>Read PPT limit by sysfs attribute name. Returns watts or -1.</summary>
    int GetPptLimit(string attribute);

    // ── Keyboard ──

    /// <summary>Get keyboard backlight brightness (0-3).</summary>
    int GetKeyboardBrightness();

    /// <summary>Set keyboard backlight brightness (0-3).</summary>
    void SetKeyboardBrightness(int level);

    /// <summary>Set TUF keyboard RGB color.</summary>
    void SetKeyboardRgb(byte r, byte g, byte b);

    // ── Events ──

    /// <summary>Fired when an ASUS WMI hotkey event occurs (Fn keys, lid, etc.).</summary>
    event Action<int>? WmiEvent;

    /// <summary>Start listening for WMI/evdev events.</summary>
    void SubscribeEvents();

    // ── Feature detection ──

    /// <summary>Check if a sysfs attribute exists (feature is supported).</summary>
    bool IsFeatureSupported(string feature);
}

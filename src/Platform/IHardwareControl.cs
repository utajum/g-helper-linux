namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over the vendor platform hardware interface (EC/WMI).
/// ASUS: asus-wmi kernel module via sysfs + evdev (ATKACPI DSTS/DEVS on Windows).
/// Lenovo: ideapad-laptop + lenovo-wmi-* kernel modules via sysfs + evdev.
/// </summary>
public interface IHardwareControl : IDisposable
{
    // Core ACPI methods (equivalent to DSTS/DEVS)

    /// <summary>Read a device value. Returns -1 if unsupported.</summary>
    int DeviceGet(int deviceId);

    /// <summary>Set a device value. Returns 1 on success.</summary>
    int DeviceSet(int deviceId, int value);

    /// <summary>Read a buffer response (e.g., fan curves).</summary>
    byte[]? DeviceGetBuffer(int deviceId, int args = 0);

    // Performance mode

    /// <summary>Get current thermal policy. 0=Balanced, 1=Turbo, 2=Silent</summary>
    int GetThrottleThermalPolicy();

    /// <summary>Set thermal policy.</summary>
    void SetThrottleThermalPolicy(int mode);

    // Fan control

    /// <summary>Get fan speed in RPM. fanIndex: 0=CPU, 1=GPU, 2=Mid</summary>
    int GetFanRpm(int fanIndex);

    /// <summary>Get fan curve (8 temp + 8 duty bytes). Returns null if unsupported.</summary>
    byte[]? GetFanCurve(int fanIndex);

    /// <summary>Set fan curve (8 temp + 8 duty bytes).</summary>
    void SetFanCurve(int fanIndex, byte[] curve);

    /// <summary>Disable custom fan curve for this fan (pwm_enable=2).
    /// Returns to firmware thermal policy defaults for the current profile.</summary>
    void DisableFanCurve(int fanIndex);

    /// <summary>Reset fan curve to BIOS factory defaults (pwm_enable=3) and read back.
    /// Returns the firmware default curve, or null if unsupported.</summary>
    byte[]? ResetFanCurveToDefaults(int fanIndex);

    /// <summary>Check if custom fan curve is active (pwm_enable==1). Returns false if
    /// firmware is in control (pwm_enable==2 or 3) or if unsupported.</summary>
    bool IsFanCurveEnabled(int fanIndex);

    /// <summary>Ensure the EC is in manual fan mode (FANM=4) by writing pwm_enable=1
    /// for all fans. Some firmware (SPLX ACPI path) silently ignores PPT writes
    /// unless FANM=4, which is a side-effect of enabling a custom fan curve.
    /// This is a no-op when fan curves are already enabled.</summary>
    void EnsureManualFanMode();

    // Battery

    /// <summary>Get charge limit (40-100).</summary>
    int GetBatteryChargeLimit();

    /// <summary>Set charge limit (40-100). Returns true if write succeeded.</summary>
    bool SetBatteryChargeLimit(int percent);

    // GPU

    /// <summary>Get GPU Eco mode state. true = dGPU disabled.</summary>
    bool GetGpuEco();

    /// <summary>Set GPU Eco mode (dgpu_disable sysfs).
    /// Prefer GPUModeControl.RequestModeSwitch() for full orchestration.
    /// SAFETY: When enabled=true, throws InvalidOperationException if:
    /// - NVIDIA driver is active (refcnt > 0) - would cause kernel panic
    /// - gpu_mux_mode=0 (Ultimate) - would create impossible boot state (no display)
    /// When enabled=false: always safe, triggers PCI bus rescan after write.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the write would violate a hardware safety invariant.</exception>
    void SetGpuEco(bool enabled);

    /// <summary>Get MUX switch mode. 0=dGPU direct, 1=hybrid.</summary>
    int GetGpuMuxMode();

    /// <summary>Set MUX switch mode (gpu_mux_mode sysfs, requires reboot).
    /// Prefer GPUModeControl.RequestModeSwitch() for full orchestration.
    /// SAFETY: Throws InvalidOperationException if dgpu_disable=1
    /// (firmware rejects MUX changes when dGPU is powered off).
    /// Throws IOException if the sysfs write fails (firmware rejection, permission error).</summary>
    /// <exception cref="InvalidOperationException">Thrown when dgpu_disable=1.</exception>
    /// <exception cref="IOException">Thrown when sysfs write fails.</exception>
    void SetGpuMuxMode(int mode);

    // Display

    /// <summary>Get panel overdrive state.</summary>
    bool GetPanelOverdrive();

    /// <summary>Set panel overdrive. Returns true if the write took effect.</summary>
    bool SetPanelOverdrive(bool enabled);

    /// <summary>Get MiniLED mode.</summary>
    int GetMiniLedMode();

    /// <summary>Set MiniLED mode.</summary>
    void SetMiniLedMode(int mode);

    /// <summary>
    /// Number of MiniLED dimming modes this panel accepts. 2 = off/on,
    /// 3 = off/multi-zone/multi-zone strong. 0 when unsupported.
    /// </summary>
    int GetMiniLedModeCount();

    /// <summary>Get raw firmware state of Optimal Display Brightness (screen_auto_brightness): 0=off, 1=on, -1 if unsupported.</summary>
    int GetScreenAutoBrightness();

    /// <summary>Set raw firmware state of Optimal Display Brightness. Mode resolution (Off/Always/BatteryOnly) is owned by Display.OptimalBrightness.</summary>
    void SetScreenAutoBrightness(bool enabled);

    // PPT / Power limits

    /// <summary>Set PPT limit by sysfs attribute name and value in watts.
    /// Returns true only when a value was written (false if unchanged or failed).</summary>
    bool SetPptLimit(string attribute, int watts);

    /// <summary>Read PPT limit by sysfs attribute name. Returns watts or -1.</summary>
    int GetPptLimit(string attribute);

    /// <summary>Read min/max/step/default for a firmware-attribute. Returns null if unavailable.</summary>
    Linux.AttrRange? GetAttributeRange(Linux.AttrDef attr);

    // Keyboard

    /// <summary>True when the kernel applies keyboard backlight hotkeys itself
    /// and reports the result via brightness_hw_changed - the app then only
    /// reads back the new value instead of cycling it.</summary>
    bool HasKbdBrightnessHwChanged { get; }

    /// <summary>Get keyboard backlight brightness (0..<see cref="KbdMaxBrightness"/>).</summary>
    int GetKeyboardBrightness();

    /// <summary>Set keyboard backlight brightness (0..<see cref="KbdMaxBrightness"/>).</summary>
    void SetKeyboardBrightness(int level);

    /// <summary>Highest valid step for the backlight (inclusive). ASUS=3,
    /// Lenovo IdeaPad LEDs report 1 (on/off) or 2 (tristate).</summary>
    int KbdMaxBrightness { get; }

    /// <summary>Set TUF keyboard RGB color.</summary>
    void SetKeyboardRgb(byte r, byte g, byte b);

    // Events

    /// <summary>Fired when an ASUS WMI hotkey event occurs (Fn keys, lid, etc.).</summary>
    event Action<int>? WmiEvent;

    /// <summary>Fired for configurable key bindings (m4, fnf4, fnf5).</summary>
    event Action<string>? KeyBindingEvent;

    /// <summary>Fired when platform_profile changes outside the app (e.g. the
    /// firmware thermal-mode hotkey Fn+Q on Lenovo). Payload is the new
    /// kernel profile token ("balanced", "performance", ...).</summary>
    event Action<string>? PlatformProfileChanged;

    /// <summary>Start listening for WMI/evdev events.</summary>
    void SubscribeEvents();

    // Feature detection

    /// <summary>Number of controllable fans (2 = CPU+GPU, 3 = CPU+GPU+Mid).</summary>
    int FanCount { get; }

    /// <summary>Check if a sysfs attribute exists (feature is supported).</summary>
    bool IsFeatureSupported(string feature);

    /// <summary>
    /// True if GPU Eco mode switching is available - via sysfs (dgpu_disable)
    /// or raw WMI debugfs (when raw_wmi opt-in is enabled and firmware supports it).
    /// </summary>
    bool IsGpuEcoAvailable();

    /// <summary>
    /// True if the user could conceivably toggle the GPU backend selector
    /// (ASUS WMI dgpu, NVIDIA dGPU detected, PCI mode already on, block
    /// artifacts on disk, or nvidia/nouveau modules installed). Drives the
    /// visibility of the GPU Backend checkbox in the Extra window.
    /// </summary>
    bool CanToggleGpuBackend();
}

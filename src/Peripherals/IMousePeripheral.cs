using GHelper.Linux.Peripherals.Mouse;

namespace GHelper.Linux.Peripherals;

/// <summary>
/// Common interface for all mouse peripherals (ASUS, Logitech, etc.).
/// Implemented by AsusMouse and LogitechMouse. Consumed by MouseWindow
/// and PeripheralsProvider. Capability methods (Has*, Supported*) drive
/// UI visibility -- sections are hidden when a feature is unsupported.
/// </summary>
public interface IMousePeripheral : IPeripheral, IDisposable
{
    int ProfileCount();
    int DPIProfileCount();
    uint MaxDPI();
    uint MinDPI();
    uint DPIIncrement();
    int MaxBrightness();
    bool HasXYDPI();
    bool HasAngleSnapping();
    bool HasAngleTuning();
    bool HasDPIColors();
    bool HasAutoPowerOff();
    bool HasLowBatteryWarning();
    bool HasDebounce();
    bool HasAcceleration();
    bool HasMotionSync();
    bool HasZoneMode();
    bool CanChangeDPICount();
    int AngleTuningStep();
    bool HasSmartShift();
    bool HasHiResScroll();
    bool HasThumbWheel();
    bool HasHaptic();
    bool HasForceSensing();
    bool HasAnalogButtons();
    bool HasPointerSpeed();
    bool HasChangeHost();
    bool HasCrown();
    bool HasOnboardProfiles();
    PollingRate[] SupportedPollingrates();
    LightingMode[] SupportedLightingModes();
    LightingZone[] SupportedLightingZones();

    int DpiProfile { get; set; }
    int CurrentDPIProfileCount { get; set; }
    AsusMouseDPI[] DpiSettings { get; }
    PollingRate PollingRate { get; set; }
    bool AngleSnapping { get; set; }
    int AngleAdjustmentDegrees { get; set; }
    DebounceTime Debounce { get; set; }
    bool MotionSync { get; set; }
    bool ZoneMode { get; set; }
    PowerOffSetting PowerOff { get; set; }
    LiftOffDistance LiftOff { get; set; }
    byte LowBatteryWarning { get; set; }
    LightingSetting[] LightingSettings { get; }
    bool SmartShiftRatchet { get; set; }
    int SmartShiftThreshold { get; set; }
    bool HiResScrollEnabled { get; set; }
    bool ThumbWheelDivert { get; set; }
    bool ThumbWheelInvert { get; set; }
    bool HiResScrollInvert { get; set; }
    bool HiResScrollDivert { get; set; }
    bool HapticEnabled { get; set; }
    int HapticLevel { get; set; }
    int PointerSpeed { get; set; }
    int HostCount { get; set; }
    int CurrentHost { get; set; }
    bool CrownSmooth { get; set; }
    bool CrownDivert { get; set; }
    bool OnboardProfileEnabled { get; set; }

    bool HasSensitivitySwitch();
    bool HasIdleEffect();
    bool HasIdleTimeout();
    bool HasSleepTimeout();
    bool HasHapticWaveform();
    bool HasBacklightDelay();
    bool HasHandDetection();
    bool HasSideScrolling();
    bool HasLowresMode();
    bool SensitivitySwitch { get; set; }
    int IdleEffectIndex { get; set; }
    int IdleTimeoutSeconds { get; set; }
    int SleepTimeoutSeconds { get; set; }
    int HapticWaveformIndex { get; set; }
    int BacklightDelayHandsIn { get; set; }
    int BacklightDelayHandsOut { get; set; }
    int BacklightDelayPowered { get; set; }
    bool HandDetection { get; set; }
    bool SideScrolling { get; set; }
    int LowresModeIndex { get; set; }

    bool IsDeviceConnected();
    void Connect();
    void Disconnect();

    void WriteDPI();
    void WritePollingRate();
    void WriteLiftOffDistance();
    void WriteDebounce();
    void WriteLightingSetting();
    void WriteMotionSync();
    void WriteAngleSnapping();
    void WritePowerOff();
    void WriteLowBatteryWarning();
    void WriteSmartShift();
    void WriteHiResScroll();
    void WriteThumbWheel();
    void WriteHaptic();
    void WritePointerSpeed();
    void WriteChangeHost();
    void WriteCrown();
    void WriteOnboardMode();
    void WriteSensitivitySwitch();
    void WriteIdleEffect();
    void WriteIdleTimeout();
    void WriteSleepTimeout();
    void PlayHapticWaveform();
    void WriteBacklightDelays();
    void WriteHandDetection();
    void WriteSideScrolling();
    void WriteLowresMode();
    void WriteColorDirect(Avalonia.Media.Color color);
}

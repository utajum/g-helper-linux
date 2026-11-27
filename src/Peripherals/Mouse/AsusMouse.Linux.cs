namespace GHelper.Linux.Peripherals.Mouse;

/// <summary>
/// IMousePeripheral for the ported Windows AsusMouse. AsusMouse.cs stays a copy of
/// Windows G-Helper; the Linux-only mapping lives here.
/// </summary>
public abstract partial class AsusMouse : IMousePeripheral
{
    uint IMousePeripheral.MaxDPI() => (uint)MaxDPI();
    uint IMousePeripheral.MinDPI() => (uint)MinDPI();
    uint IMousePeripheral.DPIIncrement() => (uint)DPIIncrements();
    bool IMousePeripheral.HasDebounce() => HasDebounceSetting();

    LightingMode[] IMousePeripheral.SupportedLightingModes() =>
        HasRGB() ? Enum.GetValues<LightingMode>().Where(IsLightingModeSupported).ToArray() : [];
    LightingZone[] IMousePeripheral.SupportedLightingZones() => HasRGB() ? SupportedLightingZones() : [];

    // Windows counts DPI profiles from 1, the shared UI from 0.
    int IMousePeripheral.DpiProfile { get => Math.Max(DpiProfile - 1, 0); set => DpiProfile = value + 1; }
    int IMousePeripheral.CurrentDPIProfileCount { get => CurrentDPIProfileCount; set => CurrentDPIProfileCount = value; }
    PollingRate IMousePeripheral.PollingRate { get => PollingRate; set => PollingRate = value; }
    bool IMousePeripheral.AngleSnapping { get => AngleSnapping; set => AngleSnapping = value; }
    int IMousePeripheral.AngleAdjustmentDegrees { get => AngleAdjustmentDegrees; set => AngleAdjustmentDegrees = (short)value; }
    DebounceTime IMousePeripheral.Debounce { get => Debounce; set => Debounce = value; }
    bool IMousePeripheral.MotionSync { get => MotionSync; set => MotionSync = value; }
    bool IMousePeripheral.ZoneMode { get => ZoneMode; set => ZoneMode = value; }
    PowerOffSetting IMousePeripheral.PowerOff { get => PowerOffSetting; set => PowerOffSetting = value; }
    LiftOffDistance IMousePeripheral.LiftOff { get => LiftOffDistance; set => LiftOffDistance = value; }
    byte IMousePeripheral.LowBatteryWarning { get => (byte)LowBatteryWarning; set => LowBatteryWarning = value; }
    LightingSetting[] IMousePeripheral.LightingSettings => LightingSetting;

    void IMousePeripheral.Disconnect() => Dispose();

    void IMousePeripheral.WriteDPI()
    {
        if (DpiProfile < 1 || DpiProfile > DpiSettings.Length)
            return;
        if (DpiSettings[DpiProfile - 1] is { } dpi)
            SetDPIForProfile(dpi, DpiProfile);
        SetDPIProfile(DpiProfile);
    }

    void IMousePeripheral.WritePollingRate() => SetPollingRate(PollingRate);
    void IMousePeripheral.WriteLiftOffDistance() => SetLiftOffDistance(LiftOffDistance);
    void IMousePeripheral.WriteDebounce() => SetDebounce(Debounce);
    void IMousePeripheral.WriteMotionSync() => SetMotionSync(MotionSync);
    void IMousePeripheral.WriteAngleSnapping() => SetAngleSnapping(AngleSnapping);
    void IMousePeripheral.WritePowerOff() => SetEnergySettings(LowBatteryWarning, PowerOffSetting);
    void IMousePeripheral.WriteLowBatteryWarning() => SetEnergySettings(LowBatteryWarning, PowerOffSetting);

    void IMousePeripheral.WriteLightingSetting()
    {
        var zones = SupportedLightingZones();
        for (int i = 0; i < zones.Length && i < LightingSetting.Length; i++)
            SetLightingSetting(LightingSetting[i], zones[i]);
    }

    void IMousePeripheral.WriteColorDirect(Avalonia.Media.Color color) =>
        WriteColorDirect(System.Drawing.Color.FromArgb(color.R, color.G, color.B));

    // Logitech-only features
    bool IMousePeripheral.HasSmartShift() => false;
    bool IMousePeripheral.HasHiResScroll() => false;
    bool IMousePeripheral.HasThumbWheel() => false;
    bool IMousePeripheral.HasHaptic() => false;
    bool IMousePeripheral.HasForceSensing() => false;
    bool IMousePeripheral.HasAnalogButtons() => false;
    bool IMousePeripheral.HasPointerSpeed() => false;
    bool IMousePeripheral.HasChangeHost() => false;
    bool IMousePeripheral.HasCrown() => false;
    bool IMousePeripheral.HasOnboardProfiles() => false;
    bool IMousePeripheral.HasSensitivitySwitch() => false;
    bool IMousePeripheral.HasIdleEffect() => false;
    bool IMousePeripheral.HasIdleTimeout() => false;
    bool IMousePeripheral.HasSleepTimeout() => false;
    bool IMousePeripheral.HasHapticWaveform() => false;
    bool IMousePeripheral.HasBacklightDelay() => false;
    bool IMousePeripheral.HasHandDetection() => false;
    bool IMousePeripheral.HasSideScrolling() => false;
    bool IMousePeripheral.HasLowresMode() => false;
    bool IMousePeripheral.SmartShiftRatchet { get; set; }
    int IMousePeripheral.SmartShiftThreshold { get; set; }
    bool IMousePeripheral.HiResScrollEnabled { get; set; }
    bool IMousePeripheral.ThumbWheelDivert { get; set; }
    bool IMousePeripheral.ThumbWheelInvert { get; set; }
    bool IMousePeripheral.HiResScrollInvert { get; set; }
    bool IMousePeripheral.HiResScrollDivert { get; set; }
    bool IMousePeripheral.HapticEnabled { get; set; }
    int IMousePeripheral.HapticLevel { get; set; }
    int IMousePeripheral.PointerSpeed { get; set; }
    int IMousePeripheral.HostCount { get; set; }
    int IMousePeripheral.CurrentHost { get; set; }
    bool IMousePeripheral.CrownSmooth { get; set; }
    bool IMousePeripheral.CrownDivert { get; set; }
    bool IMousePeripheral.OnboardProfileEnabled { get; set; }
    bool IMousePeripheral.SensitivitySwitch { get; set; }
    int IMousePeripheral.IdleEffectIndex { get; set; }
    int IMousePeripheral.IdleTimeoutSeconds { get; set; }
    int IMousePeripheral.SleepTimeoutSeconds { get; set; }
    int IMousePeripheral.HapticWaveformIndex { get; set; }
    int IMousePeripheral.BacklightDelayHandsIn { get; set; }
    int IMousePeripheral.BacklightDelayHandsOut { get; set; }
    int IMousePeripheral.BacklightDelayPowered { get; set; }
    bool IMousePeripheral.HandDetection { get; set; }
    bool IMousePeripheral.SideScrolling { get; set; }
    int IMousePeripheral.LowresModeIndex { get; set; }
    void IMousePeripheral.WriteSmartShift() { }
    void IMousePeripheral.WriteHiResScroll() { }
    void IMousePeripheral.WriteThumbWheel() { }
    void IMousePeripheral.WriteHaptic() { }
    void IMousePeripheral.WritePointerSpeed() { }
    void IMousePeripheral.WriteChangeHost() { }
    void IMousePeripheral.WriteCrown() { }
    void IMousePeripheral.WriteOnboardMode() { }
    void IMousePeripheral.WriteSensitivitySwitch() { }
    void IMousePeripheral.WriteIdleEffect() { }
    void IMousePeripheral.WriteIdleTimeout() { }
    void IMousePeripheral.WriteSleepTimeout() { }
    void IMousePeripheral.PlayHapticWaveform() { }
    void IMousePeripheral.WriteBacklightDelays() { }
    void IMousePeripheral.WriteHandDetection() { }
    void IMousePeripheral.WriteSideScrolling() { }
    void IMousePeripheral.WriteLowresMode() { }
}

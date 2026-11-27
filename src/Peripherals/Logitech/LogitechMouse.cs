using Avalonia.Media;
using GHelper.Linux.Helpers;
using GHelper.Linux.Peripherals.Logitech.HidPP;
using GHelper.Linux.Peripherals.Mouse;
using HidSharp;

namespace GHelper.Linux.Peripherals.Logitech;

/// <summary>
/// Base class for all Logitech mouse peripherals. Communicates via HID++ 2.0
/// through <see cref="HidPPDevice"/> and discovers capabilities at runtime
/// from the device's feature set. Model subclasses override display name,
/// DPI range, polling rates, and other hardware-specific defaults.
/// </summary>
public class LogitechMouse : IMousePeripheral
{
    public const ushort LOGITECH_VID = 0x046D;

    protected HidPPDevice? _device;
    protected readonly ushort _productId;
    protected readonly bool _wireless;
    protected readonly string _path;

    public bool IsDeviceReady { get; protected set; }
    /// <summary>True when the HID handle is dead and needs re-detect.</summary>
    public bool IsDeviceStale => _device?.IsDead ?? true;
    public bool Wireless => _wireless;
    public int Battery { get; protected set; } = -1;
    public bool Charging { get; protected set; }

    public int DpiProfile { get; set; }
    public int CurrentDPIProfileCount { get; set; } = 1;
    public PollingRate PollingRate { get; set; } = PollingRate.PR1000Hz;
    public bool AngleSnapping { get; set; }
    public int AngleAdjustmentDegrees { get; set; }
    public DebounceTime Debounce { get; set; }
    public bool Acceleration { get; set; }
    public bool Deceleration { get; set; }
    public bool MotionSync { get; set; }
    public bool ZoneMode { get; set; }
    public PowerOffSetting PowerOff { get; set; }
    public LiftOffDistance LiftOff { get; set; }
    public byte LowBatteryWarning { get; set; }

    private AsusMouseDPI[]? _dpiSettings;
    public AsusMouseDPI[] DpiSettings
    {
        get
        {
            if (_dpiSettings is null)
            {
                _dpiSettings = new AsusMouseDPI[DPIProfileCount()];
                for (int i = 0; i < _dpiSettings.Length; i++)
                    _dpiSettings[i] = new AsusMouseDPI { DPI = 800 };
            }
            return _dpiSettings;
        }
    }

    private LightingSetting[]? _lightingSettings;
    public LightingSetting[] LightingSettings
    {
        get
        {
            if (_lightingSettings is null)
            {
                var zones = SupportedLightingZones();
                _lightingSettings = new LightingSetting[zones.Length];
                for (int i = 0; i < zones.Length; i++)
                    _lightingSettings[i] = new LightingSetting();
            }
            return _lightingSettings;
        }
    }

    public bool SmartShiftRatchet { get; set; }
    public int SmartShiftThreshold { get; set; }
    public int SmartShiftTorque { get; set; }
    public bool HiResScrollEnabled { get; set; }
    public bool HiResScrollInvert { get; set; }
    public bool HiResScrollDivert { get; set; }
    public bool ThumbWheelDivert { get; set; }
    public bool ThumbWheelInvert { get; set; }
    public int PointerSpeed { get; set; } = 256;
    public int HostCount { get; set; } = 1;
    public int CurrentHost { get; set; }
    public bool CrownSmooth { get; set; }
    public bool CrownDivert { get; set; }
    public bool OnboardProfileEnabled { get; set; }
    public List<(ushort Cid, string Name, bool Divertable, bool Diverted)> ReprogButtons { get; } = new();
    public List<(ushort Id, string Name, bool Enabled, int EnableIndex)> Gestures { get; } = new();
    public List<(ushort Id, string Name, bool Diverted, int DivertIndex)> GestureDiverts { get; } = new();
    public List<(int Index, string Name, int Value, int Max)> GestureParams { get; } = new();
    private readonly Dictionary<int, int> _gestureParamSizes = new();

    // Keyboard features
    public bool FnInversion { get; set; }
    public int OsPlatform { get; set; }
    public int OsPlatformCount { get; set; }
    public bool GKeyDivert { get; set; }
    public int MKeyLeds { get; set; }
    public bool MrKeyLed { get; set; }
    public string DisabledKeysSummary { get; set; } = "-";

    // Headset features
    public int HeadsetSidetone { get; set; }
    public int HeadsetMicGain { get; set; } = 50;
    public bool HeadsetMicMute { get; set; }
    public bool HeadsetMicSnrEnabled { get; set; }
    public bool HeadsetAiNoise { get; set; }
    public int HeadsetAiNoiseLevel { get; set; } = 50;
    public bool HeadsetDoNotDisturb { get; set; }
    public bool HeadsetEcoMode { get; set; }
    public int HeadsetAudioMix { get; set; } = 50;
    public int HeadsetEqIndex { get; set; }
    public int HeadsetEqPresetIndex { get; set; }
    public bool HeadsetAdvancedEq { get; set; }
    public int HeadsetAutoSleepMinutes { get; set; } = 15;
    public bool HeadsetPowerMgmt { get; set; }
    public int HeadsetOnboardEffectIndex { get; set; }
    public bool HeadsetPerZoneLighting { get; set; }

    // Deferred-set HID++ feature exposure
    public bool SensitivitySwitch { get; set; }
    public int IdleEffectIndex { get; set; }
    public int IdleTimeoutSeconds { get; set; }
    public int SleepTimeoutSeconds { get; set; }
    public int HapticWaveformIndex { get; set; } = 1;
    public int BacklightDelayHandsIn { get; set; }
    public int BacklightDelayHandsOut { get; set; }
    public int BacklightDelayPowered { get; set; }
    public bool HandDetection { get; set; }
    public bool SideScrolling { get; set; }
    public int LowresModeIndex { get; set; }

    protected int _maxDpi = 16000;
    protected int _minDpi = 200;
    protected int _dpiStep = 50;
    protected PollingRate[] _supportedPollingRates = [];
    protected int _rgbZoneCount;
    protected ushort _rgbFeature;
    protected ushort[][] _rgbZoneEffects = [];
    protected (byte control, byte events)? _rgbPriorControl;
    protected bool _lightingWritten;
    protected bool _hasBattery;
    protected bool _hasDpi;
    protected bool _hasPollingRate;
    protected bool _hasSmartShift;
    protected bool _hasHiResScroll;
    protected bool _hasRgb;
    protected bool _hasBacklight;
    protected bool _hasThumbWheel;
    protected bool _hasReprogControls;
    protected bool _hasGesture;
    protected bool _hasLowResWheel;
    protected bool _hasPointerSpeed;
    protected bool _hasChangeHost;
    protected bool _hasCrown;
    protected bool _hasOnboardProfiles;
    protected bool _hasBrightnessControl;
    protected bool _hasHaptic;
    protected bool _hasForceSensing;
    protected bool _hasAnalogButtons;
    // Keyboard
    protected bool _hasFnInversion;
    protected bool _hasOsPlatform;
    protected bool _hasGKey;
    protected bool _hasMKey;
    protected bool _hasMrKey;
    protected bool _hasDisableKeys;
    // Headset
    protected bool _hasSidetone;
    protected bool _hasMicGain;
    protected bool _hasMicMute;
    protected bool _hasMicSnr;
    protected bool _hasAiNoise;
    protected bool _hasDoNotDisturb;
    protected bool _hasEcoMode;
    protected bool _hasAudioMix;
    protected bool _hasHeadsetEq;
    protected bool _hasAdvancedEq;
    protected bool _hasAutoSleep;
    protected bool _hasPowerMgmt;
    protected bool _hasOnboardEffect;
    protected bool _hasPerZoneLighting;
    protected bool _useSmartShiftEnhanced;
    protected bool _useLegacyHiRes;

    protected LogitechMouse(ushort productId, string path, bool wireless)
    {
        _productId = productId;
        _path = path;
        _wireless = wireless;
    }

    public virtual string GetDisplayName() => "Logitech Mouse";
    public virtual int ProfileCount() => 1;
    public virtual int DPIProfileCount() => 1;
    public virtual uint MaxDPI() => (uint)_maxDpi;
    public virtual uint MinDPI() => (uint)_minDpi;
    public virtual uint DPIIncrement() => (uint)_dpiStep;
    public virtual int MaxBrightness() => 100;

    public virtual bool HasXYDPI() => false;
    public virtual bool HasAngleSnapping() => false;
    public virtual bool HasAngleTuning() => false;
    public virtual bool HasDPIColors() => false;
    public virtual bool HasAutoPowerOff() => false;
    public virtual bool HasLowBatteryWarning() => false;
    public virtual bool HasDebounce() => false;
    public virtual bool HasAcceleration() => false;
    public virtual bool HasMotionSync() => false;
    public virtual bool HasZoneMode() => false;
    public virtual bool CanChangeDPICount() => false;
    public virtual int AngleTuningStep() => 0;
    public virtual bool HasBattery() => _hasBattery || _wireless;
    public virtual bool HasSmartShift() => _hasSmartShift;
    public virtual bool HasHiResScroll() => _hasHiResScroll;
    public virtual bool HasThumbWheel() => _hasThumbWheel;
    public virtual bool HasReprogControls() => _hasReprogControls;
    public virtual bool HasGesture() => _hasGesture;
    public virtual bool HasPointerSpeed() => _hasPointerSpeed;
    public virtual bool HasChangeHost() => _hasChangeHost;
    public virtual bool HasCrown() => _hasCrown;
    public virtual bool HasOnboardProfiles() => _hasOnboardProfiles;
    public virtual bool HasHaptic() => _hasHaptic;
    public virtual bool HasForceSensing() => _hasForceSensing;
    public virtual bool HasAnalogButtons() => _hasAnalogButtons;
    public virtual bool HasFnInversion() => _hasFnInversion;
    public virtual bool HasOsPlatform() => _hasOsPlatform;
    public virtual bool HasGKey() => _hasGKey;
    public virtual bool HasMKey() => _hasMKey;
    public virtual bool HasMrKey() => _hasMrKey;
    public virtual bool HasDisableKeys() => _hasDisableKeys;
    public virtual bool HasSidetone() => _hasSidetone;
    public virtual bool HasMicGain() => _hasMicGain;
    public virtual bool HasMicMute() => _hasMicMute;
    public virtual bool HasMicSnr() => _hasMicSnr;
    public virtual bool HasAiNoise() => _hasAiNoise;
    public virtual bool HasDoNotDisturb() => _hasDoNotDisturb;
    public virtual bool HasEcoMode() => _hasEcoMode;
    public virtual bool HasAudioMix() => _hasAudioMix;
    public virtual bool HasHeadsetEq() => _hasHeadsetEq;
    public virtual bool HasAdvancedEq() => _hasAdvancedEq;
    public virtual bool HasAutoSleep() => _hasAutoSleep || _hasPowerMgmt;
    public virtual bool HasPowerMgmt() => false;
    public virtual bool HasOnboardEffect() => _hasOnboardEffect;
    public virtual bool HasPerZoneLighting() => _hasPerZoneLighting;
    // No HID++ feature backs these; the old writes hit unrelated functions (0x1B04 reset, 0x8071 power mode).
    // Hand detection / side scroll are HID++ 1.0 register 0x01 bits, unreachable on 2.0 devices.
    public virtual bool HasSensitivitySwitch() => false;
    public virtual bool HasIdleEffect() => false;
    public virtual bool HasIdleTimeout() => false;
    public virtual bool HasSleepTimeout() => false;
    public virtual bool HasHapticWaveform() => _hasHaptic;
    public virtual bool HasBacklightDelay() => _hasBacklight;
    public virtual bool HasHandDetection() => false;
    public virtual bool HasSideScrolling() => false;
    public virtual bool HasLowresMode() => _hasLowResWheel;
    public virtual bool HasKeyboardFeatures() =>
        _hasFnInversion || _hasOsPlatform || _hasGKey || _hasMKey || _hasMrKey || _hasDisableKeys
        || _hasBacklight;
    public virtual bool HasHeadsetFeatures() =>
        _hasSidetone || _hasMicGain || _hasMicMute || _hasMicSnr || _hasAiNoise ||
        _hasDoNotDisturb || _hasEcoMode || _hasAudioMix || _hasHeadsetEq ||
        _hasAdvancedEq || _hasAutoSleep || _hasPowerMgmt || _hasOnboardEffect || _hasPerZoneLighting;

    public bool HapticEnabled { get; set; }
    public int HapticLevel { get; set; } = 50;
    public List<(byte Index, int Current, int Min, int Max)> ForceSensingButtons { get; } = new();
    public int AnalogButtonCount { get; set; }
    public int AnalogMaxActuation { get; set; }
    public int AnalogMaxRapidTrigger { get; set; }
    public int AnalogMaxHaptics { get; set; }
    public List<(byte Index, int Actuation, int RapidTrigger, int Haptics)> AnalogButtons { get; } = new();

    public virtual PollingRate[] SupportedPollingrates() =>
        _supportedPollingRates.Length > 0
            ? _supportedPollingRates
            : [PollingRate.PR125Hz, PollingRate.PR250Hz, PollingRate.PR500Hz, PollingRate.PR1000Hz];

    public virtual LightingMode[] SupportedLightingModes()
    {
        if (!_hasRgb)
            return [LightingMode.Off];
        LightingMode[] all = [LightingMode.Off, LightingMode.Static, LightingMode.Breathing, LightingMode.ColorCycle, LightingMode.Rainbow];
        // Modes zone 0 reports; the fixed list when its effects are unreadable.
        LightingMode[] offered = _rgbZoneEffects.Length > 0
            ? all.Where(m => LightingModeToHidppEffects(m).Any(id => Array.IndexOf(_rgbZoneEffects[0], id) >= 0)).ToArray()
            : [];
        return offered.Length > 0 ? offered : all[..4];
    }

    public virtual LightingZone[] SupportedLightingZones() =>
        _rgbZoneCount > 0 || _hasBrightnessControl ? [LightingZone.Logo] : [];

    /// <summary>Scans HidSharp and sysfs for a HID device matching LOGITECH_VID and the model PID.</summary>
    public virtual bool IsDeviceConnected()
    {
        if (_productId == 0)
            return false;

        // HidSharp path (USB devices).
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(LOGITECH_VID, _productId))
            {
                if (string.IsNullOrEmpty(_path) || d.DevicePath.Contains(_path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): device scan failed: {ex.Message}");
        }

        // Sysfs fallback (BT devices that HidSharp cannot enumerate).
        return FindHidrawViaSysfs() is not null;
    }

    /// <summary>
    /// Opens HidPPDevice, pings for protocol version, discovers features,
    /// and reads capability metadata (DPI range, RGB zones, battery, etc.).
    /// </summary>
    public virtual void Connect()
    {
        try
        {
            var hidDevice = FindHidDevice();
            if (hidDevice is not null)
            {
                _device = new HidPPDevice(hidDevice, 0xFF);
            }
            else
            {
                // Sysfs fallback for BT devices.
                string? rawPath = FindHidrawViaSysfs();
                if (rawPath is null)
                    throw new IOException($"No HID device found for VID={LOGITECH_VID:X4} PID={_productId:X4}");

                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): using raw hidraw at {rawPath}");
                _device = new HidPPDevice(rawPath, 0xFF);
            }

            _device.Open();

            float proto = _device.Ping();
            if (proto == 0)
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ping returned 0 (device may be off)");

            if (proto >= 2.0f)
                _device.DiscoverFeatures();

            DiscoverCapabilities();

            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): connected, protocol {_device.ProtocolVersion:F1}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): connect failed - {ex.Message}");
            throw;
        }
    }

    /// <summary>Disposes the HidPPDevice and clears ready state.</summary>
    public virtual void Disconnect()
    {
        if (_device is not null && !_device.IsDead && _rgbPriorControl is { } prior)
        {
            try
            { HidPPProtocol.ReleaseRGBControl(_device, prior.control, prior.events); }
            catch { }
        }
        _rgbPriorControl = null;
        _device?.Dispose();
        _device = null;
        IsDeviceReady = false;
    }

    /// <summary>
    /// Scans /sys/class/hidraw for a device matching LOGITECH_VID and this model's
    /// PID. Returns the /dev/hidrawN path, or null if not found. This finds BT
    /// and I2C devices that HidSharp cannot enumerate.
    /// </summary>
    protected string? FindHidrawViaSysfs()
    {
        string target = $":{LOGITECH_VID:X8}:{_productId:X8}".ToUpperInvariant();
        try
        {
            foreach (string dir in Directory.GetDirectories("/sys/class/hidraw"))
            {
                string uevent = Path.Combine(dir, "device", "uevent");
                if (!File.Exists(uevent))
                    continue;

                foreach (string line in File.ReadLines(uevent))
                {
                    // HID_ID=BBBB:VVVVVVVV:PPPPPPPP
                    if (!line.StartsWith("HID_ID=", StringComparison.Ordinal))
                        continue;

                    string hidId = line[7..].ToUpperInvariant();
                    if (!hidId.EndsWith(target, StringComparison.Ordinal))
                        break;

                    string devName = Path.GetFileName(dir);
                    string devPath = $"/dev/{devName}";
                    if (File.Exists(devPath))
                        return devPath;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): sysfs scan failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Finds the HidSharp HidDevice for this mouse. Picks the interface whose
    /// path matches the path hint, or the first one with a usable HID++ interface.
    /// </summary>
    protected virtual HidDevice? FindHidDevice()
    {
        try
        {
            HidDevice? fallback = null;
            foreach (var d in DeviceList.Local.GetHidDevices(LOGITECH_VID, _productId))
            {
                try
                {
                    if (!string.IsNullOrEmpty(_path)
                        && d.DevicePath.Contains(_path, StringComparison.OrdinalIgnoreCase))
                        return d;

                    if (d.GetMaxOutputReportLength() >= 20)
                        return d;

                    fallback ??= d;
                }
                catch
                {
                    // Unreadable descriptor, skip.
                }
            }
            return fallback;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): FindHidDevice failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Reads DPI, polling rate, RGB, and battery capabilities from the device's
    /// HID++ feature set and stores the results in protected fields.
    /// </summary>
    protected virtual void DiscoverCapabilities()
    {
        if (_device is null)
            return;

        if (_device.ProtocolVersion < 2.0f)
        {
            DiscoverLegacyRegisters();
            return;
        }

        if (_device.GetFeatureIndex(Feature.ADJUSTABLE_DPI) >= 0
            || _device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_DPI) >= 0)
        {
            _hasDpi = true;
            try
            {
                var range = HidPPProtocol.GetDPIRange(_device);
                if (range is not null)
                {
                    _minDpi = range.Value.min;
                    _maxDpi = range.Value.max;
                    if (range.Value.step > 0)
                        _dpiStep = range.Value.step;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): DPI discovery failed: {ex.Message}");
            }
        }

        _hasPollingRate = _device.GetFeatureIndex(Feature.REPORT_RATE) >= 0
                       || _device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE) >= 0;

        // 0x8070 first: a volatile effect there needs no software-control claim.
        _rgbFeature = _device.GetFeatureIndex(Feature.COLOR_LED_EFFECTS) >= 0 ? Feature.COLOR_LED_EFFECTS
                    : _device.GetFeatureIndex(Feature.RGB_EFFECTS) >= 0 ? Feature.RGB_EFFECTS
                    : (ushort)0;
        if (_rgbFeature != 0)
        {
            _hasRgb = true;
            _rgbPriorControl = null;
            try
            {
                _rgbZoneCount = HidPPProtocol.GetRGBZoneCount(_device, _rgbFeature);
                _rgbZoneEffects = new ushort[_rgbZoneCount][];
                for (int zone = 0; zone < _rgbZoneCount; zone++)
                    _rgbZoneEffects[zone] = HidPPProtocol.GetRGBZoneEffectIds(_device, _rgbFeature, (byte)zone);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): RGB zone discovery failed: {ex.Message}");
            }
        }

        _hasBattery = _device.GetFeatureIndex(Feature.UNIFIED_BATTERY) >= 0
                   || _device.GetFeatureIndex(Feature.BATTERY_STATUS) >= 0
                   || _device.GetFeatureIndex(Feature.BATTERY_VOLTAGE) >= 0;

        _hasBacklight = _device.GetFeatureIndex(Feature.BACKLIGHT2) >= 0;

        _useSmartShiftEnhanced = _device.GetFeatureIndex(Feature.SMART_SHIFT_ENHANCED) >= 0;
        _hasSmartShift = _device.GetFeatureIndex(Feature.SMART_SHIFT) >= 0
                      || _useSmartShiftEnhanced;

        if (_device.GetFeatureIndex(Feature.HIRES_WHEEL) >= 0)
        {
            _hasHiResScroll = true;
        }
        else if (_device.GetFeatureIndex(Feature.HI_RES_SCROLLING) >= 0)
        {
            _hasHiResScroll = true;
            _useLegacyHiRes = true;
        }

        _hasThumbWheel = _device.GetFeatureIndex(Feature.THUMB_WHEEL) >= 0;
        _hasCrown = _device.GetFeatureIndex(Feature.CROWN) >= 0;

        _hasReprogControls = _device.GetFeatureIndex(Feature.REPROG_CONTROLS_V4) >= 0
                          || _device.GetFeatureIndex(Feature.REPROG_CONTROLS_V2) >= 0;
        if (_hasReprogControls)
            ReadReprogControls();

        _hasGesture = _device.GetFeatureIndex(Feature.GESTURE_2) >= 0;

        _hasLowResWheel = _device.GetFeatureIndex(Feature.LOWRES_WHEEL) >= 0;

        _hasPointerSpeed = _device.GetFeatureIndex(Feature.POINTER_SPEED) >= 0;
        _hasChangeHost = _device.GetFeatureIndex(Feature.CHANGE_HOST) >= 0;
        _hasOnboardProfiles = _device.GetFeatureIndex(Feature.ONBOARD_PROFILES) >= 0;
        _hasBrightnessControl = _device.GetFeatureIndex(Feature.BRIGHTNESS_CONTROL) >= 0;
        _hasHaptic = _device.GetFeatureIndex(Feature.HAPTIC) >= 0;
        _hasForceSensing = _device.GetFeatureIndex(Feature.FORCE_SENSING_BUTTON) >= 0;
        _hasAnalogButtons = _device.GetFeatureIndex(Feature.ANALOG_BUTTONS) >= 0;

        // Keyboard features
        _hasFnInversion = _device.GetFeatureIndex(Feature.FN_INVERSION) >= 0
                       || _device.GetFeatureIndex(Feature.NEW_FN_INVERSION) >= 0
                       || _device.GetFeatureIndex(Feature.K375S_FN_INVERSION) >= 0;
        _hasOsPlatform = _device.GetFeatureIndex(Feature.MULTIPLATFORM) >= 0
                      || _device.GetFeatureIndex(Feature.DUALPLATFORM) >= 0;
        _hasGKey = _device.GetFeatureIndex(Feature.GKEY) >= 0;
        _hasMKey = _device.GetFeatureIndex(Feature.MKEYS) >= 0;
        _hasMrKey = _device.GetFeatureIndex(Feature.MR) >= 0;
        _hasDisableKeys = _device.GetFeatureIndex(Feature.KEYBOARD_DISABLE_KEYS) >= 0;

        // Headset features
        _hasSidetone = _device.GetFeatureIndex(Feature.SIDETONE) >= 0
                    || _device.GetFeatureIndex(Feature.HEADSET_AUDIO_SIDETONE) >= 0;
        _hasMicGain = _device.GetFeatureIndex(Feature.HEADSET_MIC_GAIN) >= 0;
        _hasMicMute = _device.GetFeatureIndex(Feature.HEADSET_MIC_MUTE) >= 0;
        _hasMicSnr = _device.GetFeatureIndex(Feature.HEADSET_MIC_SNR) >= 0;
        _hasAiNoise = _device.GetFeatureIndex(Feature.HEADSET_AI_NOISE_REDUCTION) >= 0;
        _hasDoNotDisturb = _device.GetFeatureIndex(Feature.HEADSET_DO_NOT_DISTURB) >= 0;
        _hasEcoMode = _device.GetFeatureIndex(Feature.HEADSET_BATTERY_SAVER) >= 0;
        _hasAudioMix = _device.GetFeatureIndex(Feature.HEADSET_MIX) >= 0;
        _hasHeadsetEq = _device.GetFeatureIndex(Feature.EQUALIZER) >= 0
                     || _device.GetFeatureIndex(Feature.HEADSET_EQ) >= 0
                     || _device.GetFeatureIndex(Feature.HEADSET_ONBOARD_EQ) >= 0;
        _hasAdvancedEq = _device.GetFeatureIndex(Feature.HEADSET_ADVANCED_PARA_EQ) >= 0;
        _hasAutoSleep = _device.GetFeatureIndex(Feature.CENTURION_AUTO_SLEEP) >= 0;
        // ADC power-off minutes exist from feature version 2.
        _hasPowerMgmt = _device.GetFeatureIndex(Feature.ADC_MEASUREMENT) >= 0
                     && _device.GetFeatureVersion(Feature.ADC_MEASUREMENT) >= 2;
        _hasOnboardEffect = _device.GetFeatureIndex(Feature.HEADSET_RGB_ONBOARD_EFFECTS) >= 0;
        _hasPerZoneLighting = _device.GetFeatureIndex(Feature.HEADSET_RGB_HOSTMODE) >= 0;
    }

    /// <summary>
    /// Discovers capabilities on HID++ 1.0 devices by probing each known register
    /// (battery 0x07/0x0D, dpi 0x63, fn-swap 0x09, mouse-button-flags 0x01,
    /// keyboard hand-detection 0x01). A register is considered present when the
    /// device returns a non-error reply.
    /// </summary>
    protected virtual void DiscoverLegacyRegisters()
    {
        if (_device is null)
            return;

        // Battery: 0x07 or 0x0D probe
        if (_device.ReadRegister(0x0D) is not null || _device.ReadRegister(0x07) is not null)
            _hasBattery = true;

        // DPI register 0x63
        var dpi = _device.ReadRegister(0x63);
        if (dpi is not null && dpi.Length >= 5)
        {
            _hasDpi = true;
            _maxDpi = 1500;
            _minDpi = 100;
            _dpiStep = 100;
            int raw = dpi[2];
            if (raw >= 0x81 && raw <= 0x8F)
                DpiSettings[0].DPI = (uint)((raw - 0x80) * 100);
        }

        // FN swap register 0x09
        var fn = _device.ReadRegister(0x09);
        if (fn is not null && fn.Length >= 5)
        {
            _hasFnInversion = true;
            FnInversion = (fn[3] & 0x01) != 0;
        }

        // MOUSE_BUTTON_FLAGS register 0x01 holds smooth-scroll (0x40) and side-scroll (0x02) bits
        var flags = _device.ReadRegister(0x01);
        if (flags is not null && flags.Length >= 5)
        {
            byte b = flags[2];
            _hasHiResScroll = true;
            HiResScrollEnabled = (b & 0x40) != 0;
            SideScrolling = (b & 0x02) != 0;
        }

        // KEYBOARD_HAND_DETECTION also lives in 0x01 p2 mask 0x30 (inverted)
        if (flags is not null && flags.Length >= 5)
        {
            HandDetection = (flags[4] & 0x30) == 0;
        }
    }

    // ---- Settings persistence ----

    private string? _persistKey;

    /// <summary>Config key for this device. Format: logi_{PID}[_{UnitId}].</summary>
    public string PersistKey
    {
        get
        {
            if (_persistKey == null)
            {
                string? uid = null;
                try
                { uid = _device != null ? HidPPProtocol.GetUnitId(_device) : null; }
                catch { }
                _persistKey = uid != null
                    ? $"logi_{_productId:X4}_{uid}"
                    : $"logi_{_productId:X4}";
            }
            return _persistKey;
        }
    }

    /// <summary>Save user-modifiable settings to AppConfig.</summary>
    public void SaveSettings()
    {
        var d = new Dictionary<string, object>();

        // DPI
        var dpiArr = new List<int>();
        foreach (var s in DpiSettings)
            dpiArr.Add((int)s.DPI);
        d["dpi"] = dpiArr;
        d["dpi_profile"] = DpiProfile;

        // Polling rate
        d["polling_rate"] = (int)PollingRate;

        // SmartShift
        if (_hasSmartShift)
        {
            d["smart_shift_ratchet"] = SmartShiftRatchet;
            d["smart_shift_threshold"] = SmartShiftThreshold;
            d["smart_shift_torque"] = SmartShiftTorque;
        }

        // Hi-Res scroll
        if (_hasHiResScroll)
        {
            d["hires_scroll"] = HiResScrollEnabled;
            d["hires_invert"] = HiResScrollInvert;
            d["hires_divert"] = HiResScrollDivert;
        }

        // Pointer speed
        if (_hasPointerSpeed)
            d["pointer_speed"] = PointerSpeed;

        // Thumb wheel
        if (_hasThumbWheel)
        {
            d["thumb_divert"] = ThumbWheelDivert;
            d["thumb_invert"] = ThumbWheelInvert;
        }

        // Crown
        if (_hasCrown)
        {
            d["crown_smooth"] = CrownSmooth;
            d["crown_divert"] = CrownDivert;
        }

        // Lighting (per zone, using Export/Import wire format as hex)
        if (_hasRgb || _hasBacklight)
        {
            var zones = new List<string>();
            foreach (var ls in LightingSettings)
                zones.Add(Convert.ToHexString(ls.Export()));
            d["lighting"] = zones;
            d["lighting_set"] = _lightingWritten;
        }

        AppConfig.SetObject(PersistKey, d);
        Logger.WriteLine($"LogitechMouse({GetDisplayName()}): saved settings to {PersistKey}");
    }

    /// <summary>Load saved settings from config and write them to the device.
    /// Called after SynchronizeDevice so we know which features exist.</summary>
    public void ApplySavedSettings()
    {
        var d = AppConfig.GetObject(PersistKey);
        if (d == null)
            return;

        Logger.WriteLine($"LogitechMouse({GetDisplayName()}): restoring saved settings from {PersistKey}");
        try
        {
            // DPI
            if (d.TryGetValue("dpi", out var dpiObj) && dpiObj is System.Text.Json.JsonElement dpiEl
                && dpiEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int i = 0;
                foreach (var v in dpiEl.EnumerateArray())
                {
                    if (i < DpiSettings.Length && v.TryGetInt32(out int dpi))
                        DpiSettings[i].DPI = (uint)dpi;
                    i++;
                }
                if (_hasDpi)
                { try { WriteDPI(); } catch { } }
            }

            if (d.TryGetValue("dpi_profile", out var dpObj) && dpObj is System.Text.Json.JsonElement dpEl
                && dpEl.TryGetInt32(out int prof))
            {
                DpiProfile = prof;
            }

            // Polling rate
            if (d.TryGetValue("polling_rate", out var prObj) && prObj is System.Text.Json.JsonElement prEl
                && prEl.TryGetInt32(out int pr) && _hasPollingRate
                && SupportedPollingrates().Contains((PollingRate)pr))
            {
                PollingRate = (PollingRate)pr;
                try
                { WritePollingRate(); }
                catch { }
            }

            // SmartShift
            if (_hasSmartShift)
            {
                if (TryGetBool(d, "smart_shift_ratchet", out bool r))
                    SmartShiftRatchet = r;
                if (TryGetInt(d, "smart_shift_threshold", out int t))
                    SmartShiftThreshold = t;
                if (TryGetInt(d, "smart_shift_torque", out int tq))
                    SmartShiftTorque = tq;
                try
                { WriteSmartShift(); }
                catch { }
            }

            // Hi-Res scroll
            if (_hasHiResScroll)
            {
                if (TryGetBool(d, "hires_scroll", out bool h))
                    HiResScrollEnabled = h;
                if (TryGetBool(d, "hires_invert", out bool hi))
                    HiResScrollInvert = hi;
                if (TryGetBool(d, "hires_divert", out bool hd))
                    HiResScrollDivert = hd;
                try
                { WriteHiResScroll(); }
                catch { }
            }

            // Pointer speed
            if (_hasPointerSpeed && TryGetInt(d, "pointer_speed", out int ps))
            {
                PointerSpeed = ps;
                try
                { WritePointerSpeed(); }
                catch { }
            }

            // Thumb wheel
            if (_hasThumbWheel)
            {
                if (TryGetBool(d, "thumb_divert", out bool td))
                    ThumbWheelDivert = td;
                if (TryGetBool(d, "thumb_invert", out bool ti))
                    ThumbWheelInvert = ti;
                try
                { WriteThumbWheel(); }
                catch { }
            }

            // Crown
            if (_hasCrown)
            {
                if (TryGetBool(d, "crown_smooth", out bool cs))
                    CrownSmooth = cs;
                if (TryGetBool(d, "crown_divert", out bool cd))
                    CrownDivert = cd;
                try
                { WriteCrown(); }
                catch { }
            }

            // Lighting. RGB restores only what the user set: older configs saved untouched defaults,
            // and a restore now takes RGB control from the firmware.
            bool lightingSet = TryGetBool(d, "lighting_set", out bool ls) && ls;
            if ((_hasRgb ? lightingSet : _hasBacklight) && d.TryGetValue("lighting", out var ltObj)
                && ltObj is System.Text.Json.JsonElement ltEl
                && ltEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int i = 0;
                foreach (var v in ltEl.EnumerateArray())
                {
                    if (i < LightingSettings.Length && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        try
                        {
                            byte[] bytes = Convert.FromHexString(v.GetString()!);
                            LightingSettings[i].Import(bytes);
                        }
                        catch { }
                    }
                    i++;
                }
                try
                { WriteLightingSetting(); }
                catch { }
            }

            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): settings restored");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): restore failed - {ex.Message}");
        }
    }

    private static bool TryGetBool(Dictionary<string, object> d, string key, out bool value)
    {
        value = false;
        if (!d.TryGetValue(key, out var obj) || obj is not System.Text.Json.JsonElement el)
            return false;
        if (el.ValueKind == System.Text.Json.JsonValueKind.True)
        { value = true; return true; }
        if (el.ValueKind == System.Text.Json.JsonValueKind.False)
        { value = false; return true; }
        return false;
    }

    private static bool TryGetInt(Dictionary<string, object> d, string key, out int value)
    {
        value = 0;
        if (!d.TryGetValue(key, out var obj) || obj is not System.Text.Json.JsonElement el)
            return false;
        return el.TryGetInt32(out value);
    }

    /// <summary>Reads all device state (battery, DPI, polling rate, lighting, scroll, etc.).</summary>
    public virtual void SynchronizeDevice()
    {
        try
        {
            ReadBattery();
            ReadDPI();
            ReadPollingRate();
            ReadLighting();
            ReadSmartShift();
            ReadHiResScroll();
            ReadThumbWheel();
            ReadCrown();
            ReadPointerSpeed();
            ReadHostInfo();
            ReadGestures();
            ReadGestureDiverts();
            ReadGestureParams();
            ReadOnboardMode();
            ReadHaptic();
            ReadForceSensing();
            ReadAnalogButtons();
            ReadExtraSettings();

            IsDeviceReady = true;
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): synchronised OK");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): sync failed - {ex.Message}");
        }
    }

    public virtual void ReadBattery()
    {
        if (_device is null)
            return;
        try
        {
            if (HasBattery())
            {
                var bat = HidPPProtocol.GetBattery(_device);
                if (bat is not null)
                {
                    // -1 = no reading while charging; keep the last level.
                    if (bat.Value.level >= 0)
                        Battery = bat.Value.level;
                    Charging = bat.Value.charging;
                    return;
                }
            }

            // HID++ 1.0 register fallback for legacy devices that lack the
            // unified/voltage/status features. Tries 0x0D (BATTERY_CHARGE) then 0x07 (BATTERY_STATUS).
            ReadBatteryRegister();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): battery read failed - {ex.Message}");
        }
    }

    private void ReadBatteryRegister()
    {
        if (_device is null)
            return;

        // Register replies are [subId, register, p0, p1, p2].
        // BATTERY_CHARGE (0x0D): p0 = charge%, p2 high nibble = status (0x50 recharging, 0x90 full).
        var reply = _device.ReadRegister(0x0D);
        if (reply is not null && reply.Length >= 5)
        {
            int charge = reply[2];
            byte status = (byte)(reply[4] & 0xF0);
            if (charge > 0 || status != 0)
            {
                Battery = charge;
                Charging = status == 0x50 || status == 0x90;
                _hasBattery = true;
                return;
            }
        }

        // BATTERY_STATUS (0x07): p0 = level code, p1 = charging byte.
        reply = _device.ReadRegister(0x07);
        if (reply is null || reply.Length < 5)
            return;

        byte sb = reply[2];
        byte cb = reply[3];
        int level = sb switch { 7 => 100, 5 => 70, 3 => 30, 1 => 10, _ => 0 };
        bool charging = (cb & 0x21) == 0x21 || (cb & 0x22) == 0x22;
        if (level > 0 || charging)
        {
            Battery = level;
            Charging = charging;
            _hasBattery = true;
        }
    }

    protected virtual void ReadDPI()
    {
        if (_device is null || !_hasDpi)
            return;
        try
        {
            var dpi = HidPPProtocol.GetDPI(_device);
            if (dpi is > 0 && DpiSettings.Length > 0)
                DpiSettings[DpiProfile].DPI = (uint)dpi.Value;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): DPI read failed - {ex.Message}");
        }
    }

    protected virtual void ReadPollingRate()
    {
        if (_device is null || !_hasPollingRate)
            return;
        try
        {
            // 0x8061 first: devices with both report the real rate there.
            var ext = HidPPProtocol.GetExtendedPollingRate(_device);
            if (ext is not null)
            {
                _supportedPollingRates = ext.Value.supportedIndices.Select(ExtendedIndexToPollingRate).ToArray();
                PollingRate = ExtendedIndexToPollingRate(ext.Value.currentIndex);
                return;
            }
            var rate = HidPPProtocol.GetPollingRate(_device);
            if (rate is not null)
            {
                _supportedPollingRates = rate.Value.supportedMs.Where(ms => ms is 1 or 2 or 4 or 8).Select(MsToPollingRate).ToArray();
                PollingRate = MsToPollingRate(rate.Value.currentMs);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): polling rate read failed - {ex.Message}");
        }
    }

    protected virtual void ReadLighting()
    {
        if (_device is null)
            return;

        // RGB_EFFECTS (0x8071) has no getter for the active effect; 0x8070 has one on some devices.
        if (_rgbFeature == Feature.COLOR_LED_EFFECTS)
        {
            try
            {
                for (int zone = 0; zone < LightingSettings.Length && zone < _rgbZoneEffects.Length; zone++)
                {
                    var cur = HidPPProtocol.GetRGBZoneEffect(_device, (byte)zone);
                    if (cur is null || cur.Value.index >= _rgbZoneEffects[zone].Length)
                        continue;
                    ushort id = _rgbZoneEffects[zone][cur.Value.index];
                    if (HidppEffectToLightingMode(id) is not { } mode)
                        continue;
                    var ls = LightingSettings[zone];
                    ls.Mode = mode;
                    if (id is 0x01 or 0x02 or 0x0A)
                    {
                        ls.R = cur.Value.effectParams[0];
                        ls.G = cur.Value.effectParams[1];
                        ls.B = cur.Value.effectParams[2];
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): lighting read failed - {ex.Message}");
            }
        }

        if (_hasBrightnessControl)
        {
            try
            {
                var brightness = HidPPProtocol.GetBrightnessControl(_device);
                if (brightness is not null && LightingSettings.Length > 0)
                    LightingSettings[0].Brightness = (byte)brightness.Value;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): brightness control read failed - {ex.Message}");
            }
        }
    }

    protected virtual void ReadSmartShift()
    {
        if (_device is null || !_hasSmartShift)
            return;
        try
        {
            if (_useSmartShiftEnhanced)
            {
                var ss = HidPPProtocol.GetSmartShiftEnhanced(_device);
                if (ss is not null)
                {
                    SmartShiftRatchet = ss.Value.ratchet;
                    SmartShiftThreshold = ss.Value.autoThreshold;
                    SmartShiftTorque = ss.Value.torque;
                }
            }
            else
            {
                var ss = HidPPProtocol.GetSmartShift(_device);
                if (ss is not null)
                {
                    SmartShiftRatchet = ss.Value.ratchet;
                    SmartShiftThreshold = ss.Value.autoThreshold;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): SmartShift read failed - {ex.Message}");
        }
    }

    protected virtual void ReadHiResScroll()
    {
        if (_device is null || !_hasHiResScroll)
            return;
        try
        {
            if (_useLegacyHiRes)
            {
                var hrs = HidPPProtocol.GetHiResScrollingLegacy(_device);
                if (hrs is not null)
                    HiResScrollEnabled = hrs.Value.enabled;
            }
            else
            {
                var hrs = HidPPProtocol.GetHiResScroll(_device);
                if (hrs is not null)
                {
                    HiResScrollEnabled = hrs.Value.hiRes;
                    HiResScrollInvert = hrs.Value.invert;
                    HiResScrollDivert = hrs.Value.divert;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): HiResScroll read failed - {ex.Message}");
        }
    }

    public virtual void WriteDPI()
    {
        if (_device is null || !_hasDpi)
            return;
        try
        {
            int dpi = (int)DpiSettings[DpiProfile].DPI;
            if (_device.ProtocolVersion < 2.0f)
            {
                byte enc = (byte)Math.Clamp((dpi / 100) + 0x80, 0x81, 0x8F);
                _device.WriteRegister(0x63, enc, 0x00, 0x00);
                return;
            }
            HidPPProtocol.SetDPI(_device, dpi);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): DPI write failed - {ex.Message}");
        }
    }

    public virtual void WritePollingRate()
    {
        if (_device is null || !_hasPollingRate)
            return;
        try
        {
            if (_device.GetFeatureIndex(Feature.EXTENDED_ADJUSTABLE_REPORT_RATE) >= 0)
                HidPPProtocol.SetExtendedPollingRate(_device, PollingRateToExtendedIndex(PollingRate));
            else
                HidPPProtocol.SetPollingRate(_device, PollingRateToMs(PollingRate));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): polling rate write failed - {ex.Message}");
        }
    }

    public virtual void WriteLightingSetting()
    {
        if (_device is null)
            return;

        _lightingWritten = true;
        if (_hasRgb && _rgbZoneCount > 0 && LightingSettings.Length > 0)
        {
            try
            {
                // Zones past the UI list take its last entry.
                for (int zone = 0; zone < _rgbZoneCount; zone++)
                {
                    var ls = LightingSettings[Math.Min(zone, LightingSettings.Length - 1)];
                    if (!WriteRgbZone((byte)zone, ls.Mode, ls.R, ls.G, ls.B, (int)ls.Speed * 1000))
                        Logger.WriteLine($"LogitechMouse({GetDisplayName()}): zone {zone} lighting {ls.Mode} not applied");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): lighting write failed - {ex.Message}");
            }
        }

        if (_hasBrightnessControl && LightingSettings.Length > 0)
        {
            try
            {
                HidPPProtocol.SetBrightnessControl(_device, LightingSettings[0].Brightness);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): brightness control write failed - {ex.Message}");
            }
        }
    }

    protected virtual void ReadThumbWheel()
    {
        if (_device is null || !_hasThumbWheel)
            return;
        try
        {
            var tw = HidPPProtocol.GetThumbWheel(_device);
            if (tw is not null)
            {
                ThumbWheelDivert = tw.Value.divert;
                ThumbWheelInvert = tw.Value.invert;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ThumbWheel read failed - {ex.Message}");
        }
    }

    protected virtual void ReadPointerSpeed()
    {
        if (_device is null || !_hasPointerSpeed)
            return;
        try
        {
            var speed = HidPPProtocol.GetPointerSpeed(_device);
            if (speed is not null)
                PointerSpeed = speed.Value;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): PointerSpeed read failed - {ex.Message}");
        }
    }

    protected virtual void ReadHostInfo()
    {
        if (_device is null || !_hasChangeHost)
            return;
        try
        {
            var info = HidPPProtocol.GetHostInfo(_device);
            if (info is not null)
            {
                HostCount = info.Value.hostCount;
                CurrentHost = info.Value.currentHost;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): HostInfo read failed - {ex.Message}");
        }
    }

    public virtual void WriteSmartShift()
    {
        if (_device is null || !_hasSmartShift)
            return;
        try
        {
            if (_useSmartShiftEnhanced)
                HidPPProtocol.SetSmartShiftEnhanced(_device, SmartShiftRatchet,
                    SmartShiftThreshold, SmartShiftTorque);
            else
                HidPPProtocol.SetSmartShift(_device, SmartShiftRatchet, SmartShiftThreshold);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): SmartShift write failed - {ex.Message}");
        }
    }

    public virtual void WriteHiResScroll()
    {
        if (_device is null || !_hasHiResScroll)
            return;
        try
        {
            if (_device.ProtocolVersion < 2.0f)
            {
                WriteMouseButtonFlagsBit(0x40, HiResScrollEnabled);
                return;
            }
            if (_useLegacyHiRes)
                HidPPProtocol.SetHiResScrollingLegacy(_device, HiResScrollEnabled);
            else
                HidPPProtocol.SetHiResScroll(_device, HiResScrollEnabled, HiResScrollInvert, HiResScrollDivert);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): HiResScroll write failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Read-modify-write a single bit in the HID++ 1.0 MOUSE_BUTTON_FLAGS
    /// register at 0x01. Used for smooth-scroll (bit 0x40) and side-scroll (bit 0x02).
    /// </summary>
    private void WriteMouseButtonFlagsBit(byte mask, bool value)
    {
        if (_device is null)
            return;
        var cur = _device.ReadRegister(0x01);
        if (cur is null || cur.Length < 5)
            return;
        byte cur0 = cur[2];
        if (value)
            cur0 |= mask;
        else
            cur0 &= (byte)~mask;
        _device.WriteRegister(0x01, cur0, cur[3], cur[4]);
    }

    public virtual void WriteThumbWheel()
    {
        if (_device is null || !_hasThumbWheel)
            return;
        try
        {
            HidPPProtocol.SetThumbWheel(_device, ThumbWheelDivert, ThumbWheelInvert);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ThumbWheel write failed - {ex.Message}");
        }
    }

    protected virtual void ReadCrown()
    {
        if (_device is null || !_hasCrown)
            return;
        try
        {
            var crown = HidPPProtocol.GetCrown(_device);
            if (crown is not null)
            {
                CrownSmooth = crown.Value.smoothScroll;
                CrownDivert = crown.Value.diverted;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Crown read failed - {ex.Message}");
        }
    }

    public virtual void WriteCrown()
    {
        if (_device is null || !_hasCrown)
            return;
        try
        {
            HidPPProtocol.SetCrown(_device, CrownSmooth, CrownDivert);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Crown write failed - {ex.Message}");
        }
    }

    protected virtual void ReadOnboardMode()
    {
        if (_device is null || !_hasOnboardProfiles)
            return;
        try
        {
            var mode = HidPPProtocol.GetOnboardMode(_device);
            if (mode is not null)
                OnboardProfileEnabled = mode.Value;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): OnboardMode read failed - {ex.Message}");
        }
    }

    public virtual void WriteOnboardMode()
    {
        if (_device is null || !_hasOnboardProfiles)
            return;
        try
        {
            HidPPProtocol.SetOnboardMode(_device, OnboardProfileEnabled);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): OnboardMode write failed - {ex.Message}");
        }
    }

    public virtual void WritePointerSpeed()
    {
        if (_device is null || !_hasPointerSpeed)
            return;
        try
        {
            HidPPProtocol.SetPointerSpeed(_device, PointerSpeed);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): PointerSpeed write failed - {ex.Message}");
        }
    }

    public virtual void WriteChangeHost()
    {
        if (_device is null || !_hasChangeHost)
            return;
        try
        {
            HidPPProtocol.SetChangeHost(_device, CurrentHost);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ChangeHost write failed - {ex.Message}");
        }
    }

    protected virtual void ReadReprogControls()
    {
        if (_device is null || !_hasReprogControls)
            return;
        try
        {
            var controls = HidPPProtocol.GetReprogControlsList(_device);
            ReprogButtons.Clear();
            foreach (var c in controls)
            {
                bool divertable = (c.flags & 0x20) != 0;
                bool diverted = false;
                if (divertable)
                {
                    byte? rep = HidPPProtocol.GetCidReporting(_device, c.cid);
                    diverted = rep is not null && (rep.Value & 0x01) != 0;
                }
                ReprogButtons.Add((c.cid, c.name, divertable, diverted));
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ReprogControls read failed - {ex.Message}");
        }
    }

    public virtual void WriteReprogDivert(ushort cid, bool diverted)
    {
        if (_device is null || !_hasReprogControls)
            return;
        try
        {
            HidPPProtocol.SetCidReporting(_device, cid, diverted);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ReprogDivert write failed - {ex.Message}");
        }
    }

    protected virtual void ReadGestures()
    {
        if (_device is null || !_hasGesture)
            return;
        try
        {
            var list = HidPPProtocol.GetGestureList(_device);
            Gestures.Clear();
            foreach (var g in list)
                Gestures.Add((g.gestureId, g.name, g.enabled, g.enableIndex));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Gestures read failed - {ex.Message}");
        }
    }

    public virtual void WriteGesture(int enableIndex, bool enabled)
    {
        if (_device is null || !_hasGesture)
            return;
        try
        {
            HidPPProtocol.SetGestureEnabled(_device, enableIndex, enabled);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Gesture write failed - {ex.Message}");
        }
    }

    protected virtual void ReadGestureDiverts()
    {
        if (_device is null || !_hasGesture)
            return;
        try
        {
            var list = HidPPProtocol.GetGestureDivertList(_device);
            GestureDiverts.Clear();
            foreach (var g in list)
                GestureDiverts.Add((g.gestureId, g.name, g.diverted, g.divertIndex));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): GestureDiverts read failed - {ex.Message}");
        }
    }

    public virtual void WriteGestureDivert(int divertIndex, bool diverted)
    {
        if (_device is null || !_hasGesture)
            return;
        try
        {
            HidPPProtocol.SetGestureDiverted(_device, divertIndex, diverted);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): GestureDivert write failed - {ex.Message}");
        }
    }

    protected virtual void ReadGestureParams()
    {
        if (_device is null || !_hasGesture)
            return;
        try
        {
            var list = HidPPProtocol.GetGestureParams(_device);
            GestureParams.Clear();
            _gestureParamSizes.Clear();
            foreach (var p in list)
            {
                GestureParams.Add((p.index, p.name, p.currentValue, p.maxValue));
                _gestureParamSizes[p.index] = p.size;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): GestureParams read failed - {ex.Message}");
        }
    }

    public virtual void WriteGestureParam(int paramIndex, int value)
    {
        if (_device is null || !_hasGesture)
            return;
        try
        {
            if (_gestureParamSizes.TryGetValue(paramIndex, out int size))
                HidPPProtocol.SetGestureParam(_device, paramIndex, value, size);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"LogitechMouse({GetDisplayName()}): GestureParam write failed - {ex.Message}");
        }
    }

    protected virtual void ReadHaptic()
    {
        if (_device is null || !_hasHaptic)
            return;
        try
        {
            var h = HidPPProtocol.GetHapticLevel(_device);
            if (h is not null)
            { HapticEnabled = h.Value.enabled; HapticLevel = h.Value.level; }
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Haptic read failed - {ex.Message}"); }
    }

    public virtual void WriteHaptic()
    {
        if (_device is null || !_hasHaptic)
            return;
        try
        { HidPPProtocol.SetHapticLevel(_device, HapticEnabled, HapticLevel); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Haptic write failed - {ex.Message}"); }
    }

    protected virtual void ReadForceSensing()
    {
        if (_device is null || !_hasForceSensing)
            return;
        try
        {
            ForceSensingButtons.Clear();
            ForceSensingButtons.AddRange(HidPPProtocol.GetForceSensingButtons(_device));
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ForceSensing read failed - {ex.Message}"); }
    }

    public virtual void WriteForceSensingButton(byte index, int force)
    {
        if (_device is null || !_hasForceSensing)
            return;
        try
        { HidPPProtocol.SetForceSensingButton(_device, index, force); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): ForceSensing write failed - {ex.Message}"); }
    }

    protected virtual void ReadAnalogButtons()
    {
        if (_device is null || !_hasAnalogButtons)
            return;
        try
        {
            var ab = HidPPProtocol.GetAnalogButtons(_device);
            if (ab is null)
                return;
            AnalogButtonCount = ab.Value.buttonCount;
            AnalogMaxActuation = ab.Value.maxActuation;
            AnalogMaxRapidTrigger = ab.Value.maxRapidTrigger;
            AnalogMaxHaptics = ab.Value.maxHaptics;
            AnalogButtons.Clear();
            AnalogButtons.AddRange(ab.Value.buttons);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): AnalogButtons read failed - {ex.Message}"); }
    }

    public virtual void WriteAnalogButton(byte index, int actuation, int rapidTrigger, int haptics)
    {
        if (_device is null || !_hasAnalogButtons)
            return;
        try
        { HidPPProtocol.SetAnalogButton(_device, index, actuation, rapidTrigger, haptics); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): AnalogButton write failed - {ex.Message}"); }
    }

    /// <summary>Reads keyboard (Fn inversion, OS, backlight, disabled keys) and headset (sidetone, power-off) settings.</summary>
    protected virtual void ReadExtraSettings()
    {
        if (_device is null || _device.ProtocolVersion < 2.0f)
            return;
        try
        {
            if (_hasFnInversion && HidPPProtocol.GetFnInversion(_device) is bool fn)
                FnInversion = fn;

            if (_hasOsPlatform)
            {
                var os = HidPPProtocol.GetOsPlatform(_device);
                if (os is null)
                    _hasOsPlatform = false;
                else
                {
                    OsPlatformCount = os.Value.count;
                    if (os.Value.platform < os.Value.count)
                        OsPlatform = os.Value.platform;
                }
            }

            if (_hasBacklight && HidPPProtocol.GetBacklightDelays(_device) is { } bl)
            {
                BacklightDelayHandsOut = bl.handsOut;
                BacklightDelayHandsIn = bl.handsIn;
                BacklightDelayPowered = bl.powered;
            }

            // getDisabledKeys fn 0x10 payload[0].
            if (_hasDisableKeys && _device.FeatureRequest(Feature.KEYBOARD_DISABLE_KEYS, 0x10) is { Length: >= 3 } dk)
                DisabledKeysSummary = HidPPDecode.DisabledKeysSummary(dk[2]);

            if (_hasSidetone && _device.GetFeatureIndex(Feature.HEADSET_AUDIO_SIDETONE) < 0
                && _device.FeatureRequest(Feature.SIDETONE, 0x00) is { Length: >= 3 } st)
                HeadsetSidetone = Math.Min((int)st[2], 100);

            if (_hasPowerMgmt && _device.GetFeatureIndex(Feature.CENTURION_AUTO_SLEEP) < 0
                && _device.FeatureRequest(Feature.ADC_MEASUREMENT, 0x10) is { Length: >= 3 } adc)
                HeadsetAutoSleepMinutes = adc[2];
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): settings read failed - {ex.Message}"); }
    }

    public virtual void WriteLiftOffDistance() { }
    public virtual void WriteDebounce() { }
    public virtual void WriteAcceleration() { }
    public virtual void WriteMotionSync() { }
    public virtual void WriteAngleSnapping() { }
    public virtual void WritePowerOff() { }
    public virtual void WriteLowBatteryWarning() { }

    /// <summary>Streams a single colour to all RGB zones for Aura Sync integration.</summary>
    public virtual void WriteColorDirect(Color color)
    {
        if (_device is null || !_hasRgb || _rgbZoneCount == 0)
            return;
        try
        {
            for (int zone = 0; zone < _rgbZoneCount; zone++)
                WriteRgbZone((byte)zone, LightingMode.Static, color.R, color.G, color.B, 0);
        }
        catch
        {
            // Streaming is best-effort.
        }
    }

    // ponytail: keyboard + headset writes are minimal toggles. Full register-level
    // protocol per feature was descoped; UI persists state per-device, hardware
    // writes are best-effort. Each method silently no-ops if feature absent.

    public virtual void WriteFnInversion()
    {
        if (_device is null || !_hasFnInversion)
            return;
        try
        {
            if (_device.ProtocolVersion < 2.0f)
            {
                _device.WriteRegister(0x09, 0x00, (byte)(FnInversion ? 0x01 : 0x00));
                return;
            }
            HidPPProtocol.SetFnInversion(_device, FnInversion);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): FnInversion write failed - {ex.Message}"); }
    }

    public virtual void WriteOsPlatform()
    {
        if (_device is null || !_hasOsPlatform)
            return;
        try
        {
            HidPPProtocol.SetOsPlatform(_device, OsPlatform);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): OsPlatform write failed - {ex.Message}"); }
    }

    public virtual void WriteGKeyDivert()
    {
        if (_device is null || !_hasGKey)
            return;
        try
        { _device.FeatureRequest(Feature.GKEY, 0x20, (byte)(GKeyDivert ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): GKey write failed - {ex.Message}"); }
    }

    public virtual void WriteMKeyLeds()
    {
        if (_device is null || !_hasMKey)
            return;
        try
        { _device.FeatureRequest(Feature.MKEYS, 0x10, (byte)MKeyLeds); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): MKey write failed - {ex.Message}"); }
    }

    public virtual void WriteMrKeyLed()
    {
        if (_device is null || !_hasMrKey)
            return;
        try
        { _device.FeatureRequest(Feature.MR, 0x00, (byte)(MrKeyLed ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): MrKey write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetSidetone()
    {
        if (_device is null || !_hasSidetone)
            return;
        try
        {
            int pct = Math.Clamp(HeadsetSidetone, 0, 100);
            if (_device.GetFeatureIndex(Feature.HEADSET_AUDIO_SIDETONE) < 0)
            {
                _device.FeatureRequest(Feature.SIDETONE, 0x10, (byte)pct);
                return;
            }
            // 0x0604 v1: [mic, level]; v2+: [mic, 0xFF, level] with level 0..N-1, N from fn 0x20 payload[2].
            if (_device.GetFeatureVersion(Feature.HEADSET_AUDIO_SIDETONE) <= 1)
            {
                _device.FeatureRequest(Feature.HEADSET_AUDIO_SIDETONE, 0x10, 0x01, (byte)pct);
                return;
            }
            byte[]? steps = _device.FeatureRequest(Feature.HEADSET_AUDIO_SIDETONE, 0x20);
            int n = steps is not null && steps.Length >= 5 && steps[4] > 1 ? steps[4] : 101;
            _device.FeatureRequest(Feature.HEADSET_AUDIO_SIDETONE, 0x10, 0x01, 0xFF, (byte)HidPPDecode.PercentToRange(pct, 0, n - 1));
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): Sidetone write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetMicGain()
    {
        if (_device is null || !_hasMicGain)
            return;
        try
        {
            // fn 0x00 payload [min, max] as int8; write fn 0x20 [gain] in that range.
            byte[]? info = _device.FeatureRequest(Feature.HEADSET_MIC_GAIN, 0x00);
            int min = -128, max = 127;
            if (info is not null && info.Length >= 4 && (sbyte)info[3] > (sbyte)info[2])
            { min = (sbyte)info[2]; max = (sbyte)info[3]; }
            _device.FeatureRequest(Feature.HEADSET_MIC_GAIN, 0x20, (byte)(sbyte)HidPPDecode.PercentToRange(HeadsetMicGain, min, max));
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): MicGain write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetMicMute()
    {
        if (_device is null || !_hasMicMute)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_MIC_MUTE, 0x20, (byte)(HeadsetMicMute ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): MicMute write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetMicSnr()
    {
        if (_device is null || !_hasMicSnr)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_MIC_SNR, 0x10, (byte)(HeadsetMicSnrEnabled ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): MicSnr write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetAiNoise()
    {
        if (_device is null || !_hasAiNoise)
            return;
        try
        {
            // Enable via fn 0x10; level 0-3 via fn 0x30.
            _device.FeatureRequest(Feature.HEADSET_AI_NOISE_REDUCTION, 0x10, (byte)(HeadsetAiNoise ? 0x01 : 0x00));
            if (HeadsetAiNoise)
                _device.FeatureRequest(Feature.HEADSET_AI_NOISE_REDUCTION, 0x30, (byte)HidPPDecode.PercentToRange(HeadsetAiNoiseLevel, 0, 3));
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): AiNoise write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetDoNotDisturb()
    {
        if (_device is null || !_hasDoNotDisturb)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_DO_NOT_DISTURB, 0x10, (byte)(HeadsetDoNotDisturb ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): DoNotDisturb write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetEcoMode()
    {
        if (_device is null || !_hasEcoMode)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_BATTERY_SAVER, 0x10, (byte)(HeadsetEcoMode ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): EcoMode write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetAudioMix()
    {
        if (_device is null || !_hasAudioMix)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_MIX, 0x10, (byte)HidPPDecode.PercentToRange(HeadsetAudioMix, 0, 255)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): AudioMix write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetEq()
    {
        if (_device is null || !_hasHeadsetEq)
            return;
        try
        {
            ushort feat = _device.GetFeatureIndex(Feature.HEADSET_ONBOARD_EQ) >= 0 ? Feature.HEADSET_ONBOARD_EQ
                        : _device.GetFeatureIndex(Feature.HEADSET_EQ) >= 0 ? Feature.HEADSET_EQ
                        : Feature.EQUALIZER;
            _device.FeatureRequest(feat, 0x10, (byte)HeadsetEqIndex);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): HeadsetEq write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetAdvancedEq()
    {
        if (_device is null || !_hasAdvancedEq)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_ADVANCED_PARA_EQ, 0x10, (byte)(HeadsetAdvancedEq ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): AdvancedEq write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetAutoSleep()
    {
        if (_device is null || !(_hasAutoSleep || _hasPowerMgmt))
            return;
        try
        {
            byte minutes = (byte)Math.Clamp(HeadsetAutoSleepMinutes, 0, 255);
            if (_device.GetFeatureIndex(Feature.CENTURION_AUTO_SLEEP) < 0)
            {
                // ADC_MEASUREMENT (0x1F20) v2+: fn 0x20 [power-off minutes, 0 = never].
                _device.FeatureRequest(Feature.ADC_MEASUREMENT, 0x20, minutes);
                return;
            }
            // 0x0108 timer slots: v<3 [t], v3 [reserved, t], v4+ [t, b, c]; the others go back as read.
            int version = _device.GetFeatureVersion(Feature.CENTURION_AUTO_SLEEP);
            int count = version >= 4 ? 3 : version >= 3 ? 2 : 1;
            byte[]? cur = _device.FeatureRequest(Feature.CENTURION_AUTO_SLEEP, 0x00);
            var args = new byte[count];
            if (cur is not null && cur.Length >= 2 + count)
                Array.Copy(cur, 2, args, 0, count);
            args[count == 2 ? 1 : 0] = minutes;
            _device.FeatureRequest(Feature.CENTURION_AUTO_SLEEP, 0x10, args);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): AutoSleep write failed - {ex.Message}"); }
    }

    // ADC power-off minutes go through the auto sleep slider (WriteHeadsetAutoSleep).
    public virtual void WriteHeadsetPowerMgmt() { }

    public virtual void WriteHeadsetOnboardEffect()
    {
        if (_device is null || !_hasOnboardEffect)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_RGB_ONBOARD_EFFECTS, 0x10, (byte)HeadsetOnboardEffectIndex); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): OnboardEffect write failed - {ex.Message}"); }
    }

    public virtual void WriteSensitivitySwitch() { }

    public virtual void WriteIdleEffect() { }

    public virtual void WriteIdleTimeout() { }

    public virtual void WriteSleepTimeout() { }

    public virtual void PlayHapticWaveform()
    {
        if (_device is null || !_hasHaptic)
            return;
        try
        {
            if (!HidPPProtocol.PlayHapticWaveform(_device, (byte)HapticWaveformIndex))
                Logger.WriteLine($"LogitechMouse({GetDisplayName()}): haptic waveform {HapticWaveformIndex} not played (unsupported or busy)");
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): HapticWaveform play failed - {ex.Message}"); }
    }

    public virtual void WriteBacklightDelays()
    {
        if (_device is null || !_hasBacklight)
            return;
        try
        { HidPPProtocol.SetBacklightDelays(_device, BacklightDelayHandsOut, BacklightDelayHandsIn, BacklightDelayPowered); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): BacklightDelays write failed - {ex.Message}"); }
    }

    public virtual void WriteHandDetection()
    {
        if (_device is null)
            return;
        try
        {
            // Bits 0x30 in p2 of register 0x01 disable hand-detection. value=true clears them.
            var cur = _device.ReadRegister(0x01);
            if (cur is null || cur.Length < 5)
                return;
            byte p2 = cur[4];
            if (HandDetection)
                p2 &= 0xCF;
            else
                p2 |= 0x30;
            _device.WriteRegister(0x01, cur[2], cur[3], p2);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): HandDetection write failed - {ex.Message}"); }
    }

    public virtual void WriteSideScrolling()
    {
        if (_device is null)
            return;
        try
        {
            WriteMouseButtonFlagsBit(0x02, SideScrolling);
        }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): SideScrolling write failed - {ex.Message}"); }
    }

    public virtual void WriteLowresMode()
    {
        if (_device is null || !_hasLowResWheel)
            return;
        try
        { _device.FeatureRequest(Feature.LOWRES_WHEEL, 0x10, (byte)LowresModeIndex); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): LowresMode write failed - {ex.Message}"); }
    }

    public virtual void WriteHeadsetPerZoneLighting()
    {
        if (_device is null || !_hasPerZoneLighting)
            return;
        try
        { _device.FeatureRequest(Feature.HEADSET_RGB_HOSTMODE, 0x80, (byte)(HeadsetPerZoneLighting ? 0x01 : 0x00)); }
        catch (Exception ex) { Logger.WriteLine($"LogitechMouse({GetDisplayName()}): PerZoneLighting write failed - {ex.Message}"); }
    }

    public bool CanExport() => false;
    public byte[] Export() => [];
    public bool Import(byte[] blob) => false;

    public PeripheralType DeviceType() => PeripheralType.Mouse;

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }

    /// <summary>Converts a polling interval in ms to the shared PollingRate enum.</summary>
    protected static PollingRate MsToPollingRate(int ms) => ms switch
    {
        8 => PollingRate.PR125Hz,
        4 => PollingRate.PR250Hz,
        2 => PollingRate.PR500Hz,
        1 => PollingRate.PR1000Hz,
        _ when ms <= 1 => PollingRate.PR1000Hz,
        _ => PollingRate.PR1000Hz,
    };

    /// <summary>
    /// Converts an EXTENDED_ADJUSTABLE_REPORT_RATE index to a PollingRate.
    /// Index: 0=8ms(125Hz), 1=4ms(250Hz), 2=2ms(500Hz), 3=1ms(1000Hz),
    ///        4=500us(2000Hz), 5=250us(4000Hz), 6=125us(8000Hz).
    /// </summary>
    protected static PollingRate ExtendedIndexToPollingRate(int index) => index switch
    {
        0 => PollingRate.PR125Hz,
        1 => PollingRate.PR250Hz,
        2 => PollingRate.PR500Hz,
        3 => PollingRate.PR1000Hz,
        4 => PollingRate.PR2000Hz,
        5 => PollingRate.PR4000Hz,
        6 => PollingRate.PR8000Hz,
        _ => PollingRate.PR1000Hz,
    };

    /// <summary>Converts a PollingRate to an EXTENDED_ADJUSTABLE_REPORT_RATE index.</summary>
    protected static byte PollingRateToExtendedIndex(PollingRate rate) => rate switch
    {
        PollingRate.PR125Hz => 0,
        PollingRate.PR250Hz => 1,
        PollingRate.PR500Hz => 2,
        PollingRate.PR1000Hz => 3,
        PollingRate.PR2000Hz => 4,
        PollingRate.PR4000Hz => 5,
        PollingRate.PR8000Hz => 6,
        _ => 3,
    };

    /// <summary>Converts the shared PollingRate enum to a polling interval in ms.</summary>
    protected static int PollingRateToMs(PollingRate rate) => rate switch
    {
        PollingRate.PR125Hz => 8,
        PollingRate.PR250Hz => 4,
        PollingRate.PR500Hz => 2,
        PollingRate.PR1000Hz => 1,
        _ => 1,
    };

    /// <summary>Maps an HID++ RGB effect ID to the shared LightingMode enum, or null if not modelled.</summary>
    protected static LightingMode? HidppEffectToLightingMode(ushort effect) => effect switch
    {
        0x00 => LightingMode.Off,
        0x01 => LightingMode.Static,
        0x02 or 0x0A => LightingMode.Breathing,
        0x03 or 0x15 => LightingMode.ColorCycle,
        0x04 or 0x16 => LightingMode.Rainbow,
        _ => null,
    };

    /// <summary>Maps the shared LightingMode enum to HID++ RGB effect IDs, in order of preference.</summary>
    protected static ushort[] LightingModeToHidppEffects(LightingMode mode) => mode switch
    {
        LightingMode.Off => [0x00, 0x01],
        LightingMode.Breathing => [0x0A, 0x02],
        LightingMode.ColorCycle => [0x03, 0x15],
        LightingMode.Rainbow => [0x16, 0x04],
        _ => [0x01],
    };

    /// <summary>
    /// Applies a mode to one zone using the effect index the zone reports for it. On 0x8071
    /// software control is taken on first use. Off falls back to static black.
    /// </summary>
    private bool WriteRgbZone(byte zone, LightingMode mode, byte r, byte g, byte b, int periodMs)
    {
        if (_device is null || zone >= _rgbZoneEffects.Length)
            return false;
        if (mode == LightingMode.Off)
            r = g = b = 0;

        foreach (ushort id in LightingModeToHidppEffects(mode))
        {
            int index = Array.IndexOf(_rgbZoneEffects[zone], id);
            byte[]? effectParams = index >= 0 ? HidPPDecode.BuildRgbEffectParams(id, r, g, b, periodMs) : null;
            if (effectParams is null)
                continue;
            if (_rgbFeature == Feature.RGB_EFFECTS)
            {
                _rgbPriorControl ??= HidPPProtocol.ClaimRGBControl(_device);
                if (_rgbPriorControl is null)
                    return false;
            }
            return HidPPProtocol.SetRGBZoneEffect(_device, _rgbFeature, zone, (byte)index, effectParams);
        }
        return false;
    }
}

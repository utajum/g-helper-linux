namespace GHelper.Linux.Platform.Linux.Lenovo;

/// <summary>
/// Linux implementation of IHardwareControl for Lenovo laptops using only
/// mainline kernel interfaces:
///
///   Performance modes:
///     /sys/firmware/acpi/platform_profile (lenovo-wmi-gamezone on Legion/LOQ,
///       DYTC via ideapad-laptop on IdeaPads)
///     Fallback for pre-SmartFan models (Legion Y530/Y540, Y7000 2018-2019):
///       ideapad fan_mode (0=Super Silent, 1=Standard, 4=Efficient Thermal)
///
///   Fan RPM: hwmon lenovo_wmi_other / yogafan / acpi_fan (fan{N}_input).
///   Fan curves: no mainline interface - reported unsupported.
///
///   Battery: charge_control_end_threshold when present, else the battery
///     extension charge_types (Long_Life = ~60% cap from ideapad, 80% from
///     lenovo-wmi-other on kernel 7.2+), else the deprecated conservation_mode.
///
///   PPT power limits: /sys/class/firmware-attributes/lenovo-wmi-other-0/
///     attributes/{ppt_pl1_spl,ppt_pl2_sppt,ppt_pl3_fppt}/current_value.
///     Firmware accepts writes only while platform_profile is "custom".
///
///   Keyboard backlight: /sys/class/leds/platform::kbd_backlight (max 1 or 2).
///
///   GPU MUX / Eco / panel overdrive / MiniLED: no mainline Lenovo interface,
///     reported unsupported (GPU Eco still works via the generic PCI backend).
/// </summary>
public class LinuxLenovoWmi : IHardwareControl
{
    private readonly string? _fanHwmonDir;
    private readonly string? _cpuTempHwmonDir;
    private readonly string? _batteryDir;
    private readonly string? _kbdLedDir;
    private readonly int _kbdMaxBrightness = 1;

    private Thread? _profileWatchThread;
    private volatile bool _watching;
    private int _lastFanMode = int.MinValue;
    private readonly List<FileStream> _eventStreams = new();

    public int FanCount { get; private set; } = 2;

    /// <summary>Raw WMI codes are never forwarded on Lenovo (kernel sparse
    /// keymaps handle Fn rows); this event never fires.</summary>
    public event Action<int>? WmiEvent { add { } remove { } }

    /// <summary>Fired for the configurable Lenovo keys: "novo" (Novo button,
    /// KEY_PROG1/PROG2 on "Ideapad extra buttons") and "refresh_rate"
    /// (KEY_REFRESH_RATE_TOGGLE from the ideapad WMI hotkeys).</summary>
    public event Action<string>? KeyBindingEvent;

    public event Action<string>? PlatformProfileChanged;

    public LinuxLenovoWmi()
    {
        _fanHwmonDir = LenovoSysfs.FanHwmon();
        _cpuTempHwmonDir = SysfsHelper.FindHwmonByName("coretemp")
                        ?? SysfsHelper.FindHwmonByName("k10temp");
        _batteryDir = SysfsHelper.FindBattery();
        _kbdLedDir = LenovoSysfs.KbdBacklightLed();

        if (_kbdLedDir != null)
            _kbdMaxBrightness = Math.Max(1,
                SysfsHelper.ReadInt(Path.Combine(_kbdLedDir, "max_brightness"), 1));

        if (_fanHwmonDir != null)
        {
            int fans = 0;
            for (int i = 1; i <= 4; i++)
                if (File.Exists(Path.Combine(_fanHwmonDir, $"fan{i}_input")))
                    fans = i;
            FanCount = Math.Clamp(fans, 1, 3);
        }

        SysfsHelper.LogAllHwmon();
        LenovoDetection.LogCapabilities();
    }

    // Core ACPI-equivalent methods (same device-id contract as the ASUS backend
    // so vendor-neutral callers keep working)

    public int DeviceGet(int deviceId)
    {
        return deviceId switch
        {
            0x00120075 => GetThrottleThermalPolicy(),
            0x00120057 => GetBatteryChargeLimit(),
            0x00110013 => GetFanRpm(0),
            0x00110014 => GetFanRpm(1),
            0x00110031 => GetFanRpm(2),
            0x00120094 => GetCpuTemp(),
            0x00120097 => GetGpuTemp(),
            0x00050021 => GetKeyboardBrightness(),
            _ => -1
        };
    }

    public int DeviceSet(int deviceId, int value)
    {
        return deviceId switch
        {
            0x00120075 => SetAndReturn(() => SetThrottleThermalPolicy(value)),
            0x00120057 => SetAndReturn(() => SetBatteryChargeLimit(value)),
            0x00050021 => SetAndReturn(() => SetKeyboardBrightness(value)),
            _ => -1
        };
    }

    public byte[]? DeviceGetBuffer(int deviceId, int args = 0) => null;

    private static int SetAndReturn(Action action)
    {
        try
        { action(); return 1; }
        catch { return 0; }
    }

    // Performance mode. App contract: 0=Balanced, 1=Turbo, 2=Silent.

    public int GetThrottleThermalPolicy()
    {
        // Primary: platform_profile (gamezone or DYTC)
        var profile = SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfile);
        if (profile != null)
            return MapProfileToMode(profile);

        // Fallback: ideapad fan_mode (Y530/Y540 era without SmartFan/DYTC).
        // Values: 0 Super Silent (reads back as 133 on some firmware),
        // 1 Standard, 2 Dust Cleaning, 4 Efficient Thermal Dissipation.
        var fanModePath = LenovoSysfs.IdeapadAttr("fan_mode");
        if (fanModePath != null)
        {
            int raw = SysfsHelper.ReadInt(fanModePath, -1);
            return raw switch
            {
                0 or 133 => 2,
                4 => 1,
                1 or 2 => 0,
                _ => raw < 0 ? -1 : 0
            };
        }

        return -1;
    }

    public void SetThrottleThermalPolicy(int mode)
    {
        // When platform_profile exists, ModeControl drives it through
        // IPowerManager.SetPlatformProfile - nothing to do here.
        if (LenovoDetection.HasPlatformProfile())
            return;

        // Pre-SmartFan models: map the app mode onto ideapad fan_mode.
        var fanModePath = LenovoSysfs.IdeapadAttr("fan_mode");
        if (fanModePath == null)
            return;

        int raw = mode switch
        {
            1 => 4,  // Turbo -> Efficient Thermal Dissipation
            2 => 0,  // Silent -> Super Silent
            _ => 1,  // Balanced -> Standard
        };

        if (_lastFanMode == raw)
            return;

        SysfsHelper.WriteInt(fanModePath, raw);
        _lastFanMode = raw;
    }

    private static int MapProfileToMode(string profile) => profile switch
    {
        "performance" or "balanced-performance" or "max-power" => 1,
        "low-power" or "quiet" => 2,
        // custom is the God-mode profile the PPT auto-switch lands on - it
        // belongs to whatever app mode requested the PPT write, so report
        // that mode instead of bouncing the UI to Balanced.
        "custom" => CurrentAppBaseMode(),
        _ => 0  // balanced
    };

    private static int CurrentAppBaseMode()
    {
        int baseMode = Mode.Modes.GetCurrentBase();
        return baseMode is >= 0 and <= 2 ? baseMode : 0;
    }

    // Fan control (RPM read-only; mainline exposes no Lenovo fan curve interface)

    public int GetFanRpm(int fanIndex)
    {
        if (_fanHwmonDir == null)
            return -1;
        return SysfsHelper.ReadInt(
            Path.Combine(_fanHwmonDir, $"fan{fanIndex + 1}_input"), -1);
    }

    public byte[]? GetFanCurve(int fanIndex) => null;

    public void SetFanCurve(int fanIndex, byte[] curve) { }

    public void DisableFanCurve(int fanIndex) { }

    public byte[]? ResetFanCurveToDefaults(int fanIndex) => null;

    public bool IsFanCurveEnabled(int fanIndex) => false;

    public void EnsureManualFanMode() { }

    // Battery

    public int GetBatteryChargeLimit()
    {
        // Native percent threshold (rare on Lenovo consumer lines, but honored
        // first when the kernel exposes it)
        if (_batteryDir != null)
        {
            string path = Path.Combine(_batteryDir, "charge_control_end_threshold");
            if (File.Exists(path))
            {
                int value = SysfsHelper.ReadInt(path, -1);
                if (value > 0)
                    return value;
            }
        }

        // Battery extension: Long_Life == conservation mode. charge_types reads
        // as e.g. "Standard [Long_Life]" with the active type in brackets.
        var chargeTypes = LenovoSysfs.BatteryChargeTypes();
        if (chargeTypes != null)
        {
            string? raw = SysfsHelper.ReadAttribute(chargeTypes);
            if (raw != null)
                return raw.Contains("[Long_Life]") ? ConservationCap : 100;
        }

        // Deprecated ideapad attribute
        var conservation = LenovoSysfs.IdeapadAttr("conservation_mode");
        if (conservation != null)
            return SysfsHelper.ReadInt(conservation, 0) == 1 ? ConservationCap : 100;

        return -1;
    }

    /// <summary>Long_Life cap. ideapad (~60%) always exposes conservation_mode
    /// next to charge_types; lenovo-wmi-other (80%, kernel 7.2+) only
    /// registers charge_types when ideapad lacks the feature.</summary>
    public static int ConservationCap =>
        LenovoSysfs.IdeapadAttr("conservation_mode") == null
        && LenovoSysfs.BatteryChargeTypes() != null ? 80 : 60;

    /// <summary>True when only the conservation toggle (fixed cap) backs the
    /// charge limit: no native percent threshold file. The charge-limit
    /// slider then snaps to the two real outcomes (ConservationCap/100).</summary>
    public bool UsesConservationFallback =>
        (_batteryDir == null
            || !File.Exists(Path.Combine(_batteryDir, "charge_control_end_threshold")))
        && (LenovoSysfs.BatteryChargeTypes() != null
            || LenovoSysfs.IdeapadAttr("conservation_mode") != null);

    public bool SetBatteryChargeLimit(int percent)
    {
        percent = Math.Clamp(percent, 40, 100);

        if (_batteryDir != null)
        {
            string path = Path.Combine(_batteryDir, "charge_control_end_threshold");
            if (File.Exists(path))
                return SysfsHelper.WriteInt(path, percent);
        }

        // Conservation mode is a fixed cap: enable it for any requested limit
        // at or below the cap, disable for anything above.
        bool conserve = percent <= ConservationCap;

        var chargeTypes = LenovoSysfs.BatteryChargeTypes();
        if (chargeTypes != null)
            return SysfsHelper.WriteAttribute(chargeTypes, conserve ? "Long_Life" : "Standard");

        var conservation = LenovoSysfs.IdeapadAttr("conservation_mode");
        if (conservation != null)
            return SysfsHelper.WriteInt(conservation, conserve ? 1 : 0);

        return false;
    }

    // GPU - no mainline Lenovo MUX/Eco interface. The generic PCI backend
    // (gpu_backend=pci) owns dGPU power management on Lenovo.

    public bool GetGpuEco() => false;

    public void SetGpuEco(bool enabled)
    {
        Helpers.Logger.WriteLine("LinuxLenovoWmi: no firmware dGPU disable on mainline - PCI backend owns Eco");
    }

    public int GetGpuMuxMode() => -1;

    public void SetGpuMuxMode(int mode)
    {
        Helpers.Logger.WriteLine("LinuxLenovoWmi: no MUX switch interface on mainline Lenovo");
    }

    public bool IsGpuEcoAvailable()
    {
        // Mainline Lenovo has no firmware dgpu_disable; Eco is the PCI backend
        // (driver block + reboot). Show the GPU panel whenever that path is
        // usable so Eco/Standard stay reachable, including while the dGPU is
        // hot-removed in Eco (block artifacts present, device off the bus).
        return LinuxAsusWmi.IsPciBackendUsable();
    }

    public bool CanToggleGpuBackend()
    {
        // The PCI backend is the only Eco mechanism on Lenovo; offer the
        // selector only when a second GPU exists. The old fallbacks (loaded
        // nouveau module, any amdgpu hwmon) matched iGPU-only machines like
        // the Legion Go S, where backend choice is meaningless.
        return Gpu.GPUModeControl.HasSecondGpu();
    }

    // Display - no mainline Lenovo equivalents

    public bool GetPanelOverdrive() => false;

    public bool SetPanelOverdrive(bool enabled) => false;

    public int GetMiniLedMode() => -1;

    public void SetMiniLedMode(int mode) { }

    public int GetMiniLedModeCount() => 0;

    public int GetScreenAutoBrightness() => -1;

    public void SetScreenAutoBrightness(bool enabled) { }

    // PPT / power limits (lenovo-wmi-other firmware-attributes).
    // The mainline attribute names match the ASUS legacy/fw-attr names the app
    // already uses: ppt_pl1_spl, ppt_pl2_sppt, ppt_pl3_fppt (alias ppt_fppt).

    private static string MapPptAttribute(string attribute) => attribute switch
    {
        "ppt_fppt" => "ppt_pl3_fppt",
        _ => attribute,
    };

    // Circuit-breaker: stop retrying after the first failure this session.
    // Reset when the user triggers a new mode switch (SetThrottleThermalPolicy).
    private static bool _customSwitchFailed;

    /// <summary>Reset the custom-profile circuit-breaker so the next PPT write
    /// retries the switch. Called on mode changes.</summary>
    public static void ResetCustomSwitchBreaker() => _customSwitchFailed = false;

    /// <summary>Switch platform_profile to "custom" (God mode) for PPT writes.
    /// Prefers the per-device class path (kernel 6.12+) which bypasses the
    /// legacy aggregation layer's unconditional EINVAL on "custom".</summary>
    private static bool SwitchToCustomProfile(string currentProfile)
    {
        if (_customSwitchFailed)
            return false;

        if (!LenovoDetection.HasCustomProfile())
        {
            Helpers.Logger.WriteLine(
                "LinuxLenovoWmi: PPT requires platform_profile=custom which this firmware lacks - skipping");
            _customSwitchFailed = true;
            return false;
        }

        Helpers.Logger.WriteLine(
            $"LinuxLenovoWmi: PPT requested - switching platform_profile {currentProfile} -> custom (God mode)");

        // Try per-device class path first (works on kernel 6.12+ where the
        // legacy /sys/firmware/acpi/platform_profile rejects "custom").
        var gzPath = LenovoSysfs.GamezoneProfilePath();
        if (gzPath != null)
        {
            if (SysfsHelper.WriteAttribute(gzPath, "custom"))
            {
                Thread.Sleep(100);
                return true;
            }
            Helpers.Logger.WriteLine("LinuxLenovoWmi: gamezone class path write failed, trying legacy path");
        }

        // Fallback: legacy aggregated path (works on older kernels).
        if (SysfsHelper.WriteAttribute(SysfsHelper.PlatformProfile, "custom"))
        {
            Thread.Sleep(100);
            return true;
        }

        Helpers.Logger.WriteLine("LinuxLenovoWmi: switch to custom failed on all paths - circuit-breaker tripped");
        _customSwitchFailed = true;
        return false;
    }

    public bool SetPptLimit(string attribute, int watts)
    {
        var path = LenovoSysfs.FirmwareAttrCurrentValue(MapPptAttribute(attribute));
        if (path == null)
            return false;

        // Firmware rejects writes (-EBUSY) unless platform_profile is "custom"
        var profile = SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfile);
        if (profile != null && profile != "custom")
        {
            if (!SwitchToCustomProfile(profile))
                return false;
        }

        return SysfsHelper.WriteInt(path, watts);
    }

    public int GetPptLimit(string attribute)
    {
        var path = LenovoSysfs.FirmwareAttrCurrentValue(MapPptAttribute(attribute));
        return path != null ? SysfsHelper.ReadInt(path, -1) : -1;
    }

    public AttrRange? GetAttributeRange(AttrDef attr)
    {
        if (attr == null)
            return null;

        var dir = LenovoSysfs.FirmwareAttrDir(MapPptAttribute(attr.FwAttrName))
               ?? LenovoSysfs.FirmwareAttrDir(MapPptAttribute(attr.LegacyName));
        if (dir == null)
            return null;

        int min = SysfsHelper.ReadInt(Path.Combine(dir, "min_value"), -1);
        int max = SysfsHelper.ReadInt(Path.Combine(dir, "max_value"), -1);
        int step = SysfsHelper.ReadInt(Path.Combine(dir, "scalar_increment"), 1);
        int def = SysfsHelper.ReadInt(Path.Combine(dir, "default_value"), -1);
        if (min < 0 && max < 0)
            return null;
        return new AttrRange(min, max, step <= 0 ? 1 : step, def);
    }

    // Keyboard backlight. ideapad LEDs report max 1 (on/off) or 2 (tristate);
    // writes are clamped so callers can cycle 0..max safely.

    public bool HasKbdBrightnessHwChanged { get; } =
        LenovoSysfs.KbdBacklightLed() is string led
        && File.Exists(Path.Combine(led, "brightness_hw_changed"));

    public int KbdMaxBrightness => _kbdMaxBrightness;

    public int GetKeyboardBrightness()
    {
        if (_kbdLedDir == null)
            return -1;
        return SysfsHelper.ReadInt(Path.Combine(_kbdLedDir, "brightness"), -1);
    }

    public void SetKeyboardBrightness(int level)
    {
        if (_kbdLedDir == null)
            return;
        SysfsHelper.WriteInt(Path.Combine(_kbdLedDir, "brightness"),
            Math.Clamp(level, 0, _kbdMaxBrightness));
    }

    public void SetKeyboardRgb(byte r, byte g, byte b)
    {
        // 4-zone RGB on Legion/LOQ is a separate ITE USB HID device, not a
        // platform LED - out of scope for the platform backend.
    }

    // Events. Fn+Q (thermal mode key) is handled entirely in firmware/kernel:
    // the kernel cycles platform_profile itself (DYTC) or lenovo-wmi-events
    // relays the firmware change to lenovo-wmi-gamezone. Either way the
    // observable effect is a platform_profile change, so a watcher on that
    // file is the correct hotkey integration - the app adopts the new mode.

    public void SubscribeEvents()
    {
        if (_watching)
            return;
        _watching = true;

        if (LenovoDetection.HasPlatformProfile())
        {
            _profileWatchThread = new Thread(ProfileWatchLoop)
            {
                IsBackground = true,
                Name = "LenovoProfileWatch"
            };
            _profileWatchThread.Start();
            Helpers.Logger.WriteLine("LinuxLenovoWmi: watching platform_profile for Fn+Q / external changes");
        }

        StartInputLoops();
    }

    //  evdev input: Novo button, refresh-rate key, camera lens cover 

    private const ushort EvKey = 1;
    private const ushort EvSw = 5;
    private const ushort KeyProg1 = 148;
    private const ushort KeyProg2 = 149;
    private const ushort KeyRefreshRateToggle = 0x232;
    private const ushort SwCameraLensCover = 0x09;

    /// <summary>Open the Lenovo extra-button input devices and forward their
    /// events as configurable key bindings:
    ///   "Ideapad extra buttons"     KEY_PROG1/PROG2 (Novo button) -> "novo"
    ///                               KEY_REFRESH_RATE_TOGGLE       -> "refresh_rate"
    ///   "Lenovo WMI Camera Button"  EV_SW SW_CAMERA_LENS_COVER (logged; the
    ///                               kernel input event already reaches the DE)
    /// </summary>
    private void StartInputLoops()
    {
        var devices = FindLenovoInputDevices();
        if (devices.Count == 0)
            return;

        foreach (var dev in devices)
        {
            try
            {
                var fs = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.Read);
                lock (_eventStreams)
                {
                    _eventStreams.Add(fs);
                }
                var t = new Thread(() => ReadInputEvents(fs))
                {
                    IsBackground = true,
                    Name = $"LenovoInput-{Path.GetFileName(dev)}"
                };
                t.Start();
                Helpers.Logger.WriteLine($"LinuxLenovoWmi: listening for Lenovo events on {dev}");
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"LinuxLenovoWmi: could not open {dev}: {ex.Message}");
            }
        }
    }

    private void ReadInputEvents(FileStream fs)
    {
        var buffer = new byte[24]; // struct input_event on 64-bit
        try
        {
            while (_watching)
            {
                if (fs.Read(buffer, 0, 24) != 24)
                    continue;

                ushort type = BitConverter.ToUInt16(buffer, 16);
                ushort code = BitConverter.ToUInt16(buffer, 18);
                int value = BitConverter.ToInt32(buffer, 20);

                if (type == EvKey && value == 1)
                {
                    switch (code)
                    {
                        case KeyProg1 or KeyProg2:
                            Helpers.Logger.WriteLine($"LinuxLenovoWmi: Novo button (KEY_PROG{code - KeyProg1 + 1})");
                            KeyBindingEvent?.Invoke("novo");
                            break;
                        case KeyRefreshRateToggle:
                            Helpers.Logger.WriteLine("LinuxLenovoWmi: refresh-rate toggle key");
                            KeyBindingEvent?.Invoke("refresh_rate");
                            break;
                    }
                }
                else if (type == EvSw && code == SwCameraLensCover)
                {
                    Helpers.Logger.WriteLine($"LinuxLenovoWmi: camera lens cover {(value == 1 ? "CLOSED" : "OPEN")}");
                }
            }
        }
        catch (Exception ex)
        {
            if (_watching)
                Helpers.Logger.WriteLine($"LinuxLenovoWmi: reader error on {fs.Name}: {ex.Message}");
        }
    }

    private static List<string> FindLenovoInputDevices()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists("/proc/bus/input/devices"))
                return result;

            var content = File.ReadAllText("/proc/bus/input/devices");
            foreach (var section in content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                bool wanted = section.Contains("Ideapad extra buttons", StringComparison.OrdinalIgnoreCase)
                    || section.Contains("Lenovo WMI Camera Button", StringComparison.OrdinalIgnoreCase);
                if (!wanted)
                    continue;

                foreach (var line in section.Split('\n'))
                {
                    if (!line.StartsWith("H: Handlers="))
                        continue;
                    foreach (var part in line.Split(' '))
                        if (part.StartsWith("event"))
                            result.Add($"/dev/input/{part.Trim()}");
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"LinuxLenovoWmi: FindLenovoInputDevices failed: {ex.Message}");
        }
        return result;
    }

    private void ProfileWatchLoop()
    {
        string? last = SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfile);

        while (_watching)
        {
            Thread.Sleep(1000);

            string? current;
            try
            {
                current = SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfile);
            }
            catch
            {
                continue;
            }

            if (current == null || current == last)
                continue;

            last = current;

            // The app's own writes also land here; the subscriber no-ops when
            // the mapped mode already matches, so only real external changes act.
            Helpers.Logger.WriteLine($"LinuxLenovoWmi: platform_profile changed -> {current}");
            PlatformProfileChanged?.Invoke(current);
        }
    }

    // Feature detection

    public bool IsFeatureSupported(string feature)
    {
        return feature switch
        {
            "throttle_thermal_policy" =>
                LenovoDetection.HasPlatformProfile() || LenovoDetection.HasFanMode(),
            _ => LenovoSysfs.FirmwareAttrCurrentValue(MapPptAttribute(feature)) != null,
        };
    }

    // Temperature helpers (vendor-neutral hwmon)

    private int GetCpuTemp()
    {
        if (_cpuTempHwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_cpuTempHwmonDir, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
        }

        if (Directory.Exists(SysfsHelper.Thermal))
        {
            foreach (var zone in Directory.GetDirectories(SysfsHelper.Thermal, "thermal_zone*"))
            {
                int temp = SysfsHelper.ReadInt(Path.Combine(zone, "temp"), -1);
                if (temp > 0)
                    return temp / 1000;
            }
        }

        return -1;
    }

    private int GetGpuTemp()
    {
        // Skip every NVIDIA read while the dGPU is suspended or idle with
        // runtime PM on: probing it (hwmon/NVML/nvidia-smi) wakes it or keeps
        // resetting its autosuspend timer. Fall through to the APU/amdgpu
        // sensor instead.
        bool nvSkip = Gpu.NVidia.LinuxNvidiaGpuControl.ShouldSkipDgpuTelemetry();

        var nvidiaHwmon = nvSkip ? null : SysfsHelper.FindHwmonByName("nvidia");
        if (nvidiaHwmon != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(nvidiaHwmon, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
        }

        var amdHwmon = SysfsHelper.FindHwmonByName("amdgpu");
        if (amdHwmon != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(amdHwmon, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
        }

        // Fallback: NVML via gpu-helper (~5ms, no nvidia-smi fork)
        int nvmlTemp = Gpu.NVidia.LinuxNvidiaGpuControl.GetTempViaNvml();
        if (nvmlTemp > 0)
            return nvmlTemp;

        // Last resort: nvidia-smi fork (~200ms)
        if (!nvSkip && Directory.Exists("/sys/module/nvidia"))
        {
            try
            {
                var output = SysfsHelper.RunCommand("nvidia-smi",
                    "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
                if (!string.IsNullOrWhiteSpace(output) && int.TryParse(output.Trim(), out int smiTemp) && smiTemp > 0)
                    return smiTemp;
            }
            catch { }
        }

        return -1;
    }

    public void Dispose()
    {
        _watching = false;
        lock (_eventStreams)
        {
            foreach (var fs in _eventStreams)
            {
                try
                { fs.Dispose(); }
                catch { }
            }
            _eventStreams.Clear();
        }
        GC.SuppressFinalize(this);
    }
}

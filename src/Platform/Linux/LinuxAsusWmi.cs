namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux implementation of IAsusWmi using the asus-wmi kernel module (sysfs).
/// Maps G-Helper's ATKACPI device IDs to Linux sysfs attributes.
/// 
/// Sysfs paths (require kernel 6.2+ for full feature set):
///   /sys/devices/platform/asus-nb-wmi/throttle_thermal_policy
///   /sys/devices/platform/asus-nb-wmi/panel_od
///   /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable
///   /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode
///   /sys/bus/platform/devices/asus-nb-wmi/mini_led_mode
///   /sys/devices/platform/asus-nb-wmi/ppt_*
///   /sys/devices/platform/asus-nb-wmi/nv_*
///   /sys/class/hwmon/hwmon*/fan{1,2,3}_input
///   /sys/class/hwmon/hwmon*/pwm{1,2,3}_auto_point{1-8}_{temp,pwm}
///   /sys/class/power_supply/BAT0/charge_control_end_threshold
///   /sys/class/leds/asus::kbd_backlight/brightness
///   /sys/class/leds/asus::kbd_backlight/multi_intensity
/// </summary>
public class LinuxAsusWmi : IAsusWmi
{
    private string? _asusFanRpmHwmonDir;   // Hwmon with fan*_input files (RPM reading)
    private string? _asusFanCurveHwmonDir; // Hwmon with pwm*_auto_point* files (fan curve control)
    private string? _asusBaseHwmonDir;     // Base ASUS hwmon (temps, etc.)
    private string? _cpuTempHwmonDir;      // CPU temperature hwmon (coretemp/k10temp)
    private string? _batteryDir;
    private Thread? _eventThread;
    private volatile bool _eventListening;

    public event Action<int>? WmiEvent;

    public LinuxAsusWmi()
    {
        // Discover hwmon devices — names vary by kernel version:
        //   Kernel <6.x:  "asus_nb_wmi" (single hwmon for fans + temps + curves)
        //   Kernel 6.x+:  "asus" (base, has fan*_input for RPM)
        //                  "asus_custom_fan_curve" (has pwm*_auto_point* for curve control, NO fan RPM)
        //                  "coretemp"/"k10temp" (CPU temp)
        //
        // Fan RPM: find hwmon that actually has fan1_input
        _asusFanRpmHwmonDir = SysfsHelper.FindHwmonByNameWithFile("fan1_input",
                                  "asus", "asus_nb_wmi", "asus_custom_fan_curve")
                           ?? SysfsHelper.FindHwmonByName("asus_nb_wmi")
                           ?? SysfsHelper.FindHwmonByName("asus");

        // Fan curves: prefer asus_custom_fan_curve (has pwm*_auto_point*), fallback to RPM hwmon
        _asusFanCurveHwmonDir = SysfsHelper.FindHwmonByName("asus_custom_fan_curve")
                             ?? SysfsHelper.FindHwmonByName("asus_nb_wmi")
                             ?? _asusFanRpmHwmonDir;

        _asusBaseHwmonDir = SysfsHelper.FindHwmonByName("asus")
                         ?? SysfsHelper.FindHwmonByName("asus_nb_wmi")
                         ?? _asusFanRpmHwmonDir;

        _cpuTempHwmonDir = SysfsHelper.FindHwmonByName("coretemp")   // Intel
                        ?? SysfsHelper.FindHwmonByName("k10temp");    // AMD

        _batteryDir = SysfsHelper.FindBattery();

        // Log discovery results
        SysfsHelper.LogAllHwmon();

        if (_asusFanRpmHwmonDir != null)
            Helpers.Logger.WriteLine($"ASUS fan RPM hwmon: {_asusFanRpmHwmonDir}");
        else
            Helpers.Logger.WriteLine("WARNING: No hwmon with fan*_input found. Fan RPM unavailable.");

        if (_asusFanCurveHwmonDir != null)
            Helpers.Logger.WriteLine($"ASUS fan curve hwmon: {_asusFanCurveHwmonDir}");
        else
            Helpers.Logger.WriteLine("WARNING: ASUS fan curve hwmon not found. Fan curve features unavailable.");

        if (_asusBaseHwmonDir != null)
            Helpers.Logger.WriteLine($"ASUS base hwmon: {_asusBaseHwmonDir}");

        if (_cpuTempHwmonDir != null)
            Helpers.Logger.WriteLine($"CPU temp hwmon: {_cpuTempHwmonDir}");

        if (_batteryDir != null)
            Helpers.Logger.WriteLine($"Battery found: {_batteryDir}");
    }

    // ── Core ACPI-equivalent methods ──

    public int DeviceGet(int deviceId)
    {
        // Map known device IDs to sysfs reads
        // This is the translation layer: G-Helper device ID → Linux sysfs
        return deviceId switch
        {
            0x00120075 => GetThrottleThermalPolicy(),       // PerformanceMode
            0x00120057 => GetBatteryChargeLimit(),           // BatteryLimit
            0x00050019 => GetPanelOverdrive() ? 1 : 0,      // ScreenOverdrive
            0x00090020 => GetGpuEco() ? 1 : 0,              // GPUEcoROG
            0x00090016 => GetGpuMuxMode(),                    // GPUMuxROG
            0x0005001E => GetMiniLedMode(),                   // ScreenMiniled1
            0x0005002E => GetMiniLedMode(),                   // ScreenMiniled2
            0x00110013 => GetFanRpm(0),                       // CPU_Fan
            0x00110014 => GetFanRpm(1),                       // GPU_Fan
            0x00110031 => GetFanRpm(2),                       // Mid_Fan
            0x00120094 => GetCpuTemp(),                       // Temp_CPU
            0x00120097 => GetGpuTemp(),                       // Temp_GPU
            0x00050021 => GetKeyboardBrightness(),            // TUF_KB_BRIGHTNESS
            _ => -1  // Unsupported device ID
        };
    }

    public int DeviceSet(int deviceId, int value)
    {
        return deviceId switch
        {
            0x00120075 => SetAndReturn(() => SetThrottleThermalPolicy(value)),
            0x00120057 => SetAndReturn(() => SetBatteryChargeLimit(value)),
            0x00050019 => SetAndReturn(() => SetPanelOverdrive(value != 0)),
            0x00090020 => SetAndReturn(() => SetGpuEco(value != 0)),
            0x00090016 => SetAndReturn(() => SetGpuMuxMode(value)),
            0x0005001E => SetAndReturn(() => SetMiniLedMode(value)),
            0x0005002E => SetAndReturn(() => SetMiniLedMode(value)),
            0x00050021 => SetAndReturn(() => SetKeyboardBrightness(value)),
            _ => -1
        };
    }

    public byte[]? DeviceGetBuffer(int deviceId, int args = 0)
    {
        // Fan curve buffer read
        return deviceId switch
        {
            0x00110024 => GetFanCurve(0),  // DevsCPUFanCurve
            0x00110025 => GetFanCurve(1),  // DevsGPUFanCurve
            0x00110032 => GetFanCurve(2),  // DevsMidFanCurve
            _ => null
        };
    }

    // ── Performance mode ──

    public int GetThrottleThermalPolicy()
    {
        return SysfsHelper.ReadInt(
            Path.Combine(SysfsHelper.AsusWmiPlatform, "throttle_thermal_policy"), -1);
    }

    public void SetThrottleThermalPolicy(int mode)
    {
        SysfsHelper.WriteInt(
            Path.Combine(SysfsHelper.AsusWmiPlatform, "throttle_thermal_policy"), mode);
    }

    // ── Fan control ──

    public int GetFanRpm(int fanIndex)
    {
        // Use the hwmon that has fan*_input files (RPM sensors)
        var hwmon = _asusFanRpmHwmonDir ?? _asusBaseHwmonDir;
        if (hwmon != null)
        {
            int rpm = SysfsHelper.ReadInt(
                Path.Combine(hwmon, $"fan{fanIndex + 1}_input"), -1);
            if (rpm > 0) return rpm;
        }

        // For GPU fan (index 1), try nvidia-smi as fallback
        // nvidia-smi returns percentage, we return -2 to indicate "percentage mode"
        if (fanIndex == 1)
        {
            try
            {
                var output = SysfsHelper.RunCommand("nvidia-smi", "--query-gpu=fan.speed --format=csv,noheader,nounits");
                if (!string.IsNullOrWhiteSpace(output) && int.TryParse(output.Trim(), out int fanPercent) && fanPercent >= 0)
                    return -2 - fanPercent; // Encode: -2 means "percentage", value is -(2 + percent)
            }
            catch { /* nvidia-smi not available */ }
        }

        return -1;
    }

    /// <summary>
    /// Get GPU fan speed as percentage from nvidia-smi (0-100).
    /// Returns null if unavailable.
    /// </summary>
    public int? GetGpuFanPercent()
    {
        try
        {
            var output = SysfsHelper.RunCommand("nvidia-smi", "--query-gpu=fan.speed --format=csv,noheader,nounits");
            if (!string.IsNullOrWhiteSpace(output) && int.TryParse(output.Trim(), out int fanPercent) && fanPercent >= 0)
                return fanPercent;
        }
        catch { }
        return null;
    }

    public byte[]? GetFanCurve(int fanIndex)
    {
        if (_asusFanCurveHwmonDir == null) return null;

        var curve = new byte[16];
        int pwmIndex = fanIndex + 1;

        for (int i = 0; i < 8; i++)
        {
            // Temperature in millidegrees → degrees
            int tempMilli = SysfsHelper.ReadInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_temp"), -1);
            if (tempMilli < 0) return null;
            curve[i] = (byte)(tempMilli / 1000);

            // PWM 0-255 → percentage 0-100
            int pwm = SysfsHelper.ReadInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_pwm"), -1);
            if (pwm < 0) return null;
            curve[8 + i] = (byte)(pwm * 100 / 255);
        }

        return curve;
    }

    public void SetFanCurve(int fanIndex, byte[] curve)
    {
        if (_asusFanCurveHwmonDir == null || curve.Length != 16) return;

        int pwmIndex = fanIndex + 1;

        // First set pwm_enable to 2 (automatic/curve mode)
        SysfsHelper.WriteInt(Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_enable"), 2);

        for (int i = 0; i < 8; i++)
        {
            // Degrees → millidegrees
            SysfsHelper.WriteInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_temp"),
                curve[i] * 1000);

            // Percentage 0-100 → PWM 0-255
            SysfsHelper.WriteInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_pwm"),
                curve[8 + i] * 255 / 100);
        }
    }

    // ── Battery ──

    public int GetBatteryChargeLimit()
    {
        if (_batteryDir == null) return -1;
        return SysfsHelper.ReadInt(
            Path.Combine(_batteryDir, "charge_control_end_threshold"), -1);
    }

    public void SetBatteryChargeLimit(int percent)
    {
        if (_batteryDir == null) return;
        percent = Math.Clamp(percent, 40, 100);
        SysfsHelper.WriteInt(
            Path.Combine(_batteryDir, "charge_control_end_threshold"), percent);
    }

    // ── GPU ──

    public bool GetGpuEco()
    {
        return SysfsHelper.ReadInt(
            Path.Combine(SysfsHelper.AsusBusPlatform, "dgpu_disable"), 0) == 1;
    }

    public void SetGpuEco(bool enabled)
    {
        SysfsHelper.WriteInt(
            Path.Combine(SysfsHelper.AsusBusPlatform, "dgpu_disable"), enabled ? 1 : 0);
    }

    public int GetGpuMuxMode()
    {
        return SysfsHelper.ReadInt(
            Path.Combine(SysfsHelper.AsusBusPlatform, "gpu_mux_mode"), -1);
    }

    public void SetGpuMuxMode(int mode)
    {
        SysfsHelper.WriteInt(
            Path.Combine(SysfsHelper.AsusBusPlatform, "gpu_mux_mode"), mode);
    }

    // ── Display ──

    public bool GetPanelOverdrive()
    {
        return SysfsHelper.ReadInt(
            Path.Combine(SysfsHelper.AsusWmiPlatform, "panel_od"), 0) == 1;
    }

    public void SetPanelOverdrive(bool enabled)
    {
        SysfsHelper.WriteInt(
            Path.Combine(SysfsHelper.AsusWmiPlatform, "panel_od"), enabled ? 1 : 0);
    }

    public int GetMiniLedMode()
    {
        return SysfsHelper.ReadInt(
            Path.Combine(SysfsHelper.AsusBusPlatform, "mini_led_mode"), -1);
    }

    public void SetMiniLedMode(int mode)
    {
        SysfsHelper.WriteInt(
            Path.Combine(SysfsHelper.AsusBusPlatform, "mini_led_mode"), mode);
    }

    // ── PPT / Power limits ──

    public void SetPptLimit(string attribute, int watts)
    {
        // PPT attributes: ppt_pl1_spl, ppt_pl2_sppt, ppt_fppt, nv_dynamic_boost, nv_temp_target
        SysfsHelper.WriteInt(
            Path.Combine(SysfsHelper.AsusWmiPlatform, attribute), watts);
    }

    public int GetPptLimit(string attribute)
    {
        return SysfsHelper.ReadInt(
            Path.Combine(SysfsHelper.AsusWmiPlatform, attribute), -1);
    }

    // ── Keyboard ──

    public int GetKeyboardBrightness()
    {
        var ledPath = Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "brightness");
        return SysfsHelper.ReadInt(ledPath, -1);
    }

    public void SetKeyboardBrightness(int level)
    {
        var ledPath = Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "brightness");
        SysfsHelper.WriteInt(ledPath, Math.Clamp(level, 0, 3));
    }

    public void SetKeyboardRgb(byte r, byte g, byte b)
    {
        var intensityPath = Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "multi_intensity");
        SysfsHelper.WriteAttribute(intensityPath, $"{r} {g} {b}");
    }

    // ── Temperature ──

    private int GetCpuTemp()
    {
        // Try dedicated CPU temp hwmon (coretemp/k10temp) — package temp
        if (_cpuTempHwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_cpuTempHwmonDir, "temp1_input"), -1);
            if (temp > 0) return temp / 1000;
        }

        // Try ASUS base hwmon
        if (_asusBaseHwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_asusBaseHwmonDir, "temp1_input"), -1);
            if (temp > 0) return temp / 1000;
        }

        // Fallback to thermal zones
        if (Directory.Exists(SysfsHelper.Thermal))
        {
            foreach (var zone in Directory.GetDirectories(SysfsHelper.Thermal))
            {
                var type = SysfsHelper.ReadAttribute(Path.Combine(zone, "type"));
                if (type != null && type.Contains("x86_pkg_temp", StringComparison.OrdinalIgnoreCase))
                {
                    int temp = SysfsHelper.ReadInt(Path.Combine(zone, "temp"), -1);
                    if (temp > 0) return temp / 1000;
                }
            }

            // Last resort: first thermal zone
            var fallback = Path.Combine(SysfsHelper.Thermal, "thermal_zone0", "temp");
            int fallbackTemp = SysfsHelper.ReadInt(fallback, -1);
            if (fallbackTemp > 0) return fallbackTemp / 1000;
        }

        return -1;
    }

    private int GetGpuTemp()
    {
        // Try NVIDIA hwmon (cached lookup, no repeated filesystem scan)
        var nvidiaHwmon = SysfsHelper.FindHwmonByName("nvidia");
        if (nvidiaHwmon != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(nvidiaHwmon, "temp1_input"), -1);
            if (temp > 0) return temp / 1000;
        }

        // Try amdgpu hwmon
        var amdHwmon = SysfsHelper.FindHwmonByName("amdgpu");
        if (amdHwmon != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(amdHwmon, "temp1_input"), -1);
            if (temp > 0) return temp / 1000;
        }

        // Fallback: try nvidia-smi (proprietary driver doesn't always expose hwmon)
        try
        {
            var output = SysfsHelper.RunCommand("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
            if (!string.IsNullOrWhiteSpace(output) && int.TryParse(output.Trim(), out int smiTemp) && smiTemp > 0)
                return smiTemp;
        }
        catch { /* nvidia-smi not available */ }

        return -1;
    }

    // ── Events ──

    public void SubscribeEvents()
    {
        _eventListening = true;
        _eventThread = new Thread(EventLoop)
        {
            Name = "AsusWmi-EventLoop",
            IsBackground = true
        };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        // Find all ASUS input devices.
        // On newer kernels with the 'asus' HID driver, hotkey events come from the
        // USB N-KEY Device ("Asus Keyboard" = event8), NOT from "Asus WMI hotkeys" (event9).
        // We listen on ALL discovered ASUS input devices simultaneously using poll().
        var devices = FindAsusInputDevices();
        if (devices.Count == 0)
        {
            Helpers.Logger.WriteLine("WARNING: Could not find any ASUS input device for hotkey events");
            return;
        }

        foreach (var dev in devices)
            Helpers.Logger.WriteLine($"Listening for ASUS events on {dev}");

        var streams = new List<FileStream>();
        try
        {
            foreach (var dev in devices)
            {
                try
                {
                    streams.Add(new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
                catch (Exception ex)
                {
                    Helpers.Logger.WriteLine($"Could not open {dev}: {ex.Message}");
                }
            }

            if (streams.Count == 0)
            {
                Helpers.Logger.WriteLine("WARNING: Could not open any ASUS input devices");
                return;
            }

            // If only one device, use simple blocking read
            if (streams.Count == 1)
            {
                ReadEventsFromStream(streams[0]);
            }
            else
            {
                // Multiple devices: read each in its own thread
                var threads = new List<Thread>();
                foreach (var stream in streams)
                {
                    var s = stream; // capture for closure
                    var t = new Thread(() => ReadEventsFromStream(s))
                    {
                        Name = $"AsusWmi-Reader-{Path.GetFileName(s.Name)}",
                        IsBackground = true
                    };
                    t.Start();
                    threads.Add(t);
                }
                // Wait for all reader threads (they'll exit when _eventListening = false)
                foreach (var t in threads)
                    t.Join();
            }
        }
        catch (Exception ex)
        {
            if (_eventListening)
                Helpers.Logger.WriteLine("Event loop error", ex);
        }
        finally
        {
            foreach (var fs in streams)
            {
                try { fs.Dispose(); } catch { }
            }
        }
    }

    private void ReadEventsFromStream(FileStream fs)
    {
        var buffer = new byte[24]; // sizeof(struct input_event) on 64-bit
        try
        {
            while (_eventListening)
            {
                int bytesRead = fs.Read(buffer, 0, 24);
                if (bytesRead == 24)
                {
                    // struct input_event: {timeval(16 bytes), __u16 type, __u16 code, __s32 value}
                    ushort type = BitConverter.ToUInt16(buffer, 16);
                    ushort code = BitConverter.ToUInt16(buffer, 18);
                    int value = BitConverter.ToInt32(buffer, 20);

                    // EV_KEY = 1, key press = value 1
                    if (type == 1 && value == 1)
                    {
                        string mappedKey = MapLinuxKeyToBindingName(code);
                        if (mappedKey != "")
                        {
                            Helpers.Logger.WriteLine($"ASUS event: key={code} (0x{code:X}) → {mappedKey}");
                            KeyBindingEvent?.Invoke(mappedKey);
                        }
                        else
                        {
                            // Also fire legacy numeric event for non-configurable keys
                            int legacyEvent = MapLinuxKeyToLegacyEvent(code);
                            if (legacyEvent > 0)
                            {
                                Helpers.Logger.WriteLine($"ASUS event: key={code} (0x{code:X}) → legacy={legacyEvent}");
                                WmiEvent?.Invoke(legacyEvent);
                            }
                            else
                            {
                                Helpers.Logger.WriteLine($"ASUS event: key={code} (0x{code:X}) → unmapped");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (_eventListening)
                Helpers.Logger.WriteLine($"Reader error on {fs.Name}: {ex.Message}");
        }
    }

    /// <summary>Find all ASUS input devices in /dev/input/.</summary>
    private static List<string> FindAsusInputDevices()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists("/proc/bus/input/devices")) return result;

            var content = File.ReadAllText("/proc/bus/input/devices");
            var sections = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            // Priority: USB keyboard first ("Asus Keyboard"), then WMI ("Asus WMI hotkeys")
            foreach (var section in sections)
            {
                if (!section.Contains("asus", StringComparison.OrdinalIgnoreCase)) continue;

                bool isKeyboard = section.Contains("keyboard", StringComparison.OrdinalIgnoreCase);
                bool isWmi = section.Contains("wmi", StringComparison.OrdinalIgnoreCase);

                if (isKeyboard || isWmi)
                {
                    string? eventDev = ExtractEventDevice(section);
                    if (eventDev != null)
                    {
                        // USB keyboard first in the list (higher priority)
                        if (isKeyboard && !isWmi)
                            result.Insert(0, eventDev);
                        else
                            result.Add(eventDev);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("FindAsusInputDevices failed", ex);
        }
        return result;
    }

    private static string? ExtractEventDevice(string section)
    {
        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith("H: Handlers="))
            {
                var parts = line.Split(' ');
                foreach (var part in parts)
                {
                    if (part.StartsWith("event"))
                        return $"/dev/input/{part.Trim()}";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Fired for configurable keys (m4, fnf4, fnf5).
    /// The string is the binding name that maps to AppConfig key.
    /// </summary>
    public event Action<string>? KeyBindingEvent;

    /// <summary>
    /// Map Linux KEY_* codes to configurable key binding names.
    /// These are keys the user can assign actions to.
    /// </summary>
    private static string MapLinuxKeyToBindingName(ushort linuxKeyCode)
    {
        return linuxKeyCode switch
        {
            // KEY_PROG1 (148) = ROG/M5 key → configurable as "m4" (Windows G-Helper naming)
            148 or 190 => "m4",
            // KEY_PROG3 (202) = Fn+F4 Aura key → configurable as "fnf4"
            202 => "fnf4",
            // KEY_PROG4 (203) = Fn+F5 / M4 performance key → configurable as "fnf5"
            203 => "fnf5",
            _ => ""
        };
    }

    /// <summary>
    /// Map Linux KEY_* codes to legacy G-Helper event codes for non-configurable keys
    /// (keyboard brightness, touchpad, etc.).
    /// </summary>
    private static int MapLinuxKeyToLegacyEvent(ushort linuxKeyCode)
    {
        return linuxKeyCode switch
        {
            // KEY_KBDILLUMUP (229) → Fn+F3 (196)
            229 => 196,
            // KEY_KBDILLUMDOWN (230) → Fn+F2 (197)
            230 => 197,
            // KEY_TOUCHPAD_TOGGLE (0x212 = 530) → Fn+F10 (107)
            530 => 107,
            // KEY_SLEEP (142) → Fn+F11 (108)
            142 => 108,
            // KEY_CAMERA (212) → Camera toggle (133)
            212 => 133,
            // KEY_RFKILL (247) → Fn+F12 airplane (136)
            247 => 136,
            // KEY_BRIGHTNESSDOWN (224) → Brightness down (16)
            224 => 16,
            // KEY_BRIGHTNESSUP (225) → Brightness up (32)
            225 => 32,
            // KEY_FN_ESC (407) → Fn+ESC FnLock toggle (78)
            407 => 78,
            // KEY_MICMUTE (248/505) → Mic mute (124 = M3 default)
            248 or 505 => 124,
            _ => -1
        };
    }

    // ── Feature detection ──

    public bool IsFeatureSupported(string feature)
    {
        // Check various possible locations for the attribute
        return SysfsHelper.Exists(Path.Combine(SysfsHelper.AsusWmiPlatform, feature))
            || SysfsHelper.Exists(Path.Combine(SysfsHelper.AsusBusPlatform, feature));
    }

    // ── Helpers ──

    private static int SetAndReturn(Action action)
    {
        try
        {
            action();
            return 1;
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _eventListening = false;
        _eventThread?.Join(2000);
    }
}

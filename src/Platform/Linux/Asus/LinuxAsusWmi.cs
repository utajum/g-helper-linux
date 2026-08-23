namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux implementation of IHardwareControl using the asus-wmi kernel module (sysfs).
/// Maps G-Helper's ATKACPI device IDs to Linux sysfs attributes.
/// 
/// Sysfs paths - resolved at runtime via SysfsHelper.ResolveAttrPath():
///
///   Legacy (kernel 6.2+ with CONFIG_ASUS_WMI_DEPRECATED_ATTRS=y):
///     /sys/devices/platform/asus-nb-wmi/throttle_thermal_policy
///     /sys/devices/platform/asus-nb-wmi/panel_od
///     /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable
///     /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode
///     /sys/bus/platform/devices/asus-nb-wmi/mini_led_mode
///     /sys/devices/platform/asus-nb-wmi/ppt_*
///     /sys/devices/platform/asus-nb-wmi/nv_*
///
///   Firmware-attributes (kernel 6.8+ with asus_armoury module):
///     /sys/class/firmware-attributes/asus-armoury/attributes/{name}/current_value
///
///   Always at fixed paths:
///     /sys/class/hwmon/hwmon*/fan{1,2,3}_input
///     /sys/class/hwmon/hwmon*/pwm{1,2,3}_auto_point{1-8}_{temp,pwm}
///     /sys/class/power_supply/BAT0/charge_control_end_threshold
///     /sys/class/leds/asus::kbd_backlight/brightness
///     /sys/class/leds/asus::kbd_backlight/multi_intensity
/// </summary>
public class LinuxAsusWmi : IHardwareControl
{
    private string? _asusFanRpmHwmonDir;   // Hwmon with fan*_input files (RPM reading)
    private string? _asusFanCurveHwmonDir; // Hwmon with pwm*_auto_point* files (fan curve control)
    private string? _asusBaseHwmonDir;     // Base ASUS hwmon (temps, etc.)
    private string? _cpuTempHwmonDir;      // CPU temperature hwmon (coretemp/k10temp)
    private string? _batteryDir;
    private Thread? _eventThread;
    private volatile bool _eventListening;
    private readonly List<FileStream> _eventStreams = new();  // Track open evdev streams for Dispose()

    private readonly Dictionary<string, int> _lastWrittenInt = new();
    private int _lastThrottlePolicy = int.MinValue;

    // FW-attr external change watcher
    private Thread? _fwAttrWatchThread;
    private volatile bool _fwAttrWatching;
    private readonly Dictionary<string, string> _fwAttrCache = new();

    public int FanCount { get; private set; } = 2;

    public event Action<int>? WmiEvent;

    /// <summary>ASUS firmware never changes platform_profile behind the app's
    /// back (Fn+F5 arrives as a WMI hotkey instead), so this never fires.</summary>
    public event Action<string>? PlatformProfileChanged { add { } remove { } }

    public LinuxAsusWmi()
    {
        // Discover hwmon devices - names vary by kernel version:
        // Kernel <6.x:  "asus_nb_wmi" (single hwmon for fans + temps + curves)
        // Kernel 6.x+:  "asus" (base, has fan*_input for RPM)
        // "asus_custom_fan_curve" (has pwm*_auto_point* for curve control, NO fan RPM)
        // "coretemp"/"k10temp" (CPU temp)
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
        {
            Helpers.Logger.WriteLine($"ASUS fan curve hwmon: {_asusFanCurveHwmonDir}");
            // Detect fan count: pwm1/pwm2 always present, pwm3 only on 3-fan models.
            // Some kernels create phantom pwm3 files that exist but return ENODEV on write,
            // so we verify by reading pwm3_enable (read won't error on phantom nodes).
            FanCount = File.Exists(Path.Combine(_asusFanCurveHwmonDir, "pwm3_enable"))
                    && SysfsHelper.ReadInt(Path.Combine(_asusFanCurveHwmonDir, "pwm3_enable"), -1) >= 0
                    ? 3 : 2;
            Helpers.Logger.WriteLine($"Fan count detected: {FanCount}");
        }
        else
            Helpers.Logger.WriteLine("WARNING: ASUS fan curve hwmon not found. Fan curve features unavailable.");

        if (_asusBaseHwmonDir != null)
            Helpers.Logger.WriteLine($"ASUS base hwmon: {_asusBaseHwmonDir}");

        if (_cpuTempHwmonDir != null)
            Helpers.Logger.WriteLine($"CPU temp hwmon: {_cpuTempHwmonDir}");

        if (_batteryDir != null)
            Helpers.Logger.WriteLine($"Battery found: {_batteryDir}");

        StartFwAttrWatcher();
    }

    // FW-attr external change polling

    private void StartFwAttrWatcher()
    {
        if (!Directory.Exists(SysfsHelper.FirmwareAttributes))
            return;

        // Seed cache with current values
        foreach (var attr in AsusAttributes.All)
        {
            string path = Path.Combine(SysfsHelper.FirmwareAttributes, attr.FwAttrName, "current_value");
            if (File.Exists(path))
            {
                string? val = SysfsHelper.ReadAttribute(path);
                if (val != null)
                    _fwAttrCache[attr.FwAttrName] = val;
            }
        }

        if (_fwAttrCache.Count == 0)
            return;

        _fwAttrWatching = true;
        _fwAttrWatchThread = new Thread(FwAttrWatchLoop)
        {
            Name = "FwAttrWatch",
            IsBackground = true
        };
        _fwAttrWatchThread.Start();
        Helpers.Logger.WriteLine($"FW-attr watcher started ({_fwAttrCache.Count} attributes)");
    }

    private void FwAttrWatchLoop()
    {
        while (_fwAttrWatching)
        {
            Thread.Sleep(5000);
            if (!_fwAttrWatching)
                break;

            foreach (var kvp in _fwAttrCache)
            {
                string path = Path.Combine(SysfsHelper.FirmwareAttributes, kvp.Key, "current_value");
                string? val = SysfsHelper.ReadAttribute(path);
                if (val == null || val == kvp.Value)
                    continue;

                // Skip values we wrote ourselves (_lastWrittenInt keys are legacy names)
                if (int.TryParse(val, out int parsed))
                {
                    lock (_lastWrittenInt)
                    {
                        if (_lastWrittenInt.TryGetValue(kvp.Key, out int wrote) && wrote == parsed)
                            continue;
                        var def = AsusAttributes.All.FirstOrDefault(a => a.FwAttrName == kvp.Key);
                        if (def != null && _lastWrittenInt.TryGetValue(def.LegacyName, out wrote) && wrote == parsed)
                            continue;
                    }
                }

                _fwAttrCache[kvp.Key] = val;
                Helpers.Logger.WriteLine($"FW-attr external change: {kvp.Key} -> {val}");
            }
        }
    }

    // Core ACPI-equivalent methods

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
            0x0005002A => GetScreenAutoBrightness(),          // ScreenOptimalBrightness
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
            // GPU mode changes MUST go through GPUModeControl - direct writes to
            // dgpu_disable cause kernel panics if the NVIDIA/AMD driver is active.
            // 0x00090020 (GPUEco) and 0x00090016 (GPUMux) intentionally removed.
            0x0005001E => SetAndReturn(() => SetMiniLedMode(value)),
            0x0005002E => SetAndReturn(() => SetMiniLedMode(value)),
            0x0005002A => SetAndReturn(() => SetScreenAutoBrightness(value != 0)),
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

    // Performance mode

    public int GetThrottleThermalPolicy()
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.ThrottleThermalPolicy, SysfsHelper.AsusWmiPlatform);
        if (path != null)
            return SysfsHelper.ReadInt(path, -1);

        // Fallback: derive from platform_profile if throttle_thermal_policy doesn't exist
        var profile = SysfsHelper.ReadAttribute(SysfsHelper.PlatformProfile);
        if (profile != null)
        {
            return profile switch
            {
                "balanced" => 0,
                "performance" => 1,
                "low-power" or "quiet" => 2,
                _ => -1
            };
        }

        return -1;
    }

    public void SetThrottleThermalPolicy(int mode)
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.ThrottleThermalPolicy, SysfsHelper.AsusWmiPlatform);
        if (path == null)
            return; // ModeControl still sets platform_profile directly when this attr is absent

        if (_lastThrottlePolicy == mode)
            return;

        SysfsHelper.WriteInt(path, mode);
        _lastThrottlePolicy = mode;
    }

    // Fan control

    public int GetFanRpm(int fanIndex)
    {
        // Use the hwmon that has fan*_input files (RPM sensors)
        var hwmon = _asusFanRpmHwmonDir ?? _asusBaseHwmonDir;
        if (hwmon != null)
        {
            int rpm = SysfsHelper.ReadInt(
                Path.Combine(hwmon, $"fan{fanIndex + 1}_input"), -1);
            if (rpm > 0)
                return rpm;
        }

        // For GPU fan (index 1), try nvidia-smi as fallback (only if NVIDIA driver is loaded)
        // nvidia-smi returns percentage, we return -2 to indicate "percentage mode"
        if (fanIndex == 1 && Directory.Exists("/sys/module/nvidia"))
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
        if (!Directory.Exists("/sys/module/nvidia"))
            return null;
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
        if (_asusFanCurveHwmonDir == null)
            return null;

        var curve = new byte[16];
        int pwmIndex = fanIndex + 1;

        for (int i = 0; i < 8; i++)
        {
            // Temperature: asus_custom_fan_curve sysfs uses raw degrees (NOT millidegrees).
            // The kernel stores temps directly from the ACPI buffer: data->temps[i] = buf[i]
            int temp = SysfsHelper.ReadInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_temp"), -1);
            if (temp < 0)
                return null;
            curve[i] = (byte)temp;

            // PWM 0-255 → percentage 0-100
            int pwm = SysfsHelper.ReadInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_pwm"), -1);
            if (pwm < 0)
                return null;
            curve[8 + i] = (byte)(pwm * 100 / 255);
        }

        // The kernel's default fan curves for CPU/GPU often have all-zero temperatures
        // but valid PWM duty cycles. This happens because the asus-wmi driver only
        // populates temps when the user explicitly writes custom curves; the EC's
        // built-in default temps are not exposed through sysfs.
        // Synthesize a reasonable temperature ramp so the UI has usable data.
        bool allTempsZero = true;
        bool anyPwmNonZero = false;
        for (int i = 0; i < 8; i++)
        {
            if (curve[i] > 0)
                allTempsZero = false;
            if (curve[8 + i] > 0)
                anyPwmNonZero = true;
        }

        if (allTempsZero && anyPwmNonZero)
        {
            byte[] synthTemps = { 30, 40, 50, 60, 70, 80, 90, 100 };
            for (int i = 0; i < 8; i++)
                curve[i] = synthTemps[i];

            Helpers.Logger.WriteLine($"Fan {fanIndex}: synthesized temp ramp for zero-temp kernel defaults");
        }

        return curve;
    }

    public void SetFanCurve(int fanIndex, byte[] curve)
    {
        if (_asusFanCurveHwmonDir == null || curve.Length != 16)
            return;

        int pwmIndex = fanIndex + 1;

        // Write all curve data points FIRST.
        // Each sysfs write to auto_point files updates the in-kernel fan_curve_data struct
        // and sets data->enabled = false (preventing premature writes to EC).
        for (int i = 0; i < 8; i++)
        {
            // Temperature: asus_custom_fan_curve sysfs uses raw degrees (NOT millidegrees)
            SysfsHelper.WriteInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_temp"),
                curve[i]);

            // Percentage 0-100 → PWM 0-255
            SysfsHelper.WriteInt(
                Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_auto_point{i + 1}_pwm"),
                curve[8 + i] * 255 / 100);
        }

        // Enable custom curve: pwm_enable=1 sets data->enabled=true and calls
        // fan_curve_write() which pushes the curve to the EC via WMI DEVS method.
        SysfsHelper.WriteInt(Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_enable"), 1);
    }

    public void DisableFanCurve(int fanIndex)
    {
        if (_asusFanCurveHwmonDir == null)
            return;
        int pwmIndex = fanIndex + 1;

        // pwm_enable=2 disables the custom curve for this fan, returning to
        // the firmware's thermal policy defaults for the current profile.
        SysfsHelper.WriteInt(Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_enable"), 2);
        Helpers.Logger.WriteLine($"Fan {fanIndex}: disabled custom curve (pwm_enable=2)");
    }

    public byte[]? ResetFanCurveToDefaults(int fanIndex)
    {
        if (_asusFanCurveHwmonDir == null)
            return null;
        int pwmIndex = fanIndex + 1;

        // pwm_enable=3 resets the fan curve to BIOS factory defaults for the
        // currently active platform profile, then disables the custom curve.
        SysfsHelper.WriteInt(Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_enable"), 3);
        Helpers.Logger.WriteLine($"Fan {fanIndex}: reset to factory defaults (pwm_enable=3)");

        // Read back the firmware defaults so the UI can display them
        return GetFanCurve(fanIndex);
    }

    public bool IsFanCurveEnabled(int fanIndex)
    {
        if (_asusFanCurveHwmonDir == null)
            return false;
        int pwmIndex = fanIndex + 1;
        return SysfsHelper.ReadInt(
            Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_enable"), 0) == 1;
    }

    public void EnsureManualFanMode()
    {
        if (_asusFanCurveHwmonDir == null)
            return;

        // Write pwm_enable=1 for each fan to set FANM=4 in the EC.
        // If the fan is already in manual mode (pwm_enable==1) the kernel
        // driver short-circuits and the write is effectively a no-op.
        // If the fan is in firmware mode (pwm_enable==2 or 3), this
        // activates the kernel's last-known curve data (BIOS defaults if
        // no custom curve was ever written) - identical to what SetFanCurve
        // does as its final step.
        for (int fan = 0; fan < FanCount; fan++)
        {
            int pwmIndex = fan + 1;
            string path = Path.Combine(_asusFanCurveHwmonDir, $"pwm{pwmIndex}_enable");
            int current = SysfsHelper.ReadInt(path, -1);
            if (current != 1)
            {
                SysfsHelper.WriteInt(path, 1);
                Helpers.Logger.WriteLine($"EnsureManualFanMode: fan {fan} pwm_enable {current} → 1");
            }
        }
    }

    // Battery

    public int GetBatteryChargeLimit()
    {
        if (_batteryDir == null)
            return -1;
        return SysfsHelper.ReadInt(
            Path.Combine(_batteryDir, "charge_control_end_threshold"), -1);
    }

    public bool SetBatteryChargeLimit(int percent)
    {
        if (_batteryDir == null)
            return false;
        percent = Math.Clamp(percent, 40, 100);

        // Some models only accept 60/80/100 as charge limits
        if (Helpers.AppConfig.IsChargeLimit6080())
        {
            if (percent > 85)
                percent = 100;
            else if (percent >= 80)
                percent = 80;
            else if (percent < 60)
                percent = 60;
        }

        return SysfsHelper.WriteInt(
            Path.Combine(_batteryDir, "charge_control_end_threshold"), percent);
    }

    // GPU

    // Raw WMI GPU Eco detection (cached)
    private bool? _rawWmiGpuEcoAvailable;

    /// <summary>
    /// True if GPU Eco switching is available - either via sysfs (dgpu_disable),
    /// via raw WMI debugfs (when raw_wmi is enabled and firmware supports it),
    /// or via the PCI backend (modprobe block + udev hot-remove; works on any
    /// laptop with a discrete NVIDIA GPU, no ASUS firmware required).
    /// </summary>
    public bool IsGpuEcoAvailable()
    {
        // Single-GPU machines (iGPU-only handhelds, thin-and-lights) have
        // nothing to switch: hide Eco/Standard/Ultimate everywhere. The
        // probe is Eco-resilient, so a dGPU hidden by our own Eco still
        // counts (see GPUModeControl.HasSecondGpu).
        if (!Gpu.GPUModeControl.HasSecondGpu())
            return false;

        if (IsFeatureSupported(AsusAttributes.DgpuDisable))
            return true;

        // PCI backend: keep the panel visible across the Eco hot-remove so
        // the user can always reach the Standard button to bring the dGPU
        // back. Shared probe with CanToggleGpuBackend so the two checks
        // cannot drift apart (the previous narrow `HasDiscreteNvidiaGpu`
        // check hid the panel after a successful PCI-Eco because the dGPU
        // is no longer on the PCI bus).
        if (IsPciBackendUsable())
            return true;

        if (!Helpers.AppConfig.Is("raw_wmi") || !AsusWmiDebugfs.IsAvailable())
            return false;

        if (!_rawWmiGpuEcoAvailable.HasValue)
        {
            _rawWmiGpuEcoAvailable = AsusWmiDebugfs.IsDevicePresent(AsusWmiDebugfs.DEVID_DGPU)
                                  || AsusWmiDebugfs.IsDevicePresent(AsusWmiDebugfs.DEVID_DGPU_VIVO);
            Helpers.Logger.WriteLine($"Raw WMI GPUEco probe: {(_rawWmiGpuEcoAvailable.Value ? "available" : "not found")}");
        }

        return _rawWmiGpuEcoAvailable.Value;
    }

    /// <summary>
    /// Optional root prefix for every disk-state probe in this class. Set
    /// to a sandbox path via <c>GHELPER_TEST_ROOT</c> so the test harness
    /// can exercise PCI / module / eco-artifact detection without touching
    /// the host. Empty in production so the real sysfs / /etc paths are
    /// used. Matches the same env var consumed by <c>GPUModeControl</c>
    /// so a single export covers both layers.
    /// </summary>
    internal static string TestPathPrefix
        => Environment.GetEnvironmentVariable("GHELPER_TEST_ROOT") ?? "";

    /// <summary>
    /// Cheap PCI bus scan: returns true if at least one device has vendor
    /// 0x10de (NVIDIA) and a VGA/3D-controller class. Results are cached
    /// because PCI topology cannot change without a reboot or hot-plug
    /// (live PCI re-scan during Eco→Standard recovery calls
    /// <see cref="InvalidateGpuPresenceCache"/> to drop the cache).
    /// </summary>
    private static bool? _hasDiscreteNvidiaCache;
    internal static bool HasDiscreteNvidiaGpu()
    {
        if (_hasDiscreteNvidiaCache.HasValue)
            return _hasDiscreteNvidiaCache.Value;
        try
        {
            string pciDir = TestPathPrefix + "/sys/bus/pci/devices";
            if (!Directory.Exists(pciDir))
            {
                _hasDiscreteNvidiaCache = false;
                return false;
            }
            foreach (var dev in Directory.EnumerateDirectories(pciDir))
            {
                try
                {
                    string vendorPath = Path.Combine(dev, "vendor");
                    string classPath = Path.Combine(dev, "class");
                    if (!File.Exists(vendorPath) || !File.Exists(classPath))
                        continue;
                    string vendor = File.ReadAllText(vendorPath).Trim();
                    if (vendor != "0x10de")
                        continue;
                    string klass = File.ReadAllText(classPath).Trim();
                    // VGA controller (0x0300xx) or 3D controller (0x0302xx)
                    if (klass.StartsWith("0x0300") || klass.StartsWith("0x0302"))
                    {
                        _hasDiscreteNvidiaCache = true;
                        return true;
                    }
                }
                catch
                {
                    // sysfs read race: ignore this device and continue
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"HasDiscreteNvidiaGpu: PCI scan failed: {ex.Message}");
        }
        _hasDiscreteNvidiaCache = false;
        return false;
    }

    /// <summary>
    /// Broader check used to decide whether to expose the GPU-backend
    /// checkbox in the Extra window. Returns true whenever the user could
    /// conceivably switch GPU modes - even if the dGPU is currently
    /// invisible to the PCI bus (udev hot-remove rule already applied,
    /// BIOS power-off state, etc.).
    ///
    /// Conditions (any one is sufficient):
    ///   1. ASUS dgpu_disable WMI is exposed (ASUS hardware with dGPU)
    ///   2. A discrete NVIDIA GPU is currently on the PCI bus
    ///   3. The PCI backend is usable (opted-in AND has dGPU evidence -
    ///      shared with IsGpuEcoAvailable, see IsPciBackendUsable)
    ///   4. The nvidia or nouveau kernel module is installed on disk
    ///      (a dGPU once was - or will be - present even if hot-removed now)
    /// </summary>
    public bool CanToggleGpuBackend()
    {
        // Backend choice is only meaningful with a second GPU (same gate as
        // the mode-switching UI).
        if (!Gpu.GPUModeControl.HasSecondGpu())
            return false;
        if (IsFeatureSupported(AsusAttributes.DgpuDisable))
            return true;
        if (HasDiscreteNvidiaGpu())
            return true;
        if (IsPciBackendUsable())
            return true;
        if (HasNvidiaModuleAvailable())
            return true;
        return false;
    }

    /// <summary>
    /// Shared probe: PCI backend is opted-in by config AND there is
    /// hardware evidence this system has (or had) an NVIDIA dGPU. Used by
    /// both the main GPU panel (IsGpuEcoAvailable) and the Extra Settings
    /// backend selector (CanToggleGpuBackend) so the two cannot drift.
    ///
    /// Evidence is any of:
    ///   - dGPU currently on the PCI bus
    ///   - Our own eco block artifacts on disk (we installed them, so we
    ///     MUST stay able to remove them via the UI even after the udev
    ///     hot-remove rule has made the dGPU invisible to /sys/bus/pci)
    ///   - nvidia or nouveau kernel module on disk (dGPU expected after
    ///     we unblock it; typical post-Eco state with hot-removed device)
    ///
    /// Returns false when the user has not enabled PCI mode, so machines
    /// without a dGPU never see the panel just because the config file
    /// got imported from a different system.
    /// </summary>
    internal static bool IsPciBackendUsable()
    {
        if (!Helpers.AppConfig.IsPciGpuBackend())
            return false;
        if (HasDiscreteNvidiaGpu())
            return true;
        try
        {
            string prefix = TestPathPrefix;
            if (File.Exists(prefix + "/etc/modprobe.d/ghelper-gpu-block.conf") ||
                File.Exists(prefix + "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"))
                return true;
        }
        catch
        {
            // Permission denied / transient I/O - fall through
        }
        // Loaded nvidia/nouveau without a device = dGPU hot-removed (Eco),
        // and a proprietary nvidia.ko is only installed for real hardware.
        // Both stay sufficient. nouveau on disk alone proves nothing (it
        // ships with virtually every distro kernel), so on iGPU-only
        // machines (where gpu_backend auto-defaults to "pci") it needs
        // independent second-GPU evidence.
        if (HasNvidiaModuleLoaded() || HasNvidiaProprietaryOnDisk())
            return true;
        return HasNouveauOnDisk() && Gpu.GPUModeControl.HasSecondGpu();
    }

    /// <summary>
    /// Reset the cached "is the dGPU present" probes. Call after an
    /// in-process event that may alter PCI bus topology - specifically
    /// the live PCI Eco-to-Standard transition, which removes the
    /// modprobe block and triggers /sys/bus/pci/rescan to bring the dGPU
    /// back online without a reboot. The next IsGpuEcoAvailable /
    /// CanToggleGpuBackend call will then re-scan /sys/bus/pci/devices
    /// instead of returning the stale pre-rescan (empty) cached result.
    /// </summary>
    internal static void InvalidateGpuPresenceCache()
    {
        _hasDiscreteNvidiaCache = null;
        _nvidiaLoadedCache = null;
        _nvidiaOnDiskCache = null;
        _nouveauOnDiskCache = null;
    }

    private static bool? _nvidiaLoadedCache;
    private static bool? _nvidiaOnDiskCache;
    private static bool? _nouveauOnDiskCache;

    /// <summary>Any nvidia/nouveau evidence at all (loaded or on disk).</summary>
    internal static bool HasNvidiaModuleAvailable() =>
        HasNvidiaModuleLoaded() || HasNvidiaProprietaryOnDisk() || HasNouveauOnDisk();

    /// <summary>nvidia or nouveau bound right now. Without a matching PCI
    /// device this means the dGPU was hot-removed (Eco): strong evidence.</summary>
    internal static bool HasNvidiaModuleLoaded()
    {
        if (_nvidiaLoadedCache.HasValue)
            return _nvidiaLoadedCache.Value;
        string prefix = TestPathPrefix;
        _nvidiaLoadedCache = Directory.Exists(prefix + "/sys/module/nvidia")
            || Directory.Exists(prefix + "/sys/module/nouveau");
        return _nvidiaLoadedCache.Value;
    }

    /// <summary>Proprietary nvidia*.ko installed for the running kernel.
    /// Only present where a real NVIDIA card is (or was) expected.</summary>
    internal static bool HasNvidiaProprietaryOnDisk() =>
        _nvidiaOnDiskCache ??= ModuleOnDisk("nvidia*.ko*");

    /// <summary>nouveau*.ko on disk. Weak evidence: it ships with virtually
    /// every distro kernel, including iGPU-only machines.</summary>
    internal static bool HasNouveauOnDisk() =>
        _nouveauOnDiskCache ??= ModuleOnDisk("nouveau*.ko*");

    private static bool ModuleOnDisk(string pattern)
    {
        try
        {
            string prefix = TestPathPrefix;
            string release;
            try
            { release = File.ReadAllText(prefix + "/proc/sys/kernel/osrelease").Trim(); }
            catch { release = ""; }
            string[] roots = string.IsNullOrEmpty(release)
                ? new[] { prefix + "/lib/modules" }
                : new[] { $"{prefix}/lib/modules/{release}/updates", $"{prefix}/lib/modules/{release}/kernel/drivers/gpu/drm/nouveau", prefix + "/lib/modules" };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                        return true;
                }
                catch
                {
                    // permission denied on a sub-tree - ignore and continue
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"ModuleOnDisk({pattern}): scan failed: {ex.Message}");
        }
        return false;
    }

    public bool GetGpuEco()
    {
        // Path 1: sysfs (standard - works on models with dgpu_disable)
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.DgpuDisable, SysfsHelper.AsusBusPlatform);
        if (path != null)
            return SysfsHelper.ReadInt(path, 0) == 1;

        // Path 2: raw WMI debugfs (opt-in - for models without dgpu_disable sysfs)
        // Use cached probe to pick the right device ID (0 pkexec), then 1 DSTS call
        if (!Helpers.AppConfig.Is("raw_wmi"))
            return false;
        uint devId = AsusWmiDebugfs.IsDevicePresent(AsusWmiDebugfs.DEVID_DGPU)
            ? AsusWmiDebugfs.DEVID_DGPU : AsusWmiDebugfs.DEVID_DGPU_VIVO;
        uint? r = AsusWmiDebugfs.Dsts(devId);
        if (r == null)
            return false;
        return (r.Value & 0x01) == 1;
    }

    /// <summary>
    /// Check if the NVIDIA DRM driver is currently active (holding GPU resources).
    /// Returns true if nvidia_drm is loaded AND refcnt > 0.
    /// Used by SetGpuEco guard - prevents kernel panics from ACPI hot-removal.
    /// </summary>
    private static bool IsNvidiaDrmActive()
    {
        // Module not loaded → safe to disable dGPU
        if (!Directory.Exists("/sys/module/nvidia_drm"))
            return false;

        int refcnt = SysfsHelper.ReadInt("/sys/module/nvidia_drm/refcnt", -1);

        // Can't read refcnt → assume active for safety
        if (refcnt < 0)
        {
            Helpers.Logger.WriteLine("SetGpuEco guard: nvidia_drm loaded but refcnt unreadable - assuming active");
            return true;
        }

        return refcnt > 0;
    }

    /// <summary>
    /// Check if ANY dGPU driver is currently active (NVIDIA or AMD).
    /// Combined guard for SetGpuEco - prevents ACPI hot-removal crash for both vendors.
    /// </summary>
    private bool IsDgpuDriverActive()
    {
        if (IsNvidiaDrmActive())
            return true;

        if (IsAmdDgpuDriverActive())
            return true;

        return false;
    }

    /// <summary>
    /// Check if the AMD dGPU driver (amdgpu) is currently active.
    /// AMD has no module refcnt like NVIDIA - instead check PCI runtime_status.
    /// Returns true if amdgpu module is loaded AND bound to the dGPU AND runtime_status != "suspended".
    /// </summary>
    private static bool IsAmdDgpuDriverActive()
    {
        // amdgpu module not loaded → safe
        if (!Directory.Exists("/sys/module/amdgpu"))
            return false;

        // Module is loaded - find the AMD dGPU PCI device
        string? pciAddr = FindAmdDgpuPciAddress();
        if (pciAddr == null)
            return false; // No AMD dGPU found

        // Check if amdgpu driver is bound to this device
        string driverLink = $"/sys/bus/pci/devices/{pciAddr}/driver";
        try
        {
            if (Directory.Exists(driverLink))
            {
                string target = Path.GetFileName(
                    Directory.ResolveLinkTarget(driverLink, false)?.FullName ?? "");
                if (target != "amdgpu")
                    return false; // Different driver bound (vfio-pci, etc.)
            }
            else
            {
                return false; // No driver bound
            }
        }
        catch
        {
            // Can't read driver symlink - fall through to runtime_status check
        }

        // Check runtime power state
        string statusPath = $"/sys/bus/pci/devices/{pciAddr}/power/runtime_status";
        string? status = SysfsHelper.ReadAttribute(statusPath);

        if (status == "suspended")
        {
            Helpers.Logger.WriteLine($"SetGpuEco guard: AMD dGPU {pciAddr} runtime_status=suspended - safe");
            return false;
        }

        // "active" or any other value (including null/unreadable) → assume active for safety
        Helpers.Logger.WriteLine($"SetGpuEco guard: AMD dGPU {pciAddr} runtime_status={status ?? "unreadable"} - active");
        return true;
    }

    /// <summary>
    /// Scan PCI bus for AMD discrete GPU.
    /// Criteria: vendor=0x1002, class=0x0300xx or 0x0302xx, boot_vga=0 (not iGPU).
    /// </summary>
    private static string? FindAmdDgpuPciAddress()
    {
        try
        {
            string pciDir = "/sys/bus/pci/devices";
            if (!Directory.Exists(pciDir))
                return null;

            foreach (var deviceDir in Directory.GetDirectories(pciDir))
            {
                string? vendor = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "vendor"));
                if (vendor != "0x1002")
                    continue;

                string? cls = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "class"));
                if (cls == null)
                    continue;
                if (!cls.StartsWith("0x0300") && !cls.StartsWith("0x0302"))
                    continue;

                string? bootVga = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "boot_vga"));
                if (bootVga == "1")
                    continue; // iGPU, not dGPU

                return Path.GetFileName(deviceDir);
            }
        }
        catch { }

        return null;
    }

    public void SetGpuEco(bool enabled)
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.DgpuDisable, SysfsHelper.AsusBusPlatform);

        // No sysfs → try raw WMI debugfs (opt-in)
        if (path == null)
        {
            if (!Helpers.AppConfig.Is("raw_wmi"))
                return;

            // Same safety guards as sysfs path
            if (enabled && IsDgpuDriverActive())
                throw new InvalidOperationException(
                    "SAFETY: Cannot disable dGPU - driver is active (raw WMI path).");
            if (enabled && GetGpuMuxMode() == 0)
                throw new InvalidOperationException(
                    "SAFETY: Cannot disable dGPU - MUX is in dGPU mode (raw WMI path).");

            uint devId = AsusWmiDebugfs.IsDevicePresent(AsusWmiDebugfs.DEVID_DGPU)
                ? AsusWmiDebugfs.DEVID_DGPU : AsusWmiDebugfs.DEVID_DGPU_VIVO;
            uint? result = AsusWmiDebugfs.Devs(devId, enabled ? 1u : 0u);
            Helpers.Logger.WriteLine($"SetGpuEco({enabled}) via raw WMI debugfs " +
                $"(device 0x{devId:X8}, result={(result.HasValue ? $"0x{result.Value:X}" : "null")})");
            return;
        }

        // Skip write if already in desired state - writing dgpu_disable can block
        // in the kernel for 30-60 seconds while the GPU powers down via ACPI/WMI
        int current = SysfsHelper.ReadInt(path, -1);
        int desired = enabled ? 1 : 0;
        if (current == desired)
        {
            Helpers.Logger.WriteLine($"SetGpuEco: dgpu_disable already {desired}, skipping write");
            return;
        }

        if (enabled)
        {
            // SAFETY GUARD 1: Never disable dGPU when dGPU driver is active
            // Writing dgpu_disable=1 triggers ACPI hot-removal (acpiphp_disable_and_eject_slot).
            // If nvidia_drm or amdgpu is bound, hot-removal causes kernel panic / GPU fault.
            if (IsDgpuDriverActive())
                throw new InvalidOperationException(
                    "SAFETY: Cannot write dgpu_disable=1 - dGPU driver is active. " +
                    "This would cause a kernel panic via ACPI hot-removal.");

            // SAFETY GUARD 2: Never disable dGPU when MUX=0 (Ultimate/dGPU-direct)
            // MUX=0 means the dGPU is the sole display output. Disabling it = no display = black screen.
            // This creates an impossible boot state that requires CMOS reset to recover.
            int mux = GetGpuMuxMode();
            if (mux == 0)
                throw new InvalidOperationException(
                    "SAFETY: Cannot write dgpu_disable=1 - gpu_mux_mode=0 (Ultimate). " +
                    "This creates an impossible state: dGPU is sole display output but powered off.");
        }

        // Thread.Sleep(500);
        SysfsHelper.WriteInt(path, desired);

        if (!enabled)
        {
            // PCI bus rescan after enabling dGPU
            // After dgpu_disable=0, the dGPU needs to reappear in the PCI device tree.
            // The kernel ACPI _ON method usually triggers re-enumeration, but an explicit
            // rescan ensures reliability (supergfxctl pattern: special_asus.rs:145-149).
            // Best-effort: /sys/bus/pci/rescan requires root, may fail for non-root users.
            Helpers.Logger.WriteLine("SetGpuEco: dGPU enabled, triggering PCI bus rescan");
            Thread.Sleep(50); // Brief settle time for hardware (supergfxctl uses 50ms)
            if (!SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1"))
                Helpers.Logger.WriteLine("SetGpuEco: PCI rescan failed (may need root) - dGPU should re-enumerate via ACPI");
        }
    }

    public int GetGpuMuxMode()
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.GpuMuxMode, SysfsHelper.AsusBusPlatform);
        if (path == null)
            return -1;
        return SysfsHelper.ReadInt(path, -1);
    }

    public void SetGpuMuxMode(int mode)
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.GpuMuxMode, SysfsHelper.AsusBusPlatform);
        if (path == null)
            return;

        int current = SysfsHelper.ReadInt(path, -1);
        if (current == mode)
        {
            Helpers.Logger.WriteLine($"SetGpuMuxMode: gpu_mux_mode already {mode}, skipping write");
            return;
        }

        // SAFETY GUARD 3: Never write gpu_mux_mode when dGPU is disabled
        // Firmware rejects MUX changes when dgpu_disable=1 (returns ENODEV).
        // The kernel write can hang for several seconds before returning the error.
        // Refusing immediately is safer and faster.
        if (GetGpuEco())
            throw new InvalidOperationException(
                "SAFETY: Cannot write gpu_mux_mode - dgpu_disable=1. " +
                "Firmware rejects MUX changes when dGPU is powered off.");

        if (!SysfsHelper.WriteInt(path, mode))
            throw new IOException(
                $"gpu_mux_mode write rejected by firmware (wrote {mode} to {path})");
    }

    // Display

    public bool GetPanelOverdrive()
    {
        // AsusAttributes.PanelOd handles the alias: panel_od (legacy) → panel_overdrive (fw-attr)
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.PanelOd, SysfsHelper.AsusWmiPlatform);
        if (path == null)
            return false;
        return SysfsHelper.ReadInt(path, 0) == 1;
    }

    public bool SetPanelOverdrive(bool enabled)
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.PanelOd, SysfsHelper.AsusWmiPlatform);
        if (path == null)
            return false;
        SysfsHelper.WriteInt(path, enabled ? 1 : 0);
        // Readback: the kernel returns -EIO if the EC rejected the write.
        int actual = SysfsHelper.ReadInt(path, -1);
        if (actual < 0 || (actual == 1) != enabled)
        {
            Helpers.Logger.WriteLine($"Panel overdrive: write {(enabled ? 1 : 0)} readback {actual} - mismatch");
            return false;
        }
        return true;
    }

    public int GetMiniLedMode()
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.MiniLedMode, SysfsHelper.AsusBusPlatform);
        if (path == null)
            return -1;
        return SysfsHelper.ReadInt(path, -1);
    }

    public void SetMiniLedMode(int mode)
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.MiniLedMode, SysfsHelper.AsusBusPlatform);
        if (path != null)
            SysfsHelper.WriteInt(path, mode);
    }

    public int GetMiniLedModeCount()
    {
        if (!IsFeatureSupported(AsusAttributes.MiniLedMode))
            return 0;

        // possible_values only exists under asus-armoury. Legacy sysfs and
        // kernels that publish no list fall back to the off/on pair.
        var values = SysfsHelper.ReadPossibleValues(AsusAttributes.MiniLedMode);
        return values is { Length: >= 2 } ? values.Length : 2;
    }

    // Optimal Display Brightness (screen_auto_brightness, ACPI WMI DEVID 0x0005002A).
    // Linux kernel asus-armoury exposes this as a boolean firmware-attribute only;
    // no legacy asus-nb-wmi sysfs equivalent. Same firmware endpoint as Windows.

    public int GetScreenAutoBrightness()
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.ScreenAutoBrightness);
        if (path == null)
            return -1;
        return SysfsHelper.ReadInt(path, -1);
    }

    public void SetScreenAutoBrightness(bool enabled)
    {
        var path = SysfsHelper.ResolveAttrPath(AsusAttributes.ScreenAutoBrightness);
        if (path != null)
            SysfsHelper.WriteInt(path, enabled ? 1 : 0);
    }

    // PPT / Power limits

    public void SetPptLimit(string attribute, int watts)
    {
        // Lock: the FW-attr watcher thread reads this dictionary.
        lock (_lastWrittenInt)
        {
            if (_lastWrittenInt.TryGetValue(attribute, out int prev) && prev == watts)
                return;
        }

        // AMD: asus-wmi PPT sysfs is a no-op on some boards. Route through
        // the SMU directly when available, fall back to sysfs otherwise.
        if (RyzenPower.TrySetPpt(attribute, watts))
        {
            lock (_lastWrittenInt)
                _lastWrittenInt[attribute] = watts;
            return;
        }

        // On dual-backend kernels (asus-nb-wmi + asus-armoury), we cannot predict which
        // backend is functional for any given attribute. Write to ALL available paths
        // legacy sysfs and firmware-attributes - so at least one succeeds.
        // See: https://github.com/utajum/g-helper-linux/issues/23
        bool written;
        var attrDef = AsusAttributes.FindByLegacyName(attribute);
        if (attrDef != null)
        {
            written = SysfsHelper.WriteToAllBackends(attrDef, watts.ToString(), SysfsHelper.AsusWmiPlatform);
        }
        else
        {
            // Fallback for unknown attributes: single resolved path
            var path = SysfsHelper.ResolveAttrPath(attribute, SysfsHelper.AsusWmiPlatform);
            written = path != null && SysfsHelper.WriteInt(path, watts);
        }

        // Cache only successful writes. A rejected write must stay retryable,
        // otherwise the dedupe check above blocks it until restart.
        if (!written)
        {
            Helpers.Logger.WriteLine($"SetPptLimit: {attribute}={watts} write failed, not caching");
            return;
        }

        lock (_lastWrittenInt)
            _lastWrittenInt[attribute] = watts;
    }

    public AttrRange? GetAttributeRange(AttrDef attr) => AsusAttributeRange.Read(attr);

    public int GetPptLimit(string attribute)
    {
        // Read from the first available backend (legacy preferred for reliability)
        var attrDef = AsusAttributes.FindByLegacyName(attribute);
        if (attrDef != null)
            return SysfsHelper.ReadFromAnyBackend(attrDef, -1, SysfsHelper.AsusWmiPlatform);

        var path = SysfsHelper.ResolveAttrPath(attribute, SysfsHelper.AsusWmiPlatform);
        if (path == null)
            return -1;
        return SysfsHelper.ReadInt(path, -1);
    }

    // Keyboard

    /// <summary>
    /// True when the kernel driver handles keyboard brightness changes in hardware
    /// (brightness_hw_changed sysfs exists). When true, physical Fn keys change sysfs
    /// directly and userspace should NOT write, just read.
    /// </summary>
    public bool HasKbdBrightnessHwChanged { get; } =
        File.Exists(Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "brightness_hw_changed"));

    /// <summary>Off + low/medium/high.</summary>
    public int KbdMaxBrightness => 3;

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

    /// <summary>
    /// Set TUF keyboard RGB mode via sysfs kbd_rgb_mode attribute.
    /// This is the primary RGB control for TUF Gaming keyboards.
    /// Format: space-separated byte array "cmd mode R G B speed"
    /// Learned from asusctl: rog-platform/src/keyboard_led.rs + asusd/src/aura_laptop/mod.rs
    /// </summary>
    public void SetKeyboardRgbMode(int mode, byte r, byte g, byte b, int speed)
    {
        var modePath = Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "kbd_rgb_mode");
        if (!SysfsHelper.Exists(modePath))
        {
            Helpers.Logger.WriteLine($"kbd_rgb_mode not available at {modePath}");
            return;
        }
        // Protocol: [1, mode, R, G, B, speed] - matches asusctl's TUF write
        string value = $"1 {mode} {r} {g} {b} {speed}";
        SysfsHelper.WriteAttribute(modePath, value);
    }

    /// <summary>
    /// Set TUF keyboard RGB power state via sysfs kbd_rgb_state attribute.
    /// Controls which lighting states are active (boot/awake/sleep).
    /// Format: space-separated byte array "cmd boot awake sleep keyboard"
    /// Learned from asusctl: rog-aura/src/keyboard/power.rs TUF format
    /// </summary>
    public void SetKeyboardRgbState(bool boot, bool awake, bool sleep)
    {
        var statePath = Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "kbd_rgb_state");
        if (!SysfsHelper.Exists(statePath))
        {
            Helpers.Logger.WriteLine($"kbd_rgb_state not available at {statePath}");
            return;
        }
        // Protocol: [1, boot, awake, sleep, 1] - matches asusctl's TUF power state
        string value = $"1 {(boot ? 1 : 0)} {(awake ? 1 : 0)} {(sleep ? 1 : 0)} 1";
        SysfsHelper.WriteAttribute(statePath, value);
    }

    /// <summary>
    /// Check if TUF-specific kbd_rgb_mode sysfs attribute is available.
    /// </summary>
    public bool HasKeyboardRgbMode()
    {
        return SysfsHelper.Exists(
            Path.Combine(SysfsHelper.Leds, "asus::kbd_backlight", "kbd_rgb_mode"));
    }

    // Temperature

    private int GetCpuTemp()
    {
        // Try dedicated CPU temp hwmon (coretemp/k10temp) - package temp
        if (_cpuTempHwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_cpuTempHwmonDir, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
        }

        // Try ASUS base hwmon
        if (_asusBaseHwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_asusBaseHwmonDir, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
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
                    if (temp > 0)
                        return temp / 1000;
                }
            }

            // Last resort: first thermal zone
            var fallback = Path.Combine(SysfsHelper.Thermal, "thermal_zone0", "temp");
            int fallbackTemp = SysfsHelper.ReadInt(fallback, -1);
            if (fallbackTemp > 0)
                return fallbackTemp / 1000;
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

        // Readings at or above 125 C are sensor glitches, drop them.
        const int MaxValidTemp = 125;

        // Try NVIDIA hwmon (cached lookup, no repeated filesystem scan)
        var nvidiaHwmon = nvSkip ? null : SysfsHelper.FindHwmonByName("nvidia");
        if (nvidiaHwmon != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(nvidiaHwmon, "temp1_input"), -1) / 1000;
            if (temp > 0 && temp < MaxValidTemp)
                return temp;
        }

        // Try amdgpu hwmon
        var amdHwmon = SysfsHelper.FindHwmonByName("amdgpu");
        if (amdHwmon != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(amdHwmon, "temp1_input"), -1) / 1000;
            if (temp > 0 && temp < MaxValidTemp)
                return temp;
        }

        // Fallback: NVML via gpu-helper (~5ms, no nvidia-smi fork)
        int nvmlTemp = Gpu.NVidia.LinuxNvidiaGpuControl.GetTempViaNvml();
        if (nvmlTemp > 0 && nvmlTemp < MaxValidTemp)
            return nvmlTemp;

        // Last resort: nvidia-smi fork (~200ms)
        if (!nvSkip && Directory.Exists("/sys/module/nvidia"))
        {
            try
            {
                var output = SysfsHelper.RunCommand("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader,nounits");
                if (!string.IsNullOrWhiteSpace(output) && int.TryParse(output.Trim(), out int smiTemp) && smiTemp > 0 && smiTemp < MaxValidTemp)
                    return smiTemp;
            }
            catch { }
        }

        return -1;
    }

    // Events

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

            // Store references so Dispose() can close them to unblock reads
            lock (_eventStreams)
            {
                _eventStreams.AddRange(streams);
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
                try
                { fs.Dispose(); }
                catch { }
            }
        }
    }

    private void ReadEventsFromStream(FileStream fs)
    {
        var buffer = new byte[24]; // sizeof(struct input_event) on 64-bit
        int pendingScanCode = -1;  // EV_MSC/MSC_SCAN value, reset after each EV_KEY
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

                    // EV_MSC (4) / MSC_SCAN (4) - capture scan code for next EV_KEY.
                    // MSC_SCAN values match Windows WMI event codes and are consistent
                    // across models even when KEY_* codes differ (e.g. TUF vs ROG).
                    if (type == 4 && code == 4)
                    {
                        pendingScanCode = value;
                        continue;
                    }

                    // EV_KEY = 1, key press = value 1
                    if (type == 1 && value == 1)
                    {
                        // Try MSC_SCAN first (universal across models), fall back to KEY_* code
                        string mappedKey = pendingScanCode >= 0
                            ? MapScanCodeToBindingName(pendingScanCode)
                            : "";
                        if (mappedKey == "")
                            mappedKey = MapLinuxKeyToBindingName(code);

                        if (mappedKey != "")
                        {
                            Helpers.Logger.WriteLine($"ASUS event: key={code} (0x{code:X}) scan=0x{pendingScanCode:X} → {mappedKey}");
                            KeyBindingEvent?.Invoke(mappedKey);
                        }
                        else
                        {
                            // Try legacy mapping: MSC_SCAN first, then KEY_* code
                            int legacyEvent = pendingScanCode >= 0
                                ? MapScanCodeToLegacyEvent(pendingScanCode)
                                : -1;
                            if (legacyEvent < 0)
                                legacyEvent = MapLinuxKeyToLegacyEvent(code);

                            if (legacyEvent > 0)
                            {
                                Helpers.Logger.WriteLine($"ASUS event: key={code} (0x{code:X}) scan=0x{pendingScanCode:X} → legacy={legacyEvent}");
                                WmiEvent?.Invoke(legacyEvent);
                            }
                        }

                        pendingScanCode = -1;
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
            if (!File.Exists("/proc/bus/input/devices"))
                return result;

            var content = File.ReadAllText("/proc/bus/input/devices");
            var sections = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            // Priority: USB keyboard first ("Asus Keyboard"), then WMI ("Asus WMI hotkeys")
            foreach (var section in sections)
            {
                // Match sections containing "asus" (name or sysfs path) or USB vendor 0b05 (ASUSTek).
                // The vendor match catches ITE-named ASUS HID devices like "ITE Tech. Inc. ITE Device(8910)".
                bool isAsus = section.Contains("asus", StringComparison.OrdinalIgnoreCase)
                    || section.Contains("Vendor=0b05", StringComparison.OrdinalIgnoreCase);
                if (!isAsus)
                    continue;

                bool isKeyboard = section.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
                    || section.Contains("Vendor=0b05", StringComparison.OrdinalIgnoreCase);
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
    /// Internal so the FnLockRemapper bridge can reuse this mapping when it
    /// has exclusively grabbed event6/event7 and needs to dispatch the same
    /// binding names that LinuxAsusWmi would have dispatched.
    /// </summary>
    internal static string MapLinuxKeyToBindingName(ushort linuxKeyCode)
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
    /// Map MSC_SCAN values to configurable key binding names.
    /// MSC_SCAN values equal Windows WMI event codes and are consistent across
    /// all ASUS models (TUF, ROG, Vivobook) even when KEY_* codes differ.
    /// Internal so the FnLockRemapper bridge can reuse it.
    /// </summary>
    internal static string MapScanCodeToBindingName(int scanCode)
    {
        return scanCode switch
        {
            56 => "m4",    // ROG/M4/M5 button (Windows event 0x38)
            179 => "fnf4", // Fn+F4 Aura key (Windows event 0xB3)
            174 => "fnf5", // Fn+F5 performance cycle (Windows event 0xAE)
            _ => ""
        };
    }

    /// <summary>
    /// Map MSC_SCAN values to legacy G-Helper event codes for non-configurable keys.
    /// MSC_SCAN values ARE the Windows WMI event codes, so they pass through directly.
    /// Internal so the FnLockRemapper bridge can reuse it.
    /// </summary>
    internal static int MapScanCodeToLegacyEvent(int scanCode)
    {
        return scanCode switch
        {
            196 => 196, // Fn+F3 keyboard brightness up
            197 => 197, // Fn+F2 keyboard brightness down
            107 => 107, // Fn+F10 touchpad toggle
            108 => 108, // Fn+F11 sleep
            133 => 133, // Camera toggle
            136 => 136, // Fn+F12 airplane
            16 => 16,   // Fn+F7 brightness down
            32 => 32,   // Fn+F8 brightness up
            78 => 78,   // Fn+ESC FnLock
            124 => 124, // Mic mute (M3)
            _ => -1
        };
    }

    /// <summary>
    /// Map Linux KEY_* codes to legacy G-Helper event codes for non-configurable keys
    /// (keyboard brightness, touchpad, etc.).
    /// Internal so the FnLockRemapper bridge can reuse it.
    /// </summary>
    internal static int MapLinuxKeyToLegacyEvent(ushort linuxKeyCode)
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

    // Feature detection

    public bool IsFeatureSupported(string feature)
    {
        // Check legacy sysfs, bus sysfs, AND firmware-attributes
        return SysfsHelper.ResolveAttrPath(feature, SysfsHelper.AsusWmiPlatform, SysfsHelper.AsusBusPlatform) != null;
    }

    // Helpers

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
        _fwAttrWatching = false;

        lock (_eventStreams)
        {
            foreach (var fs in _eventStreams)
            {
                try
                { fs.Close(); }
                catch { }
            }
            _eventStreams.Clear();
        }

        _eventThread?.Join(500);
        _fwAttrWatchThread?.Join(500);
    }
}

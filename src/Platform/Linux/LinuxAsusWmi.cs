namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux implementation of IAsusWmi using the asus-wmi kernel module (sysfs).
/// Maps G-Helper's ATKACPI device IDs to Linux sysfs attributes.
/// 
/// Sysfs paths — resolved at runtime via SysfsHelper.ResolveAttrPath():
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
public class LinuxAsusWmi : IAsusWmi
{
    private string? _asusFanRpmHwmonDir;   // Hwmon with fan*_input files (RPM reading)
    private string? _asusFanCurveHwmonDir; // Hwmon with pwm*_auto_point* files (fan curve control)
    private string? _asusBaseHwmonDir;     // Base ASUS hwmon (temps, etc.)
    private string? _cpuTempHwmonDir;      // CPU temperature hwmon (coretemp/k10temp)
    private string? _batteryDir;
    private Thread? _eventThread;
    private volatile bool _eventListening;
    private readonly List<FileStream> _eventStreams = new();  // Track open evdev streams for Dispose()

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
            // GPU mode changes MUST go through GpuModeController — direct writes to
            // dgpu_disable cause kernel panics if the NVIDIA/AMD driver is active.
            // 0x00090020 (GPUEco) and 0x00090016 (GPUMux) intentionally removed.
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
        var path = SysfsHelper.ResolveAttrPath("throttle_thermal_policy", SysfsHelper.AsusWmiPlatform);
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
        var path = SysfsHelper.ResolveAttrPath("throttle_thermal_policy", SysfsHelper.AsusWmiPlatform);
        if (path != null)
        {
            SysfsHelper.WriteInt(path, mode);
        }
        // If throttle_thermal_policy doesn't exist, ModeControl still sets platform_profile directly
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

        // Some models only accept 60/80/100 as charge limits
        if (Helpers.AppConfig.IsChargeLimit6080())
        {
            if (percent > 85) percent = 100;
            else if (percent >= 80) percent = 80;
            else if (percent < 60) percent = 60;
        }

        SysfsHelper.WriteInt(
            Path.Combine(_batteryDir, "charge_control_end_threshold"), percent);
    }

    // ── GPU ──

    public bool GetGpuEco()
    {
        var path = SysfsHelper.ResolveAttrPath("dgpu_disable", SysfsHelper.AsusBusPlatform);
        if (path == null) return false;
        return SysfsHelper.ReadInt(path, 0) == 1;
    }

    /// <summary>
    /// Check if the NVIDIA DRM driver is currently active (holding GPU resources).
    /// Returns true if nvidia_drm is loaded AND refcnt > 0.
    /// Used by SetGpuEco guard — prevents kernel panics from ACPI hot-removal.
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
            Helpers.Logger.WriteLine("SetGpuEco guard: nvidia_drm loaded but refcnt unreadable — assuming active");
            return true;
        }

        return refcnt > 0;
    }

    /// <summary>
    /// Check if ANY dGPU driver is currently active (NVIDIA or AMD).
    /// Combined guard for SetGpuEco — prevents ACPI hot-removal crash for both vendors.
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
    /// AMD has no module refcnt like NVIDIA — instead check PCI runtime_status.
    /// Returns true if amdgpu module is loaded AND bound to the dGPU AND runtime_status != "suspended".
    /// </summary>
    private static bool IsAmdDgpuDriverActive()
    {
        // amdgpu module not loaded → safe
        if (!Directory.Exists("/sys/module/amdgpu"))
            return false;

        // Module is loaded — find the AMD dGPU PCI device
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
            // Can't read driver symlink — fall through to runtime_status check
        }

        // Check runtime power state
        string statusPath = $"/sys/bus/pci/devices/{pciAddr}/power/runtime_status";
        string? status = SysfsHelper.ReadAttribute(statusPath);

        if (status == "suspended")
        {
            Helpers.Logger.WriteLine($"SetGpuEco guard: AMD dGPU {pciAddr} runtime_status=suspended — safe");
            return false;
        }

        // "active" or any other value (including null/unreadable) → assume active for safety
        Helpers.Logger.WriteLine($"SetGpuEco guard: AMD dGPU {pciAddr} runtime_status={status ?? "unreadable"} — active");
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
            if (!Directory.Exists(pciDir)) return null;

            foreach (var deviceDir in Directory.GetDirectories(pciDir))
            {
                string? vendor = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "vendor"));
                if (vendor != "0x1002") continue;

                string? cls = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "class"));
                if (cls == null) continue;
                if (!cls.StartsWith("0x0300") && !cls.StartsWith("0x0302")) continue;

                string? bootVga = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "boot_vga"));
                if (bootVga == "1") continue; // iGPU, not dGPU

                return Path.GetFileName(deviceDir);
            }
        }
        catch { }

        return null;
    }

    public void SetGpuEco(bool enabled)
    {
        var path = SysfsHelper.ResolveAttrPath("dgpu_disable", SysfsHelper.AsusBusPlatform);
        if (path == null) return;

        // Skip write if already in desired state — writing dgpu_disable can block
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
            // ── SAFETY GUARD 1: Never disable dGPU when dGPU driver is active ──
            // Writing dgpu_disable=1 triggers ACPI hot-removal (acpiphp_disable_and_eject_slot).
            // If nvidia_drm or amdgpu is bound, hot-removal causes kernel panic / GPU fault.
            if (IsDgpuDriverActive())
                throw new InvalidOperationException(
                    "SAFETY: Cannot write dgpu_disable=1 — dGPU driver is active. " +
                    "This would cause a kernel panic via ACPI hot-removal.");

            // ── SAFETY GUARD 2: Never disable dGPU when MUX=0 (Ultimate/dGPU-direct) ──
            // MUX=0 means the dGPU is the sole display output. Disabling it = no display = black screen.
            // This creates an impossible boot state that requires CMOS reset to recover.
            int mux = GetGpuMuxMode();
            if (mux == 0)
                throw new InvalidOperationException(
                    "SAFETY: Cannot write dgpu_disable=1 — gpu_mux_mode=0 (Ultimate). " +
                    "This creates an impossible state: dGPU is sole display output but powered off.");
        }

        SysfsHelper.WriteInt(path, desired);

        if (!enabled)
        {
            // ── PCI bus rescan after enabling dGPU ──
            // After dgpu_disable=0, the dGPU needs to reappear in the PCI device tree.
            // The kernel ACPI _ON method usually triggers re-enumeration, but an explicit
            // rescan ensures reliability (supergfxctl pattern: special_asus.rs:145-149).
            // Best-effort: /sys/bus/pci/rescan requires root, may fail for non-root users.
            Helpers.Logger.WriteLine("SetGpuEco: dGPU enabled, triggering PCI bus rescan");
            Thread.Sleep(50); // Brief settle time for hardware (supergfxctl uses 50ms)
            if (!SysfsHelper.WriteAttribute("/sys/bus/pci/rescan", "1"))
                Helpers.Logger.WriteLine("SetGpuEco: PCI rescan failed (may need root) — dGPU should re-enumerate via ACPI");
        }
    }

    public int GetGpuMuxMode()
    {
        var path = SysfsHelper.ResolveAttrPath("gpu_mux_mode", SysfsHelper.AsusBusPlatform);
        if (path == null) return -1;
        return SysfsHelper.ReadInt(path, -1);
    }

    public void SetGpuMuxMode(int mode)
    {
        var path = SysfsHelper.ResolveAttrPath("gpu_mux_mode", SysfsHelper.AsusBusPlatform);
        if (path == null) return;

        int current = SysfsHelper.ReadInt(path, -1);
        if (current == mode)
        {
            Helpers.Logger.WriteLine($"SetGpuMuxMode: gpu_mux_mode already {mode}, skipping write");
            return;
        }

        // ── SAFETY GUARD 3: Never write gpu_mux_mode when dGPU is disabled ──
        // Firmware rejects MUX changes when dgpu_disable=1 (returns ENODEV).
        // The kernel write can hang for several seconds before returning the error.
        // Refusing immediately is safer and faster.
        if (GetGpuEco())
            throw new InvalidOperationException(
                "SAFETY: Cannot write gpu_mux_mode — dgpu_disable=1. " +
                "Firmware rejects MUX changes when dGPU is powered off.");

        if (!SysfsHelper.WriteInt(path, mode))
            throw new IOException(
                $"gpu_mux_mode write rejected by firmware (wrote {mode} to {path})");
    }

    // ── Display ──

    public bool GetPanelOverdrive()
    {
        // panel_od on legacy, panel_overdrive on some firmware-attributes
        var path = SysfsHelper.ResolveAttrPath("panel_od", SysfsHelper.AsusWmiPlatform);
        path ??= SysfsHelper.ResolveAttrPath("panel_overdrive", SysfsHelper.AsusWmiPlatform);
        if (path == null) return false;
        return SysfsHelper.ReadInt(path, 0) == 1;
    }

    public void SetPanelOverdrive(bool enabled)
    {
        var path = SysfsHelper.ResolveAttrPath("panel_od", SysfsHelper.AsusWmiPlatform);
        path ??= SysfsHelper.ResolveAttrPath("panel_overdrive", SysfsHelper.AsusWmiPlatform);
        if (path != null)
            SysfsHelper.WriteInt(path, enabled ? 1 : 0);
    }

    public int GetMiniLedMode()
    {
        var path = SysfsHelper.ResolveAttrPath("mini_led_mode", SysfsHelper.AsusBusPlatform);
        if (path == null) return -1;
        return SysfsHelper.ReadInt(path, -1);
    }

    public void SetMiniLedMode(int mode)
    {
        var path = SysfsHelper.ResolveAttrPath("mini_led_mode", SysfsHelper.AsusBusPlatform);
        if (path != null)
            SysfsHelper.WriteInt(path, mode);
    }

    // ── PPT / Power limits ──

    public void SetPptLimit(string attribute, int watts)
    {
        // PPT attributes: ppt_pl1_spl, ppt_pl2_sppt, ppt_fppt, nv_dynamic_boost, nv_temp_target
        var path = SysfsHelper.ResolveAttrPath(attribute, SysfsHelper.AsusWmiPlatform);
        if (path != null)
            SysfsHelper.WriteInt(path, watts);
    }

    public int GetPptLimit(string attribute)
    {
        var path = SysfsHelper.ResolveAttrPath(attribute, SysfsHelper.AsusWmiPlatform);
        if (path == null) return -1;
        return SysfsHelper.ReadInt(path, -1);
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
        // Protocol: [1, mode, R, G, B, speed] — matches asusctl's TUF write
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
        // Protocol: [1, boot, awake, sleep, 1] — matches asusctl's TUF power state
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
        // Check legacy sysfs, bus sysfs, AND firmware-attributes
        return SysfsHelper.ResolveAttrPath(feature, SysfsHelper.AsusWmiPlatform, SysfsHelper.AsusBusPlatform) != null;
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

        // Close evdev streams to unblock any blocking fs.Read() calls
        lock (_eventStreams)
        {
            foreach (var fs in _eventStreams)
            {
                try { fs.Close(); } catch { }
            }
            _eventStreams.Clear();
        }

        _eventThread?.Join(500);
    }
}

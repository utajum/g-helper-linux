using System.Text;
using GHelper.Linux.USB;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Generates a comprehensive system diagnostics report for troubleshooting.
/// Collects sysfs state, permissions, kernel modules, model detection flags,
/// and hardware info into a formatted text block for GitHub issue reports.
/// </summary>
public static class Diagnostics
{
    public static string GenerateReport()
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== G-Helper Linux Diagnostics ===");
        sb.AppendLine();

        // System Identity
        AppendSystemInfo(sb);

        // Power source posture (AC, battery, profile, throttle, boost, ASPM)
        AppendPowerSource(sb);

        // CPU model, governor, microcode, freq range, online cores
        AppendCpu(sb);

        // Model Detection Flags
        AppendModelFlags(sb);

        // App Config snapshot (curated whitelist; no PII)
        AppendAppConfig(sb);

        // Kernel Modules
        AppendKernelModules(sb);

        // Module Backend (asus-nb-wmi vs asus-armoury)
        AppendModuleBackend(sb);

        // Lenovo platform state (only emitted on Lenovo hardware)
        AppendLenovoState(sb);

        // Raw WMI (debugfs)
        AppendRawWmiProbe(sb);

        // Sysfs Permissions & Values
        AppendSysfsState(sb);

        // hwmon Devices
        AppendHwmon(sb);

        // GPU drivers (NVIDIA + AMD): version, modules, install method,
        // PCI bind state, runtime power state, initramfs presence
        AppendNvidia(sb);
        AppendAmdGpu(sb);

        // AMD SMU tuning state (ryzenadj): family, supported params,
        // live/saved/stock limits
        AppendRyzenSmu(sb);

        // Displays: connectors, compositor, current refresh
        AppendDisplays(sb);

        // USB HID (ASUS)
        AppendUsbDevices(sb);

        // firmware-attributes (asus_armoury)
        AppendFirmwareAttributes(sb);

        // Input Devices
        AppendInputDevices(sb);

        // ROG Ally controller state (only emitted on RC71L/RC72L)
        AppendAllyState(sb);

        // XG Mobile dock state (only emitted when egpu_connected=1 or
        // a USB-HID dock is enumerated)
        AppendXgmState(sb);

        AppendLedSysfs(sb);

        // ghelper systemd units (boot service + autostart .desktop)
        AppendGhelperUnits(sb);

        // udev / tmpfiles
        AppendInstallState(sb);

        // Suspend / resume history (this boot)
        AppendSuspendResume(sb);

        // Boot service journal
        AppendBootServiceLog(sb);

        // Recent log (last, always at the end)
        AppendRecentLog(sb);

        return sb.ToString();
    }

    private static void AppendSystemInfo(StringBuilder sb)
    {
        sb.AppendLine("--- System ---");

        sb.AppendLine($"G-Helper: v{AppConfig.AppVersion}");

        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        sb.AppendLine($"Mode: {(string.IsNullOrEmpty(appImage) ? "binary" : $"AppImage ({appImage})")}");

        var model = Platform.Linux.SysfsHelper.ReadAttribute(
            Path.Combine(Platform.Linux.SysfsHelper.DmiId, "product_name")) ?? "?";
        sb.AppendLine($"Product: {model}");

        var bios = Platform.Linux.SysfsHelper.ReadAttribute(
            Path.Combine(Platform.Linux.SysfsHelper.DmiId, "bios_version")) ?? "?";
        sb.AppendLine($"BIOS: {bios}");

        var kernel = Platform.Linux.SysfsHelper.RunCommand("uname", "-r") ?? "?";
        sb.AppendLine($"Kernel: {kernel}");

        // Secure Boot / kernel lockdown: blocks unsigned modules + direct SMU/MSR
        // access, which silently removes Ryzen power tuning and similar features.
        AppendSecureBootState(sb);

        // OS release
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                foreach (var line in lines)
                {
                    if (line.StartsWith("PRETTY_NAME="))
                    {
                        sb.AppendLine($"OS: {line[12..].Trim('"')}");
                        break;
                    }
                }
            }
        }
        catch { }

        // Desktop environment
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?";
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?";
        sb.AppendLine($"Desktop: {desktop} ({session})");

        sb.AppendLine();
    }

    /// <summary>Report Secure Boot / kernel-lockdown state. When lockdown is
    /// active, unsigned modules (ryzen_smu) and direct SMU/MSR access are blocked,
    /// which silently removes Ryzen power tuning and similar low-level features.</summary>
    private static void AppendSecureBootState(StringBuilder sb)
    {
        // /sys/kernel/security/lockdown: "none [integrity] confidentiality" -
        // the active token is the bracketed one. Absent if the LSM isn't built.
        string? raw = Platform.Linux.SysfsHelper.ReadAttribute("/sys/kernel/security/lockdown");
        if (string.IsNullOrEmpty(raw))
        {
            sb.AppendLine("Secure Boot: unknown (lockdown LSM not exposed)");
            return;
        }

        int a = raw.IndexOf('['), b = raw.IndexOf(']');
        string active = (a >= 0 && b > a) ? raw.Substring(a + 1, b - a - 1) : raw.Trim();

        if (active == "none")
        {
            sb.AppendLine("Secure Boot / lockdown: off");
            return;
        }

        sb.AppendLine($"Secure Boot / lockdown: ACTIVE ({active})");
        sb.AppendLine("  WARNING: kernel lockdown blocks unsigned modules and direct SMU/MSR");
        sb.AppendLine("           access - Ryzen power tuning (ryzen_smu / curve optimizer /");
        sb.AppendLine("           STAPM / APU limits) and similar features are unavailable.");
        sb.AppendLine("           Disable Secure Boot in BIOS to restore.");
    }

    private static void AppendModelFlags(StringBuilder sb)
    {
        sb.AppendLine("--- Model Detection ---");

        var flags = new (string Name, bool Value)[]
        {
            ("IsTUF", AppConfig.IsTUF()),
            ("IsROG", AppConfig.IsROG()),
            ("IsStrix", AppConfig.IsStrix()),
            ("IsVivoZenbook", AppConfig.IsVivoZenbook()),
            ("IsProArt", AppConfig.IsProArt()),
            ("IsAlly", AppConfig.IsAlly()),
            ("NoGpu", AppConfig.NoGpu()),
            ("IsChargeLimit6080", AppConfig.IsChargeLimit6080()),
            ("IsWhite", AppConfig.IsWhite()),
            ("NoAura", AppConfig.NoAura()),
            ("IsBacklightZones", AppConfig.IsBacklightZones()),
            ("IsStrix4ZoneFlipped", AppConfig.IsStrix4ZoneFlipped()),
            ("IsNoDirectRGB", AppConfig.IsNoDirectRGB()),
            ("IsDynamicLighting", AppConfig.IsDynamicLighting()),
            ("IsIntelHX", AppConfig.IsIntelHX()),
            ("IsCPULight", AppConfig.IsCPULight()),
            ("IsResetRequired", AppConfig.IsResetRequired()),
            ("IsFanRequired", AppConfig.IsFanRequired()),
            ("IsSleepBacklight", AppConfig.IsSleepBacklight()),
            ("IsSlash", AppConfig.IsSlash()),
            // AURA hardware-detected state (populated by Aura.DetectBacklightType
            // at startup). When IsBacklightDetected is false the device gets
            // the basic AURA mode set (no model-list fallback).
            ("Aura.IsBacklightDetected", Aura.IsBacklightDetected),
            ("Aura.HasLogo", Aura.HasLogo),
            ("Aura.HasLightbar", Aura.HasLightbar),
            ("Aura.HasRearglow", Aura.HasRearglow),
            ("Aura.isWhite", Aura.isWhite),
        };

        foreach (var (name, value) in flags)
        {
            if (value)
                sb.AppendLine($"  {name}: true");
        }

        // AURA detection scalar fields (always shown for diagnostics).
        // FamilyByte / YearByte are not exposed publicly - check the
        // probe log line ("Aura Probe: Type=... Year=... Family=...")
        // for those values.
        sb.AppendLine($"  Aura.BacklightType: {Aura.BacklightType}");

        // Show any that are true; if none are true, say so
        if (!flags.Any(f => f.Value))
            sb.AppendLine("  (no model flags matched)");

        sb.AppendLine();
    }

    private static void AppendKernelModules(StringBuilder sb)
    {
        sb.AppendLine("--- Kernel Modules ---");

        var lsmod = Platform.Linux.SysfsHelper.RunCommand("bash",
            "-c \"lsmod 2>/dev/null | grep -iE 'asus|hid_asus' || echo '(none found)'\"");
        sb.AppendLine(lsmod ?? "(lsmod failed)");
        sb.AppendLine();
    }

    private static void AppendModuleBackend(StringBuilder sb)
    {
        sb.AppendLine("--- Module Backend ---");

        // Detect which kernel module is providing ASUS WMI attributes
        bool hasLegacy = Directory.Exists(Platform.Linux.SysfsHelper.AsusWmiPlatform)
                      || Directory.Exists(Platform.Linux.SysfsHelper.AsusBusPlatform);
        bool hasFirmwareAttrs = Directory.Exists(Platform.Linux.SysfsHelper.FirmwareAttributes);

        if (hasLegacy && hasFirmwareAttrs)
            sb.AppendLine("  Active: both asus-nb-wmi AND asus-armoury (dual backend)");
        else if (hasLegacy)
            sb.AppendLine("  Active: asus-nb-wmi (legacy sysfs)");
        else if (hasFirmwareAttrs)
            sb.AppendLine("  Active: asus-armoury (firmware-attributes)");
        else
            sb.AppendLine("  Active: NONE - no ASUS WMI module detected");

        // Show which backend resolved for the two safety-critical GPU attributes
        var criticalAttrs = new[] { Platform.Linux.AsusAttributes.DgpuDisable, Platform.Linux.AsusAttributes.GpuMuxMode };
        foreach (var attr in criticalAttrs)
        {
            var resolved = Platform.Linux.SysfsHelper.ResolveAttrPath(attr,
                Platform.Linux.SysfsHelper.AsusWmiPlatform,
                Platform.Linux.SysfsHelper.AsusBusPlatform);

            if (resolved == null)
            {
                sb.AppendLine($"  {attr.LegacyName}: not found (feature unavailable)");
            }
            else
            {
                string backend = Platform.Linux.SysfsHelper.IsFirmwareAttributesPath(resolved)
                    ? "asus-armoury" : "asus-nb-wmi";
                sb.AppendLine($"  {attr.LegacyName}: {backend} → {resolved}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendLenovoState(StringBuilder sb)
    {
        if (!AppConfig.IsLenovoDevice())
            return;

        sb.AppendLine("--- Lenovo Platform ---");
        sb.AppendLine($"  Vendor: {AppConfig.GetDmiVendor()}");
        sb.AppendLine($"  BIOS prefix: {Platform.Linux.Lenovo.LenovoDetection.BiosPrefix()}");

        var (driver, loaded) = Platform.Linux.Lenovo.LenovoDetection.PlatformDriver();
        sb.AppendLine($"  Driver: {driver} ({(loaded ? "loaded" : "not loaded")})");
        sb.AppendLine($"  ideapad device: {Platform.Linux.Lenovo.LenovoSysfs.IdeapadDevice() ?? "none"}");

        string? profile = Platform.Linux.SysfsHelper.ReadAttribute(Platform.Linux.SysfsHelper.PlatformProfile);
        string? choices = Platform.Linux.SysfsHelper.ReadAttribute(Platform.Linux.SysfsHelper.PlatformProfileChoices);
        sb.AppendLine($"  platform_profile: {profile ?? "n/a"} (choices: {choices ?? "n/a"})");

        foreach (var attr in new[] { "fan_mode", "conservation_mode", "fn_lock", "usb_charging", "camera_power" })
        {
            var path = Platform.Linux.Lenovo.LenovoSysfs.IdeapadAttr(attr);
            sb.AppendLine($"  ideapad {attr}: {(path != null ? Platform.Linux.SysfsHelper.ReadAttribute(path) ?? "?" : "n/a")}");
        }

        var fwAttrs = Platform.Linux.Lenovo.LenovoSysfs.FirmwareAttributesDir();
        sb.AppendLine($"  lenovo-wmi-other attributes: {fwAttrs ?? "none"}");
        if (fwAttrs != null)
        {
            foreach (var name in new[] { "ppt_pl1_spl", "ppt_pl2_sppt", "ppt_pl3_fppt" })
            {
                var path = Platform.Linux.Lenovo.LenovoSysfs.FirmwareAttrCurrentValue(name);
                sb.AppendLine($"    {name}: {(path != null ? Platform.Linux.SysfsHelper.ReadAttribute(path) ?? "?" : "n/a")}");
            }
        }

        sb.AppendLine($"  fan hwmon: {Platform.Linux.Lenovo.LenovoSysfs.FanHwmon() ?? "none"}");
        sb.AppendLine($"  kbd backlight LED: {Platform.Linux.Lenovo.LenovoSysfs.KbdBacklightLed() ?? "none"}");

        var chargeTypes = Platform.Linux.Lenovo.LenovoSysfs.BatteryChargeTypes();
        sb.AppendLine($"  battery charge_types: {(chargeTypes != null ? Platform.Linux.SysfsHelper.ReadAttribute(chargeTypes) ?? "?" : "n/a")}");

        // Generation capability note (BIOS-prefix table from LenovoLegionLinux research)
        var note = Platform.Linux.Lenovo.LenovoDetection.BiosCapabilityNote();
        if (note != null)
            sb.AppendLine($"  generation: {note}");

        // fan_mode friendly names (model-dependent EC presets)
        var fanModePath = Platform.Linux.Lenovo.LenovoSysfs.IdeapadAttr("fan_mode");
        if (fanModePath != null)
        {
            int fm = Platform.Linux.SysfsHelper.ReadInt(fanModePath, -1);
            string fmName = fm switch
            {
                0 => "Super Silent",
                1 => "Standard",
                2 => "Dual / Dust Cleaning",
                4 => "Efficient Thermal Dissipation",
                133 => "Super Silent (133 readback quirk)",
                _ => "unknown",
            };
            sb.AppendLine($"  fan_mode decoded: {fm} = {fmName}");
        }

        // Manual fan target (kernel 7.0+)
        var targetHwmon = Platform.Linux.Lenovo.LenovoSysfs.FanTargetHwmon();
        sb.AppendLine($"  fan target hwmon: {targetHwmon ?? "none (kernel < 7.0 or unsupported)"}");
        if (targetHwmon != null)
        {
            for (int fan = 1; fan <= 4; fan++)
            {
                var range = Platform.Linux.Lenovo.LenovoSysfs.FanTargetRange(fan);
                if (range == null)
                    continue;
                int target = Platform.Linux.Lenovo.LenovoFeatures.GetFanTarget(fan);
                sb.AppendLine($"    fan{fan}: target={(target <= 0 ? "auto" : target.ToString())} range={range.Value.Min}-{range.Value.Max} step={range.Value.Div}");
            }
        }

        // Lenovo LEDs (fnlock; micmute/mute from lenovo-wmi-hotkey-utilities 6.14+)
        var leds = new List<string>();
        foreach (var led in new[] { "platform::fnlock", "platform::micmute", "platform::mute", "platform::ylogo", "platform::ioport" })
            if (Platform.Linux.Lenovo.LenovoSysfs.Led(led) != null)
                leds.Add(led);
        sb.AppendLine($"  LEDs: {(leds.Count > 0 ? string.Join(", ", leds) : "none")}");

        // Flip to Start UEFI variable
        var flip = Platform.Linux.Lenovo.LenovoFeatures.GetFlipToStart();
        sb.AppendLine($"  flip-to-start (FBSWIF): {(flip == null ? "n/a" : flip.Value ? "on" : "off")}");

        // RGB keyboard
        sb.AppendLine($"  RGB keyboard: {USB.LenovoRgb.Kind}");

        // ideapad debugfs feature dump (root-only; readable when perms allow)
        foreach (var (label, path) in new[]
        {
            ("debugfs cfg", Platform.Linux.Lenovo.LenovoSysfs.IdeapadDebugfsCfg),
            ("debugfs status", Platform.Linux.Lenovo.LenovoSysfs.IdeapadDebugfsStatus),
        })
        {
            try
            {
                if (File.Exists(path))
                {
                    string content = File.ReadAllText(path).TrimEnd();
                    sb.AppendLine($"  {label}:");
                    foreach (var line in content.Split('\n'))
                        sb.AppendLine($"    {line}");
                }
            }
            catch
            {
                sb.AppendLine($"  {label}: not readable (debugfs is root-only)");
            }
        }

        sb.AppendLine();
    }

    private static void AppendRawWmiProbe(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("--- Raw WMI (debugfs) ---");
        sb.AppendLine(Platform.Linux.AsusWmiDebugfs.GetDiagnostics());
    }

    private static void AppendSysfsState(StringBuilder sb)
    {
        sb.AppendLine("--- Sysfs State ---");

        // Fixed paths (non-WMI attributes - always at known locations)
        var fixedPaths = new[]
        {
            // Battery
            "/sys/class/power_supply/BAT0/charge_control_end_threshold",
            "/sys/class/power_supply/BAT1/charge_control_end_threshold",
            "/sys/class/power_supply/BATC/charge_control_end_threshold",
            "/sys/class/power_supply/BATT/charge_control_end_threshold",
            // Keyboard
            "/sys/class/leds/asus::kbd_backlight/brightness",
            "/sys/class/leds/asus::kbd_backlight/multi_intensity",
            "/sys/class/leds/asus::kbd_backlight/kbd_rgb_mode",
            "/sys/class/leds/asus::kbd_backlight/kbd_rgb_state",
            // ROG Ally gamepad RGB LED node (asus-armoury exposes this for
            // controller-side lighting in addition to the standard keyboard
            // backlight). Reference: asusctl rog-platform/src/keyboard_led.rs:51.
            "/sys/class/leds/ally:rgb:gamepad/brightness",
            "/sys/class/leds/ally:rgb:gamepad/multi_intensity",
            // Platform profile
            "/sys/firmware/acpi/platform_profile",
            "/sys/firmware/acpi/platform_profile_choices",
            // CPU boost
            "/sys/devices/system/cpu/intel_pstate/no_turbo",
            "/sys/devices/system/cpu/cpufreq/boost",
            // ASPM
            "/sys/module/pcie_aspm/parameters/policy",
        };

        foreach (var path in fixedPaths)
        {
            if (!File.Exists(path))
                continue;

            var perms = GetFilePermissions(path);
            var value = Platform.Linux.SysfsHelper.ReadAttribute(path);

            // kbd_rgb_mode and kbd_rgb_state are DEVICE_ATTR_WO in the kernel - read always fails
            if (value == null && (path.EndsWith("kbd_rgb_mode") || path.EndsWith("kbd_rgb_state")))
                value = "(write-only, present)";
            else
                value ??= "(read failed)";

            var shortPath = path
                .Replace("/sys/class/power_supply/", "power_supply/")
                .Replace("/sys/class/leds/", "leds/")
                .Replace("/sys/devices/system/cpu/", "cpu/")
                .Replace("/sys/firmware/acpi/", "acpi/")
                .Replace("/sys/module/pcie_aspm/parameters/", "pcie_aspm/");

            sb.AppendLine($"  {shortPath}: {perms} = {value}");
        }

        // Keyboard backlight directory listing (TUF kbd_rgb_mode/state diagnostics)
        const string kbdDir = "/sys/class/leds/asus::kbd_backlight";
        if (Directory.Exists(kbdDir))
        {
            sb.AppendLine();
            sb.AppendLine("  kbd_backlight attributes:");
            try
            {
                foreach (var file in Directory.GetFiles(kbdDir).OrderBy(f => f))
                {
                    var name = Path.GetFileName(file);
                    var perms = GetFilePermissions(file);
                    sb.AppendLine($"    {name}: {perms}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    (error: {ex.Message})");
            }
        }

        // Resolved WMI attributes (may be legacy sysfs or firmware-attributes)
        // Uses AsusAttributes.All as single source of truth - covers all 16 known attributes
        sb.AppendLine();
        sb.AppendLine("  WMI attributes (resolved via ResolveAttrPath):");

        foreach (var attr in Platform.Linux.AsusAttributes.All)
        {
            var resolved = Platform.Linux.SysfsHelper.ResolveAttrPath(attr,
                Platform.Linux.SysfsHelper.AsusWmiPlatform,
                Platform.Linux.SysfsHelper.AsusBusPlatform);

            if (resolved == null)
                continue;

            var perms = GetFilePermissions(resolved);
            var value = Platform.Linux.SysfsHelper.ReadAttribute(resolved) ?? "(read failed)";
            string backend = Platform.Linux.SysfsHelper.IsFirmwareAttributesPath(resolved)
                ? "fw-attr" : "legacy";
            string aliasNote = attr.HasAlias ? $" (fw: {attr.FwAttrName})" : "";

            sb.AppendLine($"    {attr.LegacyName}{aliasNote} [{backend}]: {perms} = {value}");
        }

        sb.AppendLine();
    }

    private static void AppendHwmon(StringBuilder sb)
    {
        sb.AppendLine("--- hwmon ---");

        try
        {
            if (Directory.Exists(Platform.Linux.SysfsHelper.Hwmon))
            {
                foreach (var hwmonDir in Directory.GetDirectories(Platform.Linux.SysfsHelper.Hwmon))
                {
                    var name = Platform.Linux.SysfsHelper.ReadAttribute(
                        Path.Combine(hwmonDir, "name")) ?? "(no name)";
                    var dirName = Path.GetFileName(hwmonDir);

                    // For asus-related hwmon, list key files
                    if (name.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("coretemp", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("k10temp", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("amdgpu", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
                    {
                        var extras = new List<string>();
                        if (File.Exists(Path.Combine(hwmonDir, "fan1_input")))
                            extras.Add("fan1");
                        if (File.Exists(Path.Combine(hwmonDir, "fan2_input")))
                            extras.Add("fan2");
                        if (File.Exists(Path.Combine(hwmonDir, "fan3_input")))
                            extras.Add("fan3");
                        if (File.Exists(Path.Combine(hwmonDir, "pwm1_enable")))
                            extras.Add("pwm1");
                        if (File.Exists(Path.Combine(hwmonDir, "pwm2_enable")))
                            extras.Add("pwm2");
                        if (File.Exists(Path.Combine(hwmonDir, "temp1_input")))
                            extras.Add("temp1");

                        var extraStr = extras.Count > 0 ? $" [{string.Join(", ", extras)}]" : "";
                        sb.AppendLine($"  {dirName} = {name}{extraStr}");
                    }
                    else
                    {
                        sb.AppendLine($"  {dirName} = {name}");
                    }
                }
            }
            else
            {
                sb.AppendLine("  /sys/class/hwmon not found");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error: {ex.Message})");
        }

        sb.AppendLine();
    }

    private static void AppendNvidia(StringBuilder sb)
    {
        sb.AppendLine("--- NVIDIA ---");

        bool nvidiaLoaded = Directory.Exists("/sys/module/nvidia");
        if (!nvidiaLoaded)
        {
            sb.AppendLine("  Kernel driver: not loaded");
            // Even when the driver isn't loaded the dGPU may be on the PCI
            // bus (likely bound to nothing or to vfio-pci); show its state.
            var bdfNoDriver = FindPciGpuBdf(vendorId: "0x10de");
            if (bdfNoDriver != null)
            {
                AppendPciDeviceState(sb, bdfNoDriver, "  dGPU");
            }
            sb.AppendLine();
            return;
        }

        // Kernel-side driver version (single sysfs read, doesn't wake the dGPU).
        var version = Platform.Linux.SysfsHelper.ReadAttribute("/sys/module/nvidia/version");
        sb.AppendLine($"  Driver version: {version ?? "?"}");

        // Loaded module set tells us which subsystems initialised.
        var modules = new List<string>();
        foreach (var name in new[]
        {
            "nvidia", "nvidia_drm", "nvidia_modeset", "nvidia_uvm",
            "nvidia_peermem", "nvidia_wmi_ec_backlight"
        })
        {
            if (Directory.Exists($"/sys/module/{name}"))
                modules.Add(name);
        }
        sb.AppendLine($"  Modules loaded: {string.Join(", ", modules)}");

        // KMS state (1 = nvidia DRM KMS active, required for Wayland).
        // The parameter file is 0400 (root-only) on some kernels.
        var modeset = Platform.Linux.SysfsHelper.ReadAttributeSilent("/sys/module/nvidia_drm/parameters/modeset");
        if (modeset != null)
            sb.AppendLine($"  nvidia_drm.modeset: {modeset}");

        // refcnt > 0 means something is actively using the GPU.
        int refcnt = Platform.Linux.SysfsHelper.ReadInt("/sys/module/nvidia_drm/refcnt", -1);
        sb.AppendLine($"  nvidia_drm refcnt: {(refcnt < 0 ? "?" : refcnt.ToString())}");

        // /proc/driver/nvidia/version banner reveals open vs proprietary.
        var procVersion = Platform.Linux.SysfsHelper.ReadAttribute("/proc/driver/nvidia/version");
        if (procVersion != null)
        {
            bool isOpen = procVersion.Contains("open", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"  Variant: {(isOpen ? "open kernel modules" : "proprietary")}");
        }

        // Module path classifies the install method (DKMS, distro kmod,
        // nvidia-installer, etc.). modinfo is reliable across distros.
        var modulePath = Platform.Linux.SysfsHelper.RunCommand("modinfo", "-F filename nvidia");
        if (!string.IsNullOrWhiteSpace(modulePath))
        {
            modulePath = modulePath.Trim();
            sb.AppendLine($"  Module path: {modulePath}");

            string installMethod;
            if (modulePath.Contains("/dkms/"))
                installMethod = "DKMS (compiled from source)";
            else if (modulePath.Contains("/updates/"))
                installMethod = "distro update/kmod";
            else if (modulePath.Contains("/extra/"))
                installMethod = "extra (nvidia-installer or distro package)";
            else if (modulePath.Contains("/kernel/"))
                installMethod = "in-tree kernel";
            else
                installMethod = "unknown";
            sb.AppendLine($"  Install method: {installMethod}");
        }

        // PCI device state.
        var bdf = FindPciGpuBdf(vendorId: "0x10de");
        if (bdf != null)
        {
            AppendPciDeviceState(sb, bdf, "  dGPU");
        }

        // nvidia-smi: reports the userspace library version. Will fail
        // silently with stderr if not installed or driver is in D3cold.
        var smi = Platform.Linux.SysfsHelper.RunCommandWithTimeout(
            "nvidia-smi",
            "--query-gpu=driver_version,name --format=csv,noheader",
            3000);
        if (!string.IsNullOrWhiteSpace(smi))
        {
            var smiLine = smi.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (!string.IsNullOrWhiteSpace(smiLine))
                sb.AppendLine($"  nvidia-smi: {smiLine.Trim()}");
        }

        // Initramfs presence: critical for understanding whether nvidia is
        // already bound to the PCI device by the time our boot service runs.
        var initramfs = ProbeInitramfsForNvidia();
        if (initramfs != null)
            sb.AppendLine($"  In initramfs: {initramfs}");

        // NVML telemetry via gpu-helper (fast, no nvidia-smi fork)
        AppendNvmlDiagnostics(sb);

        sb.AppendLine();

        AppendGpuSwitchingHealth(sb);
    }

    private static void AppendNvmlDiagnostics(StringBuilder sb)
    {
        if (!Gpu.NVidia.NvidiaProcessScanner.EnsureHelper())
            return;

        // nvml-info: static device capabilities
        var info = Platform.Linux.SysfsHelper.RunSudoOrPkexec(
            Platform.Linux.SysfsHelper.GpuHelperPath, new[] { "nvml-info" });
        if (!string.IsNullOrWhiteSpace(info))
        {
            sb.AppendLine("  NVML info:");
            foreach (var line in info.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"    {line.Trim()}");
        }

        // nvml-temp: live telemetry snapshot
        var live = Platform.Linux.SysfsHelper.RunSudoOrPkexec(
            Platform.Linux.SysfsHelper.GpuHelperPath, new[] { "nvml-temp" });
        if (!string.IsNullOrWhiteSpace(live))
            sb.AppendLine($"  NVML live: {live.Trim()}");
    }

    /// <summary>True if any /etc/modprobe.d/*.conf line satisfies the predicate.</summary>
    private static bool ModprobeConfMatch(Func<string, bool> pred)
    {
        try
        {
            const string modprobeDir = "/etc/modprobe.d";
            if (!Directory.Exists(modprobeDir))
                return false;
            foreach (var conf in Directory.EnumerateFiles(modprobeDir, "*.conf"))
                foreach (var line in File.ReadAllLines(conf))
                    if (pred(line))
                        return true;
        }
        catch { }
        return false;
    }

    private static void AppendSwitchHazards(StringBuilder sb)
    {
        var hazards = new List<string>();
        try
        {
            // nvidia_wmi_ec_backlight force=1: param is 0444 (world-readable);
            // grep modprobe.d as backstop when the module is blocked/not loaded.
            var nvForce = Platform.Linux.SysfsHelper.ReadAttributeSilent(
                "/sys/module/nvidia_wmi_ec_backlight/parameters/force");
            bool nvidiaForce = (nvForce != null && nvForce.Trim() == "Y")
                || ModprobeConfMatch(l => l.Contains("nvidia_wmi_ec_backlight")
                    && l.Contains("force") && l.Contains("=1"));
            if (nvidiaForce)
                hazards.Add("nvidia_wmi_ec_backlight force=1 (EC/WMI brightness calls block during dGPU teardown; remove from /etc/modprobe.d)");

            // i915 enable_dpcd_backlight 1/2/3: sysfs param is 0400 (root-only),
            // so scan modprobe.d + /proc/cmdline. -1 (per-VBT default) / 0 = off.
            bool DpcdValue(string v) => v is "1" or "2" or "3";
            bool i915Dpcd = ModprobeConfMatch(line =>
            {
                int ix = line.IndexOf("enable_dpcd_backlight", StringComparison.Ordinal);
                if (ix < 0)
                    return false;
                int eq = line.IndexOf('=', ix);
                if (eq < 0)
                    return false;
                int sp = line.IndexOfAny(new[] { ' ', '\t' }, eq);
                string v = (sp < 0 ? line.Substring(eq + 1) : line.Substring(eq + 1, sp - eq - 1)).Trim();
                return DpcdValue(v);
            });
            string? acpiBacklight = null;
            var cmdline = Platform.Linux.SysfsHelper.ReadAttributeSilent("/proc/cmdline");
            if (cmdline != null)
            {
                foreach (var token in cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!i915Dpcd && token.StartsWith("i915.enable_dpcd_backlight=", StringComparison.Ordinal)
                        && DpcdValue(token.Substring("i915.enable_dpcd_backlight=".Length)))
                        i915Dpcd = true;
                    // acpi_backlight=nvidia_wmi_ec|vendor forces a blocking
                    // EC/firmware backlight (video_detect.c cmdline override).
                    if (token.StartsWith("acpi_backlight=", StringComparison.Ordinal))
                    {
                        string v = token.Substring("acpi_backlight=".Length);
                        if (v is "nvidia_wmi_ec" or "vendor")
                            acpiBacklight = v;
                    }
                }
            }
            if (i915Dpcd)
                hazards.Add("i915 enable_dpcd_backlight (1/2/3) (DP-AUX backlight stalls i915 modeset on dGPU removal; remove from /etc/modprobe.d or cmdline)");
            if (acpiBacklight != null)
                hazards.Add($"acpi_backlight={acpiBacklight} on kernel cmdline (forces blocking EC/firmware backlight; same stall class)");

            // --- driver-conflict class ---

            // nouveau loaded alongside proprietary nvidia: the release/rescan
            // logic targets the nvidia driver; nouveau binding the dGPU breaks it.
            if (Directory.Exists("/sys/module/nouveau"))
                hazards.Add("nouveau is loaded alongside nvidia (driver conflict; blacklist nouveau)");

            // dGPU bound to vfio-pci (passthrough): unbind/rmmod logic expects
            // nvidia/amdgpu, not vfio-pci.
            try
            {
                foreach (var pciDev in Directory.EnumerateDirectories("/sys/bus/pci/devices"))
                {
                    var vendorPath = Path.Combine(pciDev, "vendor");
                    var classPath = Path.Combine(pciDev, "class");
                    if (!File.Exists(vendorPath) || !File.Exists(classPath))
                        continue;
                    string vendor = File.ReadAllText(vendorPath).Trim();
                    if (vendor != "0x10de" && vendor != "0x1002")
                        continue;
                    string klass = File.ReadAllText(classPath).Trim();
                    if (!klass.StartsWith("0x0300") && !klass.StartsWith("0x0302"))
                        continue; // VGA / 3D controller only
                    var driverLink = Path.Combine(pciDev, "driver");
                    if (!Directory.Exists(driverLink))
                        continue;
                    string drv = Path.GetFileName(new DirectoryInfo(driverLink).ResolveLinkTarget(true)?.FullName ?? "");
                    if (drv == "vfio-pci")
                    {
                        hazards.Add($"dGPU {Path.GetFileName(pciDev)} is bound to vfio-pci (passthrough; GPU switch expects nvidia/amdgpu)");
                        break;
                    }
                }
            }
            catch { }
        }
        catch { }

        if (hazards.Count > 0)
        {
            sb.AppendLine("  WARNING: config that can break/stall live GPU switching:");
            foreach (var h in hazards)
                sb.AppendLine($"    - {h}");
        }
    }

    private static void AppendGpuSwitchingHealth(StringBuilder sb)
    {
        if (!Directory.Exists("/sys/module/nvidia"))
            return;

        sb.AppendLine("--- GPU switching health ---");

        // nvidia_drm.modeset (0400 root-only on some kernels)
        var modeset = Platform.Linux.SysfsHelper.ReadAttributeSilent(
            "/sys/module/nvidia_drm/parameters/modeset");
        if (modeset != null)
        {
            bool ok = modeset.Trim() == "Y";
            sb.AppendLine($"  nvidia_drm.modeset: {modeset.Trim()}{(ok ? "" : "  (should be Y for compositor DRM uevent signaling)")}");
        }

        // NVreg_DynamicPowerManagement
        var dynPm = Platform.Linux.SysfsHelper.ReadAttribute(
            "/sys/module/nvidia/parameters/DynamicPowerManagement");
        if (dynPm != null)
        {
            string val = dynPm.Trim();
            bool ok = val == "0x02" || val == "2";
            sb.AppendLine($"  NVreg_DynamicPowerManagement: {val}{(ok ? "" : "  (0x02 enables automatic D3cold for Eco)")}");
        }

        AppendSwitchHazards(sb);

        // DRM device mapping: which card/renderD belongs to nvidia
        try
        {
            foreach (var pciDev in Directory.EnumerateDirectories("/sys/bus/pci/devices"))
            {
                string vendorPath = Path.Combine(pciDev, "vendor");
                if (!File.Exists(vendorPath))
                    continue;
                if (!File.ReadAllText(vendorPath).Trim().StartsWith("0x10de", StringComparison.OrdinalIgnoreCase))
                    continue;
                string drmDir = Path.Combine(pciDev, "drm");
                if (!Directory.Exists(drmDir))
                {
                    sb.AppendLine($"  DRM devices: none (nvidia driver may lack KMS)");
                    break;
                }
                var names = new List<string>();
                foreach (var entry in Directory.EnumerateDirectories(drmDir))
                {
                    string n = Path.GetFileName(entry);
                    if (n.StartsWith("card", StringComparison.Ordinal)
                        || n.StartsWith("renderD", StringComparison.Ordinal))
                        names.Add(n);
                }
                if (names.Count > 0)
                    sb.AppendLine($"  nvidia DRM devices: {string.Join(", ", names)}");
                break;
            }
        }
        catch { }

        // I2C adapters owned by nvidia
        try
        {
            var i2cBuses = new List<string>();
            foreach (var i2cDev in Directory.EnumerateDirectories("/sys/bus/i2c/devices"))
            {
                string namePath = Path.Combine(i2cDev, "name");
                if (!File.Exists(namePath))
                    continue;
                string name = File.ReadAllText(namePath).Trim();
                if (name.Contains("NVIDIA", StringComparison.Ordinal))
                    i2cBuses.Add($"{Path.GetFileName(i2cDev)} ({name})");
            }
            if (i2cBuses.Count > 0)
                sb.AppendLine($"  nvidia I2C adapters: {string.Join("; ", i2cBuses)}");
        }
        catch { }

        // KWIN_DRM_DEVICES
        string? kwinDrmDevices = Environment.GetEnvironmentVariable("KWIN_DRM_DEVICES");
        if (!string.IsNullOrEmpty(kwinDrmDevices))
        {
            sb.AppendLine($"  KWIN_DRM_DEVICES: {kwinDrmDevices}");
        }
        else
        {
            try
            {
                string etcEnv = "/etc/environment";
                if (File.Exists(etcEnv))
                {
                    foreach (var line in File.ReadLines(etcEnv))
                    {
                        if (line.TrimStart().StartsWith("KWIN_DRM_DEVICES=", StringComparison.Ordinal))
                        {
                            sb.AppendLine($"  KWIN_DRM_DEVICES (/etc/environment): {line.Trim()}");
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        // EGL vendor state
        string eglActive = "/usr/share/glvnd/egl_vendor.d/10_nvidia.json";
        string eglInactive = eglActive + "_inactive";
        if (File.Exists(eglActive))
            sb.AppendLine("  nvidia EGL vendor: active");
        else if (File.Exists(eglInactive))
            sb.AppendLine("  nvidia EGL vendor: hidden (Eco mode)");

        // Vulkan ICD state
        string vkActive = "/usr/share/vulkan/icd.d/nvidia_icd.json";
        string vkInactive = vkActive + "_inactive";
        if (File.Exists(vkActive))
            sb.AppendLine("  nvidia Vulkan ICD: active");
        else if (File.Exists(vkInactive))
            sb.AppendLine("  nvidia Vulkan ICD: hidden (Eco mode)");

        sb.AppendLine();
    }

    // AMD GPU diagnostics. Useful on AMD APUs (iGPU only) as well as
    // hybrid AMD + AMD dGPU laptops. The amdgpu kernel driver carries
    // version through DRM IOCTL but we read what's cheaply available.
    private static void AppendAmdGpu(StringBuilder sb)
    {
        sb.AppendLine("--- AMD GPU ---");

        bool amdgpuLoaded = Directory.Exists("/sys/module/amdgpu");
        if (!amdgpuLoaded)
        {
            sb.AppendLine("  Kernel driver: not loaded");
            sb.AppendLine();
            return;
        }

        // Module file path tells us where amdgpu came from.
        var modulePath = Platform.Linux.SysfsHelper.RunCommand("modinfo", "-F filename amdgpu");
        if (!string.IsNullOrWhiteSpace(modulePath))
            sb.AppendLine($"  Module path: {modulePath.Trim()}");

        // amdgpu version string (kernel module). Most kernels expose this
        // as a srcversion or via /sys/module/amdgpu/version (not always
        // present), so do a best-effort read.
        var amdVersion = Platform.Linux.SysfsHelper.ReadAttribute("/sys/module/amdgpu/version")
                       ?? Platform.Linux.SysfsHelper.ReadAttribute("/sys/module/amdgpu/srcversion");
        if (!string.IsNullOrEmpty(amdVersion))
            sb.AppendLine($"  Module version: {amdVersion}");

        // Enumerate all AMD GPU PCI devices (iGPU + dGPU on hybrid AMD
        // laptops). Mainly to surface power state on each card.
        var amdBdfs = FindAllPciGpuBdfs(vendorId: "0x1002");
        if (amdBdfs.Count == 0)
        {
            sb.AppendLine("  PCI devices: (none with vendor 0x1002 + display class)");
        }
        else
        {
            for (int i = 0; i < amdBdfs.Count; i++)
            {
                var label = amdBdfs.Count == 1 ? "  GPU" : $"  GPU[{i}]";
                AppendPciDeviceState(sb, amdBdfs[i], label);
            }
        }

        // rocm-smi is the AMD analogue of nvidia-smi but rarely installed.
        // Try it anyway; silent failure if not present.
        var rocm = Platform.Linux.SysfsHelper.RunCommandWithTimeout(
            "rocm-smi", "--showproductname --csv", 3000);
        if (!string.IsNullOrWhiteSpace(rocm))
        {
            var firstNonEmpty = rocm.Split('\n').FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(l) && !l.StartsWith("===") && !l.StartsWith("device,"));
            if (!string.IsNullOrWhiteSpace(firstNonEmpty))
                sb.AppendLine($"  rocm-smi: {firstNonEmpty.Trim()}");
        }

        sb.AppendLine();
    }

    // Walks /sys/bus/pci/devices/* and returns the BDF of the first device
    // matching the requested vendor ID with PCI class 0x0300 (VGA display
    // controller) or 0x0302 (3D controller). Returns null if no match.
    private static string? FindPciGpuBdf(string vendorId)
    {
        var all = FindAllPciGpuBdfs(vendorId);
        return all.Count > 0 ? all[0] : null;
    }

    private static List<string> FindAllPciGpuBdfs(string vendorId)
    {
        var result = new List<string>();
        const string pciDevices = "/sys/bus/pci/devices";
        if (!Directory.Exists(pciDevices))
            return result;

        try
        {
            foreach (var dev in Directory.GetDirectories(pciDevices))
            {
                var vendor = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(dev, "vendor"));
                if (vendor == null || !vendor.Equals(vendorId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // class is 24-bit (e.g. "0x030000" = VGA controller, "0x030200" = 3D).
                // We accept anything starting with 0x0300 or 0x0302.
                var pciClass = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(dev, "class"));
                if (pciClass == null)
                    continue;

                if (pciClass.StartsWith("0x0300", StringComparison.OrdinalIgnoreCase)
                    || pciClass.StartsWith("0x0302", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(Path.GetFileName(dev));
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"FindAllPciGpuBdfs scan failed: {ex.Message}");
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void AppendRyzenSmu(StringBuilder sb)
    {
        sb.AppendLine("--- AMD SMU (ryzenadj) ---");
        try
        {
            sb.Append(Platform.Linux.RyzenPower.DebugDump());
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (state unavailable: {ex.Message})");
        }
        sb.AppendLine();
    }

    // Appends a multi-line block describing a PCI device's bind state and
    // runtime power state. Used by both AppendNvidia and AppendAmdGpu so
    // the diagnostic format stays consistent.
    private static void AppendPciDeviceState(StringBuilder sb, string bdf, string labelPrefix)
    {
        sb.AppendLine($"{labelPrefix} BDF: {bdf}");

        var devDir = $"/sys/bus/pci/devices/{bdf}";

        // device + subsystem identify the silicon and the OEM branding.
        var deviceId = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "device"));
        var subsystemVendor = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "subsystem_vendor"));
        var subsystemDevice = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "subsystem_device"));
        if (deviceId != null)
            sb.AppendLine($"{labelPrefix} device: {deviceId} (subsys {subsystemVendor ?? "?"}:{subsystemDevice ?? "?"})");

        // PCI driver binding (nvidia / amdgpu / vfio-pci / nouveau / unbound).
        var driverLink = Path.Combine(devDir, "driver");
        string pciDriver = "(unbound)";
        try
        {
            if (Directory.Exists(driverLink))
            {
                var target = Platform.Linux.SysfsHelper.RunCommand("readlink", $"-f {driverLink}");
                if (!string.IsNullOrWhiteSpace(target))
                    pciDriver = Path.GetFileName(target.Trim());
            }
        }
        catch { /* leave as unbound */ }
        sb.AppendLine($"{labelPrefix} PCI driver: {pciDriver}");

        // Runtime power management state (D0 = active, D3hot/D3cold = suspended).
        var rtStatus = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "power/runtime_status"));
        var rtControl = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "power/control"));
        if (rtStatus != null || rtControl != null)
            sb.AppendLine($"{labelPrefix} PCI power: status={rtStatus ?? "?"} control={rtControl ?? "?"}");

        // Current PCI link speed / width. Drops to lower link speeds when
        // the GPU is in D3cold; useful for verifying eco transitions.
        var linkSpeed = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "current_link_speed"));
        var linkWidth = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devDir, "current_link_width"));
        if (linkSpeed != null || linkWidth != null)
            sb.AppendLine($"{labelPrefix} PCI link: {linkSpeed ?? "?"} x{linkWidth ?? "?"}");
    }

    // Cross-distro initramfs nvidia probe. Returns null if no probe tool
    // is available; "yes (...)" / "no" otherwise. Times out aggressively
    // since initramfs blobs can be 30-80 MB to walk.
    private static string? ProbeInitramfsForNvidia()
    {
        var kernel = Platform.Linux.SysfsHelper.RunCommand("uname", "-r");
        if (string.IsNullOrWhiteSpace(kernel))
            return null;
        kernel = kernel.Trim();

        // (probe binary, image path candidates, list args)
        var candidates = new (string Tool, string[] Images, string Args)[]
        {
            // Arch / CachyOS (mkinitcpio)
            ("lsinitcpio",
             new[]
             {
                 $"/boot/initramfs-linux.img",
                 $"/boot/initramfs-{kernel}.img",
                 $"/boot/initramfs-linux-cachyos.img",
             },
             "-l"),
            // Debian / Ubuntu
            ("lsinitramfs",
             new[]
             {
                 $"/boot/initrd.img-{kernel}",
                 $"/boot/initrd.img",
             },
             ""),
            // Fedora / RHEL (dracut)
            ("lsinitrd",
             new[]
             {
                 $"/boot/initramfs-{kernel}.img",
             },
             ""),
        };

        foreach (var (tool, images, args) in candidates)
        {
            // Check the binary is on PATH; skip if not.
            var which = Platform.Linux.SysfsHelper.RunCommand("which", tool);
            if (string.IsNullOrWhiteSpace(which))
                continue;

            foreach (var img in images)
            {
                if (!File.Exists(img))
                    continue;

                var listing = Platform.Linux.SysfsHelper.RunCommandWithTimeout(
                    "bash",
                    $"-c \"{tool} {args} '{img}' 2>/dev/null | grep -oE 'nvidia[a-z_]*' | sort -u | head -n 8\"",
                    8000);
                if (listing == null)
                    return $"unknown (probe of {img} via {tool} failed)";

                var hits = listing
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Distinct()
                    .ToList();

                if (hits.Count == 0)
                    return $"no ({Path.GetFileName(img)})";

                return $"yes ({string.Join(", ", hits)})";
            }
        }

        return null;
    }

    private static void AppendUsbDevices(StringBuilder sb)
    {
        sb.AppendLine("--- USB HID (ASUS 0x0b05) ---");

        var lsusb = Platform.Linux.SysfsHelper.RunCommand("bash",
            "-c \"lsusb 2>/dev/null | grep -i '0b05' || echo '(none found)'\"");
        sb.AppendLine(lsusb ?? "(lsusb failed)");
        sb.AppendLine();

        // Also scan native hidraw devices (catches I2C-HID that lsusb misses)
        sb.AppendLine("--- HID Raw Devices (ASUS, incl. I2C-HID) ---");
        try
        {
            var devices = USB.HidrawHelper.EnumerateAsusDevices();
            if (devices.Count == 0)
            {
                sb.AppendLine("(none found)");

                // Check if hid_asus module is loaded - needed for I2C-HID hidraw nodes
                bool hidAsusLoaded = Directory.Exists("/sys/module/hid_asus");
                if (!hidAsusLoaded)
                    sb.AppendLine("  NOTE: hid_asus module not loaded (try: sudo modprobe hid_asus)");
            }
            else
            {
                foreach (var dev in devices)
                {
                    sb.AppendLine($"  {dev.Path}: VID=0x{dev.Vendor:X4} PID=0x{dev.Product:X4} Bus={dev.BusName} Aura={dev.HasAuraReport}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error: {ex.Message})");
        }

        sb.AppendLine($"AsusHid.IsAvailable: {USB.AsusHid.IsAvailable()}");
        sb.AppendLine($"AsusHid.UsingI2cHidraw: {USB.AsusHid.UsingI2cHidraw}");
        sb.AppendLine($"Aura.IsAvailable: {USB.Aura.IsAvailable()}");

        // AURA hardware capability probe (GetFeature 0x5D response)
        try
        {
            var caps = USB.HidrawHelper.QueryAuraCapabilities();
            if (caps != null)
            {
                sb.AppendLine($"AURA GetFeature: {BitConverter.ToString(caps, 0, 24)}");
                sb.AppendLine($"  KBBackLightType[9]=0x{caps[9]:X2} Zones[13]=0x{caps[13]:X2} Version[10]=0x{caps[10]:X2} Series[17]=0x{caps[17]:X2}");
                sb.AppendLine($"  LEDs: Bar={caps[18]} Logo={caps[19]} Aero={caps[20]} VCut={caps[21]} Rear={caps[22]} Bump={caps[23]}");
            }
            else
            {
                sb.AppendLine("AURA GetFeature: failed (no response)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"AURA GetFeature: error ({ex.Message})");
        }

        sb.AppendLine();
    }

    private static void AppendFirmwareAttributes(StringBuilder sb)
    {
        sb.AppendLine("--- firmware-attributes ---");

        const string fwAttrBase = "/sys/class/firmware-attributes";

        if (!Directory.Exists(fwAttrBase))
        {
            sb.AppendLine("  /sys/class/firmware-attributes: not present");
            sb.AppendLine();
            return;
        }

        try
        {
            foreach (var deviceDir in Directory.GetDirectories(fwAttrBase))
            {
                var deviceName = Path.GetFileName(deviceDir);
                sb.AppendLine($"  {deviceName}:");

                var attrsDir = Path.Combine(deviceDir, "attributes");
                if (!Directory.Exists(attrsDir))
                    continue;

                foreach (var attrDir in Directory.GetDirectories(attrsDir))
                {
                    var attrName = Path.GetFileName(attrDir);
                    var currentValue = Platform.Linux.SysfsHelper.ReadAttribute(
                        Path.Combine(attrDir, "current_value"));

                    if (currentValue != null)
                        sb.AppendLine($"    {attrName} = {currentValue}");
                    else
                        sb.AppendLine($"    {attrName} (no current_value)");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error: {ex.Message})");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// ROG Ally controller diagnostic block. Only emitted when AppConfig.IsAlly()
    /// returns true (i.e. board name contains "RC7"). Lists saved controller
    /// state, binding count, and the LinuxAmdGpuMetrics snapshot used by the
    /// auto-mode timer.
    /// </summary>
    private static void AppendAllyState(StringBuilder sb)
    {
        if (!AppConfig.IsAlly())
            return;

        sb.AppendLine("--- ROG Ally Controller ---");

        // Saved mode (controller_mode, default Auto=0).
        int rawMode = AppConfig.Get("controller_mode", 0);
        sb.AppendLine($"  controller_mode    : {rawMode} ({(Ally.ControllerMode)rawMode})");
        sb.AppendLine($"  controller_disabled: {AppConfig.Is("controller_disabled")}");
        sb.AppendLine($"  ally_show_tray     : {AppConfig.Is("ally_show_tray")}");

        // Persisted bindings - count non-empty `bind_*` keys (rough proxy for
        // "user has customized something" without dumping the whole catalog).
        int bindingCount = 0;
        foreach (var key in new[]
        {
            "m1","m2","a","b","x","y",
            "du","dd","dl","dr",
            "lt","rt","lb","rb",
            "ll","rs","vb","mb",
        })
        {
            if (!string.IsNullOrEmpty(AppConfig.GetString("bind_" + key, "")))
                bindingCount++;
            if (!string.IsNullOrEmpty(AppConfig.GetString("bind2_" + key, "")))
                bindingCount++;
        }
        sb.AppendLine($"  custom bindings    : {bindingCount}");

        // iGPU metrics snapshot - what the auto-mode timer sees.
        int? busy = Gpu.AMD.LinuxAmdGpuMetrics.GetIgpuBusyPercent();
        float? power = Gpu.AMD.LinuxAmdGpuMetrics.GetIgpuPowerWatts();
        int? temp = Gpu.AMD.LinuxAmdGpuMetrics.GetIgpuTempCelsius();
        sb.AppendLine($"  iGPU available     : {Gpu.AMD.LinuxAmdGpuMetrics.IsAvailable}");
        sb.AppendLine($"  iGPU busy %        : {(busy.HasValue ? busy.ToString() : "(n/a)")}");
        sb.AppendLine($"  iGPU power W       : {(power.HasValue ? power.Value.ToString("0.0") : "(n/a)")}");
        sb.AppendLine($"  iGPU temp °C       : {(temp.HasValue ? temp.ToString() : "(n/a)")}");

        sb.AppendLine();
    }

    private static void AppendXgmState(StringBuilder sb)
    {
        // Skip the entire block when there's no sign of an XG Mobile dock
        // (no laptop receptacle reporting connected, no USB-HID dock).
        var connectedPath = Platform.Linux.SysfsHelper.ResolveAttrPath(
            Platform.Linux.AsusAttributes.EgpuConnected);
        string? connectedRaw = connectedPath != null
            ? Platform.Linux.SysfsHelper.ReadAttribute(connectedPath)?.Trim()
            : null;
        bool hidPresent = false;
        string? hidPath = null;
        try
        {
            hidPresent = USB.XGM.IsConnected();
            hidPath = USB.XGM.GetDevicePath();
        }
        catch { }

        if (connectedRaw != "1" && !hidPresent)
            return;

        sb.AppendLine("--- XG Mobile Dock ---");

        // Sysfs side (laptop receptacle)
        sb.AppendLine($"  egpu_connected     : {connectedRaw ?? "(n/a)"} (path={connectedPath ?? "(none)"})");

        var enablePath = Platform.Linux.SysfsHelper.ResolveAttrPath(
            Platform.Linux.AsusAttributes.EgpuEnable);
        string? enabledRaw = enablePath != null
            ? Platform.Linux.SysfsHelper.ReadAttribute(enablePath)?.Trim()
            : null;
        sb.AppendLine($"  egpu_enable        : {enabledRaw ?? "(n/a)"} (path={enablePath ?? "(none)"})");

        // HID side (dock USB)
        sb.AppendLine($"  HID dock present   : {hidPresent}");
        if (hidPresent)
            sb.AppendLine($"  HID dock path      : {hidPath}");

        // Persisted dock prefs
        sb.AppendLine($"  xmg_light          : {AppConfig.Get("xmg_light", 1)}");
        sb.AppendLine($"  xmg_brightness     : {AppConfig.Get("xmg_brightness", 3)}");
        sb.AppendLine($"  xgm_special (6850M): {AppConfig.Is("xgm_special")}");

        // Per-mode dock fan curves (silent / balanced / turbo). Read the
        // raw config strings directly so we don't have to mutate the
        // current performance_mode just to query each variant.
        for (int mode = 0; mode <= 2; mode++)
        {
            string label = mode switch { 1 => "turbo", 2 => "silent", _ => "balanced" };
            string key = $"fan_xgm_{mode}";
            string? raw = AppConfig.GetString(key);
            sb.AppendLine(string.IsNullOrEmpty(raw)
                ? $"  fan_xgm[{label}]      : (default)"
                : $"  fan_xgm[{label}]      : {raw}");
        }

        sb.AppendLine();
    }

    private static void AppendInputDevices(StringBuilder sb)
    {
        sb.AppendLine("--- Input Devices (ASUS) ---");

        try
        {
            if (File.Exists("/proc/bus/input/devices"))
            {
                var content = File.ReadAllText("/proc/bus/input/devices");
                var devices = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

                foreach (var device in devices)
                {
                    if (device.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        device.Contains("0b05", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract just the Name and Handlers lines
                        foreach (var line in device.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("N:") || trimmed.StartsWith("H:"))
                                sb.AppendLine($"  {trimmed}");
                        }
                        sb.AppendLine();
                    }
                }
            }
        }
        catch { }

        sb.AppendLine();
    }

    private static void AppendLedSysfs(StringBuilder sb)
    {
        // /sys/class/leds/asus::* - report each ASUS LED's perms, current
        // brightness and max_brightness.
        sb.AppendLine("--- LEDs (asus::*) ---");

        const string ledRoot = "/sys/class/leds";
        if (!Directory.Exists(ledRoot))
        {
            sb.AppendLine("  (no /sys/class/leds present)");
            sb.AppendLine();
            return;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(ledRoot, "asus::*");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (enumeration failed: {ex.Message})");
            sb.AppendLine();
            return;
        }

        if (dirs.Length == 0)
        {
            sb.AppendLine("  (no asus::* LEDs found)");
            sb.AppendLine();
            return;
        }

        Array.Sort(dirs, StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string brightnessPath = Path.Combine(dir, "brightness");
            string maxPath = Path.Combine(dir, "max_brightness");

            string brightness = Platform.Linux.SysfsHelper.ReadAttribute(brightnessPath)?.Trim() ?? "(n/a)";
            string max = Platform.Linux.SysfsHelper.ReadAttribute(maxPath)?.Trim() ?? "(n/a)";
            string mode = "(n/a)";
            try
            {
                if (File.Exists(brightnessPath))
                {
#pragma warning disable CA1416
                    mode = "0" + Convert.ToString((int)File.GetUnixFileMode(brightnessPath) & 0x1FF, 8).PadLeft(3, '0');
#pragma warning restore CA1416
                }
            }
            catch { }

            sb.AppendLine($"  {name,-28} brightness={brightness}/{max} mode={mode}");
        }

        sb.AppendLine();
    }

    private static void AppendInstallState(StringBuilder sb)
    {
        sb.AppendLine("--- Install State ---");

        var udevExists = File.Exists("/etc/udev/rules.d/90-ghelper.rules");
        sb.AppendLine($"  udev rules: {(udevExists ? "installed" : "NOT FOUND")}");

        if (udevExists)
        {
            var udevVersion = "unknown";
            try
            {
                // Read first 5 lines to find version comment
                foreach (var line in File.ReadLines("/etc/udev/rules.d/90-ghelper.rules").Take(5))
                {
                    if (line.StartsWith("# Version:"))
                    {
                        var ver = line.Substring("# Version:".Length).Trim();
                        udevVersion = string.IsNullOrEmpty(ver) || ver == "VERSION_PLACEHOLDER" ? "dev" : ver;
                        break;
                    }
                }
            }
            catch { }
            sb.AppendLine($"  udev rules version: {udevVersion}");

            // Compare with app version if both are real versions (not "dev"/"unknown")
            if (udevVersion != "dev" && udevVersion != "unknown")
            {
                var appVer = AppConfig.AppVersion;
                if (appVer != udevVersion)
                    sb.AppendLine($"  \u26a0 udev rules version mismatch (app: {appVer}, rules: {udevVersion})");
            }
        }

        var tmpfilesExists = File.Exists("/etc/tmpfiles.d/90-ghelper.conf");
        sb.AppendLine($"  tmpfiles.d: {(tmpfilesExists ? "installed" : "NOT FOUND")}");

        var symlinkTarget = Platform.Linux.SysfsHelper.RunCommand("readlink", "-f /usr/local/bin/ghelper");
        sb.AppendLine($"  /usr/local/bin/ghelper: {symlinkTarget ?? "NOT FOUND"}");

        var optExists = File.Exists("/opt/ghelper/ghelper");
        sb.AppendLine($"  /opt/ghelper/ghelper: {(optExists ? "installed" : "NOT FOUND")}");

        sb.AppendLine("  managed files:");
        try
        {
            foreach (var r in Install.Installer.ComputeStatus())
                sb.AppendLine($"    {r.File.Id,-17} {Install.Installer.StateLabel(r.State),-13} {r.File.Dest}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"    (status unavailable: {ex.Message})");
        }

        sb.AppendLine();
    }

    private static void AppendBootServiceLog(StringBuilder sb)
    {
        sb.AppendLine("--- Boot Service Log (ghelper-gpu-boot) ---");
        var output = Platform.Linux.SysfsHelper.RunCommand("journalctl",
            "-t ghelper-gpu-boot --no-pager -n 200");
        if (string.IsNullOrWhiteSpace(output))
            sb.AppendLine("  (no entries or journalctl not available)");
        else
            sb.AppendLine(output);
        sb.AppendLine();

        sb.AppendLine("--- GPU Helper Log (gpu-helper) ---");
        var helperLog = Platform.Linux.SysfsHelper.RunCommand("journalctl",
            "-t gpu-helper --no-pager -n 100");
        if (string.IsNullOrWhiteSpace(helperLog))
            sb.AppendLine("  (no entries - journalctl unreadable without privilege, or helper not yet invoked)");
        else
            sb.AppendLine(helperLog);
        sb.AppendLine();

        sb.AppendLine("--- RyzenAdj Log (ryzenadj) ---");
        var ryzenLog = Platform.Linux.SysfsHelper.RunCommand("journalctl",
            "-t ryzenadj --no-pager -n 100");
        if (string.IsNullOrWhiteSpace(ryzenLog))
            sb.AppendLine("  (no entries - not a Ryzen APU, or ryzenadj not yet invoked)");
        else
            sb.AppendLine(ryzenLog);
        sb.AppendLine();

        sb.AppendLine("--- Boot Service State (/etc/ghelper/*) ---");
        string[] stateFiles =
        {
            "/etc/ghelper/pending-gpu-mode",
            "/etc/ghelper/persistent-gpu-mode",
            "/etc/ghelper/eco-retry-count",
            "/etc/ghelper/last-eco-failed",
            "/etc/ghelper/last-recovery",
        };
        foreach (var path in stateFiles)
        {
            var name = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                sb.AppendLine($"  {name}: (not present)");
                continue;
            }
            try
            {
                var content = File.ReadAllText(path).Trim();
                if (string.IsNullOrEmpty(content))
                    content = "(empty)";
                sb.AppendLine($"  {name}: {content}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {name}: (read error: {ex.Message})");
            }
        }
        sb.AppendLine();
    }

    private static void AppendRecentLog(StringBuilder sb)
    {
        var lines = Logger.GetRecentLines();
        var total = Logger.TotalLines;

        sb.AppendLine($"--- Recent Log ({lines.Length} of {total} total) ---");

        foreach (var line in lines)
            sb.AppendLine(line);

        sb.AppendLine();
    }

    private static void AppendPowerSource(StringBuilder sb)
    {
        sb.AppendLine("--- Power Source ---");

        const string psBase = "/sys/class/power_supply";

        bool foundAdapter = false;
        bool foundBattery = false;

        if (Directory.Exists(psBase))
        {
            try
            {
                foreach (var devPath in Directory.GetDirectories(psBase).OrderBy(p => p))
                {
                    var type = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devPath, "type"));
                    var name = Path.GetFileName(devPath);

                    if (type == null)
                        continue;

                    if (type.Equals("Mains", StringComparison.OrdinalIgnoreCase))
                    {
                        foundAdapter = true;
                        var onlineRaw = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devPath, "online"));
                        var online = onlineRaw == "1" ? "connected" : "disconnected";

                        // power_now is in μW on most kernels; some platforms expose
                        // it sporadically. Best-effort only.
                        var powerNow = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "power_now"), -1);
                        string watts = powerNow > 0 ? $" ({powerNow / 1_000_000} W)" : "";

                        sb.AppendLine($"  AC adapter ({name}): {online}{watts}");
                    }
                    else if (type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
                    {
                        foundBattery = true;
                        var status = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devPath, "status")) ?? "?";
                        var capacityRaw = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(devPath, "capacity"));
                        string capacity = capacityRaw != null ? $"{capacityRaw}%" : "?";

                        // Some batteries report charge_*, others energy_*. Read whichever exists.
                        long now = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "charge_now"), -1);
                        long full = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "charge_full"), -1);
                        long design = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "charge_full_design"), -1);
                        string unit = "mAh";

                        if (now < 0 && full < 0)
                        {
                            now = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "energy_now"), -1);
                            full = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "energy_full"), -1);
                            design = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(devPath, "energy_full_design"), -1);
                            unit = "mWh";
                        }

                        // Sysfs values are μA/μW; convert to mA/mW for readability.
                        string body;
                        if (now >= 0 && full > 0)
                        {
                            long nowM = now / 1000;
                            long fullM = full / 1000;
                            int health = design > 0 ? (int)(full * 100 / design) : -1;
                            string healthPart = design > 0
                                ? $", design {design / 1000} {unit}, health {health}%"
                                : "";
                            body = $" {capacity}, {nowM}/{fullM} {unit}{healthPart}";
                        }
                        else
                        {
                            body = $" {capacity}";
                        }

                        sb.AppendLine($"  Battery ({name}): {status},{body}");

                        var chargeLimit = Platform.Linux.SysfsHelper.ReadAttribute(
                            Path.Combine(devPath, "charge_control_end_threshold"));
                        if (chargeLimit != null)
                            sb.AppendLine($"    charge_control_end_threshold: {chargeLimit}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (power_supply scan failed: {ex.Message})");
            }
        }

        if (!foundAdapter)
            sb.AppendLine("  AC adapter: (no Mains supply found)");
        if (!foundBattery)
            sb.AppendLine("  Battery: (no Battery supply found)");

        // Profile + throttle + boost + ASPM aggregate
        var profile = Platform.Linux.SysfsHelper.ReadAttribute("/sys/firmware/acpi/platform_profile");
        if (profile != null)
            sb.AppendLine($"  Power profile: {profile}");

        var throttlePath = Platform.Linux.SysfsHelper.ResolveAttrPath(
            Platform.Linux.AsusAttributes.ThrottleThermalPolicy,
            Platform.Linux.SysfsHelper.AsusWmiPlatform,
            Platform.Linux.SysfsHelper.AsusBusPlatform);
        if (throttlePath != null)
        {
            var throttleRaw = Platform.Linux.SysfsHelper.ReadAttribute(throttlePath);
            string label = throttleRaw switch
            {
                "0" => "Balanced",
                "1" => "Turbo",
                "2" => "Silent",
                _ => "?",
            };
            sb.AppendLine($"  Throttle policy: {throttleRaw} ({label})");
        }

        var noTurbo = Platform.Linux.SysfsHelper.ReadAttribute("/sys/devices/system/cpu/intel_pstate/no_turbo");
        var cpufreqBoost = Platform.Linux.SysfsHelper.ReadAttribute("/sys/devices/system/cpu/cpufreq/boost");
        if (noTurbo != null)
            sb.AppendLine($"  CPU boost (intel_pstate.no_turbo): {(noTurbo == "0" ? "on" : "off")}");
        if (cpufreqBoost != null)
            sb.AppendLine($"  CPU boost (cpufreq.boost): {(cpufreqBoost == "1" ? "on" : "off")}");

        var aspm = Platform.Linux.SysfsHelper.ReadAttribute("/sys/module/pcie_aspm/parameters/policy");
        if (aspm != null)
            sb.AppendLine($"  ASPM policy: {aspm}");

        // NVMe power state visibility when ASPM powersave is active.
        // D3cold + powersave ASPM is a known cause of NVMe resume failures / kernel panics.
        bool aspmDeep = aspm != null && (aspm.Contains("[powersave]") || aspm.Contains("[powersupersave]"));
        const string nvmeClass = "/sys/class/nvme";
        if (Directory.Exists(nvmeClass))
        {
            foreach (var nvmeDir in Directory.GetDirectories(nvmeClass))
            {
                var state = Platform.Linux.SysfsHelper.ReadAttribute(
                    Path.Combine(nvmeDir, "device", "power_state"));
                var d3cold = Platform.Linux.SysfsHelper.ReadAttribute(
                    Path.Combine(nvmeDir, "device", "d3cold_allowed"));
                var nvmeName = Path.GetFileName(nvmeDir);
                sb.AppendLine($"  {nvmeName}: power_state={state ?? "?"} d3cold_allowed={d3cold ?? "?"}");
            }

            if (aspmDeep)
            {
                sb.AppendLine("  NOTE: ASPM powersave + NVMe d3cold can cause resume failures.");
                sb.AppendLine("        If you experience freezes/panics on power mode changes,");
                sb.AppendLine("        add nvme_core.default_ps_max_latency_us=0 to boot params.");
            }
        }

        sb.AppendLine();
    }

    // CPU summary: model, microcode, online cores, governor, freq range,
    // current temp. Sourced entirely from /proc and /sys to avoid shell-outs.
    private static void AppendCpu(StringBuilder sb)
    {
        sb.AppendLine("--- CPU ---");

        // Parse /proc/cpuinfo first record (defensive: some fields absent on
        // ARM / Snapdragon X1 / virtualized systems).
        string? model = null, family = null, modelNum = null, stepping = null, microcode = null;
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        break; // first record only

                    int colon = line.IndexOf(':');
                    if (colon < 0)
                        continue;
                    var key = line[..colon].Trim();
                    var val = line[(colon + 1)..].Trim();

                    switch (key)
                    {
                        case "model name":
                            model ??= val;
                            break;
                        case "Model":
                            model ??= val;
                            break;     // ARM
                        case "cpu family":
                            family ??= val;
                            break;
                        case "model":
                            modelNum ??= val;
                            break;
                        case "stepping":
                            stepping ??= val;
                            break;
                        case "microcode":
                            microcode ??= val;
                            break;
                        case "CPU implementer":
                            family ??= val;
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (/proc/cpuinfo read failed: {ex.Message})");
        }

        if (model != null)
        {
            var parts = new List<string>();
            if (family != null)
                parts.Add($"family {family}");
            if (modelNum != null)
            {
                string modelHex = int.TryParse(modelNum, out int m) ? m.ToString("X") : modelNum;
                parts.Add($"model 0x{modelHex}");
            }
            if (stepping != null)
                parts.Add($"stepping {stepping}");
            sb.AppendLine(parts.Count > 0
                ? $"  Model: {model} ({string.Join(", ", parts)})"
                : $"  Model: {model}");
        }
        else
        {
            sb.AppendLine("  Model: (not detected)");
        }

        // Microcode: /sys path is usually 0o400 (root-only); /proc/cpuinfo is
        // world-readable. Prefer sysfs if accessible, fall back to cpuinfo.
        var microcodeSysfs = Platform.Linux.SysfsHelper.ReadAttribute("/sys/devices/system/cpu/cpu0/microcode/version");
        if (microcodeSysfs != null)
            sb.AppendLine($"  Microcode: {microcodeSysfs}");
        else if (microcode != null)
            sb.AppendLine($"  Microcode: {microcode}");

        // Online vs total cores
        int online = 0, total = 0;
        try
        {
            foreach (var dir in Directory.GetDirectories("/sys/devices/system/cpu", "cpu*"))
            {
                var name = Path.GetFileName(dir);
                // Match only "cpuN" where N is digits
                if (name.Length <= 3 || !name.StartsWith("cpu"))
                    continue;
                bool numeric = name[3..].All(char.IsDigit);
                if (!numeric)
                    continue;

                total++;
                var onlineFile = Path.Combine(dir, "online");
                // cpu0 typically lacks the online file (always on); count it.
                if (!File.Exists(onlineFile))
                {
                    online++;
                    continue;
                }
                if (Platform.Linux.SysfsHelper.ReadAttribute(onlineFile) == "1")
                    online++;
            }
        }
        catch { /* fall through */ }

        if (total > 0)
        {
            // P/E split via core_type (Intel hybrid only; ARM big.LITTLE doesn't
            // populate this in mainline yet).
            int p = 0, e = 0;
            try
            {
                for (int i = 0; i < total; i++)
                {
                    var ct = Platform.Linux.SysfsHelper.ReadAttribute($"/sys/devices/system/cpu/cpu{i}/topology/core_type");
                    if (ct == null)
                        continue;
                    if (ct.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        p++;
                    else if (ct.Contains("Atom", StringComparison.OrdinalIgnoreCase))
                        e++;
                }
            }
            catch { /* fall through */ }

            string topology = (p > 0 || e > 0) ? $" ({p}P + {e}E)" : "";
            sb.AppendLine($"  Cores: {online} online / {total} total{topology}");
        }

        var governor = Platform.Linux.SysfsHelper.ReadAttribute("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor");
        var pstateStatus = Platform.Linux.SysfsHelper.ReadAttribute("/sys/devices/system/cpu/intel_pstate/status");
        if (governor != null || pstateStatus != null)
        {
            string drv = pstateStatus != null ? $" (intel_pstate {pstateStatus})" : "";
            sb.AppendLine($"  Governor: {governor ?? "?"}{drv}");
        }

        long minF = Platform.Linux.SysfsHelper.ReadInt("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_min_freq", -1);
        long maxF = Platform.Linux.SysfsHelper.ReadInt("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", -1);
        long curF = Platform.Linux.SysfsHelper.ReadInt("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", -1);
        if (minF > 0 && maxF > 0)
        {
            string cur = curF > 0 ? $", current {curF / 1000} MHz (cpu0)" : "";
            sb.AppendLine($"  Freq: {minF / 1000} - {maxF / 1000} MHz{cur}");
        }

        // CPU temp from coretemp (Intel) or k10temp (AMD). temp1_input is in
        // millidegrees C. The same hwmon dir LinuxAsusWmi resolves at init.
        var cpuTempHwmon = Platform.Linux.SysfsHelper.FindHwmonByName("coretemp")
                        ?? Platform.Linux.SysfsHelper.FindHwmonByName("k10temp");
        if (cpuTempHwmon != null)
        {
            int milliC = Platform.Linux.SysfsHelper.ReadInt(Path.Combine(cpuTempHwmon, "temp1_input"), -1);
            if (milliC > 0)
                sb.AppendLine($"  Temp: {milliC / 1000} °C ({Path.GetFileName(cpuTempHwmon)}/temp1)");
        }

        sb.AppendLine();
    }

    // App Config snapshot: curated whitelist of keys ghelper consults to make
    // decisions. Excludes anything fingerprint-able (language, layout, paths).
    private static void AppendAppConfig(StringBuilder sb)
    {
        sb.AppendLine("--- App Config ---");

        // performance_mode → label
        int perfMode = AppConfig.Get("performance_mode", -1);
        string perfLabel = perfMode switch
        {
            0 => "Balanced",
            1 => "Turbo",
            2 => "Silent",
            _ => "(not set)",
        };
        sb.AppendLine($"  performance_mode: {(perfMode < 0 ? "-1" : perfMode.ToString())} ({perfLabel})");

        int chargeLimit = AppConfig.Get("charge_limit", -1);
        sb.AppendLine($"  charge_limit: {(chargeLimit < 0 ? "(not set)" : chargeLimit + "%")}");

        sb.AppendLine($"  gpu_mode: {AppConfig.GetString("gpu_mode", "(not set)")}");
        sb.AppendLine($"  gpu_backend: {AppConfig.GetString("gpu_backend", "(not set)")}");
        sb.AppendLine($"  gpu_auto: {YesNo(AppConfig.Is("gpu_auto"))}");
        sb.AppendLine($"  gpu_optimized_enabled: {YesNo(AppConfig.Is("gpu_optimized_enabled"))}");
        sb.AppendLine($"  raw_wmi: {YesNo(AppConfig.Is("raw_wmi"))}");
        sb.AppendLine($"  screen_auto: {YesNo(AppConfig.Is("screen_auto"))}");
        sb.AppendLine($"  auto_apply_power: {YesNo(AppConfig.IsMode("auto_apply_power"))}");

        // Per-mode fan/GPU auto-apply flags and saved fan curves.
        for (int m = 0; m < 3; m++)
        {
            string mName = m switch { 0 => "Balanced", 1 => "Turbo", 2 => "Silent", _ => "?" };
            bool fans = AppConfig.Get($"auto_apply_fans_{m}", 0) == 1;
            bool gpu = AppConfig.Get($"auto_apply_gpu_{m}", 0) == 1;
            string cpuCurve = AppConfig.GetString($"fan_profile_cpu_{m}") ?? "-";
            string gpuCurve = AppConfig.GetString($"fan_profile_gpu_{m}") ?? "-";
            string midCurve = AppConfig.GetString($"fan_profile_mid_{m}") ?? "-";
            sb.AppendLine($"  mode {m} ({mName}): auto_fans={YesNo(fans)} auto_gpu={YesNo(gpu)}");
            sb.AppendLine($"    fan_cpu: {cpuCurve}");
            sb.AppendLine($"    fan_gpu: {gpuCurve}");
            if (midCurve != "-")
                sb.AppendLine($"    fan_mid: {midCurve}");
        }

        // Current pwm_enable state from asus_custom_fan_curve hwmon.
        var fanHwmon = Platform.Linux.SysfsHelper.FindHwmonByName("asus_custom_fan_curve");
        if (fanHwmon != null)
        {
            for (int p = 1; p <= 3; p++)
            {
                string epath = Path.Combine(fanHwmon, $"pwm{p}_enable");
                if (!File.Exists(epath))
                    break;
                int val = Platform.Linux.SysfsHelper.ReadInt(epath, -1);
                string label = val switch { 1 => "custom", 2 => "firmware", 3 => "factory-reset", _ => "?" };
                sb.AppendLine($"  pwm{p}_enable: {val} ({label})");
            }
        }

        sb.AppendLine($"  fan_manual: {YesNo(AppConfig.Is("fan_manual"))} (running: {YesNo(Fan.ManualFanService.Running)})");

        // optimal_brightness: -1 = unset, 0 = Off, 1 = On Always, 2 = On Battery
        int oab = AppConfig.Get("optimal_brightness", -1);
        string oabLabel = oab switch
        {
            0 => "Off",
            1 => "On Always",
            2 => "On Battery only",
            _ => "not configured",
        };
        string oabValue = oab < 0 ? "-1" : oab.ToString();
        sb.AppendLine($"  optimal_brightness: {oabValue} ({oabLabel})");

        sb.AppendLine($"  topmost: {YesNo(AppConfig.Is("topmost"))}");
        sb.AppendLine($"  silent_start: {YesNo(AppConfig.Is("silent_start"))}");
        sb.AppendLine($"  bw_icon: {YesNo(AppConfig.IsBWIcon())}");
        sb.AppendLine($"  toggle_clamshell_mode: {YesNo(AppConfig.Is("toggle_clamshell_mode"))}");
        sb.AppendLine($"  autostart: {YesNo(AppConfig.IsNotFalse("autostart"))}");

        sb.AppendLine();
    }

    // Display connectors via DRM sysfs (compositor-agnostic) plus a coarse
    // identification of the active compositor + ghelper's chosen refresh
    // backend. Current refresh rate is best-effort via the backend.
    private static void AppendDisplays(StringBuilder sb)
    {
        sb.AppendLine("--- Displays ---");

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?";
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?";
        sb.AppendLine($"  Session: {session} ({desktop})");

        // Which backend ghelper uses for refresh control. We probe binary
        // presence; the active app instance may have already picked one
        // but Diagnostics is static and may run pre-init.
        var backends = new List<string>();
        if (!string.IsNullOrEmpty(Platform.Linux.SysfsHelper.RunCommand("which", "kscreen-doctor")))
            backends.Add("kscreen-doctor");
        if (!string.IsNullOrEmpty(Platform.Linux.SysfsHelper.RunCommand("which", "gdctl")))
            backends.Add("gdctl");
        if (!string.IsNullOrEmpty(Platform.Linux.SysfsHelper.RunCommand("which", "wlr-randr")))
            backends.Add("wlr-randr");
        if (!string.IsNullOrEmpty(Platform.Linux.SysfsHelper.RunCommand("which", "xrandr")))
            backends.Add("xrandr");
        sb.AppendLine($"  Refresh backends available: {(backends.Count == 0 ? "(none)" : string.Join(", ", backends))}");

        // DRM connectors: /sys/class/drm/card*-* directories. Each one is a
        // physical connector (eDP, HDMI-A, DP, etc.). status + enabled +
        // first mode line gives a useful snapshot without any compositor.
        const string drmBase = "/sys/class/drm";
        bool anyConnector = false;
        if (Directory.Exists(drmBase))
        {
            try
            {
                sb.AppendLine("  Connectors:");
                foreach (var dir in Directory.GetDirectories(drmBase).OrderBy(p => p))
                {
                    var name = Path.GetFileName(dir);
                    // Skip card%d (the device); we want card%d-CONNECTOR (the outputs).
                    if (!name.Contains('-'))
                        continue;

                    var status = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(dir, "status"));
                    if (status == null)
                        continue;

                    anyConnector = true;
                    var enabled = Platform.Linux.SysfsHelper.ReadAttribute(Path.Combine(dir, "enabled"));
                    string mode = "";
                    if (status == "connected")
                    {
                        try
                        {
                            var modesPath = Path.Combine(dir, "modes");
                            if (File.Exists(modesPath))
                            {
                                var firstMode = File.ReadLines(modesPath).FirstOrDefault();
                                if (!string.IsNullOrWhiteSpace(firstMode))
                                    mode = $", preferred={firstMode.Trim()}";
                            }
                        }
                        catch { }
                    }
                    sb.AppendLine($"    {name}: {status}, {enabled ?? "?"}{mode}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (DRM connector scan failed: {ex.Message})");
            }
        }

        if (!anyConnector)
            sb.AppendLine("  Connectors: (none enumerated)");

        // Ghelper's current refresh-rate state
        bool screenAuto = AppConfig.Is("screen_auto");
        sb.AppendLine($"  Auto refresh on AC/battery: {(screenAuto ? "enabled" : "disabled")}");

        sb.AppendLine();
    }

    // ghelper-specific systemd integration: the boot service (PCI mode +
    // pending mode application) plus the autostart .desktop file.
    private static void AppendGhelperUnits(StringBuilder sb)
    {
        sb.AppendLine("--- ghelper systemd units ---");

        bool hasSystemctl = !string.IsNullOrEmpty(
            Platform.Linux.SysfsHelper.RunCommand("which", "systemctl"));

        const string bootUnit = "ghelper-gpu-boot.service";

        if (hasSystemctl)
        {
            // Probe presence first; systemctl returns empty stdout when the
            // unit isn't installed at all.
            var enabledRaw = Platform.Linux.SysfsHelper.RunCommand("systemctl", $"is-enabled {bootUnit}");
            string enabled = string.IsNullOrWhiteSpace(enabledRaw) ? "not-installed" : enabledRaw.Trim();

            if (enabled == "not-installed")
            {
                sb.AppendLine($"  {bootUnit}: not installed");
            }
            else
            {
                // ActiveState comes from `systemctl show` instead of `is-active` because
                // `is-active` returns non-zero exit for "inactive" oneshot services that
                // ran successfully, which our RunCommand swallows as a failure.
                var show = Platform.Linux.SysfsHelper.RunCommand("systemctl",
                    $"show {bootUnit} --property=ActiveState,SubState,ExecMainStartTimestamp,ExecMainExitTimestamp,ExecMainStatus,NRestarts");
                string active = "?", subState = "?", lastStart = "(never)", lastExit = "(never)", exitCode = "?", restarts = "?";
                if (show != null)
                {
                    foreach (var line in show.Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0)
                            continue;
                        var k = line[..eq];
                        var v = line[(eq + 1)..].Trim();
                        switch (k)
                        {
                            case "ActiveState":
                                if (!string.IsNullOrEmpty(v))
                                    active = v;
                                break;
                            case "SubState":
                                if (!string.IsNullOrEmpty(v))
                                    subState = v;
                                break;
                            case "ExecMainStartTimestamp":
                                if (!string.IsNullOrEmpty(v))
                                    lastStart = v;
                                break;
                            case "ExecMainExitTimestamp":
                                if (!string.IsNullOrEmpty(v))
                                    lastExit = v;
                                break;
                            case "ExecMainStatus":
                                if (!string.IsNullOrEmpty(v))
                                    exitCode = v;
                                break;
                            case "NRestarts":
                                if (!string.IsNullOrEmpty(v))
                                    restarts = v;
                                break;
                        }
                    }
                }

                sb.AppendLine($"  {bootUnit}: enabled={enabled}, active={active} ({subState})");
                sb.AppendLine($"    last start: {lastStart}");
                sb.AppendLine($"    last exit:  {lastExit} (status {exitCode}, restarts {restarts})");
            }
        }
        else
        {
            // Fallback: probe symlink presence in the standard wants/ dir.
            bool symLinked =
                File.Exists($"/etc/systemd/system/multi-user.target.wants/{bootUnit}")
                || File.Exists($"/etc/systemd/system/default.target.wants/{bootUnit}");
            sb.AppendLine($"  {bootUnit}: enabled={(symLinked ? "yes (via symlink)" : "no")} (systemctl not on PATH; status unavailable)");
        }

        // Autostart .desktop
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/home";
        var autostart = Path.Combine(home, ".config/autostart/ghelper.desktop");
        if (File.Exists(autostart))
        {
            string? exec = null;
            try
            {
                foreach (var line in File.ReadLines(autostart))
                {
                    if (line.StartsWith("Exec=", StringComparison.Ordinal))
                    {
                        exec = line[5..].Trim();
                        break;
                    }
                }
            }
            catch { }
            sb.AppendLine($"  Desktop autostart: present (exec={exec ?? "?"})");
        }
        else
        {
            sb.AppendLine("  Desktop autostart: (file not present)");
        }

        sb.AppendLine();
    }

    // Suspend / resume context for this boot. Three bounded journalctl
    // queries plus a couple of file reads. Each line degrades silently
    // when the underlying source isn't available.
    private static void AppendSuspendResume(StringBuilder sb)
    {
        sb.AppendLine("--- Suspend / Resume ---");

        // Boot uptime
        try
        {
            if (File.Exists("/proc/uptime"))
            {
                var raw = File.ReadAllText("/proc/uptime").Trim();
                var first = raw.Split(' ').FirstOrDefault();
                if (double.TryParse(first, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double secs))
                {
                    long total = (long)secs;
                    long h = total / 3600;
                    long m = (total % 3600) / 60;
                    long s = total % 60;
                    sb.AppendLine($"  Uptime: {h}h {m}m {s}s");
                }
            }
        }
        catch { }

        // Last suspend entry / exit lines
        var lastEntry = Platform.Linux.SysfsHelper.RunCommandWithTimeout("bash",
            "-c \"journalctl -b 0 -k --no-pager 2>/dev/null | grep -E 'PM: suspend entry' | tail -1\"",
            3000);
        if (!string.IsNullOrWhiteSpace(lastEntry))
            sb.AppendLine($"  Last suspend entry: {lastEntry.Trim()}");
        else
            sb.AppendLine("  Last suspend entry: (none this boot)");

        var lastExit = Platform.Linux.SysfsHelper.RunCommandWithTimeout("bash",
            "-c \"journalctl -b 0 -k --no-pager 2>/dev/null | grep -E 'PM: suspend exit' | tail -1\"",
            3000);
        if (!string.IsNullOrWhiteSpace(lastExit))
            sb.AppendLine($"  Last suspend exit:  {lastExit.Trim()}");
        else
            sb.AppendLine("  Last suspend exit:  (none this boot)");

        // Suspend / resume errors count. Use `wc -l` instead of `grep -c`:
        // grep -c exits 1 when there are zero matches AND emits "0", so
        // chaining `|| echo 0` gives a double-zero. wc -l always exits 0
        // and emits a clean count.
        var errors = Platform.Linux.SysfsHelper.RunCommandWithTimeout("bash",
            "-c \"journalctl -b 0 -k -p err --no-pager 2>/dev/null | grep -E 'suspend|resume' | wc -l\"",
            3000);
        if (!string.IsNullOrWhiteSpace(errors))
            sb.AppendLine($"  Suspend/resume errors this boot: {errors.Trim()}");

        // systemd-logind state
        var logind = Platform.Linux.SysfsHelper.RunCommand("systemctl", "is-active systemd-logind");
        if (!string.IsNullOrWhiteSpace(logind))
            sb.AppendLine($"  systemd-logind: {logind.Trim()}");

        // Lid state (when /proc/acpi/button/lid/*/state exists). File content
        // is in "state:      open" format with arbitrary whitespace; normalise.
        const string lidBase = "/proc/acpi/button/lid";
        if (Directory.Exists(lidBase))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(lidBase))
                {
                    var statePath = Path.Combine(dir, "state");
                    if (!File.Exists(statePath))
                        continue;
                    var content = Platform.Linux.SysfsHelper.ReadAttribute(statePath);
                    if (string.IsNullOrEmpty(content))
                        continue;

                    // "state:      open" -> "open"
                    int colon = content.IndexOf(':');
                    string lidState = colon >= 0 ? content[(colon + 1)..].Trim() : content.Trim();
                    sb.AppendLine($"  Lid ({Path.GetFileName(dir)}): {lidState}");
                }
            }
            catch { }
        }

        sb.AppendLine();
    }

    // Lowercase yes/no for boolean output in diagnostic lines. Avoids the
    // C# default "True"/"False" capitalization mismatching surrounding text.
    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string GetFilePermissions(string path)
    {
        try
        {
            var stat = Platform.Linux.SysfsHelper.RunCommand("stat", $"-c %a {path}");
            return stat ?? "???";
        }
        catch
        {
            return "???";
        }
    }
}

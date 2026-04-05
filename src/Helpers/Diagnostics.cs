using System.Text;

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

        // ── System Identity ──
        AppendSystemInfo(sb);

        // ── Model Detection Flags ──
        AppendModelFlags(sb);

        // ── Kernel Modules ──
        AppendKernelModules(sb);

        // ── Module Backend (asus-nb-wmi vs asus-armoury) ──
        AppendModuleBackend(sb);

        // ── Raw WMI (debugfs) ──
        AppendRawWmiProbe(sb);

        // ── Sysfs Permissions & Values ──
        AppendSysfsState(sb);

        // ── hwmon Devices ──
        AppendHwmon(sb);

        // ── USB HID (ASUS) ──
        AppendUsbDevices(sb);

        // ── firmware-attributes (asus_armoury) ──
        AppendFirmwareAttributes(sb);

        // ── Input Devices ──
        AppendInputDevices(sb);

        // ── udev / tmpfiles ──
        AppendInstallState(sb);

        // ── Boot service journal ──
        AppendBootServiceLog(sb);

        // ── Recent log (last, always at the end) ──
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
            ("IsSingleColor", AppConfig.IsSingleColor()),
            ("NoAura", AppConfig.NoAura()),
            ("IsAdvancedRGB", AppConfig.IsAdvancedRGB()),
            ("Is4ZoneRGB", AppConfig.Is4ZoneRGB()),
            ("IsDynamicLighting", AppConfig.IsDynamicLighting()),
            ("IsIntelHX", AppConfig.IsIntelHX()),
            ("IsCPULight", AppConfig.IsCPULight()),
            ("IsResetRequired", AppConfig.IsResetRequired()),
            ("IsFanRequired", AppConfig.IsFanRequired()),
            ("IsSleepBacklight", AppConfig.IsSleepBacklight()),
            ("IsSlash", AppConfig.IsSlash()),
        };

        foreach (var (name, value) in flags)
        {
            if (value) sb.AppendLine($"  {name}: true");
        }

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
            sb.AppendLine("  Active: NONE — no ASUS WMI module detected");

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

    private static void AppendRawWmiProbe(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("--- Raw WMI (debugfs) ---");
        sb.AppendLine(Platform.Linux.AsusWmiDebugfs.GetDiagnostics());
    }

    private static void AppendSysfsState(StringBuilder sb)
    {
        sb.AppendLine("--- Sysfs State ---");

        // Fixed paths (non-WMI attributes — always at known locations)
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

            // kbd_rgb_mode and kbd_rgb_state are DEVICE_ATTR_WO in the kernel — read always fails
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
        // Uses AsusAttributes.All as single source of truth — covers all 16 known attributes
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
                        if (File.Exists(Path.Combine(hwmonDir, "fan1_input"))) extras.Add("fan1");
                        if (File.Exists(Path.Combine(hwmonDir, "fan2_input"))) extras.Add("fan2");
                        if (File.Exists(Path.Combine(hwmonDir, "fan3_input"))) extras.Add("fan3");
                        if (File.Exists(Path.Combine(hwmonDir, "pwm1_enable"))) extras.Add("pwm1");
                        if (File.Exists(Path.Combine(hwmonDir, "pwm2_enable"))) extras.Add("pwm2");
                        if (File.Exists(Path.Combine(hwmonDir, "temp1_input"))) extras.Add("temp1");

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

                // Check if hid_asus module is loaded — needed for I2C-HID hidraw nodes
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
                if (!Directory.Exists(attrsDir)) continue;

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

        sb.AppendLine();
    }

    private static void AppendBootServiceLog(StringBuilder sb)
    {
        sb.AppendLine("--- Boot Service Log (ghelper-gpu-boot) ---");
        var output = Platform.Linux.SysfsHelper.RunCommand("journalctl",
            "-t ghelper-gpu-boot --no-pager -n 50");
        if (string.IsNullOrWhiteSpace(output))
            sb.AppendLine("  (no entries or journalctl not available)");
        else
            sb.AppendLine(output);
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

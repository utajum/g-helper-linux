using System.Diagnostics;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux system integration: DMI info, autostart, notifications, kernel checks.
/// Replaces WMI Win32_ComputerSystem/Win32_BIOS, Task Scheduler, etc.
/// </summary>
public class LinuxSystemIntegration : ISystemIntegration
{
    private readonly string _autostartDir;
    private readonly string _desktopFilePath;

    public LinuxSystemIntegration()
    {
        _autostartDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart");
        _desktopFilePath = Path.Combine(_autostartDir, "ghelper.desktop");
    }

    public string GetModelName()
    {
        return SysfsHelper.ReadAttribute(Path.Combine(SysfsHelper.DmiId, "product_name"))
            ?? "Unknown ASUS Laptop";
    }

    public string GetBiosVersion()
    {
        return SysfsHelper.ReadAttribute(Path.Combine(SysfsHelper.DmiId, "bios_version"))
            ?? "Unknown";
    }

    public string GetKernelVersion()
    {
        return SysfsHelper.RunCommand("uname", "-r") ?? "Unknown";
    }

    public Version GetKernelVersionParsed()
    {
        try
        {
            var raw = GetKernelVersion();
            // Parse "6.8.0-45-generic" → Version(6, 8, 0)
            var parts = raw.Split('-')[0].Split('.');
            if (parts.Length >= 3)
                return new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            if (parts.Length == 2)
                return new Version(int.Parse(parts[0]), int.Parse(parts[1]));
        }
        catch { }
        return new Version(0, 0);
    }

    public void SetAutostart(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(_autostartDir);
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "ghelper";
            var desktop = $"""
                [Desktop Entry]
                Type=Application
                Name=G-Helper
                Comment=ASUS Laptop Control (Linux)
                Exec={exePath}
                Icon=ghelper
                Terminal=false
                Categories=System;HardwareSettings;
                StartupNotify=false
                X-GNOME-Autostart-enabled=true
                """;
            File.WriteAllText(_desktopFilePath, desktop);
            Helpers.Logger.WriteLine($"Autostart enabled: {_desktopFilePath}");
        }
        else
        {
            if (File.Exists(_desktopFilePath))
            {
                File.Delete(_desktopFilePath);
                Helpers.Logger.WriteLine("Autostart disabled");
            }
        }
    }

    public bool IsAutostartEnabled()
    {
        return File.Exists(_desktopFilePath);
    }

    public void ShowNotification(string title, string body, string? iconName = null)
    {
        try
        {
            Helpers.Logger.WriteLine($"ShowNotification: {title} - {body}");
            
            var args = iconName != null
                ? $"-i {iconName} \"{title}\" \"{body}\""
                : $"\"{title}\" \"{body}\"";
            
            var result = SysfsHelper.RunCommand("notify-send", args);
            if (result == null)
            {
                Helpers.Logger.WriteLine("notify-send failed (returned null)");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("ShowNotification failed", ex);
        }
    }

    public bool IsAsusWmiLoaded()
    {
        // Check if asus-nb-wmi module is loaded
        var modules = SysfsHelper.RunCommand("lsmod", "");
        if (modules != null && modules.Contains("asus_nb_wmi"))
            return true;

        // Also check if sysfs path exists (module might be built-in)
        return SysfsHelper.Exists(SysfsHelper.AsusWmiPlatform);
    }

    // ── Camera Toggle ──

    /// <summary>Check if the camera (uvcvideo) module is currently loaded.</summary>
    public static bool IsCameraEnabled()
    {
        var modules = SysfsHelper.RunCommand("lsmod", "");
        return modules != null && modules.Contains("uvcvideo");
    }

    /// <summary>Toggle camera by loading/unloading the uvcvideo kernel module.
    /// Requires root — tries modprobe directly, then pkexec (graphical prompt).</summary>
    public static void SetCameraEnabled(bool enabled)
    {
        string args = enabled ? "uvcvideo" : "-r uvcvideo";

        // Try modprobe directly (works if running as root or via polkit rule)
        var result = SysfsHelper.RunCommand("modprobe", args);
        if (result != null || IsCameraEnabled() == enabled)
        {
            Helpers.Logger.WriteLine($"Camera {(enabled ? "enabled" : "disabled")} via modprobe");
            return;
        }

        // Fallback: pkexec gives a graphical password prompt
        result = SysfsHelper.RunCommand("pkexec", $"modprobe {args}");
        Helpers.Logger.WriteLine($"Camera {(enabled ? "enabled" : "disabled")}: {(result != null ? "OK (pkexec)" : "failed (needs root)")}");
    }

    // ── Touchpad Toggle ──

    /// <summary>Find the touchpad xinput device ID. Returns null if not found.
    /// Requires xinput (works on X11 and Wayland with XWayland).</summary>
    public static string? FindTouchpadId()
    {
        var fullList = SysfsHelper.RunCommand("xinput", "list");
        if (fullList == null) return null;

        foreach (var line in fullList.Split('\n'))
        {
            if (line.Contains("Touchpad", StringComparison.OrdinalIgnoreCase))
            {
                // Extract id=N from the line
                var match = System.Text.RegularExpressions.Regex.Match(line, @"id=(\d+)");
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>Check if the touchpad is currently enabled.</summary>
    public static bool? IsTouchpadEnabled()
    {
        var id = FindTouchpadId();
        if (id == null) return null; // No touchpad found

        var props = SysfsHelper.RunCommand("xinput", $"list-props {id}");
        if (props == null) return null;

        // Look for "Device Enabled" property
        foreach (var line in props.Split('\n'))
        {
            if (line.Contains("Device Enabled"))
            {
                return line.TrimEnd().EndsWith("1");
            }
        }
        return null;
    }

    /// <summary>Enable or disable the touchpad via xinput.</summary>
    public static void SetTouchpadEnabled(bool enabled)
    {
        var id = FindTouchpadId();
        if (id == null)
        {
            Helpers.Logger.WriteLine("Touchpad not found in xinput");
            return;
        }

        string action = enabled ? "enable" : "disable";
        SysfsHelper.RunCommand("xinput", $"{action} {id}");
        Helpers.Logger.WriteLine($"Touchpad {action}d (xinput id={id})");
    }

    // ── Touchscreen Toggle ──

    /// <summary>Find the touchscreen xinput device ID. Returns null if not found.</summary>
    public static string? FindTouchscreenId()
    {
        var fullList = SysfsHelper.RunCommand("xinput", "list");
        if (fullList == null) return null;

        foreach (var line in fullList.Split('\n'))
        {
            if (line.Contains("Touchscreen", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Touch Screen", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("touch", StringComparison.OrdinalIgnoreCase) &&
                 line.Contains("screen", StringComparison.OrdinalIgnoreCase)))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"id=(\d+)");
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>Check if the touchscreen is currently enabled.</summary>
    public static bool? IsTouchscreenEnabled()
    {
        var id = FindTouchscreenId();
        if (id == null) return null; // No touchscreen found

        var props = SysfsHelper.RunCommand("xinput", $"list-props {id}");
        if (props == null) return null;

        foreach (var line in props.Split('\n'))
        {
            if (line.Contains("Device Enabled"))
            {
                return line.TrimEnd().EndsWith("1");
            }
        }
        return null;
    }

    /// <summary>Enable or disable the touchscreen via xinput.</summary>
    public static void SetTouchscreenEnabled(bool enabled)
    {
        var id = FindTouchscreenId();
        if (id == null)
        {
            Helpers.Logger.WriteLine("Touchscreen not found in xinput");
            return;
        }

        string action = enabled ? "enable" : "disable";
        SysfsHelper.RunCommand("xinput", $"{action} {id}");
        Helpers.Logger.WriteLine($"Touchscreen {action}d (xinput id={id})");
    }

    // ── CPU Core Control ──

    /// <summary>Get the total number of CPU threads (logical processors).</summary>
    public static int GetCpuCount()
    {
        try
        {
            return Directory.GetDirectories("/sys/devices/system/cpu/", "cpu[0-9]*").Length;
        }
        catch { return 0; }
    }

    /// <summary>Get the number of currently online CPU cores.</summary>
    public static int GetOnlineCpuCount()
    {
        int count = 0;
        try
        {
            var cpuDirs = Directory.GetDirectories("/sys/devices/system/cpu/", "cpu[0-9]*");
            foreach (var dir in cpuDirs)
            {
                var onlinePath = Path.Combine(dir, "online");
                if (!File.Exists(onlinePath))
                {
                    count++; // cpu0 has no online file, it's always on
                    continue;
                }
                if (SysfsHelper.ReadInt(onlinePath, 0) == 1)
                    count++;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("GetOnlineCpuCount failed", ex);
        }
        return count;
    }

    /// <summary>Set the number of online CPU cores. Disables from the highest-numbered cores down.</summary>
    public static void SetOnlineCpuCount(int targetCount)
    {
        try
        {
            var cpuDirs = Directory.GetDirectories("/sys/devices/system/cpu/", "cpu[0-9]*");
            // Sort numerically
            Array.Sort(cpuDirs, (a, b) =>
            {
                int numA = int.Parse(Path.GetFileName(a).Replace("cpu", ""));
                int numB = int.Parse(Path.GetFileName(b).Replace("cpu", ""));
                return numA.CompareTo(numB);
            });

            int total = cpuDirs.Length;
            targetCount = Math.Clamp(targetCount, 1, total);

            for (int i = 0; i < total; i++)
            {
                var onlinePath = Path.Combine(cpuDirs[i], "online");
                if (!File.Exists(onlinePath)) continue; // cpu0 can't be toggled

                bool shouldBeOnline = i < targetCount;
                SysfsHelper.WriteAttribute(onlinePath, shouldBeOnline ? "1" : "0");
            }

            Helpers.Logger.WriteLine($"CPU cores set to {targetCount}/{total}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("SetOnlineCpuCount failed", ex);
        }
    }
}

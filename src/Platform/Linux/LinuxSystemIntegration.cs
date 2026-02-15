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
        _desktopFilePath = Path.Combine(_autostartDir, "ghelper-linux.desktop");
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
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "ghelper-linux";
            var desktop = $"""
                [Desktop Entry]
                Type=Application
                Name=G-Helper
                Comment=ASUS Laptop Control (Linux)
                Exec={exePath}
                Icon=ghelper-linux
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
}

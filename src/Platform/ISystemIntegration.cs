namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over OS-level system integration.
/// Windows: WMI Win32_ComputerSystem/Win32_BIOS, Task Scheduler, Registry
/// Linux: DMI sysfs, XDG autostart, D-Bus notifications
/// </summary>
public interface ISystemIntegration
{
    /// <summary>Get laptop model name (e.g., "ROG Strix G614JVR").</summary>
    string GetModelName();

    /// <summary>Get BIOS version string.</summary>
    string GetBiosVersion();

    /// <summary>Get Linux kernel version.</summary>
    string GetKernelVersion();

    /// <summary>Enable/disable autostart on login.</summary>
    void SetAutostart(bool enabled);

    /// <summary>Check if autostart is enabled.</summary>
    bool IsAutostartEnabled();

    /// <summary>Show a desktop notification (toast).</summary>
    void ShowNotification(string title, string body, string? iconName = null);

    /// <summary>Check if the required asus-wmi kernel module is loaded.</summary>
    bool IsAsusWmiLoaded();

    /// <summary>Get the minimum required kernel version for full features.</summary>
    Version GetKernelVersionParsed();
}

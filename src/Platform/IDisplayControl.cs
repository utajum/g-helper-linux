namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over display/screen control.
/// Windows: user32 EnumDisplaySettings/ChangeDisplaySettings, gdi32 gamma, WMI brightness
/// Linux: xrandr/DRM, /sys/class/backlight/
/// </summary>
public interface IDisplayControl
{
    /// <summary>Get current screen brightness (0-100).</summary>
    int GetBrightness();

    /// <summary>Set screen brightness (0-100).</summary>
    void SetBrightness(int percent);

    /// <summary>Get current refresh rate in Hz.</summary>
    int GetRefreshRate();

    /// <summary>Get all available refresh rates for primary display.</summary>
    List<int> GetAvailableRefreshRates();

    /// <summary>Set refresh rate in Hz.</summary>
    void SetRefreshRate(int hz);

    /// <summary>Set display gamma (1.0 = normal).</summary>
    void SetGamma(float r, float g, float b);

    /// <summary>Get primary display name.</summary>
    string? GetDisplayName();
}

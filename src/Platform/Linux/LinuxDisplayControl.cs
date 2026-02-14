using System.Text.RegularExpressions;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux implementation of IDisplayControl.
/// Uses:
///   - /sys/class/backlight/ for brightness control
///   - xrandr for refresh rate control and display info
///   - xrandr --gamma for gamma control
/// 
/// On Wayland (GNOME), falls back to:
///   - gdbus/gnome-randr for refresh rate
///   - dbus brightness interface
/// </summary>
public class LinuxDisplayControl : IDisplayControl
{
    private string? _backlightDir;
    private int _maxBrightness;
    private bool _isWayland;

    public LinuxDisplayControl()
    {
        _backlightDir = FindBestBacklight();
        _isWayland = IsWaylandSession();

        if (_backlightDir != null)
        {
            _maxBrightness = SysfsHelper.ReadInt(
                Path.Combine(_backlightDir, "max_brightness"), 100);
            Helpers.Logger.WriteLine($"Backlight found: {_backlightDir} (max={_maxBrightness})");
        }
        else
        {
            Helpers.Logger.WriteLine("WARNING: No backlight device found. Brightness control unavailable.");
        }

        Helpers.Logger.WriteLine($"Display session type: {(_isWayland ? "Wayland" : "X11")}");
    }

    // ── Brightness ──

    public int GetBrightness()
    {
        if (_backlightDir == null || _maxBrightness <= 0) return -1;

        int current = SysfsHelper.ReadInt(
            Path.Combine(_backlightDir, "brightness"), -1);
        if (current < 0) return -1;

        return (int)Math.Round(current * 100.0 / _maxBrightness);
    }

    public void SetBrightness(int percent)
    {
        if (_backlightDir == null || _maxBrightness <= 0) return;

        percent = Math.Clamp(percent, 0, 100);
        int rawValue = (int)Math.Round(percent * _maxBrightness / 100.0);

        SysfsHelper.WriteInt(
            Path.Combine(_backlightDir, "brightness"), rawValue);
    }

    // ── Refresh Rate ──

    public int GetRefreshRate()
    {
        var displayName = GetPrimaryOutput();
        if (displayName == null) return -1;

        // Use xrandr to get current refresh rate
        var output = SysfsHelper.RunCommand("xrandr", $"--output {displayName} --verbose");
        if (output == null)
        {
            // Fallback: parse basic xrandr output
            output = SysfsHelper.RunCommand("xrandr", "");
            if (output == null) return -1;
        }

        // Find the line with * (current mode) and parse the refresh rate
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains('*'))
            {
                // Pattern: "1920x1080  144.00*+"  or  "1920x1080     60.00*+  144.00  ..."
                var match = Regex.Match(line, @"(\d+\.\d+)\*");
                if (match.Success && double.TryParse(match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out double hz))
                {
                    return (int)Math.Round(hz);
                }
            }
        }

        return -1;
    }

    public List<int> GetAvailableRefreshRates()
    {
        var rates = new List<int>();
        var displayName = GetPrimaryOutput();
        if (displayName == null) return rates;

        var output = SysfsHelper.RunCommand("xrandr", "");
        if (output == null) return rates;

        bool foundDisplay = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains(displayName) && line.Contains(" connected"))
            {
                foundDisplay = true;
                continue;
            }

            if (foundDisplay)
            {
                // Mode lines start with whitespace
                if (!line.StartsWith("   ") && !line.StartsWith("\t"))
                {
                    // Next output section — stop
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                        break;
                }

                // Parse all refresh rates from the mode line
                // Format: "   1920x1080     144.00 + 120.00   60.00"
                var matches = Regex.Matches(line, @"(\d+\.\d+)");
                foreach (Match match in matches)
                {
                    if (double.TryParse(match.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture, out double hz))
                    {
                        int intHz = (int)Math.Round(hz);
                        if (!rates.Contains(intHz) && intHz > 0)
                            rates.Add(intHz);
                    }
                }
            }
        }

        rates.Sort();
        rates.Reverse(); // Highest first
        return rates;
    }

    public void SetRefreshRate(int hz)
    {
        var displayName = GetPrimaryOutput();
        if (displayName == null) return;

        // Find the correct mode string for the requested refresh rate
        var output = SysfsHelper.RunCommand("xrandr", "");
        if (output == null) return;

        string? currentResolution = null;
        bool foundDisplay = false;

        foreach (var line in output.Split('\n'))
        {
            if (line.Contains(displayName) && line.Contains(" connected"))
            {
                foundDisplay = true;
                continue;
            }

            if (foundDisplay)
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    break;

                // Find the current resolution (line with *)
                if (line.Contains('*') && currentResolution == null)
                {
                    var resMatch = Regex.Match(line.Trim(), @"(\d+x\d+)");
                    if (resMatch.Success)
                        currentResolution = resMatch.Groups[1].Value;
                }
            }
        }

        if (currentResolution == null) return;

        // Set the mode with xrandr
        SysfsHelper.RunCommand("xrandr",
            $"--output {displayName} --mode {currentResolution} --rate {hz}");

        Helpers.Logger.WriteLine($"SetRefreshRate: {displayName} → {currentResolution}@{hz}Hz");
    }

    // ── Gamma ──

    public void SetGamma(float r, float g, float b)
    {
        var displayName = GetPrimaryOutput();
        if (displayName == null) return;

        // Clamp gamma values to reasonable range
        r = Math.Clamp(r, 0.1f, 5.0f);
        g = Math.Clamp(g, 0.1f, 5.0f);
        b = Math.Clamp(b, 0.1f, 5.0f);

        var gammaStr = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F2}:{1:F2}:{2:F2}", r, g, b);

        SysfsHelper.RunCommand("xrandr",
            $"--output {displayName} --gamma {gammaStr}");

        Helpers.Logger.WriteLine($"SetGamma: {displayName} → {gammaStr}");
    }

    // ── Display Name ──

    public string? GetDisplayName()
    {
        var output = GetPrimaryOutput();
        if (output == null) return null;

        // Get the full display info from xrandr
        var xrandrOutput = SysfsHelper.RunCommand("xrandr", "--verbose");
        if (xrandrOutput == null) return output;

        // Try to find EDID-derived display name
        bool inOutput = false;
        foreach (var line in xrandrOutput.Split('\n'))
        {
            if (line.StartsWith(output))
            {
                inOutput = true;
                continue;
            }

            if (inOutput && line.Length > 0 && !char.IsWhiteSpace(line[0]))
                break;

            if (inOutput && line.Contains("EDID:"))
            {
                // EDID parsing is complex; return the output name
                return output;
            }
        }

        return output;
    }

    // ── Private helpers ──

    /// <summary>
    /// Find the best backlight device.
    /// Preference order: firmware > platform > raw
    /// ACPI backlight > vendor backlight > generic
    /// </summary>
    private static string? FindBestBacklight()
    {
        if (!Directory.Exists(SysfsHelper.Backlight)) return null;

        string? bestDir = null;
        int bestPriority = -1;

        try
        {
            foreach (var dir in Directory.GetDirectories(SysfsHelper.Backlight))
            {
                var typePath = Path.Combine(dir, "type");
                var type = SysfsHelper.ReadAttribute(typePath) ?? "raw";

                int priority = type switch
                {
                    "firmware" => 3,  // ACPI (best — most compatible)
                    "platform" => 2,  // Vendor-specific
                    "raw" => 1,       // Direct hardware
                    _ => 0
                };

                // Also prefer intel_backlight or amdgpu_bl0 over generic
                var name = Path.GetFileName(dir);
                if (name.Contains("intel") || name.Contains("amd") || name.Contains("nvidia"))
                    priority += 1;

                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    bestDir = dir;
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("FindBestBacklight failed", ex);
        }

        return bestDir;
    }

    /// <summary>
    /// Get the primary/laptop display output name from xrandr.
    /// Priority: eDP-1 > eDP-2 > LVDS-1 > first connected
    /// </summary>
    private static string? GetPrimaryOutput()
    {
        var output = SysfsHelper.RunCommand("xrandr", "--query");
        if (output == null) return null;

        string? primary = null;
        string? firstConnected = null;

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(" connected")) continue;

            var outputName = line.Split(' ')[0];

            // eDP is always the laptop panel
            if (outputName.StartsWith("eDP", StringComparison.OrdinalIgnoreCase))
                return outputName;

            // LVDS is the legacy laptop panel connector
            if (outputName.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase))
                primary ??= outputName;

            // Track first connected as fallback
            firstConnected ??= outputName;
        }

        return primary ?? firstConnected;
    }

    /// <summary>
    /// Detect if we're running on a Wayland session.
    /// </summary>
    private static bool IsWaylandSession()
    {
        var xdgType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (xdgType != null && xdgType.Equals("wayland", StringComparison.OrdinalIgnoreCase))
            return true;

        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return !string.IsNullOrEmpty(waylandDisplay);
    }
}

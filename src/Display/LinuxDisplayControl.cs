using GHelper.Linux.I18n;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Display;

/// <summary>
/// Linux implementation of IDisplayControl.
/// Owns backlight (sysfs) and delegates refresh rate / gamma / display name
/// to an auto-detected IDisplayBackend (xrandr, wlr-randr, kscreen-doctor).
/// </summary>
public class LinuxDisplayControl : IDisplayControl
{
    /// <summary>Minimum brightness percentage - prevents screen from going fully black.</summary>
    public const int MinBrightnessPercent = 4;

    private string? _backlightDir;
    private int _maxBrightness;
    private readonly IDisplayBackend? _backend;

    public LinuxDisplayControl()
    {
        InitBacklight();
        _backend = DisplayBackendFactory.Create();
    }

    /// <summary>True when a /sys/class/backlight/ device is available.</summary>
    public bool HasBacklight => _backlightDir != null && _maxBrightness > 0;

    /// <summary>Name of the currently active backlight device (e.g. "nvidia_0").</summary>
    public string? ActiveBacklightName => _backlightDir != null ? Path.GetFileName(_backlightDir) : null;

    /// <summary>The active display backend (for logging/diagnostics). Null if no backend available.</summary>
    public IDisplayBackend? Backend => _backend;

    // Backlight - sysfs (works on both X11 and Wayland)

    public int GetBrightness()
    {
        if (_backlightDir == null || _maxBrightness <= 0)
            return -1;

        int current = SysfsHelper.ReadInt(
            Path.Combine(_backlightDir, "brightness"), -1);
        if (current < 0)
            return -1;

        return (int)Math.Round(current * 100.0 / _maxBrightness);
    }

    public void SetBrightness(int percent)
    {
        if (_backlightDir == null || _maxBrightness <= 0)
            return;

        percent = Math.Clamp(percent, MinBrightnessPercent, 100);
        int rawValue = (int)Math.Round(percent * _maxBrightness / 100.0);

        SysfsHelper.WriteInt(
            Path.Combine(_backlightDir, "brightness"), rawValue);
    }

    // Display operations - delegated to IDisplayBackend

    public int GetRefreshRate()
        => _backend?.GetRefreshRate() ?? -1;

    public List<int> GetAvailableRefreshRates()
        => _backend?.GetAvailableRefreshRates() ?? new List<int>();

    public void SetRefreshRate(int hz)
    {
        if (_backend == null)
        {
            Helpers.Logger.WriteLine("SetRefreshRate: no display backend available");
            return;
        }
        // Skip the modeset when the panel is already at the target rate.
        int current = _backend.GetRefreshRate();
        if (current == hz)
        {
            Helpers.Logger.WriteLine($"SetRefreshRate({hz}Hz): already set, skipping");
            return;
        }
        Helpers.Logger.WriteLine($"SetRefreshRate({hz}Hz) via {_backend.Name} backend");
        _backend.SetRefreshRate(hz);
    }

    public void SetGamma(float r, float g, float b)
    {
        if (_backend == null)
            return;

        if (!_backend.SupportsGamma)
        {
            Helpers.Logger.WriteLine($"SetGamma: not supported by {_backend.Name} backend");
            return;
        }

        _backend.SetGamma(r, g, b);
    }

    public string? GetDisplayName()
        => _backend?.GetDisplayName();

    // Backlight module detection & loading

    /// <summary>
    /// When nvidia is active but no nvidia backlight exists, returns the module name to load.
    /// </summary>
    public static string? GetMissingBacklightModule()
    {
        if (Directory.Exists("/sys/module/nvidia"))
        {
            bool hasNvidiaBacklight = false;
            try
            {
                if (Directory.Exists(SysfsHelper.Backlight))
                {
                    foreach (var dir in Directory.GetDirectories(SysfsHelper.Backlight))
                    {
                        if (Path.GetFileName(dir).Contains("nvidia", StringComparison.OrdinalIgnoreCase))
                        {
                            hasNvidiaBacklight = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (!hasNvidiaBacklight)
            {
                string? modinfo = SysfsHelper.RunCommand("modinfo", "nvidia-wmi-ec-backlight");
                if (modinfo != null)
                    return "nvidia-wmi-ec-backlight";
            }
        }

        return null;
    }

    /// <summary>
    /// Human-readable hint when no backlight device exists and no module can be loaded.
    /// </summary>
    public static string GetBacklightHint()
    {
        if (Directory.Exists("/sys/module/nvidia"))
            return Labels.Get("hint_nvidia");
        if (Directory.Exists("/sys/module/i915"))
            return Labels.Get("hint_intel");
        if (Directory.Exists("/sys/module/amdgpu"))
            return Labels.Get("hint_amd");

        return Labels.Get("hint_none");
    }

    /// <summary>
    /// Load the given backlight kernel module via pkexec and re-scan for backlight devices.
    /// </summary>
    public bool TryLoadBacklightModule(string moduleName)
    {
        Helpers.Logger.WriteLine($"Backlight: loading module {moduleName} (sudo, pkexec fallback)...");
        SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "modprobe", moduleName });

        Thread.Sleep(1000);

        InitBacklight();
        if (HasBacklight)
        {
            Helpers.Logger.WriteLine($"Backlight: module loaded, device found: {_backlightDir} (max={_maxBrightness})");
            return true;
        }

        Helpers.Logger.WriteLine($"Backlight: module loaded but no backlight device appeared.");
        return false;
    }

    // Backlight device enumeration & switching

    /// <summary>
    /// Returns all available backlight device names (e.g. ["nvidia_0", "intel_backlight"]).
    /// </summary>
    public static List<string> GetAvailableBacklights()
    {
        var result = new List<string>();
        if (!Directory.Exists(SysfsHelper.Backlight))
            return result;

        try
        {
            foreach (var dir in Directory.GetDirectories(SysfsHelper.Backlight))
                result.Add(Path.GetFileName(dir));
        }
        catch { }

        result.Sort();
        return result;
    }

    /// <summary>
    /// Switch the active backlight device by name (e.g. "nvidia_0").
    /// </summary>
    public bool SetActiveBacklight(string name)
    {
        var path = Path.Combine(SysfsHelper.Backlight, name);
        if (!Directory.Exists(path))
            return false;

        _backlightDir = path;
        _maxBrightness = SysfsHelper.ReadInt(
            Path.Combine(_backlightDir, "max_brightness"), 100);
        Helpers.Logger.WriteLine($"Backlight switched to: {_backlightDir} (max={_maxBrightness})");
        return true;
    }

    // Private helpers

    private void InitBacklight()
    {
        _backlightDir = FindBestBacklight();
        if (_backlightDir != null)
        {
            _maxBrightness = SysfsHelper.ReadInt(
                Path.Combine(_backlightDir, "max_brightness"), 100);
            Helpers.Logger.WriteLine($"Backlight found: {_backlightDir} (max={_maxBrightness})");
        }
        else
        {
            _maxBrightness = 0;
            Helpers.Logger.WriteLine("WARNING: No backlight device found. Brightness control unavailable.");
        }
    }

    /// <summary>
    /// Find the best backlight device.
    /// Priority: firmware > platform > raw, with bonus for intel/amd/nvidia names.
    /// </summary>
    private static string? FindBestBacklight()
    {
        if (!Directory.Exists(SysfsHelper.Backlight))
            return null;

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
                    "firmware" => 3,
                    "platform" => 2,
                    "raw" => 1,
                    _ => 0
                };

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
}

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Helper for reading/writing Linux sysfs pseudo-filesystem attributes.
/// All ASUS WMI features on Linux are exposed via sysfs under:
///   /sys/devices/platform/asus-nb-wmi/
///   /sys/class/hwmon/hwmon*/  (for fan/thermal sensors)
///   /sys/class/power_supply/  (for battery)
///   /sys/class/leds/          (for keyboard backlight)
///   /sys/class/backlight/     (for screen brightness)
/// </summary>
public static class SysfsHelper
{
    // ── Well-known sysfs paths ──

    public const string AsusWmiPlatform = "/sys/devices/platform/asus-nb-wmi";
    public const string AsusBusPlatform = "/sys/bus/platform/devices/asus-nb-wmi";
    public const string PowerSupply = "/sys/class/power_supply";
    public const string Backlight = "/sys/class/backlight";
    public const string Leds = "/sys/class/leds";
    public const string Thermal = "/sys/class/thermal";
    public const string Hwmon = "/sys/class/hwmon";
    public const string CpuFreq = "/sys/devices/system/cpu/cpufreq";
    public const string IntelPstate = "/sys/devices/system/cpu/intel_pstate";
    public const string DmiId = "/sys/class/dmi/id";
    public const string PlatformProfile = "/sys/firmware/acpi/platform_profile";
    public const string PlatformProfileChoices = "/sys/firmware/acpi/platform_profile_choices";
    public const string PcieAspm = "/sys/module/pcie_aspm/parameters/policy";

    /// <summary>Read a sysfs attribute as a trimmed string. Returns null on failure.</summary>
    public static string? ReadAttribute(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.ReadAttribute({path}) failed", ex);
            return null;
        }
    }

    /// <summary>Read a sysfs attribute as an integer. Returns defaultValue on failure.</summary>
    public static int ReadInt(string path, int defaultValue = -1)
    {
        var str = ReadAttribute(path);
        if (str != null && int.TryParse(str, out int value))
            return value;
        return defaultValue;
    }

    /// <summary>Write a string to a sysfs attribute. Returns true on success.</summary>
    public static bool WriteAttribute(string path, string value)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.WriteAllText(path, value);
            Helpers.Logger.WriteLine($"SysfsHelper.WriteAttribute({path}) = {value}");
            return true;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.WriteAttribute({path}, {value}) failed", ex);
            return false;
        }
    }

    /// <summary>Write an integer to a sysfs attribute.</summary>
    public static bool WriteInt(string path, int value)
    {
        return WriteAttribute(path, value.ToString());
    }

    /// <summary>Check if a sysfs path exists.</summary>
    public static bool Exists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>
    /// Find the hwmon device for a given driver name (e.g., "asus_nb_wmi", "amdgpu", "nvidia").
    /// Returns the hwmon directory path or null.
    /// Tries exact match first, then normalized match (underscores ↔ dashes).
    /// Results are cached to avoid repeated filesystem scans during sensor polling.
    /// </summary>
    private static readonly Dictionary<string, string?> _hwmonCache = new();

    public static string? FindHwmonByName(string driverName)
    {
        // Return cached result (including null = "not found")
        if (_hwmonCache.TryGetValue(driverName, out var cached))
            return cached;

        string? result = FindHwmonByNameUncached(driverName);
        _hwmonCache[driverName] = result;
        return result;
    }

    private static string? FindHwmonByNameUncached(string driverName)
    {
        try
        {
            if (!Directory.Exists(Hwmon)) return null;

            // Normalize: asus_nb_wmi ↔ asus-nb-wmi
            string normalized = driverName.Replace('_', '-');
            string withUnderscores = driverName.Replace('-', '_');

            foreach (var hwmonDir in Directory.GetDirectories(Hwmon))
            {
                var namePath = Path.Combine(hwmonDir, "name");
                var name = ReadAttribute(namePath);
                if (name == null) continue;

                // Exact match
                if (name.Equals(driverName, StringComparison.OrdinalIgnoreCase))
                    return hwmonDir;

                // Normalized match (underscore vs dash)
                if (name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(withUnderscores, StringComparison.OrdinalIgnoreCase))
                    return hwmonDir;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.FindHwmonByName({driverName}) failed", ex);
        }
        return null;
    }

    /// <summary>Log all hwmon devices once at startup for diagnostics.</summary>
    public static void LogAllHwmon()
    {
        try
        {
            if (!Directory.Exists(Hwmon)) return;
            foreach (var hwmonDir in Directory.GetDirectories(Hwmon))
            {
                var name = ReadAttribute(Path.Combine(hwmonDir, "name"));
                Helpers.Logger.WriteLine($"  hwmon: {Path.GetFileName(hwmonDir)} = {name ?? "(no name)"}");
            }
        }
        catch { }
    }

    /// <summary>
    /// Find all hwmon devices matching a driver name.
    /// </summary>
    public static List<string> FindAllHwmonByName(string driverName)
    {
        var results = new List<string>();
        try
        {
            if (!Directory.Exists(Hwmon)) return results;

            foreach (var hwmonDir in Directory.GetDirectories(Hwmon))
            {
                var namePath = Path.Combine(hwmonDir, "name");
                var name = ReadAttribute(namePath);
                if (name != null && name.Equals(driverName, StringComparison.OrdinalIgnoreCase))
                    results.Add(hwmonDir);
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.FindAllHwmonByName({driverName}) failed", ex);
        }
        return results;
    }

    /// <summary>
    /// Find the first battery device in /sys/class/power_supply/ that has type="Battery".
    /// Returns the directory path (e.g., "/sys/class/power_supply/BAT0") or null.
    /// </summary>
    public static string? FindBattery()
    {
        try
        {
            if (!Directory.Exists(PowerSupply)) return null;

            foreach (var psDir in Directory.GetDirectories(PowerSupply))
            {
                var typePath = Path.Combine(psDir, "type");
                var type = ReadAttribute(typePath);
                if (type != null && type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
                    return psDir;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("SysfsHelper.FindBattery() failed", ex);
        }
        return null;
    }

    /// <summary>
    /// Find the AC adapter in /sys/class/power_supply/.
    /// </summary>
    public static string? FindAcAdapter()
    {
        try
        {
            if (!Directory.Exists(PowerSupply)) return null;

            foreach (var psDir in Directory.GetDirectories(PowerSupply))
            {
                var typePath = Path.Combine(psDir, "type");
                var type = ReadAttribute(typePath);
                if (type != null && type.Equals("Mains", StringComparison.OrdinalIgnoreCase))
                    return psDir;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("SysfsHelper.FindAcAdapter() failed", ex);
        }
        return null;
    }

    /// <summary>
    /// Find the first backlight device.
    /// Returns the directory path (e.g., "/sys/class/backlight/intel_backlight") or null.
    /// </summary>
    public static string? FindBacklight()
    {
        try
        {
            if (!Directory.Exists(Backlight)) return null;
            var dirs = Directory.GetDirectories(Backlight);
            return dirs.Length > 0 ? dirs[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Run a shell command and return stdout. Returns null on failure.</summary>
    public static string? RunCommand(string command, string args = "")
    {
        return RunCommandWithTimeout(command, args, 5000);
    }

    /// <summary>Run a shell command with specified timeout (milliseconds).</summary>
    public static string? RunCommandWithTimeout(string command, string args, int timeoutMs)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            // Read output with timeout
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (outputTask.Wait(timeoutMs))
            {
                var output = outputTask.Result.Trim();
                proc.WaitForExit(100); // Give a moment for exit code
                return proc.ExitCode == 0 ? output : null;
            }
            else
            {
                // Timeout - kill the process
                try
                {
                    proc.Kill();
                    Helpers.Logger.WriteLine($"RunCommand timeout: {command} {args}");
                }
                catch { /* Ignore kill errors */ }
                return null;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.RunCommand({command} {args}) failed", ex);
            return null;
        }
    }
}

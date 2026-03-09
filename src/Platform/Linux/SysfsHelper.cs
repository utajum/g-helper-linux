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
    public const string FirmwareAttributes = "/sys/class/firmware-attributes/asus-armoury/attributes";
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

    // ── Firmware-attributes path resolution cache ──
    // On newer kernels (6.8+ with asus_armoury module), legacy sysfs paths under
    // asus-nb-wmi may not exist. Instead, attributes live under:
    //   /sys/class/firmware-attributes/asus-armoury/attributes/{name}/current_value
    // This cache maps attribute names to resolved full paths (including /current_value
    // suffix for firmware-attributes, or legacy path if that exists).
    // null value = attribute doesn't exist in either location.
    private static readonly Dictionary<string, string?> _resolvedPaths = new();

    /// <summary>
    /// Resolve the actual sysfs path for an ASUS WMI attribute using an AttrDef.
    /// Handles aliased attributes (where legacy and firmware-attributes names differ).
    ///
    /// Resolution order:
    /// 1. Try legacy paths with LegacyName
    /// 2. Try firmware-attributes with FwAttrName (may differ from LegacyName for aliased attrs)
    /// 3. If BOTH exist AND the attribute has an alias, prefer firmware-attributes
    ///    (the legacy path may be a phantom that returns ENODEV on dual-backend machines)
    ///
    /// Results are cached for the lifetime of the process.
    /// </summary>
    public static string? ResolveAttrPath(AttrDef attr, params string[] legacyBases)
    {
        if (_resolvedPaths.TryGetValue(attr.LegacyName, out var cached))
            return cached;

        if (legacyBases.Length == 0)
            legacyBases = new[] { AsusWmiPlatform, AsusBusPlatform };

        // Find legacy path
        string? legacyResult = null;
        foreach (var basePath in legacyBases)
        {
            var legacyPath = Path.Combine(basePath, attr.LegacyName);
            if (File.Exists(legacyPath))
            {
                legacyResult = legacyPath;
                break;
            }
        }

        // Find firmware-attributes path (using FwAttrName, which may differ from LegacyName)
        string? fwResult = null;
        var fwPath = Path.Combine(FirmwareAttributes, attr.FwAttrName, "current_value");
        if (File.Exists(fwPath))
        {
            fwResult = fwPath;
        }
        else if (attr.HasAlias)
        {
            // Also try the legacy name in firmware-attributes (some kernels may use it)
            var fwPathLegacy = Path.Combine(FirmwareAttributes, attr.LegacyName, "current_value");
            if (File.Exists(fwPathLegacy))
                fwResult = fwPathLegacy;
        }

        // Choose: for aliased attributes, prefer firmware-attributes when available
        // (legacy path may be a phantom ENODEV on dual-backend machines)
        string? result;
        if (attr.HasAlias && fwResult != null)
            result = fwResult;
        else
            result = legacyResult ?? fwResult;

        if (attr.HasAlias && fwResult != null && legacyResult != null)
            Helpers.Logger.WriteLine($"ResolveAttrPath({attr.LegacyName}): preferring firmware-attributes ({attr.FwAttrName}) over legacy ({legacyResult}) — legacy may be phantom ENODEV");

        _resolvedPaths[attr.LegacyName] = result;
        return result;
    }

    /// <summary>
    /// Resolve the actual sysfs path for an ASUS WMI attribute by name (string).
    /// Tries legacy paths first (AsusWmiPlatform, AsusBusPlatform), then
    /// falls back to firmware-attributes (asus-armoury).
    /// For aliased attributes, use the AttrDef overload instead.
    /// Results are cached for the lifetime of the process.
    /// </summary>
    /// <param name="attrName">Attribute name, e.g. "dgpu_disable", "throttle_thermal_policy"</param>
    /// <param name="legacyBases">Legacy base paths to check (in order). Defaults to both platform paths.</param>
    /// <returns>Full resolved path to read/write, or null if not found anywhere.</returns>
    public static string? ResolveAttrPath(string attrName, params string[] legacyBases)
    {
        // Check if this is a known attribute with an alias — use AttrDef resolution
        var attrDef = AsusAttributes.FindByLegacyName(attrName);
        if (attrDef != null)
            return ResolveAttrPath(attrDef, legacyBases);

        if (_resolvedPaths.TryGetValue(attrName, out var cached))
            return cached;

        // Default legacy bases if none specified
        if (legacyBases.Length == 0)
            legacyBases = new[] { AsusWmiPlatform, AsusBusPlatform };

        // Try legacy paths first
        foreach (var basePath in legacyBases)
        {
            var legacyPath = Path.Combine(basePath, attrName);
            if (File.Exists(legacyPath))
            {
                _resolvedPaths[attrName] = legacyPath;
                return legacyPath;
            }
        }

        // Try firmware-attributes (asus-armoury)
        var fwPath = Path.Combine(FirmwareAttributes, attrName, "current_value");
        if (File.Exists(fwPath))
        {
            _resolvedPaths[attrName] = fwPath;
            return fwPath;
        }

        // Not found anywhere
        _resolvedPaths[attrName] = null;
        return null;
    }

    /// <summary>
    /// Check if a resolved path is a firmware-attributes path (vs legacy sysfs).
    /// </summary>
    public static bool IsFirmwareAttributesPath(string? path)
    {
        return path != null && path.StartsWith(FirmwareAttributes);
    }

    /// <summary>
    /// Log which backend was resolved for each known attribute (for diagnostics).
    /// </summary>
    public static void LogResolvedAttributes()
    {
        bool hasFirmwareAttrs = Directory.Exists(FirmwareAttributes);
        Helpers.Logger.WriteLine($"Firmware-attributes (asus-armoury): {(hasFirmwareAttrs ? "PRESENT" : "not present")}");

        foreach (var attr in AsusAttributes.All)
        {
            var path = ResolveAttrPath(attr);
            if (path == null)
            {
                Helpers.Logger.WriteLine($"  {attr.LegacyName}: not found");
            }
            else if (IsFirmwareAttributesPath(path))
            {
                var suffix = attr.HasAlias ? $" (as {attr.FwAttrName})" : "";
                // Check if legacy path also exists (phantom on dual-backend machines)
                string? legacyCheck = null;
                foreach (var basePath in new[] { AsusWmiPlatform, AsusBusPlatform })
                {
                    var lp = Path.Combine(basePath, attr.LegacyName);
                    if (File.Exists(lp)) { legacyCheck = lp; break; }
                }
                var phantomNote = legacyCheck != null ? $" [preferred over legacy {legacyCheck}]" : "";
                Helpers.Logger.WriteLine($"  {attr.LegacyName}: firmware-attributes{suffix}{phantomNote}");
            }
            else
            {
                Helpers.Logger.WriteLine($"  {attr.LegacyName}: legacy sysfs");
            }
        }
    }

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
            Helpers.Logger.WriteLine($"SysfsHelper.WriteAttribute({path}) = {value}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            File.WriteAllText(path, value);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 1000)
                Helpers.Logger.WriteLine($"SysfsHelper.WriteAttribute({path}) completed in {sw.ElapsedMilliseconds}ms (slow!)");
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

    /// <summary>Find a hwmon device by name that also contains a specific file (e.g., "fan1_input").
    /// Tries names in order, returning the first match that has the required file.</summary>
    public static string? FindHwmonByNameWithFile(string requiredFile, params string[] names)
    {
        try
        {
            if (!Directory.Exists(Hwmon)) return null;

            foreach (var name in names)
            {
                foreach (var hwmonDir in Directory.GetDirectories(Hwmon))
                {
                    var namePath = Path.Combine(hwmonDir, "name");
                    var hwmonName = ReadAttribute(namePath);
                    if (hwmonName == null) continue;

                    // Match name (with underscore/dash normalization)
                    string normalized = name.Replace('_', '-');
                    string withUnderscores = name.Replace('-', '_');

                    bool nameMatch = hwmonName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                  || hwmonName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                                  || hwmonName.Equals(withUnderscores, StringComparison.OrdinalIgnoreCase);

                    if (nameMatch && File.Exists(Path.Combine(hwmonDir, requiredFile)))
                        return hwmonDir;
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.FindHwmonByNameWithFile({requiredFile}) failed", ex);
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
            var errorTask = proc.StandardError.ReadToEndAsync();
            
            if (outputTask.Wait(timeoutMs))
            {
                var output = outputTask.Result.Trim();
                var errorOutput = errorTask.IsCompleted ? errorTask.Result.Trim() : "";
                
                proc.WaitForExit(100); // Give a moment for exit code
                
                if (proc.ExitCode != 0 && !string.IsNullOrEmpty(errorOutput))
                {
                    Helpers.Logger.WriteLine($"RunCommand({command} {args}) failed with exit code {proc.ExitCode}: {errorOutput}");
                }
                
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

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
    // Well-known sysfs paths

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

    // Firmware-attributes path resolution cache
    // On newer kernels (6.8+ with asus_armoury module), legacy sysfs paths under
    // asus-nb-wmi may not exist. Instead, attributes live under:
    // /sys/class/firmware-attributes/asus-armoury/attributes/{name}/current_value
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

        // Choose: prefer firmware-attributes for aliased OR explicitly-flagged attrs
        // (legacy path may be a phantom ENODEV on dual-backend machines)
        string? result;
        if ((attr.HasAlias || attr.PreferFwAttr) && fwResult != null)
            result = fwResult;
        else
            result = legacyResult ?? fwResult;

        if ((attr.HasAlias || attr.PreferFwAttr) && fwResult != null && legacyResult != null)
        {
            string reason = attr.HasAlias ? "alias" : "explicit preference";
            Helpers.Logger.WriteLine($"ResolveAttrPath({attr.LegacyName}): preferring firmware-attributes ({attr.FwAttrName}) over legacy ({legacyResult}) - {reason}");
        }

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
        // Check if this is a known attribute with an alias - use AttrDef resolution
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
    /// Write a value to ALL available backends (legacy sysfs + firmware-attributes) for the
    /// given attribute. Returns true if at least one write succeeded.
    ///
    /// Use this for PPT power limit writes where we cannot predict which backend is functional
    /// on dual-backend kernels (asus-nb-wmi + asus-armoury loaded simultaneously).
    /// On the reporter's GZ302EA, the firmware-attributes ppt_pl3_fppt path exists but
    /// returns ENODEV/EACCES, while the legacy ppt_fppt works fine - and vice versa on
    /// other machines.  Writing to both is the safest approach.
    /// </summary>
    public static bool WriteToAllBackends(AttrDef attr, string value, params string[] legacyBases)
    {
        var result = WriteToAllBackendsDetailed(attr, value, legacyBases);
        return result.Any;
    }

    /// <summary>
    /// Result of <see cref="WriteToAllBackendsDetailed"/>. Lets callers
    /// distinguish a legacy-only write (firmware applies the change
    /// immediately) from a fw-attr-only write (BIOS-staged, reboot needed)
    /// so they can show the right user message.
    /// </summary>
    public readonly struct BackendWriteResult
    {
        public bool Legacy { get; init; }
        public bool FwAttr { get; init; }
        public bool Any => Legacy || FwAttr;
    }

    /// <summary>
    /// Same as <see cref="WriteToAllBackends"/> but reports which backend
    /// actually accepted the write. Used by the XG Mobile toggle to pick
    /// between an "active in ~15 s" notification (legacy succeeded - ACPI
    /// applies immediately) and a "reboot required" notification (only
    /// fw-attr succeeded - asus-armoury stages until next boot).
    /// </summary>
    public static BackendWriteResult WriteToAllBackendsDetailed(AttrDef attr, string value, params string[] legacyBases)
    {
        if (legacyBases.Length == 0)
            legacyBases = new[] { AsusWmiPlatform, AsusBusPlatform };

        bool legacyOk = false;
        bool fwOk = false;

        // Try all legacy paths
        foreach (var basePath in legacyBases)
        {
            var legacyPath = Path.Combine(basePath, attr.LegacyName);
            if (WriteAttribute(legacyPath, value))
                legacyOk = true;
        }

        // Try firmware-attributes path (using FwAttrName, which may differ from LegacyName)
        var fwPath = Path.Combine(FirmwareAttributes, attr.FwAttrName, "current_value");
        if (WriteAttribute(fwPath, value))
            fwOk = true;

        // For aliased attrs (e.g. ppt_fppt → ppt_pl3_fppt), also try the legacy name
        // under firmware-attributes in case the kernel uses that naming
        if (attr.HasAlias)
        {
            var fwPathLegacy = Path.Combine(FirmwareAttributes, attr.LegacyName, "current_value");
            if (fwPathLegacy != fwPath && WriteAttribute(fwPathLegacy, value))
                fwOk = true;
        }

        return new BackendWriteResult { Legacy = legacyOk, FwAttr = fwOk };
    }

    /// <summary>
    /// Read from the first available backend (legacy sysfs or firmware-attributes) for the
    /// given attribute. Returns the integer value or defaultValue if none could be read.
    ///
    /// Unlike WriteToAllBackends (which writes everywhere), reading only needs the first
    /// successful result since they should all reflect the same hardware state.
    /// </summary>
    public static int ReadFromAnyBackend(AttrDef attr, int defaultValue = -1, params string[] legacyBases)
    {
        if (legacyBases.Length == 0)
            legacyBases = new[] { AsusWmiPlatform, AsusBusPlatform };

        // Try legacy paths first (more reliable on dual-backend systems)
        foreach (var basePath in legacyBases)
        {
            var legacyPath = Path.Combine(basePath, attr.LegacyName);
            var val = ReadInt(legacyPath, int.MinValue);
            if (val != int.MinValue)
                return val;
        }

        // Try firmware-attributes
        var fwPath = Path.Combine(FirmwareAttributes, attr.FwAttrName, "current_value");
        var fwVal = ReadInt(fwPath, int.MinValue);
        if (fwVal != int.MinValue)
            return fwVal;

        // Aliased fallback
        if (attr.HasAlias)
        {
            var fwPathLegacy = Path.Combine(FirmwareAttributes, attr.LegacyName, "current_value");
            if (fwPathLegacy != fwPath)
            {
                var aliasVal = ReadInt(fwPathLegacy, int.MinValue);
                if (aliasVal != int.MinValue)
                    return aliasVal;
            }
        }

        return defaultValue;
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
                    if (File.Exists(lp))
                    { legacyCheck = lp; break; }
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
            if (!File.Exists(path))
                return null;
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.ReadAttribute({path}) failed", ex);
            return null;
        }
    }

    /// <summary>Read a sysfs attribute without logging on failure (for
    /// root-only files where permission-denied is expected).</summary>
    public static string? ReadAttributeSilent(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return File.ReadAllText(path).Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Read the asus-armoury <c>possible_values</c> companion file for a
    /// given attribute. Returns null when the attr isn't present, returns
    /// an empty array when the kernel publishes no value list (treat as
    /// "free-form integer"). Each value is whitespace-trimmed.
    ///
    /// File location: <c>FirmwareAttributes/&lt;attr&gt;/possible_values</c>.
    /// Format: kernel writes a space- or newline-separated list, e.g.
    /// "256 512 1024 2048" or "0\n1".
    /// </summary>
    public static string[]? ReadPossibleValues(AttrDef attr)
    {
        try
        {
            // possible_values lives next to current_value in the fw-attr
            // directory; legacy sysfs attrs don't expose it.
            var dir = Path.Combine(FirmwareAttributes, attr.FwAttrName);
            if (!Directory.Exists(dir))
                return null;
            var pvPath = Path.Combine(dir, "possible_values");
            if (!File.Exists(pvPath))
                return Array.Empty<string>();

            var raw = File.ReadAllText(pvPath);
            // Kernel typically emits values separated by spaces / newlines /
            // semicolons. Split on any whitespace + collapse blanks.
            return raw
                .Split(new[] { ' ', '\t', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.ReadPossibleValues({attr.FwAttrName}) failed", ex);
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
            if (!File.Exists(path))
                return false;
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
            if (!Directory.Exists(Hwmon))
                return null;

            // Normalize: asus_nb_wmi ↔ asus-nb-wmi
            string normalized = driverName.Replace('_', '-');
            string withUnderscores = driverName.Replace('-', '_');

            foreach (var hwmonDir in Directory.GetDirectories(Hwmon))
            {
                var namePath = Path.Combine(hwmonDir, "name");
                var name = ReadAttribute(namePath);
                if (name == null)
                    continue;

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
            if (!Directory.Exists(Hwmon))
                return null;

            foreach (var name in names)
            {
                foreach (var hwmonDir in Directory.GetDirectories(Hwmon))
                {
                    var namePath = Path.Combine(hwmonDir, "name");
                    var hwmonName = ReadAttribute(namePath);
                    if (hwmonName == null)
                        continue;

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
            if (!Directory.Exists(Hwmon))
                return;
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
            if (!Directory.Exists(Hwmon))
                return results;

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
    /// Find the laptop battery device in /sys/class/power_supply/.
    /// Returns the directory path (e.g., "/sys/class/power_supply/BAT0") or null.
    ///
    /// Multiple type=Battery devices can coexist (laptop + wireless keyboard/mouse/headset
    /// HID batteries). Directory.GetDirectories returns sysfs entries in registration order
    /// (no sort), so on some kernels (e.g. ROG Flow Z13 2025 with hid_asus loading early)
    /// a HID device battery wins the first-match race over the ACPI BAT0. Score candidates
    /// to deterministically prefer the real laptop battery:
    ///   +100  name starts with "BAT" (ACPI standard: BAT0, BAT1, BATC, BATT)
    ///   +50   has charge_control_end_threshold attribute (real laptop batteries do)
    ///   -1000 name starts with "hid-" (excludes wireless peripheral batteries)
    /// </summary>
    public static string? FindBattery()
    {
        try
        {
            if (!Directory.Exists(PowerSupply))
                return null;

            string? best = null;
            int bestScore = int.MinValue;

            foreach (var psDir in Directory.GetDirectories(PowerSupply))
            {
                var type = ReadAttribute(Path.Combine(psDir, "type"));
                if (type == null || !type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileName(psDir);
                int score = 0;
                if (name.StartsWith("BAT", StringComparison.Ordinal))
                    score += 100;
                if (name.StartsWith("hid-", StringComparison.OrdinalIgnoreCase))
                    score -= 1000;
                if (File.Exists(Path.Combine(psDir, "charge_control_end_threshold")))
                    score += 50;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = psDir;
                }
            }

            return best;
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
            if (!Directory.Exists(PowerSupply))
                return null;

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
            if (!Directory.Exists(Backlight))
                return null;
            var dirs = Directory.GetDirectories(Backlight);
            return dirs.Length > 0 ? dirs[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Connected display names under a PCI GPU's DRM card.</summary>
    public static List<string> FindConnectedDisplays(string gpuSysPath)
    {
        var result = new List<string>();
        try
        {
            string drmDir = Path.Combine(gpuSysPath, "drm");
            if (!Directory.Exists(drmDir))
                return result;
            foreach (var cardDir in Directory.GetDirectories(drmDir, "card*"))
            {
                foreach (var connDir in Directory.GetDirectories(cardDir))
                {
                    string name = Path.GetFileName(connDir);
                    if (!name.Contains('-'))
                        continue;
                    string statusPath = Path.Combine(connDir, "status");
                    string? status = ReadAttribute(statusPath);
                    if (status == "connected")
                    {
                        int dash = name.IndexOf('-');
                        result.Add(dash >= 0 ? name.Substring(dash + 1) : name);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>True if this PCI GPU has the internal panel (eDP) connected.</summary>
    public static bool HasInternalDisplay(string gpuSysPath)
    {
        var displays = FindConnectedDisplays(gpuSysPath);
        return displays.Any(d => d.StartsWith("eDP", StringComparison.OrdinalIgnoreCase));
    }

    public static readonly string SudoPath = ResolveSudoPath();

    // Re-resolved per call so a Repair that moves the helper applies without restart.
    public static string GpuHelperPath => ResolveGpuHelperPath();

    /// <summary>Where gpu-helper should be installed on this machine.
    /// Sealed-root SteamOS cannot write /usr/local; /etc is a writable overlay.</summary>
    internal static string DefaultGpuHelperInstallPath =>
        ImmutableOs.IsSteamOs && ImmutableOs.IsRootReadOnly()
            ? "/etc/ghelper/gpu-helper"
            : "/usr/local/lib/ghelper/gpu-helper";

    private static string ResolveGpuHelperPath()
    {
        // NixOS: module puts a Nix-native gpu-helper on PATH
        var nixPath = NixOS.ResolveGpuHelper();
        if (nixPath != null)
            return nixPath;

        // /etc/ghelper is the sealed-root SteamOS location (writable overlay).
        // TODO needs to be removed at some point. /opt/ghelper is the legacy user-owned location, used only until Repair.
        foreach (var p in new[] { "/usr/local/lib/ghelper/gpu-helper", "/etc/ghelper/gpu-helper", "/opt/ghelper/gpu-helper" })
            if (System.IO.File.Exists(p))
                return p;

        return DefaultGpuHelperInstallPath;
    }

    public static string RyzenadjPath => ResolveRyzenadjPath();

    /// <summary>Where the bundled ryzenadj CLI should be installed.
    /// Sealed-root SteamOS cannot write /usr/local; /etc is a writable overlay.</summary>
    internal static string DefaultRyzenadjInstallPath =>
        ImmutableOs.IsSteamOs && ImmutableOs.IsRootReadOnly()
            ? "/etc/ghelper/ryzenadj"
            : "/usr/local/lib/ghelper/ryzenadj";

    private static string ResolveRyzenadjPath()
    {
        // NixOS: module puts the nixpkgs ryzenadj on PATH
        var nixPath = NixOS.ResolveRyzenadj();
        if (nixPath != null)
            return nixPath;

        // TODO needs to be removed at some point. /opt/ghelper is the legacy user-owned location, used only until Repair.
        foreach (var p in new[] { "/usr/local/lib/ghelper/ryzenadj", "/etc/ghelper/ryzenadj", "/opt/ghelper/ryzenadj" })
            if (System.IO.File.Exists(p))
                return p;

        return DefaultRyzenadjInstallPath;
    }

    private static string ResolveSudoPath()
    {
        foreach (var p in new[]
        {
            "/usr/bin/sudo",
            "/bin/sudo",
            "/usr/local/bin/sudo",
            "/run/wrappers/bin/sudo",
        })
        {
            if (System.IO.File.Exists(p))
                return p;
        }
        return "sudo";
    }

    /// <summary>Run a shell command and return stdout. Returns null on failure.</summary>
    public static string? RunCommand(string command, string args = "")
    {
        return RunCommandWithTimeout(command, args, 5000);
    }

    /// <summary>Run a bash script via pkexec. The script is passed as a single arg to bash -c,
    /// avoiding the whitespace splitting issue with ProcessStartInfo.Arguments</summary>
    public static string? RunPkexecBash(string script)
    {
        return RunCommandWithTimeout("pkexec", new[] { "bash", "-c", script }, 120000);
    }

    /// <summary>
    /// Run a privileged command using sudo NOPASSWD if available, otherwise
    /// fall back to pkexec (GUI auth dialog). Silent fast path when sudoers
    /// permits; GUI prompt only when sudo itself refuses (auth failure), NOT
    /// when the command ran via sudo but returned a non-zero exit code (e.g.
    /// systemd rate-limit, rmmod busy, etc.). Returns stdout on success or
    /// null on failure.
    /// </summary>
    public static string? RunSudoOrPkexec(string command, string[] args, int sudoTimeoutMs = 5000, int pkexecTimeoutMs = 60000, bool allowPkexec = true)
    {
        var sudoArgs = new string[args.Length + 2];
        sudoArgs[0] = "-n";
        sudoArgs[1] = command;
        Array.Copy(args, 0, sudoArgs, 2, args.Length);

        var (exitCode, stdout, stderr) = RunProcessWithStderr(SudoPath, sudoArgs, sudoTimeoutMs);
        if (exitCode == 0)
            return stdout;

        // Distinguish sudo auth/permission failure from command-side failure.
        // sudo (classic and sudo-rs) prefixes its own error messages with "sudo:".
        // If stderr starts with "sudo:" the issue is permission / auth - pkexec
        // can help. Any other stderr came from the command itself; re-running via
        // pkexec would produce the same failure and just add an unnecessary prompt.
        bool sudoRefused = stderr.StartsWith("sudo:", StringComparison.Ordinal)
                        || stderr.Contains("not allowed to execute", StringComparison.Ordinal)
                        || stderr.Contains("may not run sudo", StringComparison.Ordinal);

        if (!sudoRefused)
        {
            if (!string.IsNullOrEmpty(stderr))
                Helpers.Logger.WriteLine($"RunCommand({SudoPath} -n {command}) failed (command error, not auth): {stderr.Trim()}");
            return null;
        }

        if (!allowPkexec)
        {
            LogEscalationSkip(command, args);
            return null;
        }

        Helpers.Logger.WriteLine($"sudo -n {command} not permitted - falling back to pkexec");

        var pkArgs = new string[args.Length + 1];
        pkArgs[0] = command;
        Array.Copy(args, 0, pkArgs, 1, args.Length);
        return RunCommandWithTimeout("pkexec", pkArgs, pkexecTimeoutMs);
    }

    // Polling / auto-apply paths must never pop an auth dialog: with broken
    // sudoers a periodic pkexec fallback becomes an endless prompt loop
    // (issue #146). Logged once per subcommand.
    private static readonly HashSet<string> _escalationSkipLogged = new();

    private static void LogEscalationSkip(string command, string[] args)
    {
        string key = command + " " + (args.Length > 0 ? args[0] : "");
        lock (_escalationSkipLogged)
        {
            if (!_escalationSkipLogged.Add(key))
                return;
        }
        Helpers.Logger.WriteLine(
            $"sudo -n {key} refused; non-interactive path, skipping pkexec (fix sudoers via Updates > Install/Repair)");
    }

    /// <summary>
    /// Like <see cref="RunSudoOrPkexec"/> but also returns stderr and the exit
    /// code so callers can inspect command-side failures (e.g. an NVML error
    /// code reported by gpu-helper). stdout is null when the command failed.
    /// </summary>
    public static (string? stdout, string stderr, int exitCode) RunSudoOrPkexecEx(
        string command, string[] args, int sudoTimeoutMs = 5000, int pkexecTimeoutMs = 60000, bool allowPkexec = true)
    {
        var sudoArgs = new string[args.Length + 2];
        sudoArgs[0] = "-n";
        sudoArgs[1] = command;
        Array.Copy(args, 0, sudoArgs, 2, args.Length);

        var (exitCode, stdout, stderr) = RunProcessWithStderr(SudoPath, sudoArgs, sudoTimeoutMs);
        if (exitCode == 0)
            return (stdout, stderr, 0);

        bool sudoRefused = stderr.StartsWith("sudo:", StringComparison.Ordinal)
                        || stderr.Contains("not allowed to execute", StringComparison.Ordinal)
                        || stderr.Contains("may not run sudo", StringComparison.Ordinal);
        if (!sudoRefused)
            return (null, stderr, exitCode);

        if (!allowPkexec)
        {
            LogEscalationSkip(command, args);
            return (null, stderr, exitCode);
        }

        var pkArgs = new string[args.Length + 1];
        pkArgs[0] = command;
        Array.Copy(args, 0, pkArgs, 1, args.Length);
        var (pkExit, pkOut, pkErr) = RunProcessWithStderr("pkexec", pkArgs, pkexecTimeoutMs);
        return (pkExit == 0 ? pkOut : null, pkErr, pkExit);
    }

    /// <summary>
    /// Like <see cref="RunSudoOrPkexecEx"/> but stdout is preserved even when
    /// the command exits non-zero. For tools like ryzenadj that report per-item
    /// results on stdout and exit non-zero if any single item failed.
    /// </summary>
    public static (string stdout, string stderr, int exitCode) RunSudoOrPkexecRaw(
        string command, string[] args, int sudoTimeoutMs = 5000, int pkexecTimeoutMs = 60000, bool allowPkexec = true)
    {
        var sudoArgs = new string[args.Length + 2];
        sudoArgs[0] = "-n";
        sudoArgs[1] = command;
        Array.Copy(args, 0, sudoArgs, 2, args.Length);

        var (exitCode, stdout, stderr) = RunProcessWithStderr(SudoPath, sudoArgs, sudoTimeoutMs);

        bool sudoRefused = exitCode != 0
                        && (stderr.StartsWith("sudo:", StringComparison.Ordinal)
                         || stderr.Contains("not allowed to execute", StringComparison.Ordinal)
                         || stderr.Contains("may not run sudo", StringComparison.Ordinal));
        if (!sudoRefused)
            return (stdout, stderr, exitCode);

        if (!allowPkexec)
        {
            LogEscalationSkip(command, args);
            return (stdout, stderr, exitCode);
        }

        var pkArgs = new string[args.Length + 1];
        pkArgs[0] = command;
        Array.Copy(args, 0, pkArgs, 1, args.Length);
        var (pkExit, pkOut, pkErr) = RunProcessWithStderr("pkexec", pkArgs, pkexecTimeoutMs);
        return (pkOut, pkErr, pkExit);
    }

    // Test runs must never execute privileged commands on the host.
    private static bool BlockedInTests(string command) =>
        (command == SudoPath || command == "pkexec")
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHELPER_TEST_ROOT"));

    /// <summary>Run a command and return (exitCode, stdout, stderr). Never returns null.</summary>
    private static (int exitCode, string stdout, string stderr) RunProcessWithStderr(
        string command, string[] args, int timeoutMs)
    {
        if (BlockedInTests(command))
            return (-1, "", "blocked in tests");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return (-1, "", "process start returned null");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            if (!outTask.Wait(timeoutMs))
            {
                try
                { proc.Kill(); }
                catch { }
                Helpers.Logger.WriteLine($"RunCommand timeout: {command} {string.Join(" ", args)}");
                return (-1, "", "timeout");
            }

            proc.WaitForExit(100);
            return (proc.ExitCode, outTask.Result.Trim(), errTask.IsCompleted ? errTask.Result.Trim() : "");
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>Run a command with explicit args (no whitespace splitting).</summary>
    public static string? RunCommandWithTimeout(string command, string[] args, int timeoutMs)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return RunProcess(psi, $"{command} {string.Join(" ", args)}", timeoutMs);
    }

    /// <summary>Run a shell command with specified timeout (milliseconds).</summary>
    public static string? RunCommandWithTimeout(string command, string args, int timeoutMs)
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
        return RunProcess(psi, $"{command} {args}", timeoutMs);
    }

    private static string? RunProcess(System.Diagnostics.ProcessStartInfo psi, string fullCommand, int timeoutMs)
    {
        if (BlockedInTests(psi.FileName))
            return null;
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            if (outputTask.Wait(timeoutMs))
            {
                var output = outputTask.Result.Trim();
                var errorOutput = errorTask.IsCompleted ? errorTask.Result.Trim() : "";

                proc.WaitForExit(100); // Give a moment for exit code

                if (proc.ExitCode != 0 && !string.IsNullOrEmpty(errorOutput))
                {
                    Helpers.Logger.WriteLine($"RunCommand({fullCommand}) failed with exit code {proc.ExitCode}: {errorOutput}");
                }

                return proc.ExitCode == 0 ? output : null;
            }
            else
            {
                try
                {
                    proc.Kill();
                    Helpers.Logger.WriteLine($"RunCommand timeout: {fullCommand}");
                }
                catch { /* Ignore kill errors */ }
                return null;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"SysfsHelper.RunCommand({fullCommand}) failed", ex);
            return null;
        }
    }
}

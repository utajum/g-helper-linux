namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Central definition of all ASUS WMI sysfs attributes.
/// Maps between legacy asus-nb-wmi sysfs names and asus-armoury firmware-attribute names.
///
/// Legacy sysfs:       /sys/devices/platform/asus-nb-wmi/{LegacyName}
/// Firmware-attributes: /sys/class/firmware-attributes/asus-armoury/attributes/{FwAttrName}/current_value
///
/// Most attributes have the same name in both interfaces. Two have different names:
///   ppt_fppt (legacy)  →  ppt_pl3_fppt (firmware-attributes)
///   panel_od (legacy)   →  panel_overdrive (firmware-attributes)
///
/// Source: Linux kernel drivers/platform/x86/asus-wmi.c + asus-armoury.c
/// </summary>
public sealed class AttrDef
{
    /// <summary>Name used in legacy asus-nb-wmi sysfs interface.</summary>
    public string LegacyName { get; }

    /// <summary>Name used in asus-armoury firmware-attributes interface (may differ from legacy).</summary>
    public string FwAttrName { get; }

    /// <summary>True if the legacy and firmware-attribute names differ.</summary>
    public bool HasAlias => LegacyName != FwAttrName;

    /// <summary>Human-readable description for diagnostics.</summary>
    public string Description { get; }

    public AttrDef(string legacyName, string? fwAttrName = null, string description = "")
    {
        LegacyName = legacyName;
        FwAttrName = fwAttrName ?? legacyName;
        Description = description;
    }

    /// <summary>Implicit conversion to string returns the legacy name for backward compatibility.</summary>
    public static implicit operator string(AttrDef attr) => attr.LegacyName;

    public override string ToString() => HasAlias ? $"{LegacyName} (fw: {FwAttrName})" : LegacyName;
}

/// <summary>
/// All known ASUS WMI attributes, used as the single source of truth for attribute names
/// across the entire codebase. All sysfs resolution goes through these definitions.
/// </summary>
public static class AsusAttributes
{
    // ── Performance / Thermal ──

    public static readonly AttrDef ThrottleThermalPolicy = new("throttle_thermal_policy",
        description: "Performance mode (quiet/balanced/performance)");

    // ── PPT Power Limits ──

    public static readonly AttrDef PptPl1Spl = new("ppt_pl1_spl",
        description: "PL1 sustained power limit");

    public static readonly AttrDef PptPl2Sppt = new("ppt_pl2_sppt",
        description: "PL2 short burst power limit");

    public static readonly AttrDef PptFppt = new("ppt_fppt", fwAttrName: "ppt_pl3_fppt",
        description: "fPPT fast boost power limit");

    public static readonly AttrDef PptApuSppt = new("ppt_apu_sppt",
        description: "APU SPPT power limit");

    public static readonly AttrDef PptPlatformSppt = new("ppt_platform_sppt",
        description: "Platform SPPT power limit");

    // ── NVIDIA GPU ──

    public static readonly AttrDef NvDynamicBoost = new("nv_dynamic_boost",
        description: "NVIDIA dynamic boost");

    public static readonly AttrDef NvTempTarget = new("nv_temp_target",
        description: "NVIDIA temperature target");

    // Armoury-only (no legacy equivalent)
    public static readonly AttrDef NvBaseTgp = new("nv_base_tgp",
        description: "NVIDIA base TGP (read-only)");

    public static readonly AttrDef NvTgp = new("nv_tgp",
        description: "NVIDIA settable TGP");

    // ── GPU Mode ──

    public static readonly AttrDef DgpuDisable = new("dgpu_disable",
        description: "dGPU power (Eco mode)");

    public static readonly AttrDef GpuMuxMode = new("gpu_mux_mode",
        description: "GPU MUX switch");

    public static readonly AttrDef EgpuEnable = new("egpu_enable",
        description: "eGPU enable");

    // ── Display ──

    public static readonly AttrDef PanelOd = new("panel_od", fwAttrName: "panel_overdrive",
        description: "Panel overdrive");

    public static readonly AttrDef MiniLedMode = new("mini_led_mode",
        description: "Mini LED mode");

    // ── System ──

    public static readonly AttrDef BootSound = new("boot_sound",
        description: "Boot sound");

    // ── All known attributes (for diagnostics enumeration) ──

    public static readonly AttrDef[] All =
    {
        ThrottleThermalPolicy,
        PptPl1Spl, PptPl2Sppt, PptFppt, PptApuSppt, PptPlatformSppt,
        NvDynamicBoost, NvTempTarget, NvBaseTgp, NvTgp,
        DgpuDisable, GpuMuxMode, EgpuEnable,
        PanelOd, MiniLedMode,
        BootSound,
    };

    // Lookup cache: legacy name → AttrDef (built once on first use)
    private static Dictionary<string, AttrDef>? _byLegacyName;

    /// <summary>
    /// Find an AttrDef by its legacy sysfs name. Returns null if not a known attribute.
    /// Used by SysfsHelper.ResolveAttrPath(string) to transparently handle aliases.
    /// </summary>
    public static AttrDef? FindByLegacyName(string legacyName)
    {
        if (_byLegacyName == null)
        {
            _byLegacyName = new Dictionary<string, AttrDef>();
            foreach (var attr in All)
                _byLegacyName[attr.LegacyName] = attr;
        }
        return _byLegacyName.TryGetValue(legacyName, out var result) ? result : null;
    }
}

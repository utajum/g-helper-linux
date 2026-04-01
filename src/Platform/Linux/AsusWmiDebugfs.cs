using System.Text.RegularExpressions;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Raw ACPI/WMI access via the asus-nb-wmi kernel debugfs interface.
///
/// The kernel's asus-wmi driver exposes DSTS (device status) and DEVS (device set)
/// methods via debugfs at /sys/kernel/debug/asus-nb-wmi/. This bypasses the sysfs
/// presence-bit check that prevents some 2020-2021 models from exposing dgpu_disable
/// despite the firmware supporting GPUEco (device 0x00090020).
///
/// Requires root — all calls use pkexec. Must be explicitly enabled via "raw_wmi" config.
///
/// ACPI device IDs (from linux/platform_data/x86/asus-wmi.h + Windows g-helper):
///   0x00090020 — dGPU disable / GPUEco (ROG/TUF)
///   0x00090120 — dGPU disable / GPUEco (Vivobook/Zenbook)
///   0x00090016 — GPU MUX switch (ROG/TUF)
///   0x00090026 — GPU MUX switch (Vivobook)
///   0x00090018 — eGPU connected (read-only)
///   0x00090019 — eGPU enable/disable
///   0x00120099 — dGPU base TGP watts (read-only)
/// </summary>
public static class AsusWmiDebugfs
{
    private const string DebugfsDir = "/sys/kernel/debug/asus-nb-wmi";

    // GPU-related ACPI device IDs
    public const uint DEVID_DGPU           = 0x00090020;
    public const uint DEVID_DGPU_VIVO      = 0x00090120;
    public const uint DEVID_GPU_MUX        = 0x00090016;
    public const uint DEVID_GPU_MUX_VIVO   = 0x00090026;
    public const uint DEVID_EGPU_CONNECTED = 0x00090018;
    public const uint DEVID_EGPU           = 0x00090019;
    public const uint DEVID_DGPU_BASE_TGP  = 0x00120099;

    // All probed device IDs (order matters — matches output parsing)
    private static readonly uint[] ProbeIds =
    {
        DEVID_DGPU, DEVID_DGPU_VIVO, DEVID_GPU_MUX, DEVID_GPU_MUX_VIVO,
        DEVID_EGPU_CONNECTED, DEVID_EGPU, DEVID_DGPU_BASE_TGP
    };
    private static readonly string[] ProbeLabels =
    {
        "GPUEco", "GPUEco Vivo", "GPU MUX", "GPU MUX Vivo",
        "eGPU Connected", "eGPU Enable", "dGPU Base TGP"
    };

    // DSTS response: bit 16 = device present, bit 0 = current state
    private const uint PRESENCE_BIT = 0x00010000;

    private static readonly Regex ResultRegex = new(@"=\s*0x([0-9A-Fa-f]+)", RegexOptions.Compiled);

    // Probe cache — populated by ProbeAll(), used by IsDevicePresent() and LogProbeResults()
    private static Dictionary<uint, uint?>? _probeCache;

    /// <summary>
    /// True if asus-nb-wmi module is loaded (guarantees debugfs dir exists).
    /// We check the module sysfs instead of debugfs dir because debugfs is
    /// root-only (0700) and Directory.Exists() fails for non-root users.
    /// </summary>
    public static bool IsAvailable() => Directory.Exists("/sys/module/asus_nb_wmi");

    // ── Startup probe (single pkexec call) ──────────────────────────────

    /// <summary>
    /// Probe all GPU-related ACPI device IDs in a single pkexec call.
    /// Results are cached for IsDevicePresent() and LogProbeResults().
    /// Call once at startup when raw_wmi is enabled.
    /// </summary>
    public static void ProbeAll()
    {
        _probeCache = new Dictionary<uint, uint?>();

        // Build a single shell command that probes all device IDs
        var parts = new List<string>();
        foreach (uint id in ProbeIds)
            parts.Add($"echo 0x{id:X8} > {DebugfsDir}/dev_id && cat {DebugfsDir}/dsts 2>&1");

        string script = string.Join("; ", parts);
        string? output = SysfsHelper.RunPkexecBash(script);

        if (output == null)
        {
            Logger.WriteLine("Raw WMI: ProbeAll failed (pkexec returned null)");
            return;
        }

        // Parse output — one DSTS result per line
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < ProbeIds.Length; i++)
            _probeCache[ProbeIds[i]] = i < lines.Length ? ParseResult(lines[i]) : null;
    }

    /// <summary>
    /// Log cached probe results. Call after ProbeAll().
    /// No pkexec — reads from cache only.
    /// </summary>
    public static void LogProbeResults()
    {
        if (_probeCache == null) return;

        for (int i = 0; i < ProbeIds.Length; i++)
        {
            uint id = ProbeIds[i];
            string label = ProbeLabels[i];

            if (!_probeCache.TryGetValue(id, out var result) || result == null)
            {
                Logger.WriteLine($"  Raw WMI DSTS(0x{id:X8}) {label}: not supported");
                continue;
            }

            bool present = (result.Value & PRESENCE_BIT) != 0;
            int state = (int)(result.Value & 0x01);
            Logger.WriteLine($"  Raw WMI DSTS(0x{id:X8}) {label}: 0x{result.Value:X} (present={present}, state={state})");
        }
    }

    // ── Presence check (cached) ─────────────────────────────────────────

    /// <summary>
    /// True if DSTS for this device ID returned with the presence bit set.
    /// Uses cache from ProbeAll() if available — no pkexec call.
    /// Falls back to a live DSTS call (1 pkexec) if cache is not populated.
    /// </summary>
    public static bool IsDevicePresent(uint deviceId)
    {
        if (_probeCache != null && _probeCache.TryGetValue(deviceId, out var cached))
            return cached.HasValue && (cached.Value & PRESENCE_BIT) != 0;

        // Cache miss — live probe (1 pkexec)
        uint? r = Dsts(deviceId);
        return r.HasValue && (r.Value & PRESENCE_BIT) != 0;
    }

    // ── Live DSTS/DEVS calls (1 pkexec each) ────────────────────────────

    /// <summary>
    /// Call DSTS(deviceId) — read device status via debugfs.
    /// Returns the raw uint result, or null on failure. Requires 1 pkexec call.
    /// </summary>
    public static uint? Dsts(uint deviceId)
    {
        string script = $"echo 0x{deviceId:X8} > {DebugfsDir}/dev_id && cat {DebugfsDir}/dsts";
        string? output = SysfsHelper.RunPkexecBash(script);
        return ParseResult(output);
    }

    /// <summary>
    /// Call DEVS(deviceId, ctrlParam) — set device state via debugfs.
    /// Returns the raw uint result, or null on failure. Requires 1 pkexec call.
    /// </summary>
    public static uint? Devs(uint deviceId, uint ctrlParam)
    {
        string script = $"echo 0x{deviceId:X8} > {DebugfsDir}/dev_id " +
                        $"&& echo {ctrlParam} > {DebugfsDir}/ctrl_param " +
                        $"&& cat {DebugfsDir}/devs";
        string? output = SysfsHelper.RunPkexecBash(script);
        return ParseResult(output);
    }

    // ── Diagnostics (no pkexec) ─────────────────────────────────────────

    /// <summary>
    /// Return diagnostic info about debugfs availability and raw_wmi config.
    /// Does NOT call pkexec — safe to include in diagnostics report.
    /// </summary>
    public static string GetDiagnostics()
    {
        var lines = new List<string>();
        lines.Add($"  asus-nb-wmi module: {(IsAvailable() ? "loaded" : "not found")}");
        lines.Add($"  raw_wmi config: {(AppConfig.Is("raw_wmi") ? "enabled" : "disabled")}");
        return string.Join("\n", lines);
    }

    // ── Parsing ─────────────────────────────────────────────────────────

    /// <summary>Parse "DSTS(0xNNNNN) = 0xNNNNN" or "DEVS(0xN, 0xN) = 0xN" output.</summary>
    private static uint? ParseResult(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = ResultRegex.Match(output);
        if (!match.Success) return null;
        try { return Convert.ToUInt32(match.Groups[1].Value, 16); }
        catch { return null; }
    }
}

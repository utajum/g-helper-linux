namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// AMD Ryzen SMU power/temp/current tuning via the upstream ryzenadj CLI
/// (FlyGoat/RyzenAdj, bundled static build; nixpkgs package on NixOS).
/// Invoked elevated (sudo NOPASSWD rule installed by the app). Usage model
/// mirrors Ryzen Controller and Universal x86 Tuning Utility:
///   - current limits are read from one `ryzenadj -i` table
///   - all pending values are applied in one batched invocation
///   - values are clamped to Ryzen Controller's per-option bounds
///   - applied values persist in config and re-apply at app start
/// Separate from RyzenSmu.cs, which drives the Curve Optimizer through the
/// ryzen_smu kernel driver.
/// </summary>
public static class RyzenPower
{
    private static HashSet<string>? _supported;
    private static Dictionary<string, float>? _limits;
    private static bool _probed;

    // Serializes probe/apply state across the startup re-apply task, mode
    // auto-apply and the Fans window. Monitor is reentrant, so the compound
    // flows (ResetToStock -> Apply) nest fine.
    private static readonly object _sync = new();

    // Params ryzenadj reported as unsupported on this family/SMU. Learned
    // from apply output; survives re-probes so rejected params stay hidden.
    private static readonly HashSet<string> _confirmedUnsupported = new();

    /// <summary>CPU family reported by ryzenadj (e.g. "Hawk Point").</summary>
    public static string? Family { get; private set; }

    /// <summary>Params settable but absent from the -i table, so they cannot
    /// be probed by reading. ryzenadj reports per-arg errors on apply.</summary>
    private static readonly string[] UnprobeableParams = ["min-gfxclk", "max-gfxclk"];

    /// <summary>Families whose SMU accepts the gfxclk pair (ryzenadj
    /// set_min/max_gfxclk_freq); every newer APU rejects it (#191).</summary>
    private static readonly string[] GfxClkFamilies = ["Raven", "Picasso", "Dali", "Lucienne"];

    /// <summary>
    /// Sane slider defaults for params the PM table reports as 0/nan,
    /// taken from Ryzen Controller's option definitions (display units).
    /// </summary>
    public static readonly Dictionary<string, float> Defaults = new()
    {
        ["stapm-limit"] = 25,     // W
        ["fast-limit"] = 25,      // W
        ["slow-limit"] = 10,      // W
        ["apu-slow-limit"] = 25,  // W
        ["stapm-time"] = 900,     // s
        ["slow-time"] = 60,       // s
        ["tctl-temp"] = 85,       // C
        ["apu-skin-temp"] = 45,   // C
        ["dgpu-skin-temp"] = 45,  // C
        ["vrm-current"] = 45,     // A
        ["vrmsoc-current"] = 45,  // A
        ["vrmmax-current"] = 45,  // A
        ["vrmsocmax-current"] = 45, // A
        ["min-gfxclk"] = 400,     // MHz
        ["max-gfxclk"] = 2200,    // MHz
    };

    /// <summary>
    /// Per-param bounds in display units, from Ryzen Controller's option
    /// definitions (skin temps from UXTU presets; not defined in RC).
    /// </summary>
    public static readonly Dictionary<string, (float Min, float Max)> Bounds = new()
    {
        ["stapm-limit"] = (3, 100),
        ["fast-limit"] = (3, 100),
        ["slow-limit"] = (3, 100),
        ["apu-slow-limit"] = (3, 100),
        ["stapm-time"] = (1, 3600),
        ["slow-time"] = (1, 1000),
        ["tctl-temp"] = (50, 105),
        ["apu-skin-temp"] = (40, 100),
        ["dgpu-skin-temp"] = (40, 100),
        ["vrm-current"] = (20, 150),
        ["vrmsoc-current"] = (20, 150),
        ["vrmmax-current"] = (20, 150),
        ["vrmsocmax-current"] = (20, 150),
        ["min-gfxclk"] = (400, 2200),
        ["max-gfxclk"] = (400, 2200),
    };

    /// <summary>Clamp a display-unit value to the Ryzen Controller bounds.</summary>
    public static float Clamp(string param, float value)
        => Bounds.TryGetValue(param, out var b) ? Math.Clamp(value, b.Min, b.Max) : value;

    /// <summary>True when this machine has an AMD Ryzen (or Valve custom) APU
    /// vendor/model. Cheap /proc/cpuinfo check used to gate installing and
    /// advertising the ryzenadj binary; actual SMU support is probed later.</summary>
    public static bool IsRyzenCpu { get; } = DetectRyzenCpu();

    private static bool DetectRyzenCpu()
    {
        try
        {
            bool amd = false;
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("vendor_id", StringComparison.Ordinal))
                    amd = line.Contains("AuthenticAMD", StringComparison.Ordinal);
                else if (amd && line.StartsWith("model name", StringComparison.Ordinal))
                    return line.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Custom APU", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public static HashSet<string>? SupportedParams => _probed ? _supported : Probe();

    public static bool Available => SupportedParams is { Count: > 0 };

    public static bool IsSupported(string param)
        => SupportedParams?.Contains(param) == true;

    /// <summary>Log to the app log and to the systemd journal (tag
    /// "ryzenadj", like gpu-helper's syslog trail) so privileged SMU
    /// actions are auditable via journalctl -t ryzenadj.</summary>
    private static void Log(string msg)
    {
        Helpers.Logger.WriteLine("RyzenPower: " + msg);
        try
        { SysfsHelper.RunCommandWithTimeout("logger", new[] { "-t", "ryzenadj", msg }, 2000); }
        catch { }
    }

    /// <summary>
    /// Run `ryzenadj -i` and parse the PM table. Fills the supported-param
    /// set and the current limit values in one pass. Non-interactive: never
    /// pops an auth dialog (broken sudoers must not loop prompts).
    /// </summary>
    public static HashSet<string>? Probe()
    {
        lock (_sync)
            return ProbeLocked();
    }

    private static HashSet<string>? ProbeLocked()
    {
        _probed = true;
        _supported = null;
        _limits = null;
        if (!IsRyzenCpu)
            return null;
        try
        {
            var (stdout, _, exitCode) = SysfsHelper.RunSudoOrPkexecEx(
                SysfsHelper.RyzenadjPath, new[] { "-i" }, allowPkexec: false);
            if (exitCode != 0 || string.IsNullOrEmpty(stdout))
                return null;

            var supported = new HashSet<string>();
            var limits = new Dictionary<string, float>();
            foreach (var line in stdout.Split('\n'))
            {
                if (line.StartsWith("CPU Family:", StringComparison.Ordinal))
                {
                    Family = line["CPU Family:".Length..].Trim();
                    continue;
                }
                // Table rows: | STAPM LIMIT | 45.000 | stapm-limit |
                if (!line.StartsWith("|", StringComparison.Ordinal))
                    continue;
                var cells = line.Split('|', StringSplitOptions.TrimEntries);
                // Split yields: "", name, value, param, ""
                if (cells.Length < 5)
                    continue;
                string param = cells[3];
                if (param.Length == 0
                    || !param.Any(char.IsAsciiLetterLower)
                    || !param.All(c => char.IsAsciiLetterLower(c) || c == '-'))
                    continue; // header, |---| separator, VALUE rows, "power-saving /"
                if (param == "max-performance" || param == "power-saving")
                    continue; // CCLK setpoint pseudo rows, not sliders
                if (_confirmedUnsupported.Contains(param))
                    continue;
                supported.Add(param);
                if (float.TryParse(cells[2],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val) && val > 0)
                    limits[param] = val;
            }

            if (supported.Count > 0)
            {
                // Settable on old APUs but absent from the -i table; apply
                // output confirms/denies them per family.
                if (Family != null && GfxClkFamilies.Contains(Family))
                    foreach (var p in UnprobeableParams)
                        if (!_confirmedUnsupported.Contains(p))
                            supported.Add(p);
                _supported = supported;
                _limits = limits;
                SnapshotStock(limits);
                Log($"probe: family={Family} supported={string.Join(",", supported)}");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("RyzenPower: probe failed", ex);
            _supported = null;
        }
        return _supported;
    }

    // Stock limits snapshot. The first probe of each boot sees the BIOS
    // values (the app's own re-apply runs after that probe), so record them
    // for the Reset button. Boot id gates re-snapshots within a session.

    private static string StockKey(string param) => "ryzen_stock_" + param.Replace('-', '_');

    /// <summary>Stock (boot-time) display value for a param, or null.</summary>
    public static int? StockValue(string param)
    {
        int v = Helpers.AppConfig.Get(StockKey(param));
        return v < 0 ? null : v;
    }

    private static void SnapshotStock(Dictionary<string, float> limits)
    {
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            if (Helpers.AppConfig.GetString("ryzen_stock_boot_id") == bootId)
                return;
            foreach (var (param, val) in limits)
                Helpers.AppConfig.Set(StockKey(param), (int)Math.Round(val));
            Helpers.AppConfig.Set("ryzen_stock_boot_id", bootId);
            Log("stock limits captured: " + string.Join(" ",
                limits.Select(kv => $"{kv.Key}={kv.Value:0}")));
        }
        catch { }
    }

    /// <summary>Current limits from the last probe (display units: W/A/C/s).
    /// Re-probes if never probed.</summary>
    public static Dictionary<string, float>? ReadInfo()
    {
        if (!_probed)
            Probe();
        return _limits;
    }

    /// <summary>Force the next SupportedParams/ReadInfo to re-run ryzenadj -i.</summary>
    public static void Invalidate() => _probed = false;

    /// <summary>
    /// Apply a batch of settings in ONE ryzenadj invocation (raw units:
    /// mW/mA for power and current, plain for temp/time/clock). Params the
    /// SMU reports as unsupported are dropped and the rest is re-applied,
    /// so one bad param cannot block the batch. Transient SMU rejections
    /// are retried (Ryzen Controller behavior). Returns true when every
    /// remaining arg was accepted.
    /// </summary>
    public static bool Apply(IReadOnlyCollection<(string Param, int Raw)> settings, bool interactive = true)
    {
        lock (_sync)
            return ApplyLocked(settings, interactive);
    }

    private static bool ApplyLocked(IReadOnlyCollection<(string Param, int Raw)> settings, bool interactive)
    {
        var pending = settings
            .Where(s => !_confirmedUnsupported.Contains(s.Param))
            .ToList();
        if (pending.Count == 0)
            return false;

        // Attempt budget covers unsupported-strip reruns + transient retries.
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var args = pending.Select(s => $"--{s.Param}={s.Raw}").ToArray();
            var (stdout, stderr, exitCode) = SysfsHelper.RunSudoOrPkexecRaw(
                SysfsHelper.RyzenadjPath, args, allowPkexec: interactive);

            if (exitCode == 0)
            {
                Log($"applied {string.Join(" ", args)}");
                return true;
            }

            bool transient = false;
            bool stripped = false;
            foreach (var line in stdout.Split('\n'))
            {
                // "set_max_gfxclk_freq is not supported on this family" or
                // "... on this SMU": permanent, drop and re-apply the rest.
                // "set_X is rejected by SMU": transient, retry helps.
                // The token is the internal setter name, which may carry a
                // suffix over the CLI param (max_gfxclk_freq vs max-gfxclk,
                // apu_skin_temp_limit vs apu-skin-temp): prefix-match it
                // against the batch.
                if (!line.StartsWith("set_", StringComparison.Ordinal))
                    continue;
                if (line.Contains("is not supported", StringComparison.Ordinal))
                {
                    int end = line.IndexOf(' ');
                    if (end <= 4)
                        continue;
                    string token = line[4..end].Replace('_', '-');
                    var hit = pending.FirstOrDefault(s =>
                        token == s.Param
                        || token.StartsWith(s.Param + "-", StringComparison.Ordinal));
                    if (hit.Param != null)
                    {
                        _confirmedUnsupported.Add(hit.Param);
                        _supported?.Remove(hit.Param);
                        pending.Remove(hit);
                        stripped = true;
                        Log($"{line.Trim()} (dropped {hit.Param})");
                    }
                }
                else if (line.Contains("rejected by SMU", StringComparison.Ordinal))
                {
                    transient = true;
                    Log($"{line.Trim()} (attempt {attempt})");
                }
            }

            if (pending.Count == 0)
            {
                Log("no supported params left in batch");
                return false;
            }
            if (!stripped && !transient)
            {
                Log($"apply failed exit={exitCode} stderr={stderr.Trim()}");
                return false;
            }
        }
        Log("apply gave up after 4 attempts");
        return false;
    }

    /// <summary>Set a single parameter. Value is raw (mW for power, mA for
    /// current, C/s/MHz plain).</summary>
    public static bool Set(string param, int value)
        => Apply([(param, value)]);

    // Config persistence (display units). Mirrors Ryzen Controller's
    // apply-on-start preset / UXTU's ApplyOnStart command string.

    private static string ConfigKey(string param) => "ryzen_" + param.Replace('-', '_');

    /// <summary>Saved display-unit value for a param, or null.</summary>
    public static int? SavedValue(string param)
    {
        int v = Helpers.AppConfig.Get(ConfigKey(param));
        return v < 0 ? null : v;
    }

    public static void SaveValue(string param, int displayValue)
        => Helpers.AppConfig.Set(ConfigKey(param), displayValue);

    /// <summary>Raw multiplier for a param (display -> mW/mA).</summary>
    public static int RawScale(string param)
        => param.EndsWith("-limit", StringComparison.Ordinal)
        || param.EndsWith("-current", StringComparison.Ordinal)
            ? 1000 : 1;

    /// <summary>
    /// Re-apply saved values at app start (one batch, non-interactive so a
    /// broken sudoers cannot spawn an auth prompt loop). No-op when nothing
    /// was saved or the CPU is not a Ryzen APU.
    /// </summary>
    public static void ApplySavedOnStart()
    {
        if (!IsRyzenCpu)
            return;
        var batch = new List<(string, int)>();
        foreach (var param in Defaults.Keys)
        {
            int? saved = SavedValue(param);
            if (saved == null)
                continue;
            int display = (int)Clamp(param, saved.Value);
            batch.Add((param, display * RawScale(param)));
        }
        if (batch.Count == 0)
            return;
        // Probe first: on a fresh boot the table still holds the BIOS
        // values, and this snapshot is what the Reset button restores.
        if (Probe() == null)
            return;
        Log($"re-applying {batch.Count} saved value(s) at start");
        Apply(batch, interactive: false);
    }

    /// <summary>
    /// Reset to stock: forget all saved values (no more re-apply at start)
    /// and restore the limits captured at boot. Params without a stock
    /// snapshot (e.g. the gfxclk pair, absent from the -i table) are only
    /// cleared; the SMU keeps them until reboot. Returns true when the
    /// restore batch was accepted.
    /// </summary>
    public static bool ResetToStock()
    {
        if (!IsRyzenCpu)
            return false;
        lock (_sync)
            return ResetToStockLocked();
    }

    private static bool ResetToStockLocked()
    {
        var batch = new List<(string, int)>();
        foreach (var param in Defaults.Keys)
        {
            Helpers.AppConfig.Set(ConfigKey(param), -1); // forget saved value
            if (!IsSupported(param))
                continue;
            int? stock = StockValue(param);
            if (stock == null)
                continue;
            batch.Add((param, stock.Value * RawScale(param)));
        }
        Invalidate();
        if (batch.Count == 0)
            return false;
        bool ok = Apply(batch);
        Invalidate();
        Log(ok ? "reset to stock limits" : "reset to stock failed");
        return ok;
    }

    /// <summary>State dump for the diagnostics report.</summary>
    public static string DebugDump()
    {
        lock (_sync)
            return DebugDumpLocked();
    }

    private static string DebugDumpLocked()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  ryzenadj: {SysfsHelper.RyzenadjPath}");
        sb.AppendLine($"  IsRyzenCpu: {IsRyzenCpu}");
        if (!IsRyzenCpu)
            return sb.ToString();
        bool available = Available; // probes on first call
        sb.AppendLine($"  available: {available}");
        sb.AppendLine($"  family: {Family ?? "n/a"}");
        if (_supported != null)
            sb.AppendLine($"  supported: {string.Join(",", _supported)}");
        if (_confirmedUnsupported.Count > 0)
            sb.AppendLine($"  dropped as unsupported: {string.Join(",", _confirmedUnsupported)}");
        if (_limits is { Count: > 0 })
            sb.AppendLine("  live limits: " + string.Join(" ",
                _limits.Select(kv => $"{kv.Key}={kv.Value:0.#}")));
        var saved = Defaults.Keys.Select(p => (p, v: SavedValue(p)))
            .Where(t => t.v != null).Select(t => $"{t.p}={t.v}").ToList();
        sb.AppendLine(saved.Count > 0
            ? "  saved (re-applied at start): " + string.Join(" ", saved)
            : "  saved (re-applied at start): none");
        var stock = Defaults.Keys.Select(p => (p, v: StockValue(p)))
            .Where(t => t.v != null).Select(t => $"{t.p}={t.v}").ToList();
        if (stock.Count > 0)
            sb.AppendLine("  stock (boot snapshot): " + string.Join(" ", stock));
        return sb.ToString();
    }

    /// <summary>Map PPT attribute name to ryzenadj parameter name.</summary>
    public static string? PptToParam(string attribute) => attribute switch
    {
        "ppt_pl1_spl" => "stapm-limit",
        "ppt_fppt" => "fast-limit",
        "ppt_pl2_sppt" => "slow-limit",
        "ppt_apu_sppt" => "apu-slow-limit",
        _ => null,
    };

    /// <summary>Try to set a PPT attribute via the SMU. Returns true if
    /// handled. Non-interactive: mode auto-apply must never pop an auth
    /// dialog (issue #146).</summary>
    public static bool TrySetPpt(string attribute, int watts)
    {
        var param = PptToParam(attribute);
        if (param == null || !IsSupported(param))
            return false;
        int raw = (int)Clamp(param, watts) * 1000; // watts to mW
        return Apply([(param, raw)], interactive: false);
    }
}

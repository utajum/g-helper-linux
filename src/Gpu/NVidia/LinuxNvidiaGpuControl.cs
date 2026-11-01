using System.Globalization;
using System.Text.RegularExpressions;
using GHelper.Linux.I18n;

using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.NVidia;

/// <summary>
/// Linux NVIDIA GPU control using:
///   - nvidia-smi CLI for monitoring and clock limits
///   - /sys/class/hwmon/ nvidia hwmon for temperature
///   - nvidia-settings for clock offsets (requires X11)
/// 
/// Requires nvidia proprietary driver to be installed.
/// nvidia-smi is the most reliable cross-version approach on Linux.
/// 
/// NVML P/Invoke is possible but nvidia-smi is always present when the driver is
/// installed and handles version compatibility automatically.
/// </summary>
public class LinuxNvidiaGpuControl : IGpuControl
{
    private string? _hwmonDir;
    private string? _gpuName;
    private bool _available;

    public string Vendor => "NVIDIA";

    public LinuxNvidiaGpuControl()
    {
        _hwmonDir = SysfsHelper.FindHwmonByName("nvidia");
        _gpuName = QueryGpuName();
        _available = !string.IsNullOrEmpty(_gpuName);

        if (_available)
        {
            Helpers.Logger.WriteLine($"NVIDIA GPU found: {_gpuName ?? "unknown"}");
        }
        else
        {
            Helpers.Logger.WriteLine("NVIDIA GPU not available (nvidia-smi not found or no GPU)");
        }
    }

    public bool IsAvailable() => _available;

    public string? GetGpuName() => _gpuName;

    /// <summary>
    /// Fast GPU temp read via gpu-helper nvml-temp (~5ms, no nvidia-smi fork).
    /// Returns the temperature in Celsius or -1 on failure.
    /// Usable as a static fallback from both ASUS and Lenovo WMI backends.
    /// </summary>
    public static int GetTempViaNvml()
    {
        if (GpuQueryGate.IsPaused)
            return -1;
        if (!Directory.Exists("/sys/module/nvidia"))
            return -1;
        if (ShouldSkipDgpuTelemetry())
            return -1;
        if (!Helpers.AppConfig.IsNotFalse("dgpu_monitor"))
            return -1;
        if (!NvidiaProcessScanner.EnsureHelper())
            return -1;
        try
        {
            // Periodic poll: never escalate interactively (issue #146).
            string? output = SysfsHelper.RunSudoOrPkexec(
                SysfsHelper.GpuHelperPath, new[] { "nvml-temp" }, allowPkexec: false);
            if (string.IsNullOrWhiteSpace(output))
                return -1;
            int temp = ParseNvmlTemp(output, out bool busy);
            if (busy)
                DgpuIdleBackoff.NoteBusy(Environment.TickCount64);
            return temp;
        }
        catch { }
        return -1;
    }

    /// <summary>Parse gpu-helper nvml-temp output: temp in C (-1 if absent), busy = util above 0.</summary>
    internal static int ParseNvmlTemp(string output, out bool busy)
    {
        int temp = -1;
        busy = false;
        foreach (var token in output.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("temp=", StringComparison.Ordinal) &&
                int.TryParse(token.AsSpan(5), out int t) && t > 0)
                temp = t;
            else if (token.StartsWith("util=", StringComparison.Ordinal) &&
                int.TryParse(token.AsSpan(5), out int u) && u > 0)
                busy = true;
        }
        return temp;
    }

    // Temperature

    public int? GetCurrentTemp()
    {
        // Method 1: hwmon sysfs (fastest, no process spawn)
        if (_hwmonDir != null)
        {
            int temp = SysfsHelper.ReadInt(Path.Combine(_hwmonDir, "temp1_input"), -1);
            if (temp > 0)
                return temp / 1000;
        }

        // Method 2: nvidia-smi
        var output = RunNvidiaSmi("--query-gpu=temperature.gpu", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int smiTemp))
            return smiTemp;

        return null;
    }

    // Utilization

    public int? GetGpuUse()
    {
        var output = RunNvidiaSmi("--query-gpu=utilization.gpu", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int usage))
        {
            if (usage > 0)
                DgpuIdleBackoff.NoteBusy(Environment.TickCount64);
            return usage;
        }

        return null;
    }

    // Clocks

    public int? GetCurrentClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.graphics", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int clock))
            return clock;

        return null;
    }

    public int? GetCurrentMemoryClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.memory", "--format=csv,noheader,nounits");
        if (output != null && int.TryParse(output.Trim(), out int clock))
            return clock;

        return null;
    }

    // Power

    public int? GetCurrentPower()
    {
        var output = RunNvidiaSmi("--query-gpu=power.draw", "--format=csv,noheader,nounits");
        if (output != null && double.TryParse(output.Trim(), CultureInfo.InvariantCulture, out double watts))
            return (int)Math.Round(watts);

        return null;
    }

    // Clock Offsets

    public void SetCoreClockOffset(int offsetMhz)
    {
        if (!_available)
            return;

        // nvidia-settings requires X11. On X11, this works:
        // nvidia-settings -a "[gpu:0]/GPUGraphicsClockOffsetAllPerformanceLevels=<offset>"
        // On Wayland, we need nvidia-smi or coolbits
        var result = SysfsHelper.RunCommand("nvidia-settings",
            $"-a \"[gpu:0]/GPUGraphicsClockOffsetAllPerformanceLevels={offsetMhz}\"");

        if (result == null)
        {
            // Fallback: try nvidia-smi lock-gpu-clocks with offset
            // This is less precise but works on Wayland
            Helpers.Logger.WriteLine($"NVIDIA: nvidia-settings not available, GPU core offset not set");
            return;
        }

        Helpers.Logger.WriteLine($"NVIDIA: Set core clock offset to {offsetMhz} MHz");
    }

    public void SetMemoryClockOffset(int offsetMhz)
    {
        if (!_available)
            return;

        var result = SysfsHelper.RunCommand("nvidia-settings",
            $"-a \"[gpu:0]/GPUMemoryTransferRateOffsetAllPerformanceLevels={offsetMhz}\"");

        if (result == null)
        {
            Helpers.Logger.WriteLine($"NVIDIA: nvidia-settings not available, GPU memory offset not set");
            return;
        }

        Helpers.Logger.WriteLine($"NVIDIA: Set memory clock offset to {offsetMhz} MHz");
    }

    /// <summary>
    /// Query GPU power limits: (defaultW, minW, maxW, enforcedW).
    /// Returns null if unavailable.
    /// </summary>
    public (int defaultW, int minW, int maxW, int enforcedW)? GetPowerLimits()
    {
        var output = RunNvidiaSmi(
            "--query-gpu=power.default_limit,power.min_limit,power.max_limit,enforced.power.limit",
            "--format=csv,noheader,nounits");
        if (output == null)
            return null;

        var parts = output.Split(',');
        if (parts.Length < 4)
            return null;

        double ParseW(string s)
        {
            s = s.Trim();
            if (double.TryParse(s, CultureInfo.InvariantCulture, out double v))
                return v;
            return -1;
        }

        var def = ParseW(parts[0]);
        var min = ParseW(parts[1]);
        var max = ParseW(parts[2]);
        var enf = ParseW(parts[3]);

        if (def < 0 || min < 0 || max < 0)
            return null;
        // Any card reporting a default over 1000W has no real power management.
        if (def > 1000)
            return null;
        return ((int)Math.Round(def), (int)Math.Round(min), (int)Math.Round(max), (int)Math.Round(enf));
    }

    /// <summary>
    /// Apply power limit and clock lock in a single pkexec call.
    /// clockLockMhz &lt;= 0 means reset clock lock.
    /// </summary>
    public void ApplyGpuSettings(int powerW, int clockLockMhz) => ApplyAll(powerW, clockLockMhz, null, null);

    // interactive: false for auto-apply paths (startup, mode change) so a
    // broken sudoers degrades to a logged skip instead of an auth dialog.
    public void ApplyAll(int? powerW, int clockLockMhz, int? coreOffsetMhz, int? memOffsetMhz, bool interactive = true)
    {
        if (!_available)
            return;

        int cmdCount = 0;
        int okCount = 0;
        string? nvmlResult = null;

        if (powerW != null)
        {
            var caps = GetCapabilities();
            if (caps.PowerLimit)
            {
                cmdCount++;
                var r = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "smi", "-pl", powerW.Value.ToString() }, allowPkexec: interactive);
                if (r != null)
                    okCount++;
                else
                    Helpers.Logger.WriteLine($"NVIDIA: nvidia-smi -pl {powerW.Value} FAILED");
            }
        }
        if (clockLockMhz > 0)
        {
            cmdCount++;
            int lock_ = Math.Clamp(clockLockMhz, 200, 3000);
            var r = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "smi", "-lgc", $"0,{lock_}" }, allowPkexec: interactive);
            if (r != null)
                okCount++;
            else
                Helpers.Logger.WriteLine($"NVIDIA: nvidia-smi -lgc 0,{lock_} FAILED");
        }
        else if (clockLockMhz == 0)
        {
            cmdCount++;
            var r = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "smi", "-rgc" }, allowPkexec: interactive);
            if (r != null)
                okCount++;
            else
                Helpers.Logger.WriteLine("NVIDIA: nvidia-smi -rgc FAILED");
        }
        if (coreOffsetMhz != null || memOffsetMhz != null)
        {
            if (!NvidiaProcessScanner.EnsureHelper())
            {
                Helpers.Logger.WriteLine(
                    "NVIDIA: gpu-helper not available - clock offsets skipped (re-run install script)");
            }
            else
            {
                cmdCount++;
                int reqCore = ClampCore(coreOffsetMhz ?? 0);
                int reqMem = ClampMem(memOffsetMhz ?? 0);
                var (stdout, stderr, _) = SysfsHelper.RunSudoOrPkexecEx(
                    SysfsHelper.GpuHelperPath,
                    new[] { "nvml-clocks", reqCore.ToString(), reqMem.ToString() },
                    allowPkexec: interactive);
                if (stdout != null)
                {
                    okCount++;
                    nvmlResult = stdout;
                    LastApplyResetRequired = false;
                    Helpers.Logger.WriteLine($"NVIDIA: nvml-clocks => {nvmlResult}");
                }
                else
                {
                    Helpers.Logger.WriteLine(
                        $"NVIDIA: nvml-clocks {reqCore} {reqMem} FAILED: {stderr}");
                    if (IsResetRequired(stderr))
                        HandleResetRequired(reqCore, reqMem);
                }
            }
        }

        if (cmdCount == 0)
            return;

        InvalidateNvmlCache();

        Helpers.Logger.WriteLine(
            $"NVIDIA: ApplyAll power={powerW?.ToString() ?? "-"}W "
            + $"lock={(clockLockMhz > 0 ? $"{clockLockMhz}MHz" : "unlocked")} "
            + $"coreOff={coreOffsetMhz?.ToString() ?? "-"} "
            + $"memOff={memOffsetMhz?.ToString() ?? "-"} "
            + $"({okCount}/{cmdCount} cmds OK via sudo or pkexec)");
    }

    /// <summary>
    /// True when the last clock-offset apply failed with NVML_ERROR_RESET_REQUIRED
    /// (the GPU firmware crashed from an unstable overclock). The Fans window uses
    /// this to highlight the "Recover dGPU" action.
    /// </summary>
    public static bool LastApplyResetRequired { get; private set; }

    /// <summary>Clear the reset-required flag after a successful recovery.</summary>
    public static void ResetLastApplyState() => LastApplyResetRequired = false;

    private static DateTime _lastResetNotifyUtc;

    private static bool IsResetRequired(string stderr) =>
        !string.IsNullOrEmpty(stderr)
        && (stderr.Contains("nvml-error=16", StringComparison.Ordinal)
            || stderr.Contains("RESET_REQUIRED", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The GPU reports NVML_ERROR_RESET_REQUIRED (Xid 62/154). Revert the offsets
    /// to 0 (best effort), zero the saved config so auto-apply stops re-sending the
    /// bad value, and notify the user once (debounced) - the only real recovery is
    /// a dGPU power-cycle (Eco -> Standard) or reboot.
    /// </summary>
    private void HandleResetRequired(int reqCore, int reqMem)
    {
        LastApplyResetRequired = true;

        if (reqCore != 0 || reqMem != 0)
        {
            // Try to clear the offending offset and stop it being re-applied.
            SysfsHelper.RunSudoOrPkexecEx(SysfsHelper.GpuHelperPath, new[] { "nvml-clocks", "0", "0" }, allowPkexec: false);
            Helpers.AppConfig.SetMode("gpu_clock_core", 0);
            Helpers.AppConfig.SetMode("gpu_clock_mem", 0);
        }

        if ((DateTime.UtcNow - _lastResetNotifyUtc) < TimeSpan.FromSeconds(30))
            return;
        _lastResetNotifyUtc = DateTime.UtcNow;

        Helpers.Logger.WriteLine(
            "NVIDIA: GPU in RESET_REQUIRED state (overclock crashed) - offsets reverted, power-cycle needed");
        App.System?.ShowNotification(
            Labels.Get("gpu_reset_required_title"),
            Labels.Get("gpu_reset_required_msg"),
            "dialog-error");
    }

    private readonly record struct NvmlInfo(
        string? Driver,
        (int min, int max)? CoreRange,
        (int min, int max)? MemRange,
        int? CoreOffset,
        int? MemOffset,
        bool LockGpu,
        bool LockMem,
        bool PowerMgmt,
        int? TempShutdown,
        int? TempSlowdown,
        string? Vbios,
        int? MemBusWidth);

    /// <summary>Per-tunable hardware support, so the UI only shows what the card
    /// can actually do.</summary>
    public readonly record struct GpuCapabilities(
        bool ClockOffset,
        bool PowerLimit,
        bool GpuClockLock,
        bool MemClockLock);

    private NvmlInfo? _nvmlCache;
    private DateTime _nvmlCacheAt;

    private static readonly TimeSpan NvmlCacheTtl = TimeSpan.FromSeconds(30);

    public void InvalidateNvmlCache() => _nvmlCacheAt = DateTime.MinValue;

    private NvmlInfo? QueryNvmlInfo()
    {
        if (!_available)
            return null;
        if (_nvmlCache != null && (DateTime.UtcNow - _nvmlCacheAt) < NvmlCacheTtl)
            return _nvmlCache;
        // Don't wake a suspended or idle dGPU to refresh NVML info; serve the
        // last cache (applied offsets/ranges stay valid across suspend). With no
        // cache yet, probe an awake dGPU anyway: mode apply needs the capabilities.
        if (ShouldSkipDgpuTelemetry() && (_nvmlCache != null || IsDgpuSuspended()))
            return _nvmlCache;
        if (!NvidiaProcessScanner.EnsureHelper())
            return null;

        // Capability probe: runs unprompted (startup, tab load), so no pkexec.
        string? output = SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "nvml-info" }, allowPkexec: false);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        string? driver = null, vbios = null;
        (int min, int max)? coreRange = null, memRange = null;
        int? coreOff = null, memOff = null;
        int? tempShutdown = null, tempSlowdown = null, memBusWidth = null;
        bool lockGpu = false, lockMem = false, powerMgmt = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "driver":
                    driver = val;
                    break;
                case "core-offset":
                    if (int.TryParse(val, out var co))
                        coreOff = co;
                    break;
                case "mem-offset":
                    if (int.TryParse(val, out var mo))
                        memOff = mo;
                    break;
                case "core-range":
                    coreRange = ParseRange(val);
                    break;
                case "mem-range":
                    memRange = ParseRange(val);
                    break;
                case "lock-gpu":
                    lockGpu = val == "1";
                    break;
                case "lock-mem":
                    lockMem = val == "1";
                    break;
                case "power-mgmt":
                    powerMgmt = val == "1";
                    break;
                case "temp-shutdown":
                    if (int.TryParse(val, out var ts))
                        tempShutdown = ts;
                    break;
                case "temp-slowdown":
                    if (int.TryParse(val, out var td))
                        tempSlowdown = td;
                    break;
                case "vbios":
                    vbios = val;
                    break;
                case "mem-bus-width":
                    if (int.TryParse(val, out var mbw))
                        memBusWidth = mbw;
                    break;
            }
        }
        var info = new NvmlInfo(driver, coreRange, memRange, coreOff, memOff,
            lockGpu, lockMem, powerMgmt, tempShutdown, tempSlowdown, vbios, memBusWidth);
        _nvmlCache = info;
        _nvmlCacheAt = DateTime.UtcNow;
        return info;
    }

    private static (int min, int max)? ParseRange(string s)
    {
        var p = s.Split(',');
        if (p.Length == 2 && int.TryParse(p[0].Trim(), out var a) && int.TryParse(p[1].Trim(), out var b))
            return (a, b);
        return null;
    }

    public bool IsClockOffsetSupported() => QueryNvmlInfo()?.CoreRange != null;

    /// <summary>What this card supports, derived from the cached nvml-info probe.</summary>
    public GpuCapabilities GetCapabilities()
    {
        var i = QueryNvmlInfo();
        if (i == null)
            return new GpuCapabilities(false, false, false, false);
        return new GpuCapabilities(
            ClockOffset: i.Value.CoreRange != null,
            PowerLimit: i.Value.PowerMgmt,
            GpuClockLock: i.Value.LockGpu,
            MemClockLock: i.Value.LockMem);
    }

    private (int min, int max) CoreCap()
    {
        int max = 250, min = -250;
        string n = _gpuName ?? "";
        if (n.Contains("5080") || n.Contains("5090"))
            max = 400;
        else if (n.Contains("5070 Ti") || n.Contains("4080") || n.Contains("4090"))
            max = 300;
        return (Helpers.AppConfig.Get("min_gpu_core", min), Helpers.AppConfig.Get("max_gpu_core", max));
    }

    private (int min, int max) MemCap()
    {
        int max = 500, min = -500;
        string n = _gpuName ?? "";
        if (n.Contains("5080") || n.Contains("5090"))
            max = 1000;
        return (Helpers.AppConfig.Get("min_gpu_memory", min), Helpers.AppConfig.Get("max_gpu_memory", max));
    }

    private static (int min, int max)? Intersect((int min, int max)? range, (int min, int max) cap)
    {
        if (range == null)
            return null;
        return (Math.Max(range.Value.min, cap.min), Math.Min(range.Value.max, cap.max));
    }

    public (int min, int max)? GetCoreOffsetRange() => Intersect(QueryNvmlInfo()?.CoreRange, CoreCap());

    public (int min, int max)? GetMemOffsetRange() => Intersect(QueryNvmlInfo()?.MemRange, MemCap());

    public (int core, int mem)? GetClockOffsets()
    {
        var info = QueryNvmlInfo();
        if (info?.CoreOffset == null || info?.MemOffset == null)
            return null;
        return (info.Value.CoreOffset.Value, info.Value.MemOffset.Value);
    }

    public void ApplyClockOffsets(int coreMhz, int memMhz) => ApplyAll(null, -1, coreMhz, memMhz);

    /// <summary>Clamp a requested offset to the safe + NVML-intersected range.</summary>
    private int ClampCore(int v)
    {
        var r = GetCoreOffsetRange();
        return r == null ? v : Math.Clamp(v, r.Value.min, r.Value.max);
    }

    private int ClampMem(int v)
    {
        var r = GetMemOffsetRange();
        return r == null ? v : Math.Clamp(v, r.Value.min, r.Value.max);
    }

    // Extended queries (not in interface but useful)

    /// <summary>Get comprehensive GPU status in one call.</summary>
    public (int? temp, int? usage, int? clock, int? memClock, int? power, int? fanSpeed)?
        GetFullStatus()
    {
        // Single nvidia-smi call for all values (much faster than 5 separate calls)
        var output = RunNvidiaSmi(
            "--query-gpu=temperature.gpu,utilization.gpu,clocks.current.graphics,clocks.current.memory,power.draw,fan.speed",
            "--format=csv,noheader,nounits");

        if (output == null)
            return null;

        var parts = output.Split(',');
        if (parts.Length < 6)
            return null;

        int? ParsePart(string s)
        {
            s = s.Trim();
            if (s == "[N/A]" || s == "N/A" || s == "")
                return null;
            if (int.TryParse(s, out int v))
                return v;
            if (double.TryParse(s, CultureInfo.InvariantCulture, out double d))
                return (int)Math.Round(d);
            return null;
        }

        return (
            temp: ParsePart(parts[0]),
            usage: ParsePart(parts[1]),
            clock: ParsePart(parts[2]),
            memClock: ParsePart(parts[3]),
            power: ParsePart(parts[4]),
            fanSpeed: ParsePart(parts[5])
        );
    }

    public readonly record struct GpuLiveStatus(
        int? Temp, int? Usage, int? CoreClock, int? MemClock, int? SmClock,
        double? PowerDraw, double? PowerLimit, int? VramUsedMb, int? VramTotalMb,
        string? Pstate, string? ThrottleReason, int? PcieGen, int? PcieWidth);

    public GpuLiveStatus? GetLiveStatus()
    {
        var output = RunNvidiaSmi(
            "--query-gpu=temperature.gpu,utilization.gpu,clocks.current.graphics,clocks.current.memory,"
            + "clocks.current.sm,power.draw,power.limit,memory.used,memory.total,pstate,"
            + "clocks_event_reasons.active,pcie.link.gen.current,pcie.link.width.current",
            "--format=csv,noheader,nounits");
        if (output == null)
            return null;
        var p = output.Split(',');
        if (p.Length < 13)
            return null;

        int? I(int i)
        {
            var s = p[i].Trim();
            if (s.Contains("N/A", StringComparison.Ordinal) || s == "")
                return null;
            if (int.TryParse(s, out int v))
                return v;
            return double.TryParse(s, CultureInfo.InvariantCulture, out double d) ? (int)Math.Round(d) : null;
        }
        double? D(int i)
        {
            var s = p[i].Trim();
            if (s.Contains("N/A", StringComparison.Ordinal) || s == "")
                return null;
            return double.TryParse(s, CultureInfo.InvariantCulture, out double d) ? d : null;
        }
        string? S(int i)
        {
            var s = p[i].Trim();
            return (s.Contains("N/A", StringComparison.Ordinal) || s == "") ? null : s;
        }

        return new GpuLiveStatus(
            I(0), I(1), I(2), I(3), I(4), D(5), D(6), I(7), I(8), S(9),
            DecodeThrottle(S(10)), I(11), I(12));
    }

    /// <summary>Decode the clocks_event_reasons.active hex bitmask to a short label.</summary>
    private static string? DecodeThrottle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        raw = raw.Trim();
        long bits;
        try
        {
            bits = Convert.ToInt64(raw.Replace("0x", "").Replace("0X", ""), 16);
        }
        catch
        {
            return null;
        }
        // 0x1 = GpuIdle: not a real throttle, hide it.
        if (bits == 0 || bits == 0x1)
            return null;
        var list = new System.Collections.Generic.List<string>();
        if ((bits & 0x4) != 0)
            list.Add("SW Power Cap");
        if ((bits & 0x80) != 0)
            list.Add("HW Power Brake");
        if ((bits & 0x40) != 0)
            list.Add("HW Thermal");
        if ((bits & 0x20) != 0)
            list.Add("SW Thermal");
        if ((bits & 0x8) != 0)
            list.Add("HW Slowdown");
        if ((bits & 0x10) != 0)
            list.Add("Sync Boost");
        if ((bits & 0x2) != 0)
            list.Add("App Clock");
        return list.Count > 0 ? string.Join(", ", list) : null;
    }

    /// <summary>Lock or reset VRAM (memory) clocks. mhz &lt;= 0 resets (-rmc).</summary>
    public void ApplyMemClockLock(int mhz, bool interactive = true)
    {
        if (!_available || !NvidiaProcessScanner.EnsureHelper())
            return;
        if (mhz > 0)
            SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "smi", "-lmc", $"0,{mhz}" }, allowPkexec: interactive);
        else
            SysfsHelper.RunSudoOrPkexec(SysfsHelper.GpuHelperPath, new[] { "smi", "-rmc" }, allowPkexec: interactive);
    }

    /// <summary>Max supported graphics / memory clock (MHz) for lock-slider bounds.</summary>
    public (int core, int mem)? GetMaxClocks()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.max.graphics,clocks.max.memory",
            "--format=csv,noheader,nounits");
        if (output == null)
            return null;
        var p = output.Split(',');
        if (p.Length >= 2 && int.TryParse(p[0].Trim(), out var c) && int.TryParse(p[1].Trim(), out var m))
            return (c, m);
        return null;
    }

    // Private helpers

    private string? QueryGpuName()
    {
        var output = RunNvidiaSmi("--query-gpu=name", "--format=csv,noheader");
        return output?.Trim();
    }

    // Circuit breaker: when the dGPU is wedged/absent, nvidia-smi hangs until its
    // timeout on every call. Without this, pollers (tray, Fans live readout) keep
    // spawning hanging processes and the app crawls. After a couple of consecutive
    // failures we stop calling nvidia-smi for a cooldown window.
    private static int _smiFailStreak;
    private static DateTime _smiCooldownUntilUtc;
    private const int SmiTimeoutMs = 1200;
    private const int SmiFailThreshold = 2;
    private static readonly TimeSpan SmiCooldown = TimeSpan.FromSeconds(15);

    // A wedged driver leaves each timed-out nvidia-smi stuck in uninterruptible
    // sleep holding the NVML rwlock, so it can never be reaped and every later
    // one blocks behind it. Repeating a 15s cooldown forever just grows that
    // pile, so give up entirely once this many cooldowns pass with no success.
    private const int SmiCooldownsBeforeHold = 4;

    // dGPU runtime-PM guard. A runtime-suspended (D3cold) dGPU is healthy but
    // powered down. Reading power/runtime_status is passive, but any nvidia-smi
    // or NVML query resumes it from D3cold and defeats Optimus power saving - so
    // telemetry reads short-circuit while it is suspended. The PCI path is
    // stable, so it is cached once found; if absent (e.g. booted in Eco) we
    // re-scan slowly so it appears after a later switch to Standard.
    private static string? _rtStatusPath;
    private static DateTime _rtRescanUtc;
    private static bool _idleBackoffLogged;

    /// <summary>True when the discrete NVIDIA GPU is runtime-suspended (D3cold).
    /// Passive sysfs read; callers use it to avoid waking the dGPU for telemetry.</summary>
    public static bool IsDgpuSuspended()
    {
        if (_rtStatusPath == null && DateTime.UtcNow >= _rtRescanUtc)
        {
            _rtRescanUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            try
            {
                foreach (var dev in Directory.EnumerateDirectories("/sys/bus/pci/devices"))
                {
                    string cls = SysfsHelper.ReadAttribute(Path.Combine(dev, "class")) ?? "";
                    if (!cls.StartsWith("0x0300", StringComparison.Ordinal)
                        && !cls.StartsWith("0x0302", StringComparison.Ordinal))
                        continue;
                    if (SysfsHelper.ReadInt(Path.Combine(dev, "boot_vga"), 0) == 1)
                        continue; // iGPU
                    if ((SysfsHelper.ReadAttribute(Path.Combine(dev, "vendor")) ?? "")
                        .StartsWith("0x10de", StringComparison.OrdinalIgnoreCase))
                    {
                        _rtStatusPath = Path.Combine(dev, "power/runtime_status");
                        break;
                    }
                }
            }
            catch { }
        }
        return _rtStatusPath != null
            && SysfsHelper.ReadAttribute(_rtStatusPath) == "suspended";
    }

    /// <summary>True when periodic telemetry must skip the dGPU: it is either
    /// runtime-suspended, or idle with runtime PM enabled (control=auto,
    /// usage_count=0). Polling an idle-active dGPU resets its autosuspend
    /// timer on every query, so it never reaches D3cold (issue #157).</summary>
    public static bool ShouldSkipDgpuTelemetry()
    {
        if (IsDgpuSuspended())
        {
            DgpuIdleBackoff.NoteSuspended(Environment.TickCount64);
            return true;
        }
        if (_rtStatusPath == null)
            return false;
        var powerDir = Path.GetDirectoryName(_rtStatusPath)!;
        // control=on means runtime PM is off; polling cannot break anything.
        if (SysfsHelper.ReadAttribute(Path.Combine(powerDir, "control")) != "auto")
            return false;
        // usage_count=0: no client holds the GPU, suspend is pending.
        int usage = SysfsHelper.ReadInt(Path.Combine(powerDir, "runtime_usage"), -1);
        if (usage >= 0)
            return usage == 0;
        // runtime_usage needs CONFIG_PM_ADVANCED_DEBUG; fall back to backoff (issue #157).
        if (!_idleBackoffLogged)
        {
            _idleBackoffLogged = true;
            Helpers.Logger.WriteLine("NVIDIA: runtime_usage unavailable, using idle backoff for dGPU telemetry");
        }
        return DgpuIdleBackoff.ShouldSkip(Environment.TickCount64);
    }

    /// <summary>
    /// Clear the nvidia-smi circuit breaker. Called when the dGPU comes back so
    /// a stale streak from the previous session cannot trip the hold instantly.
    /// </summary>
    public static void ResetSmiBreaker()
    {
        _smiFailStreak = 0;
        _smiCooldownUntilUtc = DateTime.MinValue;
    }

    private static string? RunNvidiaSmi(string query, string format = "")
    {
        // Proactive gate: GPU mode switches pause telemetry so we never spawn
        // an nvidia-smi that turns into a driver holder mid-unbind.
        if (GpuQueryGate.IsPaused)
            return null;

        // Never wake a runtime-suspended dGPU just to read telemetry.
        if (IsDgpuSuspended())
            return null;

        if (DateTime.UtcNow < _smiCooldownUntilUtc)
            return null;

        var args = string.IsNullOrEmpty(format) ? query : $"{query} {format}";
        var result = SysfsHelper.RunCommandWithTimeout("nvidia-smi", args, SmiTimeoutMs);

        if (result == null)
        {
            if (++_smiFailStreak >= SmiFailThreshold)
            {
                int cooldowns = _smiFailStreak - SmiFailThreshold + 1;
                if (cooldowns >= SmiCooldownsBeforeHold)
                {
                    GpuQueryGate.Hold("nvidia-smi wedged - dGPU not responding");
                }
                else
                {
                    _smiCooldownUntilUtc = DateTime.UtcNow + SmiCooldown;
                    Helpers.Logger.WriteLine(
                        $"NVIDIA: nvidia-smi unresponsive - pausing GPU queries for {SmiCooldown.TotalSeconds:0}s"
                        + $" ({cooldowns}/{SmiCooldownsBeforeHold} before giving up)");
                }
            }
        }
        else
        {
            _smiFailStreak = 0;
        }
        return result;
    }
}

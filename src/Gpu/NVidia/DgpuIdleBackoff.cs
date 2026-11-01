namespace GHelper.Linux.Gpu.NVidia;

/// <summary>
/// Idle detection when power/runtime_usage is missing (kernels without
/// CONFIG_PM_ADVANCED_DEBUG). An NVML query keeps the dGPU active for ~20 s,
/// so steady polling never lets it suspend. After a poll window with no busy
/// reading, telemetry pauses for QuietWindowMs so an idle dGPU can suspend.
/// If it stays awake anyway (held by a display or an idle CUDA context), the
/// poll window doubles up to MaxPollWindowMs. Times are monotonic milliseconds
/// (Environment.TickCount64), so wall-clock jumps do not matter.
/// </summary>
public static class DgpuIdleBackoff
{
    public const long QuietWindowMs = 25_000;
    public const long BasePollWindowMs = 60_000;
    public const long MaxPollWindowMs = 600_000;

    private static readonly object _lock = new();
    private static bool _started;
    private static bool _quiet;
    private static long _quietUntilMs;
    private static long _nextQuietMs;
    private static long _pollWindowMs = BasePollWindowMs;

    public static bool ShouldSkip(long nowMs)
    {
        lock (_lock)
        {
            if (!_started)
            {
                // Start with a poll window: startup probes and mode applies need readings.
                _started = true;
                _nextQuietMs = nowMs + _pollWindowMs;
                return false;
            }

            if (_quiet)
            {
                if (nowMs < _quietUntilMs)
                    return true;

                // Quiet window ended with the dGPU still awake: something else holds it.
                _quiet = false;
                _pollWindowMs = Math.Min(_pollWindowMs * 2, MaxPollWindowMs);
                _nextQuietMs = nowMs + _pollWindowMs;
                return false;
            }

            if (nowMs >= _nextQuietMs)
            {
                _quiet = true;
                _quietUntilMs = nowMs + QuietWindowMs;
                return true;
            }

            return false;
        }
    }

    /// <summary>dGPU seen suspended: reset, and give the next wake a full poll window.</summary>
    public static void NoteSuspended(long nowMs)
    {
        lock (_lock)
        {
            _started = true;
            _quiet = false;
            _pollWindowMs = BasePollWindowMs;
            _nextQuietMs = nowMs + _pollWindowMs;
        }
    }

    /// <summary>dGPU utilization above 0: postpone the next quiet window.</summary>
    public static void NoteBusy(long nowMs)
    {
        lock (_lock)
        {
            long next = nowMs + _pollWindowMs;
            if (next > _nextQuietMs)
                _nextQuietMs = next;
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _started = false;
            _quiet = false;
            _quietUntilMs = 0;
            _nextQuietMs = 0;
            _pollWindowMs = BasePollWindowMs;
        }
    }
}

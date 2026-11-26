namespace GHelper.Linux.Fan;

/// <summary>Temperature knobs: rolling window in ticks (fan_hysteresis),
/// own vs hotter sensor (fan_shift), fixed CPU/GPU mix (fan_avg, -1 = off).</summary>
public readonly record struct FanBlend(int Window, int Shift, int Avg);

/// <summary>One tick: duty percent per fan (null = nothing to send), or
/// GiveBack when the fans must go back to the BIOS.</summary>
public readonly record struct FanStep(int[]? Duty, bool GiveBack);

/// <summary>
/// Temperatures to fan duty for the manual EC follower: rolling average,
/// CPU/GPU blend, curve interpolation with the 20 % floor, and full speed
/// from 95 C on either sensor until both are under 90 C.
/// </summary>
public sealed class ManualFanPolicy
{
    public const int OverheatOn = 95;
    public const int OverheatOff = 90;
    private const int MaxTempFailures = 3;

    private readonly Queue<int> _cpu = new();
    private readonly Queue<int> _gpu = new();
    private int _tempFailures;

    public bool Overheat { get; private set; }

    /// <summary>curve(fan) gives the 16-byte curve of a fan. A CPU temp
    /// &lt;= 0 skips the tick; three in a row give the fans back.</summary>
    public FanStep Next(int cpu, int gpu, int fans, FanBlend blend, Func<int, byte[]> curve)
    {
        if (cpu <= 0)
            return new(null, ++_tempFailures >= MaxTempFailures);
        _tempFailures = 0;

        int window = Math.Clamp(blend.Window, 1, 30);
        Push(_cpu, cpu, window);
        if (gpu > 0)
            Push(_gpu, gpu, window);
        else
            _gpu.Clear();

        int hottest = Math.Max(cpu, gpu);
        Overheat = hottest >= OverheatOn || (Overheat && hottest >= OverheatOff);

        double avgCpu = ManualFanMath.Average(_cpu);
        double avgGpu = ManualFanMath.Average(_gpu);
        bool gpuOk = avgGpu > 0;

        // cpu fan follows cpu, gpu fan follows gpu (cpu when the dgpu is
        // asleep), mid fan follows cpu
        double cpuSrc, gpuSrc;
        if (blend.Avg >= 0 && gpuOk)
        {
            cpuSrc = gpuSrc = ManualFanMath.ApplyAvg(avgCpu, avgGpu, blend.Avg);
        }
        else if (gpuOk)
        {
            cpuSrc = ManualFanMath.ApplyShift(avgCpu, avgGpu, blend.Shift);
            gpuSrc = ManualFanMath.ApplyShift(avgGpu, avgCpu, blend.Shift);
        }
        else
        {
            cpuSrc = gpuSrc = avgCpu;
        }

        var duty = new int[fans];
        for (int fan = 0; fan < fans; fan++)
            duty[fan] = Overheat ? 100
                : ManualFanMath.ClampDuty(ManualFanMath.Interpolate(curve(fan), fan == 1 ? gpuSrc : cpuSrc));
        return new(duty, false);
    }

    private static void Push(Queue<int> q, int value, int window)
    {
        q.Enqueue(value);
        while (q.Count > window)
            q.Dequeue();
    }
}

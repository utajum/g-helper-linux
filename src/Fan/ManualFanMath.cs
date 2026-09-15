namespace GHelper.Linux.Fan;

/// <summary>Pure math for the manual fan follower. Kept separate for tests.</summary>
public static class ManualFanMath
{
    /// <summary>Duty percent for temp from a 16-byte curve (8 temps + 8 pwm %).
    /// Linear between points, flat outside.</summary>
    public static int Interpolate(byte[] curve, double temp)
    {
        if (curve.Length != 16)
            return -1;
        if (temp <= curve[0])
            return curve[8];
        if (temp >= curve[7])
            return curve[15];
        for (int i = 1; i < 8; i++)
        {
            if (temp > curve[i])
                continue;
            int t0 = curve[i - 1], t1 = curve[i];
            int p0 = curve[8 + i - 1], p1 = curve[8 + i];
            if (t1 == t0)
                return p1;
            return (int)Math.Round(p0 + (p1 - p0) * (temp - t0) / (t1 - t0));
        }
        return curve[15];
    }

    /// <summary>Weight own temp against the hotter of the two.
    /// shift 0 = own only, 100 = follow max.</summary>
    public static double ApplyShift(double own, double other, int shift)
    {
        shift = Math.Clamp(shift, 0, 100);
        double max = Math.Max(own, other);
        return (own * (100 - shift) + max * shift) / 100.0;
    }

    /// <summary>Blend cpu and gpu temps. avg 0 = cpu only, 100 = gpu only.</summary>
    public static double ApplyAvg(double cpu, double gpu, int avg)
    {
        avg = Math.Clamp(avg, 0, 100);
        return (cpu * (100 - avg) + gpu * avg) / 100.0;
    }

    /// <summary>Firmware parity: 0 stays off, otherwise 20-100.</summary>
    public static int ClampDuty(int pct)
    {
        if (pct <= 0)
            return 0;
        return Math.Clamp(pct, 20, 100);
    }

    public static int ToDuty255(int pct) => Math.Clamp(pct, 0, 100) * 255 / 100;

    /// <summary>Rolling average of the last N samples.</summary>
    public static double Average(Queue<int> window)
    {
        if (window.Count == 0)
            return -1;
        double sum = 0;
        foreach (int v in window)
            sum += v;
        return sum / window.Count;
    }
}

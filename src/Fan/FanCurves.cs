using GHelper.Linux.Helpers;

namespace GHelper.Linux.Fan;

/// <summary>
/// Who runs the fan curves: the manual EC follower when it runs, else the
/// firmware (asus-wmi hwmon curve). Callers apply, disable and query the
/// curves here and do not need to know which one is active.
/// </summary>
public static class FanCurves
{
    /// <summary>Apply the saved curves of the current mode (mode switch).</summary>
    public static void ApplyMode(int mode)
    {
        ManualFanService.Sync();
        if (ManualFanService.Running || !AppConfig.IsMode("auto_apply_fans"))
            return;

        var wmi = App.Wmi;
        if (wmi == null)
            return;
        for (int fan = 0; fan < wmi.FanCount; fan++)
        {
            byte[] curve = AppConfig.GetFanConfig(fan);
            if (curve.Length == 16)
            {
                wmi.SetFanCurve(fan, curve);
                Logger.WriteLine($"AutoFans: Applied fan {fan} curve for mode {mode}");
            }
        }
    }

    /// <summary>Push one saved curve now. The EC follower reads the saved
    /// curves every tick, so only the firmware needs a write.</summary>
    public static void Apply(int fan, byte[] curve)
    {
        if (!ManualFanService.Running)
            App.Wmi?.SetFanCurve(fan, curve);
    }

    /// <summary>BIOS fan control until the next mode apply.</summary>
    public static void Disable(int fans)
    {
        if (ManualFanService.Running)
        {
            ManualFanService.Stop();
            return;
        }
        for (int fan = 0; fan < fans; fan++)
            App.Wmi?.DisableFanCurve(fan);
    }

    public static bool IsActive(int fan) =>
        ManualFanService.Running || (App.Wmi?.IsFanCurveEnabled(fan) ?? false);
}

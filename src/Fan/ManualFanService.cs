using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Fan;

/// <summary>
/// Experimental manual fan control, port of the Windows g-helper experimental
/// build. A 1 s loop feeds CPU/GPU temps to <see cref="ManualFanPolicy"/> and
/// sends the duties through <see cref="EcFanChannel"/> (gpu-helper ec-fanctl
/// as root, the same EC path MyASUS fan test uses). Runs only while
/// "fan_manual" is on and the active mode has auto_apply_fans. Fans go back
/// to the BIOS on stop, app exit, helper death or when CPU temp is lost.
/// </summary>
public static class ManualFanService
{
    public const string ConfigKey = "fan_manual";

    private const int TickMs = 1000;
    private const int MaxHelperErrors = 5;
    private const int TempCpuDevId = 0x00120094;
    private const int TempGpuDevId = 0x00120097;

    private static readonly object _lock = new();
    private static System.Timers.Timer? _timer;
    private static bool _busy;
    private static bool _unsupported; // probe failed once, do not respawn on every mode change
    private static EcFanChannel? _channel;
    private static ManualFanPolicy _policy = new();
    private static int _fans;
    private static int _errors;

    public static bool Enabled => AppConfig.Is(ConfigKey);
    public static bool Running { get; private set; }

    /// <summary>Fans window text: duties while running, the reason when it
    /// could not start, "" when stopped.</summary>
    public static event Action<string>? StatusChanged;

    /// <summary>Match the follower to config. Called on every mode apply and
    /// on user toggles. interactive allows a pkexec prompt; mode changes only
    /// try sudo -n so no dialog pops at boot.</summary>
    public static void Sync(bool interactive = false)
    {
        lock (_lock)
        {
            if (interactive)
                _unsupported = false;
            if (!Enabled || !AppConfig.IsMode("auto_apply_fans"))
            {
                Stop();
                return;
            }
            if (_unsupported)
                return;
            if (!Start(interactive) && interactive)
                AppConfig.Set(ConfigKey, 0);
        }
    }

    // caller holds _lock
    private static bool Start(bool interactive)
    {
        if (Running)
            return true;
        if (App.Wmi is not LinuxAsusWmi wmi)
            return Fail(unsupported: true);

        var channel = EcFanChannel.Open(interactive, wmi.GetFanRpm(0), out bool authFailed);
        if (channel == null)
            return Fail(unsupported: !authFailed);

        _channel = channel;
        _fans = channel.Probe.Fans;
        _policy = new ManualFanPolicy();
        _errors = 0;
        _timer = new System.Timers.Timer(TickMs);
        _timer.Elapsed += (_, _) => Tick();
        _timer.Start();
        Running = true;
        Logger.WriteLine($"ManualFan: started, {_fans} fans, {channel.Probe.Link} link, table {channel.Probe.Version}");
        return true;
    }

    private static bool Fail(bool unsupported)
    {
        _unsupported = unsupported;
        Status(Labels.Get(unsupported ? "manual_fan_unsupported" : "sysfiles_auth_cancelled"));
        return false;
    }

    /// <summary>Stop and hand the fans back to the EC.</summary>
    public static void Stop()
    {
        lock (_lock)
        {
            if (!Running)
                return;
            Running = false;
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
            _channel?.Dispose();
            _channel = null;
            Status("");
            Logger.WriteLine("ManualFan: stopped");
        }
    }

    private static void Status(string text) => StatusChanged?.Invoke(text);

    private static void Tick()
    {
        if (_busy || !Running)
            return;
        _busy = true;
        try
        {
            // hold the lock so Stop cannot release the EC mid-write
            lock (_lock)
            {
                if (Running)
                    TickCore();
            }
        }
        catch (Exception e)
        {
            Logger.WriteLine($"ManualFan: tick error: {e.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static void TickCore()
    {
        var channel = _channel;
        if (App.Wmi is not LinuxAsusWmi wmi || channel == null)
            return;

        bool wasHot = _policy.Overheat;
        var blend = new FanBlend(AppConfig.Get("fan_hysteresis", 6), AppConfig.Get("fan_shift", 50), AppConfig.Get("fan_avg", -1));
        var step = _policy.Next(wmi.DeviceGet(TempCpuDevId), wmi.DeviceGet(TempGpuDevId), _fans, blend, Curve);
        if (_policy.Overheat != wasHot)
            Logger.WriteLine($"ManualFan: overheat {(_policy.Overheat ? "on, full speed" : "off")}");

        if (step.GiveBack)
        {
            // no CPU temp = flying blind, give the fans back
            Logger.WriteLine("ManualFan: CPU temp unavailable, stopping");
            Task.Run(Stop);
            return;
        }
        if (step.Duty == null)
            return;

        if (!channel.Set(step.Duty.Select(ManualFanMath.ToDuty255).ToArray()))
        {
            if (!channel.Alive || ++_errors >= MaxHelperErrors)
            {
                Logger.WriteLine("ManualFan: EC not answering, stopping");
                Task.Run(Stop);
            }
            return;
        }
        _errors = 0;
        Status(string.Join("  ", step.Duty.Select((d, fan) => $"{(fan == 0 ? "CPU" : fan == 1 ? "GPU" : "Mid")} {d}%")));
    }

    private static byte[] Curve(int fan)
    {
        byte[] curve = AppConfig.GetFanConfig(fan);
        return curve.Length == 16 ? curve : AppConfig.GetDefaultCurve(fan);
    }
}

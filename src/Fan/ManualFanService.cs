using System.Diagnostics;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Fan;

/// <summary>
/// Experimental manual fan control, port of the Windows g-helper experimental
/// build. A 1 s loop averages CPU/GPU temps, interpolates the current mode
/// curves and pins per-fan duty through the EC HealthyTable (gpu-helper
/// ec-fanctl as root, the same path MyASUS fan test uses). Runs only while
/// "fan_manual" is on and the active mode has auto_apply_fans. Fans go back
/// to the EC curve on stop, app exit, helper death or when CPU temp is lost.
/// </summary>
public static class ManualFanService
{
    public const string ConfigKey = "fan_manual";

    private const int TickMs = 1000;
    private const int MaxTempFailures = 3;
    private const int MaxHelperErrors = 5;
    private const int TempCpuDevId = 0x00120094;
    private const int TempGpuDevId = 0x00120097;

    private static readonly object _lock = new();
    private static System.Timers.Timer? _timer;
    private static bool _busy;
    private static bool _unsupported; // probe failed once, do not respawn on every mode change
    private static Helper? _helper;
    private static int _fans;
    private static Queue<int> _cpuWindow = new();
    private static Queue<int> _gpuWindow = new();
    private static int _tempFailures;

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

        var helper = Helper.Spawn(interactive, out bool authFailed);
        if (helper == null)
            return Fail(unsupported: !authFailed);
        if (!helper.Validate(wmi.GetFanRpm(0)))
        {
            helper.Dispose();
            return Fail(unsupported: true);
        }

        _helper = helper;
        _fans = Math.Min(helper.FanCount, 3);
        _cpuWindow = new Queue<int>();
        _gpuWindow = new Queue<int>();
        _tempFailures = 0;
        _timer = new System.Timers.Timer(TickMs);
        _timer.Elapsed += (_, _) => Tick();
        _timer.Start();
        Running = true;
        Logger.WriteLine($"ManualFan: started, {_fans} fans");
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
            _helper?.Dispose();
            _helper = null;
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
        var helper = _helper;
        if (App.Wmi is not LinuxAsusWmi wmi || helper == null)
            return;

        int cpu = wmi.DeviceGet(TempCpuDevId);
        int gpu = wmi.DeviceGet(TempGpuDevId);

        if (cpu <= 0)
        {
            // no CPU temp = flying blind, give the fans back
            if (++_tempFailures >= MaxTempFailures)
            {
                Logger.WriteLine("ManualFan: CPU temp unavailable, stopping");
                Task.Run(Stop);
            }
            return;
        }
        _tempFailures = 0;

        if (helper.Errors >= MaxHelperErrors)
        {
            Logger.WriteLine("ManualFan: EC not answering, stopping");
            Task.Run(Stop);
            return;
        }

        int window = Math.Clamp(AppConfig.Get("fan_hysteresis", 6), 1, 30);
        Push(_cpuWindow, cpu, window);
        if (gpu > 0)
            Push(_gpuWindow, gpu, window);
        else
            _gpuWindow.Clear();

        double avgCpu = ManualFanMath.Average(_cpuWindow);
        double avgGpu = ManualFanMath.Average(_gpuWindow);
        bool gpuOk = avgGpu > 0;

        int shift = AppConfig.Get("fan_shift", 50);
        int avg = AppConfig.Get("fan_avg", -1);

        // cpu fan follows cpu, gpu fan follows gpu (cpu when the dgpu is
        // asleep), mid fan follows cpu
        double cpuSrc, gpuSrc;
        if (avg >= 0 && gpuOk)
        {
            cpuSrc = gpuSrc = ManualFanMath.ApplyAvg(avgCpu, avgGpu, avg);
        }
        else if (gpuOk)
        {
            cpuSrc = ManualFanMath.ApplyShift(avgCpu, avgGpu, shift);
            gpuSrc = ManualFanMath.ApplyShift(avgGpu, avgCpu, shift);
        }
        else
        {
            cpuSrc = gpuSrc = avgCpu;
        }

        var status = new List<string>();
        for (int fan = 0; fan < _fans; fan++)
        {
            byte[] curve = AppConfig.GetFanConfig(fan);
            if (curve.Length != 16)
                curve = AppConfig.GetDefaultCurve(fan);

            double src = fan == 1 ? gpuSrc : cpuSrc;
            int duty = ManualFanMath.ClampDuty(ManualFanMath.Interpolate(curve, src));
            status.Add($"{(fan == 0 ? "CPU" : fan == 1 ? "GPU" : "Mid")} {duty}%");

            if (!helper.SetDuty(fan, ManualFanMath.ToDuty255(duty)))
            {
                Logger.WriteLine("ManualFan: helper gone, stopping");
                Task.Run(Stop);
                return;
            }
        }

        Status(string.Join("  ", status));
    }

    private static void Push(Queue<int> q, int value, int window)
    {
        q.Enqueue(value);
        while (q.Count > window)
            q.Dequeue();
    }

    /// <summary>One root gpu-helper ec-fanctl process. Commands go over
    /// stdin, replies are drained in the background. EOF on stdin makes the
    /// helper release the fans, so a dead app self-heals.</summary>
    private sealed class Helper : IDisposable
    {
        private readonly Process _proc;
        private readonly string _via;
        private int _errors;

        public int FanCount { get; private set; }
        public int Rpm0 { get; private set; } = -1;
        public int Errors => _errors;

        private Helper(Process proc, string via)
        {
            _proc = proc;
            _via = via;
        }

        /// <summary>Spawn and probe the EC in one round trip. Null when auth
        /// fails or the helper dies; authFailed tells the two apart.</summary>
        public static Helper? Spawn(bool interactive, out bool authFailed)
        {
            authFailed = false;
            if (!Gpu.NVidia.NvidiaProcessScanner.EnsureHelper())
                return null;
            string bin = SysfsHelper.GpuHelperPath;

            var attempts = new List<(string file, string args, int timeoutMs)>
            {
                ("sudo", $"-n {bin} ec-fanctl", 5000),
            };
            if (interactive)
                attempts.Add(("pkexec", $"{bin} ec-fanctl", 90000));

            bool spawned = false;
            foreach (var (file, args, timeoutMs) in attempts)
            {
                Process? p = null;
                try
                {
                    p = Process.Start(new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    });
                    if (p == null)
                        continue;
                    spawned = true;

                    p.StandardInput.WriteLine("probe");
                    var read = Task.Run(() => p.StandardOutput.ReadLine());
                    if (!read.Wait(timeoutMs) || read.Result == null || p.HasExited)
                    {
                        Kill(p);
                        continue;
                    }

                    var h = new Helper(p, file);
                    h.ParseProbe(read.Result);
                    h.Drain();
                    Logger.WriteLine($"ManualFan: ec-fanctl via {file}: {read.Result}");
                    return h;
                }
                catch (Exception e)
                {
                    Logger.WriteLine($"ManualFan: {file} spawn failed: {e.Message}");
                    Kill(p);
                }
            }

            // spawned but never answered: auth dialog closed or polkit denied
            authFailed = spawned;
            return null;
        }

        /// <summary>"probe &lt;ver&gt; &lt;fans&gt; &lt;rpm0&gt; &lt;rpm1&gt;" or "probe -".</summary>
        private void ParseProbe(string line)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 4 && p[0] == "probe" && int.TryParse(p[2], out int fans) && int.TryParse(p[3], out int rpm0))
            {
                FanCount = fans;
                Rpm0 = rpm0;
            }
        }

        /// <summary>Accept only when the EC answered with a sane fan count and
        /// its tach agrees with the hwmon reading, so a foreign EC that
        /// happens to decode the ports never gets a duty write.</summary>
        public bool Validate(int hwmonRpm0)
        {
            if (FanCount < 1 || FanCount > 3)
                return false;
            if (hwmonRpm0 > 0 && Rpm0 >= 0)
            {
                int tol = Math.Max(300, hwmonRpm0 * 15 / 100);
                if (Math.Abs(Rpm0 - hwmonRpm0) > tol)
                {
                    Logger.WriteLine($"ManualFan: tach mismatch ec={Rpm0} hwmon={hwmonRpm0}");
                    return false;
                }
            }
            return true;
        }

        private void Drain()
        {
            var p = _proc;
            Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("err"))
                    {
                        Interlocked.Increment(ref _errors);
                        Logger.WriteLine($"ManualFan: helper {line}");
                    }
                    else
                        Interlocked.Exchange(ref _errors, 0);
                }
            });
            Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardError.ReadLine()) != null)
                    Logger.WriteLine($"ManualFan: helper stderr: {line}");
            });
        }

        /// <summary>False when the pipe is gone.</summary>
        public bool SetDuty(int fan, int duty255)
        {
            if (_proc.HasExited)
                return false;
            try
            {
                _proc.StandardInput.WriteLine($"set {fan} {duty255}");
                return true;
            }
            catch (Exception e)
            {
                Logger.WriteLine($"ManualFan: helper write failed: {e.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _proc.StandardInput.WriteLine("auto");
                _proc.StandardInput.WriteLine("quit");
                _proc.StandardInput.Close();
                if (!_proc.WaitForExit(2000))
                    _proc.Kill();
            }
            catch
            {
                Kill(_proc);
            }
            Logger.WriteLine($"ManualFan: ec-fanctl ({_via}) stopped");
        }

        private static void Kill(Process? p)
        {
            try
            { p?.Kill(); }
            catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Platform.Linux;

/// <summary>Batches "udevadm monitor" events, so DRM state is not polled (a poll can wake the dGPU).</summary>
public sealed class UdevMonitor : IDisposable
{
    /// <summary>One udev line, already parsed.</summary>
    public sealed record Event(string Action, string DevPath, string Subsystem)
    {
        /// <summary>Last path segment, e.g. "card2-HDMI-A-3".</summary>
        public string SysName
        {
            get
            {
                int slash = DevPath.LastIndexOf('/');
                return slash >= 0 && slash + 1 < DevPath.Length
                    ? DevPath.Substring(slash + 1)
                    : DevPath;
            }
        }
    }

    /// <summary>Parse one monitor line; null for the banner and blank lines.</summary>
    public static Event? ParseEvent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return null;
        if (parts[0] != "UDEV" && parts[0] != "KERNEL")
            return null;
        if (!parts[1].StartsWith("[", StringComparison.Ordinal))
            return null;

        string subsystem = parts[4];
        if (subsystem.Length < 3 || subsystem[0] != '(' || subsystem[^1] != ')')
            return null;
        subsystem = subsystem.Substring(1, subsystem.Length - 2);

        return new Event(parts[2], parts[3], subsystem);
    }

    private readonly string[] _subsystems;
    private readonly TimeSpan _debounce;
    private readonly Action<IReadOnlyList<Event>> _onBatch;
    private readonly object _gate = new();
    private readonly List<Event> _pending = new();

    private Process? _proc;
    private Timer? _debounceTimer;
    private bool _running;
    private bool _missingLogged;
    private int _restarts;

    public UdevMonitor(string[] subsystems, TimeSpan debounce, Action<IReadOnlyList<Event>> onBatch)
    {
        _subsystems = subsystems;
        _debounce = debounce;
        _onBatch = onBatch;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            _restarts = 0;
        }
        Spawn();
    }

    public void Stop()
    {
        Process? proc;
        lock (_gate)
        {
            if (!_running)
                return;
            _running = false;
            proc = _proc;
            _proc = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pending.Clear();
        }
        KillQuiet(proc);
        Logger.WriteLine("UdevMonitor: stopped");
    }

    private static void KillQuiet(Process? proc)
    {
        if (proc == null)
            return;
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch { }
        try
        { proc.Dispose(); }
        catch { }
    }

    private void Spawn()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "udevadm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // --udev is the processed stream, which is readable without root.
        psi.ArgumentList.Add("monitor");
        psi.ArgumentList.Add("--udev");
        foreach (var sub in _subsystems)
            psi.ArgumentList.Add($"--subsystem-match={sub}");

        Process proc;
        try
        {
            var started = Process.Start(psi);
            if (started == null)
                throw new InvalidOperationException("Process.Start returned null");
            proc = started;
        }
        catch (Exception ex)
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                Logger.WriteLine($"UdevMonitor: cannot run udevadm ({ex.Message}), falling back to the reconcile timer");
            }
            lock (_gate)
                _running = false;
            return;
        }

        lock (_gate)
        {
            if (!_running)
            {
                // Stopped while we were starting.
                KillQuiet(proc);
                return;
            }
            _proc = proc;
        }

        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Logger.WriteLine($"UdevMonitor: {e.Data.Trim()}");
        };
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => OnExited(proc);

        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"UdevMonitor: could not read udevadm output: {ex.Message}");
            OnExited(proc);
            return;
        }

        Logger.WriteLine($"UdevMonitor: watching {string.Join(",", _subsystems)} (pid {proc.Id})");
    }

    private void OnExited(Process proc)
    {
        bool restart;
        int delaySec;
        lock (_gate)
        {
            if (!_running || !ReferenceEquals(_proc, proc))
                return; // stopped on purpose, or already replaced
            _proc = null;
            // Back off so a permanently broken udevadm cannot spin.
            _restarts = Math.Min(_restarts + 1, 5);
            delaySec = _restarts * 2;
            restart = true;
        }
        KillQuiet(proc);
        if (!restart)
            return;

        Logger.WriteLine($"UdevMonitor: udevadm exited, restarting in {delaySec}s");
        var t = new Timer(_ =>
        {
            bool go;
            lock (_gate)
                go = _running && _proc == null;
            if (go)
                Spawn();
        }, null, TimeSpan.FromSeconds(delaySec), Timeout.InfiniteTimeSpan);
        // Keep the one-shot alive until it fires.
        _ = t;
    }

    private void OnLine(string? line)
    {
        var ev = ParseEvent(line);
        if (ev == null)
            return;

        lock (_gate)
        {
            if (!_running)
                return;
            _pending.Add(ev);
            _debounceTimer ??= new Timer(_ => Flush(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // Trailing debounce: the burst has to go quiet before we report.
            try
            { _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private void Flush()
    {
        List<Event> batch;
        lock (_gate)
        {
            if (!_running || _pending.Count == 0)
                return;
            batch = new List<Event>(_pending);
            _pending.Clear();
        }

        try
        { _onBatch(batch); }
        catch (Exception ex)
        {
            Logger.WriteLine($"UdevMonitor: handler failed: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}

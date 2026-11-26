using System.Diagnostics;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Fan;

/// <summary>Reply to "probe": "probe ok &lt;link&gt; &lt;version&gt; &lt;fans&gt; &lt;rpm&gt;..."
/// or "probe no &lt;reason&gt;".</summary>
public readonly record struct EcProbe(bool Ok, string Link, int Version, int Fans, int Rpm0, string Reason)
{
    public static EcProbe Parse(string? line)
    {
        var p = (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 6 && p[0] == "probe" && p[1] == "ok" && int.TryParse(p[3], out int version)
            && int.TryParse(p[4], out int fans) && int.TryParse(p[5], out int rpm0))
            return new(true, p[2], version, fans, rpm0, "");
        if (p.Length >= 3 && p[0] == "probe" && p[1] == "no")
            return new(false, "", 0, 0, -1, p[2]);
        // an older installed gpu-helper speaks another protocol
        return new(false, "", 0, 0, -1, "helper");
    }

    /// <summary>Null to accept. The EC tach must match the hwmon tach, so a
    /// foreign EC that happens to answer never gets a duty write.</summary>
    public string? Reject(int hwmonRpm0)
    {
        if (!Ok)
            return Reason;
        int tol = Math.Max(300, hwmonRpm0 * 15 / 100);
        if (hwmonRpm0 > 0 && Rpm0 >= 0 && Math.Abs(Rpm0 - hwmonRpm0) > tol)
            return $"tach ec={Rpm0} hwmon={hwmonRpm0}";
        return null;
    }
}

/// <summary>
/// One root gpu-helper ec-fanctl process. The helper finds the EC link,
/// checks the EC like the MyASUS fan test, holds the fans while "set" lines
/// arrive and gives them back to the BIOS on quit, EOF, a signal or 5 s
/// without a command.
/// </summary>
internal sealed class EcFanChannel : IDisposable
{
    private const int ReplyMs = 3000;

    private readonly Process _proc;
    private readonly string _via;
    private bool _broken;

    public EcProbe Probe { get; private set; }
    public bool Alive => !_broken && !_proc.HasExited;

    private EcFanChannel(Process proc, string via)
    {
        _proc = proc;
        _via = via;
    }

    /// <summary>Spawn and probe. Null when auth fails or the EC does not
    /// qualify; authFailed tells the two apart.</summary>
    public static EcFanChannel? Open(bool interactive, int hwmonRpm0, out bool authFailed)
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
                var proc = p;
                Task.Run(() =>
                {
                    string? line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                        Logger.WriteLine($"ManualFan: helper stderr: {line}");
                });

                var ch = new EcFanChannel(p, file);
                string? reply = ch.Request("probe", timeoutMs);
                if (reply == null)
                {
                    Kill(p);
                    continue;
                }
                Logger.WriteLine($"ManualFan: ec-fanctl via {file}: {reply}");
                ch.Probe = EcProbe.Parse(reply);
                string? reject = ch.Probe.Reject(hwmonRpm0);
                if (reject != null)
                {
                    Logger.WriteLine($"ManualFan: EC not usable: {reject}");
                    ch.Dispose();
                    return null;
                }
                return ch;
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

    /// <summary>Duty 0-255 per fan from fan 0. False on an EC error or a
    /// dead helper (then Alive is false).</summary>
    public bool Set(IReadOnlyList<int> duty255)
    {
        string? reply = Request("set " + string.Join(' ', duty255), ReplyMs);
        if (reply == "ok")
            return true;
        Logger.WriteLine($"ManualFan: helper {reply ?? "no reply"}");
        return false;
    }

    /// <summary>One line out, one line back. A timeout or a closed pipe
    /// breaks the channel for good.</summary>
    private string? Request(string line, int timeoutMs)
    {
        if (!Alive)
            return null;
        try
        {
            _proc.StandardInput.WriteLine(line);
            var read = _proc.StandardOutput.ReadLineAsync();
            if (read.Wait(timeoutMs) && read.Result != null)
                return read.Result;
        }
        catch (Exception e)
        {
            Logger.WriteLine($"ManualFan: helper pipe: {e.Message}");
        }
        _broken = true;
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (!_proc.HasExited)
            {
                _proc.StandardInput.WriteLine("quit");
                _proc.StandardInput.Close();
            }
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

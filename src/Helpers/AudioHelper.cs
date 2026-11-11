using System.Diagnostics;
using System.Globalization;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Manages the lifecycle of the bundled ghelper-audio native helper.
///
/// The helper is a small PipeWire client that runs the rnnoise -> EQ ->
/// delay -> reverb chain and exposes a virtual "G-Helper Microphone" audio
/// source. It receives line-based commands on stdin and emits fixed-size
/// binary audio frames on stdout (see audio-helper/protocol.h).
///
/// Lifetime: single instance owned by the application. Start() spawns the
/// helper, Stop() asks it to exit cleanly. If it crashes, we restart up to
/// a small budget; beyond that we surface an error and stay disabled.
/// </summary>
public sealed class AudioHelper : IDisposable
{
    public static AudioHelper Instance { get; } = new AudioHelper();

    private readonly object _lock = new();
    private Process? _proc;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private readonly Queue<DateTime> _recentCrashes = new();
    private const int CrashBudget = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);

    public event Action<AudioFrame>? FrameReceived;
    public event Action<string>? ErrorReported;

    /// <summary>True if the helper binary is available on this build.</summary>
    public bool IsAvailable => HelperPath() != null;

    public bool IsRunning
    {
        get { lock (_lock) return _proc != null && !_proc.HasExited; }
    }

    /// <summary>Returns the cached path of the extracted helper binary, or null.</summary>
    private static string? HelperPath() => NativeLibExtractor.FindTool("ghelper-audio");

    public bool Start()
    {
        lock (_lock)
        {
            if (_proc != null && !_proc.HasExited)
                return true;

            string? path = HelperPath();
            if (path == null)
            {
                ErrorReported?.Invoke("ghelper-audio binary not found");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _proc = Process.Start(psi);
                if (_proc == null)
                {
                    ErrorReported?.Invoke("failed to spawn ghelper-audio");
                    return false;
                }
                _proc.EnableRaisingEvents = true;
                _proc.Exited += OnProcExited;

                _readerCts = new CancellationTokenSource();
                var token = _readerCts.Token;
                _readerTask = Task.Run(() => ReadLoop(_proc, token), token);

                // Drain stderr in the background (errors + diagnostics)
                _ = Task.Run(() => ReadErrLoop(_proc));

                return true;
            }
            catch (Exception ex)
            {
                ErrorReported?.Invoke("spawn error: " + ex.Message);
                _proc?.Dispose();
                _proc = null;
                return false;
            }
        }
    }

    private void OnProcExited(object? sender, EventArgs e)
    {
        _recentCrashes.Enqueue(DateTime.UtcNow);
        while (_recentCrashes.Count > 0 &&
               DateTime.UtcNow - _recentCrashes.Peek() > CrashWindow)
            _recentCrashes.Dequeue();
        if (_recentCrashes.Count >= CrashBudget)
            ErrorReported?.Invoke($"ghelper-audio crashed {_recentCrashes.Count} times in {CrashWindow.TotalSeconds:F0}s; staying disabled");
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_proc == null)
                return;
            try
            {
                if (!_proc.HasExited)
                {
                    _proc.StandardInput.WriteLine("QUIT");
                    _proc.StandardInput.Flush();
                    if (!_proc.WaitForExit(500))
                        _proc.Kill(entireProcessTree: false);
                }
            }
            catch { }
            try
            { _readerCts?.Cancel(); }
            catch { }
            try
            { _readerTask?.Wait(500); }
            catch { }
            _proc.Dispose();
            _proc = null;
            _readerCts = null;
            _readerTask = null;
        }
    }

    public void Dispose() => Stop();

    // --- live command API ---------------------------------------------------

    public void SetRnnoise(bool on)
    {
        AudioState.Instance.RnnoiseOn = on;
        Send($"RNN {(on ? 1 : 0)}");
    }
    public void SetEq(bool on)
    {
        AudioState.Instance.EqOn = on;
        Send($"EQ {(on ? 1 : 0)}");
    }
    public void SetDelay(bool on)
    {
        AudioState.Instance.DelayOn = on;
        Send($"DLY {(on ? 1 : 0)}");
    }
    public void SetReverb(bool on)
    {
        AudioState.Instance.ReverbOn = on;
        Send($"RVB {(on ? 1 : 0)}");
    }
    public void SetMonitor(bool on)
    {
        AudioState.Instance.MonitorOn = on;
        Send($"MON {(on ? 1 : 0)}");
    }
    /// <summary>
    /// Master output gain per-mille (0..2000, 1000 = unity, 2000 = +6 dB
    /// soft-clipped). Applied at the virtual-source stream so apps
    /// recording from G-Helper Mic hear the user-chosen level.
    /// </summary>
    public void SetMasterVolume(int mille)
    {
        if (mille < 0)
            mille = 0;
        if (mille > 2000)
            mille = 2000;
        AudioState.Instance.MasterVolume = mille;
        Send(string.Format(CultureInfo.InvariantCulture, "VOL {0}", mille));
    }
    /// <summary>
    /// Post-EQ uniform gain in centi-dB applied immediately after the band
    /// cascade. The "drag the response line" gesture in the EQ window
    /// pushes through here. Clamped to +/- 3600 centi-dB (+/- 36 dB) on
    /// both ends so the helper's linear multiplier stays sane.
    /// </summary>
    public void SetEqGain(int centiDb)
    {
        if (centiDb < -3600)
            centiDb = -3600;
        if (centiDb > 3600)
            centiDb = 3600;
        AudioState.Instance.EqGainCentiDb = centiDb;
        Send(string.Format(CultureInfo.InvariantCulture, "EGN {0}", centiDb));
    }
    /// <summary>
    /// RNNoise post-gate aggressiveness, per-mille (0..1000). Higher
    /// values cut more residual hiss between phrases; lower values stay
    /// closer to the raw model output. Default 700 gives a Zoom-like
    /// feel. Drives the helper's <c>AGG</c> command.
    /// </summary>
    public void SetRnnoiseAggressiveness(int mille)
    {
        if (mille < 0)
            mille = 0;
        if (mille > 1000)
            mille = 1000;
        AudioState.Instance.RnnoiseAggressiveness = mille;
        Send(string.Format(CultureInfo.InvariantCulture, "AGG {0}", mille));
    }

    /// <summary>
    /// Granular pitch shifter offset in centi-semitones. Clamped to
    /// +/- 2400 (+/- 24 st) so the shifter's read-head sweep stays
    /// inside the grain buffer with comfortable margin.
    /// </summary>
    public void SetPitchShift(int centisemis)
    {
        if (centisemis < -2400)
            centisemis = -2400;
        if (centisemis > 2400)
            centisemis = 2400;
        AudioState.Instance.VoicePitchShiftCentisemis = centisemis;
        Send(string.Format(CultureInfo.InvariantCulture, "PSH {0}", centisemis));
    }

    /// <summary>
    /// Autotune (chromatic snap) on / off. When on the pitch shifter is
    /// driven by the snap-to-nearest-semitone helper instead of the
    /// manual offset.
    /// </summary>
    public void SetAutotune(bool on)
    {
        AudioState.Instance.VoiceAutotuneOn = on;
        Send(string.Format(CultureInfo.InvariantCulture, "ATN {0}", on ? 1 : 0));
    }

    /// <summary>
    /// Bit-crusher: amplitude quantisation depth + sample-and-hold
    /// downsample factor. <paramref name="bits"/> = 0 keeps the bypass
    /// path. Clamped to safe ranges helper-side.
    /// </summary>
    public void SetBitcrush(int bits, int downsample)
    {
        if (bits < 0)
            bits = 0;
        if (bits > 15)
            bits = 15;
        if (downsample < 1)
            downsample = 1;
        if (downsample > 64)
            downsample = 64;
        AudioState.Instance.VoiceBitcrushBits = bits;
        AudioState.Instance.VoiceBitcrushDownsample = downsample;
        Send(string.Format(CultureInfo.InvariantCulture, "BCR {0} {1}", bits, downsample));
    }

    /// <summary>
    /// Vocoder Matrix-intensity per-mille (0..1000). Controls the carrier
    /// timbre blend: 0 = clean two-oscillator analog vocoder, 1000 = full
    /// ring-modulated sub-octave Sentinel character. Presets stamp this
    /// so e.g. Kraftwerk (0) and Matrix Agent (1000) sound distinct even
    /// with similar envelope settings.
    /// </summary>
    public void SetVocoderMatrix(int mille)
    {
        if (mille < 0)
            mille = 0;
        if (mille > 1000)
            mille = 1000;
        AudioState.Instance.VocoderMatrixIntensity = mille;
        Send(string.Format(CultureInfo.InvariantCulture, "MTX {0}", mille));
    }

    /// <summary>
    /// Voice-stage band-pass: high-pass cutoff + low-pass cutoff in Hz.
    /// 0 in either field bypasses that side. Telephone band = 300 / 3400;
    /// Vader muffled = 0 / 3000; alien tinny radio = 800 / 3000.
    /// </summary>
    public void SetVoiceBandpass(int hpfHz, int lpfHz)
    {
        if (hpfHz < 0)
            hpfHz = 0;
        if (hpfHz > 2000)
            hpfHz = 2000;
        if (lpfHz < 0)
            lpfHz = 0;
        if (lpfHz > 20000)
            lpfHz = 20000;
        var s = AudioState.Instance;
        s.VoiceBandpassHpfHz = hpfHz;
        s.VoiceBandpassLpfHz = lpfHz;
        Send(string.Format(CultureInfo.InvariantCulture, "BPF {0} {1}", hpfHz, lpfHz));
    }

    /// <summary>
    /// Stutter-gate rate + duty cycle. 0 Hz disables. Drives the Cylon
    /// preset's "By Your Command" rhythmic chopping.
    /// </summary>
    public void SetVoiceStutter(int hz, int dutyMille)
    {
        if (hz < 0)
            hz = 0;
        if (hz > 40)
            hz = 40;
        if (dutyMille < 50)
            dutyMille = 50;
        if (dutyMille > 950)
            dutyMille = 950;
        var s = AudioState.Instance;
        s.VoiceStutterHz = hz;
        s.VoiceStutterDutyMille = dutyMille;
        Send(string.Format(CultureInfo.InvariantCulture, "STT {0} {1}", hz, dutyMille));
    }

    /// <summary>
    /// Autotune target pitch in Hz. 0 selects chromatic snap (T-Pain);
    /// a positive value forces monotone snap to that single pitch
    /// (DECtalk Stephen Hawking style). Only consulted while
    /// <see cref="SetAutotune"/> is true.
    /// </summary>
    public void SetVoiceAutotuneTarget(int hz)
    {
        if (hz < 0)
            hz = 0;
        if (hz > 1000)
            hz = 1000;
        AudioState.Instance.VoiceAutotuneTargetHz = hz;
        Send(string.Format(CultureInfo.InvariantCulture, "ATT {0}", hz));
    }
    public void SetVocoder(bool on)
    {
        AudioState.Instance.VocoderOn = on;
        Send($"VOC {(on ? 1 : 0)}");
    }
    /// <summary>
    /// Push a full vocoder parameter set. The helper's stdin format takes
    /// 7 args (mix, carrier_hz, attack_ms, release_ms, detune, follow,
    /// shift_semis). When <paramref name="follow"/> is true the helper
    /// ignores <paramref name="carrierHz"/> and uses the tracked voice
    /// pitch transposed by <paramref name="pitchShiftSemis"/> instead.
    /// </summary>
    public void SetVocoder(int mixMille, int carrierHz, int attackMs, int releaseMs,
                           int detuneMille, bool follow, int pitchShiftSemis)
    {
        var s = AudioState.Instance;
        s.VocoderMix = mixMille;
        s.VocoderCarrierHz = carrierHz;
        s.VocoderAttackMs = attackMs;
        s.VocoderReleaseMs = releaseMs;
        s.VocoderDetune = detuneMille;
        s.VocoderFollow = follow;
        s.VocoderPitchShift = pitchShiftSemis;
        Send(string.Format(CultureInfo.InvariantCulture,
                           "VOP {0} {1} {2} {3} {4} {5} {6}",
                           mixMille, carrierHz, attackMs, releaseMs,
                           detuneMille, follow ? 1 : 0, pitchShiftSemis));
    }

    /// <summary>
    /// Toggle a chain effect (or master enable) and emit the right command
    /// to the helper. Returns the new state. Safe to call regardless of
    /// helper running state - if the helper is not running, the master
    /// enable will be started; effect toggles just update AudioState so
    /// the new value applies whenever the helper next starts.
    /// </summary>
    public bool ToggleRnnoise() { var v = !AudioState.Instance.RnnoiseOn; SetRnnoise(v); return v; }
    public bool ToggleEq() { var v = !AudioState.Instance.EqOn; SetEq(v); return v; }
    public bool ToggleDelay() { var v = !AudioState.Instance.DelayOn; SetDelay(v); return v; }
    public bool ToggleReverb() { var v = !AudioState.Instance.ReverbOn; SetReverb(v); return v; }
    public bool ToggleMonitor() { var v = !AudioState.Instance.MonitorOn; SetMonitor(v); return v; }
    public bool ToggleVocoder() { var v = !AudioState.Instance.VocoderOn; SetVocoder(v); return v; }

    /// <summary>
    /// Master enable/disable. Starts or stops the helper process and
    /// persists the user's intent in AppConfig so the app remembers the
    /// state across launches.
    /// </summary>
    public bool ToggleMaster()
    {
        if (IsRunning)
        {
            Stop();
            AppConfig.Set("audio_enabled", 0);
            return false;
        }
        bool started = Start();
        AppConfig.Set("audio_enabled", started ? 1 : 0);
        if (started)
            ReapplyAllState();
        return started;
    }

    /// <summary>
    /// Push every persisted parameter (chain on/off + per-effect knobs) to a
    /// freshly-spawned helper so it matches the UI immediately. Called by
    /// <see cref="ToggleMaster"/> after a successful start and by the auto-
    /// start path on app launch.
    /// </summary>
    public void ReapplyAllState()
    {
        var s = AudioState.Instance;
        // Chain on/off toggles.
        SetRnnoise(s.RnnoiseOn);
        SetVocoder(s.VocoderOn);
        SetEq(s.EqOn);
        SetDelay(s.DelayOn);
        SetReverb(s.ReverbOn);
        if (s.MonitorOn)
            SetMonitor(true);
        // Per-effect parameters.
        SetDelay(s.DelayMs, s.DelayFeedback, s.DelayMix);
        SetReverb(s.ReverbRoom, s.ReverbDamp, s.ReverbWidth, s.ReverbMix);
        SetVocoder(s.VocoderMix, s.VocoderCarrierHz, s.VocoderAttackMs,
                   s.VocoderReleaseMs, s.VocoderDetune,
                   s.VocoderFollow, s.VocoderPitchShift);
        SetMasterVolume(s.MasterVolume);
        SetEqGain(s.EqGainCentiDb);
        SetRnnoiseAggressiveness(s.RnnoiseAggressiveness);
        SetPitchShift(s.VoicePitchShiftCentisemis);
        SetAutotune(s.VoiceAutotuneOn);
        SetBitcrush(s.VoiceBitcrushBits, s.VoiceBitcrushDownsample);
        SetVocoderMatrix(s.VocoderMatrixIntensity);
        SetVoiceBandpass(s.VoiceBandpassHpfHz, s.VoiceBandpassLpfHz);
        SetVoiceStutter(s.VoiceStutterHz, s.VoiceStutterDutyMille);
        SetVoiceAutotuneTarget(s.VoiceAutotuneTargetHz);
        for (int i = 0; i < s.EqBands.Length; i++)
        {
            var b = s.EqBands[i];
            SetEqBand(i, b.Type, b.FreqHz, b.QMille, b.GainCentiDb);
        }
        // Re-target capture to the user's last-chosen mic if they set one.
        if (!string.IsNullOrEmpty(s.SelectedSource))
            SetSource(s.SelectedSource);
    }

    /// <summary>
    /// Point the capture stream at the given PipeWire node name. Pass
    /// <c>null</c> or <c>"default"</c> to let wireplumber pick the system
    /// default source.
    /// </summary>
    public void SetSource(string? nodeName)
    {
        var t = string.IsNullOrWhiteSpace(nodeName) ? "default" : nodeName!.Trim();
        AudioState.Instance.SelectedSource = (t == "default") ? "" : t;
        Send($"SRC {t}");
    }

    public void SetEqBand(int idx, int type, int freqHz, int qMille, int gainCentiDb)
    {
        if (idx >= 0 && idx < AudioState.NumEqBands)
        {
            var b = AudioState.Instance.EqBands[idx];
            b.Type = type;
            b.FreqHz = freqHz;
            b.QMille = qMille;
            b.GainCentiDb = gainCentiDb;
            b.Persist();
        }
        Send(string.Format(CultureInfo.InvariantCulture,
                           "EQB {0} {1} {2} {3} {4}",
                           idx, type, freqHz, qMille, gainCentiDb));
    }

    public void SetDelay(int ms, int feedbackMille, int mixMille)
    {
        var s = AudioState.Instance;
        s.DelayMs = ms;
        s.DelayFeedback = feedbackMille;
        s.DelayMix = mixMille;
        Send(string.Format(CultureInfo.InvariantCulture,
                           "DLP {0} {1} {2}",
                           ms, feedbackMille, mixMille));
    }

    public void SetReverb(int roomMille, int dampMille, int widthMille, int mixMille)
    {
        var s = AudioState.Instance;
        s.ReverbRoom = roomMille;
        s.ReverbDamp = dampMille;
        s.ReverbWidth = widthMille;
        s.ReverbMix = mixMille;
        Send(string.Format(CultureInfo.InvariantCulture,
                           "RVP {0} {1} {2} {3}",
                           roomMille, dampMille, widthMille, mixMille));
    }

    private void Send(string line)
    {
        lock (_lock)
        {
            if (_proc == null || _proc.HasExited)
                return;
            try
            {
                _proc.StandardInput.WriteLine(line);
                _proc.StandardInput.Flush();
            }
            catch { }
        }
    }

    // --- reader loops -------------------------------------------------------

    private void ReadLoop(Process proc, CancellationToken token)
    {
        try
        {
            var stream = proc.StandardOutput.BaseStream;
            var reader = new BinaryReader(stream);
            var frame = new AudioFrame();
            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                if (!frame.TryReadFrom(reader))
                    break;
                FrameReceived?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            ErrorReported?.Invoke("reader: " + ex.Message);
        }
    }

    private void ReadErrLoop(Process proc)
    {
        try
        {
            string? line, last = null;
            while ((line = proc.StandardError.ReadLine()) != null)
            {
                last = line;
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("err=", StringComparison.OrdinalIgnoreCase))
                    ErrorReported?.Invoke(line);
            }
            // stderr closed: the helper exited, e.g. the loader could not start it.
            if (proc.WaitForExit(1000) && proc.ExitCode != 0)
                Logger.WriteLine($"AudioHelper: ghelper-audio exited with code {proc.ExitCode}: {last}");
        }
        catch { }
    }
}

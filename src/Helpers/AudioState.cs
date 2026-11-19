namespace GHelper.Linux.Helpers;

/// <summary>
/// In-memory mirror of the parameter values currently active in the
/// ghelper-audio helper, backed by <see cref="AppConfig"/> so every change
/// persists across launches. All UIs (main window toggle, AudioWindow
/// checkboxes, mini-windows, hotkeys) read and write through this state.
///
/// Lifetime: process-wide singleton. Mutated only from the UI thread.
/// </summary>
public sealed class AudioState
{
    public const int NumEqBands = 9;

    /// <summary>
    /// Factory defaults for each EQ band. Declared BEFORE <see cref="Instance"/>
    /// so the static field initializer runs first - the singleton's
    /// constructor reads this array to seed user-overridable values, and
    /// C# initializes static fields in declaration order within a type.
    /// Reordering would trigger a NullReferenceException during type init.
    /// Stays in sync with the helper's params_init() defaults.
    /// </summary>
    public static readonly EqBand[] Defaults =
    {
        new EqBand { Index = 0, Type = 3, FreqHz =    80, QMille =  707, GainCentiDb =    0 },
        new EqBand { Index = 1, Type = 1, FreqHz =   120, QMille =  707, GainCentiDb =  300 },
        new EqBand { Index = 2, Type = 0, FreqHz =   250, QMille = 1000, GainCentiDb =    0 },
        new EqBand { Index = 3, Type = 0, FreqHz =   400, QMille = 1000, GainCentiDb = -200 },
        new EqBand { Index = 4, Type = 0, FreqHz =  1500, QMille = 1000, GainCentiDb =    0 },
        new EqBand { Index = 5, Type = 0, FreqHz =  3500, QMille =  700, GainCentiDb =  300 },
        new EqBand { Index = 6, Type = 0, FreqHz =  6000, QMille = 1000, GainCentiDb =    0 },
        new EqBand { Index = 7, Type = 2, FreqHz =  9000, QMille =  700, GainCentiDb =  200 },
        new EqBand { Index = 8, Type = 0, FreqHz = 12000, QMille = 1000, GainCentiDb =    0 },
    };

    public static AudioState Instance { get; } = new AudioState();

    private AudioState()
    {
        // Constructor reads everything from AppConfig with sensible defaults
        // so a fresh install matches the helper's params_init() values, and
        // a returning user gets exactly what they had last time.
        _rnnoiseOn = AppConfig.Get("audio_rnnoise_on", 1) == 1;
        _noiseModel = AppConfig.Get("audio_noise_model", 0) == 1 ? 1 : 0;
        _vocoderOn = AppConfig.Get("audio_vocoder_on", 0) == 1;
        _eqOn = AppConfig.Get("audio_eq_on", 0) == 1;
        _delayOn = AppConfig.Get("audio_delay_on", 0) == 1;
        _reverbOn = AppConfig.Get("audio_reverb_on", 0) == 1;
        _monitorOn = AppConfig.Get("audio_monitor_on", 0) == 1;

        _delayMs = AppConfig.Get("audio_delay_ms", 250);
        _delayFeedback = AppConfig.Get("audio_delay_feedback", 350);
        _delayMix = AppConfig.Get("audio_delay_mix", 300);

        _reverbRoom = AppConfig.Get("audio_reverb_room", 700);
        _reverbDamp = AppConfig.Get("audio_reverb_damp", 500);
        _reverbWidth = AppConfig.Get("audio_reverb_width", 800);
        _reverbMix = AppConfig.Get("audio_reverb_mix", 350);

        _vocoderMix = AppConfig.Get("audio_vocoder_mix", 700);
        _vocoderCarrier = AppConfig.Get("audio_vocoder_carrier", 110);
        _vocoderAttackMs = AppConfig.Get("audio_vocoder_attack", 5);
        _vocoderReleaseMs = AppConfig.Get("audio_vocoder_release", 30);
        _vocoderDetune = AppConfig.Get("audio_vocoder_detune", 20);
        // Follow ON by default so the vocoder actually tracks the user's
        // voice out of the box - much more intuitive than the constant
        // monotone carrier. Shift=0 means "robot at my pitch".
        _vocoderFollow = AppConfig.Get("audio_vocoder_follow", 1) == 1;
        _vocoderPitchShift = AppConfig.Get("audio_vocoder_pitch_shift", 0);

        _selectedSource = AppConfig.GetString("audio_source") ?? "";

        // Master output gain, per-mille (0..2000). 1000 = unity, 2000 = +6 dB
        // with soft clipping. Defaults to unity so a returning user without
        // a stored value hears no surprise level change vs the original mic.
        _masterVolume = AppConfig.Get("audio_master_volume", 1000);

        // Post-EQ uniform gain in centi-dB. Drives the "drag the response
        // curve vertically" gesture in the EQ window. Independent of the
        // per-band gains; persists across launches like every other knob.
        _eqGainCentiDb = AppConfig.Get("audio_eq_gain", 0);

        // Denoise post-gate aggressiveness, per-mille (0..1000). Default
        // 700 gives a Zoom-like feel: speech is unchanged, room tone
        // drops to near-inaudible during pauses. 0 = bare denoiser output.
        _rnnoiseAggressiveness = AppConfig.Get("audio_rnnoise_aggressiveness", 700);

        // Voice-effects parameters used by vocoder presets to nail the
        // character their names promise. Defaults are all bypass so a
        // fresh install behaves exactly like the previous build.
        _voicePitchShiftCentisemis = AppConfig.Get("audio_voice_pitch_shift", 0);
        _voiceAutotuneOn = AppConfig.Get("audio_voice_autotune", 0) == 1;
        _voiceBitcrushBits = AppConfig.Get("audio_voice_bitcrush_bits", 0);
        _voiceBitcrushDownsample = AppConfig.Get("audio_voice_bitcrush_downsample", 1);
        // Vocoder Matrix-intensity per-mille. 0 = clean Kraftwerk/Hawking
        // analog vocoder; 1000 = full Sentinel-with-ring-mod treatment.
        // 500 default matches the previous "always-on" colouration so
        // long-time users hear roughly what they used to.
        _vocoderMatrixIntensity = AppConfig.Get("audio_vocoder_matrix", 500);

        // Voice-stage band-pass cutoffs (0 = that side bypassed). Presets
        // use them to dial telephone bandwidth or muffled-LP characters.
        _voiceBandpassHpfHz = AppConfig.Get("audio_voice_bpf_hpf", 0);
        _voiceBandpassLpfHz = AppConfig.Get("audio_voice_bpf_lpf", 0);

        // Stutter gate rate + duty (0 Hz = bypass). 4-8 Hz with ~500
        // duty produces the Cylon "By Your Command" cadence.
        _voiceStutterHz = AppConfig.Get("audio_voice_stutter_hz", 0);
        _voiceStutterDutyMille = AppConfig.Get("audio_voice_stutter_duty", 500);

        // Autotune target Hz. 0 = chromatic snap (T-Pain); >0 = fixed
        // pitch (Hawking monotone).
        _voiceAutotuneTargetHz = AppConfig.Get("audio_voice_autotune_target", 0);

        // Name of the last applied vocoder preset, "" once any knob moves.
        _vocoderPreset = AppConfig.GetString("audio_vocoder_preset") ?? "";

        EqBands = new EqBand[NumEqBands];
        for (int i = 0; i < NumEqBands; i++)
        {
            var d = Defaults[i];
            EqBands[i] = new EqBand
            {
                Index = i,
                Type = AppConfig.Get($"audio_eq{i}_type", d.Type),
                FreqHz = AppConfig.Get($"audio_eq{i}_freq", d.FreqHz),
                QMille = AppConfig.Get($"audio_eq{i}_q", d.QMille),
                GainCentiDb = AppConfig.Get($"audio_eq{i}_gain", d.GainCentiDb),
            };
        }
    }

    // --- Chain effect on/off (default: denoise only, matches helper) -------

    private bool _rnnoiseOn;
    public bool RnnoiseOn
    {
        get => _rnnoiseOn;
        set { _rnnoiseOn = value; AppConfig.Set("audio_rnnoise_on", value ? 1 : 0); }
    }

    /// <summary>Denoise model names, by <see cref="NoiseModel"/> index.</summary>
    public static readonly string[] NoiseModelNames = { "RNNoise", "GTCRN" };

    private int _noiseModel;
    /// <summary>Denoise model: 0 = RNNoise (default), 1 = GTCRN.</summary>
    public int NoiseModel
    {
        get => _noiseModel;
        set { _noiseModel = value; AppConfig.Set("audio_noise_model", value); }
    }

    private bool _eqOn;
    public bool EqOn
    {
        get => _eqOn;
        set { _eqOn = value; AppConfig.Set("audio_eq_on", value ? 1 : 0); }
    }

    private bool _delayOn;
    public bool DelayOn
    {
        get => _delayOn;
        set { _delayOn = value; AppConfig.Set("audio_delay_on", value ? 1 : 0); }
    }

    private bool _reverbOn;
    public bool ReverbOn
    {
        get => _reverbOn;
        set { _reverbOn = value; AppConfig.Set("audio_reverb_on", value ? 1 : 0); }
    }

    private bool _monitorOn;
    public bool MonitorOn
    {
        get => _monitorOn;
        set { _monitorOn = value; AppConfig.Set("audio_monitor_on", value ? 1 : 0); }
    }

    private bool _vocoderOn;
    public bool VocoderOn
    {
        get => _vocoderOn;
        set { _vocoderOn = value; AppConfig.Set("audio_vocoder_on", value ? 1 : 0); }
    }

    // --- Vocoder params ----------------------------------------------------

    private int _vocoderMix;
    public int VocoderMix
    {
        get => _vocoderMix;
        set { _vocoderMix = value; AppConfig.Set("audio_vocoder_mix", value); }
    }
    private int _vocoderCarrier;
    public int VocoderCarrierHz
    {
        get => _vocoderCarrier;
        set { _vocoderCarrier = value; AppConfig.Set("audio_vocoder_carrier", value); }
    }
    private int _vocoderAttackMs;
    public int VocoderAttackMs
    {
        get => _vocoderAttackMs;
        set { _vocoderAttackMs = value; AppConfig.Set("audio_vocoder_attack", value); }
    }
    private int _vocoderReleaseMs;
    public int VocoderReleaseMs
    {
        get => _vocoderReleaseMs;
        set { _vocoderReleaseMs = value; AppConfig.Set("audio_vocoder_release", value); }
    }
    private int _vocoderDetune;
    public int VocoderDetune
    {
        get => _vocoderDetune;
        set { _vocoderDetune = value; AppConfig.Set("audio_vocoder_detune", value); }
    }
    private bool _vocoderFollow;
    /// <summary>
    /// When on, the vocoder carrier tracks the input voice pitch and the
    /// pitch slider becomes a +/- semitone transposition. When off, the
    /// slider is the fixed carrier frequency (Hz) - classic Kraftwerk
    /// vocoder behaviour.
    /// </summary>
    public bool VocoderFollow
    {
        get => _vocoderFollow;
        set { _vocoderFollow = value; AppConfig.Set("audio_vocoder_follow", value ? 1 : 0); }
    }
    private int _vocoderPitchShift;
    /// <summary>
    /// Transposition in equal-tempered semitones, -24..+24. Only applied
    /// when <see cref="VocoderFollow"/> is true; ignored otherwise.
    /// </summary>
    public int VocoderPitchShift
    {
        get => _vocoderPitchShift;
        set { _vocoderPitchShift = value; AppConfig.Set("audio_vocoder_pitch_shift", value); }
    }

    // --- Master output volume (per-mille; 1000 = unity) --------------------

    private int _masterVolume;
    /// <summary>
    /// Master output gain applied at the virtual-source stream. Per-mille
    /// 0..2000 where 1000 = unity (0 dB) and 2000 = +6 dB with soft
    /// clipping. The monitor playback stream stays at unity so self-
    /// checking remains an honest reference.
    /// </summary>
    public int MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = value; AppConfig.Set("audio_master_volume", value); }
    }

    private int _eqGainCentiDb;
    /// <summary>
    /// Uniform gain applied immediately after the EQ band cascade. Drives
    /// the "drag the response curve vertically" UI gesture - shape stays
    /// fixed, the whole line slides up or down. Clamped to +/- 3600
    /// centi-dB (+/- 36 dB) in the helper; the UI is free to drag past
    /// the visible chart bounds within that range.
    /// </summary>
    public int EqGainCentiDb
    {
        get => _eqGainCentiDb;
        set { _eqGainCentiDb = value; AppConfig.Set("audio_eq_gain", value); }
    }

    // --- Denoise post-gate aggressiveness, per-mille ---------------------

    private int _rnnoiseAggressiveness;
    /// <summary>
    /// Strength of the soft gate the helper applies after the denoise
    /// model output. 0 = raw model output (preserves the natural noise
    /// floor between phrases); 1000 = up to -24 dB of additional
    /// attenuation during silence. Default 700.
    /// </summary>
    public int RnnoiseAggressiveness
    {
        get => _rnnoiseAggressiveness;
        set { _rnnoiseAggressiveness = value; AppConfig.Set("audio_rnnoise_aggressiveness", value); }
    }

    // --- Voice-effects parameters (vocoder preset stack) -----------------

    private int _voicePitchShiftCentisemis;
    /// <summary>
    /// Granular pitch shifter offset in centi-semitones (1 semitone = 100).
    /// Range -2400..+2400 = +/- 2 octaves. Drives "Darth Vader" (negative)
    /// and "Chipmunk" (positive) presets; bypassed when 0.
    /// </summary>
    public int VoicePitchShiftCentisemis
    {
        get => _voicePitchShiftCentisemis;
        set { _voicePitchShiftCentisemis = value; AppConfig.Set("audio_voice_pitch_shift", value); }
    }

    private bool _voiceAutotuneOn;
    /// <summary>
    /// When true the pitch shifter is driven by the autotune helper -
    /// snap the live pitch to the nearest equal-tempered semitone. The
    /// "Cher / T-Pain" preset turns this on with the manual shift at 0.
    /// </summary>
    public bool VoiceAutotuneOn
    {
        get => _voiceAutotuneOn;
        set { _voiceAutotuneOn = value; AppConfig.Set("audio_voice_autotune", value ? 1 : 0); }
    }

    private int _voiceBitcrushBits;
    /// <summary>
    /// Amplitude quantisation depth, 0 = bypass, 1..15 = stair-step levels.
    /// Combined with <see cref="VoiceBitcrushDownsample"/> for AM-radio /
    /// 8-bit-computer character on the "Robot Phone" preset.
    /// </summary>
    public int VoiceBitcrushBits
    {
        get => _voiceBitcrushBits;
        set { _voiceBitcrushBits = value; AppConfig.Set("audio_voice_bitcrush_bits", value); }
    }

    private int _voiceBitcrushDownsample;
    /// <summary>
    /// Sample-and-hold factor for the bit crusher. 1 = no downsampling,
    /// N = hold each input sample for N output samples (effective sample
    /// rate = 48k / N).
    /// </summary>
    public int VoiceBitcrushDownsample
    {
        get => _voiceBitcrushDownsample;
        set { _voiceBitcrushDownsample = value; AppConfig.Set("audio_voice_bitcrush_downsample", value); }
    }

    private int _vocoderMatrixIntensity;
    /// <summary>
    /// Vocoder-stage "Matrix intensity" per-mille (0..1000). Controls the
    /// blend between a clean two-oscillator carrier (Kraftwerk / Hawking,
    /// at 0) and the full ring-modulated, sub-octave-stacked, tanh-driven
    /// Sentinel carrier (Matrix Agent, at 1000). Lets presets that share
    /// envelope and detune settings still sound distinct.
    /// </summary>
    public int VocoderMatrixIntensity
    {
        get => _vocoderMatrixIntensity;
        set { _vocoderMatrixIntensity = value; AppConfig.Set("audio_vocoder_matrix", value); }
    }

    private int _voiceBandpassHpfHz;
    /// <summary>
    /// Voice-stage high-pass cutoff in Hz. 0 = bypass. Combined with
    /// <see cref="VoiceBandpassLpfHz"/> to dial in the Robot Phone
    /// telephone band (300 Hz HPF + 3400 Hz LPF) and similar shapes.
    /// </summary>
    public int VoiceBandpassHpfHz
    {
        get => _voiceBandpassHpfHz;
        set { _voiceBandpassHpfHz = value; AppConfig.Set("audio_voice_bpf_hpf", value); }
    }

    private int _voiceBandpassLpfHz;
    /// <summary>Voice-stage low-pass cutoff in Hz. 0 = bypass.</summary>
    public int VoiceBandpassLpfHz
    {
        get => _voiceBandpassLpfHz;
        set { _voiceBandpassLpfHz = value; AppConfig.Set("audio_voice_bpf_lpf", value); }
    }

    private int _voiceStutterHz;
    /// <summary>
    /// Stutter-gate rate in Hz. 0 = bypass. 4-8 Hz with the default
    /// 500-per-mille duty cycle reproduces the Cylon "By Your Command"
    /// stuttering machine cadence.
    /// </summary>
    public int VoiceStutterHz
    {
        get => _voiceStutterHz;
        set { _voiceStutterHz = value; AppConfig.Set("audio_voice_stutter_hz", value); }
    }

    private int _voiceStutterDutyMille;
    /// <summary>
    /// Stutter-gate duty cycle in per-mille (50..950). 500 = exact
    /// square wave; lower values give a shorter "on" pulse for a
    /// more clipped staccato feel.
    /// </summary>
    public int VoiceStutterDutyMille
    {
        get => _voiceStutterDutyMille;
        set { _voiceStutterDutyMille = value; AppConfig.Set("audio_voice_stutter_duty", value); }
    }

    private int _voiceAutotuneTargetHz;
    /// <summary>
    /// Fixed autotune target pitch in Hz. 0 selects the chromatic-snap
    /// behaviour (snap to nearest equal-tempered semitone, T-Pain style).
    /// A positive value forces every voiced frame to that one frequency,
    /// producing the true monotone of a DECtalk-style speech synth.
    /// Only consulted when <see cref="VoiceAutotuneOn"/> is true.
    /// </summary>
    public int VoiceAutotuneTargetHz
    {
        get => _voiceAutotuneTargetHz;
        set { _voiceAutotuneTargetHz = value; AppConfig.Set("audio_voice_autotune_target", value); }
    }

    private string _vocoderPreset;
    /// <summary>Last applied vocoder preset name; "" when knobs were changed after it.</summary>
    public string VocoderPreset
    {
        get => _vocoderPreset;
        set { _vocoderPreset = value ?? ""; AppConfig.Set("audio_vocoder_preset", _vocoderPreset); }
    }

    // --- Selected capture source (PipeWire node name, "" = system default) -

    private string _selectedSource;
    public string SelectedSource
    {
        get => _selectedSource;
        set { _selectedSource = value ?? ""; AppConfig.Set("audio_source", _selectedSource); }
    }

    // --- Delay ------------------------------------------------------------

    private int _delayMs;
    public int DelayMs
    {
        get => _delayMs;
        set { _delayMs = value; AppConfig.Set("audio_delay_ms", value); }
    }
    private int _delayFeedback;
    public int DelayFeedback
    {
        get => _delayFeedback;
        set { _delayFeedback = value; AppConfig.Set("audio_delay_feedback", value); }
    }
    private int _delayMix;
    public int DelayMix
    {
        get => _delayMix;
        set { _delayMix = value; AppConfig.Set("audio_delay_mix", value); }
    }

    // --- Reverb -----------------------------------------------------------

    private int _reverbRoom;
    public int ReverbRoom
    {
        get => _reverbRoom;
        set { _reverbRoom = value; AppConfig.Set("audio_reverb_room", value); }
    }
    private int _reverbDamp;
    public int ReverbDamp
    {
        get => _reverbDamp;
        set { _reverbDamp = value; AppConfig.Set("audio_reverb_damp", value); }
    }
    private int _reverbWidth;
    public int ReverbWidth
    {
        get => _reverbWidth;
        set { _reverbWidth = value; AppConfig.Set("audio_reverb_width", value); }
    }
    private int _reverbMix;
    public int ReverbMix
    {
        get => _reverbMix;
        set { _reverbMix = value; AppConfig.Set("audio_reverb_mix", value); }
    }

    // --- EQ ---------------------------------------------------------------

    public sealed class EqBand
    {
        public int Index;        // immutable slot index
        public int Type;         // 0=peak,1=lowshelf,2=highshelf,3=hp,4=lp,5=notch
        public int FreqHz;
        public int QMille;       // Q * 1000
        public int GainCentiDb;  // gain_db * 100

        public void Persist()
        {
            AppConfig.Set($"audio_eq{Index}_type", Type);
            AppConfig.Set($"audio_eq{Index}_freq", FreqHz);
            AppConfig.Set($"audio_eq{Index}_q", QMille);
            AppConfig.Set($"audio_eq{Index}_gain", GainCentiDb);
        }
    }

    public EqBand[] EqBands { get; }

    // --- Reset helpers ---------------------------------------------------

    public void ResetDelay()
    {
        DelayMs = 250;
        DelayFeedback = 350;
        DelayMix = 300;
    }
    public void ResetReverb()
    {
        ReverbRoom = 700;
        ReverbDamp = 500;
        ReverbWidth = 800;
        ReverbMix = 350;
    }
    public void ResetVocoder()
    {
        VocoderMix = 700;
        VocoderCarrierHz = 110;
        VocoderAttackMs = 5;
        VocoderReleaseMs = 30;
        VocoderDetune = 20;
        VocoderFollow = true;
        VocoderPitchShift = 0;
        // Voice-effects are part of the vocoder preset stack, so a reset
        // clears them too. Otherwise after a "Chipmunk" preset, pressing
        // Reset would still leave you sounding +12 semitones up.
        VoicePitchShiftCentisemis = 0;
        VoiceAutotuneOn = false;
        VoiceBitcrushBits = 0;
        VoiceBitcrushDownsample = 1;
        VocoderMatrixIntensity = 500;
        VoiceBandpassHpfHz = 0;
        VoiceBandpassLpfHz = 0;
        VoiceStutterHz = 0;
        VoiceStutterDutyMille = 500;
        VoiceAutotuneTargetHz = 0;
        VocoderPreset = "";
    }
    public void ResetEqBand(int i)
    {
        if (i < 0 || i >= NumEqBands)
            return;
        var d = Defaults[i];
        var b = EqBands[i];
        b.Type = d.Type;
        b.FreqHz = d.FreqHz;
        b.QMille = d.QMille;
        b.GainCentiDb = d.GainCentiDb;
        b.Persist();
    }
    public void ResetEqToDefaults()
    {
        for (int i = 0; i < NumEqBands; i++)
            ResetEqBand(i);
        EqGainCentiDb = 0;
    }
    public void ResetRnnoise() { /* no params - just a toggle */ }
}

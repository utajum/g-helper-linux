namespace GHelper.Linux.Helpers;

/// <summary>
/// One audio analysis snapshot from the ghelper-audio helper.
///
/// Wire format: little-endian binary, 2600 bytes total. Layout MUST stay in
/// sync with audio-helper/protocol.h (struct gha_frame).
///
/// Protocol v2 (WIP, not shipped):
///   - VAD always meaningful (RNNoise model runs even when bypassed).
///   - NoiseReductionDb is real reduction from the denoiser (positive scale,
///     0 = nothing removed); replaces the old "rms_in - rms_out" delta.
///   - TrackedPitchHz exposes the pitch tracker output for UI hints.
/// </summary>
public sealed class AudioFrame
{
    public const uint Magic = 0x47484146u; // "GHAF"
    public const uint ExpectedVersion = 2u;
    public const int WaveformSamples = 256;
    public const int SpectrumBins = 64;
    public const int PacketSize = 4 + 4 + 4 + 4              // header
                                       + 5 * 4                       // scalars
                                       + WaveformSamples * 4 * 2     // waveforms
                                       + SpectrumBins * 4 * 2;       // spectra

    public uint Seq;
    public uint Flags;
    public bool RnnoiseOn => (Flags & 1u) != 0;
    public bool EqOn => (Flags & 2u) != 0;
    public bool DelayOn => (Flags & 4u) != 0;
    public bool ReverbOn => (Flags & 8u) != 0;
    public bool MonitorOn => (Flags & 16u) != 0;
    public bool VocoderOn => (Flags & 32u) != 0;

    public float VadProb;
    public float RmsInDb;
    public float RmsOutDb;
    /// <summary>
    /// Real noise reduction performed by the denoiser this window. Positive
    /// dB-above-silence scale: 0 = nothing removed (denoise bypassed or
    /// quiet mic), ~30-40 = aggressive denoise on a noisy mic. Replaces
    /// the previous whole-chain rms_in - rms_out delta which was
    /// misleadingly affected by every other DSP stage.
    /// </summary>
    public float NoiseReductionDb;
    /// <summary>Last detected voice pitch in Hz, held across silence.</summary>
    public float TrackedPitchHz;

    public readonly float[] WaveformIn = new float[WaveformSamples];
    public readonly float[] WaveformOut = new float[WaveformSamples];
    public readonly float[] SpectrumIn = new float[SpectrumBins];
    public readonly float[] SpectrumOut = new float[SpectrumBins];

    /// <summary>
    /// Parse one packet from a stream. Returns false on magic mismatch or EOF.
    /// Buffer is reused across calls (the caller passes a pre-allocated frame).
    /// </summary>
    public bool TryReadFrom(BinaryReader r)
    {
        try
        {
            uint magic = r.ReadUInt32();
            if (magic != Magic)
                return false;
            uint ver = r.ReadUInt32();
            if (ver != ExpectedVersion)
                return false;
            Seq = r.ReadUInt32();
            Flags = r.ReadUInt32();

            VadProb = r.ReadSingle();
            RmsInDb = r.ReadSingle();
            RmsOutDb = r.ReadSingle();
            NoiseReductionDb = r.ReadSingle();
            TrackedPitchHz = r.ReadSingle();

            for (int i = 0; i < WaveformSamples; i++)
                WaveformIn[i] = r.ReadSingle();
            for (int i = 0; i < WaveformSamples; i++)
                WaveformOut[i] = r.ReadSingle();
            for (int i = 0; i < SpectrumBins; i++)
                SpectrumIn[i] = r.ReadSingle();
            for (int i = 0; i < SpectrumBins; i++)
                SpectrumOut[i] = r.ReadSingle();

            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}

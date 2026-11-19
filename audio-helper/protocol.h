/* G-Helper audio helper: shared IPC protocol header.
 *
 * Binary audio-frame layout written on stdout (little-endian),
 * line-based commands read on stdin.
 *
 * Frame rate: ~60 Hz (every 800 samples at 48 kHz).
 * Packet size: 2600 bytes (16 header + 20 scalars + 2048 waveforms + 512 spectra).
 *
 * Protocol v2 (WIP, not shipped):
 *   - VAD always runs (RNNoise model invoked regardless of bypass flag),
 *     so vad_prob is meaningful even when rnnoise is bypassed.
 *   - noise_reduction_db is the *actual* energy removed by the denoiser
 *     (RMS of input minus RMS of denoised output), not whole-chain Δ.
 *     Reads 0 when denoise is bypassed.
 *   - VOP command takes 7 args including pitch-follow + transpose.
 */
#ifndef GHELPER_AUDIO_PROTOCOL_H
#define GHELPER_AUDIO_PROTOCOL_H

#include <stdint.h>

#define GHA_MAGIC 0x47484146u /* "GHAF" */
#define GHA_PROTOCOL_VERSION 2u

#define GHA_WAVEFORM_SAMPLES 256
#define GHA_SPECTRUM_BINS 64
#define GHA_EQ_BANDS 9

#pragma pack(push, 4)

struct gha_frame
{
    uint32_t magic;   /* GHA_MAGIC */
    uint32_t version; /* GHA_PROTOCOL_VERSION */
    uint32_t seq;     /* monotonically increasing */
    uint32_t flags;   /* bit 0: rnnoise on, 1: eq on, 2: delay on, 3: reverb on, 4: monitor on, 5: vocoder on */

    float vad_prob;           /* 0..1 voice-activity prob from rnnoise; ALWAYS computed */
    float rms_in_db;          /* -inf..0 dBFS RMS of raw input */
    float rms_out_db;         /* -inf..0 dBFS RMS post-chain */
    float noise_reduction_db; /* dB RMS of (denoise in - out); 0 when denoise bypassed */
    float tracked_pitch_hz;   /* detected voice pitch (0 when silence/no track) */

    float waveform_in[GHA_WAVEFORM_SAMPLES]; /* downsampled mono, -1..1 */
    float waveform_out[GHA_WAVEFORM_SAMPLES];

    float spectrum_in[GHA_SPECTRUM_BINS]; /* log-magnitude dB, -80..0 */
    float spectrum_out[GHA_SPECTRUM_BINS];
};

#pragma pack(pop)

/* Stdin command format (line-terminated, ASCII):
 *
 *   SRC <pw-node-name|default>
 *                       point the capture stream at a specific source
 *                       node. "default" or empty arg = let wireplumber
 *                       choose (PW_ID_ANY).
 *
 *   MON <0|1>           monitor: when 1, route processed audio to the
 *                       default sink so the user can hear what their
 *                       virtual mic sounds like.
 *
 *   RNN <0|1>           enable/disable denoise
 *   NSE <gtcrn|rnnoise> denoise model (default gtcrn); RNNoise always gives the VAD
 *   VOC <0|1>           enable/disable vocoder
 *   EQ  <0|1>           enable/disable parametric EQ
 *   DLY <0|1>           enable/disable delay
 *   RVB <0|1>           enable/disable reverb
 *
 *   EQB <idx> <type> <freq_hz> <q> <gain_db>
 *                       set EQ band idx (0..8), type 0=peak 1=lowshelf
 *                       2=highshelf 3=highpass 4=lowpass 5=notch
 *
 *   DLP <time_ms> <feedback_0_1> <mix_0_1>
 *                       set delay params
 *
 *   RVP <room_0_1> <damp_0_1> <width_0_1> <mix_0_1>
 *                       set reverb params (Schroeder)
 *
 *   VOP <mix_0_1> <carrier_hz> <attack_ms> <release_ms> <detune_0_1> <follow_0_1> <shift_semis>
 *                       set vocoder params. mix/detune are per-mille.
 *                       follow=1 makes the carrier track detected voice
 *                       pitch; shift_semis (-24..+24) transposes when
 *                       following. carrier_hz is used only when follow=0.
 *
 *   VOL <0..2000>       master gain per-mille for the virtual-source output.
 *                       1000 = unity, 2000 = +6 dB (soft-clipped via tanh).
 *                       Monitor playback is unaffected.
 *
 *   QUIT
 *
 * Frame flags layout (uint32 LE):
 *   bit 0: rnnoise on
 *   bit 1: EQ on
 *   bit 2: delay on
 *   bit 3: reverb on
 *   bit 4: monitor on
 *   bit 5: vocoder on
 */

#endif

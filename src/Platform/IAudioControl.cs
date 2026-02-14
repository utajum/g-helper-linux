namespace GHelper.Linux.Platform;

/// <summary>
/// Abstraction over audio control (mic mute, speaker mute).
/// Windows: NAudio WASAPI/CoreAudio
/// Linux: PulseAudio (pactl) or PipeWire (wpctl)
/// </summary>
public interface IAudioControl
{
    /// <summary>Toggle microphone mute state.</summary>
    void ToggleMicMute();

    /// <summary>Get microphone mute state.</summary>
    bool IsMicMuted();

    /// <summary>Toggle speaker mute state.</summary>
    void ToggleSpeakerMute();

    /// <summary>Get speaker mute state.</summary>
    bool IsSpeakerMuted();
}

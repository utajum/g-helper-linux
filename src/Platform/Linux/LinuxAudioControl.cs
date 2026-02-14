namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// Linux audio control via PulseAudio (pactl) or PipeWire (wpctl).
/// Replaces NAudio WASAPI/CoreAudio on Windows.
/// </summary>
public class LinuxAudioControl : IAudioControl
{
    private enum AudioSystem { PulseAudio, PipeWire, None }

    private readonly AudioSystem _system;

    public LinuxAudioControl()
    {
        // Detect available audio system
        if (SysfsHelper.RunCommand("which", "wpctl") != null)
            _system = AudioSystem.PipeWire;
        else if (SysfsHelper.RunCommand("which", "pactl") != null)
            _system = AudioSystem.PulseAudio;
        else
            _system = AudioSystem.None;

        Helpers.Logger.WriteLine($"Audio system: {_system}");
    }

    public void ToggleMicMute()
    {
        switch (_system)
        {
            case AudioSystem.PipeWire:
                SysfsHelper.RunCommand("wpctl", "set-mute @DEFAULT_AUDIO_SOURCE@ toggle");
                break;
            case AudioSystem.PulseAudio:
                SysfsHelper.RunCommand("pactl", "set-source-mute @DEFAULT_SOURCE@ toggle");
                break;
        }
    }

    public bool IsMicMuted()
    {
        switch (_system)
        {
            case AudioSystem.PipeWire:
                var wpOutput = SysfsHelper.RunCommand("wpctl", "get-volume @DEFAULT_AUDIO_SOURCE@");
                return wpOutput?.Contains("[MUTED]") ?? false;
            case AudioSystem.PulseAudio:
                var paOutput = SysfsHelper.RunCommand("pactl", "get-source-mute @DEFAULT_SOURCE@");
                return paOutput?.Contains("yes") ?? false;
            default:
                return false;
        }
    }

    public void ToggleSpeakerMute()
    {
        switch (_system)
        {
            case AudioSystem.PipeWire:
                SysfsHelper.RunCommand("wpctl", "set-mute @DEFAULT_AUDIO_SINK@ toggle");
                break;
            case AudioSystem.PulseAudio:
                SysfsHelper.RunCommand("pactl", "set-sink-mute @DEFAULT_SINK@ toggle");
                break;
        }
    }

    public bool IsSpeakerMuted()
    {
        switch (_system)
        {
            case AudioSystem.PipeWire:
                var wpOutput = SysfsHelper.RunCommand("wpctl", "get-volume @DEFAULT_AUDIO_SINK@");
                return wpOutput?.Contains("[MUTED]") ?? false;
            case AudioSystem.PulseAudio:
                var paOutput = SysfsHelper.RunCommand("pactl", "get-sink-mute @DEFAULT_SINK@");
                return paOutput?.Contains("yes") ?? false;
            default:
                return false;
        }
    }
}

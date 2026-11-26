namespace GHelper.Linux.Display;

// MiniLED backlight dimming mode.
//
// Single owner for the cycle order. The platform layer
// (LinuxAsusWmi.Get/SetMiniLedMode/GetMiniLedModeCount) knows the raw sysfs
// value and how many modes the panel accepts; picking the next one lives here
// so the tray button and the hotkey cannot disagree.
//
// Mode count is panel-dependent: 2 (off/on) or 3 (off/multi-zone/multi-zone
// strong). Hardcoding either one breaks the other - writing 2 to a two-mode
// panel is rejected, and a two-step toggle can never reach mode 2 on a
// three-mode panel.
public static class MiniLed
{
    /// <summary>
    /// Advance to the next mode, wrapping at the panel's mode count.
    /// Returns the applied mode, or -1 when unsupported.
    /// </summary>
    public static int CycleMode()
    {
        var wmi = App.Wmi;
        if (wmi == null)
            return -1;

        int count = wmi.GetMiniLedModeCount();
        if (count < 2)
            return -1;

        // A negative read means the attr is unreadable; start from off.
        int current = wmi.GetMiniLedMode();
        int next = current < 0 ? 0 : (current + 1) % count;

        wmi.SetMiniLedMode(next);
        return next;
    }

    /// <summary>
    /// Write back the mode the firmware reports. On some models the panel comes
    /// up in multi-zone after a cold boot while the firmware still reports the
    /// saved mode.
    /// </summary>
    public static void Reapply(Platform.IHardwareControl? wmi)
    {
        if (wmi == null || !Helpers.AppConfig.IsForceMiniled())
            return;

        int mode = wmi.GetMiniLedMode();
        if (mode < 0)
            return;

        wmi.SetMiniLedMode(mode);
        Helpers.Logger.WriteLine($"MiniLED: re-applied mode {mode}");
    }
}

using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Input;

/// <summary>
/// Hotkey and configurable key binding dispatch. Maps hardware events and
/// binding names to actions and executes them. Counterpart of the Windows
/// InputDispatcher; lives here so App stays a lifecycle shell.
/// </summary>
public static class InputDispatcher
{
    // Debounce rapid FnF5: avoids ACPI mutex contention between our
    // sysfs writes (process ctx) and hid_asus WMI calls (BH ctx).
    private static CancellationTokenSource? _perfDebounce;
    private static int _perfCycleCount;

    // Legacy event codes for non-configurable keys
    public const int EventKbBrightnessUp = 196;   // Fn+F3
    public const int EventKbBrightnessDown = 197;  // Fn+F2
    public const int EventTouchpadToggle = 107;    // Fn+F10

    /// <summary>
    /// Available actions for configurable key bindings (app-internal only).
    /// Keys = action ID stored in config, Values = display name for UI.
    /// </summary>
    public static readonly Dictionary<string, string> AvailableKeyActions = new()
    {
        { "none",              "None" },
        { "ghelper",           "Toggle G-Helper" },
        { "performance",       "Cycle Performance Mode" },
        { "aura",              "Cycle Aura Mode" },
        { "brightness_up",     "Keyboard Brightness Up" },
        { "brightness_down",   "Keyboard Brightness Down" },
        { "micmute",           "Toggle Microphone Mute" },
        { "mute",              "Toggle Speaker Mute" },
        { "screen_refresh",    "Cycle Screen Refresh Rate" },
        { "overdrive",         "Toggle Panel Overdrive" },
        { "miniled",           "Toggle MiniLED" },
        { "camera",            "Toggle Camera" },
        { "touchpad",          "Toggle Touchpad" },
        // ROG Ally controller-mode toggle. Bind this to the M1/M2 buttons on
        // the Ally chassis. Evdev codes for those buttons vary by kernel /
        // BIOS revision and aren't standard XInput - Ally users will need to
        // discover them with `evtest` and map via the existing key-binding UI.
        { "ally_toggle_mode",  "Ally: Toggle Controller Mode" },
        // Audio / DSP chain hotkey targets. Display names are prefixed
        // "Audio: ..." so they cluster together at the bottom of the
        // dropdown (Dictionary preserves insertion order in .NET 5+).
        { "audio_toggle",   "Audio: Toggle Audio Helper" },
        { "audio_rnnoise",  "Audio: Toggle Denoise (RNNoise)" },
        { "audio_vocoder",  "Audio: Toggle Vocoder" },
        { "audio_eq",       "Audio: Toggle Parametric EQ" },
        { "audio_delay",    "Audio: Toggle Delay" },
        { "audio_reverb",   "Audio: Toggle Reverb" },
        { "audio_monitor",  "Audio: Toggle Monitor Playback" },
    };

    /// <summary>Default actions for each configurable key (matches Windows G-Helper).</summary>
    private static readonly Dictionary<string, string> DefaultKeyActions = new()
    {
        { "m4",     "ghelper" },          // ROG/M5 key (laptop) / ROG button (Ally) → toggle window
        { "fnf4",   "aura" },             // Fn+F4 → cycle aura mode (laptop only)
        { "fnf5",   "performance" },      // Fn+F5 / M4 → cycle performance mode (laptop only)
        // Ally hardware buttons (ExtraWindow remaps fnf4↔paddle, fnf5↔cc on Ally).
        { "paddle", "ghelper" },          // Ally X back paddles → toggle window
        { "cc",     "ally_toggle_mode" }, // Ally Cmd Center → cycle controller mode
        // Lenovo hardware buttons (ExtraWindow remaps m4↔novo, fnf4↔refresh_rate).
        { "novo",         "ghelper" },        // Novo button (KEY_PROG1/2) → toggle window
        { "refresh_rate", "screen_refresh" }, // KEY_REFRESH_RATE_TOGGLE → cycle refresh
    };

    /// <summary>Human-readable names for configurable keys (for UI labels).</summary>
    public static readonly Dictionary<string, string> ConfigurableKeyNames = new()
    {
        { "m4",     "ROG / M5 Key" },
        { "fnf4",   "Fn+F4 (Aura)" },
        { "fnf5",   "Fn+F5 / M4 (Performance)" },
        { "paddle", "Ally Back Paddles" },
        { "cc",     "Ally Cmd Center" },
        { "novo",         "Novo Button" },
        { "refresh_rate", "Refresh Rate Key" },
    };

    public static string GetKeyActionDisplayName(string actionId)
    {
        return actionId switch
        {
            "none" => Labels.Get("action_none"),
            "ghelper" => Labels.Get("action_ghelper"),
            "performance" => Labels.Get("action_performance"),
            "aura" => Labels.Get("action_aura"),
            "brightness_up" => Labels.Get("action_brightness_up"),
            "brightness_down" => Labels.Get("action_brightness_down"),
            "micmute" => Labels.Get("action_micmute"),
            "mute" => Labels.Get("action_mute"),
            "screen_refresh" => Labels.Get("action_screen_refresh"),
            "overdrive" => Labels.Get("action_overdrive"),
            "miniled" => Labels.Get("action_miniled"),
            "camera" => Labels.Get("action_camera"),
            "touchpad" => Labels.Get("action_touchpad"),
            "ally_toggle_mode" => Labels.Get("action_ally_toggle_mode"),
            "audio_toggle" => Labels.Get("action_audio_toggle"),
            "audio_rnnoise" => Labels.Get("action_audio_rnnoise"),
            "audio_vocoder" => Labels.Get("action_audio_vocoder"),
            "audio_eq" => Labels.Get("action_audio_eq"),
            "audio_delay" => Labels.Get("action_audio_delay"),
            "audio_reverb" => Labels.Get("action_audio_reverb"),
            "audio_monitor" => Labels.Get("action_audio_monitor"),
            _ => actionId
        };
    }

    public static string GetKeyDisplayName(string bindingName)
    {
        return bindingName switch
        {
            "m4" => Labels.Get("key_m4"),
            "fnf4" => Labels.Get("key_fnf4"),
            "fnf5" => Labels.Get("key_fnf5"),
            "paddle" => Labels.Get("ally_extra_btn_paddle"),
            "cc" => Labels.Get("ally_extra_btn_cc"),
            _ => bindingName
        };
    }

    /// <summary>Get the current action for a configurable key binding.</summary>
    public static string GetKeyAction(string bindingName)
    {
        string? action = AppConfig.GetString(bindingName);
        if (string.IsNullOrEmpty(action) || !AvailableKeyActions.ContainsKey(action))
        {
            DefaultKeyActions.TryGetValue(bindingName, out action);
            action ??= "none";
        }
        return action;
    }

    /// <summary>Handle non-configurable hotkey events (brightness, etc.).</summary>
    public static void DispatchHotkey(int eventCode)
    {
        Logger.WriteLine($"Hotkey event: {eventCode}");

        switch (eventCode)
        {
            case EventKbBrightnessUp:
                CycleKeyboardBrightness(up: true);
                break;

            case EventKbBrightnessDown:
                CycleKeyboardBrightness(up: false);
                break;

            case EventTouchpadToggle:
                // The desktop toggles the touchpad; the NumberPad follows its new state.
                Task.Run(() =>
                {
                    Thread.Sleep(200);
                    if (LinuxSystemIntegration.IsTouchpadEnabled() is bool on)
                        NumberPad.SetTouchpadEnabled(on);
                });
                break;
        }
    }

    /// <summary>
    /// Handle configurable key binding events.
    /// Reads the assigned action from config, falls back to default.
    /// </summary>
    public static void DispatchKeyBinding(string bindingName)
    {
        string action = GetKeyAction(bindingName);
        Logger.WriteLine($"Key binding: {bindingName} → action={action}");
        ExecuteKeyAction(action);
    }

    /// <summary>
    /// Re-entry point for the FnLockRemapper bridge. When fn-lock has
    /// exclusively grabbed the device that would normally deliver brightness
    /// hotkeys to LinuxAsusWmi, the remapper recognises the scancode and
    /// calls this so the same action fires.
    /// </summary>
    public static void RaiseHotkeyFromFnLock(int eventCode) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => DispatchHotkey(eventCode));

    /// <summary>
    /// Re-entry point for the FnLockRemapper bridge. Same purpose as
    /// <see cref="RaiseHotkeyFromFnLock"/> but for the configurable
    /// m4/fnf4/fnf5 bindings.
    /// </summary>
    public static void RaiseKeyBindingFromFnLock(string bindingName) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => DispatchKeyBinding(bindingName));

    /// <summary>
    /// Re-entry point for the FnLockRemapper when an F-key is mapped to an
    /// action target (e.g. F4 → "aura"). Bypasses the binding-name lookup
    /// (m4/fnf4/fnf5) and dispatches the action directly via
    /// <see cref="ExecuteKeyAction"/>.
    /// </summary>
    public static void RaiseActionFromFnLock(string action) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ExecuteKeyAction(action));

    /// <summary>Execute a key action by its action ID.</summary>
    public static void ExecuteKeyAction(string action)
    {
        switch (action)
        {
            case "none":
                break;

            case "ghelper":
                Avalonia.Threading.Dispatcher.UIThread.Post(() => App.ToggleMainWindow());
                break;

            case "performance":
                DebounceCyclePerformance();
                break;

            case "aura":
                string modeName;
                if (USB.LenovoRgb.IsAvailable())
                {
                    // Cycle the Lenovo RGB modes in declared order.
                    var modes = new List<int>(USB.LenovoRgb.GetModes().Keys);
                    int cur = modes.IndexOf((int)USB.LenovoRgb.Mode);
                    int next = modes[(cur + 1 + modes.Count) % modes.Count];
                    USB.LenovoRgb.Mode = (USB.LenovoRgbMode)next;
                    AppConfig.Set("lenovo_rgb_mode", next);
                    USB.LenovoRgb.Apply();
                    modeName = USB.LenovoRgb.GetModes()[next];
                }
                else
                {
                    modeName = USB.Aura.CycleAuraMode();
                }
                App.System?.ShowNotification(Labels.Get("aura"), modeName, "preferences-desktop-color");
                // Refresh main window keyboard section if visible
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    App.MainWindowInstance?.RefreshKeyboard());
                break;

            case "brightness_up":
                CycleKeyboardBrightness(up: true);
                break;

            case "brightness_down":
                CycleKeyboardBrightness(up: false);
                break;

            case "micmute":
                App.Audio?.ToggleMicMute();
                bool micMuted = App.Audio?.IsMicMuted() ?? false;
                App.System?.ShowNotification(Labels.Get("microphone"),
                    micMuted ? Labels.Get("muted") : Labels.Get("unmuted"),
                    micMuted ? "microphone-sensitivity-muted" : "microphone-sensitivity-high");
                break;

            case "mute":
                App.Audio?.ToggleSpeakerMute();
                bool spkMuted = App.Audio?.IsSpeakerMuted() ?? false;
                App.System?.ShowNotification(Labels.Get("speaker"),
                    spkMuted ? Labels.Get("muted") : Labels.Get("unmuted"),
                    spkMuted ? "audio-volume-muted" : "audio-volume-high");
                break;

            case "screen_refresh":
                CycleScreenRefreshRate();
                break;

            case "overdrive":
                bool currentOd = App.Wmi?.GetPanelOverdrive() ?? false;
                App.Wmi?.SetPanelOverdrive(!currentOd);
                AppConfig.Set("panel_od", !currentOd ? 1 : 0);
                App.System?.ShowNotification(Labels.Get("panel_overdrive"),
                    !currentOd ? Labels.Get("enabled") : Labels.Get("disabled"),
                    "preferences-desktop-display");
                break;

            case "miniled":
                int nextMiniLed = Display.MiniLed.CycleMode();
                if (nextMiniLed >= 0)
                    App.System?.ShowNotification(Labels.Get("mini_led"),
                        nextMiniLed != 0 ? Labels.Get("enabled") : Labels.Get("disabled"),
                        "preferences-desktop-display");
                break;

            case "camera":
                bool camOn = LinuxSystemIntegration.IsCameraEnabled();
                LinuxSystemIntegration.SetCameraEnabled(!camOn);
                App.System?.ShowNotification(Labels.Get("camera"),
                    !camOn ? Labels.Get("enabled") : Labels.Get("disabled"),
                    !camOn ? "camera-on" : "camera-off");
                break;

            case "ally_toggle_mode":
                App.Ally?.ToggleModeHotkey();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    App.MainWindowInstance?.RefreshAllyPanel());
                break;

            case "touchpad":
                bool? tpOn = LinuxSystemIntegration.IsTouchpadEnabled();
                if (tpOn.HasValue)
                {
                    LinuxSystemIntegration.SetTouchpadEnabled(!tpOn.Value);
                    App.System?.ShowNotification(Labels.Get("touchpad"),
                        !tpOn.Value ? Labels.Get("enabled") : Labels.Get("disabled"),
                        !tpOn.Value ? "input-touchpad-on" : "input-touchpad-off");
                }
                break;

            case "audio_toggle":
                {
                    bool on = AudioHelper.Instance.ToggleMaster();
                    App.System?.ShowNotification(Labels.Get("microphone"),
                        on ? Labels.Get("enabled") : Labels.Get("disabled"),
                        on ? "microphone-sensitivity-high" : "microphone-sensitivity-muted");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        App.MainWindowInstance?.RefreshAudioToggle());
                    break;
                }

            case "audio_rnnoise":
                NotifyAudioToggle(AudioHelper.Instance.ToggleRnnoise(), "Denoise");
                break;
            case "audio_vocoder":
                NotifyAudioToggle(AudioHelper.Instance.ToggleVocoder(), "Vocoder");
                break;
            case "audio_eq":
                NotifyAudioToggle(AudioHelper.Instance.ToggleEq(), "EQ");
                break;
            case "audio_delay":
                NotifyAudioToggle(AudioHelper.Instance.ToggleDelay(), "Delay");
                break;
            case "audio_reverb":
                NotifyAudioToggle(AudioHelper.Instance.ToggleReverb(), "Reverb");
                break;
            case "audio_monitor":
                NotifyAudioToggle(AudioHelper.Instance.ToggleMonitor(), "Monitor");
                break;
        }
    }

    private static void NotifyAudioToggle(bool on, string effectName)
    {
        App.System?.ShowNotification($"Audio: {effectName}",
            on ? Labels.Get("enabled") : Labels.Get("disabled"),
            on ? "audio-x-generic" : "audio-volume-muted");
    }

    /// <summary>Coalesce rapid FnF5 presses over 300ms, apply once.</summary>
    private static void DebounceCyclePerformance()
    {
        _perfDebounce?.Cancel();
        _perfDebounce = new CancellationTokenSource();
        Interlocked.Increment(ref _perfCycleCount);
        var token = _perfDebounce.Token;
        _ = Task.Delay(300, token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;
            int n = Interlocked.Exchange(ref _perfCycleCount, 0);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var mode = App.Mode;
                if (mode == null)
                    return;
                // Advance n steps through the mode list
                int target = Mode.Modes.GetCurrent();
                var list = Mode.Modes.GetList();
                if (list.Count == 0)
                    return;
                int idx = list.IndexOf(target);
                if (idx < 0)
                    idx = 0;
                idx = (idx + n) % list.Count;
                mode.SetPerformanceMode(list[idx], notify: true);
                App.UpdateTrayIcon();
                App.MainWindowInstance?.RefreshPerformanceMode();
            });
        }, TaskScheduler.Default);
    }

    private static void CycleScreenRefreshRate()
    {
        var display = App.Display;
        if (display == null)
            return;

        // Hotkey cycle disables auto mode (manual override)
        AppConfig.Set("screen_auto", 0);

        var rates = display.GetAvailableRefreshRates();
        if (rates.Count < 2)
            return;

        int current = display.GetRefreshRate();
        rates.Sort();

        // Find next rate (cycle: 60 → 120 → 165 → 60...)
        int nextRate = rates[0];
        for (int i = 0; i < rates.Count; i++)
        {
            if (rates[i] > current)
            {
                nextRate = rates[i];
                break;
            }
        }

        display.SetRefreshRate(nextRate);
        App.System?.ShowNotification(Labels.Get("display"), Labels.Format("refresh_rate_format", nextRate), "video-display");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            App.MainWindowInstance?.RefreshScreenPublic());
    }

    public static void CycleKeyboardBrightness(bool up)
    {
        int next;
        if (App.Wmi?.HasKbdBrightnessHwChanged == true)
        {
            // Kernel already changed brightness in sysfs - just read the new value
            next = App.Wmi.GetKeyboardBrightness();
            if (next < 0)
                next = 0;
        }
        else
        {
            // Kernel doesn't handle it, we must increment and write
            int current = App.Wmi?.GetKeyboardBrightness() ?? 0;
            next = up ? Math.Min(current + 1, 3) : Math.Max(current - 1, 0);
            App.Wmi?.SetKeyboardBrightness(next);
        }
        // Persist under AC- or battery-specific key so future AC/DC transitions restore the right level.
        AppConfig.Set(USB.Aura.GetBrightnessConfigKey(), next);
        string level = next switch
        {
            0 => Labels.Get("kbd_off"),
            1 => Labels.Get("kbd_low"),
            2 => Labels.Get("kbd_medium"),
            3 => Labels.Get("kbd_high"),
            _ => Labels.Format("kbd_level", next)
        };
        App.System?.ShowNotification(Labels.Get("keyboard"), level, "keyboard-brightness");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            App.MainWindowInstance?.RefreshKeyboard();
            App.MainWindowInstance?.RefreshExtraKeyboardBrightness();
        });
    }
}

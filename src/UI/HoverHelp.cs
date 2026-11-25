using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.I18n.HelpText;

namespace GHelper.Linux.UI;

/// <summary>
/// Hover help: every named control gets a tooltip that explains it, in the app
/// language (I18n/HelpText). Text keys are "WindowType.x:Name" for XAML controls and
/// the bare Name for controls built in code. A tip the code sets, also later,
/// stays first, and the help text goes below it. Off by default (config "hover_help").
/// </summary>
internal static class HoverHelp
{
    private const string ConfigKey = "hover_help";
    private const string Gap = "\n\n";
    private static bool _on;
    private static string _lang = "en";

    internal static string ToggleLabel => Help.Toggle(Labels.CurrentLanguage);

    /// <summary>Saved setting. Setting it updates the open windows at once.</summary>
    internal static bool Enabled
    {
        get => _on;
        set
        {
            _on = value;
            AppConfig.Set(ConfigKey, value ? 1 : 0);
            ApplyAll();
        }
    }

    /// <summary>Call once at startup, before the first window opens.</summary>
    internal static void Install()
    {
        _on = AppConfig.Is(ConfigKey);
        _lang = Labels.CurrentLanguage;
        Labels.LanguageChanged += OnLanguageChanged;
        Control.LoadedEvent.AddClassHandler<Control>((control, _) => Apply(control));
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
    }

    // Take the old text off in the old language, then add it in the new one.
    private static void OnLanguageChanged()
    {
        bool on = _on;
        _on = false;
        ApplyAll();
        _lang = Labels.CurrentLanguage;
        _on = on;
        ApplyAll();
    }

    private static void ApplyAll()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows)
                foreach (var control in window.GetLogicalDescendants().OfType<Control>())
                    Apply(control);
    }

    private static void Apply(Control control)
    {
        string? help = Find(control);
        if (help == null)
            return;
        object? tip = ToolTip.GetTip(control);
        if (tip is not (null or string))
            return;
        string? own = Without((string?)tip, help);
        string? next = _on ? (own == null ? help : own + Gap + help) : own;
        if (next == (string?)tip)
            return;
        if (next == null)
            control.ClearValue(ToolTip.TipProperty);
        else
            ToolTip.SetTip(control, next);
        if (_on)
            ToolTip.SetShowOnDisabled(control, true);
        else
            control.ClearValue(ToolTip.ShowOnDisabledProperty);
    }

    private static string? Find(Control control)
    {
        if (control.Name is not { } name)
            return null;
        var window = control.FindLogicalAncestorOfType<Window>();
        return (window == null ? null : Help.Get(_lang, window.GetType().Name + "." + name))
            ?? Help.Get(_lang, name);
    }

    // The tip the code set, without the help text added here.
    private static string? Without(string? tip, string help)
    {
        if (string.IsNullOrEmpty(tip) || tip == help)
            return null;
        return tip.EndsWith(Gap + help, StringComparison.Ordinal) ? tip[..^(Gap.Length + help.Length)] : tip;
    }
}

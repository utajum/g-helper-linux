using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n.HelpText;

namespace GHelper.Linux.UI;

/// <summary>
/// Hover help: every named control gets a tooltip that explains it, from
/// I18n/HelpText/English.cs. Text keys are "WindowType.x:Name" for XAML controls and
/// the bare Name for controls built in code. A tip the code sets, also later,
/// stays first, and the help text goes below it. On by default (config "hover_help").
/// </summary>
internal static class HoverHelp
{
    private const string ConfigKey = "hover_help";
    private const string Gap = "\n\n";
    private static bool _on = true;

    internal static string ToggleLabel => English.ToggleLabel;

    /// <summary>Saved setting. Setting it updates the open windows at once.</summary>
    internal static bool Enabled
    {
        get => _on;
        set
        {
            _on = value;
            AppConfig.Set(ConfigKey, value ? 1 : 0);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                foreach (var window in desktop.Windows)
                    foreach (var control in window.GetLogicalDescendants().OfType<Control>())
                        Apply(control);
        }
    }

    /// <summary>Call once at startup, before the first window opens.</summary>
    internal static void Install()
    {
        _on = AppConfig.IsNotFalse(ConfigKey);
        Control.LoadedEvent.AddClassHandler<Control>((control, _) => Apply(control));
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
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
        var tips = English.Tips;
        var window = control.FindLogicalAncestorOfType<Window>();
        if (window != null && tips.TryGetValue(window.GetType().Name + "." + name, out var text))
            return text;
        return tips.GetValueOrDefault(name);
    }

    // The tip the code set, without the help text added here.
    private static string? Without(string? tip, string help)
    {
        if (string.IsNullOrEmpty(tip) || tip == help)
            return null;
        return tip.EndsWith(Gap + help, StringComparison.Ordinal) ? tip[..^(Gap.Length + help.Length)] : tip;
    }
}

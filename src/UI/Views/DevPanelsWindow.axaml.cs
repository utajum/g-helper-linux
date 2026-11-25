using Avalonia.Controls;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// Dev-only toggles that force-show hardware-gated panels so the UI can be
/// checked on machines without the matching hardware. Each toggle names the
/// window the panel lives in. Reachable from the main window button shown
/// when GHELPER_DEV is set.
/// </summary>
public partial class DevPanelsWindow : Window
{
    private sealed record Toggle(string Name, string Location, string ConfigKey, Action? Refresh);

    private static readonly Toggle[] Toggles =
    {
        new("AnimeMatrix panel", "Main window", "show_anime_matrix_dev",
            () => App.MainWindowInstance?.RefreshAnimeMatrix()),
        new("GPU panel + Ultimate/Optimized buttons", "Main window", "show_gpu_dev",
            () => App.MainWindowInstance?.RefreshGpuModePublic()),
        new("Ally panel", "Main window", "show_ally_dev",
            () => App.MainWindowInstance?.RefreshAllyPanel()),
        new("MiniLED button", "Main window", "show_miniled_dev",
            () => App.MainWindowInstance?.RefreshScreenPublic()),
        new("Aura keyboard panel", "Main window", "show_aura_dev",
            () => App.MainWindowInstance?.RefreshKeyboard()),
        new("XG Mobile button + dock panel", "Main window / Extra Settings (reopen)", "show_xgm_dev",
            () => App.MainWindowInstance?.RefreshAllyPanel()),
        new("Suspend mode combo", "Extra Settings (reopen)", "show_deep_sleep_dev", null),
        new("EPP combos", "Extra Settings (reopen)", "show_epp_dev", null),
        new("NumberPad button", "Extra Settings (reopen)", "show_numberpad_dev", null),
    };

    public DevPanelsWindow()
    {
        InitializeComponent();

        foreach (var toggle in Toggles)
        {
            var check = new CheckBox
            {
                Name = "checkDevPanel",
                Content = $"{toggle.Name}  ({toggle.Location})",
                FontSize = 12,
                IsChecked = Helpers.AppConfig.Is(toggle.ConfigKey),
            };
            check.IsCheckedChanged += (_, _) =>
            {
                Helpers.AppConfig.Set(toggle.ConfigKey, (check.IsChecked ?? false) ? 1 : 0);
                try
                {
                    toggle.Refresh?.Invoke();
                }
                catch (Exception ex)
                {
                    Helpers.Logger.WriteLine($"DevPanels: refresh for '{toggle.Name}' failed: {ex.Message}");
                }
            };
            panelToggles.Children.Add(check);
        }
    }
}

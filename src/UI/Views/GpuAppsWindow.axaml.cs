using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Gpu.Block;
using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

/// <summary>Which apps use which GPU, and which may keep the dGPU while it is hidden.</summary>
public partial class GpuAppsWindow : Window
{
    private List<GpuAppUse> _rows = new();
    private List<CatalogApp>? _catalog;

    /// <summary>Most catalog matches drawn for one search.</summary>
    private const int MaxCatalogRows = 50;

    public GpuAppsWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        inputName.TextChanged += (_, _) => RenderRows();

        Loaded += (_, _) => Refresh();
        Closing += (_, _) => Labels.LanguageChanged -= ApplyLabels;
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("gpu_apps_title");
        labelAdd.Text = Labels.Get("gpu_apps_add");
        labelRefresh.Text = Labels.Get("refresh");
        inputName.PlaceholderText = Labels.Get("gpu_apps_search_hint");
        labelFooter.Text = Labels.Get("gpu_apps_desc") + "\n" + Labels.Get("gpu_apps_launch_hint");
        ApplyStateLabels();
    }

    /// <summary>Header text: blocked or not decides what the list means.</summary>
    private void ApplyStateLabels()
    {
        bool blocked = GpuBlockControl.IsActiveFast();
        labelState.Text = Labels.Get(blocked ? "gpu_apps_state_on" : "gpu_apps_state_off");
        labelStateDesc.Text = Labels.Get(blocked ? "gpu_apps_state_on_desc" : "gpu_apps_state_off_desc");
    }

    private void ButtonRefresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Refresh();

    /// <summary>Rebuild the model off the UI thread.</summary>
    private void Refresh()
    {
        buttonRefresh.IsEnabled = false;
        Task.Run(() =>
        {
            List<GpuAppUse> rows;
            try
            {
                _catalog ??= GpuAppCatalog.Load();
                var titles = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var app in _catalog)
                    if (app.Entry.Contains('='))
                        titles.TryAdd(app.Entry, app.Title);
                var refused = new List<string>();
                foreach (var app in GpuBlockControl.Refused())
                    if (!NvidiaProcessScanner.IsSystemProcess(app.Comm))
                        refused.Add(app.Comm);
                rows = GpuProcessList.MergeWithPolicy(GpuProcessList.Scan(), GpuBlockPolicy.List(), refused, titles);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuAppsWindow: refresh failed: {ex.Message}");
                rows = new List<GpuAppUse>();
            }

            Dispatcher.UIThread.Post(() =>
            {
                buttonRefresh.IsEnabled = true;
                _rows = rows;
                ApplyStateLabels();
                RenderRows();
            });
        });
    }

    /// <summary>Draw the current model, filtered by the search box.</summary>
    private void RenderRows()
    {
        string filter = (inputName.Text ?? "").Trim();
        panelRows.Children.Clear();

        var shown = new List<GpuAppUse>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            listed.Add(row.Comm);
            if (Matches(row.Comm, row.Title, filter))
                shown.Add(row);
        }

        // Installed apps and games only on a search: the full catalog would bury the live rows.
        int extra = 0;
        foreach (var app in filter.Length > 0 ? _catalog ?? new() : new())
        {
            if (extra < MaxCatalogRows && !listed.Contains(app.Entry) && Matches(app.Entry, app.Title, filter))
            {
                shown.Add(new GpuAppUse(app.Entry, 0, false, false, Title: app.Title));
                extra++;
            }
        }

        if (shown.Count == 0)
        {
            panelRows.Children.Add(new TextBlock
            {
                Text = Labels.Get(filter.Length > 0 ? "gpu_apps_no_match" : "gpu_apps_none_running"),
                Foreground = new SolidColorBrush(Color.Parse("#8A8A8A")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(4, 10, 4, 10),
            });
            return;
        }

        foreach (var row in shown)
            panelRows.Children.Add(BuildRow(row));
    }

    private static bool Matches(string entry, string? title, string filter)
        => filter.Length == 0
           || entry.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || (title?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private Control BuildRow(GpuAppUse row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
        };

        // Name over a one line state text.
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = (row.Title == null ? row.Comm : row.Comm.Contains('=') ? row.Title : $"{row.Title} ({row.Comm})")
                + (row.Pids > 1 ? $"  x{row.Pids}" : ""),
            FontSize = 12,
        });
        text.Children.Add(new TextBlock
        {
            Text = RowSubtitle(row),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8A8A8A")),
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var tag = BuildTag(row);
        if (tag != null)
        {
            Grid.SetColumn(tag, 1);
            grid.Children.Add(tag);
        }

        var action = new Button
        {
            Content = new TextBlock
            {
                Text = Labels.Get(row.Allowed ? "gpu_apps_remove" : "gpu_apps_allow"),
                FontSize = 11,
            },
            Padding = new Avalonia.Thickness(12, 3, 12, 3),
            FontSize = 11,
            Margin = new Avalonia.Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = GpuBlockPolicy.Validate(row.Comm) == null,
        };
        action.Classes.Add("ghelper");
        string name = row.Comm;
        bool allowed = row.Allowed;
        action.Click += (_, _) => Apply(() => allowed
            ? GpuBlockPolicy.Deny(name)
            : GpuBlockPolicy.Allow(name));
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        var border = new Border { Child = grid };
        border.Classes.Add("row");
        return border;
    }

    /// <summary>Plain words for the row state.</summary>
    private static string RowSubtitle(GpuAppUse row)
    {
        if (row.Refused && !row.Allowed)
            return Labels.Get("gpu_apps_sub_refused");
        if (row.Pids == 0 && row.Comm.StartsWith("SteamAppId=", StringComparison.Ordinal))
            return Labels.Get("gpu_apps_sub_steam");
        if (row.Pids == 0 && row.Title != null && !row.Allowed)
            return Labels.Get("gpu_apps_sub_installed");
        if (row.Pids == 0)
            return Labels.Get("gpu_apps_sub_not_running");
        if (row.OnDgpu && row.Allowed)
            return Labels.Get("gpu_apps_sub_dgpu_allowed");
        if (row.OnDgpu)
            return Labels.Get("gpu_apps_sub_dgpu");
        return Labels.Get("gpu_apps_sub_igpu");
    }

    /// <summary>GPU tag: green dGPU, grey iGPU, none when not running.</summary>
    private static Border? BuildTag(GpuAppUse row)
    {
        if (row.Pids == 0)
            return null;

        string text = (row.OnDgpu, row.OnIgpu) switch
        {
            (true, true) => "dGPU + iGPU",
            (true, false) => "dGPU",
            _ => "iGPU",
        };
        string fg = row.OnDgpu ? "#9CCC65" : "#9A9A9A";
        string bg = row.OnDgpu ? "#2F3A22" : "#333333";

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse(bg)),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(fg)),
            },
        };
        border.Classes.Add("tag");
        return border;
    }

    private void ButtonAdd_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string name = (inputName.Text ?? "").Trim();
        string? err = GpuBlockPolicy.Validate(name);
        if (err != null)
        {
            // Spelled out so the keys are visible to the i18n checker.
            string msg = err == "gpu_apps_too_long"
                ? Labels.Get("gpu_apps_too_long")
                : Labels.Get("gpu_apps_invalid");
            App.System?.ShowNotification(Labels.Get("gpu_apps_title"), msg, "dialog-warning");
            return;
        }
        Apply(() => GpuBlockPolicy.Allow(name), clearInput: true);
    }

    /// <summary>Policy change off the UI thread: the helper can prompt.</summary>
    private void Apply(Func<bool> change, bool clearInput = false)
    {
        buttonAdd.IsEnabled = false;
        Task.Run(() =>
        {
            bool ok = change();
            Dispatcher.UIThread.Post(() =>
            {
                buttonAdd.IsEnabled = true;
                if (!ok)
                {
                    App.System?.ShowNotification(Labels.Get("gpu_apps_title"),
                        Labels.Get("gpu_apps_failed"), "dialog-warning");
                }
                if (clearInput)
                    inputName.Text = "";
                Refresh();
                App.MainWindowInstance?.RefreshGpuModePublic();
            });
        });
    }
}

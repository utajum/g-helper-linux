using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Gpu.NVidia;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

public partial class NvidiaProcessesWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;

    public NvidiaProcessesWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshList();

        Loaded += (_, _) =>
        {
            RefreshList();
            _refreshTimer.Start();
        };

        Closing += (_, _) => _refreshTimer.Stop();
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("gpu_dgpu_processes_title");
        labelRefresh.Text = Labels.Get("gpu_refresh");
    }

    private void ButtonRefresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => RefreshList();

    private void RefreshList()
    {
        var holders = NvidiaProcessScanner.ScanHolders();
        labelHeader.Text = Labels.Format("gpu_dgpu_users_count", holders.Count);
        panelProcessList.Children.Clear();

        if (holders.Count == 0)
        {
            panelProcessList.Children.Add(BuildEmptyState());
            return;
        }

        var active = new List<NvidiaHolder>();
        var libOnly = new List<NvidiaHolder>();
        foreach (var h in holders)
        {
            if (h.BlocksUnload)
                active.Add(h);
            else if (h.LibsMapped > 0)
                libOnly.Add(h);
        }
        active.Sort((a, b) =>
        {
            int aTotal = a.FdCount + a.DriFdCount + a.I2cFdCount;
            int bTotal = b.FdCount + b.DriFdCount + b.I2cFdCount;
            return bTotal != aTotal ? bTotal - aTotal : a.Pid - b.Pid;
        });
        libOnly.Sort((a, b) => a.Pid - b.Pid);

        if (active.Count > 0)
        {
            panelProcessList.Children.Add(BuildSectionHeader(Labels.Get("gpu_holder_fd_active")));
            foreach (var h in active)
                panelProcessList.Children.Add(BuildRow(h));
        }
        if (libOnly.Count > 0)
        {
            panelProcessList.Children.Add(BuildSectionHeader(Labels.Get("gpu_holder_libs_loaded")));
            panelProcessList.Children.Add(BuildLibHint());
            foreach (var h in libOnly)
                panelProcessList.Children.Add(BuildRow(h));
        }
    }

    private static Control BuildSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
            Margin = new Thickness(2, 10, 0, 4),
        };
    }

    private static Control BuildLibHint()
    {
        return new TextBlock
        {
            Text = Labels.Get("gpu_holder_libs_loaded_hint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            Margin = new Thickness(2, 0, 2, 6),
        };
    }

    private static Control BuildEmptyState()
    {
        return new Border
        {
            Classes = { "panel" },
            Padding = new Thickness(20),
            Child = new TextBlock
            {
                Text = Labels.Get("gpu_no_processes_using_dgpu"),
                Foreground = new SolidColorBrush(Color.Parse("#888888")),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 12,
            }
        };
    }

    private Control BuildRow(NvidiaHolder holder)
    {
        bool isLibOnly = !holder.BlocksUnload && holder.LibsMapped > 0;

        var border = new Border
        {
            Classes = { "panel" },
            Padding = new Thickness(12, 8),
            Opacity = isLibOnly ? 0.72 : 1.0,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
        };

        var info = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };

        var primaryText = new TextBlock
        {
            Text = $"{holder.Comm}  (PID {holder.Pid})",
            FontWeight = isLibOnly ? FontWeight.Normal : FontWeight.Bold,
            FontSize = 13,
        };

        string detailText;
        if (isLibOnly)
        {
            detailText = Labels.Format("gpu_process_row_libs", holder.User);
        }
        else
        {
            int total = holder.FdCount + holder.DriFdCount + holder.I2cFdCount;
            string suffix = "";
            if (holder.DriFdCount > 0)
                suffix += $" +{holder.DriFdCount} DRI";
            if (holder.I2cFdCount > 0)
                suffix += $" +{holder.I2cFdCount} I2C";
            detailText = Labels.Format("gpu_process_row_detail", holder.User, total) + suffix;
        }

        var secondaryText = new TextBlock
        {
            Text = detailText,
            Classes = { "label-dim" },
            FontSize = 11,
        };

        info.Children.Add(primaryText);
        info.Children.Add(secondaryText);

        if (!string.IsNullOrEmpty(holder.ServiceUnit))
        {
            var badgeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            badgeRow.Children.Add(BuildServiceBadge(holder.ServiceUnit));
            info.Children.Add(badgeRow);
        }
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var btnKill = new Button
        {
            Name = "buttonProcessKill",
            Content = Labels.Get("gpu_kill"),
            Classes = { "ghelper" },
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11,
            MinWidth = 70,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnKill.Click += (_, _) => OnKillClick(holder, force: false);
        Grid.SetColumn(btnKill, 1);
        grid.Children.Add(btnKill);

        var btnForce = new Button
        {
            Name = "buttonProcessForceKill",
            Content = Labels.Get("gpu_force_kill"),
            Background = new SolidColorBrush(Color.Parse("#A82A2A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Padding = new Thickness(10, 6),
            FontSize = 11,
            MinWidth = 90,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnForce.Click += (_, _) => OnKillClick(holder, force: true);
        Grid.SetColumn(btnForce, 2);
        grid.Children.Add(btnForce);

        border.Child = grid;
        return border;
    }

    private static Control BuildServiceBadge(string unit)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2E4A5A")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var txt = new TextBlock
        {
            Text = unit,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#9FD0E8")),
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(badge, unit);
        badge.Child = txt;
        return badge;
    }

    private async void OnKillClick(NvidiaHolder holder, bool force)
    {
        string question = force
            ? Labels.Format("gpu_force_kill_confirm", holder.Comm, holder.Pid)
            : Labels.Format("gpu_kill_confirm", holder.Comm, holder.Pid);

        bool confirmed = await ConfirmKillDialog(question);
        if (!confirmed)
            return;

        bool ok = NvidiaProcessScanner.KillProcess(holder.Pid, force, holder, out string err);
        if (!ok)
        {
            Logger.WriteLine($"NvidiaProcessesWindow: kill PID {holder.Pid} failed: {err}");
        }

        RefreshList();
    }

    private async System.Threading.Tasks.Task<bool> ConfirmKillDialog(string question)
    {
        var dialog = new Window
        {
            Title = Labels.Get("gpu_kill_confirm_title"),
            Width = 380,
            MaxHeight = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

        var body = new TextBlock
        {
            Text = question,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(20, 16, 20, 0),
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        };

        var btnYes = new Button
        {
            Name = "buttonKillConfirm",
            Content = Labels.Get("gpu_kill_confirm_yes"),
            Background = new SolidColorBrush(Color.Parse("#A82A2A")),
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Padding = new Thickness(14, 8),
            MinWidth = 110,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnYes.Click += (_, _) => { tcs.SetResult(true); dialog.Close(); };

        var btnNo = new Button
        {
            Name = "buttonKillCancel",
            Content = Labels.Get("cancel"),
            Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            Padding = new Thickness(14, 8),
            MinWidth = 110,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnNo.Click += (_, _) => { tcs.SetResult(false); dialog.Close(); };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 16),
        };
        btnRow.Children.Add(btnYes);
        btnRow.Children.Add(btnNo);

        var stack = new StackPanel();
        stack.Children.Add(body);
        stack.Children.Add(btnRow);

        dialog.Content = stack;
        dialog.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.SetResult(false);
        };
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }
}

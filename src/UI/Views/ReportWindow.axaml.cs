using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

// In-app bug report: the user writes title and description, the app adds system info and
// diagnostics, the endpoint opens a public GitHub issue. Sending is always a user action.
public partial class ReportWindow : Window
{
    private static readonly IBrush ColorText = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush ColorError = new SolidColorBrush(Color.Parse("#FF2020"));

    private readonly string? _latestVersion;
    private readonly string _reportId;
    private BugReport.Info? _info;
    private string _diagnostics = "";
    private string _error = "";
    private bool _retry;
    private bool _sending;
    private bool _sent;

    public ReportWindow() : this(null) { }

    /// <param name="latestVersion">Newer release version, or null when this build is the latest.</param>
    public ReportWindow(string? latestVersion)
    {
        InitializeComponent();
        _latestVersion = latestVersion;
        var draft = BugReport.LoadDraft();
        _reportId = draft.Id;
        textTitle.MaxLength = BugReport.TitleMax;
        textDescription.MaxLength = BugReport.DescriptionMax;
        textTitle.Text = draft.Title;
        textDescription.Text = draft.Description;

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();
        RenderLinks();

        Loaded += (_, _) => _ = CollectAsync();
        Closed += (_, _) =>
        {
            Labels.LanguageChanged -= ApplyLabels;
            BugReport.SaveDraft(_sent ? null : Current);
        };
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("report_bug");
        labelHeader.Text = Labels.Get("report_bug");
        labelReportTitle.Text = Labels.Get("report_title");
        labelDescription.Text = Labels.Get("report_description");
        textDescription.PlaceholderText = Labels.Get("report_description_hint");
        labelSystem.Text = Labels.Get("report_system");
        buttonPreview.Content = Labels.Get("report_preview");
        labelPublicNote.Text = Labels.Get("report_public_note");
        labelYourReports.Text = Labels.Get("report_your_reports");
        buttonSend.Content = Labels.Get(_retry ? "report_retry" : "report_send");
        labelOldVersion.IsVisible = _latestVersion != null;
        if (_latestVersion != null)
            labelOldVersion.Text = Labels.Format("report_old_version", AppConfig.AppVersion, _latestVersion);
        UpdateState();
    }

    private async Task CollectAsync()
    {
        labelStatus.Text = Labels.Get("collecting");
        try
        {
            (_info, _diagnostics) = await Task.Run(() => BugReport.Collect(Diagnostics.GenerateReport()));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Report: diagnostics failed: {ex.Message}");
            labelStatus.Text = Labels.Get("failed");
            return;
        }
        var i = _info;
        textSystem.Text = $"G-Helper {i.Version} ({i.InstallMode})\n{i.Model}\nBIOS {i.Bios}\n" +
                          $"Kernel {i.Kernel}\n{i.Os}\n{i.Desktop}";
        UpdatePreview();
        UpdateState();
    }

    private string TitleText => (textTitle.Text ?? "").Trim();
    private string DescriptionText => (textDescription.Text ?? "").Trim();
    private BugReport.Draft Current => new(_reportId, TitleText, DescriptionText);
    private bool Valid => BugReport.IsValid(Current);

    private void UpdateState()
    {
        bool ready = _info != null;
        buttonSend.IsEnabled = ready && Valid && !_sending && !_sent;
        if (!ready || _sending || _sent)
            return;
        bool showError = _error.Length > 0 && Valid;
        labelStatus.Text = showError ? _error : Valid ? "" : Labels.Get("report_need_text");
        labelStatus.Foreground = showError ? ColorError : ColorText;
    }

    private void UpdatePreview()
    {
        if (_info == null || !textPreview.IsVisible)
            return;
        textPreview.Text = $"[app] {TitleText}\n\n{textSystem.Text}\n\n{DescriptionText}\n\n{_diagnostics}";
    }

    private void Input_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePreview();
        UpdateState();
    }

    private void ButtonPreview_Click(object? sender, RoutedEventArgs e)
    {
        textPreview.IsVisible = !textPreview.IsVisible;
        UpdatePreview();
    }

    private async void ButtonSend_Click(object? sender, RoutedEventArgs e)
    {
        if (_info == null || !Valid || _sending)
            return;
        _sending = true;
        textTitle.IsEnabled = textDescription.IsEnabled = false;
        buttonSend.IsEnabled = false;
        labelStatus.Text = Labels.Get("report_sending");
        labelStatus.Foreground = ColorText;

        var r = await BugReport.SendAsync(Current, _info, _diagnostics);

        _sending = false;
        textTitle.IsEnabled = textDescription.IsEnabled = true;
        if (r.Ok)
        {
            _sent = true;
            Logger.WriteLine($"Report: sent #{r.Number}");
            BugReport.Remember(r, TitleText);
            labelStatus.Text = Labels.Format("report_sent", r.Number);
            RenderLinks();
            await ShowSentDialog(r);
            Close();
            return;
        }

        Logger.WriteLine($"Report: failed {r.Status}");
        _error = r.Message;
        _retry = r.Retry;
        buttonSend.Content = Labels.Get(_retry ? "report_retry" : "report_send");
        UpdateState();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Report: open link failed: {ex.Message}");
        }
    }

    private static Button LinkButton(string text, string url)
    {
        var b = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#4DA3FF")),
            Padding = new Avalonia.Thickness(0, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        b.Click += (_, _) => OpenUrl(url);
        return b;
    }

    private void RenderLinks()
    {
        var links = BugReport.Links;
        panelReports.Children.Clear();
        foreach (var l in links)
            panelReports.Children.Add(LinkButton($"#{l.Number}  {l.Title}  {l.Date}", l.Url));
        labelYourReports.IsVisible = links.Count > 0;
    }

    private async Task ShowSentDialog(BugReport.Result r)
    {
        var dialog = new Window
        {
            Title = Labels.Get("report_bug"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
            Icon = Icon,
        };
        var root = new StackPanel { Margin = new Avalonia.Thickness(20, 16, 20, 16), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = Labels.Format("report_sent", r.Number),
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F0F0F0")),
        });
        root.Children.Add(LinkButton(r.Url, r.Url));
        root.Children.Add(new TextBlock
        {
            Text = Labels.Get("report_sent_note"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ColorText,
        });

        var open = new Button { Content = Labels.Get("report_open"), MinWidth = 110 };
        open.Click += (_, _) => OpenUrl(r.Url);
        var copy = new Button { Content = Labels.Get("report_copy_link"), MinWidth = 110 };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(r.Url);
                copy.Content = Labels.Get("copied");
            }
        };
        var close = new Button { Content = Labels.Get("sysfiles_diff_close"), MinWidth = 90 };
        close.Click += (_, _) => dialog.Close();
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { open, copy, close },
        });

        dialog.Content = root;
        await dialog.ShowDialog(this);
    }
}

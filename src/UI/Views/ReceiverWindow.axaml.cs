using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;
using GHelper.Linux.Peripherals;
using GHelper.Linux.Peripherals.Logitech;
using GHelper.Linux.Peripherals.Logitech.HidPP;

namespace GHelper.Linux.UI.Views;

/// <summary>Lists plugged-in Logitech receivers with their paired devices; pairs and unpairs devices.</summary>
public partial class ReceiverWindow : Window
{
    private const int PairSeconds = 30;
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9A9A9A"));

    private static ReceiverWindow? _open;

    private CancellationTokenSource? _pairing;
    private bool _busy;

    /// <summary>Shows the one receivers window, or brings it to the front.</summary>
    public static void ShowOrActivate()
    {
        if (_open is { IsVisible: true })
        {
            _open.Activate();
            return;
        }
        _open = new ReceiverWindow();
        if (Helpers.AppConfig.Is("topmost"))
            _open.Topmost = true;
        Helpers.WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(_open);
        _open.Show();
    }

    public static void CloseIfOpen() => _open?.Close();

    public ReceiverWindow()
    {
        InitializeComponent();

        Labels.LanguageChanged += ApplyLabels;
        ApplyLabels();

        Loaded += (_, _) => Refresh();
        Closing += (_, _) => _pairing?.Cancel();
        Closed += (_, _) => Labels.LanguageChanged -= ApplyLabels;
    }

    private void ApplyLabels()
    {
        Title = Labels.Get("receivers_title");
        labelHeader.Text = Labels.Get("receivers_title");
        labelRefresh.Text = Labels.Get("refresh");
        labelCancel.Text = Labels.Get("cancel");
    }

    private void ButtonRefresh_Click(object? sender, RoutedEventArgs e) => Refresh();

    private void ButtonCancel_Click(object? sender, RoutedEventArgs e) => _pairing?.Cancel();

    private async void Refresh()
    {
        if (_busy)
            return;
        var receivers = await Task.Run(ReadReceivers);

        panelReceivers.Children.Clear();
        if (receivers.Count == 0)
            panelReceivers.Children.Add(DimText(Labels.Get("receivers_none")));
        foreach (var (entry, slots) in receivers)
            panelReceivers.Children.Add(BuildReceiver(entry, slots));
    }

    /// <summary>Plugged-in receivers with their slots; slots is null when the receiver cannot be opened.</summary>
    private static List<(ReceiverEntry entry, List<LogitechReceiver.Slot>? slots)> ReadReceivers()
    {
        var list = new List<(ReceiverEntry, List<LogitechReceiver.Slot>?)>();
        foreach (var entry in ReceiverInfo.KnownReceivers)
        {
            using var receiver = new LogitechReceiver(entry);
            if (!receiver.IsConnected())
                continue;
            list.Add((entry, receiver.Open() ? receiver.ReadSlots() : null));
        }
        return list;
    }

    private Control BuildReceiver(ReceiverEntry entry, List<LogitechReceiver.Slot>? slots)
    {
        var stack = new StackPanel { Spacing = 6 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = $"{entry.Name} (0x{entry.ProductId:X4})",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var pair = SmallButton(name: "buttonReceiverPair", Labels.Get("receivers_pair"), () => PairAsync(entry));
        pair.IsEnabled = slots is not null;
        Grid.SetColumn(pair, 1);
        head.Children.Add(pair);
        stack.Children.Add(head);

        if (slots is null)
            stack.Children.Add(DimText(Labels.Get("receivers_no_access")));
        else if (slots.Count == 0)
            stack.Children.Add(DimText(Labels.Get("receivers_empty")));
        else
        {
            foreach (var slot in slots)
                stack.Children.Add(BuildSlot(entry, slot));
        }

        return new Border { Classes = { "panel" }, Child = stack };
    }

    private Control BuildSlot(ReceiverEntry entry, LogitechReceiver.Slot slot)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = $"{slot.Index}. {slot.Name}", TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"0x{slot.Wpid:X4}", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(text);
        // Nano receivers do not unpair; pairing a new device replaces the old one.
        if (entry.Kind == ReceiverKind.Nano)
            return row;
        var unpair = SmallButton(name: "buttonReceiverUnpair", Labels.Get("receivers_unpair"), () => UnpairAsync(entry, slot));
        Grid.SetColumn(unpair, 1);
        row.Children.Add(unpair);
        return row;
    }

    private async void PairAsync(ReceiverEntry entry)
    {
        SetBusy(true);
        using var cts = new CancellationTokenSource();
        _pairing = cts;
        var token = cts.Token;
        ShowStatus(Labels.Get(entry.Kind == ReceiverKind.Bolt ? "receivers_hint_bolt" : "receivers_hint_unifying"), cancel: true);

        var result = await Task.Run(() =>
        {
            using var receiver = new LogitechReceiver(entry);
            if (!receiver.Open())
                return new LogitechReceiver.PairResult(LogitechReceiver.PairError.Failed);
            return receiver.Pair(PairSeconds, step => Dispatcher.UIThread.Post(() => ShowStep(step)), token);
        });
        if (result.Error == LogitechReceiver.PairError.None)
            await Task.Run(PeripheralsProvider.RescanReceivers);

        _pairing = null;
        ShowStatus(ResultText(result), cancel: false);
        SetBusy(false);
        Refresh();
    }

    private async void UnpairAsync(ReceiverEntry entry, LogitechReceiver.Slot slot)
    {
        if (!await Confirm(Labels.Format("receivers_unpair_confirm", slot.Name)))
            return;
        SetBusy(true);
        bool ok = await Task.Run(() =>
        {
            using var receiver = new LogitechReceiver(entry);
            bool done = receiver.Open() && receiver.Unpair(slot.Index);
            PeripheralsProvider.RescanReceivers();
            return done;
        });
        ShowStatus(ok ? "" : Labels.Get("receivers_unpair_failed"), cancel: false);
        SetBusy(false);
        Refresh();
    }

    private void ShowStep(LogitechReceiver.PairStep step)
    {
        if (_pairing is null)
            return;
        string name = step.Name ?? "?";
        if (step.Passkey is null)
            ShowStatus(Labels.Format("receivers_pairing_with", name), cancel: true);
        else if (step.Typed)
            ShowStatus(Labels.Format("receivers_passkey_type", step.Passkey, name), cancel: true);
        else
        {
            var clicks = HidPPDecode.PasskeyClicks(step.Passkey)
                .Select(right => Labels.Get(right ? "receivers_right" : "receivers_left"));
            ShowStatus(Labels.Format("receivers_passkey_click", name, string.Join(", ", clicks)), cancel: true);
        }
    }

    private static string ResultText(LogitechReceiver.PairResult result) => result.Error switch
    {
        LogitechReceiver.PairError.None => Labels.Format("receivers_paired", result.Name ?? "?"),
        LogitechReceiver.PairError.Timeout => Labels.Get("receivers_error_timeout"),
        LogitechReceiver.PairError.NotSupported => Labels.Get("receivers_error_unsupported"),
        LogitechReceiver.PairError.TooMany => Labels.Get("receivers_error_full"),
        LogitechReceiver.PairError.Cancelled => "",
        _ => Labels.Get("receivers_error_failed"),
    };

    private void ShowStatus(string text, bool cancel)
    {
        labelStatus.Text = text;
        panelStatus.IsVisible = text.Length > 0;
        buttonCancel.IsVisible = cancel;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        panelReceivers.IsEnabled = !busy;
        buttonRefresh.IsEnabled = !busy;
    }

    private static TextBlock DimText(string text) =>
        new() { Text = text, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static Button SmallButton(string name, string text, Action click)
    {
        var button = new Button
        {
            Name = name,
            Classes = { "ghelper" },
            Padding = new Thickness(10, 4),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        };
        button.Click += (_, _) => click();
        return button;
    }

    private async Task<bool> Confirm(string question)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = Labels.Get("receivers_title"),
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
        };
        var yes = SmallButton(name: "buttonReceiverUnpairConfirm", Labels.Get("receivers_unpair"), () => { tcs.TrySetResult(true); dialog.Close(); });
        var no = SmallButton(name: "buttonReceiverUnpairCancel", Labels.Get("cancel"), () => { tcs.TrySetResult(false); dialog.Close(); });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 16 };
        stack.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(buttons);
        dialog.Content = stack;
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }
}

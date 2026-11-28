using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI;

/// <summary>
/// Gold coin donate button: coin sound, shake, click count and a mute badge.
/// Each click adds a coin; two seconds after the last click the donation page opens.
/// The main window builds the look in XAML, the update prompt builds it with Create.
/// </summary>
internal sealed class CoinButton
{
    internal const string DonateUrl = "https://buymeacoffee.com/utajum";

    private static readonly IBrush GoldBrush = new SolidColorBrush(Color.Parse("#FFD700"));
    private static readonly IBrush DarkBrush = new SolidColorBrush(Color.Parse("#8B6914"));

    // Shake pattern: decaying oscillation over ~10 frames (300ms)
    private static readonly double[] ShakeOffsets = { 3, -3, 2.5, -2.5, 2, -2, 1.5, -1.5, 1, 0 };

    private readonly TextBlock _count;
    private readonly TextBlock _mute;
    private readonly TranslateTransform _transform = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _shake = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private int _clicks;
    private int _frame;

    internal CoinButton(Button button, TextBlock count, Border muteBorder, TextBlock mute)
    {
        CoinSound.EnsureReady();
        _count = count;
        _mute = mute;
        UpdateMuteVisual();

        button.RenderTransform = _transform;
        button.Click += (_, _) => OnClick();
        button.PointerEntered += (_, _) =>
        {
            if (!Muted)
                CoinSound.Play();
            StartShake();
        };
        button.PointerExited += (_, _) => StopShake();

        // The badge sits inside the button, so its press must not count as a coin.
        muteBorder.PointerPressed += OnMutePressed;
        mute.PointerPressed += OnMutePressed;

        _debounce.Tick += (_, _) => OpenPage();
        _shake.Tick += (_, _) =>
        {
            if (_frame >= ShakeOffsets.Length)
                StopShake();
            else
                _transform.X = ShakeOffsets[_frame++];
        };
    }

    private static bool Muted => AppConfig.Get("donate_muted", 1) == 1;

    /// <summary>Builds the same coin button the main window has and wires it.</summary>
    internal static Button Create(string name)
    {
        var count = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = DarkBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var mute = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var muteBorder = new Border
        {
            Name = "borderCoinMute",
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.Parse("#B8860B")),
            Padding = new Thickness(4, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = mute,
        };
        var coin = new Border
        {
            CornerRadius = new CornerRadius(12),
            Width = 24,
            Height = 24,
            Background = new SolidColorBrush(Color.Parse("#B8860B")),
            BorderBrush = DarkBrush,
            BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "$",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = GoldBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 0, 2),
            },
        };
        var label = new TextBlock
        {
            Text = Labels.Get("donate"),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#4A3800")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 24, 0),
        };
        row.Children.Add(coin);
        row.Children.Add(label);
        row.Children.Add(count);

        var content = new Grid();
        content.Children.Add(row);
        content.Children.Add(muteBorder);

        var button = new Button
        {
            Name = name,
            Classes = { "ghelper-coin" },
            Height = 36,
            MinWidth = 140,
            Content = content,
        };
        _ = new CoinButton(button, count, muteBorder, mute);
        return button;
    }

    private void OnClick()
    {
        _clicks++;
        _count.Text = $"\u00D7{_clicks}";
        if (!Muted)
            CoinSound.Play();
        StartShake();

        // Restart 2-second debounce
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnMutePressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true; // Don't trigger parent button click
        AppConfig.Set("donate_muted", Muted ? 0 : 1);
        UpdateMuteVisual();
    }

    private void UpdateMuteVisual()
    {
        bool muted = Muted;
        _mute.Text = muted ? "M" : "\u266A";
        _mute.Foreground = muted ? DarkBrush : GoldBrush;
    }

    private void OpenPage()
    {
        _debounce.Stop();
        try
        {
            string url = $"{DonateUrl}?coins={_clicks}";
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            Logger.WriteLine($"Donate: opened {url}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Donate: failed to open URL: {ex.Message}");
        }
        _clicks = 0;
        _count.Text = "";
    }

    private void StartShake()
    {
        _frame = 0;
        _shake.Start();
    }

    private void StopShake()
    {
        _shake.Stop();
        _transform.X = 0;
    }
}

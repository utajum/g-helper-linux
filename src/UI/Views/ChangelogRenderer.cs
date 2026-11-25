using System.Diagnostics;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

// Maps the small Markdown block model from ChangelogParser to Avalonia
// controls. Palette and spacing chosen to match UpdatesWindow / FansWindow
// so the new window looks native to the app. Images are fetched async on
// background tasks; the renderer hands back a list of disposables so the
// owning window can dispose decoded bitmaps when it closes.
internal static class ChangelogRenderer
{
    private static readonly IBrush ColorText = new SolidColorBrush(Color.Parse("#F0F0F0"));
    private static readonly IBrush ColorDim = new SolidColorBrush(Color.Parse("#999999"));
    private static readonly IBrush ColorAccent = new SolidColorBrush(Color.Parse("#4A9EFF"));
    private static readonly IBrush ColorSection = new SolidColorBrush(Color.Parse("#06B48A"));
    private static readonly IBrush ColorLink = new SolidColorBrush(Color.Parse("#4A9EFF"));
    private static readonly IBrush ColorCodeBg = new SolidColorBrush(Color.Parse("#262626"));
    private static readonly IBrush ColorCodeText = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush ColorImageBg = new SolidColorBrush(Color.Parse("#222222"));
    private static readonly FontFamily MonoFont = new("monospace");

    public static void Render(List<ChangelogBlock> blocks, StackPanel target, List<Bitmap> bitmapSink)
    {
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case ChangelogBlockKind.Heading1:
                    target.Children.Add(RenderHeading(block, 18, FontWeight.Bold, ColorText, topMargin: 0));
                    break;
                case ChangelogBlockKind.Heading2:
                    target.Children.Add(RenderHeading(block, 16, FontWeight.Bold, ColorAccent, topMargin: 14));
                    target.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.Parse("#333333")),
                        Margin = new Avalonia.Thickness(0, 2, 0, 4),
                    });
                    break;
                case ChangelogBlockKind.Heading3:
                    target.Children.Add(RenderHeading(block, 13, FontWeight.SemiBold, ColorSection, topMargin: 8));
                    break;
                case ChangelogBlockKind.Paragraph:
                    target.Children.Add(RenderParagraph(block, bitmapSink));
                    break;
                case ChangelogBlockKind.ListItem:
                    target.Children.Add(RenderListItem(block, bitmapSink));
                    break;
                case ChangelogBlockKind.CodeBlock:
                    target.Children.Add(RenderCodeBlock(block));
                    break;
                case ChangelogBlockKind.Image:
                    target.Children.Add(RenderBlockImage(block.ImageUrl, block.ImageAlt, bitmapSink));
                    break;
            }
        }
    }

    private static TextBlock RenderHeading(ChangelogBlock block, double size, FontWeight weight, IBrush color, double topMargin)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = color,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, topMargin, 0, 2),
        };
        AppendInlines(tb.Inlines!, block.Inlines, color, bitmapSink: null);
        return tb;
    }

    private static Control RenderParagraph(ChangelogBlock block, List<Bitmap> bitmapSink)
    {
        // If the paragraph is a lone image inline, render it as a block image
        // so it gets the full width and not a tiny inline placement.
        if (block.Inlines.Count == 1 && block.Inlines[0].Kind == ChangelogInlineKind.Image)
        {
            var only = block.Inlines[0];
            return RenderBlockImage(only.Url, only.Text, bitmapSink);
        }

        var tb = new TextBlock
        {
            FontSize = 12,
            Foreground = ColorText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 2),
        };
        AppendInlines(tb.Inlines!, block.Inlines, ColorText, bitmapSink);
        return tb;
    }

    private static Grid RenderListItem(ChangelogBlock block, List<Bitmap> bitmapSink)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(8, 1, 0, 1),
        };
        var bullet = new TextBlock
        {
            Text = "\u2022  ",
            Foreground = ColorDim,
            FontSize = 12,
        };
        Grid.SetColumn(bullet, 0);
        grid.Children.Add(bullet);

        var body = new TextBlock
        {
            FontSize = 12,
            Foreground = ColorText,
            TextWrapping = TextWrapping.Wrap,
        };
        AppendInlines(body.Inlines!, block.Inlines, ColorText, bitmapSink);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static Border RenderCodeBlock(ChangelogBlock block)
    {
        var tb = new TextBlock
        {
            Text = block.CodeText,
            FontFamily = MonoFont,
            FontSize = 11,
            Foreground = ColorCodeText,
            TextWrapping = TextWrapping.Wrap,
        };
        return new Border
        {
            Background = ColorCodeBg,
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8),
            Margin = new Avalonia.Thickness(0, 4, 0, 4),
            Child = tb,
        };
    }

    private static void AppendInlines(InlineCollection target, List<ChangelogInline> inlines, IBrush defaultColor, List<Bitmap>? bitmapSink)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case ChangelogInlineKind.Text:
                    target.Add(new Run(inline.Text) { Foreground = defaultColor });
                    break;
                case ChangelogInlineKind.Bold:
                    target.Add(new Run(inline.Text) { FontWeight = FontWeight.SemiBold, Foreground = defaultColor });
                    break;
                case ChangelogInlineKind.Code:
                    target.Add(new Run(inline.Text)
                    {
                        FontFamily = MonoFont,
                        Foreground = ColorCodeText,
                        Background = ColorCodeBg,
                    });
                    break;
                case ChangelogInlineKind.Link:
                    target.Add(BuildLinkInline(inline));
                    break;
                case ChangelogInlineKind.Image:
                    if (bitmapSink != null)
                        target.Add(BuildInlineImage(inline.Url, inline.Text, bitmapSink));
                    break;
            }
        }
    }

    private static InlineUIContainer BuildLinkInline(ChangelogInline inline)
    {
        var btn = new Button
        {
            Name = "buttonChangelogLink",
            Content = new TextBlock
            {
                Text = inline.Text,
                Foreground = ColorLink,
                TextDecorations = TextDecorations.Underline,
                FontSize = 12,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        string url = inline.Url;
        btn.Click += (_, _) => OpenInBrowser(url);
        return new InlineUIContainer { Child = btn };
    }

    private static InlineUIContainer BuildInlineImage(string url, string alt, List<Bitmap> bitmapSink)
    {
        var img = new Image
        {
            Name = "imageChangelog",
            Stretch = Stretch.Uniform,
            MaxHeight = 120,
            Margin = new Avalonia.Thickness(2, 0, 2, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(img, string.IsNullOrEmpty(alt) ? url : alt);
        img.PointerPressed += (_, _) => OpenInBrowser(url);
        _ = LoadImageAsync(url, img, bitmapSink);
        return new InlineUIContainer { Child = img };
    }

    private static Control RenderBlockImage(string url, string alt, List<Bitmap> bitmapSink)
    {
        var img = new Image
        {
            Name = "imageChangelog",
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = 680,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(img, string.IsNullOrEmpty(alt) ? url : alt);
        img.PointerPressed += (_, _) => OpenInBrowser(url);

        var caption = new TextBlock
        {
            Text = string.IsNullOrEmpty(alt) ? Labels.Get("changelog_loading") : alt,
            Foreground = ColorDim,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(img);
        // Caption only when alt text exists - GitHub-style upload tags use alt="image" placeholder.
        if (!string.IsNullOrEmpty(alt) && !alt.Equals("image", StringComparison.OrdinalIgnoreCase))
            stack.Children.Add(caption);

        var border = new Border
        {
            Background = ColorImageBg,
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8),
            Margin = new Avalonia.Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = stack,
        };

        _ = LoadImageAsync(url, img, bitmapSink);
        return border;
    }

    private static async Task LoadImageAsync(string url, Image target, List<Bitmap> bitmapSink)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent",
                "G-Helper-Linux/" + Helpers.AppConfig.AppVersion);
            http.Timeout = TimeSpan.FromSeconds(12);
            var bytes = await http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                target.Source = bitmap;
                lock (bitmapSink)
                    bitmapSink.Add(bitmap);
            });
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogRenderer: image load failed '{url}': {ex.Message}");
        }
    }

    internal static void OpenInBrowser(string url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"ChangelogRenderer: open link failed: {ex.Message}");
        }
    }
}


using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GHelper.Linux.I18n;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Modal RGB color picker dialog.
///
/// Avalonia 12.x has no built-in color dialog on Linux, so we render our own:
/// 3 sliders (R/G/B), live preview swatch, hex input, 8 quick presets, Apply button.
///
/// Two overloads:
/// <list type="bullet">
///   <item><see cref="Show(Window, byte, byte, byte, Action{byte, byte, byte})"/> -
///         RGB callback, used by Aura colors which store ARGB ints in config.</item>
///   <item><see cref="Show(Window, string, Action{string})"/> -
///         hex-string callback, used by tray-icon BG/text colors which
///         store HTML hex strings ("#RRGGBB") in config.</item>
/// </list>
///
/// Both overloads share the same UI; the hex overload simply wraps the RGB
/// overload and translates between byte triplets and "#RRGGBB" strings.
/// </summary>
public static class ColorPicker
{
    /// <summary>
    /// Show a modal RGB picker dialog. Returns immediately; <paramref name="onColorSet"/>
    /// fires on the UI thread when the user clicks Apply (never fires on Cancel /
    /// window close).
    /// </summary>
    public static void Show(Window owner, byte initR, byte initG, byte initB,
        Action<byte, byte, byte> onColorSet, bool allowRandom = false)
    {
        var pickerWindow = new Window
        {
            Title = Labels.Get("pick_color"),
            Width = 320,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
        };

        var preview = new Border
        {
            Width = 280,
            Height = 50,
            CornerRadius = new Avalonia.CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(initR, initG, initB)),
            Margin = new Avalonia.Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var sliderR = new Slider { Name = "sliderColorRed", Minimum = 0, Maximum = 255, Value = initR, Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)) };
        var sliderG = new Slider { Name = "sliderColorGreen", Minimum = 0, Maximum = 255, Value = initG, Foreground = new SolidColorBrush(Color.FromRgb(80, 255, 80)) };
        var sliderB = new Slider { Name = "sliderColorBlue", Minimum = 0, Maximum = 255, Value = initB, Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 255)) };

        var labelR = new TextBlock { Text = $"R: {initR}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };
        var labelG = new TextBlock { Text = $"G: {initG}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };
        var labelB = new TextBlock { Text = $"B: {initB}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 2, 0, 0) };

        // Hex color input
        var hexLabel = new TextBlock { Text = Labels.Get("hex_label"), Foreground = Brushes.White, Margin = new Avalonia.Thickness(4, 6, 0, 0), FontSize = 11 };
        var hexInput = new TextBox
        {
            Name = "textColorHex",
            Text = $"#{initR:X2}{initG:X2}{initB:X2}",
            Width = 100,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(4, 2, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#262626")),
            Foreground = Brushes.White,
        };
        bool _suppressHexUpdate = false;

        void UpdatePreview()
        {
            byte r = (byte)sliderR.Value;
            byte g = (byte)sliderG.Value;
            byte b = (byte)sliderB.Value;
            preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            labelR.Text = $"R: {r}";
            labelG.Text = $"G: {g}";
            labelB.Text = $"B: {b}";
            if (!_suppressHexUpdate)
                hexInput.Text = $"#{r:X2}{g:X2}{b:X2}";
        }

        sliderR.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderG.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };
        sliderB.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") UpdatePreview(); };

        // Parse hex input when user types
        hexInput.TextChanged += (_, _) =>
        {
            var text = hexInput.Text?.Trim() ?? "";
            if (!text.StartsWith("#"))
                text = "#" + text;
            if (text.Length == 7)
            {
                try
                {
                    var c = Color.Parse(text);
                    _suppressHexUpdate = true;
                    sliderR.Value = c.R;
                    sliderG.Value = c.G;
                    sliderB.Value = c.B;
                    _suppressHexUpdate = false;
                    preview.Background = new SolidColorBrush(c);
                    labelR.Text = $"R: {c.R}";
                    labelG.Text = $"G: {c.G}";
                    labelB.Text = $"B: {c.B}";
                }
                catch { }
            }
        };

        var btnOk = new Button
        {
            Name = "buttonColorApply",
            Content = Labels.Get("apply"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            MinWidth = 120,
            MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#4CC2FF")),
            Foreground = Brushes.Black,
            FontWeight = FontWeight.Bold,
        };
        btnOk.Click += (_, _) =>
        {
            onColorSet((byte)sliderR.Value, (byte)sliderG.Value, (byte)sliderB.Value);
            pickerWindow.Close();
        };

        // Quick preset colors
        var presetPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Margin = new Avalonia.Thickness(0, 4) };
        var presets = new (byte R, byte G, byte B)[]
        {
            (255, 255, 255), (255, 0, 0), (0, 255, 0), (0, 0, 255),
            (255, 255, 0), (0, 255, 255), (255, 0, 255), (255, 128, 0),
        };
        foreach (var (pr, pg, pb) in presets)
        {
            var btn = new Button
            {
                Name = "buttonColorPreset",
                Width = 28,
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(pr, pg, pb)),
                Margin = new Avalonia.Thickness(1),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            };
            byte cr = pr, cg = pg, cb = pb;
            btn.Click += (_, _) =>
            {
                sliderR.Value = cr;
                sliderG.Value = cg;
                sliderB.Value = cb;
            };
            presetPanel.Children.Add(btn);
        }

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4) };
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(hexInput);

        var stack = new StackPanel { Margin = new Avalonia.Thickness(16, 8) };
        stack.Children.Add(preview);
        stack.Children.Add(presetPanel);
        stack.Children.Add(hexRow);
        stack.Children.Add(labelR);
        stack.Children.Add(sliderR);
        stack.Children.Add(labelG);
        stack.Children.Add(sliderG);
        stack.Children.Add(labelB);
        stack.Children.Add(sliderB);
        stack.Children.Add(btnOk);

        // Black = firmware-random color in Star/Highlight/Laser/Ripple)
        if (allowRandom)
        {
            var btnRandom = new Button
            {
                Name = "buttonColorRandom",
                Content = Labels.Get("aura_random_color"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 8, 0, 0),
                MinWidth = 120,
                MinHeight = 30,
            };
            btnRandom.Click += (_, _) =>
            {
                onColorSet(0, 0, 0);
                pickerWindow.Close();
            };
            stack.Children.Add(btnRandom);
        }

        pickerWindow.Content = stack;
        pickerWindow.ShowDialog(owner);
    }

    /// <summary>
    /// Hex-string convenience overload. Parses <paramref name="initHex"/>
    /// (#RRGGBB or RRGGBB), opens the picker, and on Apply invokes
    /// <paramref name="onColorSet"/> with the chosen color formatted
    /// as "#RRGGBB" (uppercase).
    ///
    /// On parse failure (malformed hex), falls back to white. Useful for
    /// new config keys that have never been set.
    /// </summary>
    public static void Show(Window owner, string initHex, Action<string> onColorSet)
    {
        byte r = 255, g = 255, b = 255;
        try
        {
            var parsed = Color.Parse(initHex);
            r = parsed.R;
            g = parsed.G;
            b = parsed.B;
        }
        catch
        {
            // Fall back to white on malformed input.
        }

        Show(owner, r, g, b, (nr, ng, nb) =>
        {
            onColorSet($"#{nr:X2}{ng:X2}{nb:X2}");
        });
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GHelper.Linux.Input;
using static GHelper.Linux.Input.NumberPadLayouts;

namespace GHelper.Linux.UI.Controls;

/// <summary>
/// A NumberPad layout drawn on the touchpad shape with the hit test the touchpad
/// uses: keys gray, NumLock hold zone orange, calculator zone blue.
/// </summary>
public sealed class NumberPadPreview : Control
{
    private static readonly IBrush Pad = new SolidColorBrush(Color.Parse("#141414"));
    private static readonly IBrush Key = new SolidColorBrush(Color.Parse("#373737"));
    private static readonly IBrush Edge = new SolidColorBrush(Color.Parse("#1C1C1C"));
    private static readonly IBrush Toggle = new SolidColorBrush(Color.Parse("#8A3A22"));
    private static readonly IBrush Calc = new SolidColorBrush(Color.Parse("#2A5A78"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#F0F0F0"));
    private static readonly ushort[] Digits = { KEY_KP0, KEY_KP1, KEY_KP2, KEY_KP3, KEY_KP4, KEY_KP5, KEY_KP6, KEY_KP7, KEY_KP8, KEY_KP9 };

    private NumberPadSurface? _surface;
    private TouchpadRange _range;

    public void Show(NumberPadLayout layout, TouchpadRange range)
    {
        _surface = new NumberPadSurface(layout, range);
        _range = range;
        bool mm = range.XRes > 0 && range.YRes > 0;
        double aspect = mm ? (double)range.YMax / range.YRes / ((double)range.XMax / range.XRes) : (double)range.YMax / range.XMax;
        Height = Math.Round(Width * aspect);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var s = _surface;
        int w = (int)Bounds.Width, h = (int)Bounds.Height;
        if (s == null || w <= 0 || h <= 0)
            return;

        string[] labels = s.Layout.Cells.Select(c => c == null ? "" : Label(c)).ToArray();
        var hits = new NumberPadHit[w, h];
        var keys = new string[w, h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                var hit = s.Hit((int)((x + 0.5) * _range.XMax / w), (int)((y + 0.5) * _range.YMax / h));
                hits[x, y] = hit;
                keys[x, y] = hit.Calc ? "calc" : hit.Cell >= 0 ? labels[hit.Cell] : "";
            }

        var centers = new Dictionary<string, (double X, double Y, int N)>();
        for (int y = 0; y < h; y++)
        {
            IBrush? run = null;
            int start = 0;
            for (int x = 0; x <= w; x++)
            {
                IBrush? b = null;
                if (x < w)
                {
                    string k = keys[x, y];
                    bool edge = k != "" && ((x + 1 < w && keys[x + 1, y] != k) || (y + 1 < h && keys[x, y + 1] != k));
                    b = edge ? Edge : hits[x, y].Toggle ? Toggle : hits[x, y].Calc ? Calc : k != "" ? Key : Pad;
                    if (k is not ("" or "calc"))
                    {
                        var c = centers.GetValueOrDefault(k);
                        centers[k] = (c.X + x, c.Y + y, c.N + 1);
                    }
                }
                if (b == run)
                    continue;
                if (run != null)
                    context.FillRectangle(run, new Rect(start, y, x - start, 1));
                run = b;
                start = x;
            }
        }

        foreach (var (label, c) in centers)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Text);
            context.DrawText(text, new Point(c.X / c.N - text.Width / 2, c.Y / c.N - text.Height / 2));
        }
    }

    private static string Label(ushort[] keys)
    {
        int digit = Array.IndexOf(Digits, keys[^1]);
        return digit >= 0 ? digit.ToString(CultureInfo.InvariantCulture) : keys[^1] switch
        {
            KEY_KPDOT => ".",
            KEY_KPENTER => "Enter",
            KEY_KPPLUS => "+",
            KEY_KPMINUS => "-",
            KEY_KPASTERISK => "*",
            KEY_KPSLASH => "/",
            KEY_KPEQUAL => "=",
            KEY_BACKSPACE => "Bksp",
            KEY_5 => "%",
            _ => "?",
        };
    }
}

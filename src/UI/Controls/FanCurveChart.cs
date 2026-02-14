using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GHelper.Linux.UI.Controls;

/// <summary>
/// Custom fan curve chart control.
/// Replaces G-Helper's WinForms Chart-based fan curve editor.
/// 
/// Features:
///   - 8 draggable points (temperature → fan speed %)
///   - X axis: temperature 20-100°C
///   - Y axis: fan speed 0-100%
///   - Interactive mouse drag to adjust points
///   - Grid lines matching G-Helper's chartMain/chartGrid colors
///   - Color-coded per fan (CPU=blue, GPU=red, Mid=green)
/// </summary>
public class FanCurveChart : Control
{
    // ── Dependency Properties ──

    public static readonly StyledProperty<byte[]?> CurveDataProperty =
        AvaloniaProperty.Register<FanCurveChart, byte[]?>(nameof(CurveData));

    public static readonly StyledProperty<IBrush> LineColorProperty =
        AvaloniaProperty.Register<FanCurveChart, IBrush>(nameof(LineColor),
            new SolidColorBrush(Color.Parse("#3AAEEF")));

    public static readonly StyledProperty<string> FanLabelProperty =
        AvaloniaProperty.Register<FanCurveChart, string>(nameof(FanLabel), "CPU");

    /// <summary>
    /// 16-byte fan curve: bytes 0-7 = temperatures (°C), bytes 8-15 = fan speeds (%)
    /// </summary>
    public byte[]? CurveData
    {
        get => GetValue(CurveDataProperty);
        set => SetValue(CurveDataProperty, value);
    }

    public IBrush LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public string FanLabel
    {
        get => GetValue(FanLabelProperty);
        set => SetValue(FanLabelProperty, value);
    }

    // ── Constants ──

    private const int PointCount = 8;
    private const int TempMin = 20;
    private const int TempMax = 100;
    private const int FanMin = 0;
    private const int FanMax = 100;
    private const double PointRadius = 6;
    private const double ChartMargin = 40; // Space for axis labels

    // ── State ──

    private int _dragIndex = -1;

    // ── Colors matching G-Helper ──
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#232323"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#464646"));
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#A0A0A0"));
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static readonly Pen AxisPen = new(AxisBrush, 1);

    public event EventHandler<byte[]>? CurveChanged;

    public FanCurveChart()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurveDataProperty)
            InvalidateVisual();
    }

    // ── Rendering ──

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var chartArea = new Rect(ChartMargin, 10, bounds.Width - ChartMargin - 10, bounds.Height - ChartMargin - 10);

        // Background
        context.FillRectangle(BackgroundBrush, bounds);

        // Grid
        DrawGrid(context, chartArea);

        // Axis labels
        DrawAxisLabels(context, chartArea);

        // Fan curve
        if (CurveData is { Length: 16 })
        {
            DrawCurve(context, chartArea);
            DrawPoints(context, chartArea);
        }

        // Fan label
        var labelText = new FormattedText(FanLabel,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Ubuntu, sans-serif", FontStyle.Normal, FontWeight.SemiBold),
            13, LabelBrush);
        context.DrawText(labelText, new Point(chartArea.Left + 5, 12));
    }

    private void DrawGrid(DrawingContext context, Rect area)
    {
        // Vertical lines (temperature every 10°C)
        for (int t = TempMin; t <= TempMax; t += 10)
        {
            double x = MapTempToX(t, area);
            context.DrawLine(GridPen, new Point(x, area.Top), new Point(x, area.Bottom));
        }

        // Horizontal lines (fan speed every 20%)
        for (int f = FanMin; f <= FanMax; f += 20)
        {
            double y = MapFanToY(f, area);
            context.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
        }

        // Axis border
        context.DrawLine(AxisPen, new Point(area.Left, area.Top), new Point(area.Left, area.Bottom));
        context.DrawLine(AxisPen, new Point(area.Left, area.Bottom), new Point(area.Right, area.Bottom));
    }

    private void DrawAxisLabels(DrawingContext context, Rect area)
    {
        var typeface = new Typeface("Segoe UI, Ubuntu, sans-serif");

        // X axis: Temperature labels
        for (int t = TempMin; t <= TempMax; t += 20)
        {
            double x = MapTempToX(t, area);
            var text = new FormattedText($"{t}°",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, LabelBrush);
            context.DrawText(text, new Point(x - text.Width / 2, area.Bottom + 4));
        }

        // Y axis: Fan speed labels
        for (int f = FanMin; f <= FanMax; f += 20)
        {
            double y = MapFanToY(f, area);
            var text = new FormattedText($"{f}%",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, LabelBrush);
            context.DrawText(text, new Point(area.Left - text.Width - 4, y - text.Height / 2));
        }
    }

    private void DrawCurve(DrawingContext context, Rect area)
    {
        var data = CurveData!;
        var pen = new Pen(LineColor, 2);

        for (int i = 0; i < PointCount - 1; i++)
        {
            double x1 = MapTempToX(data[i], area);
            double y1 = MapFanToY(data[8 + i], area);
            double x2 = MapTempToX(data[i + 1], area);
            double y2 = MapFanToY(data[8 + i + 1], area);
            context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        }
    }

    private void DrawPoints(DrawingContext context, Rect area)
    {
        var data = CurveData!;
        var pointBrush = LineColor;
        var highlightBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));

        for (int i = 0; i < PointCount; i++)
        {
            double x = MapTempToX(data[i], area);
            double y = MapFanToY(data[8 + i], area);

            var brush = (i == _dragIndex) ? highlightBrush : pointBrush;
            context.DrawEllipse(brush, null, new Point(x, y), PointRadius, PointRadius);

            // Draw value tooltip for hovered/dragged point
            if (i == _dragIndex)
            {
                var tipText = new FormattedText($"{data[i]}°C → {data[8 + i]}%",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI, Ubuntu, sans-serif"),
                    11, highlightBrush);
                context.DrawText(tipText, new Point(x - tipText.Width / 2, y - PointRadius - tipText.Height - 2));
            }
        }
    }

    // ── Mouse interaction ──

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (CurveData is not { Length: 16 }) return;

        var pos = e.GetPosition(this);
        var area = GetChartArea();

        // Find closest point
        _dragIndex = FindClosestPoint(pos, area);
        if (_dragIndex >= 0)
        {
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragIndex < 0 || CurveData is not { Length: 16 }) return;

        var pos = e.GetPosition(this);
        var area = GetChartArea();
        var data = (byte[])CurveData!.Clone();

        // Convert mouse position to temperature/fan values
        int temp = XToTemp(pos.X, area);
        int fan = YToFan(pos.Y, area);

        // Clamp temperature: must be between neighbors
        if (_dragIndex > 0)
            temp = Math.Max(temp, data[_dragIndex - 1] + 1);
        if (_dragIndex < PointCount - 1)
            temp = Math.Min(temp, data[_dragIndex + 1] - 1);

        temp = Math.Clamp(temp, TempMin, TempMax);
        fan = Math.Clamp(fan, FanMin, FanMax);

        data[_dragIndex] = (byte)temp;
        data[8 + _dragIndex] = (byte)fan;

        CurveData = data;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex >= 0)
        {
            e.Pointer.Capture(null);
            _dragIndex = -1;
            InvalidateVisual();

            if (CurveData is { Length: 16 })
                CurveChanged?.Invoke(this, CurveData);
        }
    }

    // ── Coordinate mapping ──

    private Rect GetChartArea()
    {
        return new Rect(ChartMargin, 10, Bounds.Width - ChartMargin - 10, Bounds.Height - ChartMargin - 10);
    }

    private static double MapTempToX(int temp, Rect area)
    {
        double ratio = (temp - TempMin) / (double)(TempMax - TempMin);
        return area.Left + ratio * area.Width;
    }

    private static double MapFanToY(int fan, Rect area)
    {
        double ratio = (fan - FanMin) / (double)(FanMax - FanMin);
        return area.Bottom - ratio * area.Height; // Y is inverted
    }

    private static int XToTemp(double x, Rect area)
    {
        double ratio = (x - area.Left) / area.Width;
        return (int)Math.Round(TempMin + ratio * (TempMax - TempMin));
    }

    private static int YToFan(double y, Rect area)
    {
        double ratio = (area.Bottom - y) / area.Height;
        return (int)Math.Round(FanMin + ratio * (FanMax - FanMin));
    }

    private int FindClosestPoint(Point mouse, Rect area)
    {
        if (CurveData is not { Length: 16 }) return -1;

        double minDist = PointRadius * 3; // Max grab distance
        int closest = -1;

        for (int i = 0; i < PointCount; i++)
        {
            double px = MapTempToX(CurveData[i], area);
            double py = MapFanToY(CurveData[8 + i], area);

            double dist = Math.Sqrt(Math.Pow(mouse.X - px, 2) + Math.Pow(mouse.Y - py, 2));
            if (dist < minDist)
            {
                minDist = dist;
                closest = i;
            }
        }

        return closest;
    }
}

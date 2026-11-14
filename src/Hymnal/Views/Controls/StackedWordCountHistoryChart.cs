using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Hymnal.Theming;
using Hymnal.ViewModels;

namespace Hymnal.Views.Controls;

public class StackedWordCountHistoryChart : Control
{
    private const double AxisWidth = 44.0;
    private const double LabelAreaHeight = 18.0;

    // The theme's chart series; the fallbacks are the purple shades this chart used before themes.
    private static readonly IBrush[] SegmentPalette =
    {
        ThemeBrushes.Get("Cora.Viz.Series1", "#9D4EDD"),
        ThemeBrushes.Get("Cora.Viz.Series2", "#C77DFF"),
        ThemeBrushes.Get("Cora.Viz.Series3", "#7B2FBE"),
        ThemeBrushes.Get("Cora.Viz.Series4", "#E0AAFF"),
        ThemeBrushes.Get("Cora.Viz.Series5", "#5A189A"),
        ThemeBrushes.Get("Cora.Viz.Series6", "#3C096C"),
    };

    public static readonly StyledProperty<IReadOnlyList<StackedHistoryPoint>?> ItemsProperty =
        AvaloniaProperty.Register<StackedWordCountHistoryChart, IReadOnlyList<StackedHistoryPoint>?>(nameof(Items));

    public static readonly StyledProperty<int?> TargetProperty =
        AvaloniaProperty.Register<StackedWordCountHistoryChart, int?>(nameof(Target));

    public static readonly StyledProperty<bool> ShowTrendlineProperty =
        AvaloniaProperty.Register<StackedWordCountHistoryChart, bool>(nameof(ShowTrendline), defaultValue: true);

    public IReadOnlyList<StackedHistoryPoint>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public bool ShowTrendline
    {
        get => GetValue(ShowTrendlineProperty);
        set => SetValue(ShowTrendlineProperty, value);
    }

    static StackedWordCountHistoryChart()
    {
        AffectsRender<StackedWordCountHistoryChart>(ItemsProperty, TargetProperty, ShowTrendlineProperty);
        AffectsMeasure<StackedWordCountHistoryChart>(ItemsProperty);
        ItemsProperty.Changed.AddClassHandler<StackedWordCountHistoryChart>((c, _) => c.UpdateTooltip());
    }

    // The theme brushes recolour in place; redraw too when a theme switch swaps the app's resources.
    public StackedWordCountHistoryChart() => ResourcesChanged += (_, _) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items == null || items.Count == 0)
            return new Size(0, 0);
        return new Size(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 160);
    }

    private void UpdateTooltip()
    {
        var items = Items;
        if (items == null || items.Count == 0) { ToolTip.SetTip(this, null); return; }

        var lastPoint = items.OrderBy(p => p.Date).Last();
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = FormatDate(lastPoint.Date),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrushes.Get("Cora.Text", "#EDE8F5")
        });
        foreach (var seg in lastPoint.Segments)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = seg.Label,
                Width = 120,
                FontSize = 11,
                Foreground = ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{seg.WordCount:N0} w",
                FontSize = 11,
                Foreground = ThemeBrushes.Get("Cora.Text", "#EDE8F5")
            });
            panel.Children.Add(row);
        }
        int total = lastPoint.Segments.Sum(s => s.WordCount);
        panel.Children.Add(new TextBlock
        {
            Text = $"Total: {total:N0} w",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrushes.Get("Cora.Text", "#EDE8F5"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        ToolTip.SetTip(this, panel);
    }

    public override void Render(DrawingContext context)
    {
        var items = Items;
        if (items == null || items.Count == 0) return;

        var sorted = items.OrderBy(p => p.Date).TakeLast(30).ToList();
        if (sorted.Count == 0) return;

        // Compute totals per day to find max
        var totals = sorted.Select(p => p.Segments.Sum(s => s.WordCount)).ToArray();
        int maxTotal = totals.Length > 0 ? totals.Max() : 0;

        // Also consider the target in the scale
        int target = Target ?? 0;
        int rawMax = Math.Max(maxTotal, target);
        if (rawMax == 0) return;

        int niceMax = ComputeNiceMax(rawMax);

        double totalWidth = Bounds.Width;
        double totalHeight = Bounds.Height;
        double barAreaHeight = totalHeight - LabelAreaHeight;
        double barAreaWidth = totalWidth - AxisWidth;
        if (barAreaHeight <= 0 || barAreaWidth <= 0) return;

        var gridBrush = ThemeBrushes.Get("Cora.Border.Subtle", "#2A2040");
        var labelBrush = ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0");
        var typeface = new Typeface("Inter");

        // Y-axis
        DrawYAxis(context, gridBrush, labelBrush, typeface, barAreaHeight, niceMax);

        // Collect segment label → color index mapping (stable across days)
        var labelToColorIndex = BuildColorMap(sorted);

        // Stacked bars
        int n = sorted.Count;
        double step = barAreaWidth / n;
        double barWidth = Math.Max(2, step - 1);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(30.0 / step));

        for (int i = 0; i < n; i++)
        {
            var point = sorted[i];
            double x = AxisWidth + 1 + i * step;
            double stackY = barAreaHeight;

            foreach (var seg in point.Segments)
            {
                if (seg.WordCount <= 0) continue;
                double segH = barAreaHeight * ((double)seg.WordCount / niceMax);
                stackY -= segH;
                int colorIdx = labelToColorIndex.TryGetValue(seg.Label, out var idx) ? idx : 0;
                var brush = SegmentPalette[colorIdx % SegmentPalette.Length];
                context.FillRectangle(brush, new Rect(x, stackY, barWidth, segH));
            }

            if (i % labelEvery == 0 || i == n - 1)
            {
                string label = FormatDate(point.Date);
                var formatted = new FormattedText(
                    label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    9,
                    labelBrush);
                context.DrawText(formatted, new Point(x, barAreaHeight + 2));
            }
        }

        // Target line (dashed amber)
        if (target > 0 && target <= niceMax)
        {
            double targetY = barAreaHeight * (1.0 - (double)target / niceMax);
            DrawDashedLine(context, new Point(AxisWidth, targetY), new Point(totalWidth, targetY),
                ThemeBrushes.Get("Cora.Accent.Create", "#FFB703"), 1.5);
        }

        // Trendline (linear regression on daily totals)
        if (ShowTrendline && totals.Length >= 2)
        {
            DrawTrendline(context, totals, niceMax, barAreaHeight, barAreaWidth, step);
        }
    }

    private void DrawYAxis(DrawingContext context, IBrush gridBrush, IBrush labelBrush, Typeface typeface, double barAreaHeight, int niceMax)
    {
        var pen = new Pen(gridBrush, 1);
        double[] fractions = { 0.0, 0.5, 1.0 };
        int[] values = { 0, niceMax / 2, niceMax };

        for (int i = 0; i < 3; i++)
        {
            double y = barAreaHeight * (1.0 - fractions[i]);
            if (fractions[i] > 0)
                context.DrawLine(pen, new Point(AxisWidth, y), new Point(Bounds.Width, y));

            string label = FormatYLabel(values[i]);
            var formatted = new FormattedText(
                label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                9,
                labelBrush);
            double textX = AxisWidth - formatted.Width - 4;
            context.DrawText(formatted, new Point(textX, y - formatted.Height / 2));
        }
    }

    private static void DrawDashedLine(DrawingContext context, Point from, Point to, IBrush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        double dx = to.X - from.X;
        double x = from.X;
        const double dashOn = 4.0;
        const double dashOff = 3.0;
        bool drawing = true;

        while (x < to.X)
        {
            double segEnd = Math.Min(x + (drawing ? dashOn : dashOff), to.X);
            if (drawing)
                context.DrawLine(pen, new Point(x, from.Y), new Point(segEnd, from.Y));
            x = segEnd;
            drawing = !drawing;
        }
    }

    private void DrawTrendline(DrawingContext context, int[] totals, int niceMax, double barAreaHeight, double barAreaWidth, double step)
    {
        int n = totals.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += totals[i];
            sumXY += i * totals[i];
            sumX2 += i * i;
        }
        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-10) return;

        double a = (n * sumXY - sumX * sumY) / denom;
        double b = (sumY - a * sumX) / n;

        double y0 = barAreaHeight * (1.0 - (a * 0 + b) / niceMax);
        double y1 = barAreaHeight * (1.0 - (a * (n - 1) + b) / niceMax);

        double x0 = AxisWidth + 1 + 0 * step + step / 2;
        double x1 = AxisWidth + 1 + (n - 1) * step + step / 2;

        y0 = Math.Clamp(y0, 0, barAreaHeight);
        y1 = Math.Clamp(y1, 0, barAreaHeight);

        var pen = new Pen(ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0"), 2);
        context.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));
    }

    private static Dictionary<string, int> BuildColorMap(List<StackedHistoryPoint> sorted)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextIdx = 0;
        foreach (var point in sorted)
        {
            foreach (var seg in point.Segments)
            {
                if (!map.ContainsKey(seg.Label))
                    map[seg.Label] = nextIdx++;
            }
        }
        return map;
    }

    private static int ComputeNiceMax(int rawMax)
    {
        if (rawMax <= 0) return 100;
        if (rawMax <= 100) return 100;
        if (rawMax <= 500) return (int)Math.Ceiling(rawMax / 100.0) * 100;
        if (rawMax <= 5000) return (int)Math.Ceiling(rawMax / 500.0) * 500;
        return (int)Math.Ceiling(rawMax / 1000.0) * 1000;
    }

    private static string FormatYLabel(int value)
    {
        if (value >= 1000) return $"{value / 1000.0:0.#}k";
        return value.ToString("N0");
    }

    private static string FormatDate(string iso)
    {
        if (DateOnly.TryParse(iso, out var d))
            return d.ToString("M/d");
        return iso;
    }
}

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

public class WordCountHistoryChart : Control
{
    private const double AxisWidth = 44.0;
    private const double LabelAreaHeight = 18.0;

    public static readonly StyledProperty<IReadOnlyList<HistoryChartPoint>?> ItemsProperty =
        AvaloniaProperty.Register<WordCountHistoryChart, IReadOnlyList<HistoryChartPoint>?>(nameof(Items));

    public IReadOnlyList<HistoryChartPoint>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    static WordCountHistoryChart()
    {
        AffectsRender<WordCountHistoryChart>(ItemsProperty);
        AffectsMeasure<WordCountHistoryChart>(ItemsProperty);
        ItemsProperty.Changed.AddClassHandler<WordCountHistoryChart>((c, _) => c.UpdateTooltip());
    }

    // The theme brushes recolour in place; redraw too when a theme switch swaps the app's resources.
    public WordCountHistoryChart() => ResourcesChanged += (_, _) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items == null || items.Count == 0)
            return new Size(0, 0);
        return new Size(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 110);
    }

    private void UpdateTooltip()
    {
        var items = Items;
        if (items == null || items.Count == 0) { ToolTip.SetTip(this, null); return; }

        var sorted = items.OrderBy(p => p.Date).ToList();
        var panel = new StackPanel { Spacing = 4 };
        foreach (var point in sorted.TakeLast(10))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = FormatDate(point.Date),
                Width = 60,
                FontSize = 11,
                Foreground = ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0")
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{point.WordCount:N0} w",
                FontSize = 11,
                Foreground = ThemeBrushes.Get("Cora.Text", "#EDE8F5")
            });
            panel.Children.Add(row);
        }
        ToolTip.SetTip(this, panel);
    }

    public override void Render(DrawingContext context)
    {
        var items = Items;
        if (items == null || items.Count == 0)
            return;

        var sorted = items.OrderBy(p => p.Date).TakeLast(30).ToList();
        if (sorted.Count == 0) return;

        int maxCount = sorted.Max(p => p.WordCount);
        if (maxCount == 0) return;

        int niceMax = ComputeNiceMax(maxCount);

        double totalWidth = Bounds.Width;
        double totalHeight = Bounds.Height;
        double barAreaHeight = totalHeight - LabelAreaHeight;
        double barAreaWidth = totalWidth - AxisWidth;
        if (barAreaHeight <= 0 || barAreaWidth <= 0) return;

        var gridBrush = ThemeBrushes.Get("Cora.Border.Subtle", "#2A2040");
        var labelBrush = ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0");
        var barBrush = ThemeBrushes.Get("Cora.Accent.Store", "#9D4EDD");
        var typeface = new Typeface("Inter");

        // Y-axis gridlines and labels at 0, niceMax/2, niceMax
        DrawYAxis(context, gridBrush, labelBrush, typeface, barAreaHeight, niceMax);

        // Bars
        int n = sorted.Count;
        double step = barAreaWidth / n;
        double barWidth = Math.Max(2, step - 1);

        int labelEvery = Math.Max(1, (int)Math.Ceiling(30.0 / step));

        for (int i = 0; i < n; i++)
        {
            var point = sorted[i];
            double x = AxisWidth + 1 + i * step;
            double barH = barAreaHeight * ((double)point.WordCount / niceMax);
            double y = barAreaHeight - barH;

            context.FillRectangle(barBrush, new Rect(x, y, barWidth, barH));

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
            // Right-align in the axis area
            double textX = AxisWidth - formatted.Width - 4;
            context.DrawText(formatted, new Point(textX, y - formatted.Height / 2));
        }
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

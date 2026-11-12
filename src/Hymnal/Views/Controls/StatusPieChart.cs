using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Hymnal.Core.Models;
using Hymnal.Theming;
using Hymnal.Views.Converters;

namespace Hymnal.Views.Controls;

public class StatusPieChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<StatusCount>?> SegmentsProperty =
        AvaloniaProperty.Register<StatusPieChart, IReadOnlyList<StatusCount>?>(nameof(Segments));

    public IReadOnlyList<StatusCount>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    static StatusPieChart()
    {
        AffectsRender<StatusPieChart>(SegmentsProperty);
        SegmentsProperty.Changed.AddClassHandler<StatusPieChart>((c, _) => c.UpdateTooltip());
    }

    // The theme brushes recolour in place; redraw too when a theme switch swaps the app's resources.
    public StatusPieChart() => ResourcesChanged += (_, _) => InvalidateVisual();

    private void UpdateTooltip()
    {
        var segments = Segments;
        if (segments == null || segments.Count == 0) { ToolTip.SetTip(this, null); return; }
        int total = segments.Sum(s => s.Count);
        if (total == 0) { ToolTip.SetTip(this, null); return; }

        var panel = new StackPanel { Spacing = 5 };
        foreach (var s in segments.OrderByDescending(s => s.Count))
        {
            int pct = (int)Math.Round(s.Count * 100.0 / total);
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = StatusBrush(s.Status),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameLabel = new TextBlock
            {
                Text = s.DisplayLabel,
                Width = 76,
                Foreground = ThemeBrushes.Get("Cora.Text", "#EDE8F5")
            };

            var countLabel = new TextBlock
            {
                Text = $"{s.Count}  ({pct}%)",
                Foreground = ThemeBrushes.Get("Cora.Text.Secondary", "#9589B0")
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(dot);
            row.Children.Add(nameLabel);
            row.Children.Add(countLabel);
            panel.Children.Add(row);
        }

        ToolTip.SetTip(this, panel);
    }

    /// <summary>The theme's colour for a status (the same keys as the status dots); unknown statuses get the chart's "other" colour.</summary>
    private static IBrush StatusBrush(ChapterStatus status) =>
        StatusToBrushConverter.KeyFor(status) is { } k
            ? ThemeBrushes.Get(k.Key, k.Fallback)
            : ThemeBrushes.Get("Cora.Viz.Other", "#808080");

    public override void Render(DrawingContext context)
    {
        var segments = Segments;
        if (segments == null || segments.Count == 0)
            return;

        double total = 0;
        foreach (var s in segments) total += s.Count;
        if (total == 0) return;

        double cx = Bounds.Width / 2;
        double cy = Bounds.Height / 2;
        double r = Math.Min(cx, cy) - 0.5;
        if (r <= 0) return;

        if (segments.Count == 1)
        {
            context.DrawEllipse(StatusBrush(segments[0].Status), null, new Point(cx, cy), r, r);
            return;
        }

        double startAngle = -Math.PI / 2;
        foreach (var segment in segments)
        {
            double sweepAngle = (segment.Count / total) * 2 * Math.PI;
            double endAngle = startAngle + sweepAngle;

            var brush = StatusBrush(segment.Status);

            var x0 = cx + r * Math.Cos(startAngle);
            var y0 = cy + r * Math.Sin(startAngle);
            var x1 = cx + r * Math.Cos(endAngle);
            var y1 = cy + r * Math.Sin(endAngle);

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(cx, cy), true);
                ctx.LineTo(new Point(x0, y0));
                ctx.ArcTo(
                    new Point(x1, y1),
                    new Size(r, r),
                    0,
                    sweepAngle > Math.PI,
                    SweepDirection.Clockwise);
                ctx.EndFigure(true);
            }

            context.DrawGeometry(brush, null, geo);
            startAngle = endAngle;
        }
    }
}

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
            var color = StatusColors.TryGetValue(s.Status, out var c) ? c : Colors.Gray;

            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameLabel = new TextBlock
            {
                Text = s.DisplayLabel,
                Width = 76,
                Foreground = new SolidColorBrush(Color.Parse("#EDE8F5"))
            };

            var countLabel = new TextBlock
            {
                Text = $"{s.Count}  ({pct}%)",
                Foreground = new SolidColorBrush(Color.Parse("#9589B0"))
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(dot);
            row.Children.Add(nameLabel);
            row.Children.Add(countLabel);
            panel.Children.Add(row);
        }

        ToolTip.SetTip(this, panel);
    }

    private static readonly Dictionary<ChapterStatus, Color> StatusColors = new()
    {
        [ChapterStatus.Planned]   = Color.Parse("#FF6B35"),
        [ChapterStatus.Outlining] = Color.Parse("#9589B0"),
        [ChapterStatus.Drafting]  = Color.Parse("#38BDF8"),
        [ChapterStatus.Editing]   = Color.Parse("#9D4EDD"),
        [ChapterStatus.Polishing] = Color.Parse("#F5C842"),
        [ChapterStatus.Reviewing] = Color.Parse("#E91E8C"),
        [ChapterStatus.Done]      = Color.Parse("#22D3A0"),
    };

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
            var color = StatusColors.TryGetValue(segments[0].Status, out var c) ? c : Colors.Gray;
            context.DrawEllipse(new SolidColorBrush(color), null, new Point(cx, cy), r, r);
            return;
        }

        double startAngle = -Math.PI / 2;
        foreach (var segment in segments)
        {
            double sweepAngle = (segment.Count / total) * 2 * Math.PI;
            double endAngle = startAngle + sweepAngle;

            var color = StatusColors.TryGetValue(segment.Status, out var sc) ? sc : Colors.Gray;
            var brush = new SolidColorBrush(color);

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

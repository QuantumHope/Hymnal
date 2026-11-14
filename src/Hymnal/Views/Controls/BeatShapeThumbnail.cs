using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hymnal.Theming;

namespace Hymnal.Views.Controls;

/// <summary>
/// A beat template's thumbnail: its tension curve (levels 0 … 1, left to right) drawn as a smooth line
/// over a soft fill, with a faint baseline. With no levels (Blank) it draws a dashed empty baseline.
/// </summary>
public class BeatShapeThumbnail : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> LevelsProperty =
        AvaloniaProperty.Register<BeatShapeThumbnail, IReadOnlyList<double>?>(nameof(Levels));

    public IReadOnlyList<double>? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    // Theme colours are looked up on every render so the thumbnail follows a theme switch.
    private static Color LineStart => ThemeBrushes.Color("Cora.Accent.Retrieve.Color", "#38BDF8");
    private static Color LineMid => ThemeBrushes.Color("Cora.Accent.Store.Color", "#9D4EDD");
    private static Color LineEnd => ThemeBrushes.Color("Cora.Accent.Merge.Color", "#E91E8C");

    private const double Inset = 8;

    static BeatShapeThumbnail() => AffectsRender<BeatShapeThumbnail>(LevelsProperty);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size).Deflate(Inset);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var baseY = bounds.Bottom;
        var baselinePen = new Pen(ThemeBrushes.Get("Cora.Border.Subtle", "#2D1B5E"), 1);
        context.DrawLine(baselinePen, new Point(bounds.Left, baseY), new Point(bounds.Right, baseY));

        var levels = Levels;
        if (levels == null || levels.Count < 2)
        {
            var midY = bounds.Top + bounds.Height / 2;
            var emptyPen = new Pen(ThemeBrushes.Get("Cora.Text.Disabled", "#5B4A86"), 1.5, new DashStyle([4, 4], 0));
            context.DrawLine(emptyPen, new Point(bounds.Left, midY), new Point(bounds.Right, midY));
            return;
        }

        var lineStart = LineStart;
        var lineMid = LineMid;
        var lineEnd = LineEnd;

        var points = ToPoints(levels, bounds);
        var line = SmoothPath(points, close: false, baseY);
        var area = SmoothPath(points, close: true, baseY);

        var horizontal = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        var across = new RelativePoint(1, 0.5, RelativeUnit.Relative);
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x66, lineMid.R, lineMid.G, lineMid.B), 0),
                new GradientStop(Color.FromArgb(0x00, lineMid.R, lineMid.G, lineMid.B), 1)
            }
        };
        var stroke = LineBrush(horizontal, across, 1, lineStart, lineMid, lineEnd);
        var glow = LineBrush(horizontal, across, 0.18, lineStart, lineMid, lineEnd);

        context.DrawGeometry(fill, null, area);
        // A wide faint pass under the line reads as a glow.
        context.DrawGeometry(null, new Pen(glow, 7, lineCap: PenLineCap.Round), line);
        context.DrawGeometry(null, new Pen(stroke, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        var peak = points[0];
        foreach (var p in points)
            if (p.Y < peak.Y)
                peak = p;
        context.DrawEllipse(new SolidColorBrush(lineEnd), null, peak, 3.5, 3.5);
    }

    /// <summary>Spreads the levels evenly across <paramref name="bounds"/>; 1 is the top, 0 the bottom.</summary>
    internal static IReadOnlyList<Point> ToPoints(IReadOnlyList<double> levels, Rect bounds)
    {
        var points = new List<Point>(levels.Count);
        for (var i = 0; i < levels.Count; i++)
        {
            var x = bounds.Left + bounds.Width * i / (levels.Count - 1);
            var y = bounds.Bottom - bounds.Height * Math.Clamp(levels[i], 0, 1);
            points.Add(new Point(x, y));
        }
        return points;
    }

    /// <summary>A Catmull-Rom curve through the points, optionally closed down to the baseline.</summary>
    private static StreamGeometry SmoothPath(IReadOnlyList<Point> points, bool close, double baseY)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(close ? new Point(points[0].X, baseY) : points[0], close);
        if (close)
            ctx.LineTo(points[0]);

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Count - 1)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.CubicBezierTo(c1, c2, p2);
        }

        if (close)
            ctx.LineTo(new Point(points[^1].X, baseY));
        ctx.EndFigure(close);
        return geometry;
    }

    private static LinearGradientBrush LineBrush(RelativePoint start, RelativePoint end, double opacity, Color lineStart, Color lineMid, Color lineEnd) => new()
    {
        StartPoint = start,
        EndPoint = end,
        Opacity = opacity,
        GradientStops =
        {
            new GradientStop(lineStart, 0),
            new GradientStop(lineMid, 0.5),
            new GradientStop(lineEnd, 1)
        }
    };
}

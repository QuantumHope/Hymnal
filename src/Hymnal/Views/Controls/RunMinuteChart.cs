using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Hymnal.ViewModels;

namespace Hymnal.Views.Controls;

/// <summary>
/// A run minute by minute, for the recap. Per minute: words added rise above the line and words cut hang below it,
/// with a marker for each minute's net. Cumulative: running totals of added, cut (below the line) and net. Hover a
/// minute for its numbers. Layout only.
/// </summary>
public sealed class RunMinuteChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<RunRecapMinute>?> ItemsProperty =
        AvaloniaProperty.Register<RunMinuteChart, IReadOnlyList<RunRecapMinute>?>(nameof(Items));

    public static readonly StyledProperty<bool> CumulativeProperty =
        AvaloniaProperty.Register<RunMinuteChart, bool>(nameof(Cumulative));

    private const double AxisWidth = 40;
    private const double LabelHeight = 16;

    private int? _hover;

    static RunMinuteChart()
    {
        AffectsRender<RunMinuteChart>(ItemsProperty, CumulativeProperty);
    }

    public RunMinuteChart()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<RunRecapMinute>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool Cumulative { get => GetValue(CumulativeProperty); set => SetValue(CumulativeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 150);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var items = Items;
        var plot = PlotRect();
        if (items is not { Count: > 0 } || plot.Width <= 0)
            return;
        var x = e.GetPosition(this).X;
        var index = Math.Clamp((int)((x - plot.Left) / (plot.Width / items.Count)), 0, items.Count - 1);
        if (_hover == index)
            return;
        _hover = index;
        var m = items[index];
        var length = m.Seconds == 60 ? "" : $" ({m.Seconds} s)";
        ToolTip.SetTip(this, Cumulative
            ? $"After minute {m.Minute}{length}: +{m.TotalAdded:N0} added, −{m.TotalCut:N0} cut, {Signed(m.TotalNet)} net"
            : $"Minute {m.Minute}{length}: +{m.Added:N0} added, −{m.Cut:N0} cut, {Signed(m.Net)} net");
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private Rect PlotRect() =>
        new(AxisWidth, 6, Math.Max(0, Bounds.Width - AxisWidth - 4), Math.Max(0, Bounds.Height - LabelHeight - 10));

    public override void Render(DrawingContext ctx)
    {
        var items = Items;
        var plot = PlotRect();
        if (items is not { Count: > 0 } || plot.Width <= 0 || plot.Height <= 0)
            return;

        var added = Res("Cora.Accent.Success.Color", Colors.LimeGreen);
        var cut = Res("Cora.Accent.Error.Color", Colors.IndianRed);
        var net = Res("Cora.Accent.Store.Color", Colors.DeepPink);
        var muted = Res("Cora.Text.Secondary.Color", Colors.Gray);
        var grid = Color.FromArgb(60, muted.R, muted.G, muted.B);

        // Scale: the tallest value above the line and the deepest below it.
        double top, bottom;
        if (Cumulative)
        {
            top = Math.Max(1, items.Max(m => Math.Max(m.TotalAdded, m.TotalNet)));
            bottom = Math.Max(items.Max(m => m.TotalCut), Math.Max(0, -items.Min(m => m.TotalNet)));
        }
        else
        {
            top = Math.Max(1, items.Max(m => m.Added));
            bottom = items.Max(m => m.Cut);
        }
        top = Nice(top);
        bottom = bottom > 0 ? Nice(bottom) : 0;
        var span = top + bottom;
        var zeroY = plot.Top + plot.Height * top / span;
        double Y(double value) => zeroY - value / span * plot.Height;

        // Gridlines and axis labels: the top, zero and the bottom.
        var gridPen = new Pen(new SolidColorBrush(grid), 1);
        ctx.DrawLine(gridPen, new Point(plot.Left, plot.Top), new Point(plot.Right, plot.Top));
        ctx.DrawLine(new Pen(new SolidColorBrush(muted), 1), new Point(plot.Left, zeroY), new Point(plot.Right, zeroY));
        Label(ctx, $"+{top:N0}", new Point(plot.Left - 6, plot.Top), muted, alignRight: true);
        Label(ctx, "0", new Point(plot.Left - 6, zeroY), muted, alignRight: true);
        if (bottom > 0)
        {
            ctx.DrawLine(gridPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            Label(ctx, $"−{bottom:N0}", new Point(plot.Left - 6, plot.Bottom), muted, alignRight: true);
        }

        var slot = plot.Width / items.Count;
        if (_hover is { } h && h < items.Count)
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(30, muted.R, muted.G, muted.B)),
                new Rect(plot.Left + h * slot, plot.Top, slot, plot.Height));

        if (Cumulative)
        {
            Line(ctx, items.Select(m => (double)m.TotalAdded).ToList(), added, 1.5);
            Line(ctx, items.Select(m => -(double)m.TotalCut).ToList(), cut, 1.5);

            // Net: a filled area from the zero line, the line on top.
            var points = NetPoints(items.Select(m => (double)m.TotalNet).ToList());
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(plot.Left, zeroY), true);
                foreach (var p in points)
                    g.LineTo(p);
                g.LineTo(new Point(points[^1].X, zeroY));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, net.R, net.G, net.B)), null, area);
            Line(ctx, items.Select(m => (double)m.TotalNet).ToList(), net, 2.5);
        }
        else
        {
            var barWidth = Math.Max(2, slot * 0.62);
            var addBrush = new SolidColorBrush(added);
            var cutBrush = new SolidColorBrush(cut);
            var netBrush = new SolidColorBrush(net);
            for (var i = 0; i < items.Count; i++)
            {
                var m = items[i];
                var x = plot.Left + i * slot + (slot - barWidth) / 2;
                if (m.Added > 0)
                    ctx.FillRectangle(addBrush, new Rect(x, Y(m.Added), barWidth, zeroY - Y(m.Added)), 2);
                if (m.Cut > 0)
                    ctx.FillRectangle(cutBrush, new Rect(x, zeroY, barWidth, Y(-m.Cut) - zeroY), 2);
                if (m.Added > 0 || m.Cut > 0)
                    ctx.FillRectangle(netBrush, new Rect(x - 1, Y(m.Net) - 1.5, barWidth + 2, 3));
            }
        }

        // Minute labels, thinned so they never crowd.
        var every = Math.Max(1, (int)Math.Ceiling(items.Count / Math.Max(1, plot.Width / 34)));
        for (var i = 0; i < items.Count; i++)
        {
            var isLast = i == items.Count - 1;
            if ((i + 1) % every != 0 && i != 0 && !isLast)
                continue;
            Label(ctx, (i + 1).ToString(CultureInfo.CurrentCulture),
                new Point(plot.Left + (i + 0.5) * slot, plot.Bottom + 4 + LabelHeight / 2), muted);
        }
        return;

        List<Point> NetPoints(List<double> values) =>
            values.Select((v, i) => new Point(plot.Left + (i + 1) * slot, Y(v))).Prepend(new Point(plot.Left, zeroY)).ToList();

        void Line(DrawingContext dc, List<double> values, Color color, double thickness)
        {
            var points = NetPoints(values);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(points[0], false);
                foreach (var p in points.Skip(1))
                    g.LineTo(p);
                g.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness, lineJoin: PenLineJoin.Round), geometry);
        }
    }

    /// <summary>Rounds an axis maximum up to 1, 2 or 5 times a power of ten.</summary>
    private static double Nice(double value)
    {
        if (value <= 0)
            return 0;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1, 2, 5, 10 })
        {
            if (value <= step * magnitude)
                return step * magnitude;
        }
        return 10 * magnitude;
    }

    private void Label(DrawingContext ctx, string text, Point anchor, Color color, bool alignRight = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 10, new SolidColorBrush(color));
        var x = alignRight ? anchor.X - formatted.Width : anchor.X - formatted.Width / 2;
        ctx.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
    }

    private static string Signed(int n) => n > 0 ? $"+{n:N0}" : n < 0 ? $"−{-n:N0}" : "±0";

    private Color Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;
}

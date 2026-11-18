using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Hymnal.Core.Services.Runs;

namespace Hymnal.Views.Controls;

/// <summary>
/// The writing calendar, GitHub style: one square per day, weeks as columns with Monday at the top, months along
/// the top. Two colour scales: the days of the current streak in the theme's accent, every other day in a quiet
/// neutral. Within each scale a light day (a short run) is pale and faint, and a heavy day is deep and saturated.
/// Today has an outline. Hover a day for its numbers. Layout only.
/// </summary>
public sealed class StreakHeatmap : Control
{
    public static readonly StyledProperty<IReadOnlyList<LogbookDay>?> DaysProperty =
        AvaloniaProperty.Register<StreakHeatmap, IReadOnlyList<LogbookDay>?>(nameof(Days));

    public static readonly StyledProperty<double> CellSizeProperty =
        AvaloniaProperty.Register<StreakHeatmap, double>(nameof(CellSize), 13);

    private const double Gap = 3;
    private const double DayLabelWidth = 26;
    private const double MonthLabelHeight = 14;

    private int? _hover;

    static StreakHeatmap()
    {
        AffectsRender<StreakHeatmap>(DaysProperty, CellSizeProperty);
        AffectsMeasure<StreakHeatmap>(DaysProperty, CellSizeProperty);
    }

    public StreakHeatmap()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Days in Monday-first weeks (<see cref="RunLogbook.Build"/>'s heatmap).</summary>
    public IReadOnlyList<LogbookDay>? Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public double CellSize { get => GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }

    private int Weeks => ((Days?.Count ?? 0) + 6) / 7;

    private double Pitch => CellSize + Gap;

    protected override Size MeasureOverride(Size availableSize) =>
        new(DayLabelWidth + Weeks * Pitch, MonthLabelHeight + 7 * Pitch);

    /// <summary>The day under a point, or null.</summary>
    internal int? IndexAt(Point point)
    {
        var days = Days;
        if (days is not { Count: > 0 })
            return null;
        var column = (int)Math.Floor((point.X - DayLabelWidth) / Pitch);
        var row = (int)Math.Floor((point.Y - MonthLabelHeight) / Pitch);
        if (column < 0 || row is < 0 or > 6)
            return null;
        var index = column * 7 + row;
        return index < days.Count ? index : null;
    }

    /// <summary>What hovering a day says.</summary>
    internal static string Describe(LogbookDay day)
    {
        var date = day.Date.ToString("ddd MMM d", CultureInfo.CurrentCulture);
        if (day.IsFuture)
            return date;
        if (day.Runs == 0)
            return $"{date}: no runs";
        var words = day.NetWords > 0 ? $"+{day.NetWords:N0}" : day.NetWords < 0 ? $"−{-day.NetWords:N0}" : "±0";
        var runs = day.Runs == 1 ? "1 run" : $"{day.Runs} runs";
        return $"{date}: {runs}, {day.Minutes} min, {words} words{(day.InStreak ? " · streak" : "")}";
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = IndexAt(e.GetPosition(this));
        if (index == _hover)
            return;
        _hover = index;
        ToolTip.SetTip(this, index is { } i && Days is { } days ? Describe(days[i]) : null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    /// <summary>
    /// A day's colour: <paramref name="level"/> 0 is a faint empty square; 1–4 climb from pale and washed out to
    /// the full, saturated <paramref name="scale"/> colour. Opaque, mixed over <paramref name="surface"/>.
    /// </summary>
    internal static Color Shade(Color scale, Color surface, int level)
    {
        if (level <= 0)
            return Mix(surface, scale, 0.1);
        var t = Math.Clamp(level, 1, 4) / 4.0;
        var hsl = scale.ToHsl();
        var tinted = new HslColor(1,
            hsl.H,
            hsl.S * (0.3 + 0.7 * t),
            hsl.L + (1 - hsl.L) * 0.3 * (1 - t)).ToRgb();
        return Mix(surface, tinted, 0.35 + 0.65 * t);
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    public override void Render(DrawingContext ctx)
    {
        var days = Days;
        if (days is not { Count: > 0 })
            return;

        var accent = Res("Cora.Accent.Store.Color", Colors.Orange);
        var neutral = Res("Cora.Text.Secondary.Color", Colors.Gray);
        var surface = Res("Cora.Surface.Void.Color", Colors.Black);
        var text = Res("Cora.Text.Color", Colors.White);
        var size = CellSize;
        var radius = Math.Max(2, size / 5);

        // Weekday labels on the left (Mon, Wed, Fri), months over the first week that starts in them.
        for (var row = 0; row < 7; row += 2)
        {
            if (row == 6)
                break;
            var name = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((row + 1) % 7));
            Label(ctx, name, new Point(0, MonthLabelHeight + row * Pitch + size / 2), neutral);
        }

        var lastMonth = -1;
        for (var week = 0; week < Weeks; week++)
        {
            var first = days[week * 7].Date;
            var month = first.Month;
            if (week == 0 || month != lastMonth)
            {
                // Skip a label that would crowd the next one (a first column that is the month's last days).
                var nextMonthSoon = week == 0 && first.AddDays(14).Month != month;
                if (!nextMonthSoon)
                    Label(ctx, first.ToString("MMM", CultureInfo.CurrentCulture),
                        new Point(DayLabelWidth + week * Pitch, MonthLabelHeight / 2), neutral);
            }
            lastMonth = month;
        }

        var todayPen = new Pen(new SolidColorBrush(text), 1.5);
        var hoverPen = new Pen(new SolidColorBrush(Color.FromArgb(160, text.R, text.G, text.B)), 1);
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var rect = new Rect(DayLabelWidth + i / 7 * Pitch, MonthLabelHeight + i % 7 * Pitch, size, size);
            if (day.IsFuture)
            {
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Mix(surface, neutral, 0.12)), 1),
                    rect.Deflate(0.5), radius, radius);
                continue;
            }

            var color = Shade(day.InStreak ? accent : neutral, surface, day.Level);
            ctx.DrawRectangle(new SolidColorBrush(color), null, rect, radius, radius);

            var isToday = i + 1 == days.Count || days[i + 1].IsFuture;
            if (isToday)
                ctx.DrawRectangle(null, todayPen, rect.Inflate(1), radius + 1, radius + 1);
            else if (_hover == i)
                ctx.DrawRectangle(null, hoverPen, rect.Inflate(0.5), radius, radius);
        }
    }

    private static void Label(DrawingContext ctx, string text, Point leftMiddle, Color color)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 9, new SolidColorBrush(color));
        ctx.DrawText(formatted, new Point(leftMiddle.X, leftMiddle.Y - formatted.Height / 2));
    }

    private Color Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;
}

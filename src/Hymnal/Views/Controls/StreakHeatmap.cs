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

    public static readonly StyledProperty<bool> FitProperty =
        AvaloniaProperty.Register<StreakHeatmap, bool>(nameof(Fit));

    public static readonly StyledProperty<bool> ShowLegendProperty =
        AvaloniaProperty.Register<StreakHeatmap, bool>(nameof(ShowLegend));

    private const double Gap = 3;
    private const double LegendHeight = 22;
    private const double DayLabelWidth = 26;
    private const double MonthLabelHeight = 14;

    private int? _hover;

    static StreakHeatmap()
    {
        AffectsRender<StreakHeatmap>(DaysProperty, CellSizeProperty, ShowLegendProperty, FitProperty);
        AffectsMeasure<StreakHeatmap>(DaysProperty, CellSizeProperty, ShowLegendProperty, FitProperty);
    }

    public StreakHeatmap()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Days in Monday-first weeks (<see cref="RunLogbook.Build"/>'s heatmap).</summary>
    public IReadOnlyList<LogbookDay>? Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public double CellSize { get => GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }

    /// <summary>
    /// Grow or shrink the squares to fill the space the layout offers (between 8 and 24 px), instead of
    /// <see cref="CellSize"/>.
    /// </summary>
    public bool Fit { get => GetValue(FitProperty); set => SetValue(FitProperty, value); }

    /// <summary>A "Less … More" key under the squares, and the streak colour.</summary>
    public bool ShowLegend { get => GetValue(ShowLegendProperty); set => SetValue(ShowLegendProperty, value); }

    private int Weeks => ((Days?.Count ?? 0) + 6) / 7;

    private double _fittedCell = double.NaN;

    /// <summary>The square size in use: <see cref="CellSize"/>, or the fitted size with <see cref="Fit"/>.</summary>
    internal double Cell => Fit && double.IsFinite(_fittedCell) ? _fittedCell : CellSize;

    private double Pitch => Cell + Gap;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Fit && Weeks > 0 && double.IsFinite(availableSize.Width))
        {
            // As large as fits both ways: the width over the weeks, and the height over the seven days.
            var cell = (availableSize.Width - DayLabelWidth) / Weeks - Gap;
            if (double.IsFinite(availableSize.Height))
                cell = Math.Min(cell, (availableSize.Height - MonthLabelHeight - (ShowLegend ? LegendHeight : 0)) / 7 - Gap);
            _fittedCell = Math.Clamp(Math.Floor(cell), 8, 24);
        }
        return new Size(DayLabelWidth + Weeks * Pitch, MonthLabelHeight + 7 * Pitch + (ShowLegend ? LegendHeight : 0));
    }

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
    /// the full, saturated <paramref name="scale"/> colour. Opaque, mixed a little with <paramref name="surface"/>.
    /// </summary>
    internal static Color Shade(Color scale, Color surface, int level)
    {
        if (level <= 0)
            return Mix(surface, scale, 0.1);
        // Pale and light at the low end, so even a short day stands out clearly from an empty one on a dark card;
        // deep and saturated at the high end.
        var t = Math.Clamp(level, 1, 4) / 4.0;
        var hsl = scale.ToHsl();
        var tinted = new HslColor(1,
            hsl.H,
            hsl.S * (0.45 + 0.55 * t),
            hsl.L + (1 - hsl.L) * 0.45 * (1 - t)).ToRgb();
        return Mix(surface, tinted, 0.8 + 0.2 * t);
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
        var size = Cell;
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

        if (ShowLegend)
            DrawLegend(ctx, accent, neutral, surface);
    }

    /// <summary>Right-aligned under the squares: "streak ■   Less ■■■■■ More".</summary>
    private void DrawLegend(DrawingContext ctx, Color accent, Color neutral, Color surface)
    {
        const double swatch = 10;
        var y = MonthLabelHeight + 7 * Pitch + LegendHeight / 2 + 2;
        var right = DayLabelWidth + Weeks * Pitch - Gap;
        var x = right;

        x -= Measure("More").Width;
        Label(ctx, "More", new Point(x, y), neutral);
        for (var level = 4; level >= 0; level--)
        {
            x -= swatch + 3;
            var r = new Rect(x, y - swatch / 2, swatch, swatch);
            ctx.DrawRectangle(new SolidColorBrush(Shade(neutral, surface, level)), null, r, 2, 2);
        }
        x -= Measure("Less").Width + 5;
        Label(ctx, "Less", new Point(x, y), neutral);

        x -= 18 + swatch;
        ctx.DrawRectangle(new SolidColorBrush(Shade(accent, surface, 3)), null,
            new Rect(x, y - swatch / 2, swatch, swatch), 2, 2);
        x -= Measure("Streak").Width + 5;
        Label(ctx, "Streak", new Point(x, y), accent);
    }

    private static FormattedText Measure(string text) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 9, null);

    private static void Label(DrawingContext ctx, string text, Point leftMiddle, Color color)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 9, new SolidColorBrush(color));
        ctx.DrawText(formatted, new Point(leftMiddle.X, leftMiddle.Y - formatted.Height / 2));
    }

    private Color Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;
}

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Hymnal.Views.Controls;

/// <summary>
/// A kitchen-timer dial for a run's length in minutes. One turn is an hour; dragging on past twelve o'clock adds
/// another lap, drawn as an inner ring. Drag or click the ring, scroll (Shift for five), use the arrow keys
/// (Page Up/Down for five), or type the minutes.
/// </summary>
public sealed class MinuteDial : Control
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<MinuteDial, int>(nameof(Value), 15, defaultBindingMode: BindingMode.TwoWay,
            coerce: (o, v) => Math.Clamp(v, ((MinuteDial)o).Minimum, ((MinuteDial)o).Maximum));

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<MinuteDial, int>(nameof(Minimum), 1);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<MinuteDial, int>(nameof(Maximum), 180);

    private const double MinutesPerLap = 60;
    private const double LapGap = 11;
    private static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(1.2);

    private bool _dragging;
    private double _dragMinutes;
    private double _lastAngle;
    private string _typed = "";
    private DateTime _typedAt;

    static MinuteDial()
    {
        AffectsRender<MinuteDial>(ValueProperty, MinimumProperty, MaximumProperty, IsFocusedProperty);
        FocusableProperty.OverrideDefaultValue<MinuteDial>(true);
    }

    public MinuteDial()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(200, 200);

    // ── Input ──────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var angle = AngleAt(e.GetPosition(this));
        // A click lands on the lap the dial is already in.
        var lap = Math.Floor((Value - 0.001) / MinutesPerLap);
        var minutes = lap * MinutesPerLap + angle / 6;
        if (minutes < Minimum)
            minutes += MinutesPerLap;
        _dragMinutes = minutes;
        _lastAngle = angle;
        _dragging = true;
        e.Pointer.Capture(this);
        Value = (int)Math.Round(minutes);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        var angle = AngleAt(e.GetPosition(this));
        var delta = angle - _lastAngle;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        _lastAngle = angle;
        _dragMinutes = Math.Clamp(_dragMinutes + delta / 6, Minimum, Maximum);
        Value = (int)Math.Round(_dragMinutes);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
        Value += Math.Sign(e.Delta.Y) * step;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int? next = e.Key switch
        {
            Key.Up or Key.Right => Value + 1,
            Key.Down or Key.Left => Value - 1,
            Key.PageUp => Value + 5,
            Key.PageDown => Value - 5,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (next is { } v)
        {
            Value = v;
            _typed = "";
            e.Handled = true;
            return;
        }

        var digit = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => e.Key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => e.Key - Key.NumPad0,
            _ => -1,
        };
        if (digit < 0)
            return;

        var now = DateTime.UtcNow;
        _typed = now - _typedAt > TypingWindow || _typed.Length >= 3 ? digit.ToString() : _typed + digit;
        _typedAt = now;
        if (int.TryParse(_typed, out var typed) && typed > 0)
            Value = typed;
        e.Handled = true;
    }

    /// <summary>Clockwise degrees from twelve o'clock, 0 to 360.</summary>
    private double AngleAt(Point p)
    {
        var c = new Rect(Bounds.Size).Center;
        var degrees = Math.Atan2(p.X - c.X, -(p.Y - c.Y)) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    // ── Drawing ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        var c = bounds.Center;
        var r = Math.Min(bounds.Width, bounds.Height) / 2 - 8;
        if (r <= 20)
            return;

        var accent = Res("Cora.Accent.Store.Color", Colors.DeepPink);
        var text = Res("Cora.Text.Color", Colors.White);
        var muted = Res("Cora.Text.Secondary.Color", Colors.Gray);
        var face = Res("Cora.Surface.Void.Color", Color.FromRgb(20, 16, 30));

        // Face, with a soft accent glow behind it.
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(WithAlpha(accent, 70), 0.7),
                new GradientStop(WithAlpha(accent, 0), 1),
            },
        }, null, c, r + 8, r + 8);
        ctx.DrawEllipse(new SolidColorBrush(face), new Pen(new SolidColorBrush(WithAlpha(muted, 90)), 1), c, r, r);

        // Minute ticks: every five, longer every fifteen.
        var tickPen = new Pen(new SolidColorBrush(WithAlpha(muted, 160)), 1.5);
        var majorPen = new Pen(new SolidColorBrush(text), 2);
        for (var m = 0; m < 60; m += 5)
        {
            var major = m % 15 == 0;
            var a = m * 6 * Math.PI / 180;
            var outer = r - 4;
            var inner = r - (major ? 14 : 9);
            ctx.DrawLine(major ? majorPen : tickPen,
                new Point(c.X + Math.Sin(a) * inner, c.Y - Math.Cos(a) * inner),
                new Point(c.X + Math.Sin(a) * outer, c.Y - Math.Cos(a) * outer));
        }

        // One arc per lap, the first on the outside.
        var ringR = r - 24;
        var laps = (int)Math.Ceiling(Value / MinutesPerLap);
        var track = new Pen(new SolidColorBrush(WithAlpha(muted, 50)), 7, lineCap: PenLineCap.Round);
        ctx.DrawEllipse(null, track, c, ringR, ringR);
        Point knob = c;
        for (var lap = 0; lap < laps; lap++)
        {
            var radius = ringR - lap * LapGap;
            var minutes = Math.Min(MinutesPerLap, Value - lap * MinutesPerLap);
            var pen = new Pen(new SolidColorBrush(lap == 0 ? accent : Lighten(accent, 0.25 * lap)), 7,
                lineCap: PenLineCap.Round);
            if (lap > 0)
                ctx.DrawEllipse(null, track, c, radius, radius);
            knob = DrawArc(ctx, pen, c, radius, minutes * 6);
        }

        // The knob at the end of the current lap.
        ctx.DrawEllipse(new SolidColorBrush(text), new Pen(new SolidColorBrush(accent), 3), knob, 8, 8);
        if (IsFocused)
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(WithAlpha(accent, 140)), 2), knob, 13, 13);

        // The number in the middle.
        var family = TryFont("Cora.Font.Machine");
        var number = new FormattedText(Value.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, FontWeight.Bold), r * 0.42,
            new SolidColorBrush(text));
        var unit = new FormattedText(Value == 1 ? "MINUTE" : "MINUTES", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, FontWeight.SemiBold), 10,
            new SolidColorBrush(muted));
        var top = c.Y - (number.Height + unit.Height) / 2;
        ctx.DrawText(number, new Point(c.X - number.Width / 2, top));
        ctx.DrawText(unit, new Point(c.X - unit.Width / 2, top + number.Height));
    }

    /// <summary>Draws an arc clockwise from twelve o'clock; returns its end point.</summary>
    private static Point DrawArc(DrawingContext ctx, Pen pen, Point c, double radius, double sweepDegrees)
    {
        var end = PointAt(c, radius, sweepDegrees);
        if (sweepDegrees >= 359.9)
        {
            ctx.DrawEllipse(null, pen, c, radius, radius);
            return PointAt(c, radius, 0);
        }
        if (sweepDegrees <= 0)
            return PointAt(c, radius, 0);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(PointAt(c, radius, 0), false);
            g.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geometry);
        return end;
    }

    private static Point PointAt(Point c, double radius, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Point(c.X + Math.Sin(a) * radius, c.Y - Math.Cos(a) * radius);
    }

    private Color Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;

    private FontFamily TryFont(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is FontFamily f ? f : FontFamily.Default;

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}

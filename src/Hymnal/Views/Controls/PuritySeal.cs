// Ported from OneRing (src/OneRing.Desktop/Controls/PuritySeal.cs at f378709); a candidate for the shared Eland.Themes package.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hymnal.Views.Controls;

/// <summary>
/// A purity seal: a wax disc stamped with a cog-and-Eye sigil, with parchment strips of litany hanging below it.
/// Purely decorative; hidden unless the theme's styles show it. Colours come from styles (theme resources).
/// </summary>
public sealed class PuritySeal : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PuritySeal, string?>(nameof(Text));
    public static readonly StyledProperty<double> SealSizeProperty =
        AvaloniaProperty.Register<PuritySeal, double>(nameof(SealSize), 34);
    public static readonly StyledProperty<double> StripLengthProperty =
        AvaloniaProperty.Register<PuritySeal, double>(nameof(StripLength), 54);
    public static readonly StyledProperty<double> TiltProperty =
        AvaloniaProperty.Register<PuritySeal, double>(nameof(Tilt));
    public static readonly StyledProperty<IBrush?> WaxProperty =
        AvaloniaProperty.Register<PuritySeal, IBrush?>(nameof(Wax), new SolidColorBrush(Color.Parse("#9A1C16")));
    public static readonly StyledProperty<IBrush?> WaxDeepProperty =
        AvaloniaProperty.Register<PuritySeal, IBrush?>(nameof(WaxDeep), new SolidColorBrush(Color.Parse("#5A0C09")));
    public static readonly StyledProperty<IBrush?> ParchmentProperty =
        AvaloniaProperty.Register<PuritySeal, IBrush?>(nameof(Parchment), new SolidColorBrush(Color.Parse("#DCC79A")));
    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<PuritySeal, IBrush?>(nameof(Ink), new SolidColorBrush(Color.Parse("#3A1A10")));
    public static readonly StyledProperty<FontFamily> ScriptFontProperty =
        AvaloniaProperty.Register<PuritySeal, FontFamily>(nameof(ScriptFont), FontFamily.Default);

    static PuritySeal()
    {
        AffectsRender<PuritySeal>(TextProperty, SealSizeProperty, StripLengthProperty, TiltProperty, WaxProperty,
            WaxDeepProperty, ParchmentProperty, InkProperty, ScriptFontProperty);
        AffectsMeasure<PuritySeal>(TextProperty, SealSizeProperty, StripLengthProperty);
    }

    /// <summary>Litany lines, one strip each, separated by '|'. Empty: a bare wax stamp with no strips.</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double SealSize { get => GetValue(SealSizeProperty); set => SetValue(SealSizeProperty, value); }
    public double StripLength { get => GetValue(StripLengthProperty); set => SetValue(StripLengthProperty, value); }
    /// <summary>Degrees the whole seal is turned, so a row of them doesn't look stamped by a machine.</summary>
    public double Tilt { get => GetValue(TiltProperty); set => SetValue(TiltProperty, value); }
    public IBrush? Wax { get => GetValue(WaxProperty); set => SetValue(WaxProperty, value); }
    public IBrush? WaxDeep { get => GetValue(WaxDeepProperty); set => SetValue(WaxDeepProperty, value); }
    public IBrush? Parchment { get => GetValue(ParchmentProperty); set => SetValue(ParchmentProperty, value); }
    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public FontFamily ScriptFont { get => GetValue(ScriptFontProperty); set => SetValue(ScriptFontProperty, value); }

    private string[] Lines => string.IsNullOrWhiteSpace(Text) ? [] : Text.Split('|', StringSplitOptions.TrimEntries);

    protected override Size MeasureOverride(Size availableSize)
    {
        var d = SealSize;
        var n = Lines.Length;
        var w = n == 0 ? d + 4 : d + StripLength * 0.7;
        var h = n == 0 ? d + 4 : d * 0.55 + StripLength + 6;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var d = SealSize;
        var r = d / 2;
        var center = new Point(Bounds.Width / 2, r + 2);
        var lines = Lines;
        var seed = StableHash(Text ?? "");

        using var tilt = ctx.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)
                                           * Matrix.CreateRotation(Tilt * Math.PI / 180)
                                           * Matrix.CreateTranslation(center.X, center.Y));

        // Parchment strips first, so the wax sits on top of them.
        for (var i = 0; i < lines.Length; i++)
        {
            var spread = lines.Length == 1 ? 0 : (i / (double)(lines.Length - 1) - 0.5);
            var angle = -spread * 38 + Jitter(seed, i, 4);
            var len = StripLength * (0.8 + 0.2 * Frac(seed, i + 7));
            var width = d * 0.36;
            var top = new Point(center.X + spread * d * 0.6, center.Y + r * 0.1);
            DrawStrip(ctx, top, width, len, angle, lines[i]);
        }

        DrawWax(ctx, center, r, seed);
    }

    private void DrawStrip(DrawingContext ctx, Point top, double width, double len, double angleDeg, string text)
    {
        var m = Matrix.CreateRotation(angleDeg * Math.PI / 180) * Matrix.CreateTranslation(top.X, top.Y);
        using var _ = ctx.PushTransform(m);

        // Swallowtail-cut strip
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var hw = width / 2;
            c.BeginFigure(new Point(-hw, 0), true);
            c.LineTo(new Point(hw, 0));
            c.LineTo(new Point(hw * 0.92, len));
            c.LineTo(new Point(0, len - width * 0.55));
            c.LineTo(new Point(-hw * 0.92, len));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(Parchment, new Pen(new SolidColorBrush(Color.FromArgb(90, 60, 30, 10)), 0.6), g);

        // Edge shading
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 80, 40, 10)), null, new Rect(width / 2 - 1.6, 0, 1.6, len - 4));

        // Litany written down the strip
        var size = Math.Max(5.5, width * 0.62);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(ScriptFont, FontStyle.Italic), size, Ink)
        {
            MaxTextWidth = len - width * 0.9,
            MaxLineCount = 1,
            Trimming = TextTrimming.None,
        };
        using (ctx.PushTransform(Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(ft.Height / 2, width * 0.55)))
            ctx.DrawText(ft, new Point(0, 0));
    }

    private void DrawWax(DrawingContext ctx, Point c, double r, int seed)
    {
        // Irregular poured-wax edge
        var blob = new StreamGeometry();
        using (var g = blob.Open())
        {
            const int n = 28;
            for (var i = 0; i <= n; i++)
            {
                var a = i / (double)n * Math.PI * 2;
                var wobble = 1 + 0.06 * Math.Sin(a * 5 + seed % 7) + 0.04 * Math.Sin(a * 9 + seed % 3);
                var p = new Point(c.X + Math.Cos(a) * r * wobble, c.Y + Math.Sin(a) * r * wobble);
                if (i == 0) g.BeginFigure(p, true); else g.LineTo(p);
            }
            g.EndFigure(true);
        }
        // Shadow, body, highlight
        using (ctx.PushTransform(Matrix.CreateTranslation(1.2, 2)))
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, blob);
        ctx.DrawGeometry(Wax, null, blob);
        var shine = new RadialGradientBrush
        {
            Center = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(110, 255, 170, 140), 0),
                new GradientStop(Color.FromArgb(0, 255, 120, 90), 0.55),
                new GradientStop(Color.FromArgb(90, 0, 0, 0), 1),
            },
        };
        ctx.DrawGeometry(shine, null, blob);

        // Raised rim of the stamp
        var deep = new Pen(WaxDeep, Math.Max(1, r * 0.09));
        var hi = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 190, 160)), Math.Max(0.6, r * 0.04));
        ctx.DrawEllipse(null, deep, c, r * 0.74, r * 0.74);
        ctx.DrawEllipse(null, hi, new Point(c.X - 0.5, c.Y - 0.6), r * 0.74, r * 0.74);

        // Sigil: the Machine's cog around the Eye
        DrawCog(ctx, c, r * 0.56, deep);
        DrawEye(ctx, c, r * 0.36, WaxDeep);
    }

    internal static void DrawCog(DrawingContext ctx, Point c, double r, IPen pen)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            const int teeth = 12;
            for (var i = 0; i < teeth * 2; i++)
            {
                var a0 = i / (double)(teeth * 2) * Math.PI * 2;
                var a1 = (i + 1) / (double)(teeth * 2) * Math.PI * 2;
                var rr = i % 2 == 0 ? r : r * 0.82;
                var p0 = new Point(c.X + Math.Cos(a0) * rr, c.Y + Math.Sin(a0) * rr);
                var p1 = new Point(c.X + Math.Cos(a1) * rr, c.Y + Math.Sin(a1) * rr);
                if (i == 0) s.BeginFigure(p0, false); else s.LineTo(p0);
                s.LineTo(p1);
            }
            s.EndFigure(true);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    /// <summary>An almond eye with a slit pupil, centred on <paramref name="c"/>, half-width <paramref name="hw"/>.</summary>
    internal static StreamGeometry EyeShape(Point c, double hw, double heightRatio = 0.55)
    {
        var g = new StreamGeometry();
        var h = hw * heightRatio * 2;
        using var s = g.Open();
        s.BeginFigure(new Point(c.X - hw, c.Y), true);
        s.QuadraticBezierTo(new Point(c.X, c.Y - h), new Point(c.X + hw, c.Y));
        s.QuadraticBezierTo(new Point(c.X, c.Y + h), new Point(c.X - hw, c.Y));
        s.EndFigure(true);
        return g;
    }

    private static void DrawEye(DrawingContext ctx, Point c, double hw, IBrush? ink)
    {
        ctx.DrawGeometry(null, new Pen(ink, Math.Max(0.8, hw * 0.16)), EyeShape(c, hw));
        ctx.DrawGeometry(ink, null, EyeShape(c, hw * 0.16, 3.2));
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in s) h = h * 31 + ch;
            return h & 0x7FFFFFFF;
        }
    }

    private static double Frac(int seed, int i) => ((unchecked(seed * 9301 + i * 49297) & 0x7FFFFFFF) % 233280) / 233280.0;
    private static double Jitter(int seed, int i, double max) => (Frac(seed, i) - 0.5) * 2 * max;
}

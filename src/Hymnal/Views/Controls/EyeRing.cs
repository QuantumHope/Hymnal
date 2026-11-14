// Ported from OneRing (src/OneRing.Desktop/Controls/EyeRing.cs at f378709); a candidate for the shared Eland.Themes package.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hymnal.Views.Controls;

/// <summary>
/// The brand mark for the Mordor theme: a gold ring wreathed in fiery script around a slit-pupilled Eye.
/// <see cref="Draw"/> is shared with the window and tray icon.
/// </summary>
public sealed class EyeRing : Control
{
    public static readonly StyledProperty<FontFamily> ScriptFontProperty =
        AvaloniaProperty.Register<EyeRing, FontFamily>(nameof(ScriptFont), FontFamily.Default);

    static EyeRing() => AffectsRender<EyeRing>(ScriptFontProperty);

    public FontFamily ScriptFont { get => GetValue(ScriptFontProperty); set => SetValue(ScriptFontProperty, value); }

    private const string Inscription = "ash nazg durbatulûk · ash nazg gimbatul · ";

    public override void Render(DrawingContext ctx) => Draw(ctx, new Rect(Bounds.Size), ScriptFont, withScript: true);

    public static void Draw(DrawingContext ctx, Rect bounds, FontFamily? scriptFont, bool withScript)
    {
        var size = Math.Min(bounds.Width, bounds.Height);
        var c = bounds.Center;
        var r = size / 2;

        // Glow of the Eye
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, 255, 110, 30), 0),
                new GradientStop(Color.FromArgb(40, 200, 50, 10), 0.6),
                new GradientStop(Color.FromArgb(0, 120, 20, 0), 1),
            },
        };
        ctx.DrawEllipse(glow, null, c, r, r);

        // The Ring
        var ringR = r * 0.78;
        var ringW = r * 0.2;
        var gold = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#FFF0B0"), 0),
                new GradientStop(Color.Parse("#E2B04A"), 0.4),
                new GradientStop(Color.Parse("#8A5A1C"), 0.75),
                new GradientStop(Color.Parse("#F2CE72"), 1),
            },
        };
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 20, 8, 2)), ringW + 2), c, ringR, ringR);
        ctx.DrawEllipse(null, new Pen(gold, ringW), c, ringR, ringR);

        // Fiery inscription around the band
        if (withScript && scriptFont is not null && size >= 40)
        {
            var fs = ringW * 0.62;
            var brush = new SolidColorBrush(Color.Parse("#FF8A2A"));
            var circumference = Math.PI * 2 * ringR;
            var step = fs * 0.52;
            var count = (int)(circumference / step);
            var typeface = new Typeface(scriptFont, FontStyle.Italic);
            for (var i = 0; i < count; i++)
            {
                var ch = Inscription[i % Inscription.Length].ToString();
                var ft = new FormattedText(ch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fs, brush);
                var a = i / (double)count * Math.PI * 2 - Math.PI / 2;
                var m = Matrix.CreateTranslation(-ft.Width / 2, -ft.Height / 2)
                        * Matrix.CreateRotation(a + Math.PI / 2)
                        * Matrix.CreateTranslation(c.X + Math.Cos(a) * ringR, c.Y + Math.Sin(a) * ringR);
                using (ctx.PushTransform(m))
                    ctx.DrawText(ft, new Point(0, 0));
            }
        }

        // The Eye
        var eyeHw = r * 0.5;
        var iris = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.Parse("#FFF2B0"), 0),
                new GradientStop(Color.Parse("#FFB040"), 0.25),
                new GradientStop(Color.Parse("#F25A12"), 0.6),
                new GradientStop(Color.Parse("#6A0E04"), 1),
            },
        };
        var eye = PuritySeal.EyeShape(c, eyeHw, 0.5);
        ctx.DrawGeometry(iris, new Pen(new SolidColorBrush(Color.Parse("#2A0602")), Math.Max(0.8, r * 0.04)), eye);
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#0A0302")), null, PuritySeal.EyeShape(c, eyeHw * 0.13, 3.4));
    }
}

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hymnal.Theming;

namespace Hymnal.Views.Controls;

/// <summary>
/// The applied theme's emblem, for the status strip and the empty editor: Synthwave's striped sun setting over its
/// grid, Mordor's Eye in its ring of script, or, for a CORA theme, her face with its signature eyes. Which one comes
/// from <see cref="HymnalTokens.Emblem"/> (bind <see cref="Kind"/> to it with DynamicResource); the colours from the
/// theme's own tokens. Draws nothing for a theme without one. Decorative and hidden from hit testing.
/// </summary>
public sealed class ThemeEmblem : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<ThemeEmblem, string?>(nameof(Kind));

    static ThemeEmblem()
    {
        AffectsRender<ThemeEmblem>(KindProperty);
        AffectsMeasure<ThemeEmblem>(KindProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ThemeEmblem>(false);
    }

    public ThemeEmblem() => ResourcesChanged += (_, _) => InvalidateVisual();

    /// <summary><c>sun</c>, <c>eye</c>, <c>face</c>, or anything else for none.</summary>
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private bool HasArt => Kind is HymnalTokens.EmblemSun or HymnalTokens.EmblemEye or HymnalTokens.EmblemFace;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!HasArt) return default;
        // Square, as large as allowed (explicit Width/Height win, as for any control)
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 64 : availableSize.Width,
                            double.IsInfinity(availableSize.Height) ? 64 : availableSize.Height);
        return new Size(side, side);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        IBrush Res(string key, string fallbackHex) =>
            this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush br ? br : new SolidColorBrush(Color.Parse(fallbackHex));

        switch (Kind)
        {
            case HymnalTokens.EmblemSun:
                DrawSun(ctx, b, Res("Synthwave.Sun", "#FF4FA3"), Res("Synthwave.Purple", "#9D4EDD"), Res("Synthwave.Pink", "#E91E8C"));
                break;
            case HymnalTokens.EmblemEye:
                var script = this.TryFindResource("Cora.Font.Persona", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
                EyeRing.Draw(ctx, b, script, withScript: Math.Min(b.Width, b.Height) >= 48);
                break;
            case HymnalTokens.EmblemFace:
                // CORA's face is drawn on a 32 × 40 grid; centre it in the square
                var side = Math.Min(b.Width, b.Height);
                var face = new Rect(b.Center.X - side * 0.4, b.Center.Y - side / 2, side * 0.8, side);
                ThemeThumbnail.DrawFace(ctx, face, Res("Chrome.Her.Mane", "#34303A"), Res("Chrome.Her.Face", "#C9CDD4"),
                    Res("Chrome.Signature", "#F9A8C9"), Res("Cora.Surface.Void", "#0C0A20"));
                break;
        }
    }

    /// <summary>
    /// A sun sinking behind a horizon, cut by the widening bands of an 80s sunset, over a grid running to the
    /// vanishing point, fitted into the largest centred square of <paramref name="bounds"/> (OneRing's Synthwave mark).
    /// </summary>
    public static void DrawSun(DrawingContext ctx, Rect bounds, IBrush? sun, IBrush? grid, IBrush? horizon)
    {
        var size = Math.Min(bounds.Width, bounds.Height);
        var o = new Point(bounds.Center.X - size / 2, bounds.Center.Y - size / 2);
        Point P(double x, double y) => new(o.X + x * size, o.Y + y * size);
        Rect R(double x, double y, double w, double h) => new(P(x, y), new Size(w * size, h * size));
        const double horizonY = 0.74;

        // The disc, minus its bands (each lower one wider) and everything under the horizon
        Geometry disc = new EllipseGeometry(R(0.12, 0.06, 0.76, 0.76));
        foreach (var (y, h) in new[] { (0.44, 0.035), (0.53, 0.045), (0.61, 0.055), (0.69, 0.065) })
            disc = new CombinedGeometry(GeometryCombineMode.Exclude, disc, new RectangleGeometry(R(0, y, 1, h)));
        disc = new CombinedGeometry(GeometryCombineMode.Exclude, disc, new RectangleGeometry(R(0, horizonY, 1, 1)));
        ctx.DrawGeometry(sun, null, disc);

        using (ctx.PushClip(R(0, horizonY, 1, 1 - horizonY)))
        {
            var gridPen = new Pen(grid, Math.Max(1, size * 0.025));
            foreach (var y in new[] { 0.81, 0.90 })
                ctx.DrawLine(gridPen, P(0, y), P(1, y));
            foreach (var x in new[] { -0.4, 0.05, 0.5, 0.95, 1.4 })
                ctx.DrawLine(gridPen, P(0.5 + (x - 0.5) * 0.18, horizonY), P(x, 1));
        }
        ctx.DrawLine(new Pen(horizon, Math.Max(1, size * 0.035)), P(0.02, horizonY), P(0.98, horizonY));
    }
}

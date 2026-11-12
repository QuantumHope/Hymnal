using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Hymnal.Theming;

namespace Hymnal.Views.Controls;

/// <summary>
/// A small drawing of Hymnal in one theme, for the theme picker (after OneRing's and CORA's ThemeThumbnail): the
/// mode bar with its band, the chapter sidebar with a selected row and status dots, and a manuscript page with a
/// heading in the theme's display face, lines of prose and the caret, four accents, and for a CORA theme her face
/// with its signature eyes. Every colour and face comes from that theme's own token file, loaded once and kept, never
/// from the theme that is applied, so the picker shows every theme as it really looks.
/// </summary>
public sealed class ThemeThumbnail : Control
{
    public static readonly StyledProperty<ThemeInfo?> ThemeInfoProperty =
        AvaloniaProperty.Register<ThemeThumbnail, ThemeInfo?>(nameof(ThemeInfo));

    private static readonly Dictionary<Uri, IResourceProvider?> Tokens = [];

    static ThemeThumbnail() => AffectsRender<ThemeThumbnail>(ThemeInfoProperty);

    /// <summary>The theme to draw (named apart from StyledElement.Theme, which is the control theme).</summary>
    public ThemeInfo? ThemeInfo { get => GetValue(ThemeInfoProperty); set => SetValue(ThemeInfoProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(104, 64);

    /// <summary>A theme's token dictionary, loaded on first use. Null if it fails to load.</summary>
    public static IResourceProvider? TokensFor(ThemeInfo theme)
    {
        lock (Tokens)
        {
            if (!Tokens.TryGetValue(theme.Tokens, out var dict))
            {
                try { dict = (IResourceProvider)AvaloniaXamlLoader.Load(theme.Tokens); }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.Xml.XmlException) { dict = null; }
                Tokens[theme.Tokens] = dict;
            }
            return dict;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        if (ThemeInfo is not { } theme || TokensFor(theme) is not { } tokens) return;
        var b = new Rect(Bounds.Size);
        IBrush Res(string key, string fallback = "Cora.Text") =>
            tokens.TryGetResource(key, theme.Variant, out var v) && v is IBrush br ? br
            : tokens.TryGetResource(fallback, theme.Variant, out var f) && f is IBrush fb ? fb : Brushes.Gray;
        T Val<T>(string key, T fallback) => tokens.TryGetResource(key, theme.Variant, out var v) && v is T t ? t : fallback;

        using (ctx.PushClip(new RoundedRect(b, 4)))
        {
            ctx.FillRectangle(Res("Cora.Surface.Window", "Cora.Surface.Void"), b);

            // Mode bar along the top, the active mode lit and underlined, then the band beneath it
            var bar = new Rect(0, 0, b.Width, 9);
            ctx.FillRectangle(Res("Cora.Gradient.Header", "Cora.Surface.Header"), bar);
            using (ctx.PushOpacity(0.75))
                foreach (var x in new[] { 38.0, 66.0 })
                    ctx.FillRectangle(Res("Cora.Text.Secondary"), new Rect(x, 3.5, 12, 2));
            ctx.FillRectangle(Res("Cora.Accent.Store"), new Rect(52, 3.5, 12, 2));
            ctx.FillRectangle(Res("Cora.Accent.Store"), new Rect(52, 7.5, 12, 1));
            ctx.DrawRectangle(Res("Cora.Accent.Create"), null, new Rect(4, 2.5, 8, 4), 1, 1);
            ctx.FillRectangle(Res("Synthwave.Neon", "Cora.Border.Header"), new Rect(0, bar.Bottom, b.Width, 1));

            // Chapter sidebar: rows with status dots, the open chapter selected
            var side = new Rect(0, bar.Bottom + 1, 27, b.Height - bar.Bottom - 1);
            ctx.FillRectangle(Res("Cora.Gradient.Sidebar", "Cora.Surface.Sidebar"), side);
            ctx.DrawLine(new Pen(Res("Cora.Border.Subtle"), 1), side.TopRight, side.BottomRight);
            string[] dots = ["Cora.Accent.Success", "Cora.Accent.Store", "Cora.Accent.Retrieve", "Cora.Accent.Unknown", "Cora.Accent.Create"];
            for (var i = 0; i < dots.Length; i++)
            {
                var y = side.Y + 5 + i * 9;
                if (i == 1)
                {
                    ctx.FillRectangle(Res("Cora.Wash.Selected", "Cora.Wash.Store"), new Rect(1, y - 3, side.Width - 2, 7));
                    ctx.FillRectangle(Res("Cora.Accent.Store"), new Rect(1, y - 3, 1.5, 7));
                }
                ctx.DrawEllipse(Res(dots[i]), null, new Point(6, y + 0.5), 1.6, 1.6);
                using (ctx.PushOpacity(i == 1 ? 1 : 0.75))
                    ctx.FillRectangle(Res(i == 1 ? "Cora.Text" : "Cora.Text.Secondary"), new Rect(10, y - 0.5, i % 2 == 0 ? 13 : 10, 2));
            }

            // The manuscript page
            var page = new Rect(side.Right + 5, bar.Bottom + 5, b.Width - side.Right - 10, b.Height - bar.Bottom - 9);
            var radius = Math.Min(Val("Cora.Radius", new CornerRadius(4)).TopLeft, 4);
            ctx.DrawRectangle(Res("Cora.Surface.Panel"), new Pen(Res("Cora.Border.Subtle"), 1), page, radius, radius);

            // A heading in the display face
            var face = new Typeface(Val("Cora.Font.Display", FontFamily.Default));
            var aa = new FormattedText("Aa", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 13, Res("Cora.Text"));
            ctx.DrawText(aa, new Point(page.X + 4, page.Y + 1));

            // Prose, ending at the caret
            var prose = Res("Cora.Text.Secondary");
            var top = page.Y + 3 + aa.Height;
            double[] widths = [page.Width - 10, page.Width - 14, page.Width - 22];
            for (var i = 0; i < widths.Length; i++)
                ctx.FillRectangle(prose, new Rect(page.X + 4, top + i * 5, widths[i], 2));
            var caretX = page.X + 4 + widths[^1] + 2;
            ctx.FillRectangle(Res("Cora.Accent.Store"), new Rect(caretX, top + 2 * 5 - 1.5, 1, 5));

            // Her face, for a theme with a signature
            if (tokens.TryGetResource("Chrome.Her.Face", theme.Variant, out var hf) && hf is IBrush herFace)
                DrawFace(ctx, new Rect(page.Right - 18, page.Y + 3, 13, 16), Res("Chrome.Her.Mane"), herFace, Res("Chrome.Signature"), Res("Cora.Surface.Void"));

            // Four accents
            string[] accents = ["Cora.Accent.Store", "Cora.Accent.Create", "Cora.Accent.Merge", "Cora.Accent.Retrieve"];
            for (var i = 0; i < accents.Length; i++)
                ctx.DrawEllipse(Res(accents[i]), null, new Point(page.Right - 22 + i * 5.5, page.Bottom - 4.5), 2, 2);
        }
        ctx.DrawRectangle(null, new Pen(Res("Cora.Border.Input", "Cora.Border.Subtle"), 1), b.Deflate(0.5), 4, 4);
    }

    // CORA's thumbnail face, on its 32 × 40 grid
    private static readonly Lazy<(Geometry Head, Geometry Seam, Geometry Eyes, Geometry Mouth)> Face = new(() => (
        StreamGeometry.Parse("M16 2 L27 8 L30 20 L25 32 L16 38 L7 32 L2 20 L5 8 Z"),
        StreamGeometry.Parse("M5 8 L16 14 L27 8 M16 14 L16 30 M2 20 L16 30 L30 20"),
        StreamGeometry.Parse("M7 18 L10 16 L14 17 L12 20 L8 20 Z M25 18 L22 16 L18 17 L20 20 L24 20 Z"),
        StreamGeometry.Parse("M11 29 Q16 30.5 21 29")));

    internal static void DrawFace(DrawingContext ctx, Rect r, IBrush mane, IBrush face, IBrush signature, IBrush voidBrush)
    {
        var s = Math.Min(r.Width / 32, r.Height / 40);
        var m = Matrix.CreateScale(s, s) * Matrix.CreateTranslation(r.X, r.Y);
        var (head, seam, eyes, mouth) = Face.Value;
        using (ctx.PushTransform(m))
        {
            using (ctx.PushOpacity(0.75)) ctx.DrawGeometry(mane, new Pen(face, 0.8), head);
            using (ctx.PushOpacity(0.4)) ctx.DrawGeometry(null, new Pen(face, 0.6), seam);
            ctx.DrawGeometry(voidBrush, new Pen(signature, 1.2), eyes);
            ctx.DrawGeometry(null, new Pen(face, 1), mouth);
        }
    }
}

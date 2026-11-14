// Ported from OneRing (src/OneRing.Desktop/Controls/ThemeChrome.cs at f378709); a candidate for the shared Eland.Themes package.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Hymnal.Views.Controls;

/// <summary>
/// A CORA theme's signature, drawn in the status bar beside its motto (after CORA's per-theme chrome, ADR-106). Which
/// one comes from the theme's <c>Theme.Chrome</c> token; its colours from the theme's <c>Chrome.*</c> tokens.
/// <list type="bullet">
/// <item>kitt: the red scanner sweeping a segmented bar, beside amber, green and red dash lamps.</item>
/// <item>cylon: the red eye gliding along a dark channel.</item>
/// <item>cortana: a hexagon and five signal bars, with light running along a circuit trace.</item>
/// <item>hal: the red eye, breathing.</item>
/// <item>lcars: a run of rounded panels in the LCARS palette.</item>
/// <item>wopr: a board of lamps blinking through war games.</item>
/// <item>any other: a short rule ending in a node of the theme's signature colour.</item>
/// </list>
/// Decorative and hidden from hit testing; animates only while it is in the visual tree and visible.
/// </summary>
public sealed class ThemeChrome : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<ThemeChrome, string?>(nameof(Kind));

    /// <summary>A fixed point in the animation, for tests and screenshots. Null: run on the clock.</summary>
    public static readonly StyledProperty<double?> PhaseProperty =
        AvaloniaProperty.Register<ThemeChrome, double?>(nameof(Phase));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    private bool _attached;

    static ThemeChrome()
    {
        AffectsRender<ThemeChrome>(KindProperty, PhaseProperty);
        AffectsMeasure<ThemeChrome>(KindProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ThemeChrome>(false);
    }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double? Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    private bool Animated => Kind is "kitt" or "cylon" or "cortana" or "hal" or "wopr";

    protected override Size MeasureOverride(Size availableSize) => Kind switch
    {
        null or "" or "none" => default,
        "kitt" => new Size(150, 12),
        "cylon" => new Size(120, 12),
        "cortana" => new Size(96, 12),
        "hal" => new Size(14, 14),
        "lcars" => new Size(120, 12),
        "wopr" => new Size(96, 12),
        _ => new Size(48, 12),
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer?.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == IsVisibleProperty || change.Property == PhaseProperty)
            UpdateTimer();
    }

    private void UpdateTimer()
    {
        var run = Animated && IsVisible && Phase is null && _attached;
        if (run)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => InvalidateVisual());
            _timer.Start();
        }
        else _timer?.Stop();
    }

    private IBrush Res(string key, string fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var v))
            switch (v)
            {
                case IBrush b: return b;
                case Color c: return new SolidColorBrush(c);
            }
        return key == fallback ? Brushes.Gray : Res(fallback, fallback);
    }

    private static Color ColorOf(IBrush b) => b is ISolidColorBrush s ? s.Color : Colors.Gray;

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        if (b.Width < 4 || b.Height < 4) return;
        var t = Phase ?? Clock.Elapsed.TotalSeconds;
        switch (Kind)
        {
            case null or "" or "none": return;
            case "kitt": Kitt(ctx, b, t); break;
            case "cylon": Cylon(ctx, b, t); break;
            case "cortana": Cortana(ctx, b, t); break;
            case "hal": Hal(ctx, b, t); break;
            case "lcars": Lcars(ctx, b); break;
            case "wopr": Wopr(ctx, b, t); break;
            default: Signature(ctx, b); break;
        }
    }

    /// <summary>Ping-pong from 0 to 1 and back over <paramref name="period"/> seconds, eased at the ends.</summary>
    private static double Sweep(double t, double period)
    {
        var p = t / period % 1;
        var tri = p < 0.5 ? p * 2 : 2 - p * 2;
        return (1 - Math.Cos(tri * Math.PI)) / 2;
    }

    private void Kitt(DrawingContext ctx, Rect b, double t)
    {
        // Dash lamps on the left, then the scanner in its bezel
        var lamp = b.Height - 4;
        string[] lamps = ["Chrome.Amber", "Chrome.DashGreen", "Chrome.DashRed"];
        for (var i = 0; i < lamps.Length; i++)
            ctx.DrawRectangle(Res(lamps[i], "Chrome.Signature"), null, new Rect(b.X + i * (lamp + 3), b.Y + 2, lamp, lamp), 1, 1);
        var bar = new Rect(b.X + lamps.Length * (lamp + 3) + 4, b.Y, b.Width - lamps.Length * (lamp + 3) - 4, b.Height);
        ctx.DrawRectangle(Res("Chrome.Bezel", "Cora.Surface.Elevated"), null, bar, 2, 2);

        const int segments = 10;
        var inner = bar.Deflate(2);
        var w = (inner.Width - (segments - 1) * 2) / segments;
        var head = Sweep(t, 1.6) * (segments - 1);
        var off = Res("Chrome.ScannerOff", "Cora.Surface.Void");
        var red = ColorOf(Res("Chrome.Scanner", "Chrome.Signature"));
        var hot = Res("Chrome.ScannerHot", "Chrome.Signature");
        for (var i = 0; i < segments; i++)
        {
            var r = new Rect(inner.X + i * (w + 2), inner.Y, w, inner.Height);
            ctx.DrawRectangle(off, null, r);
            var lit = Math.Max(0, 1 - Math.Abs(i - head) / 2.6);
            if (lit <= 0) continue;
            ctx.DrawRectangle(new SolidColorBrush(red, lit), null, r);
            if (Math.Abs(i - head) < 0.5) ctx.DrawRectangle(hot, null, r.Deflate(new Thickness(w * 0.25, 1)));
        }
    }

    private void Cylon(DrawingContext ctx, Rect b, double t)
    {
        var channel = new Rect(b.X, b.Center.Y - 3, b.Width, 6);
        ctx.DrawRectangle(Res("Chrome.RedDim", "Cora.Surface.Void"), new Pen(Res("Chrome.Gunmetal", "Cora.Border.Subtle"), 1), channel, 3, 3);
        var red = ColorOf(Res("Chrome.Red", "Chrome.Signature"));
        var x = channel.X + 6 + Sweep(t, 2.4) * (channel.Width - 12);
        var glow = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(Color.FromArgb(200, red.R, red.G, red.B), 0), new GradientStop(Color.FromArgb(0, red.R, red.G, red.B), 1) },
        };
        ctx.DrawEllipse(glow, null, new Point(x, channel.Center.Y), 22, 6);
        ctx.DrawEllipse(Res("Chrome.RedHot", "Chrome.Signature"), null, new Point(x, channel.Center.Y), 5, 2.2);
    }

    private void Cortana(DrawingContext ctx, Rect b, double t)
    {
        var cyan = Res("Chrome.Cyan", "Cora.Accent.Retrieve");
        var violet = Res("Chrome.Violet", "Chrome.Signature");
        // Hexagon with a hexagon inside it
        var c = new Point(b.X + b.Height / 2, b.Center.Y);
        ctx.DrawGeometry(null, new Pen(cyan, 1.2), Hexagon(c, b.Height / 2 - 0.5));
        ctx.DrawGeometry(violet, null, Hexagon(c, b.Height / 4));
        // Trace with light running along it
        var traceY = b.Center.Y;
        var x0 = b.X + b.Height + 4;
        var x1 = b.Right - 30;
        ctx.DrawLine(new Pen(Res("Chrome.Trace", "Cora.Border.Subtle"), 1), new Point(x0, traceY), new Point(x1, traceY));
        var p = t / 2.2 % 1;
        var sx = x0 + p * (x1 - x0);
        var cc = ColorOf(cyan);
        var run = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, cc.R, cc.G, cc.B), 0), new GradientStop(cc, 1) },
        };
        ctx.DrawRectangle(run, null, new Rect(Math.Max(x0, sx - 18), traceY - 1, Math.Min(18, sx - x0), 2));
        // Signal bars
        for (var i = 0; i < 5; i++)
        {
            var h = 3 + i * (b.Height - 3) / 4;
            var on = i < 4 || (int)(t * 2) % 2 == 0;
            ctx.DrawRectangle(on ? cyan : violet, null, new Rect(b.Right - 26 + i * 5, b.Bottom - h, 3, h));
        }
    }

    private void Hal(DrawingContext ctx, Rect b, double t)
    {
        var c = b.Center;
        var r = Math.Min(b.Width, b.Height) / 2;
        var eye = ColorOf(Res("Chrome.Eye", "Chrome.Signature"));
        var pulse = 0.75 + 0.25 * Math.Sin(t * 2.2);
        ctx.DrawEllipse(Res("Chrome.Bezel", "Cora.Surface.Elevated"), null, c, r, r);
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * pulse), eye.R, eye.G, eye.B), 0.15),
                new GradientStop(Color.FromArgb((byte)(160 * pulse), eye.R, eye.G, eye.B), 0.55),
                new GradientStop(Color.FromArgb(0, eye.R, eye.G, eye.B), 1),
            },
        };
        ctx.DrawEllipse(glow, null, c, r * 0.82, r * 0.82);
        ctx.DrawEllipse(Res("Chrome.EyeCore", "Chrome.Signature"), null, c, r * 0.16, r * 0.16);
    }

    private void Lcars(DrawingContext ctx, Rect b)
    {
        (string Key, double Width)[] panels =
            [("Chrome.Orange", 0.28), ("Chrome.Lavender", 0.12), ("Chrome.Blue", 0.2), ("Chrome.Butterscotch", 0.14), ("Chrome.Red", 0.18)];
        var x = b.X;
        for (var i = 0; i < panels.Length; i++)
        {
            var w = b.Width * panels[i].Width;
            var radius = i == 0 || i == panels.Length - 1 ? b.Height / 2 : 0;
            ctx.DrawRectangle(Res(panels[i].Key, "Chrome.Signature"), null, new Rect(x, b.Y, w - 3, b.Height), radius, radius);
            x += w;
        }
    }

    private void Wopr(DrawingContext ctx, Rect b, double t)
    {
        const int cols = 16, rows = 3;
        var cw = b.Width / cols;
        var rh = b.Height / rows;
        var lamp = Res("Chrome.Lamp", "Chrome.Signature");
        var dim = Res("Chrome.BoardDim", "Cora.Border.Subtle");
        var tick = (int)(t * 6);
        for (var r = 0; r < rows; r++)
            for (var col = 0; col < cols; col++)
            {
                // A cheap, stable hash: each lamp has its own rhythm
                var on = ((col * 7 + r * 13 + tick * (1 + (col + r) % 3)) * 2654435761u >> 29) % 3 == 0;
                ctx.DrawRectangle(on ? lamp : dim, null, new Rect(b.X + col * cw + 1, b.Y + r * rh + 0.5, cw - 2, rh - 1.5));
            }
    }

    private void Signature(DrawingContext ctx, Rect b)
    {
        var sig = Res("Chrome.Signature", "Cora.Accent.Store");
        ctx.DrawLine(new Pen(Res("Cora.Border.Header", "Cora.Border.Subtle"), 1), new Point(b.X, b.Center.Y), new Point(b.Right - 8, b.Center.Y));
        var c = new Point(b.Right - 4, b.Center.Y);
        var d = new StreamGeometry();
        using (var g = d.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - 4), true);
            g.LineTo(new Point(c.X + 4, c.Y));
            g.LineTo(new Point(c.X, c.Y + 4));
            g.LineTo(new Point(c.X - 4, c.Y));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(sig, null, d);
    }

    private static StreamGeometry Hexagon(Point c, double r)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        for (var i = 0; i < 6; i++)
        {
            var a = Math.PI / 3 * i - Math.PI / 2;
            var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            if (i == 0) s.BeginFigure(p, true); else s.LineTo(p);
        }
        s.EndFigure(true);
        return g;
    }
}

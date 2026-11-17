using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hymnal.Core.Services.Runs;

namespace Hymnal.Views.Controls;

/// <summary>
/// The look of a running writing run, drawn over the editor without taking input: a row of flames along the bottom
/// of the window with embers rising from it (tallest in the corners), a glow round the window edge that pulses with
/// the run's heartbeat, a vignette, sparks off the HUD's word count, and a burst when the timer runs out. Everything scales with <see cref="Intensity"/>, which climbs through the final stretch.
/// Layout only; the run's view model decides what happens and when.
/// </summary>
public sealed class RunAtmosphereLayer : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, bool>(nameof(IsActive));

    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, double>(nameof(Intensity), RunIntensity.Idle);

    /// <summary>How far through the run it is, 0–1: with <see cref="Intensity"/>, it sets the heartbeat's pace.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, double>(nameof(Progress));

    public static readonly StyledProperty<bool> ShowFlamesProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, bool>(nameof(ShowFlames), true);

    public static readonly StyledProperty<bool> ShowGlowProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, bool>(nameof(ShowGlow), true);

    public static readonly StyledProperty<bool> ShowSparksProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, bool>(nameof(ShowSparks), true);

    public static readonly StyledProperty<int> SparksProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, int>(nameof(Sparks));

    public static readonly StyledProperty<int> CelebrationsProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, int>(nameof(Celebrations));

    /// <summary>Where sparks fly from, measured from the layer's top-right corner (the HUD's word count).</summary>
    public static readonly StyledProperty<Point> SparkOriginFromTopRightProperty =
        AvaloniaProperty.Register<RunAtmosphereLayer, Point>(nameof(SparkOriginFromTopRight), new Point(150, 78));

    private const int MaxParticles = 900;

    private readonly List<Particle> _particles = new();
    private readonly Random _random = new();
    private TimeSpan? _lastFrame;
    private bool _frameRequested;
    private double _shownIntensity = RunIntensity.Idle;
    private double _flash;
    private double _clock;
    private double _spawnDebt;
    private double _beatClock; // seconds into the current heartbeat

    public RunAtmosphereLayer()
    {
        IsHitTestVisible = false;
    }

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Intensity { get => GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool ShowFlames { get => GetValue(ShowFlamesProperty); set => SetValue(ShowFlamesProperty, value); }
    public bool ShowGlow { get => GetValue(ShowGlowProperty); set => SetValue(ShowGlowProperty, value); }
    public bool ShowSparks { get => GetValue(ShowSparksProperty); set => SetValue(ShowSparksProperty, value); }
    public int Sparks { get => GetValue(SparksProperty); set => SetValue(SparksProperty, value); }
    public int Celebrations { get => GetValue(CelebrationsProperty); set => SetValue(CelebrationsProperty, value); }

    public Point SparkOriginFromTopRight
    {
        get => GetValue(SparkOriginFromTopRightProperty);
        set => SetValue(SparkOriginFromTopRightProperty, value);
    }

    /// <summary>True while anything is still moving (for tests: the layer stops animating when idle).</summary>
    public bool IsAnimating => _frameRequested;

    public int ParticleCount => _particles.Count;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SparksProperty && IsActive && ShowSparks)
        {
            var added = change.GetNewValue<int>() - change.GetOldValue<int>();
            if (added > 0)
                EmitSparks(Math.Min(added * 3, 30));
        }
        else if (change.Property == CelebrationsProperty && change.GetNewValue<int>() > change.GetOldValue<int>())
        {
            Celebrate();
        }

        if (change.Property == IsActiveProperty && change.GetNewValue<bool>())
            _beatClock = 0; // the first beat lands as the run starts, with the sound's

        if (change.Property == IsActiveProperty || change.Property == SparksProperty
            || change.Property == CelebrationsProperty)
            RequestFrame();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || (!IsActive && _particles.Count == 0 && _flash <= 0))
            return;
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1 / 60.0;
        _lastFrame = now;
        Step(dt);
        InvalidateVisual();

        if (IsActive || _particles.Count > 0 || _flash > 0)
            RequestFrame();
        else
            _lastFrame = null;
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds.</summary>
    internal void Step(double dt)
    {
        _clock += dt;
        _beatClock += dt;
        var beatLength = 60 / RunIntensity.PulseBpm(Progress, _shownIntensity);
        if (_beatClock >= beatLength)
            _beatClock %= beatLength;
        // Ease towards the bound intensity (it only changes once a second).
        _shownIntensity += (Intensity - _shownIntensity) * Math.Min(1, dt * 1.5);
        _flash = Math.Max(0, _flash - dt * 1.2);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (IsActive && ShowFlames && w > 0 && h > 0)
        {
            // Embers per second from both corners together.
            var rate = 40 + 260 * _shownIntensity;
            _spawnDebt += rate * dt;
            while (_spawnDebt >= 1 && _particles.Count < MaxParticles)
            {
                _spawnDebt -= 1;
                SpawnEmber(w, h, _random.Next(2) == 0);
            }
            _spawnDebt = Math.Min(_spawnDebt, 5);
        }

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _particles.RemoveAt(i);
                continue;
            }

            p.Vx += (p.Kind == ParticleKind.Ember ? Math.Sin(_clock * 3 + p.Seed) * 30 : 0) * dt;
            p.Vy += p.Gravity * dt;
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            _particles[i] = p;
        }
    }

    private void SpawnEmber(double w, double h, bool left)
    {
        // Two in three embers rise from the corners; the rest from anywhere along the bottom.
        var spread = Math.Min(w * 0.22, 260) * (0.6 + 0.6 * _shownIntensity);
        double offset;
        if (_random.Next(3) == 0)
        {
            spread = w / 2;
            offset = _random.NextDouble() * spread;
        }
        else
        {
            offset = Math.Pow(_random.NextDouble(), 1.8) * spread; // bunched into the corner
        }
        var x = left ? offset : w - offset;
        var lift = 70 + 170 * _shownIntensity;
        _particles.Add(new Particle
        {
            Kind = ParticleKind.Ember,
            X = x,
            Y = h + 6,
            Vx = (left ? 1 : -1) * _random.NextDouble() * 25,
            Vy = -(lift * (0.6 + _random.NextDouble() * 0.8)) * (1 - offset / (spread * 1.6)),
            Gravity = -10,
            Life = 0.7 + _random.NextDouble() * (1.0 + 1.2 * _shownIntensity),
            Size = (3 + _random.NextDouble() * 9) * (0.7 + 0.8 * _shownIntensity),
            Seed = _random.NextDouble() * 10,
        });
    }

    private void EmitSparks(int count)
    {
        var origin = new Point(Bounds.Width - SparkOriginFromTopRight.X, SparkOriginFromTopRight.Y);
        for (var i = 0; i < count && _particles.Count < MaxParticles; i++)
        {
            var angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * Math.PI * 1.3;
            var speed = 120 + _random.NextDouble() * 220;
            _particles.Add(new Particle
            {
                Kind = ParticleKind.Spark,
                X = origin.X + (_random.NextDouble() - 0.5) * 40,
                Y = origin.Y,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Gravity = 420,
                Life = 0.45 + _random.NextDouble() * 0.5,
                Size = 1.5 + _random.NextDouble() * 2,
                Seed = _random.NextDouble() * 10,
            });
        }
    }

    private void Celebrate()
    {
        _flash = 1;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        for (var i = 0; i < 260 && _particles.Count < MaxParticles; i++)
        {
            var fromLeft = i % 3 == 0;
            var fromCentre = i % 3 == 2;
            var x = fromCentre ? w / 2 + (_random.NextDouble() - 0.5) * w * 0.3 : fromLeft ? 0 : w;
            var angle = fromCentre
                ? -Math.PI / 2 + (_random.NextDouble() - 0.5) * 1.2
                : fromLeft ? -Math.PI / 2 + _random.NextDouble() * 0.9 : -Math.PI / 2 - _random.NextDouble() * 0.9;
            var speed = 300 + _random.NextDouble() * 520;
            _particles.Add(new Particle
            {
                Kind = ParticleKind.Spark,
                X = x,
                Y = h,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Gravity = 380,
                Life = 1.2 + _random.NextDouble() * 1.0,
                Size = 2 + _random.NextDouble() * 3,
                Seed = _random.NextDouble() * 10,
            });
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var accent = Find("Cora.Accent.Store.Color", Color.FromRgb(255, 60, 172));
        var intensity = _shownIntensity;

        if (IsActive && ShowGlow)
            DrawGlow(ctx, w, h, accent, intensity);

        if (IsActive && ShowFlames)
            DrawFlameRow(ctx, w, h, accent, intensity);

        if (IsActive && ShowFlames)
        {
            // A warm bed of light in each corner the embers rise from.
            var size = (140 + 260 * intensity) * Math.Min(1, w / 900);
            var flicker = 0.85 + 0.15 * Math.Sin(_clock * 9) * Math.Sin(_clock * 5.3);
            foreach (var cx in new[] { 0.0, w })
            {
                ctx.DrawEllipse(new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb((byte)(150 * intensity * flicker + 40), 255, 150, 50), 0),
                        new GradientStop(Color.FromArgb((byte)(70 * intensity * flicker + 15), Mix(accent, 230, 60, 30).R,
                            Mix(accent, 230, 60, 30).G, Mix(accent, 230, 60, 30).B), 0.5),
                        new GradientStop(Color.FromArgb(0, 200, 40, 20), 1),
                    },
                }, null, new Point(cx, h), size, size * 0.8);
            }
        }

        foreach (var p in _particles)
        {
            var t = p.Age / p.Life;
            if (p.Kind == ParticleKind.Ember)
            {
                if (!ShowFlames)
                    continue;
                var color = FlameColor(t, accent);
                var alpha = (byte)(Math.Clamp(1 - t, 0, 1) * 220);
                var radius = p.Size * (1 - 0.6 * t);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(alpha / 4), color.R, color.G, color.B)), null,
                    new Point(p.X, p.Y), radius * 2.2, radius * 2.2);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), null,
                    new Point(p.X, p.Y), radius, radius);
            }
            else
            {
                var alpha = (byte)(Math.Clamp(1 - t, 0, 1) * 255);
                var color = t < 0.3 ? Color.FromRgb(255, 245, 210) : Mix(accent, 255, 190, 80);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), p.Size,
                    lineCap: PenLineCap.Round);
                ctx.DrawLine(pen, new Point(p.X, p.Y), new Point(p.X - p.Vx * 0.025, p.Y - p.Vy * 0.025));
            }
        }

        if (_flash > 0)
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(90 * _flash * _flash), accent.R, accent.G, accent.B)),
                new Rect(0, 0, w, h));
    }

    private void DrawGlow(DrawingContext ctx, double w, double h, Color accent, double intensity)
    {
        // Vignette: the edges darken and close in as the run nears its end.
        var vignette = (byte)(40 + 150 * intensity);
        ctx.FillRectangle(new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55 - 0.15 * intensity),
                new GradientStop(Color.FromArgb(vignette, 0, 0, 0), 1),
            },
        }, new Rect(0, 0, w, h));

        // Edge glow, thumping with the heartbeat (lub-dub) all run long, harder as the run nears its end.
        var beat = RunIntensity.BeatEnvelope(_beatClock);
        var pulse = 0.7 + (0.3 + 0.5 * intensity) * beat;
        var alpha = (byte)Math.Clamp((25 + 150 * intensity) * pulse, 0, 255);
        var depth = 10 + 50 * intensity;
        var edge = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        var clear = Color.FromArgb(0, accent.R, accent.G, accent.B);

        Edge(new Rect(0, 0, w, depth), new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative));
        Edge(new Rect(0, h - depth, w, depth), new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));
        Edge(new Rect(0, 0, depth, h), new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative));
        Edge(new Rect(w - depth, 0, depth, h), new RelativePoint(1, 0, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));
        return;

        void Edge(Rect rect, RelativePoint from, RelativePoint to) =>
            ctx.FillRectangle(new LinearGradientBrush
            {
                StartPoint = from,
                EndPoint = to,
                GradientStops = { new GradientStop(edge, 0), new GradientStop(clear, 1) },
            }, rect);
    }

    /// <summary>
    /// A row of flame tongues along the bottom edge: each sways and flickers on its own, they stand taller as the
    /// run heats up and toward the corners, and they jump a little on each heartbeat.
    /// </summary>
    private void DrawFlameRow(DrawingContext ctx, double w, double h, Color accent, double intensity)
    {
        const double spacing = 26;
        var count = (int)Math.Ceiling(w / spacing) + 2;
        var beat = RunIntensity.BeatEnvelope(_beatClock);
        var outer = Mix(accent, 235, 70, 30);
        var mid = Mix(accent, 255, 150, 40);
        var baseHeight = 14 + 70 * intensity;

        for (var pass = 0; pass < 2; pass++)
        {
            // The back row is taller and redder; the front row shorter, brighter and offset half a tongue.
            var back = pass == 0;
            for (var i = 0; i < count; i++)
            {
                var x = (i - 0.5 + (back ? 0 : 0.5)) * spacing;
                var seed = i * 1.618 + (back ? 0 : 7.3);
                var corner = Math.Pow(Math.Abs(x / w - 0.5) * 2, 3); // 0 in the middle, 1 at the corners
                var flicker = 0.65 + 0.2 * Math.Sin(_clock * 7.1 + seed * 3) + 0.15 * Math.Sin(_clock * 13.7 + seed * 5);
                var height = baseHeight * flicker * (1 + 1.6 * corner) * (1 + 0.18 * beat) * (back ? 1.25 : 0.8);
                var width = spacing * (back ? 1.5 : 1.15);
                var sway = Math.Sin(_clock * 2.3 + seed) * width * 0.35;

                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var left = new Point(x - width / 2, h + 2);
                    var right = new Point(x + width / 2, h + 2);
                    var tip = new Point(x + sway, h - height);
                    g.BeginFigure(left, true);
                    g.CubicBezierTo(new Point(x - width / 2, h - height * 0.45), new Point(x + sway * 0.3, h - height * 0.6), tip);
                    g.CubicBezierTo(new Point(x + sway * 0.3 + width * 0.15, h - height * 0.55), new Point(x + width / 2, h - height * 0.4), right);
                    g.EndFigure(true);
                }

                var alpha = back ? 0.55 + 0.35 * intensity : 0.7 + 0.3 * intensity;
                ctx.DrawGeometry(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(back ? WithAlpha(mid, alpha) : WithAlpha(Color.FromRgb(255, 240, 190), alpha), 0),
                        new GradientStop(WithAlpha(back ? outer : mid, alpha * 0.85), 0.45),
                        new GradientStop(WithAlpha(outer, 0), 1),
                    },
                }, null, geometry);
            }
        }
    }

    private static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>White-hot to yellow to the theme-tinted orange to deep red as an ember ages.</summary>
    private static Color FlameColor(double t, Color accent) => t switch
    {
        < 0.15 => Color.FromRgb(255, 244, 214),
        < 0.4 => Color.FromRgb(255, 196, 80),
        < 0.7 => Mix(accent, 255, 120, 40),
        _ => Mix(accent, 180, 40, 30),
    };

    /// <summary>A warm colour pulled a quarter of the way towards the theme accent, so flames suit every theme.</summary>
    private static Color Mix(Color accent, byte r, byte g, byte b) => Color.FromRgb(
        (byte)(r * 0.75 + accent.R * 0.25), (byte)(g * 0.75 + accent.G * 0.25), (byte)(b * 0.75 + accent.B * 0.25));

    private Color Find(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;

    private enum ParticleKind
    {
        Ember,
        Spark,
    }

    private struct Particle
    {
        public ParticleKind Kind;
        public double X, Y, Vx, Vy, Gravity, Age, Life, Size, Seed;
    }
}

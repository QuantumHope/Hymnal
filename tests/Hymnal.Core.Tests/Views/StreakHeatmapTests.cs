using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Hymnal.Audio;
using Hymnal.Core.Services.Runs;
using Hymnal.Views.Controls;
using Xunit;

namespace Hymnal.Core.Tests.Views;

public class StreakHeatmapTests
{
    private static readonly Color Accent = Color.Parse("#FF7A2E");
    private static readonly Color Neutral = Color.Parse("#B4A07C");
    private static readonly Color Surface = Color.Parse("#0B0807");

    [Fact]
    public void Light_days_are_pale_and_dense_days_saturated_in_both_scales()
    {
        foreach (var scale in new[] { Accent, Neutral })
        {
            var shades = Enumerable.Range(1, 4).Select(level => StreakHeatmap.Shade(scale, Surface, level)).ToList();
            for (var i = 1; i < shades.Count; i++)
            {
                Assert.True(shades[i].ToHsl().S > shades[i - 1].ToHsl().S, $"saturation climbs at level {i + 1}");
                Assert.True(shades[i].ToHsl().L < shades[i - 1].ToHsl().L, $"lighter at the low end, level {i + 1}");
            }
            Assert.True(Distance(shades[^1], scale) < 2); // the busiest day is the scale colour itself
            // Even the lightest day is clearly not an empty square on a dark card.
            Assert.True(Distance(shades[0], Surface) > 150, $"level 1 is only {Distance(shades[0], Surface):0} from the surface");
        }
    }

    [Fact]
    public void The_streak_scale_is_the_accent_and_differs_from_the_other_days()
    {
        for (var level = 1; level <= 4; level++)
            Assert.NotEqual(StreakHeatmap.Shade(Accent, Surface, level), StreakHeatmap.Shade(Neutral, Surface, level));
        // An empty day is a faint square, barely off the surface.
        Assert.True(Distance(StreakHeatmap.Shade(Neutral, Surface, 0), Surface) < 40);
    }

    [Fact]
    public void Hovering_finds_the_day_and_describes_it()
    {
        var monday = new DateOnly(2026, 11, 16);
        var days = Enumerable.Range(0, 14)
            .Select(i => new LogbookDay(monday.AddDays(i - 7), i == 8 ? 2 : 0, i == 8 ? 35 : 0, i == 8 ? 410 : 0,
                i == 8 ? 3 : 0, i > 8, InStreak: i == 8))
            .ToList();
        var map = new StreakHeatmap { Days = days, CellSize = 10 };

        // Column 1 (second week), row 1 (Tuesday): 26 px of day names, 14 px of month names, 13 px pitch.
        Assert.Equal(8, map.IndexAt(new Point(26 + 13 + 5, 14 + 13 + 5)));
        Assert.Null(map.IndexAt(new Point(5, 30)));
        Assert.Null(map.IndexAt(new Point(26 + 13 * 5, 30)));
        Assert.Equal("Tue Nov 17: 2 runs, 35 min, +410 words · streak", StreakHeatmap.Describe(days[8]));
        Assert.Equal("Mon Nov 9: no runs", StreakHeatmap.Describe(days[0]));
    }

    [Fact]
    public void Run_ambience_is_loud_enough_to_hear_beside_typing()
    {
        // At the default volume the ambience bus starts at a clearly audible level and reaches the slider's full
        // level at the end; each beat plays near full volume from the first one.
        var start = RunAmbiencePlayer.BusGain(Hymnal.Core.Models.Runs.RunAtmosphereSettings.DefaultAmbienceVolume, RunIntensity.Idle);
        var end = RunAmbiencePlayer.BusGain(Hymnal.Core.Models.Runs.RunAtmosphereSettings.DefaultAmbienceVolume, 1);
        Assert.InRange(start, 0.4f, end);
        Assert.Equal(0.64f, end, 3);
        Assert.InRange(RunAmbiencePlayer.HeartbeatGain(RunIntensity.Idle), 0.8f, 1f);
    }

    private static double Distance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));
}

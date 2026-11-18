using Hymnal.Core.Audio;
using Hymnal.Core.Models.Runs;
using Hymnal.Core.Services.Runs;

namespace Hymnal.Core.Tests.Services.Runs;

public class RunIntensityTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [Fact]
    public void Most_of_a_run_idles_low()
    {
        Assert.Equal(RunIntensity.Idle, RunIntensity.At(TimeSpan.FromMinutes(9), TenMinutes));
        Assert.Equal(RunIntensity.Idle, RunIntensity.At(TimeSpan.FromSeconds(60), TenMinutes));
        Assert.False(RunIntensity.IsFinalStretch(TimeSpan.FromSeconds(61), TenMinutes));
    }

    [Fact]
    public void The_last_minute_climbs_to_full_and_fastest_at_the_end()
    {
        Assert.True(RunIntensity.IsFinalStretch(TimeSpan.FromSeconds(59), TenMinutes));
        var at45 = RunIntensity.At(TimeSpan.FromSeconds(45), TenMinutes);
        var at30 = RunIntensity.At(TimeSpan.FromSeconds(30), TenMinutes);
        var at15 = RunIntensity.At(TimeSpan.FromSeconds(15), TenMinutes);
        Assert.True(RunIntensity.Idle < at45 && at45 < at30 && at30 < at15 && at15 < 1);
        Assert.True(at15 - at30 > at30 - at45, "ease-in: later seconds climb more");
        Assert.Equal(1, RunIntensity.At(TimeSpan.Zero, TenMinutes));
        Assert.False(RunIntensity.IsFinalStretch(TimeSpan.Zero, TenMinutes));
    }

    [Fact]
    public void A_short_run_ramps_through_its_last_quarter()
    {
        var two = TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.FromSeconds(30), RunIntensity.FinalStretch(two));
        Assert.Equal(RunIntensity.Idle, RunIntensity.At(TimeSpan.FromSeconds(31), two));
        Assert.True(RunIntensity.At(TimeSpan.FromSeconds(10), two) > RunIntensity.Idle);
    }

    [Fact]
    public void The_heartbeat_beats_all_run_climbing_slowly_then_racing_at_the_end()
    {
        Assert.Equal(RunIntensity.RestingBpm, RunIntensity.PulseBpm(0, RunIntensity.Idle));
        var halfway = RunIntensity.PulseBpm(0.5, RunIntensity.Idle);
        Assert.InRange(halfway, RunIntensity.RestingBpm + 1, RunIntensity.StretchStartBpm - 1);
        Assert.Equal(RunIntensity.StretchStartBpm, RunIntensity.PulseBpm(1, RunIntensity.Idle));
        Assert.Equal(RunIntensity.FinalBpm, RunIntensity.PulseBpm(1, 1));
        Assert.True(RunIntensity.PulseBpm(0.95, 0.6) > RunIntensity.PulseBpm(0.9, RunIntensity.Idle));
    }

    [Fact]
    public void A_beat_is_a_lub_then_a_softer_dub_then_quiet()
    {
        var lub = RunIntensity.BeatEnvelope(0.03);
        var dub = RunIntensity.BeatEnvelope(0.03 + RunIntensity.DubDelaySeconds);
        var between = RunIntensity.BeatEnvelope(0.03 + RunIntensity.DubDelaySeconds / 2);
        var after = RunIntensity.BeatEnvelope(0.6);
        Assert.True(lub > dub && dub > between && between > after);
        Assert.True(after < 0.01);
    }
}

public class RunCombosTests
{
    [Theory]
    [InlineData(40, 55, 50)]
    [InlineData(90, 260, 250)] // a big paste only calls the highest
    [InlineData(999, 1000, 1000)]
    public void The_highest_threshold_crossed_is_called_out(int before, int after, int expected) =>
        Assert.Equal(expected, RunCombos.Crossed(before, after));

    [Theory]
    [InlineData(0, 49)]
    [InlineData(60, 99)]
    [InlineData(120, 80)] // falling back below a threshold is not a combo
    public void No_combo_without_crossing(int before, int after) => Assert.Null(RunCombos.Crossed(before, after));

    [Fact]
    public void Callouts_grow_with_the_count()
    {
        Assert.Equal("COMBO · 50 WORDS", RunCombos.Callout(50));
        Assert.Equal("ON FIRE · 500 WORDS", RunCombos.Callout(500));
        Assert.Equal("LEGENDARY · 2,000 WORDS", RunCombos.Callout(2000));
    }
}

public class RunLogbookTests
{
    private static RunRecord Run(string date, int minutes, int added, int cut = 0, bool completed = true, int hour = 9) =>
        new()
        {
            Id = $"{date}-{hour}",
            StartedAt = new DateTimeOffset(DateOnly.Parse(date).ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero),
            LocalDate = date,
            PlannedSeconds = minutes * 60,
            ActualSeconds = minutes * 60,
            Completed = completed,
            Workspace = "Book",
            WordsAdded = added,
            WordsCut = cut,
            WordsNet = added - cut,
        };

    // Wednesday
    private static readonly DateOnly Today = new(2026, 11, 18);

    [Fact]
    public void Totals_streaks_and_newest_first()
    {
        var summary = RunLogbook.Build(
        [
            Run("2026-11-16", 10, 100),
            Run("2026-11-17", 25, 300, 40, hour: 8),
            Run("2026-11-17", 15, 50, completed: false, hour: 20),
            Run("2026-11-01", 5, 20),
        ], Today);

        Assert.Equal(2, summary.CurrentStreak); // 16th and 17th; today has no run yet
        Assert.Equal(2, summary.BestStreak);
        Assert.Equal(4, summary.Totals.Runs);
        Assert.Equal(55, summary.Totals.Minutes);
        Assert.Equal(470, summary.Totals.WordsAdded);
        Assert.Equal(40, summary.Totals.WordsCut);
        Assert.Equal(430, summary.Totals.WordsNet);
        Assert.Equal(3, summary.Totals.Completed);
        Assert.Equal(3, summary.Totals.DaysWritten);
        Assert.Equal(
            RunScoring.Xp(100, 0, 600, true) + RunScoring.Xp(300, 40, 1500, true)
            + RunScoring.Xp(50, 0, 900, false) + RunScoring.Xp(20, 0, 300, true),
            summary.Totals.Xp);
        Assert.Equal(["2026-11-17-20", "2026-11-17-8", "2026-11-16-9", "2026-11-01-9"], summary.Runs.Select(r => r.Id));
    }

    [Fact]
    public void The_heatmap_is_twelve_Monday_first_weeks_ending_this_week()
    {
        var summary = RunLogbook.Build(
        [
            Run("2026-11-17", 10, 100),
            Run("2026-11-17", 25, 10, hour: 20), // 35 minutes that day
            Run("2026-11-16", 70, 900),
        ], Today);

        Assert.Equal(84, summary.Heatmap.Count);
        Assert.Equal(DayOfWeek.Monday, summary.Heatmap[0].Date.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 11, 22), summary.Heatmap[^1].Date); // this week's Sunday
        Assert.True(summary.Heatmap[^1].IsFuture);
        Assert.False(summary.Heatmap.Single(d => d.Date == Today).IsFuture);

        var tuesday = summary.Heatmap.Single(d => d.Date == new DateOnly(2026, 11, 17));
        Assert.Equal((2, 35, 110, 3), (tuesday.Runs, tuesday.Minutes, tuesday.NetWords, tuesday.Level));
        Assert.Equal(4, summary.Heatmap.Single(d => d.Date == new DateOnly(2026, 11, 16)).Level);
        Assert.Equal(0, summary.Heatmap.Single(d => d.Date == Today).Level);
    }

    [Fact]
    public void The_current_streak_days_are_marked()
    {
        var summary = RunLogbook.Build(
        [
            Run("2026-11-10", 10, 100), // an older streak, already broken
            Run("2026-11-15", 10, 100),
            Run("2026-11-16", 10, 100),
            Run("2026-11-17", 10, 100),
        ], Today);

        Assert.Equal(3, summary.CurrentStreak);
        Assert.Equal(
            [new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17)],
            summary.Heatmap.Where(d => d.InStreak).Select(d => d.Date));

        var broken = RunLogbook.Build([Run("2026-11-10", 10, 100)], Today);
        Assert.DoesNotContain(broken.Heatmap, d => d.InStreak);
    }

    [Fact]
    public void No_runs_is_an_empty_logbook()
    {
        var summary = RunLogbook.Build([], Today);
        Assert.Equal(0, summary.CurrentStreak);
        Assert.Equal(0, summary.Totals.Runs);
        Assert.Empty(summary.Runs);
        Assert.All(summary.Heatmap, d => Assert.Equal(0, d.Level));
    }
}

public class RunAmbienceBankTests
{
    private static readonly RunAmbienceBank Bank = RunAmbienceBank.Render();

    [Fact]
    public void The_loop_is_eight_seconds_and_has_no_seam()
    {
        Assert.Equal((int)(RunAmbienceBank.LoopSeconds * Bank.SampleRate), Bank.Bed.Length);

        // The jump from the last sample back to the first is no bigger than an ordinary step inside the loop.
        var seam = Math.Abs(Bank.Bed[0] - Bank.Bed[^1]);
        var steps = new List<float>();
        for (var i = 1; i < Bank.Bed.Length; i++)
            steps.Add(Math.Abs(Bank.Bed[i] - Bank.Bed[i - 1]));
        steps.Sort();
        var p99 = steps[(int)(steps.Count * 0.99)];
        Assert.True(seam <= p99, $"seam {seam} vs 99th percentile step {p99}");
    }

    [Fact]
    public void Every_sound_is_in_range_and_not_silent()
    {
        foreach (var clip in new[] { Bank.Bed, Bank.Heartbeat, Bank.Chime })
        {
            Assert.All(clip, s => Assert.InRange(s, -1f, 1f));
            Assert.True(clip.Max(Math.Abs) > 0.3f);
        }
    }

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("bed")]
    public void Small_speakers_can_play_it(string clip)
    {
        // Laptop speakers drop almost everything under ~120 Hz; a heartbeat made only of 50–60 Hz thumps is silent
        // on them. Enough of each sound must survive a 120 Hz high-pass.
        var samples = clip == "heartbeat" ? Bank.Heartbeat : Bank.Bed;
        var filtered = HighPass(HighPass(samples, 120, Bank.SampleRate), 120, Bank.SampleRate);
        var ratio = Rms(filtered) / Rms(samples);
        Assert.True(ratio > 0.45, $"{clip}: only {ratio:P0} of its level is above 120 Hz");
    }

    private static float[] HighPass(float[] x, double cutoff, int rate)
    {
        var rc = 1 / (2 * Math.PI * cutoff);
        var a = rc / (rc + 1.0 / rate);
        var y = new float[x.Length];
        for (var i = 1; i < x.Length; i++)
            y[i] = (float)(a * (y[i - 1] + x[i] - x[i - 1]));
        return y;
    }

    private static double Rms(float[] x) => Math.Sqrt(x.Sum(s => (double)s * s) / x.Length);

    [Fact]
    public void Renders_the_same_every_time()
    {
        var again = RunAmbienceBank.Render();
        Assert.Equal(Bank.Bed, again.Bed);
        Assert.Equal(Bank.Chime, again.Chime);
    }
}

public class RunAtmosphereSettingsTests
{
    [Fact]
    public void Everything_is_on_by_default_and_volume_is_kept_in_range()
    {
        var defaults = new RunAtmosphereSettings();
        Assert.True(defaults.Flames && defaults.EdgeGlow && defaults.Sparks && defaults.Ambience);
        Assert.Equal(1, (defaults with { AmbienceVolume = 7 }).Normalized().AmbienceVolume);
        Assert.Equal(RunAtmosphereSettings.DefaultAmbienceVolume,
            (defaults with { AmbienceVolume = double.NaN }).Normalized().AmbienceVolume);
    }
}

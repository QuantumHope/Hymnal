using Hymnal.Core.Services;
using Hymnal.Core.Services.Runs;

namespace Hymnal.Core.Tests.Services.Runs;

public class RunTrackerIntervalTests
{
    [Fact]
    public void Each_interval_counts_what_changed_since_the_last()
    {
        var tracker = new RunTracker(new WordCountService());
        tracker.Observe("a.md", "A", "one two");
        tracker.Observe("a.md", "A", "one two three four");
        Assert.Equal(new WordDelta(2, 0), tracker.TakeInterval());

        tracker.Observe("b.md", "B", "alpha beta");
        tracker.Observe("b.md", "B", "alpha");
        tracker.Observe("a.md", "A", "one two three four five");
        Assert.Equal(new WordDelta(1, 1), tracker.TakeInterval());

        Assert.Equal(new WordDelta(0, 0), tracker.TakeInterval()); // nothing since

        tracker.Observe("a.md", "A", "one two three four five six");
        tracker.Observe("a.md", "A", "one two three four five"); // typed and deleted inside one interval
        Assert.Equal(new WordDelta(0, 0), tracker.TakeInterval());

        // The run's overall diff is unaffected by taking intervals.
        Assert.Equal(new WordDelta(3, 1), tracker.Summarize().Total);
    }
}

public class WordDiffTests
{
    [Fact]
    public void Identical_texts_have_no_change()
    {
        var d = WordDiff.Compute("one two three", "one two three");
        Assert.Equal(new WordDelta(0, 0), d);
    }

    [Fact]
    public void Appending_counts_only_added_words()
    {
        var d = WordDiff.Compute("The night was dark.", "The night was dark. Rain fell hard.");
        Assert.Equal(3, d.Added);
        Assert.Equal(0, d.Cut);
    }

    [Fact]
    public void Deleting_counts_only_cut_words()
    {
        var d = WordDiff.Compute("a b c d e", "a e");
        Assert.Equal(0, d.Added);
        Assert.Equal(3, d.Cut);
        Assert.Equal(-3, d.Net);
    }

    [Fact]
    public void Replacing_a_word_counts_one_added_and_one_cut()
    {
        var d = WordDiff.Compute("she walked home slowly", "she ran home slowly");
        Assert.Equal(new WordDelta(1, 1), d);
        Assert.Equal(2, d.Touched);
    }

    [Fact]
    public void Edits_in_the_middle_keep_shared_words()
    {
        var d = WordDiff.Compute("a b c d e f", "a b X c d Y f");
        Assert.Equal(2, d.Added); // X, Y
        Assert.Equal(1, d.Cut);   // e
    }

    [Fact]
    public void Markua_attribute_lines_are_not_words()
    {
        var d = WordDiff.Compute("{class: part}\nHello", "{class: chapter, id: x}\nHello world");
        Assert.Equal(new WordDelta(1, 0), d);
    }

    [Fact]
    public void Net_always_matches_the_change_in_word_count()
    {
        var counter = new WordCountService();
        var before = "It was a bright cold day in April, and the clocks were striking thirteen.";
        var after = "It was a cold day in April. The clocks were striking thirteen, again and again.";
        var d = WordDiff.Compute(before, after);
        Assert.Equal(counter.CountWords(after) - counter.CountWords(before), d.Net);
    }

    [Fact]
    public void Huge_middles_fall_back_to_the_multiset_with_an_exact_net()
    {
        // Distinct first and last words defeat prefix/suffix trimming; 2,100 x 2,100 > 4M cells.
        var before = "START " + string.Join(' ', Enumerable.Range(0, 2100).Select(i => $"w{i}")) + " END1";
        var after = "BEGIN " + string.Join(' ', Enumerable.Range(0, 2100).Select(i => $"w{2099 - i}")) + " extra END2";
        var d = WordDiff.Compute(before, after);
        Assert.Equal(3, d.Added);  // BEGIN, extra, END2 (reversal is not counted)
        Assert.Equal(2, d.Cut);    // START, END1
        Assert.Equal(1, d.Net);
    }
}

public class RunTrackerTests
{
    private static RunTracker NewTracker() => new(new WordCountService());

    [Fact]
    public void First_observation_is_the_baseline_and_later_ones_drive_live_net()
    {
        var t = NewTracker();
        t.Observe("ch1.md", "One", "alpha beta");
        Assert.Equal(0, t.LiveNet);

        t.Observe("ch1.md", "One", "alpha beta gamma delta");
        Assert.Equal(2, t.LiveNet);

        t.Observe("ch1.md", "One", "alpha");
        Assert.Equal(-1, t.LiveNet);
    }

    [Fact]
    public void Several_chapters_are_summed_and_unchanged_ones_are_left_out()
    {
        var t = NewTracker();
        t.Observe("a.md", "A", "one two");
        t.Observe("b.md", "B", "three four five");
        t.Observe("c.md", "C", "untouched");
        t.Observe("a.md", "A", "one two six seven");
        t.Observe("b.md", "B", "three five");

        Assert.Equal(1, t.LiveNet);
        var s = t.Summarize();
        Assert.Equal(["a.md", "b.md"], s.Chapters.Select(c => c.Key));
        Assert.Equal(new WordDelta(2, 1), s.Total);
    }

    [Fact]
    public void Returning_to_a_chapter_keeps_its_first_baseline()
    {
        var t = NewTracker();
        t.Observe("a.md", "A", "one");
        t.Observe("a.md", "A", "one two");
        t.Observe("b.md", "B", "x");
        t.Observe("a.md", "A", "one two"); // reopened from disk after a save
        t.Observe("a.md", "A", "one two three");

        Assert.Equal(new WordDelta(2, 0), t.Summarize().Total);
    }

    [Theory]
    [InlineData("First line.\nSecond line.\n\n", "Second line.")]
    [InlineData("Only\n{class: part}\n", "Only")]
    [InlineData("   \n", null)]
    public void LastLine_skips_blank_and_attribute_lines(string text, string? expected)
    {
        Assert.Equal(expected, RunTracker.LastLine(text));
    }
}

public class RunScoringTests
{
    [Fact]
    public void Xp_counts_added_half_of_cut_minutes_and_the_completion_bonus()
    {
        Assert.Equal(300 + 5 + 5 * 15 + 25, RunScoring.Xp(300, 9, 15 * 60 + 30, completed: true));
        Assert.Equal(0 + 0 + 5 * 2, RunScoring.Xp(0, 0, 150, completed: false));
    }

    [Theory]
    [InlineData(100, 5, ChurnKind.FreshDrafting)]
    [InlineData(100, 50, ChurnKind.DraftingAndShaping)]
    [InlineData(10, 90, ChurnKind.DeepRevision)]
    [InlineData(0, 0, ChurnKind.None)]
    public void Churn_is_classified_by_the_share_of_words_cut(int added, int cut, ChurnKind expected)
    {
        Assert.Equal(expected, RunScoring.Churn(new WordDelta(added, cut)));
    }

    [Theory]
    [InlineData(20, 30, 25)]
    [InlineData(5, 80, 75)]
    [InlineData(95, 101, 100)]
    [InlineData(30, 40, null)]
    [InlineData(50, 50, null)]
    public void MilestoneCrossed_returns_the_highest_crossed(double before, double after, int? expected)
    {
        Assert.Equal(expected, RunScoring.MilestoneCrossed(before, after));
    }
}

public class StreakCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    private static DateOnly D(int daysAgo) => Today.AddDays(-daysAgo);

    [Fact]
    public void First_run_ever_starts_a_streak()
    {
        var o = StreakCalculator.Apply([], Today);
        Assert.Equal(new StreakOutcome(StreakChange.Started, 1, 0, 1), o);
    }

    [Fact]
    public void A_run_after_yesterday_extends_the_streak()
    {
        var o = StreakCalculator.Apply([D(1), D(2), D(3)], Today);
        Assert.Equal(StreakChange.Extended, o.Change);
        Assert.Equal(4, o.Current);
        Assert.Equal(4, o.Best);
    }

    [Fact]
    public void A_second_run_today_keeps_the_streak()
    {
        var o = StreakCalculator.Apply([D(0), D(1)], Today);
        Assert.Equal(StreakChange.Kept, o.Change);
        Assert.Equal(2, o.Current);
    }

    [Fact]
    public void A_lapsed_streak_restarts_and_reports_the_old_length_and_best()
    {
        var o = StreakCalculator.Apply([D(3), D(4), D(5), D(10), D(11), D(12), D(13), D(14)], Today);
        Assert.Equal(StreakChange.Restarted, o.Change);
        Assert.Equal(1, o.Current);
        Assert.Equal(3, o.Previous);
        Assert.Equal(5, o.Best);
        Assert.Equal("New streak started — your last one was 3 days", o.Describe());
    }

    [Fact]
    public void Current_counts_from_yesterday_until_today_has_a_run()
    {
        Assert.Equal(2, StreakCalculator.Current([D(1), D(2), D(4)], Today));
        Assert.Equal(0, StreakCalculator.Current([D(2)], Today));
    }
}

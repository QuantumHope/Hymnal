using Hymnal.Core.Search;

namespace Hymnal.Core.Tests.Search;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("", "Anything")]
    [InlineData("   ", "Anything")]
    public void A_blank_query_matches_everything_with_no_score(string query, string label) =>
        Assert.Equal(0, FuzzyMatcher.Score(query, label));

    [Theory]
    [InlineData("focus", "Focus Mode")]
    [InlineData("FOCUS", "focus mode")]
    [InlineData("fm", "Focus Mode")]
    [InlineData("tls", "Toggle Left Sidebar")]
    [InlineData("sidebar left", "Toggle Left Sidebar")]
    public void Characters_in_order_match_ignoring_case_and_word_order(string query, string label) =>
        Assert.NotNull(FuzzyMatcher.Score(query, label));

    [Theory]
    [InlineData("xyz", "Focus Mode")]
    [InlineData("edomsucof", "Focus Mode")]
    [InlineData("focus zebra", "Focus Mode")]
    public void A_missing_or_out_of_order_character_does_not_match(string query, string label) =>
        Assert.Null(FuzzyMatcher.Score(query, label));

    [Fact]
    public void Initials_score_above_scattered_letters()
    {
        var initials = FuzzyMatcher.Score("tn", "Toggle Notes");
        var scattered = FuzzyMatcher.Score("tn", "Settings");
        Assert.NotNull(initials);
        Assert.NotNull(scattered);
        Assert.True(initials > scattered, $"{initials} vs {scattered}");
    }

    [Fact]
    public void A_prefix_scores_above_the_same_letters_later_in_the_label()
    {
        var prefix = FuzzyMatcher.Score("save", "Save");
        var later = FuzzyMatcher.Score("save", "Reset Saved Layout");
        Assert.True(prefix > later, $"{prefix} vs {later}");
    }

    [Fact]
    public void A_later_word_start_is_preferred_to_the_first_occurrence()
    {
        // The first 's' in "Toggle Sidebar" is inside no word, so the matcher should take the word start.
        var wordStart = FuzzyMatcher.Score("ts", "Toggle Sidebar");
        var inside = FuzzyMatcher.Score("ts", "Tossed");
        Assert.True(wordStart > inside, $"{wordStart} vs {inside}");
    }

    [Fact]
    public void Best_score_weights_the_title_above_details()
    {
        var inTitle = FuzzyMatcher.BestScore("harbour", "The Harbour", "Scene", "part-one/harbour.md");
        var inDetail = FuzzyMatcher.BestScore("harbour", "Arrival", "Scene", "part-one/harbour.md");
        Assert.NotNull(inDetail);
        Assert.True(inTitle > inDetail, $"{inTitle} vs {inDetail}");
    }

    [Fact]
    public void Best_score_lets_each_word_match_a_different_label()
    {
        Assert.NotNull(FuzzyMatcher.BestScore("arrival harbour", "Arrival", "Scene", "harbour.md"));
        Assert.Null(FuzzyMatcher.BestScore("arrival lighthouse", "Arrival", "Scene", "harbour.md"));
    }

    [Fact]
    public void Best_score_skips_null_details()
    {
        Assert.NotNull(FuzzyMatcher.BestScore("idea", "Untitled", "Idea", null));
        Assert.Null(FuzzyMatcher.BestScore("premise", "Untitled", "Idea", null));
    }
}

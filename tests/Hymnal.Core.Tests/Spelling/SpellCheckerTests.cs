using Hymnal.Core.Spelling;

namespace Hymnal.Core.Tests.Spelling;

public sealed class SpellCheckerTests
{
    internal static string FixtureFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dictionaries");

    /// <summary>The hand-written test dictionary (Fixtures/Dictionaries/xx-TEST).</summary>
    internal static IWordChecker TestDictionary()
    {
        using var dic = File.OpenRead(Path.Combine(FixtureFolder, "xx-TEST.dic"));
        using var aff = File.OpenRead(Path.Combine(FixtureFolder, "xx-TEST.aff"));
        return HunspellWordChecker.FromStreams(dic, aff);
    }

    [Fact]
    public void Finds_misspelled_words_with_offsets()
    {
        var checker = new SpellChecker(TestDictionary());

        var issues = checker.FindIssues("The quikc brown fox jumsp over the lazy dog.");

        Assert.Equal(["quikc", "jumsp"], issues.Select(i => i.Word));
        Assert.Equal(4, issues[0].Start);
        Assert.Equal(5, issues[0].Length);
        Assert.Equal(20, issues[1].Start);
    }

    [Fact]
    public void Accepts_affixed_forms_and_sentence_capitals()
    {
        var checker = new SpellChecker(TestDictionary());

        Assert.Empty(checker.FindIssues("Cats and dogs. The cat's mat. Don’t."));
    }

    [Fact]
    public void Ignores_markup_that_is_not_prose()
    {
        var checker = new SpellChecker(TestDictionary());

        Assert.Empty(checker.FindIssues("The `qwzx` cat {class: qwzx}\n```\nqwzx\n```\n[link](https://qwzx.example)"));
    }

    [Fact]
    public void Project_words_are_accepted()
    {
        var checker = new SpellChecker(TestDictionary(), new WordAllowList(["Elara", "wyvern"]));

        Assert.Empty(checker.FindIssues("Elara and the wyvern. Wyvern and WYVERN. Elara's cat."));
        Assert.Equal(["Wyverns"], checker.FindIssues("Wyverns").Select(i => i.Word));
    }

    [Fact]
    public void Suggestions_put_the_likely_fix_first_and_keep_curly_apostrophes()
    {
        var checker = new SpellChecker(TestDictionary());

        Assert.Contains("quick", checker.Suggest("quikc"));
        Assert.Contains("don’t", checker.Suggest("dont’t"));
        Assert.DoesNotContain("quikc", checker.Suggest("quikc"));
        Assert.True(checker.Suggest("quikc", max: 1).Count <= 1);
    }

    [Fact]
    public void Honors_cancellation()
    {
        var checker = new SpellChecker(TestDictionary());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => checker.FindIssues("the cat", cts.Token));
    }

    [Theory]
    [InlineData("wyvern", true)]
    [InlineData("Wyvern", true)]
    [InlineData("WYVERN", true)]
    [InlineData("Elara", true)]
    [InlineData("ELARA", true)]
    [InlineData("elara", false)]
    [InlineData("Elara's", true)]
    [InlineData("Elara'", true)]
    [InlineData("Elaras", false)]
    [InlineData("don't", true)]
    public void Allow_list_follows_personal_dictionary_case_rules(string word, bool accepted)
    {
        // "don’t" is entered with a curly apostrophe and matched in check form.
        var allowed = new WordAllowList(["wyvern", "Elara", "don’t"]);

        Assert.Equal(accepted, allowed.Contains(word));
    }

    [Fact]
    public void Empty_allow_list_accepts_nothing()
    {
        Assert.True(WordAllowList.Empty.IsEmpty);
        Assert.False(WordAllowList.Empty.Contains("anything"));
        Assert.True(new WordAllowList(["  ", ""]).IsEmpty);
    }
}

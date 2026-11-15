using Hymnal.Core.Spelling;

namespace Hymnal.Core.Tests.Spelling;

public sealed class ProseWordScannerTests
{
    private static List<string> Words(string text) => ProseWordScanner.Scan(text).Select(w => w.Text).ToList();

    [Fact]
    public void Finds_words_with_their_offsets()
    {
        var words = ProseWordScanner.Scan("The cat sat.\nOn the mat");

        Assert.Equal(["The", "cat", "sat", "On", "the", "mat"], words.Select(w => w.Text));
        Assert.Equal(4, words[1].Start);
        Assert.Equal(3, words[1].Length);
        Assert.Equal(13, words[3].Start);
    }

    [Fact]
    public void Keeps_apostrophes_inside_words_but_not_quotes_around_them()
    {
        Assert.Equal(["don't", "o’clock", "quoted"], Words("don't o’clock 'quoted'"));
    }

    [Fact]
    public void Check_form_straightens_typographic_apostrophes()
    {
        var word = ProseWordScanner.Scan("don’t")[0];

        Assert.Equal("don’t", word.Text);
        Assert.Equal("don't", word.CheckForm);
    }

    [Fact]
    public void Splits_hyphenated_words_and_ignores_markup_punctuation()
    {
        Assert.Equal(["well", "known", "bold", "italic"], Words("well-known **bold** _italic_"));
    }

    [Theory]
    [InlineData("NASA")]
    [InlineData("3rd")]
    [InlineData("B2B")]
    [InlineData("iPhone")]
    [InlineData("camelCase")]
    [InlineData("I")]
    [InlineData("a")]
    public void Skips_words_that_are_rarely_misspellings(string word)
    {
        Assert.Empty(Words(word));
    }

    [Fact]
    public void Checks_capitalized_names()
    {
        Assert.Equal(["McAllister", "Elara"], Words("McAllister Elara"));
    }

    [Fact]
    public void Skips_fenced_code_blocks()
    {
        var text = "before\n```csharp\nvar zzqq = 1;\n```\nafter\n~~~\nxxyy\n~~~\nend";

        Assert.Equal(["before", "after", "end"], Words(text));
    }

    [Fact]
    public void An_unclosed_fence_runs_to_the_end()
    {
        Assert.Equal(["before"], Words("before\n```\ncode qwzx"));
    }

    [Fact]
    public void Skips_inline_code_and_attribute_blocks()
    {
        Assert.Equal(["Use", "now", "Heading"], Words("Use `fooBarBaz qwzx` now\n{class: part, idx: zzz}\n# Heading {#chap-one}"));
    }

    [Fact]
    public void Skips_comments()
    {
        var text = "one %% inline is prose\n%% whole line qwzx\ntwo <!-- hidden\nqwzx --> three";

        Assert.Equal(["one", "inline", "is", "prose", "two", "three"], Words(text));
    }

    [Fact]
    public void Skips_urls_emails_and_link_targets_but_checks_link_text()
    {
        var text = "See [the docs](https://exmaple.com/pathh) or www.qwzx.org, mail me@qwzx.com";

        Assert.Equal(["See", "the", "docs", "or", "mail"], Words(text));
    }

    [Fact]
    public void Skips_tags_footnote_markers_and_callout_prefixes()
    {
        Assert.Equal(["word", "Callout", "text"], Words("<span class=\"qwzx\">word</span>[^qwzx]\nA> Callout text"));
    }

    [Fact]
    public void Empty_or_null_text_has_no_words()
    {
        Assert.Empty(ProseWordScanner.Scan(null));
        Assert.Empty(ProseWordScanner.Scan(""));
    }

    [Fact]
    public void Honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ProseWordScanner.Scan("some words", cts.Token));
    }
}

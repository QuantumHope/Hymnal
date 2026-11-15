using Hymnal.Core.Markua;
using Hymnal.Core.Markua.Editing;

namespace Hymnal.Core.Tests.Markua;

/// <summary>
/// The Insert menu's edits. Cases are written as before → after, with ‸ for the caret and «…» for the selection,
/// in both the input and the expected result.
/// </summary>
public class MarkuaEditsTests
{
    private const char Caret = '‸';
    private const char SelStart = '«';
    private const char SelEnd = '»';

    private static (string Text, TextSpan Selection) Parse(string marked)
    {
        var caret = marked.IndexOf(Caret);
        if (caret >= 0)
            return (marked.Remove(caret, 1), TextSpan.Caret(caret));
        var start = marked.IndexOf(SelStart);
        var end = marked.IndexOf(SelEnd);
        Assert.True(start >= 0 && end > start, $"no caret or selection marked in \"{marked}\"");
        return (marked.Remove(end, 1).Remove(start, 1), new TextSpan(start, end - start - 1));
    }

    private static string Mark(string text, TextSpan selection) =>
        selection.IsEmpty
            ? text.Insert(selection.Start, Caret.ToString())
            : text.Insert(selection.End, SelEnd.ToString()).Insert(selection.Start, SelStart.ToString());

    private static string Apply(string marked, Func<string, TextSpan, TextEdit> edit)
    {
        var (text, selection) = Parse(marked);
        var result = edit(text, selection);
        return Mark(result.ApplyTo(text), result.Selection);
    }

    private static string Snippet(string id, string marked) => Apply(marked, MarkuaSnippets.Get(id).Apply);

    // ── Inline styles ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a «word» here", "a **«word»** here")]
    [InlineData("a **«word»** here", "a «word» here")] // selection just inside the markers
    [InlineData("a «**word**» here", "a «word» here")] // selection including them
    [InlineData("a« word »here", "a **«word»** here")] // spaces stay outside
    [InlineData("a wo‸rd here", "a **«word»** here")] // caret inside a word
    [InlineData("a word‸", "a word**‸**")] // caret at a word's end: start typing bold
    [InlineData("‸", "**‸**")]
    [InlineData("**‸**", "‸")] // a second press takes an empty pair out
    [InlineData("don‸'t", "**«don't»**")]
    public void Bold_wraps_and_unwraps(string before, string after) =>
        Assert.Equal(after, Snippet("bold", before));

    [Theory]
    [InlineData("a «word»", "a *«word»*")]
    [InlineData("a *«word»*", "a «word»")]
    [InlineData("a **«word»**", "a ***«word»***")] // bold isn't italic, so it gains italic
    [InlineData("a ***«word»***", "a **«word»**")] // bold italic loses only the italic
    [InlineData("a «**word**»", "a *«**word**»*")]
    [InlineData("**‸**", "***‸***")] // italic in an empty bold pair adds to it
    [InlineData("***‸***", "**‸**")]
    public void Italic_tells_its_star_from_bold(string before, string after) =>
        Assert.Equal(after, Snippet("italic", before));

    [Theory]
    [InlineData("strikethrough", "«gone»", "~~«gone»~~")]
    [InlineData("code", "call «Run()» now", "call `«Run()»` now")]
    [InlineData("superscript", "x«2»", "x^«2»^")]
    [InlineData("subscript", "H«2»O", "H~«2»~O")]
    [InlineData("subscript", "~~«gone»~~", "~~~«gone»~~~")] // strikethrough isn't subscript
    [InlineData("subscript", "~~‸~~", "~~~‸~~~")]
    [InlineData("bold", "***‸***", "*‸*")]
    public void Other_inline_styles(string id, string before, string after) =>
        Assert.Equal(after, Snippet(id, before));

    [Fact]
    public void Inline_style_over_several_lines_styles_each_line_and_skips_list_markers()
    {
        Assert.Equal("«- **one**\n\n- **two**»", Snippet("bold", "«- one\n\n- two»"));
        Assert.Equal("«- one\n\n- two»", Snippet("bold", "«- **one**\n\n- **two**»"));
        Assert.Equal("«**one**\r\n**two**»", Snippet("bold", "«one\r\ntwo»"));

        // Markers just outside the selection count, so this unwraps rather than doubling them.
        Assert.Equal("«one\ntwo»", Snippet("bold", "**«one**\n**two»**"));
        // A line already styled stays as it is while the others gain the style.
        Assert.Equal("«**one**\n**two**»", Snippet("bold", "«**one**\ntwo»"));
    }

    [Fact]
    public void Inline_style_on_a_whole_heading_line_styles_the_words()
    {
        Assert.Equal("## *«Title»*", Snippet("italic", "«## Title»"));
    }

    // ── Links and footnotes ─────────────────────────────────────────────────

    [Theory]
    [InlineData("see «the docs» here", "see [the docs](«https://») here")]
    [InlineData("see «https://leanpub.com» here", "see [«link text»](https://leanpub.com) here")]
    [InlineData("see ‸", "see [«link text»](https://)")]
    [InlineData("read «this »today", "read [this](«https://») today")] // spaces stay outside
    [InlineData("read« https://leanpub.com »now", "read [«link text»](https://leanpub.com) now")]
    public void Link_uses_the_selection_as_text_or_as_the_url(string before, string after) =>
        Assert.Equal(after, Snippet("link", before));

    [Fact]
    public void Footnote_numbers_on_and_puts_its_note_under_the_paragraph()
    {
        Assert.Equal(
            "One claim.[^1]\nStill the paragraph.\n\n[^1]: «Footnote text.»\n\nNext paragraph.",
            Snippet("footnote", "One claim.‸\nStill the paragraph.\n\nNext paragraph."));

        Assert.Equal(
            "A[^2] and B[^3]\n\n[^3]: «Footnote text.»",
            Snippet("footnote", "A[^2] and «B»"));
    }

    // ── Paragraph styles ────────────────────────────────────────────────────

    [Theory]
    [InlineData("heading-1", "Arri‸val", "# Arri‸val")]
    [InlineData("heading-1", "# Arri‸val", "Arri‸val")] // toggles off
    [InlineData("heading-2", "# Arri‸val", "## Arri‸val")] // changes level
    [InlineData("heading-none", "### Arri‸val", "Arri‸val")]
    [InlineData("heading-none", "Arri‸val", "Arri‸val")]
    [InlineData("heading-1", "‸", "# ‸")]
    [InlineData("heading-3", "a\n«b\nc»", "a\n«### b\n### c»")]
    public void Headings(string id, string before, string after) =>
        Assert.Equal(after, Snippet(id, before));

    [Theory]
    [InlineData("bulleted-list", "«one\ntwo»", "«- one\n- two»")]
    [InlineData("bulleted-list", "«- one\n- two»", "«one\ntwo»")]
    [InlineData("numbered-list", "«one\n\ntwo\nthree»", "«1. one\n\n2. two\n3. three»")]
    [InlineData("numbered-list", "«- one\n  - two»", "«1. one\n  2. two»")] // keeps indentation
    [InlineData("bulleted-list", "«1. one\n2. two»", "«- one\n- two»")]
    [InlineData("bulleted-list", "o‸ne", "- o‸ne")]
    public void Lists(string id, string before, string after) =>
        Assert.Equal(after, Snippet(id, before));

    [Theory]
    [InlineData("blockquote", "«one\n\ntwo»", "«> one\n>\n> two»")]
    [InlineData("blockquote", "«> one\n>\n> two»", "«one\n\ntwo»")]
    [InlineData("callout-w", "Care‸ful", "W> Care‸ful")]
    [InlineData("callout-t", "W> Care‸ful", "T> Care‸ful")] // switches kind
    [InlineData("callout-a", "«one\n\ntwo»", "«A> one\nA>\nA> two»")]
    [InlineData("callout-a", "«A> one\nA>\nA> two»", "«one\n\ntwo»")]
    [InlineData("comment", "«one\n\ntwo»", "«%% one\n\n%% two»")]
    [InlineData("comment", "%% n‸ote", "n‸ote")]
    public void Quotes_callouts_and_comments(string id, string before, string after) =>
        Assert.Equal(after, Snippet(id, before));

    [Fact]
    public void A_selection_ending_at_a_line_start_leaves_that_line_alone()
    {
        Assert.Equal("«> one»\ntwo", Snippet("blockquote", "«one\n»two"));
    }

    [Fact]
    public void Line_styles_keep_CRLF()
    {
        // A selection ending at the next line's start ends before the whole CRLF, not between its halves.
        Assert.Equal("One.\r\n\r\n* * *‸\r\n\r\nTwo.", Snippet("scene-break", "«One.\r\n»Two."));
        Assert.Equal("```\r\n«One.»\r\n```\r\n\r\nTwo.", Snippet("code-block", "«One.\r\n»Two."));
        Assert.Equal("«- one\r\n- two»\r\nthree", Snippet("bulleted-list", "«one\r\ntwo»\r\nthree"));
    }

    // ── Blocks ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("End of a scene.‸", "End of a scene.\n\n* * *\n\n‸")]
    [InlineData("One.‸\n\nTwo.", "One.\n\n* * *‸\n\nTwo.")]
    [InlineData("One.\n‸\nTwo.", "One.\n\n* * *‸\n\nTwo.")] // on a blank line
    [InlineData("‸", "* * *\n\n‸")]
    [InlineData("Mid‸dle of a line.", "Middle of a line.\n\n* * *\n\n‸")]
    public void Scene_break_goes_on_its_own_paragraph(string before, string after) =>
        Assert.Equal(after, Snippet("scene-break", before));

    [Fact]
    public void Page_break_and_structure_markers_are_blocks()
    {
        Assert.Equal("One.\r\n\r\n{pagebreak}‸\r\n\r\nTwo.", Snippet("page-break", "One.‸\r\n\r\nTwo."));
        Assert.Equal("{frontmatter}\n\n‸", Snippet("frontmatter", "‸"));
        Assert.Equal("Intro.\n\n{class: part}\n# «Part Title»", Snippet("part", "Intro.‸"));
    }

    [Fact]
    public void Table_and_image_select_what_to_type_first()
    {
        Assert.Equal("| «Header» | Header |\n| ------ | ------ |\n| Cell   | Cell   |", Snippet("table", "‸"));
        Assert.Equal("Text.\n\n![Caption](«image.png»)\n\nMore.", Snippet("image", "Text.‸\n\nMore."));
    }

    [Fact]
    public void Code_block_wraps_the_selected_lines_or_offers_a_placeholder()
    {
        Assert.Equal("Run:\n\n```\n«dotnet build\ndotnet test»\n```\n\nDone.",
            Snippet("code-block", "Run:\ndotnet «build\ndotnet» test\nDone."));
        Assert.Equal("```\n«code»\n```", Snippet("code-block", "‸"));
    }

    // ── The catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ids_and_shortcuts_are_unique()
    {
        var all = MarkuaSnippets.All;
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
        var gestures = all.Where(s => s.Shortcut != null).Select(s => s.Shortcut!.Gesture).ToList();
        Assert.Equal(gestures.Count, gestures.Distinct().Count());
        Assert.All(all, s => Assert.DoesNotContain('_', s.Title)); // an underscore would become a menu access key
    }

    [Fact]
    public void Every_snippet_applies_cleanly_to_empty_text_and_a_selection()
    {
        foreach (var snippet in MarkuaSnippets.All)
        {
            foreach (var (text, selection) in new[] { ("", TextSpan.Caret(0)), ("One two.\nThree.", new TextSpan(4, 3)) })
            {
                var edit = snippet.Apply(text, selection);
                var result = edit.ApplyTo(text);
                Assert.InRange(edit.Selection.Start, 0, result.Length);
                Assert.InRange(edit.Selection.End, 0, result.Length);
            }
        }
    }

    [Theory]
    [InlineData("bold", MarkuaTextStyle.Bold)]
    [InlineData("italic", MarkuaTextStyle.Italic)]
    [InlineData("strikethrough", MarkuaTextStyle.Strikethrough)]
    [InlineData("superscript", MarkuaTextStyle.Superscript)]
    [InlineData("subscript", MarkuaTextStyle.Subscript)]
    public void What_the_menu_writes_is_what_the_preview_reads(string id, MarkuaTextStyle style)
    {
        var text = MarkuaSnippets.Get(id).Apply("say word", new TextSpan(4, 4)).ApplyTo("say word");
        var paragraph = Assert.IsType<MarkuaParagraph>(Assert.Single(MarkuaParser.Parse(text).Blocks));
        Assert.Contains(paragraph.Runs, run => run.Text == "word" && run.Style.HasFlag(style));
    }
}

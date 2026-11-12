using Hymnal.Core.Markua;

namespace Hymnal.Core.Tests.Markua;

public class MarkuaParserTests
{
    private static string Text(IEnumerable<MarkuaRun> runs) =>
        string.Concat(runs.Select(r => r.IsLineBreak ? "\n" : r.Text));

    private static T Single<T>(MarkuaDocument doc) where T : MarkuaBlock => Assert.IsType<T>(Assert.Single(doc.Blocks));

    // ── Document basics ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Parse_EmptyInput_ReturnsEmptyDocument(string? source)
    {
        var doc = MarkuaParser.Parse(source);
        Assert.Empty(doc.Blocks);
        Assert.Empty(doc.Footnotes);
    }

    [Fact]
    public void Parse_SingleNewlineInParagraph_IsForcedLineBreak()
    {
        var paragraph = Single<MarkuaParagraph>(MarkuaParser.Parse("Roses are red,\nviolets are blue."));

        Assert.Equal("Roses are red,\nviolets are blue.", Text(paragraph.Runs));
        Assert.Single(paragraph.Runs, r => r.IsLineBreak);
    }

    [Fact]
    public void Parse_BlankLine_SeparatesParagraphs_AndCrLfIsHandled()
    {
        var doc = MarkuaParser.Parse("One.\r\n\r\n\r\nTwo.\r\n");

        Assert.Equal(2, doc.Blocks.Count);
        Assert.All(doc.Blocks, b => Assert.IsType<MarkuaParagraph>(b));
        Assert.Equal("Two.", Text(((MarkuaParagraph)doc.Blocks[1]).Runs));
    }

    // ── Headings and parts ───────────────────────────────────────────────────

    [Theory]
    [InlineData("# Chapter One", 1, "Chapter One")]
    [InlineData("## A Section ##", 2, "A Section")]
    [InlineData("###### Deep", 6, "Deep")]
    public void Parse_AtxHeadings(string source, int level, string text)
    {
        var heading = Single<MarkuaHeading>(MarkuaParser.Parse(source));
        Assert.Equal(level, heading.Level);
        Assert.Equal(text, Text(heading.Runs));
        Assert.False(heading.IsPart);
    }

    [Fact]
    public void Parse_HashWithoutSpace_IsNotAHeading()
    {
        Single<MarkuaParagraph>(MarkuaParser.Parse("#hashtag"));
    }

    [Fact]
    public void Parse_ClassPartBeforeChapterHeading_MarksPart_AndHidesAttributeLine()
    {
        var heading = Single<MarkuaHeading>(MarkuaParser.Parse("{class: part}\n# The Saga\n"));
        Assert.True(heading.IsPart);
        Assert.Equal("The Saga", Text(heading.Runs));
    }

    [Fact]
    public void Parse_PartAttribute_AppliesOnlyToNextBlock()
    {
        var doc = MarkuaParser.Parse("{class: part}\n# Part\n\n# Chapter");
        Assert.True(((MarkuaHeading)doc.Blocks[0]).IsPart);
        Assert.False(((MarkuaHeading)doc.Blocks[1]).IsPart);
    }

    [Fact]
    public void Parse_CenterAttribute_CentersNextParagraph()
    {
        var paragraph = Single<MarkuaParagraph>(MarkuaParser.Parse("{class: center}\nThe End"));
        Assert.Equal(MarkuaAlignment.Center, paragraph.Alignment);
    }

    [Fact]
    public void Parse_UnknownAttributesAndMatterDirectives_AreHidden()
    {
        var doc = MarkuaParser.Parse("{frontmatter}\n{id: intro, sample: true}\n# Intro\n{mainmatter}");
        Assert.IsType<MarkuaHeading>(Assert.Single(doc.Blocks));
    }

    // ── Breaks ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("* * *")]
    [InlineData("***")]
    [InlineData("---")]
    [InlineData("_ _ _")]
    public void Parse_SceneBreaks(string source)
    {
        Single<MarkuaSceneBreak>(MarkuaParser.Parse(source));
    }

    [Fact]
    public void Parse_PageBreakDirective()
    {
        var doc = MarkuaParser.Parse("Before.\n\n{pagebreak}\n\nAfter.");
        Assert.IsType<MarkuaPageBreak>(doc.Blocks[1]);
        Assert.Equal(3, doc.Blocks.Count);
    }

    // ── Comments ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Comments_AreHidden()
    {
        var doc = MarkuaParser.Parse("%% a note to self\nVisible.\n\n<!-- hidden\nacross lines -->\n\nAlso visible.");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("Visible.", Text(((MarkuaParagraph)doc.Blocks[0]).Runs));
        Assert.Equal("Also visible.", Text(((MarkuaParagraph)doc.Blocks[1]).Runs));
    }

    // ── Callouts and quotes ──────────────────────────────────────────────────

    [Fact]
    public void Parse_BlurbPrefix_GroupsLinesIntoOneCallout_WithParagraphsInside()
    {
        const string source = "B> **From:** Dana <dana@example.com>\nB> **To:** Marcus\nB>\nB> Marcus,\n\nAfter.";
        var doc = MarkuaParser.Parse(source);

        var callout = Assert.IsType<MarkuaCallout>(doc.Blocks[0]);
        Assert.Equal(MarkuaCalloutKind.Blurb, callout.Kind);
        Assert.Equal(2, callout.Blocks.Count);

        var header = (MarkuaParagraph)callout.Blocks[0];
        Assert.Equal("From: Dana <dana@example.com>\nTo: Marcus", Text(header.Runs));
        Assert.True(header.Runs[0].Has(MarkuaTextStyle.Bold));
        Assert.IsType<MarkuaParagraph>(doc.Blocks[1]);
    }

    [Theory]
    [InlineData('A', MarkuaCalloutKind.Aside)]
    [InlineData('W', MarkuaCalloutKind.Warning)]
    [InlineData('T', MarkuaCalloutKind.Tip)]
    [InlineData('E', MarkuaCalloutKind.Error)]
    [InlineData('I', MarkuaCalloutKind.Information)]
    [InlineData('Q', MarkuaCalloutKind.Question)]
    [InlineData('D', MarkuaCalloutKind.Discussion)]
    [InlineData('X', MarkuaCalloutKind.Exercise)]
    [InlineData('C', MarkuaCalloutKind.Center)]
    public void Parse_CalloutLetters(char letter, MarkuaCalloutKind kind)
    {
        var callout = Single<MarkuaCallout>(MarkuaParser.Parse($"{letter}> Hello"));
        Assert.Equal(kind, callout.Kind);
    }

    [Fact]
    public void Parse_FencedBlurbWithClass_AndNestedAside()
    {
        const string source = "{blurb, class: warning}\nCareful.\n\n{aside}\nInner.\n{/aside}\n{/blurb}\n\nOutside.";
        var doc = MarkuaParser.Parse(source);

        var blurb = Assert.IsType<MarkuaCallout>(doc.Blocks[0]);
        Assert.Equal(MarkuaCalloutKind.Warning, blurb.Kind);
        Assert.Equal(MarkuaCalloutKind.Aside, Assert.IsType<MarkuaCallout>(blurb.Blocks[1]).Kind);
        Assert.Equal("Outside.", Text(((MarkuaParagraph)doc.Blocks[1]).Runs));
    }

    [Fact]
    public void Parse_UnclosedFencedAside_RunsToEndOfFile()
    {
        var callout = Single<MarkuaCallout>(MarkuaParser.Parse("{aside}\nStill typing"));
        Assert.Single(callout.Blocks);
    }

    [Fact]
    public void Parse_StrayClosingDirective_IsDropped()
    {
        Single<MarkuaParagraph>(MarkuaParser.Parse("{/aside}\nText"));
    }

    [Fact]
    public void Parse_Blockquote_BothForms()
    {
        var quote = Single<MarkuaBlockQuote>(MarkuaParser.Parse("> To be,\n> or not."));
        Assert.Equal("To be,\nor not.", Text(((MarkuaParagraph)quote.Blocks.Single()).Runs));

        var fenced = Single<MarkuaBlockQuote>(MarkuaParser.Parse("{blockquote}\nQuoted.\n{/blockquote}"));
        Assert.Single(fenced.Blocks);
    }

    // ── Code ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FencedCode_KeepsContentVerbatim()
    {
        var code = Single<MarkuaCodeBlock>(MarkuaParser.Parse("```ruby\n# not a heading\n  *x*\n```"));
        Assert.Equal("ruby", code.Language);
        Assert.Equal("# not a heading\n  *x*", code.Code);
    }

    [Fact]
    public void Parse_FormatAttribute_SetsCodeLanguage_AndTildeFence()
    {
        var code = Single<MarkuaCodeBlock>(MarkuaParser.Parse("{format: python}\n~~~\nprint(1)\n~~~"));
        Assert.Equal("python", code.Language);
    }

    // ── Lists ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_UnorderedList_WithNestedList()
    {
        var list = Single<MarkuaList>(MarkuaParser.Parse("- one\n- two\n  - two a\n  - two b\n- three"));

        Assert.False(list.Ordered);
        Assert.Equal(3, list.Items.Count);
        var nested = Assert.IsType<MarkuaList>(list.Items[1][1]);
        Assert.Equal(2, nested.Items.Count);
    }

    [Fact]
    public void Parse_OrderedList_KeepsStartNumber_AndLooseItems()
    {
        var list = Single<MarkuaList>(MarkuaParser.Parse("3. three\n\n4. four\n\n   more of four"));

        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.Items[1].Count);
    }

    [Fact]
    public void Parse_ListEndsAtUnindentedParagraph()
    {
        var doc = MarkuaParser.Parse("- item\n\nParagraph.");
        Assert.IsType<MarkuaList>(doc.Blocks[0]);
        Assert.IsType<MarkuaParagraph>(doc.Blocks[1]);
    }

    // ── Resources and tables ─────────────────────────────────────────────────

    [Fact]
    public void Parse_BlockImage()
    {
        var image = Single<MarkuaResource>(MarkuaParser.Parse("![The map](images/map.png)"));
        Assert.Equal("The map", image.Caption);
        Assert.Equal("images/map.png", image.Source);
        Assert.True(image.IsImage);
        Assert.False(new MarkuaResource("", "clip.mp4").IsImage);
    }

    [Fact]
    public void Parse_PipeTable_WithAlignment()
    {
        var table = Single<MarkuaTable>(MarkuaParser.Parse("| Name | Age |\n|:---|---:|\n| Ann | 30 |\n| Bo | 4 |"));

        Assert.Equal(["Name", "Age"], table.Header.Select(Text));
        Assert.Equal([MarkuaAlignment.Left, MarkuaAlignment.Right], table.Alignments);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Bo", Text(table.Rows[1][0]));
    }

    // ── Footnotes ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Footnotes_NumberedByFirstReference_DefinitionsHidden()
    {
        const string source = "Alpha[^b] and beta[^a], again[^b].\n\n[^a]: Note A.\n[^b]: Note *B*.";
        var doc = MarkuaParser.Parse(source);

        var paragraph = Assert.IsType<MarkuaParagraph>(Assert.Single(doc.Blocks));
        var refs = paragraph.Runs.Where(r => r.Has(MarkuaTextStyle.FootnoteReference)).Select(r => r.Text);
        Assert.Equal(["1", "2", "1"], refs);

        Assert.Equal(2, doc.Footnotes.Count);
        Assert.Equal("b", doc.Footnotes[0].Label);
        Assert.Equal("Note B.", Text(doc.Footnotes[0].Runs));
        Assert.Equal("Note A.", Text(doc.Footnotes[1].Runs));
    }

    [Fact]
    public void Parse_FootnoteWithoutDefinition_SaysSo()
    {
        var doc = MarkuaParser.Parse("Text[^ghost].");
        Assert.Contains("Missing footnote", Text(Assert.Single(doc.Footnotes).Runs));
    }

    // ── Inline ───────────────────────────────────────────────────────────────

    [Fact]
    public void Inline_BoldItalicAndCombinations()
    {
        var runs = MarkuaParser.ParseInline("a **b** *c* ***d*** __e__ _f_");

        Assert.Equal("a b c d e f", Text(runs));
        Assert.Equal(MarkuaTextStyle.Bold, runs.Single(r => r.Text == "b").Style);
        Assert.Equal(MarkuaTextStyle.Italic, runs.Single(r => r.Text == "c").Style);
        Assert.Equal(MarkuaTextStyle.Bold | MarkuaTextStyle.Italic, runs.Single(r => r.Text == "d").Style);
        Assert.Equal(MarkuaTextStyle.Bold, runs.Single(r => r.Text == "e").Style);
        Assert.Equal(MarkuaTextStyle.Italic, runs.Single(r => r.Text == "f").Style);
    }

    [Fact]
    public void Inline_NestedEmphasis()
    {
        var runs = MarkuaParser.ParseInline("*it **bold** it*");
        Assert.Equal("it bold it", Text(runs));
        Assert.Equal(MarkuaTextStyle.Bold | MarkuaTextStyle.Italic, runs.Single(r => r.Text == "bold").Style);
        Assert.All(runs, r => Assert.True(r.Has(MarkuaTextStyle.Italic)));
    }

    [Theory]
    [InlineData("5 * 3 * 2")]
    [InlineData("snake_case_name")]
    [InlineData("**unclosed")]
    [InlineData("a * b")]
    public void Inline_LiteralDelimiters_StayAsText(string source)
    {
        var run = Assert.Single(MarkuaParser.ParseInline(source));
        Assert.Equal(source, run.Text);
        Assert.Equal(MarkuaTextStyle.None, run.Style);
    }

    [Fact]
    public void Inline_CodeSpan_IgnoresEmphasisInside()
    {
        var runs = MarkuaParser.ParseInline("run `*not italic*` now");
        Assert.Equal(MarkuaTextStyle.Code, runs.Single(r => r.Text == "*not italic*").Style);
    }

    [Fact]
    public void Inline_StrikeSuperSub()
    {
        var runs = MarkuaParser.ParseInline("~~gone~~ x^2^ H~2~O");
        Assert.Equal(MarkuaTextStyle.Strikethrough, runs.Single(r => r.Text == "gone").Style);
        Assert.Equal(MarkuaTextStyle.Superscript, runs.Single(r => r.Text == "2" && r.Has(MarkuaTextStyle.Superscript)).Style);
        Assert.Contains(runs, r => r.Text == "2" && r.Style == MarkuaTextStyle.Subscript);
        Assert.Equal("gone x2 H2O", Text(runs));
    }

    [Fact]
    public void Inline_Link_CarriesUrl_AndInlineImageShowsAlt()
    {
        var runs = MarkuaParser.ParseInline("See [Leanpub](https://leanpub.com \"title\") and ![a cat](cat.png).");

        var link = runs.Single(r => r.Text == "Leanpub");
        Assert.True(link.Has(MarkuaTextStyle.Link));
        Assert.Equal("https://leanpub.com", link.Url);
        Assert.Contains(runs, r => r.Text == "a cat" && r.Has(MarkuaTextStyle.Italic));
    }

    [Fact]
    public void Inline_BackslashEscapes()
    {
        var run = Assert.Single(MarkuaParser.ParseInline(@"\*literal\* and \_too\_"));
        Assert.Equal("*literal* and _too_", run.Text);
    }

    [Fact]
    public void Parse_TrailingBackslash_IsRemovedFromLineBreak()
    {
        var paragraph = Single<MarkuaParagraph>(MarkuaParser.Parse("line one\\\nline two"));
        Assert.Equal("line one\nline two", Text(paragraph.Runs));
    }

    [Fact]
    public void Parse_EmphasisSpanningForcedLineBreak()
    {
        var paragraph = Single<MarkuaParagraph>(MarkuaParser.Parse("*first\nsecond*"));
        Assert.All(paragraph.Runs.Where(r => !r.IsLineBreak), r => Assert.True(r.Has(MarkuaTextStyle.Italic)));
    }

    [Fact(Timeout = 5000)]
    public async Task Parse_EveryConstructTogether_Terminates()
    {
        const string source = """
            {class: part}
            # Part One

            Plain **bold** *italic* `code` ~~gone~~ x^2^ [link](https://x.y) note[^n]
            second line

            B> A blurb.

            {aside}
            Aside text.
            {/aside}

            > A quote.

            - one
              1. nested

            * * *

            {pagebreak}

            ![Missing](nowhere.png)

            | A | B |
            |---|:-:|
            | 1 | 2 |

            [^n]: The note.
            """;

        var doc = await Task.Run(() => MarkuaParser.Parse(source));
        Assert.Equal(
            "Heading,Paragraph,Callout,Callout,BlockQuote,List,SceneBreak,PageBreak,Resource,Table",
            string.Join(",", doc.Blocks.Select(b => b.GetType().Name.Replace("Markua", ""))));
        Assert.Single(doc.Footnotes);
    }

    [Fact]
    public void Parse_ShortStoryFixture_ParsesWithoutLosingText()
    {
        const string story = "# Grounds for Termination\n\nB> **Subject:** Re: launch\nB>\nB> The build passed.\n\n"
                             + "She read it twice.\n\"Fine,\" she said.\n\n* * *\n\nThe next morning.";
        var doc = MarkuaParser.Parse(story);

        Assert.Collection(doc.Blocks,
            b => Assert.IsType<MarkuaHeading>(b),
            b => Assert.IsType<MarkuaCallout>(b),
            b => Assert.Equal("She read it twice.\n\"Fine,\" she said.", Text(((MarkuaParagraph)b).Runs)),
            b => Assert.IsType<MarkuaSceneBreak>(b),
            b => Assert.IsType<MarkuaParagraph>(b));
    }
}

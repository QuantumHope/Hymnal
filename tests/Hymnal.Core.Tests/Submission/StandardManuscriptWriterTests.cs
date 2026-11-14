using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Hymnal.Core.Markua;
using Hymnal.Core.Submission;

namespace Hymnal.Core.Tests.Submission;

public class StandardManuscriptWriterTests
{
    private static readonly AuthorProfile Author = new()
    {
        LegalName = "Jane Q. Writer",
        Byline = "J. Q. Writer",
        Contact = "123 Any Street\n\nSpringfield, IL 62701\njane@example.com",
    };

    private static ManuscriptRequest Request(params string[] scenes) =>
        new("The Lighthouse", Author, scenes.Select(MarkuaParser.Parse).ToList());

    private static Dictionary<string, string> Parts(ManuscriptRequest request)
    {
        using var zip = new ZipArchive(new MemoryStream(StandardManuscriptWriter.ToBytes(request)), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Each paragraph of document.xml as (style, text).</summary>
    private static List<(string Style, string Text)> Paragraphs(ManuscriptRequest request)
    {
        var doc = XDocument.Parse(Parts(request)["word/document.xml"]);
        return doc.Descendants(W + "p")
            .Select(p => (
                (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "",
                string.Concat(p.Descendants().Select(d =>
                    d.Name == W + "t" ? d.Value : d.Name == W + "tab" ? "\t" : d.Name == W + "br" ? "\n" : ""))))
            .ToList();
    }

    // ── Word counts ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(742, 742, "742 words")]
    [InlineData(1, 1, "1 word")]
    [InlineData(999, 999, "999 words")]
    [InlineData(1000, 1000, "about 1,000 words")]
    [InlineData(3449, 3400, "about 3,400 words")]
    [InlineData(3450, 3500, "about 3,500 words")]
    [InlineData(12345, 12300, "about 12,300 words")]
    public void WordCount_RoundsToHundredsExceptFlash(int words, int rounded, string description)
    {
        Assert.Equal(rounded, ManuscriptWordCount.Round(words));
        Assert.Equal(description, ManuscriptWordCount.Describe(words));
    }

    [Theory]
    [InlineData(999, "Flash fiction")]
    [InlineData(1000, "Short story")]
    [InlineData(7499, "Short story")]
    [InlineData(7500, "Novelette")]
    [InlineData(17500, "Novella")]
    [InlineData(40000, "Novel")]
    public void WordCount_Category_UsesSfwaBoundaries(int words, string category) =>
        Assert.Equal(category, ManuscriptWordCount.Category(words));

    [Fact]
    public void WordCount_CountsReadableWords_NotMarkupOrTheFileHeading()
    {
        var scenes = new[]
        {
            MarkuaParser.Parse("# Scene One\n\nShe *ran* to the **door**.\n\n* * *\n\nIt was locked."),
            MarkuaParser.Parse("B> **From:** Dana\nB> Hello there."),
        };

        // "She ran to the door." (5) + "It was locked." (3) + "From: Dana" (2) + "Hello there." (2)
        Assert.Equal(12, ManuscriptWordCount.Count(scenes));
    }

    // ── Document ─────────────────────────────────────────────────────────────

    [Fact]
    public void Write_ProducesWellFormedWordPackage()
    {
        var parts = Parts(Request("# The Lighthouse\n\nA & B < C said \"hi\"."));

        foreach (var name in new[]
                 {
                     "[Content_Types].xml", "_rels/.rels", "docProps/core.xml", "word/_rels/document.xml.rels",
                     "word/document.xml", "word/styles.xml", "word/settings.xml", "word/header1.xml",
                 })
        {
            Assert.True(parts.ContainsKey(name), $"missing {name}");
            XDocument.Parse(parts[name]); // throws if malformed
        }
        Assert.Contains("A &amp; B &lt; C", parts["word/document.xml"]);
    }

    [Fact]
    public void Write_FirstPage_HasContactWordCountTitleAndByline()
    {
        var paragraphs = Paragraphs(Request("# The Lighthouse\n\nThe lamp went out."));

        Assert.Equal(("Contact", "Jane Q. Writer\t4 words"), paragraphs[0]);
        Assert.Equal(("Contact", "123 Any Street"), paragraphs[1]);
        Assert.Equal(("Contact", "Springfield, IL 62701"), paragraphs[2]);
        Assert.Equal(("Contact", "jane@example.com"), paragraphs[3]);
        Assert.Equal(("ManuscriptTitle", "The Lighthouse"), paragraphs[4]);
        Assert.Equal(("Byline", "by J. Q. Writer"), paragraphs[5]);
        Assert.Equal(("Body", "The lamp went out."), paragraphs[6]);
        Assert.Equal(("Centered", "END"), paragraphs[^1]);
    }

    [Fact]
    public void Write_DropsEachSceneHeading_AndJoinsScenesWithABreak()
    {
        var paragraphs = Paragraphs(Request("# Arrival\n\nFirst.", "# Departure\n\nSecond.", "* * *\n\nThird."));
        var body = paragraphs.SkipWhile(p => p.Style != "Byline").Skip(1).ToList();

        Assert.Equal(
            [("Body", "First."), ("Centered", "#"), ("Body", "Second."), ("Centered", "#"), ("Body", "Third."), ("Centered", "END")],
            body);
        Assert.DoesNotContain(paragraphs, p => p.Text is "Arrival" or "Departure");
    }

    [Fact]
    public void Write_KeepsItalicsAndBold_AndIndentsBlurbs()
    {
        var parts = Parts(Request("She was *very* **sure**.\n\nB> **From:** Dana"));
        var doc = XDocument.Parse(parts["word/document.xml"]);
        var runs = doc.Descendants(W + "r").ToList();

        Assert.NotNull(runs.Single(r => r.Value == "very").Element(W + "rPr")?.Element(W + "i"));
        Assert.NotNull(runs.Single(r => r.Value == "sure").Element(W + "rPr")?.Element(W + "b"));
        Assert.Contains(Paragraphs(Request("B> **From:** Dana")), p => p == ("Block", "From: Dana"));
    }

    [Fact]
    public void Write_HeaderCarriesSurnameTitleAndPage_FromThePageAfterTheFirst()
    {
        var parts = Parts(Request("Text."));

        Assert.Contains("Writer / The Lighthouse / ", parts["word/header1.xml"]);
        Assert.Contains("PAGE", parts["word/header1.xml"]);
        Assert.Contains("<w:titlePg/>", parts["word/document.xml"]);
    }

    [Fact]
    public void Write_UsesTheChosenFont()
    {
        var times = Parts(Request("Text."))["word/styles.xml"];
        var courier = Parts(Request("Text.") with { Author = Author with { Font = ManuscriptFont.CourierNew } })["word/styles.xml"];

        Assert.Contains("Times New Roman", times);
        Assert.Contains("w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:eastAsia", courier);
    }

    [Fact]
    public void Author_DefaultsBylineAndSurnameFromTheLegalName()
    {
        var author = new AuthorProfile { LegalName = "Ursula K. Le Guin" };

        Assert.Equal("Ursula K. Le Guin", author.EffectiveByline);
        Assert.Equal("Guin", author.EffectiveSurname);
        Assert.Equal("Le Guin", (author with { Surname = " Le Guin " }).EffectiveSurname);
    }
}

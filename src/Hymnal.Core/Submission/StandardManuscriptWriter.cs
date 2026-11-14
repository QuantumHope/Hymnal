using System.IO.Compression;
using System.Text;
using Hymnal.Core.Markua;

namespace Hymnal.Core.Submission;

/// <summary>
/// Lays a story out in Standard Manuscript Format (William Shunn's modern version, which most fiction markets
/// ask for) and writes it as a Word document. No library is needed: a .docx is a zip of a few XML parts.
/// <list type="bullet">
/// <item>US Letter, 1" margins, 12 pt Times New Roman or Courier New, double spaced, ½" first-line indents,
/// no space between paragraphs, ragged right.</item>
/// <item>Page 1: the legal name and contact lines top left (single spaced), the word count top right, then
/// the title centred about a third of the way down, "by" the byline under it, and the story.</item>
/// <item>Every later page: "Surname / Title / page" at the top right.</item>
/// <item>Scene breaks (and the join between two scene files) are a centred "#"; "END" closes the story.</item>
/// <item>Each scene file's own level-1 heading is dropped: the title page names the story, and scene titles
/// are the writer's labels, not the reader's.</item>
/// </list>
/// Markua's inline styles carry over (italics stay italics, as the modern format prefers to underlining);
/// blurbs, asides and quotations are indented blocks; images become a bracketed placeholder.
/// </summary>
public static class StandardManuscriptWriter
{
    // All lengths are twentieths of a point (twips): 1440 = 1 inch.
    private const int Inch = 1440;
    private const int HalfInch = 720;
    private const int PageWidth = 12240;
    private const int PageHeight = 15840;
    private const int TextWidth = PageWidth - 2 * Inch;
    private const int TitleSpaceBefore = 3 * Inch;
    private const string SceneBreakMark = "#";
    private const string EndMark = "END";

    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Writes the manuscript as a .docx to <paramref name="output"/>.</summary>
    public static void Write(ManuscriptRequest request, Stream output)
    {
        var words = ManuscriptWordCount.Count(request.Scenes);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", PackageRels);
        Add(zip, "docProps/core.xml", CoreProperties(request));
        Add(zip, "word/_rels/document.xml.rels", DocumentRels);
        Add(zip, "word/styles.xml", Styles(request.Author.Font));
        Add(zip, "word/settings.xml", Settings);
        Add(zip, "word/header1.xml", Header(request));
        Add(zip, "word/document.xml", Document(request, words));
    }

    /// <summary>The manuscript as .docx bytes.</summary>
    public static byte[] ToBytes(ManuscriptRequest request)
    {
        using var stream = new MemoryStream();
        Write(request, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The text of every block of a scene as it will be set, one string per block (used for the word count,
    /// so it matches what the manuscript shows).
    /// </summary>
    public static IEnumerable<string> StoryText(MarkuaDocument scene)
    {
        foreach (var block in StoryBlocks(scene))
            foreach (var text in BlockText(block))
                yield return text;
        foreach (var note in scene.Footnotes)
            yield return RunsText(note.Runs);
    }

    // ── Story body ────────────────────────────────────────────────────────────

    /// <summary>A scene's blocks without the file's own leading level-1 heading.</summary>
    private static IEnumerable<MarkuaBlock> StoryBlocks(MarkuaDocument scene) =>
        scene.Blocks.Count > 0 && scene.Blocks[0] is MarkuaHeading { Level: 1 }
            ? scene.Blocks.Skip(1)
            : scene.Blocks;

    private static IEnumerable<string> BlockText(MarkuaBlock block)
    {
        switch (block)
        {
            case MarkuaHeading h: yield return RunsText(h.Runs); break;
            case MarkuaParagraph p: yield return RunsText(p.Runs); break;
            case MarkuaBlockQuote q:
                foreach (var inner in q.Blocks.SelectMany(BlockText)) yield return inner;
                break;
            case MarkuaCallout c:
                foreach (var inner in c.Blocks.SelectMany(BlockText)) yield return inner;
                break;
            case MarkuaList l:
                foreach (var inner in l.Items.SelectMany(i => i).SelectMany(BlockText)) yield return inner;
                break;
            case MarkuaCodeBlock code: yield return code.Code; break;
            case MarkuaTable t:
                foreach (var cell in t.Header.Concat(t.Rows.SelectMany(r => r))) yield return RunsText(cell);
                break;
        }
    }

    private static string RunsText(IReadOnlyList<MarkuaRun> runs) =>
        string.Concat(runs.Select(r => r.IsLineBreak ? " " : r.Text));

    private static string Document(ManuscriptRequest request, int words)
    {
        var body = new StringBuilder();
        var author = request.Author;

        // Contact block, single spaced; the first line carries the word count at the right margin.
        var contact = new List<string>();
        if (!string.IsNullOrWhiteSpace(author.LegalName))
            contact.Add(author.LegalName.Trim());
        contact.AddRange(author.ContactLines);
        if (contact.Count == 0)
            contact.Add("");
        for (var i = 0; i < contact.Count; i++)
        {
            body.Append("<w:p><w:pPr><w:pStyle w:val=\"Contact\"/></w:pPr>");
            body.Append(TextRun(contact[i]));
            if (i == 0)
                body.Append("<w:r><w:tab/></w:r>").Append(TextRun(ManuscriptWordCount.Describe(words)));
            body.Append("</w:p>");
        }

        body.Append("<w:p><w:pPr><w:pStyle w:val=\"ManuscriptTitle\"/></w:pPr>")
            .Append(TextRun(request.Title.Trim())).Append("</w:p>");
        if (author.EffectiveByline.Length > 0)
            body.Append("<w:p><w:pPr><w:pStyle w:val=\"Byline\"/></w:pPr>")
                .Append(TextRun("by " + author.EffectiveByline)).Append("</w:p>");

        var footnotes = new List<MarkuaFootnote>();
        var scenes = request.Scenes.Where(s => StoryBlocks(s).Any()).ToList();
        for (var s = 0; s < scenes.Count; s++)
        {
            var blocks = StoryBlocks(scenes[s]).ToList();
            // Two scene files meet at a scene break, unless the writer already ended or began one with it.
            if (s > 0 && blocks[0] is not MarkuaSceneBreak && StoryBlocks(scenes[s - 1]).LastOrDefault() is not MarkuaSceneBreak)
                body.Append(Centered(SceneBreakMark));
            foreach (var block in blocks)
                AppendBlock(body, block, ParagraphContext.Body);
            footnotes.AddRange(scenes[s].Footnotes);
        }

        body.Append(Centered(EndMark));

        if (footnotes.Count > 0)
        {
            body.Append(Centered("Notes"));
            var number = 1;
            foreach (var note in footnotes)
                body.Append("<w:p><w:pPr><w:pStyle w:val=\"Block\"/></w:pPr>")
                    .Append(TextRun($"{number++}. ")).Append(Runs(note.Runs)).Append("</w:p>");
        }

        body.Append("<w:sectPr>")
            .Append("<w:headerReference w:type=\"default\" r:id=\"rIdHeader1\"/>")
            .Append($"<w:pgSz w:w=\"{PageWidth}\" w:h=\"{PageHeight}\"/>")
            .Append($"<w:pgMar w:top=\"{Inch}\" w:right=\"{Inch}\" w:bottom=\"{Inch}\" w:left=\"{Inch}\" w:header=\"{HalfInch}\" w:footer=\"{HalfInch}\" w:gutter=\"0\"/>")
            .Append("<w:titlePg/>")
            .Append("</w:sectPr>");

        return Xml($"<w:document xmlns:w=\"{WordNs}\" xmlns:r=\"{RelNs}\"><w:body>{body}</w:body></w:document>");
    }

    private enum ParagraphContext
    {
        Body,
        Block,
        Centered,
    }

    private static void AppendBlock(StringBuilder body, MarkuaBlock block, ParagraphContext context)
    {
        switch (block)
        {
            case MarkuaHeading heading:
                // A heading inside the story (a numbered section, say) is set centred, keeping its emphasis.
                body.Append(Paragraph("Centered", Runs(heading.Runs)));
                break;
            case MarkuaParagraph paragraph:
                var style = paragraph.Alignment switch
                {
                    MarkuaAlignment.Center => "Centered",
                    MarkuaAlignment.Right => "RightAligned",
                    _ => StyleFor(context),
                };
                body.Append(Paragraph(style, Runs(paragraph.Runs)));
                break;
            case MarkuaBlockQuote quote:
                foreach (var inner in quote.Blocks)
                    AppendBlock(body, inner, ParagraphContext.Block);
                break;
            case MarkuaCallout callout:
                var innerContext = callout.Kind == MarkuaCalloutKind.Center ? ParagraphContext.Centered : ParagraphContext.Block;
                foreach (var inner in callout.Blocks)
                    AppendBlock(body, inner, innerContext);
                break;
            case MarkuaList list:
                var number = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Ordered ? $"{number++}. " : "• ";
                    var first = true;
                    foreach (var inner in item)
                    {
                        if (first && inner is MarkuaParagraph p)
                            body.Append(Paragraph("ListItem", TextRun(marker) + Runs(p.Runs)));
                        else
                            AppendBlock(body, inner, ParagraphContext.Block);
                        first = false;
                    }
                }
                break;
            case MarkuaCodeBlock code:
                foreach (var line in code.Code.Replace("\r\n", "\n").Split('\n'))
                    body.Append(Paragraph("Code", TextRun(line)));
                break;
            case MarkuaTable table:
                foreach (var row in new[] { table.Header }.Concat(table.Rows))
                {
                    var cells = row.Select(Runs).ToList();
                    body.Append(Paragraph("Block", string.Join("<w:r><w:tab/></w:r>", cells)));
                }
                break;
            case MarkuaSceneBreak:
                body.Append(Centered(SceneBreakMark));
                break;
            case MarkuaPageBreak:
                body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                break;
            case MarkuaResource resource:
                var what = resource.IsImage ? "Image" : "Media";
                var label = string.IsNullOrWhiteSpace(resource.Caption) ? resource.Source : resource.Caption;
                body.Append(Centered($"[{what}: {label}]"));
                break;
        }
    }

    private static string StyleFor(ParagraphContext context) => context switch
    {
        ParagraphContext.Block => "Block",
        ParagraphContext.Centered => "Centered",
        _ => "Body",
    };

    private static string Paragraph(string style, string runs) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>{runs}</w:p>";

    private static string Centered(string text) => Paragraph("Centered", TextRun(text));

    private static string Runs(IReadOnlyList<MarkuaRun> runs)
    {
        var xml = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.IsLineBreak)
            {
                xml.Append("<w:r><w:br/></w:r>");
                continue;
            }
            if (run.Text.Length == 0)
                continue;
            var props = new StringBuilder();
            if (run.Has(MarkuaTextStyle.Code))
                props.Append("<w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\"/>");
            if (run.Has(MarkuaTextStyle.Bold))
                props.Append("<w:b/>");
            if (run.Has(MarkuaTextStyle.Italic))
                props.Append("<w:i/>");
            if (run.Has(MarkuaTextStyle.Strikethrough))
                props.Append("<w:strike/>");
            if (run.Has(MarkuaTextStyle.Superscript) || run.Has(MarkuaTextStyle.FootnoteReference))
                props.Append("<w:vertAlign w:val=\"superscript\"/>");
            else if (run.Has(MarkuaTextStyle.Subscript))
                props.Append("<w:vertAlign w:val=\"subscript\"/>");
            xml.Append(TextRun(run.Text, props.ToString()));
        }
        return xml.ToString();
    }

    private static string TextRun(string text, string properties = "")
    {
        var rPr = properties.Length > 0 ? $"<w:rPr>{properties}</w:rPr>" : "";
        return $"<w:r>{rPr}<w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r>";
    }

    // ── Package parts ─────────────────────────────────────────────────────────

    private static string Header(ManuscriptRequest request)
    {
        var surname = request.Author.EffectiveSurname;
        var label = (surname.Length > 0 ? surname + " / " : "") + request.Title.Trim() + " / ";
        return Xml(
            $"<w:hdr xmlns:w=\"{WordNs}\" xmlns:r=\"{RelNs}\">"
            + "<w:p><w:pPr><w:jc w:val=\"right\"/><w:spacing w:line=\"240\" w:lineRule=\"auto\"/><w:ind w:firstLine=\"0\"/></w:pPr>"
            + TextRun(label)
            + "<w:fldSimple w:instr=\" PAGE \"><w:r><w:t>2</w:t></w:r></w:fldSimple>"
            + "</w:p></w:hdr>");
    }

    private static string Styles(ManuscriptFont font)
    {
        var face = font == ManuscriptFont.CourierNew ? "Courier New" : "Times New Roman";
        string Style(string id, string name, string pPr, string rPr = "") =>
            $"<w:style w:type=\"paragraph\" w:customStyle=\"1\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/>"
            + $"<w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr>{pPr}</w:pPr>"
            + (rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "") + "</w:style>";

        return Xml(
            $"<w:styles xmlns:w=\"{WordNs}\">"
            + "<w:docDefaults><w:rPrDefault><w:rPr>"
            + $"<w:rFonts w:ascii=\"{face}\" w:hAnsi=\"{face}\" w:eastAsia=\"{face}\" w:cs=\"{face}\"/>"
            + "<w:sz w:val=\"24\"/><w:szCs w:val=\"24\"/><w:lang w:val=\"en-US\"/>"
            + "</w:rPr></w:rPrDefault>"
            + "<w:pPrDefault><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"480\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>"
            + "</w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/>"
            + "<w:pPr><w:widowControl/></w:pPr></w:style>"
            + Style("Body", "Manuscript Body", $"<w:ind w:firstLine=\"{HalfInch}\"/>")
            + Style("Contact", "Manuscript Contact",
                $"<w:tabs><w:tab w:val=\"right\" w:pos=\"{TextWidth}\"/></w:tabs><w:spacing w:line=\"240\" w:lineRule=\"auto\"/>")
            + Style("ManuscriptTitle", "Manuscript Title",
                $"<w:keepNext/><w:spacing w:before=\"{TitleSpaceBefore}\"/><w:jc w:val=\"center\"/>")
            + Style("Byline", "Manuscript Byline", "<w:spacing w:after=\"480\"/><w:jc w:val=\"center\"/>")
            + Style("Centered", "Manuscript Centered", "<w:keepNext/><w:jc w:val=\"center\"/>")
            + Style("RightAligned", "Manuscript Right", "<w:jc w:val=\"right\"/>")
            + Style("Block", "Manuscript Block", $"<w:ind w:left=\"{HalfInch}\" w:right=\"{HalfInch}\"/>")
            + Style("ListItem", "Manuscript List Item", $"<w:ind w:left=\"{Inch}\" w:hanging=\"360\"/>")
            + Style("Code", "Manuscript Code", $"<w:ind w:left=\"{HalfInch}\"/>",
                "<w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\"/>")
            + "</w:styles>");
    }

    private static string CoreProperties(ManuscriptRequest request) => Xml(
        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
        + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
        + $"<dc:title>{Escape(request.Title.Trim())}</dc:title>"
        + $"<dc:creator>{Escape(request.Author.EffectiveByline)}</dc:creator>"
        + "</cp:coreProperties>");

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
        + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
        + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
        + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
        + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
        + "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>"
        + "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>"
        + "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>"
        + "</Types>";

    private const string PackageRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>"
        + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
        + "</Relationships>";

    private const string DocumentRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
        + "<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>"
        + "<Relationship Id=\"rIdHeader1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>"
        + "</Relationships>";

    private static readonly string Settings =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + $"<w:settings xmlns:w=\"{WordNs}\"><w:defaultTabStop w:val=\"{HalfInch}\"/>"
        + "<w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>"
        + "</w:settings>";

    private static string Xml(string root) => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + root;

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                // Characters XML 1.0 can't hold (stray control codes) are dropped rather than corrupting the file.
                case < ' ' when c is not ('\t' or '\n' or '\r'): break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}

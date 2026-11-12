using System.Text;
using System.Text.RegularExpressions;

namespace Hymnal.Core.Markua;

/// <summary>
/// Turns Markua source into a <see cref="MarkuaDocument"/> for the editor's preview. It is forgiving: anything it
/// does not recognize stays visible as text, so a half-typed construct never makes prose disappear.
/// <para>
/// Markua rules that differ from Markdown and matter for the preview: a single newline inside a paragraph is a
/// forced line break; <c>{class: part}</c> before a chapter heading makes it a Part; <c>A&gt;</c>, <c>B&gt;</c>
/// and the typed blurbs (<c>W&gt; T&gt; E&gt; I&gt; Q&gt; D&gt; X&gt; C&gt;</c>) and their <c>{aside}</c> /
/// <c>{blurb}</c> fenced forms are boxed callouts; <c>%%</c> lines are comments.
/// </para>
/// </summary>
public static class MarkuaParser
{
    private static readonly Regex HeadingRegex = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?[ \t]*#*[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex SceneBreakRegex = new(@"^ {0,3}([*\-_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex FenceRegex = new(@"^ {0,3}(`{3,}|~{3,})[ \t]*([^`\s]*)", RegexOptions.Compiled);
    private static readonly Regex AttributeLineRegex = new(@"^ {0,3}\{([^{}]*)\}[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex CalloutPrefixRegex = new(@"^([ABWTEIQDXC])>(?:[ \t]|$)", RegexOptions.Compiled);
    private static readonly Regex QuotePrefixRegex = new(@"^ {0,3}>", RegexOptions.Compiled);
    private static readonly Regex ListItemRegex = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$", RegexOptions.Compiled);
    private static readonly Regex FootnoteDefinitionRegex = new(@"^ {0,3}\[\^([^\]\s]+)\]:[ \t]*(.*)$", RegexOptions.Compiled);
    private static readonly Regex ResourceRegex = new(@"^ {0,3}!\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(?:\s+""[^""]*"")?\s*\)[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex TableDelimiterRegex = new(@"^ {0,3}\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled);

    public static MarkuaDocument Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return MarkuaDocument.Empty;

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var context = new ParseContext();
        var blocks = ParseBlocks(lines, context);
        return new MarkuaDocument(blocks, context.BuildFootnotes());
    }

    /// <summary>Parses one line of inline Markua (used for a footnote or table cell; exposed for tests).</summary>
    public static IReadOnlyList<MarkuaRun> ParseInline(string text) => ParseInlines(text, new ParseContext());

    // ── Blocks ───────────────────────────────────────────────────────────────

    private static List<MarkuaBlock> ParseBlocks(IReadOnlyList<string> lines, ParseContext context,
        bool listMarkersInterrupt = false)
    {
        var blocks = new List<MarkuaBlock>();
        var pending = new PendingAttributes();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // Comments: %% line comments and HTML comments (possibly spanning lines).
            if (trimmed.StartsWith("%%", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                while (i < lines.Count && !lines[i].Contains("-->", StringComparison.Ordinal))
                    i++;
                i++;
                continue;
            }

            var fence = FenceRegex.Match(line);
            if (fence.Success)
            {
                i = ParseCodeFence(lines, i, fence, pending.Format, blocks);
                pending = new PendingAttributes();
                continue;
            }

            var attribute = AttributeLineRegex.Match(line);
            if (attribute.Success)
            {
                i = ParseAttributeLine(lines, i, attribute.Groups[1].Value.Trim(), context, blocks, ref pending);
                continue;
            }

            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                var runs = ParseInlines(heading.Groups[2].Value, context);
                blocks.Add(new MarkuaHeading(level, runs, IsPart: level == 1 && pending.IsPart));
                pending = new PendingAttributes();
                i++;
                continue;
            }

            if (SceneBreakRegex.IsMatch(line))
            {
                blocks.Add(new MarkuaSceneBreak());
                pending = new PendingAttributes();
                i++;
                continue;
            }

            var footnote = FootnoteDefinitionRegex.Match(line);
            if (footnote.Success)
            {
                var text = new StringBuilder(footnote.Groups[2].Value);
                i++;
                while (i < lines.Count && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]) && lines[i].Trim().Length > 0)
                {
                    text.Append('\n').Append(lines[i].Trim());
                    i++;
                }

                context.DefineFootnote(footnote.Groups[1].Value, text.ToString());
                continue;
            }

            var callout = CalloutPrefixRegex.Match(line);
            if (callout.Success)
            {
                var letter = callout.Groups[1].Value[0];
                var inner = new List<string>();
                while (i < lines.Count && lines[i].Length >= 2 && lines[i][0] == letter && lines[i][1] == '>')
                {
                    inner.Add(StripPrefix(lines[i], 2));
                    i++;
                }

                blocks.Add(new MarkuaCallout(CalloutKindFromLetter(letter), ParseBlocks(inner, context)));
                pending = new PendingAttributes();
                continue;
            }

            if (QuotePrefixRegex.IsMatch(line))
            {
                var inner = new List<string>();
                while (i < lines.Count && QuotePrefixRegex.IsMatch(lines[i]))
                {
                    var stripped = lines[i].TrimStart(' ');
                    inner.Add(StripPrefix(stripped, 1));
                    i++;
                }

                blocks.Add(new MarkuaBlockQuote(ParseBlocks(inner, context)));
                pending = new PendingAttributes();
                continue;
            }

            var resource = ResourceRegex.Match(line);
            if (resource.Success)
            {
                blocks.Add(new MarkuaResource(resource.Groups[1].Value.Trim(), resource.Groups[2].Value));
                pending = new PendingAttributes();
                i++;
                continue;
            }

            if (i + 1 < lines.Count && line.Contains('|') && TableDelimiterRegex.IsMatch(lines[i + 1])
                && lines[i + 1].Contains('-'))
            {
                i = ParseTable(lines, i, context, blocks);
                pending = new PendingAttributes();
                continue;
            }

            if (ListItemRegex.IsMatch(line))
            {
                i = ParseList(lines, i, context, blocks);
                pending = new PendingAttributes();
                continue;
            }

            i = ParseParagraph(lines, i, context, pending.Alignment, listMarkersInterrupt, blocks);
            pending = new PendingAttributes();
        }

        return blocks;
    }

    private static string StripPrefix(string line, int prefixLength)
    {
        var rest = line.Length > prefixLength ? line[prefixLength..] : "";
        return rest.StartsWith(' ') || rest.StartsWith('\t') ? rest[1..] : rest;
    }

    private static MarkuaCalloutKind CalloutKindFromLetter(char letter) => letter switch
    {
        'A' => MarkuaCalloutKind.Aside,
        'W' => MarkuaCalloutKind.Warning,
        'T' => MarkuaCalloutKind.Tip,
        'E' => MarkuaCalloutKind.Error,
        'I' => MarkuaCalloutKind.Information,
        'Q' => MarkuaCalloutKind.Question,
        'D' => MarkuaCalloutKind.Discussion,
        'X' => MarkuaCalloutKind.Exercise,
        'C' => MarkuaCalloutKind.Center,
        _ => MarkuaCalloutKind.Blurb,
    };

    private static MarkuaCalloutKind? CalloutKindFromClass(string? cssClass) => cssClass?.ToLowerInvariant() switch
    {
        null or "" => null,
        "warning" => MarkuaCalloutKind.Warning,
        "tip" => MarkuaCalloutKind.Tip,
        "error" => MarkuaCalloutKind.Error,
        "information" or "info" => MarkuaCalloutKind.Information,
        "question" => MarkuaCalloutKind.Question,
        "discussion" => MarkuaCalloutKind.Discussion,
        "exercise" => MarkuaCalloutKind.Exercise,
        "center" => MarkuaCalloutKind.Center,
        _ => null,
    };

    private static int ParseCodeFence(IReadOnlyList<string> lines, int start, Match fence, string? format, List<MarkuaBlock> blocks)
    {
        var marker = fence.Groups[1].Value;
        var language = fence.Groups[2].Value;
        var code = new List<string>();
        var i = start + 1;

        while (i < lines.Count)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length >= marker.Length && trimmed.All(c => c == marker[0]))
            {
                i++;
                break;
            }

            code.Add(lines[i]);
            i++;
        }

        blocks.Add(new MarkuaCodeBlock(string.Join("\n", code), language.Length > 0 ? language : format));
        return i;
    }

    /// <summary>
    /// Handles a line that is only <c>{…}</c>: a directive (<c>{pagebreak}</c>), the start of a fenced aside,
    /// blurb or blockquote, or attributes for the next block.
    /// </summary>
    private static int ParseAttributeLine(IReadOnlyList<string> lines, int i, string content, ParseContext context,
        List<MarkuaBlock> blocks, ref PendingAttributes pending)
    {
        var parts = SplitAttributes(content);
        var first = parts.Count > 0 ? parts[0] : "";
        var head = first.Contains(':') ? "" : first.ToLowerInvariant();

        switch (head)
        {
            case "pagebreak":
                blocks.Add(new MarkuaPageBreak());
                return i + 1;

            case "frontmatter" or "mainmatter" or "backmatter":
                return i + 1;

            case "aside" or "blurb" or "blockquote":
            {
                var classes = ReadAttributes(parts);
                var end = FindClosingDirective(lines, i + 1, head);
                var inner = lines.Skip(i + 1).Take(end - i - 1).ToList();
                var innerBlocks = ParseBlocks(inner, context);

                if (head == "blockquote")
                    blocks.Add(new MarkuaBlockQuote(innerBlocks));
                else if (head == "aside")
                    blocks.Add(new MarkuaCallout(MarkuaCalloutKind.Aside, innerBlocks));
                else
                    blocks.Add(new MarkuaCallout(
                        CalloutKindFromClass(classes.GetValueOrDefault("class")) ?? MarkuaCalloutKind.Blurb, innerBlocks));

                pending = new PendingAttributes();
                return Math.Min(end + 1, lines.Count);
            }
        }

        // A stray closing directive is dropped rather than shown.
        if (first.StartsWith('/'))
            return i + 1;

        var attributes = ReadAttributes(parts);
        var cssClass = attributes.GetValueOrDefault("class")?.ToLowerInvariant();
        var align = attributes.GetValueOrDefault("align")?.ToLowerInvariant();

        if (cssClass == "part")
            pending.IsPart = true;

        if (cssClass == "center" || align == "center")
            pending.Alignment = MarkuaAlignment.Center;
        else if (align == "right")
            pending.Alignment = MarkuaAlignment.Right;
        else if (align == "left")
            pending.Alignment = MarkuaAlignment.Left;

        if (attributes.GetValueOrDefault("format") is { Length: > 0 } format)
            pending.Format = format;

        return i + 1;
    }

    private static int FindClosingDirective(IReadOnlyList<string> lines, int from, string name)
    {
        var depth = 0;
        for (var j = from; j < lines.Count; j++)
        {
            var match = AttributeLineRegex.Match(lines[j]);
            if (!match.Success)
                continue;

            var first = SplitAttributes(match.Groups[1].Value.Trim()).FirstOrDefault()?.ToLowerInvariant();
            if (first == name)
                depth++;
            else if (first == "/" + name)
            {
                if (depth == 0)
                    return j;
                depth--;
            }
        }

        return lines.Count;
    }

    private static List<string> SplitAttributes(string content) =>
        content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static Dictionary<string, string> ReadAttributes(IEnumerable<string> parts)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
                continue;

            var value = part[(colon + 1)..].Trim().Trim('"', '\'');
            result[part[..colon].Trim()] = value;
        }

        return result;
    }

    private static int ParseTable(IReadOnlyList<string> lines, int start, ParseContext context, List<MarkuaBlock> blocks)
    {
        var header = SplitRow(lines[start]).Select(cell => (IReadOnlyList<MarkuaRun>)ParseInlines(cell, context)).ToList();
        var alignments = SplitRow(lines[start + 1]).Select(cell =>
        {
            var c = cell.Trim();
            return (c.StartsWith(':'), c.EndsWith(':')) switch
            {
                (true, true) => MarkuaAlignment.Center,
                (false, true) => MarkuaAlignment.Right,
                (true, false) => MarkuaAlignment.Left,
                _ => MarkuaAlignment.Default,
            };
        }).ToList();

        var rows = new List<IReadOnlyList<IReadOnlyList<MarkuaRun>>>();
        var i = start + 2;
        while (i < lines.Count && lines[i].Trim().Length > 0 && lines[i].Contains('|'))
        {
            rows.Add(SplitRow(lines[i]).Select(cell => (IReadOnlyList<MarkuaRun>)ParseInlines(cell, context)).ToList());
            i++;
        }

        blocks.Add(new MarkuaTable(header, rows, alignments));
        return i;
    }

    private static List<string> SplitRow(string line)
    {
        var row = line.Trim();
        if (row.StartsWith('|'))
            row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal))
            row = row[..^1];

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var k = 0; k < row.Length; k++)
        {
            if (row[k] == '\\' && k + 1 < row.Length && row[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (row[k] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(row[k]);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static int ParseList(IReadOnlyList<string> lines, int start, ParseContext context, List<MarkuaBlock> blocks)
    {
        var first = ListItemRegex.Match(lines[start]);
        var indent = IndentWidth(first.Groups[1].Value);
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        var startNumber = ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var n) ? n : 1;

        var items = new List<IReadOnlyList<MarkuaBlock>>();
        var i = start;

        while (i < lines.Count)
        {
            var marker = ListItemRegex.Match(lines[i]);
            if (!marker.Success || SceneBreakRegex.IsMatch(lines[i]) || IndentWidth(marker.Groups[1].Value) != indent
                || char.IsDigit(marker.Groups[2].Value[0]) != ordered)
                break;

            var contentIndent = marker.Groups[3].Index;
            var itemLines = new List<string> { marker.Groups[3].Value };
            i++;

            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // A blank line continues the item only when the next text is indented under it.
                    var next = i + 1;
                    while (next < lines.Count && lines[next].Trim().Length == 0)
                        next++;

                    if (next < lines.Count && IndentWidth(LeadingWhitespace(lines[next])) > indent)
                    {
                        itemLines.Add("");
                        i++;
                        continue;
                    }

                    break;
                }

                var lineIndent = IndentWidth(LeadingWhitespace(line));
                if (lineIndent > indent)
                {
                    itemLines.Add(Dedent(line, Math.Min(lineIndent, contentIndent)));
                    i++;
                    continue;
                }

                // Same-level marker → next item; anything else at this indent ends the list unless it is a lazy
                // continuation of the item's paragraph.
                if (ListItemRegex.IsMatch(line) || StartsBlock(line))
                    break;

                if (itemLines.Count > 0 && itemLines[^1].Trim().Length > 0)
                {
                    itemLines.Add(line.Trim());
                    i++;
                    continue;
                }

                break;
            }

            items.Add(ParseBlocks(itemLines, context, listMarkersInterrupt: true));

            // Skip blank lines between items of the same list.
            var peek = i;
            while (peek < lines.Count && lines[peek].Trim().Length == 0)
                peek++;
            if (peek < lines.Count && peek != i)
            {
                var nextMarker = ListItemRegex.Match(lines[peek]);
                if (nextMarker.Success && !SceneBreakRegex.IsMatch(lines[peek])
                    && IndentWidth(nextMarker.Groups[1].Value) == indent
                    && char.IsDigit(nextMarker.Groups[2].Value[0]) == ordered)
                    i = peek;
            }
        }

        blocks.Add(new MarkuaList(ordered, startNumber, items));
        return i;
    }

    private static string LeadingWhitespace(string line)
    {
        var k = 0;
        while (k < line.Length && (line[k] == ' ' || line[k] == '\t'))
            k++;
        return line[..k];
    }

    private static int IndentWidth(string whitespace) => whitespace.Sum(c => c == '\t' ? 4 : 1);

    private static string Dedent(string line, int width)
    {
        var removed = 0;
        var k = 0;
        while (k < line.Length && removed < width && (line[k] == ' ' || line[k] == '\t'))
        {
            removed += line[k] == '\t' ? 4 : 1;
            k++;
        }

        return line[k..];
    }

    /// <summary>True for a line that interrupts a paragraph by starting a different block.</summary>
    private static bool StartsBlock(string line) =>
        HeadingRegex.IsMatch(line) || FenceRegex.IsMatch(line) || AttributeLineRegex.IsMatch(line)
        || CalloutPrefixRegex.IsMatch(line) || QuotePrefixRegex.IsMatch(line) || SceneBreakRegex.IsMatch(line)
        || FootnoteDefinitionRegex.IsMatch(line) || ResourceRegex.IsMatch(line)
        || line.TrimStart().StartsWith("%%", StringComparison.Ordinal)
        || line.TrimStart().StartsWith("<!--", StringComparison.Ordinal);

    private static int ParseParagraph(IReadOnlyList<string> lines, int start, ParseContext context,
        MarkuaAlignment alignment, bool listMarkersInterrupt, List<MarkuaBlock> blocks)
    {
        // Outside lists a "- " or "1. " line stays prose (dialogue, years); inside an item it starts a nested list.
        var text = new List<string>();
        var i = start;
        while (i < lines.Count && lines[i].Trim().Length > 0
               && (i == start || !(StartsBlock(lines[i]) || (listMarkersInterrupt && ListItemRegex.IsMatch(lines[i])))))
        {
            var line = lines[i].Trim();
            if (line.EndsWith('\\'))
                line = line[..^1].TrimEnd();
            text.Add(line);
            i++;
        }

        blocks.Add(new MarkuaParagraph(ParseInlines(string.Join("\n", text), context), alignment));
        return i;
    }

    // ── Inlines ──────────────────────────────────────────────────────────────

    private static List<MarkuaRun> ParseInlines(string text, ParseContext context)
    {
        var runs = new List<MarkuaRun>();
        AppendInlines(text, MarkuaTextStyle.None, null, context, runs);
        return Merge(runs);
    }

    private static readonly (string Delimiter, MarkuaTextStyle Style)[] Delimiters =
    [
        ("***", MarkuaTextStyle.Bold | MarkuaTextStyle.Italic),
        ("___", MarkuaTextStyle.Bold | MarkuaTextStyle.Italic),
        ("**", MarkuaTextStyle.Bold),
        ("__", MarkuaTextStyle.Bold),
        ("~~", MarkuaTextStyle.Strikethrough),
        ("*", MarkuaTextStyle.Italic),
        ("_", MarkuaTextStyle.Italic),
        ("^", MarkuaTextStyle.Superscript),
        ("~", MarkuaTextStyle.Subscript),
    ];

    private static void AppendInlines(string text, MarkuaTextStyle style, string? url, ParseContext context,
        List<MarkuaRun> runs)
    {
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0)
                return;
            runs.Add(new MarkuaRun(plain.ToString(), style, url));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n')
            {
                Flush();
                runs.Add(MarkuaRun.LineBreak);
                i++;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && char.IsPunctuation(text[i + 1]) | char.IsSymbol(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var ticks = CountRun(text, i, '`');
                var close = FindBacktickClose(text, i + ticks, ticks);
                if (close >= 0)
                {
                    Flush();
                    var code = text[(i + ticks)..close].Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                        code = code[1..^1];
                    runs.Add(new MarkuaRun(code, style | MarkuaTextStyle.Code, url));
                    i = close + ticks;
                    continue;
                }

                plain.Append(text, i, ticks);
                i += ticks;
                continue;
            }

            // Footnote reference [^label]
            if (c == '[' && i + 1 < text.Length && text[i + 1] == '^')
            {
                var close = text.IndexOf(']', i + 2);
                if (close > i + 2 && !text[(i + 2)..close].Any(char.IsWhiteSpace))
                {
                    Flush();
                    var number = context.ReferenceFootnote(text[(i + 2)..close]);
                    runs.Add(new MarkuaRun(number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        style | MarkuaTextStyle.FootnoteReference | MarkuaTextStyle.Superscript, url));
                    i = close + 1;
                    continue;
                }
            }

            // Links [text](url) and inline images ![alt](url)
            if (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '['))
            {
                var isImage = c == '!';
                var open = isImage ? i + 1 : i;
                if (TryReadLink(text, open, out var label, out var target, out var end))
                {
                    Flush();
                    if (isImage)
                        runs.Add(new MarkuaRun(label.Length > 0 ? label : target, style | MarkuaTextStyle.Italic, url));
                    else
                        AppendInlines(label.Length > 0 ? label : target, style | MarkuaTextStyle.Link, target, context, runs);
                    i = end;
                    continue;
                }
            }

            if (c is '*' or '_' or '~' or '^' && TryEmphasis(text, i, out var delimiter, out var delimStyle, out var closeAt))
            {
                Flush();
                AppendInlines(text[(i + delimiter.Length)..closeAt], style | delimStyle, url, context, runs);
                i = closeAt + delimiter.Length;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
    }

    private static bool TryEmphasis(string text, int i, out string delimiter, out MarkuaTextStyle style, out int close)
    {
        foreach (var (d, s) in Delimiters)
        {
            if (string.CompareOrdinal(text, i, d, 0, d.Length) != 0)
                continue;

            var runLength = CountRun(text, i, d[0]);
            // Use the longest delimiter the run allows; a shorter one is tried only for shorter runs.
            if (runLength > d.Length && d.Length < 3 && d[0] is '*' or '_')
                continue;
            if (runLength > d.Length && d[0] is '~' or '^')
                continue;

            var after = i + d.Length;
            if (after >= text.Length || char.IsWhiteSpace(text[after]))
                continue;

            // Intraword underscores are literal (snake_case, file_names).
            if (d[0] == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]))
                continue;

            var found = FindCloser(text, after, d);
            if (found > after)
            {
                delimiter = d;
                style = s;
                close = found;
                return true;
            }
        }

        delimiter = "";
        style = MarkuaTextStyle.None;
        close = -1;
        return false;
    }

    private static int FindCloser(string text, int from, string delimiter)
    {
        var ch = delimiter[0];
        var j = from;
        while (j < text.Length)
        {
            if (text[j] == '`')
            {
                var ticks = CountRun(text, j, '`');
                var close = FindBacktickClose(text, j + ticks, ticks);
                j = close >= 0 ? close + ticks : j + ticks;
                continue;
            }

            if (text[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (text[j] != ch)
            {
                j++;
                continue;
            }

            var run = CountRun(text, j, ch);
            var candidate = j + run - delimiter.Length;
            var fits = delimiter.Length switch
            {
                3 => run >= 3,
                2 => run is 2 or 3,
                _ => run is 1 or 3,
            };

            if (fits && candidate >= from && !char.IsWhiteSpace(text[candidate - 1]))
            {
                var next = candidate + delimiter.Length;
                if (ch != '_' || next >= text.Length || !char.IsLetterOrDigit(text[next]))
                    return candidate;
            }

            j += run;
        }

        return -1;
    }

    private static bool TryReadLink(string text, int open, out string label, out string target, out int end)
    {
        label = target = "";
        end = -1;

        var depth = 0;
        var k = open;
        for (; k < text.Length; k++)
        {
            if (text[k] == '\\') { k++; continue; }
            if (text[k] == '[') depth++;
            else if (text[k] == ']' && --depth == 0) break;
        }

        if (k >= text.Length || k + 1 >= text.Length || text[k + 1] != '(')
            return false;

        var closeParen = text.IndexOf(')', k + 2);
        if (closeParen < 0)
            return false;

        label = text[(open + 1)..k];
        var inside = text[(k + 2)..closeParen].Trim();
        var space = inside.IndexOfAny([' ', '\t']);
        target = (space >= 0 ? inside[..space] : inside).Trim('<', '>');
        end = closeParen + 1;
        return target.Length > 0 || label.Length > 0;
    }

    private static int CountRun(string text, int start, char ch)
    {
        var k = start;
        while (k < text.Length && text[k] == ch)
            k++;
        return k - start;
    }

    private static int FindBacktickClose(string text, int from, int ticks)
    {
        var j = from;
        while (j < text.Length)
        {
            if (text[j] == '`')
            {
                var run = CountRun(text, j, '`');
                if (run == ticks)
                    return j;
                j += run;
            }
            else
            {
                j++;
            }
        }

        return -1;
    }

    private static List<MarkuaRun> Merge(List<MarkuaRun> runs)
    {
        var merged = new List<MarkuaRun>(runs.Count);
        foreach (var run in runs)
        {
            if (merged.Count > 0 && !run.IsLineBreak && merged[^1] is { IsLineBreak: false } last
                && last.Style == run.Style && last.Url == run.Url
                && !run.Has(MarkuaTextStyle.FootnoteReference))
            {
                merged[^1] = last with { Text = last.Text + run.Text };
                continue;
            }

            merged.Add(run);
        }

        return merged;
    }

    // ── State ────────────────────────────────────────────────────────────────

    private sealed class PendingAttributes
    {
        public bool IsPart { get; set; }
        public MarkuaAlignment Alignment { get; set; }
        public string? Format { get; set; }
    }

    private sealed class ParseContext
    {
        private readonly Dictionary<string, string> _definitions = new(StringComparer.Ordinal);
        private readonly List<string> _referenced = [];

        public void DefineFootnote(string label, string text) => _definitions.TryAdd(label, text);

        public int ReferenceFootnote(string label)
        {
            var index = _referenced.IndexOf(label);
            if (index >= 0)
                return index + 1;

            _referenced.Add(label);
            return _referenced.Count;
        }

        public IReadOnlyList<MarkuaFootnote> BuildFootnotes()
        {
            var notes = new List<MarkuaFootnote>(_referenced.Count);
            for (var k = 0; k < _referenced.Count; k++)
            {
                var label = _referenced[k];
                var runs = _definitions.TryGetValue(label, out var text)
                    ? ParseInlines(text, new ParseContext())
                    : [new MarkuaRun($"Missing footnote [^{label}]", MarkuaTextStyle.Italic)];
                notes.Add(new MarkuaFootnote(k + 1, label, runs));
            }

            return notes;
        }
    }
}

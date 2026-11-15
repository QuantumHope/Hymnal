using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hymnal.Core.Markua.Editing;

/// <summary>
/// The edits behind the Insert menu: each turns the editor's text and selection into one <see cref="TextEdit"/>.
/// Inline styles wrap or unwrap the selection (or the word under the caret); paragraph styles change the prefix
/// of every line the selection touches and toggle off when all of them already have it; blocks go on lines of
/// their own with a blank line either side, as Markua needs. Line endings follow the text's own (CRLF or LF).
/// </summary>
public static class MarkuaEdits
{
    private static readonly Regex HeadingPrefixRegex = new(@"^ {0,3}(#{1,6})(?:[ \t]+|$)", RegexOptions.Compiled);
    private static readonly Regex ListPrefixRegex = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+", RegexOptions.Compiled);
    private static readonly Regex QuoteOrCalloutPrefixRegex = new(@"^(?:[ABWTEIQDXC]>|[ ]{0,3}>)[ \t]?", RegexOptions.Compiled);
    private static readonly Regex CommentPrefixRegex = new(@"^%%[ \t]?", RegexOptions.Compiled);
    private static readonly Regex FootnoteLabelRegex = new(@"\[\^(\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// What starts a line without being part of its prose (list marker, heading hashes, quote or callout
    /// prefix), so styling a whole line styles the words after it.
    /// </summary>
    private static readonly Regex StructuralPrefixRegex = new(
        @"^(?:[ \t]*(?:[-*+]|\d{1,9}[.)])[ \t]+|[ ]{0,3}#{1,6}[ \t]+|[ABWTEIQDXC]>[ \t]?|[ ]{0,3}>[ \t]?)",
        RegexOptions.Compiled);

    // ── Inline styles ────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps the selection in <paramref name="marker"/> (<c>**</c>, <c>*</c>, <c>~~</c>, <c>`</c>, …), or unwraps
    /// it when it is already wrapped, inside or just outside the selection. With no selection, a caret inside a
    /// word styles that word; elsewhere an empty pair goes in with the caret between (and a second press takes an
    /// empty pair out again). A selection over several lines styles each line, since emphasis can't cross one.
    /// </summary>
    public static TextEdit ToggleInline(string text, TextSpan selection, string marker)
    {
        var sel = Clamp(text, selection);
        if (sel.IsEmpty)
        {
            var at = sel.Start;
            if (HasAt(text, at - marker.Length, marker) && HasAt(text, at, marker))
                return new TextEdit(at - marker.Length, marker.Length * 2, "", TextSpan.Caret(at - marker.Length));
            if (at > 0 && at < text.Length && IsWordChar(text[at - 1]) && IsWordChar(text[at]))
                sel = WordAt(text, at);
            else
                return new TextEdit(at, 0, marker + marker, TextSpan.Caret(at + marker.Length));
        }

        if (text.AsSpan(sel.Start, sel.Length).Contains('\n'))
            return ToggleInlinePerLine(text, sel, marker);

        // Spaces stay outside the markers ("**word **" is not bold), and so does a list marker or heading prefix
        // when the selection starts at the start of its line.
        var start = sel.Start;
        var end = sel.End;
        if (start == LineStart(text, start))
            start += StructuralPrefixRegex.Match(text[start..end]).Length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start == end)
            return new TextEdit(sel.End, 0, marker + marker, TextSpan.Caret(sel.End + marker.Length));

        var inner = text[start..end];
        if (IsWrapped(inner, marker))
        {
            var unwrapped = inner[marker.Length..^marker.Length];
            return new TextEdit(start, inner.Length, unwrapped, new TextSpan(start, unwrapped.Length));
        }

        if (IsSurrounded(text, start, end, marker))
            return new TextEdit(start - marker.Length, inner.Length + 2 * marker.Length, inner,
                new TextSpan(start - marker.Length, inner.Length));

        return new TextEdit(start, inner.Length, marker + inner + marker,
            new TextSpan(start + marker.Length, inner.Length));
    }

    private static TextEdit ToggleInlinePerLine(string text, TextSpan sel, string marker)
    {
        var lines = text.Substring(sel.Start, sel.Length).Split('\n');
        var parts = lines.Select(SplitStyledLine).ToList();
        var styled = parts.Where(p => p.Core.Length > 0).ToList();
        var unwrap = styled.Count > 0 && styled.All(p => IsWrapped(p.Core, marker));

        var result = string.Join("\n", parts.Select(p =>
        {
            if (p.Core.Length == 0)
                return p.Lead + p.Trail;
            var core = unwrap
                ? p.Core[marker.Length..^marker.Length]
                : IsWrapped(p.Core, marker) ? p.Core : marker + p.Core + marker;
            return p.Lead + core + p.Trail;
        }));
        return new TextEdit(sel.Start, sel.Length, result, new TextSpan(sel.Start, result.Length));
    }

    /// <summary>A line as (prefix and leading space, styled words, trailing space and any '\r').</summary>
    private static (string Lead, string Core, string Trail) SplitStyledLine(string line)
    {
        var lead = StructuralPrefixRegex.Match(line).Length;
        while (lead < line.Length && char.IsWhiteSpace(line[lead])) lead++;
        var end = line.Length;
        while (end > lead && char.IsWhiteSpace(line[end - 1])) end--;
        return (line[..lead], line[lead..end], line[end..]);
    }

    /// <summary>
    /// True when <paramref name="s"/> starts and ends with <paramref name="marker"/>. For a marker of one repeated
    /// character the run must match, so <c>**bold**</c> doesn't count as italic, though <c>***both***</c> counts
    /// as either.
    /// </summary>
    private static bool IsWrapped(string s, string marker)
    {
        if (s.Length <= 2 * marker.Length
            || !s.StartsWith(marker, StringComparison.Ordinal)
            || !s.EndsWith(marker, StringComparison.Ordinal))
            return false;
        if (!IsRepeatedChar(marker))
            return true;
        var c = marker[0];
        return RunMatches(RunLength(s, 0, 1, c), marker.Length)
            && RunMatches(RunLength(s, s.Length - 1, -1, c), marker.Length);
    }

    private static bool IsSurrounded(string text, int start, int end, string marker)
    {
        if (!HasAt(text, start - marker.Length, marker) || !HasAt(text, end, marker))
            return false;
        if (!IsRepeatedChar(marker))
            return true;
        var c = marker[0];
        return RunMatches(RunLength(text, start - 1, -1, c), marker.Length)
            && RunMatches(RunLength(text, end, 1, c), marker.Length)
            && text[start] != c && text[end - 1] != c;
    }

    private static bool RunMatches(int run, int markerLength) => run == markerLength || (markerLength < 3 && run == 3);

    private static int RunLength(string s, int from, int step, char c)
    {
        var n = 0;
        for (var i = from; i >= 0 && i < s.Length && s[i] == c; i += step)
            n++;
        return n;
    }

    private static bool IsRepeatedChar(string marker) => marker.All(ch => ch == marker[0]);

    private static bool HasAt(string text, int at, string value) =>
        at >= 0 && at + value.Length <= text.Length && string.CompareOrdinal(text, at, value, 0, value.Length) == 0;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';

    private static TextSpan WordAt(string text, int at)
    {
        var start = at;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = at;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return new TextSpan(start, end - start);
    }

    // ── Links and footnotes ─────────────────────────────────────────────────

    /// <summary>
    /// <c>[text](url)</c>: the selection becomes the link text and the URL is selected to type over; a selected
    /// URL becomes the target and the text is selected instead. With nothing selected, the text is selected.
    /// </summary>
    public static TextEdit InsertLink(string text, TextSpan selection)
    {
        const string textPlaceholder = "link text";
        const string urlPlaceholder = "https://";
        var sel = Clamp(text, selection);
        var selected = text.Substring(sel.Start, sel.Length).Trim();

        if (selected.Length == 0)
        {
            var link = $"[{textPlaceholder}]({urlPlaceholder})";
            return new TextEdit(sel.Start, sel.Length, link, new TextSpan(sel.Start + 1, textPlaceholder.Length));
        }

        if (LooksLikeUrl(selected))
        {
            var link = $"[{textPlaceholder}]({selected})";
            return new TextEdit(sel.Start, sel.Length, link, new TextSpan(sel.Start + 1, textPlaceholder.Length));
        }

        var wrapped = $"[{selected}]({urlPlaceholder})";
        return new TextEdit(sel.Start, sel.Length, wrapped,
            new TextSpan(sel.Start + selected.Length + 3, urlPlaceholder.Length));
    }

    private static bool LooksLikeUrl(string s) =>
        !s.Contains(' ')
        && (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"));

    /// <summary>
    /// A numbered footnote: <c>[^n]</c> after the selection (or at the caret), with <c>[^n]: </c> under the
    /// paragraph and its placeholder text selected. <c>n</c> is one more than the highest number already used.
    /// </summary>
    public static TextEdit InsertFootnote(string text, TextSpan selection)
    {
        const string placeholder = "Footnote text.";
        var sel = Clamp(text, selection);
        var nl = NewLineOf(text);
        var number = FootnoteLabelRegex.Matches(text)
            .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var reference = $"[^{number}]";

        var at = sel.End;
        var paragraphEnd = ParagraphEnd(text, at);
        var middle = text[at..paragraphEnd];
        var definition = $"[^{number}]: ";
        var replacement = reference + middle + nl + nl + definition + placeholder;
        var placeholderStart = at + replacement.Length - placeholder.Length;
        return new TextEdit(at, middle.Length, replacement, new TextSpan(placeholderStart, placeholder.Length));
    }

    /// <summary>End of the last line of the paragraph holding <paramref name="at"/> (before its line break).</summary>
    private static int ParagraphEnd(string text, int at)
    {
        var end = LineEnd(text, at);
        while (end < text.Length)
        {
            var next = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            var nextEnd = LineEnd(text, next);
            if (string.IsNullOrWhiteSpace(text[next..nextEnd]))
                break;
            end = nextEnd;
        }
        return end;
    }

    // ── Paragraph styles (line prefixes) ────────────────────────────────────

    /// <summary>
    /// Makes every line the selection touches a heading of <paramref name="level"/> (1 = <c>#</c>, a chapter), or
    /// plain text again when they all already are; level 0 always makes them plain text.
    /// </summary>
    public static TextEdit SetHeading(string text, TextSpan selection, int level) =>
        TransformLines(text, selection, lines =>
        {
            var content = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var toggleOff = level > 0 && content.Count > 0 && content.All(l => HeadingLevel(l) == level);
            return lines.Select(line =>
            {
                if (lines.Count > 1 && string.IsNullOrWhiteSpace(line))
                    return line;
                var body = HeadingPrefixRegex.Replace(line, "", 1);
                return toggleOff || level == 0 ? body : new string('#', level) + " " + body;
            }).ToList();
        });

    private static int HeadingLevel(string line)
    {
        var match = HeadingPrefixRegex.Match(line);
        return match.Success ? match.Groups[1].Length : 0;
    }

    /// <summary><c>&gt; </c> on each line (a block quote), replacing any callout prefix; toggles off.</summary>
    public static TextEdit ToggleBlockQuote(string text, TextSpan selection) =>
        ToggleLinePrefix(text, selection, "> ", QuoteOrCalloutPrefixRegex, prefixBlankLines: true);

    /// <summary>
    /// A Markua callout, <c>A&gt; </c> (aside), <c>W&gt; </c> (warning) and so on, on each line, replacing any
    /// other callout or quote prefix; blank lines inside get the bare prefix so the callout stays one block.
    /// </summary>
    public static TextEdit ToggleCallout(string text, TextSpan selection, char letter) =>
        ToggleLinePrefix(text, selection, letter + "> ", QuoteOrCalloutPrefixRegex, prefixBlankLines: true);

    /// <summary><c>%% </c> on each line (a Markua comment, left out of the book); toggles off.</summary>
    public static TextEdit ToggleComment(string text, TextSpan selection) =>
        ToggleLinePrefix(text, selection, "%% ", CommentPrefixRegex, prefixBlankLines: false);

    private static TextEdit ToggleLinePrefix(string text, TextSpan selection, string prefix, Regex family,
        bool prefixBlankLines) =>
        TransformLines(text, selection, lines =>
        {
            var bare = prefix.TrimEnd();
            var content = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var toggleOff = content.Count > 0 && content.All(l => l.StartsWith(bare, StringComparison.Ordinal));
            return lines.Select(line =>
            {
                if (toggleOff)
                    return family.Replace(line, "", 1);
                if (lines.Count > 1 && string.IsNullOrWhiteSpace(line))
                    return prefixBlankLines ? bare : line;
                return prefix + family.Replace(line, "", 1);
            }).ToList();
        });

    /// <summary>
    /// A bulleted (<c>- </c>) or numbered (<c>1. </c>, <c>2. </c>, …) list item on each non-blank line, keeping
    /// indentation and replacing the other kind of marker; toggles off when every line is already that kind.
    /// </summary>
    public static TextEdit ToggleList(string text, TextSpan selection, bool numbered) =>
        TransformLines(text, selection, lines =>
        {
            var content = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var toggleOff = content.Count > 0 && content.All(l => IsListItem(l, numbered));
            var number = 1;
            return lines.Select(line =>
            {
                if (lines.Count > 1 && string.IsNullOrWhiteSpace(line))
                    return line;
                var match = ListPrefixRegex.Match(line);
                var indent = match.Success ? match.Groups[1].Value : line[..(line.Length - line.TrimStart().Length)];
                var body = match.Success ? line[match.Length..] : line.TrimStart();
                if (toggleOff)
                    return indent + body;
                return indent + (numbered ? $"{number++}. " : "- ") + body;
            }).ToList();
        });

    private static bool IsListItem(string line, bool numbered)
    {
        var match = ListPrefixRegex.Match(line);
        return match.Success && char.IsDigit(match.Groups[2].Value[0]) == numbered;
    }

    /// <summary>
    /// Replaces the whole lines the selection touches with <paramref name="transform"/>'s lines. A caret keeps its
    /// place in the line's text; a selection grows to cover the changed lines.
    /// </summary>
    private static TextEdit TransformLines(string text, TextSpan selection, Func<List<string>, List<string>> transform)
    {
        var sel = Clamp(text, selection);
        var (start, end) = LinesSpan(text, sel);
        var nl = NewLineOf(text);
        var lines = text[start..end].Replace("\r\n", "\n").Split('\n').ToList();
        var changed = transform(lines);
        var replacement = string.Join(nl, changed);

        TextSpan after;
        if (sel.IsEmpty && lines.Count == 1)
        {
            var fromEnd = lines[0].Length - (sel.Start - start);
            after = TextSpan.Caret(start + Math.Clamp(changed[0].Length - fromEnd, 0, changed[0].Length));
        }
        else
        {
            after = new TextSpan(start, replacement.Length);
        }
        return new TextEdit(start, end - start, replacement, after);
    }

    // ── Blocks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts <paramref name="lines"/> on lines of their own: on the caret's line if it is blank, otherwise after
    /// the line the selection ends on, with a blank line before and after. Selects <paramref name="select"/>
    /// (its first occurrence in the block) if given; otherwise the caret goes after the block, on a new paragraph
    /// when the block ends the text.
    /// </summary>
    public static TextEdit InsertBlock(string text, TextSpan selection, IReadOnlyList<string> lines, string? select = null)
    {
        var sel = Clamp(text, selection);
        var nl = NewLineOf(text);
        var anchor = sel.IsEmpty ? sel.End : LinesSpan(text, sel).End;
        var lineStart = LineStart(text, anchor);
        var lineEnd = LineEnd(text, anchor);
        var onBlankLine = string.IsNullOrWhiteSpace(text[lineStart..lineEnd]);
        var at = onBlankLine ? lineStart : lineEnd;
        var removed = onBlankLine ? lineEnd - lineStart : 0;

        var block = string.Join(nl, lines);
        var (lead, trail) = Padding(text, at, at + removed, nl);
        var atEnd = at + removed >= text.Length;
        if (atEnd && select == null)
            trail = nl + nl;

        var replacement = lead + block + trail;
        TextSpan after;
        if (select != null && block.IndexOf(select, StringComparison.Ordinal) is var index and >= 0)
            after = new TextSpan(at + lead.Length + index, select.Length);
        else
            after = TextSpan.Caret(at + lead.Length + block.Length + (atEnd ? trail.Length : 0));
        return new TextEdit(at, removed, replacement, after);
    }

    /// <summary>
    /// Wraps the lines the selection touches between <paramref name="open"/> and <paramref name="close"/> lines (a
    /// code fence, <c>{aside}</c> … <c>{/aside}</c>), selecting them; with no selection, inserts the block around
    /// <paramref name="placeholder"/> and selects that.
    /// </summary>
    public static TextEdit WrapLines(string text, TextSpan selection, string open, string close, string placeholder)
    {
        var sel = Clamp(text, selection);
        if (sel.IsEmpty)
            return InsertBlock(text, sel, [open, placeholder, close], placeholder);

        var nl = NewLineOf(text);
        var (start, end) = LinesSpan(text, sel);
        var content = text[start..end];
        var (lead, trail) = Padding(text, start, end, nl);
        var replacement = lead + open + nl + content + nl + close + trail;
        return new TextEdit(start, end - start, replacement,
            new TextSpan(start + lead.Length + open.Length + nl.Length, content.Length));
    }

    /// <summary>Line breaks needed so a block replacing [<paramref name="from"/>, <paramref name="to"/>) has a blank line either side.</summary>
    private static (string Lead, string Trail) Padding(string text, int from, int to, string nl)
    {
        var before = 0;
        for (var i = from - 1; i >= 0 && text[i] is '\n' or '\r'; i--)
            if (text[i] == '\n') before++;
        var after = 0;
        for (var i = to; i < text.Length && text[i] is '\n' or '\r'; i++)
            if (text[i] == '\n') after++;

        var lead = from == 0 ? "" : Repeat(nl, Math.Max(0, 2 - before));
        var trail = to >= text.Length ? "" : Repeat(nl, Math.Max(0, 2 - after));
        return (lead, trail);
    }

    // ── Text helpers ────────────────────────────────────────────────────────

    private static TextSpan Clamp(string text, TextSpan span)
    {
        var start = Math.Clamp(span.Start, 0, text.Length);
        var length = Math.Clamp(span.Length, 0, text.Length - start);
        return new TextSpan(start, length);
    }

    /// <summary>The whole lines a selection touches; one ending at the very start of a line leaves that line out.</summary>
    private static (int Start, int End) LinesSpan(string text, TextSpan sel)
    {
        var start = LineStart(text, sel.Start);
        var last = sel.End;
        if (!sel.IsEmpty && last > start && LineStart(text, last) == last)
            last--;
        return (start, LineEnd(text, last));
    }

    private static int LineStart(string text, int at) =>
        at <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(at, text.Length) - 1) + 1;

    /// <summary>Offset of the line break ending the line that holds <paramref name="at"/> (its '\r' for CRLF), or the text's end.</summary>
    private static int LineEnd(string text, int at)
    {
        if (at >= text.Length)
            return text.Length;
        var newline = text.IndexOf('\n', at);
        if (newline < 0)
            return text.Length;
        return newline > 0 && text[newline - 1] == '\r' && newline - 1 >= at ? newline - 1 : newline;
    }

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string Repeat(string value, int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
            builder.Append(value);
        return builder.ToString();
    }
}

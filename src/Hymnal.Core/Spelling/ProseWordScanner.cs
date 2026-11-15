using System.Text.RegularExpressions;

namespace Hymnal.Core.Spelling;

/// <summary>A word of prose found in a Markua buffer: where it is, and the text as written.</summary>
/// <param name="Start">Offset of the first character in the scanned text.</param>
/// <param name="Length">Length in characters.</param>
/// <param name="Text">The word exactly as written (curly apostrophes and all).</param>
public readonly record struct ProseWord(int Start, int Length, string Text)
{
    public int End => Start + Length;

    /// <summary>The form handed to a dictionary: typographic apostrophes become <c>'</c>.</summary>
    public string CheckForm => ProseWordScanner.ToCheckForm(Text);
}

/// <summary>
/// Picks the words worth spell-checking out of a Markua chapter. Only prose is returned: code (fenced and
/// inline), <c>%%</c> and <c>&lt;!-- --&gt;</c> comments, attribute and directive blocks (<c>{…}</c>), URLs,
/// e-mail addresses, link targets, HTML tags, footnote markers and callout prefixes (<c>A&gt;</c>) are skipped.
/// <para>
/// Words that are rarely misspellings in a manuscript are skipped too: one letter, ALL CAPS (acronyms and
/// shouting), containing a digit (<c>3rd</c>, <c>B2B</c>) and lower-then-upper identifiers (<c>iPhone</c>).
/// Hyphenated words are checked part by part.
/// </para>
/// Pure and thread-safe, so a scan can run off the UI thread.
/// </summary>
public static class ProseWordScanner
{
    // Letters (with combining marks) and digits, joined by apostrophes inside the word ("don't", "o'clock").
    // A leading or trailing apostrophe is a quotation mark, so it stays outside the word.
    private static readonly Regex WordRegex = new(
        @"[\p{L}\p{M}\p{Nd}]+(?:['’ʼ][\p{L}\p{M}\p{Nd}]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FenceRegex = new(
        @"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Inline spans that are not prose. Order does not matter: each match only masks characters.
    private static readonly Regex[] InlineSkips =
    [
        new(@"(`+)[^`\r\n]*?\1", RegexOptions.Compiled | RegexOptions.CultureInvariant),                 // inline code
        new(@"\{[^{}\r\n]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant),                    // {attributes}
        new(@"\b(?:https?|ftp|file|mailto)://[^\s)>\]]+|\bwww\.[^\s)>\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.Compiled | RegexOptions.CultureInvariant),     // e-mail
        new(@"\]\([^)\r\n]*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant),                   // ](link target)
        new(@"</?[A-Za-z][^<>\r\n]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant),           // <tags>
        new(@"\[\^[^\]\r\n]*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant),                  // [^footnote]
        new(@"^[ \t]*[A-Z]>", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant), // A> callout
    ];

    /// <summary>The prose words of <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<ProseWord> Scan(string? text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var skip = new bool[text.Length];
        MaskBlocks(text, skip);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var regex in InlineSkips)
        {
            foreach (Match m in regex.Matches(text))
                Array.Fill(skip, true, m.Index, m.Length);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var words = new List<ProseWord>();
        foreach (Match m in WordRegex.Matches(text))
        {
            if (IsMasked(skip, m.Index, m.Length))
                continue;
            if (!IsWorthChecking(m.Value))
                continue;
            words.Add(new ProseWord(m.Index, m.Length, m.Value));
        }

        return words;
    }

    /// <summary>The form handed to a dictionary: typographic apostrophes become <c>'</c>.</summary>
    public static string ToCheckForm(string word) =>
        word.IndexOfAny(['’', 'ʼ']) < 0 ? word : word.Replace('’', '\'').Replace('ʼ', '\'');

    /// <summary>
    /// Whether a word is a candidate for the dictionary at all: two or more letters, no digits, not ALL CAPS and
    /// not a lower-then-upper identifier such as <c>iPhone</c> or <c>camelCase</c>.
    /// </summary>
    public static bool IsWorthChecking(string word)
    {
        var letters = 0;
        var upper = 0;
        var lowerSeen = false;
        var upperAfterLower = false;
        foreach (var c in word)
        {
            if (char.IsDigit(c))
                return false;
            if (!char.IsLetter(c))
                continue;
            letters++;
            if (char.IsUpper(c))
            {
                upper++;
                if (lowerSeen)
                    upperAfterLower = true;
            }
            else if (char.IsLower(c))
            {
                lowerSeen = true;
            }
        }

        if (letters < 2)
            return false;
        if (upper == letters)
            return false; // ALL CAPS
        if (upperAfterLower && char.IsLower(word[0]))
            return false; // iPhone, camelCase
        return true;
    }

    private static bool IsMasked(bool[] skip, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (skip[i])
                return true;
        }

        return false;
    }

    /// <summary>Masks code fences, <c>%%</c> comment lines and <c>&lt;!-- --&gt;</c> comments (which may span lines).</summary>
    private static void MaskBlocks(string text, bool[] skip)
    {
        string? openFence = null;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;
            var line = text.AsSpan(lineStart, lineEnd - lineStart);
            var fence = FenceRegex.Match(line.ToString());

            if (openFence is not null)
            {
                Array.Fill(skip, true, lineStart, lineEnd - lineStart);
                if (fence.Success && fence.Groups[1].Value[0] == openFence[0] && fence.Groups[1].Length >= openFence.Length)
                    openFence = null;
            }
            else if (fence.Success)
            {
                openFence = fence.Groups[1].Value;
                Array.Fill(skip, true, lineStart, lineEnd - lineStart);
            }
            else if (line.TrimStart().StartsWith("%%", StringComparison.Ordinal))
            {
                Array.Fill(skip, true, lineStart, lineEnd - lineStart);
            }

            if (newline < 0)
                break;
            lineStart = newline + 1;
        }

        for (var at = text.IndexOf("<!--", StringComparison.Ordinal); at >= 0;
             at = text.IndexOf("<!--", at, StringComparison.Ordinal))
        {
            var close = text.IndexOf("-->", at + 4, StringComparison.Ordinal);
            var end = close < 0 ? text.Length : close + 3;
            Array.Fill(skip, true, at, end - at);
            at = end;
            if (at >= text.Length)
                break;
        }
    }
}

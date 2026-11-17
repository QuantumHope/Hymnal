namespace Hymnal.Core.Services.Sprints;

/// <summary>Words added and cut between two versions of a text.</summary>
public readonly record struct WordDelta(int Added, int Cut)
{
    public int Net => Added - Cut;
    public int Touched => Added + Cut;

    public static WordDelta operator +(WordDelta a, WordDelta b) => new(a.Added + b.Added, a.Cut + b.Cut);
}

/// <summary>
/// A word-level diff for sprint stats. Words are tokenized the way <see cref="WordCountService"/> counts them
/// (whitespace-delimited, Markua <c>{…}</c> attribute lines skipped), so a diff's net always equals the change in
/// word count.
/// </summary>
public static class WordDiff
{
    /// <summary>Above this many LCS cells (after trimming the common prefix and suffix) the multiset fallback is used.</summary>
    public const long MaxLcsCells = 4_000_000;

    public static IReadOnlyList<string> Tokenize(string? content)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
            return tokens;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith('{'))
                continue;
            tokens.AddRange(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return tokens;
    }

    public static WordDelta Compute(string? before, string? after) => Compute(Tokenize(before), Tokenize(after));

    public static WordDelta Compute(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var start = 0;
        while (start < before.Count && start < after.Count && before[start] == after[start])
            start++;

        var endBefore = before.Count;
        var endAfter = after.Count;
        while (endBefore > start && endAfter > start && before[endBefore - 1] == after[endAfter - 1])
        {
            endBefore--;
            endAfter--;
        }

        var n = endBefore - start;
        var m = endAfter - start;
        if (n == 0 || m == 0)
            return new WordDelta(m, n);

        if ((long)n * m > MaxLcsCells)
            return MultisetDelta(before, start, endBefore, after, start, endAfter);

        var common = LcsLength(before, start, n, after, start, m);
        return new WordDelta(m - common, n - common);
    }

    /// <summary>Two-row LCS length over the slices before[b0..b0+n) and after[a0..a0+m).</summary>
    private static int LcsLength(IReadOnlyList<string> before, int b0, int n, IReadOnlyList<string> after, int a0, int m)
    {
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            var word = before[b0 + i - 1];
            for (var j = 1; j <= m; j++)
            {
                curr[j] = word == after[a0 + j - 1]
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    /// <summary>
    /// Counts each word's surplus (added) and deficit (cut) between the slices. It ignores order, so a moved
    /// passage counts as unchanged; the net is always exact.
    /// </summary>
    private static WordDelta MultisetDelta(IReadOnlyList<string> before, int b0, int b1,
        IReadOnlyList<string> after, int a0, int a1)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = b0; i < b1; i++)
            counts[before[i]] = counts.GetValueOrDefault(before[i]) - 1;
        for (var i = a0; i < a1; i++)
            counts[after[i]] = counts.GetValueOrDefault(after[i]) + 1;

        int added = 0, cut = 0;
        foreach (var c in counts.Values)
        {
            if (c > 0) added += c;
            else cut -= c;
        }
        return new WordDelta(added, cut);
    }
}

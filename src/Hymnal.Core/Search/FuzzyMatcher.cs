using System.Collections.Generic;

namespace Hymnal.Core.Search;

/// <summary>
/// Scores how well a typed query matches a label, as a command palette does: every character of the
/// query must appear in the label in order (case-insensitive), and matches that start words, run on
/// from the previous match or start the label score higher. A query of several words matches when each
/// word does, in any order.
/// </summary>
public static class FuzzyMatcher
{
    private const int MatchScore = 1;
    private const int ConsecutiveBonus = 5;
    private const int WordStartBonus = 8;
    private const int LabelStartBonus = 12;
    private const int GapPenalty = 1;

    /// <summary>
    /// The score of <paramref name="query"/> against <paramref name="label"/>, higher for a better match;
    /// null when it does not match. A blank query matches everything with a score of 0.
    /// </summary>
    public static int? Score(string query, string label)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(label);

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var total = 0;
        foreach (var word in words)
        {
            if (ScoreWord(word, label) is not { } score)
                return null;
            total += score;
        }
        return total;
    }

    /// <summary>
    /// The best of <paramref name="labels"/>' scores (null labels are skipped), with the first label
    /// weighted above the others so a match in a title beats one in its detail line. Null when none match.
    /// </summary>
    public static int? BestScore(string query, string primary, params string?[] secondary)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return 0;

        // Each word may match the title or a detail; a word that matches nowhere rules the entry out.
        var total = 0;
        foreach (var word in words)
        {
            int? best = ScoreWord(word, primary) is { } p ? p * 2 : null;
            foreach (var label in secondary)
            {
                if (label != null && ScoreWord(word, label) is { } s && (best == null || s > best))
                    best = s;
            }
            if (best is not { } score)
                return null;
            total += score;
        }
        return total;
    }

    private static int? ScoreWord(string word, string label)
    {
        // Greedy forward match, then a second pass from each later start of the first character keeps the
        // best: "ws" in "Write: Split" should find "W…S" at word starts, not the first 's' after the W.
        int? best = null;
        for (var start = 0; start < label.Length; start++)
        {
            if (char.ToLowerInvariant(label[start]) != char.ToLowerInvariant(word[0]))
                continue;
            if (ScoreFrom(word, label, start) is { } score && (best == null || score > best))
                best = score;
        }
        return best;
    }

    private static int? ScoreFrom(string word, string label, int start) =>
        ScoreRest(word, label, 0, previous: -1, new Dictionary<(int, int), int?>(), firstAt: start);

    /// <summary>
    /// Best score for <paramref name="word"/> from character <paramref name="wi"/> on, after a match at
    /// <paramref name="previous"/>. Each character tries the next word start holding it (so initials, "tls" →
    /// Toggle Left Sidebar, score as such) and the nearest match, keeping the better path that completes: a word
    /// start that strands the rest ("fst" → the S of "First Song") falls back to the nearest one.
    /// </summary>
    private static int? ScoreRest(string word, string label, int wi, int previous,
        Dictionary<(int, int), int?> memo, int firstAt = -1)
    {
        if (wi == word.Length)
            return 0;
        if (firstAt < 0 && memo.TryGetValue((wi, previous), out var cached))
            return cached;

        int nearest, preferred;
        if (firstAt >= 0)
        {
            nearest = preferred = firstAt;
        }
        else
        {
            var target = char.ToLowerInvariant(word[wi]);
            nearest = preferred = -1;
            for (var i = previous + 1; i < label.Length; i++)
            {
                if (char.ToLowerInvariant(label[i]) != target)
                    continue;
                if (nearest < 0)
                    nearest = i;
                if (i == previous + 1 || IsWordStart(label, i))
                {
                    preferred = i;
                    break;
                }
            }
        }

        int? best = null;
        foreach (var found in preferred == nearest ? [nearest] : new[] { preferred, nearest })
        {
            if (found < 0)
                continue;
            if (ScoreRest(word, label, wi + 1, found, memo) is not { } rest)
                continue;
            var score = rest + PointsFor(label, found, previous);
            if (best == null || score > best)
                best = score;
        }

        if (firstAt < 0)
            memo[(wi, previous)] = best;
        return best;
    }

    private static int PointsFor(string label, int found, int previous)
    {
        var score = MatchScore;
        if (found == 0)
            score += LabelStartBonus;
        else if (IsWordStart(label, found))
            score += WordStartBonus;
        if (previous >= 0 && found == previous + 1)
            score += ConsecutiveBonus;
        else if (previous >= 0)
            score -= Math.Min(found - previous - 1, 5) * GapPenalty;
        return score;
    }

    private static bool IsWordStart(string label, int index)
    {
        if (index == 0)
            return true;
        var before = label[index - 1];
        var here = label[index];
        return !char.IsLetterOrDigit(before)
               || (char.IsLower(before) && char.IsUpper(here));
    }
}

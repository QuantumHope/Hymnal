using System.Collections.Concurrent;

namespace Hymnal.Core.Spelling;

/// <summary>A misspelled word in a scanned buffer.</summary>
/// <param name="Start">Offset of the word in the scanned text.</param>
/// <param name="Length">Length of the word.</param>
/// <param name="Word">The word as written.</param>
public sealed record SpellingIssue(int Start, int Length, string Word)
{
    public int End => Start + Length;
}

/// <summary>
/// Words the writer has accepted on top of the dictionary (the project dictionary and words ignored this session).
/// <para>
/// Case follows Hunspell's personal-dictionary rule: a word entered in lower case (<c>wyvern</c>) is accepted in
/// any capitalization, while one entered with capitals (<c>Elara</c>) is accepted as written or in ALL CAPS but not
/// in lower case. A possessive (<c>Elara's</c>) is accepted when its stem is.
/// </para>
/// </summary>
public sealed class WordAllowList
{
    public static readonly WordAllowList Empty = new([]);

    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly HashSet<string> _anyCase = new(StringComparer.Ordinal);
    private readonly HashSet<string> _upper = new(StringComparer.Ordinal);

    public WordAllowList(IEnumerable<string> words)
    {
        foreach (var raw in words)
        {
            var word = ProseWordScanner.ToCheckForm(raw.Trim());
            if (word.Length == 0)
                continue;
            _exact.Add(word);
            _upper.Add(word.ToUpperInvariant());
            if (word == word.ToLowerInvariant())
                _anyCase.Add(word);
        }
    }

    public bool IsEmpty => _exact.Count == 0;

    /// <summary>Whether <paramref name="word"/> (in check form) is accepted.</summary>
    public bool Contains(string word)
    {
        if (IsEmpty)
            return false;
        if (Matches(word))
            return true;

        var stem = PossessiveStem(word);
        return stem is not null && Matches(stem);
    }

    private bool Matches(string word) =>
        _exact.Contains(word)
        || _anyCase.Contains(word.ToLowerInvariant())
        || (word == word.ToUpperInvariant() && _upper.Contains(word));

    internal static string? PossessiveStem(string word)
    {
        if (word.Length > 2 && (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("'S", StringComparison.Ordinal)))
            return word[..^2];
        if (word.Length > 1 && word.EndsWith('\''))
            return word[..^1];
        return null;
    }
}

/// <summary>
/// Checks prose against a dictionary plus the writer's accepted words. An instance is a snapshot: when the
/// dictionary or accepted words change, make a new one. Results are cached per word, and it is safe to use from
/// several threads at once.
/// </summary>
public sealed class SpellChecker
{
    public const int DefaultSuggestionCount = 6;

    private readonly IWordChecker _dictionary;
    private readonly WordAllowList _allowed;
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.Ordinal);

    public SpellChecker(IWordChecker dictionary, WordAllowList? allowed = null)
    {
        _dictionary = dictionary;
        _allowed = allowed ?? WordAllowList.Empty;
    }

    /// <summary>Whether <paramref name="word"/> (as written) is spelled right.</summary>
    public bool IsCorrect(string word)
    {
        var form = ProseWordScanner.ToCheckForm(word);
        return _cache.GetOrAdd(form, Lookup);
    }

    private bool Lookup(string form)
    {
        if (_allowed.Contains(form))
            return true;
        if (_dictionary.Check(form))
            return true;

        // A possessive of an accepted word the dictionary has no affix for ("Kubernetes'").
        var stem = WordAllowList.PossessiveStem(form);
        return stem is not null && _dictionary.Check(stem);
    }

    /// <summary>The misspelled prose words in <paramref name="text"/>, in order.</summary>
    public IReadOnlyList<SpellingIssue> FindIssues(string? text, CancellationToken cancellationToken = default)
    {
        var issues = new List<SpellingIssue>();
        var words = ProseWordScanner.Scan(text, cancellationToken);
        for (var i = 0; i < words.Count; i++)
        {
            if ((i & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var word = words[i];
            if (!IsCorrect(word.Text))
                issues.Add(new SpellingIssue(word.Start, word.Length, word.Text));
        }

        return issues;
    }

    /// <summary>
    /// Replacements for <paramref name="word"/>, best first. Typographic apostrophes in the word are kept in the
    /// suggestions, so accepting one doesn't change the writer's quote style.
    /// </summary>
    public IReadOnlyList<string> Suggest(string word, int max = DefaultSuggestionCount)
    {
        var form = ProseWordScanner.ToCheckForm(word);
        var curly = word.Contains('’');
        try
        {
            return _dictionary.Suggest(form)
                .Select(s => curly ? s.Replace('\'', '’') : s)
                .Where(s => !string.Equals(s, word, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }
        catch (Exception)
        {
            // Suggestions are a convenience; a dictionary quirk must not break the menu.
            return [];
        }
    }
}

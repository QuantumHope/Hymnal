using System.Text;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;

namespace Hymnal.Core.Spelling;

/// <summary>
/// The project dictionary: words this manuscript accepts on top of the language dictionary (character and place
/// names, invented words). Stored in <c>.hymnal-data/dictionary.txt</c>, one word per line, sorted, with
/// <c>#</c> comment lines, so it reads and merges cleanly in Git and travels with the manuscript.
/// <para>
/// Changes reread the file first and write it back atomically through <see cref="IMetadataStore"/>, so a word
/// added by hand or by a pull since the last load is kept.
/// </para>
/// </summary>
public sealed class ProjectDictionaryService
{
    public const string FileName = "dictionary.txt";

    /// <summary>The longest word the dictionary takes.</summary>
    public const int MaxWordLength = 100;

    internal const string Header =
        "# Hymnal project dictionary: words spell check accepts in this manuscript.\n" +
        "# One word per line. A lower-case word matches any capitalization; a capitalized one (a name) does not\n" +
        "# match in lower case. Lines starting with # are ignored.\n";

    private readonly IMetadataStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectDictionaryService(IMetadataStore store)
    {
        _store = store;
    }

    public static string PathFor(string workspaceRoot) => Path.Combine(workspaceRoot, ".hymnal-data", FileName);

    /// <summary>The project's words, sorted; empty when the file doesn't exist yet.</summary>
    public async Task<Result<IReadOnlyList<string>>> LoadAsync(string workspaceRoot)
    {
        try
        {
            var path = PathFor(workspaceRoot);
            if (!File.Exists(path))
                return Result<IReadOnlyList<string>>.Ok([]);
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return Result<IReadOnlyList<string>>.Ok(Parse(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<string>>.Fail($"The project dictionary could not be read: {ex.Message}");
        }
    }

    /// <summary>Adds <paramref name="word"/> and returns the project's words after the change.</summary>
    public Task<Result<IReadOnlyList<string>>> AddAsync(string workspaceRoot, string word)
    {
        var problem = Validate(word);
        if (problem is not null)
            return Task.FromResult(Result<IReadOnlyList<string>>.Fail(problem));
        var trimmed = word.Trim();
        return ChangeAsync(workspaceRoot, words => words.Add(trimmed));
    }

    /// <summary>Removes <paramref name="word"/> (exact match) and returns the project's words after the change.</summary>
    public Task<Result<IReadOnlyList<string>>> RemoveAsync(string workspaceRoot, string word) =>
        ChangeAsync(workspaceRoot, words => words.Remove(word.Trim()));

    /// <summary>Why <paramref name="word"/> can't go in the dictionary; null when it can.</summary>
    public static string? Validate(string? word)
    {
        var trimmed = word?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "Enter a word to add.";
        if (trimmed.Length > MaxWordLength)
            return $"Words can be at most {MaxWordLength} characters.";
        if (trimmed.Any(char.IsWhiteSpace))
            return "Add one word at a time (no spaces).";
        if (trimmed.StartsWith('#'))
            return "A word can't start with #.";
        return null;
    }

    /// <summary>The words in a dictionary file: one per line, comments and blanks skipped, de-duplicated, sorted.</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        Sorted(text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')));

    /// <summary>The file text for <paramref name="words"/>: the header, then one word per line, sorted.</summary>
    public static string Format(IEnumerable<string> words)
    {
        var text = new StringBuilder(Header);
        foreach (var word in Sorted(words))
            text.Append(word).Append('\n');
        return text.ToString();
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> words) =>
        words.Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w, StringComparer.Ordinal)
            .ToList();

    private async Task<Result<IReadOnlyList<string>>> ChangeAsync(string workspaceRoot, Func<HashSet<string>, bool> change)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(workspaceRoot).ConfigureAwait(false);
            if (!loaded.IsSuccess)
                return loaded;

            var words = new HashSet<string>(loaded.Value!, StringComparer.Ordinal);
            if (!change(words))
                return loaded;

            await _store.WriteTextAtomicAsync(PathFor(workspaceRoot), Format(words)).ConfigureAwait(false);
            return Result<IReadOnlyList<string>>.Ok(Sorted(words));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<string>>.Fail($"The project dictionary could not be saved: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}

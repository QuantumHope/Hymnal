namespace Hymnal.Core.Services.Runs;

/// <summary>What one chapter contributed to a run.</summary>
public sealed record RunChapterChange(string Key, string Title, WordDelta Delta);

/// <summary>A run's word changes: per chapter (only chapters with changes) and in total.</summary>
public sealed record RunSummary(IReadOnlyList<RunChapterChange> Chapters, WordDelta Total)
{
    public static RunSummary Empty { get; } = new([], default);
}

/// <summary>
/// Follows the text of every chapter edited during a run. The first time a chapter is seen its text is the
/// baseline; later observations replace its latest text. Pure and single-threaded: the caller decides which edits
/// belong to which chapter.
/// </summary>
public sealed class RunTracker
{
    private sealed class Entry
    {
        public required string Title { get; set; }
        public required string Baseline { get; init; }
        public required int BaselineCount { get; init; }
        public required string Latest { get; set; }
        public required int LatestCount { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly WordCountService _counter;

    public RunTracker(WordCountService counter)
    {
        _counter = counter;
    }

    /// <summary>Net words across every tracked chapter so far (cheap: word counts only).</summary>
    public int LiveNet { get; private set; }

    /// <summary>The key of the chapter most recently observed.</summary>
    public string? LastKey { get; private set; }

    public bool IsTracking(string key) => _entries.ContainsKey(key);

    /// <summary>Records <paramref name="text"/> as the chapter's baseline (first sight) or its latest text.</summary>
    public void Observe(string key, string title, string text)
    {
        LastKey = key;
        var count = _counter.CountWords(text);
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = new Entry
            {
                Title = title,
                Baseline = text,
                BaselineCount = count,
                Latest = text,
                LatestCount = count,
            };
            _order.Add(key);
            return;
        }

        entry.Title = title;
        LiveNet += count - entry.LatestCount;
        entry.Latest = text;
        entry.LatestCount = count;
    }

    /// <summary>The latest text seen for a chapter, or null if it is not tracked.</summary>
    public string? LatestText(string key) => _entries.TryGetValue(key, out var e) ? e.Latest : null;

    public string? TitleOf(string key) => _entries.TryGetValue(key, out var e) ? e.Title : null;

    /// <summary>Runs the word diff for each tracked chapter. Chapters with no change are left out.</summary>
    public RunSummary Summarize()
    {
        var chapters = new List<RunChapterChange>();
        WordDelta total = default;
        foreach (var key in _order)
        {
            var e = _entries[key];
            if (string.Equals(e.Baseline, e.Latest, StringComparison.Ordinal))
                continue;
            var delta = WordDiff.Compute(e.Baseline, e.Latest);
            if (delta.Touched == 0)
                continue;
            chapters.Add(new RunChapterChange(key, e.Title, delta));
            total += delta;
        }
        return new RunSummary(chapters, total);
    }

    /// <summary>The last non-blank line of a text, trimmed and shortened for display.</summary>
    public static string? LastLine(string? text, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('{') || line.StartsWith("<!--", StringComparison.Ordinal))
                continue;
            return line.Length <= maxLength ? line : "…" + line[^(maxLength - 1)..];
        }
        return null;
    }
}

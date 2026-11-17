using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Spelling;
using ReactiveUI;

namespace Hymnal.Spelling;

/// <summary>How far the spelling dictionary has got.</summary>
public enum SpellingStatus
{
    /// <summary>Not needed yet: nothing has asked for a check, so no dictionary has been read.</summary>
    Idle,
    Loading,
    Ready,

    /// <summary>No dictionary is installed, or the chosen one could not be read (see <see cref="SpellingService.StatusMessage"/>).</summary>
    Unavailable,
}

/// <summary>
/// The app's spell checker: the chosen language dictionary, the open workspace's project dictionary
/// (<see cref="ProjectDictionaryService"/>) and words ignored this session, combined into one
/// <see cref="Core.Spelling.SpellChecker"/> snapshot that is replaced whenever any of them changes.
/// <para>
/// The dictionary is read only when something first needs it (<see cref="EnsureLoadedAsync"/>), so a writer
/// who never uses spell check pays nothing at start-up.
/// </para>
/// </summary>
public sealed class SpellingService : ReactiveObject, IDisposable
{
    private readonly ProjectDictionaryService _projectDictionary;
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly IDisposable _languageSub;
    private IWordChecker? _words;
    private Task<SpellChecker?>? _loading;
    private string? _loadingLanguage;
    private int _workspaceGeneration;

    private SpellChecker? _checker;
    private SpellingStatus _status = SpellingStatus.Idle;
    private string? _statusMessage;
    private DictionaryInfo? _dictionary;
    private IReadOnlyList<string> _projectWords = [];
    private string? _workspaceRoot;

    public SpellingService(DictionaryCatalog catalog, ProjectDictionaryService projectDictionary, SpellCheckPreferences preferences)
    {
        Catalog = catalog;
        _projectDictionary = projectDictionary;
        Preferences = preferences;

        // A new language replaces the dictionary, but only once one has been wanted.
        _languageSub = preferences.WhenAnyValue(p => p.Language)
            .Skip(1)
            .DistinctUntilChanged(StringComparer.OrdinalIgnoreCase)
            .Subscribe(language =>
            {
                if (Status != SpellingStatus.Idle)
                    _ = ReloadDictionaryAsync();
            });
    }

    public DictionaryCatalog Catalog { get; }

    public SpellCheckPreferences Preferences { get; }

    /// <summary>The current checker; null until a dictionary is loaded, or when none is available.</summary>
    public SpellChecker? Checker
    {
        get => _checker;
        private set => this.RaiseAndSetIfChanged(ref _checker, value);
    }

    public SpellingStatus Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>Why spell check can't run (no dictionary, a damaged one); null otherwise.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    /// <summary>The dictionary in use; null until one is loaded.</summary>
    public DictionaryInfo? Dictionary
    {
        get => _dictionary;
        private set => this.RaiseAndSetIfChanged(ref _dictionary, value);
    }

    /// <summary>The open workspace's project dictionary, sorted.</summary>
    public IReadOnlyList<string> ProjectWords
    {
        get => _projectWords;
        private set => this.RaiseAndSetIfChanged(ref _projectWords, value);
    }

    /// <summary>The open workspace; null when none is open (words can then only be ignored, not added).</summary>
    public string? WorkspaceRoot
    {
        get => _workspaceRoot;
        private set => this.RaiseAndSetIfChanged(ref _workspaceRoot, value);
    }

    /// <summary>The message shown when no dictionary is installed at all.</summary>
    public string NoDictionaryMessage =>
        Catalog.UserFolder is { } folder
            ? $"No spelling dictionary is installed. Put a Hunspell .dic and .aff pair in {folder}."
            : "No spelling dictionary is installed.";

    /// <summary>
    /// Follows the open workspace: reads its project dictionary and forgets words ignored in the last one.
    /// </summary>
    public async Task SetWorkspaceAsync(string? workspaceRoot)
    {
        workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot;
        if (string.Equals(WorkspaceRoot, workspaceRoot, StringComparison.Ordinal))
            return;

        var generation = ++_workspaceGeneration;
        WorkspaceRoot = workspaceRoot;
        _ignored.Clear();

        IReadOnlyList<string> words = [];
        if (workspaceRoot is not null)
        {
            var loaded = await _projectDictionary.LoadAsync(workspaceRoot).ConfigureAwait(true);
            if (loaded.IsSuccess)
                words = loaded.Value!;
        }

        if (generation != _workspaceGeneration)
            return; // another workspace opened meanwhile

        ProjectWords = words;
        Rebuild();
    }

    /// <summary>Re-reads the project dictionary (after a pull, or an edit made outside Hymnal).</summary>
    public async Task ReloadProjectWordsAsync()
    {
        if (WorkspaceRoot is not { } root)
            return;
        var loaded = await _projectDictionary.LoadAsync(root).ConfigureAwait(true);
        if (loaded.IsSuccess && root == WorkspaceRoot)
        {
            ProjectWords = loaded.Value!;
            Rebuild();
        }
    }

    /// <summary>Loads the dictionary if it hasn't been, and returns the checker (null when none is available).</summary>
    public Task<SpellChecker?> EnsureLoadedAsync()
    {
        if (Checker is not null)
            return Task.FromResult<SpellChecker?>(Checker);
        // A load still running is shared; a failed one is retried, since the writer may have installed a
        // dictionary since (the "no dictionary" message tells them where to put one).
        if (_loading is not null && !_loading.IsCompleted
            && string.Equals(_loadingLanguage, Preferences.Language, StringComparison.OrdinalIgnoreCase))
            return _loading;
        return ReloadDictionaryAsync();
    }

    /// <summary>Accepts <paramref name="word"/> until the workspace closes or Hymnal exits.</summary>
    public void Ignore(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;
        if (_ignored.Add(word.Trim()))
            Rebuild();
    }

    /// <summary>Adds <paramref name="word"/> to the open workspace's project dictionary.</summary>
    public async Task<Result<Unit>> AddToProjectAsync(string word)
    {
        if (WorkspaceRoot is not { } root)
            return Result<Unit>.Fail("Open a workspace to keep a project dictionary.");
        var changed = await _projectDictionary.AddAsync(root, word).ConfigureAwait(true);
        return Apply(root, changed);
    }

    /// <summary>Removes <paramref name="word"/> from the open workspace's project dictionary.</summary>
    public async Task<Result<Unit>> RemoveFromProjectAsync(string word)
    {
        if (WorkspaceRoot is not { } root)
            return Result<Unit>.Fail("No workspace is open.");
        var changed = await _projectDictionary.RemoveAsync(root, word).ConfigureAwait(true);
        return Apply(root, changed);
    }

    public void Dispose() => _languageSub.Dispose();

    private Result<Unit> Apply(string root, Result<IReadOnlyList<string>> changed)
    {
        if (!changed.IsSuccess)
            return Result<Unit>.Fail(changed.Error ?? "The project dictionary could not be changed.");
        if (root == WorkspaceRoot)
        {
            // These words were read after any load SetWorkspaceAsync still has running, so they win over it.
            _workspaceGeneration++;
            ProjectWords = changed.Value!;
            Rebuild();
        }

        return Result<Unit>.Ok(Unit.Default);
    }

    private Task<SpellChecker?> ReloadDictionaryAsync()
    {
        var language = Preferences.Language;
        _loadingLanguage = language;
        var task = LoadDictionaryAsync(language);
        _loading = task;
        return task;
    }

    private async Task<SpellChecker?> LoadDictionaryAsync(string language)
    {
        Status = SpellingStatus.Loading;
        StatusMessage = null;

        var info = Catalog.Resolve(language);
        Result<IWordChecker> loaded = info is null
            ? Result<IWordChecker>.Fail(NoDictionaryMessage)
            : await Catalog.LoadAsync(info).ConfigureAwait(true);

        // A newer language choice took over while this one was reading.
        if (!string.Equals(_loadingLanguage, language, StringComparison.OrdinalIgnoreCase))
            return await (_loading ?? Task.FromResult<SpellChecker?>(null)).ConfigureAwait(true);

        if (!loaded.IsSuccess)
        {
            _words = null;
            Dictionary = null;
            Checker = null;
            StatusMessage = loaded.Error;
            Status = SpellingStatus.Unavailable;
            return null;
        }

        _words = loaded.Value;
        Dictionary = info;
        Status = SpellingStatus.Ready;
        Rebuild();
        return Checker;
    }

    /// <summary>Replaces the checker snapshot after the dictionary or the accepted words change.</summary>
    private void Rebuild()
    {
        if (_words is null)
            return;
        Checker = new SpellChecker(_words, new WordAllowList(ProjectWords.Concat(_ignored)));
    }
}

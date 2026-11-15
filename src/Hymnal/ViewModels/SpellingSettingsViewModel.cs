using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Spelling;
using Hymnal.Spelling;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Settings → Spelling: when to check (as you type, and whether Focus Mode hides the marks), which dictionary
/// to check against, and the open workspace's project dictionary (add and remove words).
/// </summary>
public sealed class SpellingSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SpellingService _service;
    private readonly ObservableAsPropertyHelper<IReadOnlyList<string>> _projectWords;
    private readonly ObservableAsPropertyHelper<bool> _hasWorkspace;
    private readonly ObservableAsPropertyHelper<string?> _statusMessage;
    private DictionaryInfo? _selectedLanguage;
    private string _newWord = "";
    private string? _wordError;
    private bool _syncing;

    public SpellingSettingsViewModel(SpellingService service)
    {
        _service = service;
        Preferences = service.Preferences;
        Languages = service.Catalog.Available();
        _selectedLanguage = service.Catalog.Resolve(Preferences.Language);

        Disposables.Add(Preferences.WhenAnyValue(p => p.Language).Subscribe(language =>
        {
            _syncing = true;
            try { SelectedLanguage = Languages.FirstOrDefault(l => l == service.Catalog.Resolve(language)); }
            finally { _syncing = false; }
        }));

        _projectWords = service.WhenAnyValue(s => s.ProjectWords).ToProperty(this, x => x.ProjectWords);
        Disposables.Add(_projectWords);
        _hasWorkspace = service.WhenAnyValue(s => s.WorkspaceRoot).Select(root => root is not null)
            .ToProperty(this, x => x.HasWorkspace);
        Disposables.Add(_hasWorkspace);
        _statusMessage = service.WhenAnyValue(s => s.StatusMessage).ToProperty(this, x => x.StatusMessage);
        Disposables.Add(_statusMessage);
        Disposables.Add(this.WhenAnyValue(x => x.ProjectWords, x => x.HasWorkspace).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(HasProjectWords));
            this.RaisePropertyChanged(nameof(ShowNoProjectWords));
        }));

        var canAdd = this.WhenAnyValue(x => x.NewWord, x => x.HasWorkspace,
            (word, workspace) => workspace && ProjectDictionaryService.Validate(word) is null);
        AddWordCommand = ReactiveCommand.CreateFromTask(AddWordAsync, canAdd);
        RemoveWordCommand = ReactiveCommand.CreateFromTask<string>(RemoveWordAsync);
        ReloadProjectWordsCommand = ReactiveCommand.CreateFromTask(service.ReloadProjectWordsAsync);
    }

    public SpellCheckPreferences Preferences { get; }

    /// <summary>The installed dictionaries.</summary>
    public IReadOnlyList<DictionaryInfo> Languages { get; }

    public bool HasLanguages => Languages.Count > 0;

    /// <summary>Shown when no dictionary is installed.</summary>
    public string NoDictionaryMessage => _service.NoDictionaryMessage;

    /// <summary>Where extra Hunspell dictionaries can be added.</summary>
    public string? UserDictionaryFolder => _service.Catalog.UserFolder;

    /// <summary>The dictionary checked against. Choosing one saves it and reloads spell check.</summary>
    public DictionaryInfo? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (ReferenceEquals(_selectedLanguage, value))
                return;
            this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
            if (!_syncing && value is not null)
                Preferences.Language = value.Id;
        }
    }

    /// <summary>A problem with the dictionary in use (it could not be read); null when there is none.</summary>
    public string? StatusMessage => _statusMessage.Value;

    public IReadOnlyList<string> ProjectWords => _projectWords.Value;

    public bool HasWorkspace => _hasWorkspace.Value;

    /// <summary>The project dictionary has words to list.</summary>
    public bool HasProjectWords => HasWorkspace && ProjectWords.Count > 0;

    /// <summary>A workspace is open but its project dictionary is empty.</summary>
    public bool ShowNoProjectWords => HasWorkspace && ProjectWords.Count == 0;

    /// <summary>The word typed into the "add a word" box.</summary>
    public string NewWord
    {
        get => _newWord;
        set
        {
            this.RaiseAndSetIfChanged(ref _newWord, value ?? "");
            WordError = null;
        }
    }

    /// <summary>Why the last add or remove failed; null when it worked.</summary>
    public string? WordError
    {
        get => _wordError;
        private set => this.RaiseAndSetIfChanged(ref _wordError, value);
    }

    public ReactiveCommand<Unit, Unit> AddWordCommand { get; }

    public ReactiveCommand<string, Unit> RemoveWordCommand { get; }

    /// <summary>Re-reads <c>.hymnal-data/dictionary.txt</c> (after editing it by hand or pulling).</summary>
    public ReactiveCommand<Unit, Unit> ReloadProjectWordsCommand { get; }

    public void Dispose() => Disposables.Dispose();

    private async Task AddWordAsync()
    {
        var added = await _service.AddToProjectAsync(NewWord);
        if (added.IsSuccess)
            NewWord = "";
        else
            WordError = added.Error;
    }

    private async Task RemoveWordAsync(string word)
    {
        var removed = await _service.RemoveFromProjectAsync(word);
        WordError = removed.IsSuccess ? null : removed.Error;
    }
}

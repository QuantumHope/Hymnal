using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Submission;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Backs the "Export for submission" dialog: the story to export (a Short Story project's whole story, or the
/// anthology story a sidebar entry belongs to), its word count as a manuscript states it, and the author's
/// name and contact details, which are kept in the app settings for the next story. Exports a Standard
/// Manuscript Format .docx through <see cref="SubmissionExportService"/>.
/// </summary>
public sealed class SubmissionExportViewModel : ViewModelBase
{
    /// <summary>App settings key for the folder the last manuscript was saved in.</summary>
    public const string FolderSettingsKey = "submissionFolder";

    private readonly WorkspaceViewModel _workspace;
    private readonly SubmissionExportService _service;
    private readonly IAppSettingsStore _settings;
    private readonly ISaveFilePickerService _picker;
    private readonly INotificationService _notifications;
    private readonly Func<Task<bool>>? _flushEdits;
    private readonly EditorViewModel? _editor;

    private SubmissionDraft? _draft;
    private string _title = "";
    private string _legalName = "";
    private string _byline = "";
    private string _surname = "";
    private string _contact = "";
    private bool _useCourier;
    private string _wordCountDescription = "";
    private string _summary = "";
    private string? _lastExportPath;
    private bool _isBusy;

    public SubmissionExportViewModel(
        WorkspaceViewModel workspace,
        SubmissionExportService service,
        IAppSettingsStore settings,
        ISaveFilePickerService picker,
        INotificationService notifications,
        Func<Task<bool>>? flushEdits = null,
        EditorViewModel? editor = null)
    {
        _workspace = workspace;
        _service = service;
        _settings = settings;
        _picker = picker;
        _notifications = notifications;
        _flushEdits = flushEdits;
        _editor = editor;
    }

    /// <summary>Submission export is for short fiction: Short Story and Anthology projects.</summary>
    public bool IsAvailable =>
        _workspace.HasWorkspace && _workspace.Profile.Kind is ProjectKind.Story or ProjectKind.Anthology;

    public string Title
    {
        get => _title;
        set
        {
            this.RaiseAndSetIfChanged(ref _title, value);
            this.RaisePropertyChanged(nameof(HeaderPreview));
        }
    }

    public string LegalName
    {
        get => _legalName;
        set
        {
            this.RaiseAndSetIfChanged(ref _legalName, value);
            RaiseAuthorDerived();
        }
    }

    public string Byline
    {
        get => _byline;
        set
        {
            this.RaiseAndSetIfChanged(ref _byline, value);
            RaiseAuthorDerived();
        }
    }

    public string Surname
    {
        get => _surname;
        set
        {
            this.RaiseAndSetIfChanged(ref _surname, value);
            RaiseAuthorDerived();
        }
    }

    public string Contact
    {
        get => _contact;
        set => this.RaiseAndSetIfChanged(ref _contact, value);
    }

    public bool UseCourier
    {
        get => _useCourier;
        set => this.RaiseAndSetIfChanged(ref _useCourier, value);
    }

    /// <summary>"about 3,400 words", as the first page states it.</summary>
    public string WordCountDescription
    {
        get => _wordCountDescription;
        private set => this.RaiseAndSetIfChanged(ref _wordCountDescription, value);
    }

    /// <summary>"Short story · 3,412 words · 2 scenes".</summary>
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    /// <summary>Hint text for the byline and surname boxes, showing what a blank box stands for.</summary>
    public string BylinePlaceholder => string.IsNullOrWhiteSpace(LegalName) ? "Same as legal name" : LegalName.Trim();

    public string SurnamePlaceholder => Author.EffectiveSurname is { Length: > 0 } s ? s : "Last word of the byline";

    /// <summary>The page-2 header the manuscript will carry, e.g. "Eland / Grounds for Termination / 2".</summary>
    public string HeaderPreview =>
        (Author.EffectiveSurname is { Length: > 0 } s ? s + " / " : "") + Title.Trim() + " / 2";

    public string? LastExportPath
    {
        get => _lastExportPath;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lastExportPath, value);
            this.RaisePropertyChanged(nameof(HasExported));
        }
    }

    public bool HasExported => LastExportPath != null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public AuthorProfile Author => new()
    {
        LegalName = LegalName.Trim(),
        Byline = Byline.Trim(),
        Surname = Surname.Trim(),
        Contact = Contact,
        Font = UseCourier ? ManuscriptFont.CourierNew : ManuscriptFont.TimesNewRoman,
    };

    /// <summary>
    /// Gets the dialog ready for the story <paramref name="from"/> belongs to (null: the story open in the
    /// editor, or a Short Story project's only story). Pending edits are saved first, so the export matches
    /// what is on screen. False (with a message shown) when there is nothing to export.
    /// </summary>
    public async Task<bool> PrepareAsync(ChapterViewModel? from = null)
    {
        if (!IsAvailable)
            return false;

        if (_flushEdits != null)
            await _flushEdits();
        if (_editor?.IsDirty == true)
            await _editor.SaveAsync();

        var story = ResolveStory(from);
        if (story == null)
        {
            _notifications.ShowInfo("Select a story in the sidebar to export it for submission.");
            return false;
        }

        var read = await _service.ReadAsync(_workspace.ManuscriptRoot, story.Value.Paths);
        if (!read.IsSuccess)
        {
            _notifications.ShowError(read.Error!);
            return false;
        }

        _draft = read.Value!;
        Title = story.Value.Title;
        WordCountDescription = _draft.WordCountDescription;
        var scenes = story.Value.Paths.Count;
        Summary = $"{_draft.Category} · {_draft.WordCount:N0} words"
                  + (scenes > 1 ? $" · {scenes} scenes" : "");
        LastExportPath = null;
        await LoadAuthorAsync();
        return true;
    }

    /// <summary>
    /// Asks where to save, then writes the manuscript there. The author details are remembered for next time.
    /// Returns the saved path, or null when cancelled or failed (failures are shown).
    /// </summary>
    public async Task<string?> ExportAsync()
    {
        if (_draft == null || IsBusy)
            return null;

        IsBusy = true;
        try
        {
            var author = Author;
            await SaveAuthorAsync(author);

            var folder = await GetSettingAsync<string>(FolderSettingsKey) is { Length: > 0 } saved && Directory.Exists(saved)
                ? saved
                : _workspace.WorkspaceRoot;
            var path = await _picker.PickSaveFileAsync(
                SubmissionExportService.SuggestedFileName(Title, author), folder, "Word document", "docx");
            if (path == null)
                return null;

            var result = await _service.ExportAsync(new ManuscriptRequest(Title.Trim(), author, _draft.Scenes), path);
            if (!result.IsSuccess)
            {
                _notifications.ShowError(result.Error!);
                return null;
            }

            LastExportPath = result.Value;
            await SetSettingAsync(FolderSettingsKey, Path.GetDirectoryName(result.Value) ?? "");
            return result.Value;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (string Title, IReadOnlyList<string> Paths)? ResolveStory(ChapterViewModel? from)
    {
        var nodes = _workspace.Nodes.ToList();
        // A manuscript is every scene Book.txt lists, missing files included, so a missing one fails the export
        // with its name rather than quietly leaving the story incomplete.
        if (_workspace.Profile.Kind == ProjectKind.Story)
        {
            var whole = StoryStructure.ShortStory(nodes);
            var title = !string.IsNullOrWhiteSpace(_workspace.CollectionTitle) ? _workspace.CollectionTitle : whole.Title;
            var files = StoryStructure.ListedChapters(nodes).Select(n => n.Node.RelativePath).ToList();
            return files.Count == 0 ? null : (title, files);
        }

        var anchor = from ?? _workspace.FocusedStory ?? _workspace.SelectedNode;
        if (anchor == null)
            return null;
        var story = StoryStructure.AnthologyStoryOf(nodes, anchor);
        if (story == null)
            return null;
        var paths = StoryStructure.ListedStoryScenes(nodes, story).Select(s => s.Node.RelativePath).ToList();
        return paths.Count == 0 ? null : (story.Node.Title, paths);
    }

    private async Task LoadAuthorAsync()
    {
        var author = await GetSettingAsync<AuthorProfile>(AuthorProfile.SettingsKey) ?? new AuthorProfile();
        LegalName = author.LegalName;
        Byline = author.Byline;
        Surname = author.Surname;
        Contact = author.Contact;
        UseCourier = author.Font == ManuscriptFont.CourierNew;
    }

    private Task SaveAuthorAsync(AuthorProfile author) => SetSettingAsync(AuthorProfile.SettingsKey, author);

    private async Task<T?> GetSettingAsync<T>(string key)
    {
        try
        {
            return await _settings.GetAsync<T>(key);
        }
        catch (Exception)
        {
            // An unreadable value is treated as unset; the dialog still works.
            return default;
        }
    }

    private async Task SetSettingAsync<T>(string key, T value)
    {
        try
        {
            await _settings.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            _notifications.ShowInfo($"Your author details couldn't be remembered: {ex.Message}");
        }
    }

    private void RaiseAuthorDerived()
    {
        this.RaisePropertyChanged(nameof(BylinePlaceholder));
        this.RaisePropertyChanged(nameof(SurnamePlaceholder));
        this.RaisePropertyChanged(nameof(HeaderPreview));
    }
}

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Models.Ai;
using Hymnal.Core.Services;
using Hymnal.Core.Services.Ai;
using Hymnal.ViewModels.Ai;
using ReactiveUI;
using Unit = System.Reactive.Unit;

namespace Hymnal.ViewModels;

/// <summary>
/// Settings surface view-model. Loaded fresh each time Settings opens (AddTransient).
/// Manages provider profile list; calls AiChatService.RefreshClientAsync on save/delete/activate.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly IProviderProfileService _profileService;
    private readonly ICredentialStore _credentials;
    private readonly IAiChatService _aiChatService;
    private readonly INotificationService _notifications;
    private readonly WorkspaceViewModel? _workspace;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly IBeatBoardService? _beatBoards;
    private readonly BeatBoardViewModel? _beatBoard;
    private readonly StorageMigrator? _storageMigrator;
    private readonly Func<Task<bool>>? _flushStoryEdits;
    private readonly ObservableAsPropertyHelper<string?> _projectChangeSummary;
    private readonly ObservableAsPropertyHelper<bool> _isStorageOptionVisible;
    private readonly ObservableAsPropertyHelper<bool> _isStorageOptionEnabled;
    private readonly ObservableAsPropertyHelper<string?> _storageOptionExplanation;

    /// <summary>Impact summary when "Keep everything in the story file" is turned on.</summary>
    public const string MoveIntoFileSummary =
        "The story's status, target, notes and beats will be kept at the end of its file.";

    /// <summary>Impact summary when "Keep everything in the story file" is turned off.</summary>
    public const string MoveOutOfFileSummary =
        "The story's status, target, notes and beats will move from its file into project data.";

    /// <summary>Why "Keep everything in the story file" can't be turned on.</summary>
    public const string StorageOptionUnavailable = "Available when Book.txt lists exactly one story file.";

    public ObservableCollection<ProviderProfileViewModel> Profiles { get; } = new();

    private string? _activeProfileId;
    public string? ActiveProfileId
    {
        get => _activeProfileId;
        private set => this.RaiseAndSetIfChanged(ref _activeProfileId, value);
    }

    public ReactiveCommand<Unit, Unit> AddProfileCommand { get; }

    /// <summary>True when a workspace is open, so the Project section applies.</summary>
    public bool HasWorkspace => _workspace?.HasWorkspace ?? false;

    public IReadOnlyList<ProjectTypeCardViewModel> ProjectCards { get; }

    private ProjectKind? _selectedProjectKind;
    public ProjectKind? SelectedProjectKind
    {
        get => _selectedProjectKind;
        set => this.RaiseAndSetIfChanged(ref _selectedProjectKind, value);
    }

    /// <summary>Summary of what applying the selected kind and storage does; null when unchanged.</summary>
    public string? ProjectChangeSummary => _projectChangeSummary.Value;

    private bool _keepInStoryFile;
    /// <summary>"Keep everything in the story file": the Short Story's data lives in its file's trailing block.</summary>
    public bool KeepInStoryFile
    {
        get => _keepInStoryFile;
        set => this.RaiseAndSetIfChanged(ref _keepInStoryFile, value);
    }

    private int _storyFileCount;
    /// <summary>How many chapter files Book.txt lists, missing ones included.</summary>
    public int StoryFileCount
    {
        get => _storyFileCount;
        private set => this.RaiseAndSetIfChanged(ref _storyFileCount, value);
    }

    /// <summary>The storage option applies to Short Story projects only.</summary>
    public bool IsStorageOptionVisible => _isStorageOptionVisible.Value;

    /// <summary>
    /// The option can be turned on only when Book.txt lists exactly one story file; it can always be turned off.
    /// </summary>
    public bool IsStorageOptionEnabled => _isStorageOptionEnabled.Value;

    /// <summary>One line saying why the option is disabled; null when it is enabled.</summary>
    public string? StorageOptionExplanation => _storageOptionExplanation.Value;

    public ReactiveCommand<ProjectTypeCardViewModel, Unit> SelectProjectKindCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyProjectKindCommand { get; }

    public SettingsViewModel(
        IProviderProfileService profileService,
        ICredentialStore credentials,
        IAiChatService aiChatService,
        INotificationService notifications,
        WorkspaceViewModel? workspace = null,
        ProjectSettingsService? projectSettings = null,
        IBeatBoardService? beatBoards = null,
        BeatBoardViewModel? beatBoard = null,
        StorageMigrator? storageMigrator = null,
        Func<Task<bool>>? flushStoryEdits = null)
    {
        _profileService = profileService;
        _credentials = credentials;
        _aiChatService = aiChatService;
        _notifications = notifications;
        _workspace = workspace;
        _projectSettings = projectSettings;
        _beatBoards = beatBoards;
        _beatBoard = beatBoard;
        _storageMigrator = storageMigrator;
        _flushStoryEdits = flushStoryEdits;

        AddProfileCommand = ReactiveCommand.CreateFromTask(AddProfileAsync);
        Disposables.Add(AddProfileCommand.ThrownExceptions
            .Subscribe(ex => _notifications.ShowError($"Add profile failed: {ex.Message}")));

        ProjectCards = ProjectTypeCatalog.All.Select(i => new ProjectTypeCardViewModel(i)).ToList();
        SelectedProjectKind = workspace?.Profile.Kind;
        foreach (var card in ProjectCards)
            card.IsSelected = card.Info.Kind == SelectedProjectKind;

        KeepInStoryFile = workspace?.Profile.Storage == StorageMode.Embedded;
        if (workspace is not null)
        {
            StoryFileCount = CountStoryFiles();
            var nodes = (INotifyCollectionChanged)workspace.Nodes;
            NotifyCollectionChangedEventHandler recount = (_, _) => StoryFileCount = CountStoryFiles();
            nodes.CollectionChanged += recount;
            Disposables.Add(System.Reactive.Disposables.Disposable.Create(() => nodes.CollectionChanged -= recount));
        }

        var currentProfile = workspace is null
            ? Observable.Return<ProjectProfile?>(null)
            : workspace.WhenAnyValue(w => w.Profile).Select(p => (ProjectProfile?)p);
        var pending = this.WhenAnyValue(x => x.SelectedProjectKind, x => x.KeepInStoryFile, x => x.StoryFileCount)
            .CombineLatest(currentProfile, (choice, profile) => ChangeSummary(choice.Item1, profile));
        var changed = pending.Select(summary => summary is not null);

        _projectChangeSummary = pending.ToProperty(this, x => x.ProjectChangeSummary);
        Disposables.Add(_projectChangeSummary);

        _isStorageOptionVisible = this.WhenAnyValue(x => x.SelectedProjectKind)
            .Select(kind => kind == ProjectKind.Story)
            .ToProperty(this, x => x.IsStorageOptionVisible);
        Disposables.Add(_isStorageOptionVisible);

        _isStorageOptionEnabled = this.WhenAnyValue(x => x.StoryFileCount)
            .CombineLatest(currentProfile, (count, profile) => count == 1 || IsEmbedded(profile))
            .ToProperty(this, x => x.IsStorageOptionEnabled);
        Disposables.Add(_isStorageOptionEnabled);

        _storageOptionExplanation = this.WhenAnyValue(x => x.IsStorageOptionEnabled)
            .Select(enabled => enabled ? null : StorageOptionUnavailable)
            .ToProperty(this, x => x.StorageOptionExplanation);
        Disposables.Add(_storageOptionExplanation);

        SelectProjectKindCommand = ReactiveCommand.Create<ProjectTypeCardViewModel, Unit>(selectedCard =>
        {
            foreach (var c in ProjectCards)
                c.IsSelected = ReferenceEquals(c, selectedCard);
            SelectedProjectKind = selectedCard.Info.Kind;
            return Unit.Default;
        });

        ApplyProjectKindCommand = ReactiveCommand.CreateFromTask(ApplyProjectKindAsync, changed);
        Disposables.Add(ApplyProjectKindCommand.ThrownExceptions
            .Subscribe(ex => _notifications.ShowError($"Project change failed: {ex.Message}")));

        if (workspace is not null)
            Disposables.Add(workspace.WhenAnyValue(w => w.HasWorkspace)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(HasWorkspace))));

        _ = LoadAsync();
    }

    /// <summary>
    /// Applies the selected kind and storage. When the storage changes (including leaving Short Story while its
    /// data is kept in the story file), the story's data is migrated first; if that fails nothing else changes.
    /// Then project.json is saved, a Short Story ↔ Anthology board move runs, and the workspace reloads.
    /// </summary>
    private async Task ApplyProjectKindAsync()
    {
        if (_workspace is null || _projectSettings is null || SelectedProjectKind is not { } kind)
            return;

        var root = _workspace.WorkspaceRoot;
        var previous = _workspace.Profile;
        var fromStorage = IsEmbedded(previous) ? StorageMode.Embedded : StorageMode.Sidecar;
        var toStorage = TargetStorage(kind, previous);

        StoryRef? migrated = null;
        if (fromStorage != toStorage)
        {
            var migration = await MigrateStorageAsync(root, fromStorage, toStorage);
            if (!migration.IsSuccess)
            {
                _notifications.ShowError(migration.Error!);
                return;
            }
            migrated = migration.Value;
        }

        var save = await _projectSettings.SaveAsync(root, new ProjectSettings(kind, toStorage));
        if (!save.IsSuccess)
        {
            var error = save.Error ?? "Could not save project settings.";
            if (migrated is not null)
            {
                // The data already moved: move it back so it stays where the unchanged project.json says.
                var back = await _storageMigrator!.MigrateAsync(root, migrated, toStorage, fromStorage);
                error += back.IsSuccess
                    ? " The story's data was moved back."
                    : $" The story's data couldn't be moved back: {back.Error}";
            }
            _notifications.ShowError(error);
            return;
        }

        await MoveBeatBoardForKindAsync(previous.Kind, kind);

        var reload = await _workspace.ReloadProjectAsync();
        if (!reload.IsSuccess)
            _notifications.ShowError(reload.Error ?? "Could not reload the project.");
    }

    /// <summary>
    /// Saves pending edits, then moves the story's data from <paramref name="from"/> to <paramref name="to"/>
    /// storage. Returns the story migrated (null when there was none), or why nothing was migrated.
    /// </summary>
    private async Task<Result<StoryRef?>> MigrateStorageAsync(string root, StorageMode from, StorageMode to)
    {
        if (_storageMigrator is null)
            return Result<StoryRef?>.Fail("The story's data can't be moved here, so the project was not changed.");

        StoryRef? story;
        if (from == StorageMode.Embedded)
        {
            var primary = await _workspace!.GetEmbeddedPrimaryAsync();
            if (!primary.IsSuccess)
                return Result<StoryRef?>.Fail(primary.Error!);
            story = primary.Value;
        }
        else
        {
            var files = StoryFiles().ToList();
            if (files.Count != 1)
                return Result<StoryRef?>.Fail(
                    "Keep everything in the story file needs Book.txt to list exactly one story file.");
            story = files[0].Story;
        }

        if (story is null)
            return Result<StoryRef?>.Ok(null);

        // Edits waiting on a debounce are saved where they belong now, so they move with the rest.
        if (!await FlushPendingEditsAsync())
            return Result<StoryRef?>.Fail("Pending edits couldn't be saved, so the story's data was not moved.");

        var result = await _storageMigrator.MigrateAsync(root, story, from, to);
        return result.IsSuccess ? Result<StoryRef?>.Ok(story) : Result<StoryRef?>.Fail(result.Error!);
    }

    private async Task<bool> FlushPendingEditsAsync()
    {
        if (_beatBoard is not null && !await _beatBoard.FlushAsync())
            return false;
        return _flushStoryEdits is null || await _flushStoryEdits();
    }

    /// <summary>The story's data is in its file: a Short Story set to embedded storage.</summary>
    private static bool IsEmbedded(ProjectProfile? profile) =>
        profile is { Kind: ProjectKind.Story, Storage: StorageMode.Embedded };

    /// <summary>
    /// The storage <paramref name="kind"/> gets: embedded for a Short Story with the option on, when it already
    /// is embedded or Book.txt lists exactly one story file; sidecar otherwise.
    /// </summary>
    private StorageMode TargetStorage(ProjectKind kind, ProjectProfile current) =>
        kind == ProjectKind.Story && KeepInStoryFile && (IsEmbedded(current) || StoryFileCount == 1)
            ? StorageMode.Embedded
            : StorageMode.Sidecar;

    /// <summary>What applying <paramref name="selected"/> (and the storage option) changes; null when nothing.</summary>
    private string? ChangeSummary(ProjectKind? selected, ProjectProfile? current)
    {
        if (selected is not { } kind || current is null)
            return null;

        var parts = new List<string>();
        if (kind != current.Kind)
            parts.Add(ProjectTypeCatalog.SwitchSummary(kind));
        var toEmbedded = TargetStorage(kind, current) == StorageMode.Embedded;
        if (toEmbedded != IsEmbedded(current))
            parts.Add(toEmbedded ? MoveIntoFileSummary : MoveOutOfFileSummary);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>The chapter files Book.txt lists (missing ones included), in order.</summary>
    private IEnumerable<ChapterViewModel> StoryFiles() =>
        _workspace?.Nodes.Where(n => n.Node.Kind == NodeKind.Chapter && !n.Node.IsExcluded) ?? [];

    private int CountStoryFiles() => StoryFiles().Count();

    /// <summary>
    /// Keeps the beat board with the story across a Short Story ↔ Anthology switch: the Short Story board
    /// becomes the first story's board, and back. The first story is the first included, present node in
    /// Book.txt order — a Part (a story with scenes) or a chapter. A board is never overwritten; when the
    /// destination already has one, or the edits on the shown board can't be saved, nothing moves and the
    /// author is told.
    /// </summary>
    private async Task MoveBeatBoardForKindAsync(ProjectKind from, ProjectKind to)
    {
        if (_workspace is null || _beatBoards is null)
            return;

        var root = _workspace.WorkspaceRoot;
        string fromKey;
        if (from == ProjectKind.Story && to == ProjectKind.Anthology)
            fromKey = BeatBoardService.ShortStoryBoardKey;
        else if (from == ProjectKind.Anthology && to == ProjectKind.Story)
            fromKey = "";
        else
            return;

        var story = _workspace.Nodes.FirstOrDefault(n => !n.Node.IsExcluded && !n.Node.IsMissing);
        if (from == ProjectKind.Anthology)
            fromKey = story?.Uuid ?? "";
        var toKey = to == ProjectKind.Anthology ? story?.Uuid ?? "" : BeatBoardService.ShortStoryBoardKey;

        if (string.IsNullOrEmpty(root) || !BeatBoardService.IsValidBoardKey(fromKey)
            || !_beatBoards.BoardExists(root, fromKey))
            return;

        if (story is null || !BeatBoardService.IsValidBoardKey(toKey))
        {
            _notifications.ShowInfo("The beats were not moved: there is no story to carry them. "
                + "They are kept under the Short Story project type.");
            return;
        }

        var title = string.IsNullOrWhiteSpace(story.Node.Title) ? story.Node.RelativePath : story.Node.Title;
        if (_beatBoards.BoardExists(root, toKey))
        {
            _notifications.ShowInfo(to == ProjectKind.Anthology
                ? $"The story's beats were not moved: '{title}' already has a beat board."
                : $"The beats of '{title}' were not moved: the short story already has a beat board.");
            return;
        }

        // Pending edits on the shown board are written first, so they move with it.
        if (_beatBoard is not null && !await _beatBoard.FlushAsync())
        {
            _notifications.ShowError("Beat edits couldn't be saved; beats were not moved.");
            return;
        }

        var moved = await _beatBoards.MoveBoardAsync(root, fromKey, toKey);
        if (!moved.IsSuccess)
            _notifications.ShowError($"The beats could not be moved with the story: {moved.Error}");
    }

    public async Task LoadAsync()
    {
        try
        {
            var profiles = await _profileService.LoadAllAsync().ConfigureAwait(false);
            var active = await _profileService.GetActiveAsync().ConfigureAwait(false);
            ActiveProfileId = active?.Id;

            Profiles.Clear();
            foreach (var p in profiles)
            {
                var vm = CreateProfileViewModel(p, p.Id == ActiveProfileId);
                Profiles.Add(vm);
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Failed to load settings: {ex.Message}");
        }
    }

    private ProviderProfileViewModel CreateProfileViewModel(ProviderProfile profile, bool isActive)
    {
        var vm = new ProviderProfileViewModel(profile, isActive, _profileService, _credentials);

        Disposables.Add(vm.SaveCommand.Subscribe(u => { var t = OnProfileSavedAsync(vm); }));
        Disposables.Add(vm.DeleteCommand.Subscribe(u => { var t = OnProfileDeletedAsync(vm); }));
        Disposables.Add(vm.SetActiveCommand.Subscribe(u => { var t = OnSetActiveAsync(vm); }));

        return vm;
    }

    private async Task AddProfileAsync()
    {
        var defaultType = ProviderType.OpenAI;
        var defaults    = ProviderProfileViewModel.TypeDefaults[defaultType];
        var newProfile  = new ProviderProfile(
            Id: Guid.NewGuid().ToString(),
            DisplayName: defaultType.ToString(),
            BaseUrl: defaults.BaseUrl,
            ModelId: defaults.ModelId,
            ContextWindowTokens: null,
            Type: defaultType);

        await _profileService.SaveAsync(newProfile).ConfigureAwait(false);
        var vm = CreateProfileViewModel(newProfile, false);
        Profiles.Add(vm);
    }

    private async Task OnProfileSavedAsync(ProviderProfileViewModel vm)
    {
        await _aiChatService.RefreshClientAsync().ConfigureAwait(false);
    }

    private async Task OnProfileDeletedAsync(ProviderProfileViewModel vm)
    {
        var existing = Profiles.FirstOrDefault(p => p.Id == vm.Id);
        if (existing is not null) Profiles.Remove(existing);
        await _aiChatService.RefreshClientAsync().ConfigureAwait(false);
    }

    private async Task OnSetActiveAsync(ProviderProfileViewModel vm)
    {
        ActiveProfileId = vm.Id;
        foreach (var p in Profiles) p.IsActive = p.Id == vm.Id;
        this.RaisePropertyChanged(nameof(Profiles));
        await _aiChatService.RefreshClientAsync().ConfigureAwait(false);
    }
}

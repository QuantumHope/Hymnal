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
    private readonly ObservableAsPropertyHelper<string?> _reopenRequiredMessage;

    /// <summary>Shown while project settings can't be changed because a reload failed.</summary>
    public const string ReopenRequired = "Reopen the project to continue.";

    /// <summary>Impact summary when "Keep everything in the story file" is turned on.</summary>
    public const string MoveIntoFileSummary =
        "The story's status, target, notes and beats will be kept at the end of its file.";

    /// <summary>Impact summary when "Keep everything in the story file" is turned off.</summary>
    public const string MoveOutOfFileSummary =
        "The story's status, target, notes and beats will move from its file into project data.";

    /// <summary>Why a story data edit is refused while the story's data moves between storages.</summary>
    public const string MovingStoryDataMessage =
        "The story's data is being moved. Make this change again once the project has reloaded.";

    /// <summary>Shown when the storage switched but old copies couldn't all be removed.</summary>
    public const string CleanupLeftoversMessage =
        "Switched storage. Some old copies in .hymnal-data / the story file couldn't be removed; they're ignored and harmless.";

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

    /// <summary>One line saying why the option can't be turned on (Book.txt's files); null when it can.</summary>
    public string? StorageOptionExplanation => _storageOptionExplanation.Value;

    /// <summary><see cref="ReopenRequired"/> while a failed reload blocks project changes; null otherwise.</summary>
    public string? ReopenRequiredMessage => _reopenRequiredMessage.Value;

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
        var needsReopen = workspace is null
            ? Observable.Return(false)
            : workspace.WhenAnyValue(w => w.NeedsReopen);
        var pending = this.WhenAnyValue(x => x.SelectedProjectKind, x => x.KeepInStoryFile, x => x.StoryFileCount)
            .CombineLatest(currentProfile, (choice, profile) => ChangeSummary(choice.Item1, profile));
        // After a failed reload the profile may not match project.json: nothing can be applied until a reopen.
        var changed = pending.CombineLatest(needsReopen, (summary, reopen) => summary is not null && !reopen);

        _reopenRequiredMessage = needsReopen
            .Select(reopen => reopen ? ReopenRequired : null)
            .ToProperty(this, x => x.ReopenRequiredMessage);
        Disposables.Add(_reopenRequiredMessage);

        _projectChangeSummary = pending.ToProperty(this, x => x.ProjectChangeSummary);
        Disposables.Add(_projectChangeSummary);

        _isStorageOptionVisible = this.WhenAnyValue(x => x.SelectedProjectKind)
            .Select(kind => kind == ProjectKind.Story)
            .ToProperty(this, x => x.IsStorageOptionVisible);
        Disposables.Add(_isStorageOptionVisible);

        var availableForFiles = this.WhenAnyValue(x => x.StoryFileCount)
            .CombineLatest(currentProfile, (count, profile) => count == 1 || IsEmbedded(profile));

        _isStorageOptionEnabled = availableForFiles
            .CombineLatest(needsReopen, (available, reopen) => available && !reopen)
            .ToProperty(this, x => x.IsStorageOptionEnabled);
        Disposables.Add(_isStorageOptionEnabled);

        _storageOptionExplanation = availableForFiles
            .Select(available => available ? null : StorageOptionUnavailable)
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
        // Where the data is now comes from project.json on disk, not the in-memory profile, which can be stale
        // (e.g. after a failed reload): migrating from the wrong mode would mirror stale data over the real one.
        var stored = await _projectSettings.LoadAsync(root);
        if (!stored.IsSuccess)
        {
            _notifications.ShowError($"Couldn't read the project settings ({stored.Error}), so nothing was changed.");
            return;
        }
        var current = stored.Value!;
        var currentlyEmbedded = current is { Kind: ProjectKind.Story, Storage: StorageMode.Embedded };
        var fromStorage = currentlyEmbedded ? StorageMode.Embedded : StorageMode.Sidecar;
        var toStorage = TargetStorage(kind, currentlyEmbedded);

        // The story whose data changes storage (null: none, or the storage stays).
        StoryRef? story = null;
        if (fromStorage != toStorage)
        {
            var resolved = await ResolveMigratingStoryAsync(fromStorage);
            if (!resolved.IsSuccess)
            {
                _notifications.ShowError(resolved.Error!);
                return;
            }
            story = resolved.Value;

            // Edits waiting on a debounce are saved where they belong now, so they move with the rest.
            if (story is not null && !await FlushPendingEditsAsync())
            {
                _notifications.ShowError("Pending edits couldn't be saved, so the story's data was not moved.");
                return;
            }
        }

        // Into the story file: an Anthology story's board becomes the Short Story board first, so it is migrated
        // into the block with the rest.
        (string From, string To)? movedBoard = null;
        if (toStorage == StorageMode.Embedded)
            movedBoard = await MoveBeatBoardForKindAsync(current.Kind, kind);

        WorkspaceViewModel.StoryDataWriteSuspension? suspended = null;
        var committed = false;
        try
        {
            // Ruling 9: write the destination; saving project.json in the new mode commits; then clean up the source.
            if (story is not null)
            {
                // From here until the reload, story data edits are refused, so none is written to the old location
                // after the destination was written from it. Only this flow's flush gets through: edits made while
                // the board moved, and saves that were already writing (the flush waits for them), are saved where
                // they belong now and moved with the rest.
                suspended = _workspace.SuspendStoryDataWrites(MovingStoryDataMessage);
                bool flushed;
                using (suspended.PermitWritesInThisFlow())
                    flushed = await FlushPendingEditsAsync();
                if (!flushed)
                {
                    await UndoBoardMoveAsync(root, movedBoard);
                    _notifications.ShowError("Pending edits couldn't be saved, so the story's data was not moved.");
                    return;
                }

                var written = await _storageMigrator!.WriteDestinationAsync(root, story, fromStorage, toStorage);
                if (!written.IsSuccess)
                {
                    await UndoBoardMoveAsync(root, movedBoard);
                    _notifications.ShowError(written.Error!);
                    return;
                }
            }

            var save = await _projectSettings.SaveAsync(root, new ProjectSettings(kind, toStorage));
            if (!save.IsSuccess)
            {
                // What was written is ignored in the unchanged mode, which still has all the data.
                await UndoBoardMoveAsync(root, movedBoard);
                _notifications.ShowError(save.Error ?? "Could not save project settings.");
                return;
            }
            committed = true;

            if (story is not null
                && !(await _storageMigrator!.CleanupSourceAsync(root, story, fromStorage, toStorage)).IsSuccess)
                _notifications.ShowInfo(CleanupLeftoversMessage);

            if (toStorage != StorageMode.Embedded)
                await MoveBeatBoardForKindAsync(current.Kind, kind);

            var reload = await _workspace.ReloadProjectAsync();
            if (!reload.IsSuccess)
            {
                // Never keep writing to the old mode's location: use the new mode's store (refusing story data until
                // it can be loaded) and ask for a reopen.
                _workspace.ResetMetadataStore(new ProjectSettings(kind, toStorage));
                _notifications.ShowError(
                    $"{reload.Error ?? "Could not reload the project."} The project settings were saved; reopen the project to continue.");
            }
        }
        finally
        {
            // After the commit, the old mode's location must never be written again: if something threw before the
            // store was rebuilt, point it at the new mode (as after a failed reload) rather than lifting the
            // suspension back onto the old store. Otherwise lifting it is a no-op once the reload (or the reset)
            // has rebuilt the store, and before the commit edits go where they did before.
            if (committed && suspended is { IsInEffect: true })
                _workspace.ResetMetadataStore(new ProjectSettings(kind, toStorage));
            else
                suspended?.Dispose();
        }
    }

    /// <summary>
    /// The story whose data moves out of <paramref name="from"/> storage: the embedded primary (null when Book.txt
    /// lists no story file), or the only story file Book.txt lists.
    /// </summary>
    private async Task<Result<StoryRef?>> ResolveMigratingStoryAsync(StorageMode from)
    {
        if (_storageMigrator is null)
            return Result<StoryRef?>.Fail("The story's data can't be moved here, so the project was not changed.");

        if (from == StorageMode.Embedded)
            return await _workspace!.GetEmbeddedPrimaryAsync();

        var files = StoryFiles().ToList();
        return files.Count == 1
            ? Result<StoryRef?>.Ok(files[0].Story)
            : Result<StoryRef?>.Fail("Keep everything in the story file needs Book.txt to list exactly one story file.");
    }

    private async Task UndoBoardMoveAsync(string root, (string From, string To)? moved)
    {
        if (moved is not { } board || _beatBoards is null)
            return;
        var back = await _beatBoards.MoveBoardAsync(root, board.To, board.From);
        if (!back.IsSuccess)
            _notifications.ShowError($"The beats couldn't be moved back: {back.Error}");
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
    private StorageMode TargetStorage(ProjectKind kind, bool currentlyEmbedded) =>
        kind == ProjectKind.Story && KeepInStoryFile && (currentlyEmbedded || StoryFileCount == 1)
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
        var toEmbedded = TargetStorage(kind, IsEmbedded(current)) == StorageMode.Embedded;
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
    /// author is told. Returns the keys moved, or null when nothing moved.
    /// </summary>
    private async Task<(string From, string To)?> MoveBeatBoardForKindAsync(ProjectKind from, ProjectKind to)
    {
        if (_workspace is null || _beatBoards is null)
            return null;

        var root = _workspace.WorkspaceRoot;
        string fromKey;
        if (from == ProjectKind.Story && to == ProjectKind.Anthology)
            fromKey = BeatBoardService.ShortStoryBoardKey;
        else if (from == ProjectKind.Anthology && to == ProjectKind.Story)
            fromKey = "";
        else
            return null;

        var story = _workspace.Nodes.FirstOrDefault(n => !n.Node.IsExcluded && !n.Node.IsMissing);
        if (from == ProjectKind.Anthology)
            fromKey = story?.Uuid ?? "";
        var toKey = to == ProjectKind.Anthology ? story?.Uuid ?? "" : BeatBoardService.ShortStoryBoardKey;

        if (string.IsNullOrEmpty(root) || !BeatBoardService.IsValidBoardKey(fromKey)
            || !_beatBoards.BoardExists(root, fromKey))
            return null;

        if (story is null || !BeatBoardService.IsValidBoardKey(toKey))
        {
            _notifications.ShowInfo("The beats were not moved: there is no story to carry them. "
                + "They are kept under the Short Story project type.");
            return null;
        }

        var title = string.IsNullOrWhiteSpace(story.Node.Title) ? story.Node.RelativePath : story.Node.Title;
        if (_beatBoards.BoardExists(root, toKey))
        {
            _notifications.ShowInfo(to == ProjectKind.Anthology
                ? $"The story's beats were not moved: '{title}' already has a beat board."
                : $"The beats of '{title}' were not moved: the short story already has a beat board.");
            return null;
        }

        // Pending edits on the shown board are written first, so they move with it.
        if (_beatBoard is not null && !await _beatBoard.FlushAsync())
        {
            _notifications.ShowError("Beat edits couldn't be saved; beats were not moved.");
            return null;
        }

        var moved = await _beatBoards.MoveBoardAsync(root, fromKey, toKey);
        if (moved.IsSuccess)
            return (fromKey, toKey);
        _notifications.ShowError($"The beats could not be moved with the story: {moved.Error}");
        return null;
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

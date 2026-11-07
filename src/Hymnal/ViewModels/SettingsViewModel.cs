using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
    private readonly ObservableAsPropertyHelper<string?> _projectChangeSummary;

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

    /// <summary>Summary of what switching to the selected kind does; null when unchanged.</summary>
    public string? ProjectChangeSummary => _projectChangeSummary.Value;

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
        BeatBoardViewModel? beatBoard = null)
    {
        _profileService = profileService;
        _credentials = credentials;
        _aiChatService = aiChatService;
        _notifications = notifications;
        _workspace = workspace;
        _projectSettings = projectSettings;
        _beatBoards = beatBoards;
        _beatBoard = beatBoard;

        AddProfileCommand = ReactiveCommand.CreateFromTask(AddProfileAsync);
        Disposables.Add(AddProfileCommand.ThrownExceptions
            .Subscribe(ex => _notifications.ShowError($"Add profile failed: {ex.Message}")));

        ProjectCards = ProjectTypeCatalog.All.Select(i => new ProjectTypeCardViewModel(i)).ToList();
        SelectedProjectKind = workspace?.Profile.Kind;
        foreach (var card in ProjectCards)
            card.IsSelected = card.Info.Kind == SelectedProjectKind;

        var currentKind = workspace is null
            ? Observable.Return<ProjectKind?>(null)
            : workspace.WhenAnyValue(w => w.Profile).Select(p => (ProjectKind?)p.Kind);
        var changed = this.WhenAnyValue(x => x.SelectedProjectKind)
            .CombineLatest(currentKind, (selected, current) => selected.HasValue && current.HasValue && selected != current);

        _projectChangeSummary = this.WhenAnyValue(x => x.SelectedProjectKind)
            .CombineLatest(currentKind, (selected, current) =>
                selected.HasValue && current.HasValue && selected != current
                    ? ProjectTypeCatalog.SwitchSummary(selected.Value)
                    : null)
            .ToProperty(this, x => x.ProjectChangeSummary);
        Disposables.Add(_projectChangeSummary);

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

    private async Task ApplyProjectKindAsync()
    {
        if (_workspace is null || _projectSettings is null || SelectedProjectKind is not { } kind)
            return;

        var previousKind = _workspace.Profile.Kind;
        var save = await _projectSettings.SaveAsync(
            _workspace.WorkspaceRoot, new ProjectSettings(kind, _workspace.Profile.Storage));
        if (!save.IsSuccess)
        {
            _notifications.ShowError(save.Error ?? "Could not save project settings.");
            return;
        }

        await MoveBeatBoardForKindAsync(previousKind, kind);

        var reload = await _workspace.ReloadProjectAsync();
        if (!reload.IsSuccess)
            _notifications.ShowError(reload.Error ?? "Could not reload the project.");
    }

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

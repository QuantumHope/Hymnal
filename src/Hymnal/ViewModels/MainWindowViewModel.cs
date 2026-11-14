using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Infrastructure;
using Hymnal.ViewModels.Ai;
using MattEland.HopeLab.Updates.ReactiveUI;
using ReactiveUI;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Hymnal.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly WorkspaceViewModel _workspaceVm;
    private readonly SupplementalDocsViewModel _supplementalDocsVm;
    private readonly NotesViewModel _notesVm;
    private readonly ChapterInfoViewModel _chapterInfoVm;

    public WorkspaceViewModel WorkspaceViewModel { get; }
    public EditorViewModel EditorViewModel { get; }
    public NotesViewModel NotesViewModel { get; }
    public ChapterInfoViewModel ChapterInfoViewModel { get; }
    public BookStatsViewModel BookStatsViewModel { get; }
    public GanttViewModel GanttViewModel { get; }
    public CorkboardViewModel CorkboardViewModel { get; }
    public ResearchViewModel ResearchViewModel { get; }
    public SupplementalDocsViewModel SupplementalDocsViewModel { get; }
    public GitPanelViewModel GitPanelViewModel { get; }
    public AiChatViewModel AiChatViewModel { get; }

    /// <summary>Story ideas for the Ideas mode; null when the app runs without it.</summary>
    public IdeasViewModel? IdeasViewModel { get; }

    /// <summary>Plan mode for story and anthology projects (beat boards); null when the app runs without it.</summary>
    public PlanHostViewModel? PlanHostViewModel { get; }

    /// <summary>App-wide sound preferences (View → Sound); null when the host has no sound.</summary>
    public Hymnal.Audio.SoundPreferences? SoundPreferences { get; }

    /// <summary>Release updates: the banner and Help → Check for Updates bind here; null when the app runs without them.</summary>
    public UpdatesViewModel? Updates { get; }

    /// <summary>View → Sound: turns all of Hymnal's sound on or off.</summary>
    public ReactiveCommand<Unit, Unit> ToggleSoundCommand { get; }

    // ── Settings overlay ──────────────────────────────────────────────────────

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => this.RaiseAndSetIfChanged(ref _isSettingsOpen, value);
    }

    private readonly Func<SettingsViewModel> _settingsVmFactory;
    private SettingsViewModel? _activeSettingsVm;
    public SettingsViewModel? ActiveSettingsViewModel
    {
        get => _activeSettingsVm;
        private set => this.RaiseAndSetIfChanged(ref _activeSettingsVm, value);
    }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAiChatCommand { get; }

    // ── Window title ──────────────────────────────────────────────────────────

    private string _title = "Hymnal";
    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    // ── Notification banner ───────────────────────────────────────────────────

    private bool _hasBanner;
    public bool HasBanner
    {
        get => _hasBanner;
        set => this.RaiseAndSetIfChanged(ref _hasBanner, value);
    }

    private string? _bannerTitle = "Notice";
    public string? BannerTitle
    {
        get => _bannerTitle;
        set => this.RaiseAndSetIfChanged(ref _bannerTitle, value);
    }

    private string? _bannerMessage;
    public string? BannerMessage
    {
        get => _bannerMessage;
        set => this.RaiseAndSetIfChanged(ref _bannerMessage, value);
    }

    private Infrastructure.NotificationKind _bannerKind;
    public Infrastructure.NotificationKind BannerKind
    {
        get => _bannerKind;
        set => this.RaiseAndSetIfChanged(ref _bannerKind, value);
    }

    // ── Centre-panel mode ─────────────────────────────────────────────────────

    private ShellMode _activeMode = ShellMode.Write;
    public ShellMode ActiveMode
    {
        get => _activeMode;
        private set
        {
            if (_activeMode == value)
                return;

            _activeMode = value;
            EditorViewModel.IsResearchSurface = value == ShellMode.Research;
            EditorViewModel.IsEditorSurfaceVisible = value is ShellMode.Write or ShellMode.Research;
            CorkboardViewModel.SetViewActive(value == ShellMode.Plan);
            GanttViewModel.SetViewActive(value == ShellMode.Manage);
            AiChatViewModel.CurrentShellModeName = value.ToString();
            this.RaisePropertyChanged(nameof(ActiveMode));
            this.RaisePropertyChanged(nameof(IsEditorVisible));
            this.RaisePropertyChanged(nameof(IsGanttVisible));
            this.RaisePropertyChanged(nameof(IsCorkboardVisible));
            this.RaisePropertyChanged(nameof(IsResearchVisible));
            this.RaisePropertyChanged(nameof(IsIdeasVisible));
            this.RaisePropertyChanged(nameof(SecondaryCentreContent));
        }
    }

    /// <summary>Plan mode content: the beat-board host for story and anthology projects, else the Corkboard.</summary>
    private object PlanContent =>
        PlanHostViewModel != null && WorkspaceViewModel.Profile.UsesBeatBoard ? PlanHostViewModel : CorkboardViewModel;

    /// <summary>View-model for the active non-Write centre panel (lazy-loaded via ViewLocator).</summary>
    public object? SecondaryCentreContent => ActiveMode switch
    {
        ShellMode.Research => ResearchViewModel,
        ShellMode.Ideas => IdeasViewModel,
        ShellMode.Plan => PlanContent,
        ShellMode.Manage => GanttViewModel,
        _ => null
    };

    /// <summary>True when the writing/editor surface is the active centre-panel view.</summary>
    public bool IsEditorVisible => ActiveMode == ShellMode.Write;

    /// <summary>True when the corkboard/plan surface is the active centre-panel view.</summary>
    public bool IsCorkboardVisible => ActiveMode == ShellMode.Plan;

    /// <summary>True when the Gantt/manage surface is the active centre-panel view.</summary>
    public bool IsGanttVisible => ActiveMode == ShellMode.Manage;

    /// <summary>True when the research surface is the active centre-panel view.</summary>
    public bool IsResearchVisible => ActiveMode == ShellMode.Research;

    /// <summary>True when the story ideas surface is the active centre-panel view.</summary>
    public bool IsIdeasVisible => ActiveMode == ShellMode.Ideas;

    private readonly ObservableAsPropertyHelper<bool> _isIdeasModeAvailable;
    /// <summary>True when a story or anthology workspace is open, so the Ideas mode can be shown.</summary>
    public bool IsIdeasModeAvailable => _isIdeasModeAvailable.Value;

    /// <summary>
    /// Asks for a quick-capture idea's title and premise; null cancels. Set by the view.
    /// </summary>
    public Func<Task<(string Title, string Premise)?>> ShowQuickIdeaDialogAsync { get; set; } =
        () => Task.FromResult<(string Title, string Premise)?>(null);

    public ReactiveCommand<Unit, Unit> SelectResearchCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectIdeasCommand { get; }
    public ReactiveCommand<Unit, Unit> QuickCaptureIdeaCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectPlanCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectWriteCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectManageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleGanttCommand { get; }

    // ── Left-rail pane aggregates ─────────────────────────────────────────────

    private readonly ObservableAsPropertyHelper<bool> _isAnyLeftPaneOpen;
    /// <summary>True when either the Chapters or Docs content area is visible.</summary>
    public bool IsAnyLeftPaneOpen => _isAnyLeftPaneOpen.Value;

    private readonly ObservableAsPropertyHelper<bool> _isBothLeftPanesOpen;
    /// <summary>True when both the Chapters and Docs content areas are visible.</summary>
    public bool IsBothLeftPanesOpen => _isBothLeftPanesOpen.Value;

    // ── Sidebar visibility (Ctrl+B) ───────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand { get; }

    // ── Right-rail pane aggregates ────────────────────────────────────────────

    private readonly ObservableAsPropertyHelper<object?> _chapterPanelContent;
    /// <summary>
    /// The view-model shown in the right sidebar's top panel slot.
    /// Returns <see cref="ChapterInfoViewModel"/> when a chapter is active,
    /// <see cref="BookStatsViewModel"/> when a part or the book is selected,
    /// or null when no workspace is open.
    /// </summary>
    public object? ChapterPanelContent => _chapterPanelContent.Value;

    private readonly ObservableAsPropertyHelper<string> _chapterPanelTitle;
    /// <summary>Header label for the right-sidebar top panel: the info panel title (e.g. "CHAPTER INFO"), "PART STATS", or "BOOK STATS".</summary>
    public string ChapterPanelTitle => _chapterPanelTitle.Value;

    private readonly ObservableAsPropertyHelper<bool> _isChapterPanelOpen;
    /// <summary>True when the top-right panel (chapter info or book/part stats) is expanded.</summary>
    public bool IsChapterPanelOpen => _isChapterPanelOpen.Value;

    private readonly ObservableAsPropertyHelper<bool> _isAnyRightPaneOpen;
    /// <summary>True when either the Notes or the top-right panel is visible.</summary>
    public bool IsAnyRightPaneOpen => _isAnyRightPaneOpen.Value;

    private readonly ObservableAsPropertyHelper<bool> _isBothRightPanesOpen;
    /// <summary>True when two or more right-rail panels have open content.</summary>
    public bool IsBothRightPanesOpen => _isBothRightPanesOpen.Value;

    private readonly ObservableAsPropertyHelper<bool> _isInfoNotesSplitterVisible;
    /// <summary>True when both Info and Notes content areas are open (shows the splitter between them).</summary>
    public bool IsInfoNotesSplitterVisible => _isInfoNotesSplitterVisible.Value;

    private readonly ObservableAsPropertyHelper<bool> _isNotesChatSplitterVisible;
    /// <summary>True when both Notes and Chat content areas are open (shows the splitter between them).</summary>
    public bool IsNotesChatSplitterVisible => _isNotesChatSplitterVisible.Value;

    private readonly ObservableAsPropertyHelper<bool> _isEditorChapterBarVisible;
    /// <summary>True when the editor chapter metadata bar should be shown.</summary>
    public bool IsEditorChapterBarVisible => _isEditorChapterBarVisible.Value;

    // ── Write layout settings ─────────────────────────────────────────────────

    private WriteLayoutSettings _currentWriteLayout = WriteLayoutSettings.CreateDefault();

    /// <summary>
    /// The Write-tab geometry that was last persisted (or defaults on first run).
    /// Code-behind reads this to size grid columns/rows after restore or reset.
    /// </summary>
    public WriteLayoutSettings CurrentWriteLayout
    {
        get => _currentWriteLayout;
        private set => this.RaiseAndSetIfChanged(ref _currentWriteLayout, value);
    }

    /// <summary>Broadcast when the layout is reset, so code-behind can reapply grid sizes.</summary>
    private readonly Subject<WriteLayoutSettings> _layoutReset = new();
    public IObservable<WriteLayoutSettings> WriteLayoutReset => _layoutReset.AsObservable();

    public ReactiveCommand<Unit, Unit> ResetWriteLayoutCommand { get; }

    // ── Exit ─────────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ExitCommand { get; } =
        ReactiveCommand.Create(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.TryShutdown(); // lets the main window finish pending saves before closing
        });

    // ── About ─────────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ShowAboutCommand { get; } =
        ReactiveCommand.Create(() => { /* handled in MainWindow code-behind */ });

    public MainWindowViewModel(
        WorkspaceViewModel workspaceViewModel,
        EditorViewModel editorViewModel,
        NotesViewModel notesViewModel,
        ChapterInfoViewModel chapterInfoViewModel,
        BookStatsViewModel bookStatsViewModel,
        GanttViewModel ganttViewModel,
        CorkboardViewModel corkboardViewModel,
        ResearchViewModel researchViewModel,
        SupplementalDocsViewModel supplementalDocsViewModel,
        GitPanelViewModel gitPanelViewModel,
        NotificationService notificationService,
        IAppSettingsStore settingsStore,
        AiChatViewModel aiChatViewModel,
        Func<SettingsViewModel> settingsVmFactory,
        IdeasViewModel? ideasViewModel = null,
        PlanHostViewModel? planHostViewModel = null,
        Hymnal.Audio.SoundPreferences? soundPreferences = null,
        UpdatesViewModel? updates = null)
    {
        _settingsStore = settingsStore;
        _workspaceVm = workspaceViewModel;
        _supplementalDocsVm = supplementalDocsViewModel;
        _notesVm = notesViewModel;
        _chapterInfoVm = chapterInfoViewModel;

        WorkspaceViewModel = workspaceViewModel;
        EditorViewModel = editorViewModel;
        NotesViewModel = notesViewModel;
        ChapterInfoViewModel = chapterInfoViewModel;
        BookStatsViewModel = bookStatsViewModel;
        GanttViewModel = ganttViewModel;
        CorkboardViewModel = corkboardViewModel;
        ResearchViewModel = researchViewModel;
        SupplementalDocsViewModel = supplementalDocsViewModel;
        GitPanelViewModel = gitPanelViewModel;
        AiChatViewModel = aiChatViewModel;
        IdeasViewModel = ideasViewModel;
        PlanHostViewModel = planHostViewModel;
        SoundPreferences = soundPreferences;
        Updates = updates;
        ToggleSoundCommand = ReactiveCommand.Create(() => soundPreferences?.ToggleSound());
        _settingsVmFactory = settingsVmFactory;

        CorkboardViewModel.SetViewActive(false);
        GanttViewModel.SetViewActive(false);

        OpenSettingsCommand = ReactiveCommand.Create(() =>
        {
            ActiveSettingsViewModel = _settingsVmFactory();
            IsSettingsOpen = true;
        });
        CloseSettingsCommand = ReactiveCommand.Create(() =>
        {
            IsSettingsOpen = false;
            ActiveSettingsViewModel = null;
        });
        ToggleAiChatCommand = ReactiveCommand.Create(() =>
        {
            AiChatViewModel.IsVisible = !AiChatViewModel.IsVisible;
        });

        Disposables.Add(OpenSettingsCommand.ThrownExceptions
            .Subscribe(ex => notificationService.ShowError($"Failed to open settings: {ex.Message}")));
        Disposables.Add(CloseSettingsCommand.ThrownExceptions
            .Subscribe(ex => notificationService.ShowError($"Failed to close settings: {ex.Message}")));

        _isAnyLeftPaneOpen = Observable.CombineLatest(
                workspaceViewModel.WhenAnyValue(x => x.IsChaptersPaneVisible),
                supplementalDocsViewModel.WhenAnyValue(x => x.IsVisible),
                (chapters, docs) => chapters || docs)
            .ToProperty(this, x => x.IsAnyLeftPaneOpen);
        Disposables.Add(_isAnyLeftPaneOpen);

        _isBothLeftPanesOpen = Observable.CombineLatest(
                workspaceViewModel.WhenAnyValue(x => x.IsChaptersPaneVisible),
                supplementalDocsViewModel.WhenAnyValue(x => x.IsVisible),
                (chapters, docs) => chapters && docs)
            .ToProperty(this, x => x.IsBothLeftPanesOpen);
        Disposables.Add(_isBothLeftPanesOpen);

        ToggleSidebarCommand = ReactiveCommand.Create(() =>
        {
            if (IsAnyLeftPaneOpen)
            {
                workspaceViewModel.IsChaptersPaneVisible = false;
                supplementalDocsViewModel.IsVisible = false;
            }
            else
            {
                workspaceViewModel.IsChaptersPaneVisible = true;
            }
        });

        SelectResearchCommand = ReactiveCommand.CreateFromTask(EnterResearchModeAsync);
        Disposables.Add(SelectResearchCommand.ThrownExceptions.Subscribe(ex =>
            notificationService.ShowError($"Failed to enter research mode: {ex.Message}")));
        var ideasAvailable = ideasViewModel == null
            ? Observable.Return(false)
            : Observable.CombineLatest(
                    workspaceViewModel.WhenAnyValue(x => x.Profile),
                    workspaceViewModel.WhenAnyValue(x => x.HasWorkspace),
                    (profile, hasWorkspace) => profile.ShowsIdeas && hasWorkspace)
                .DistinctUntilChanged();
        _isIdeasModeAvailable = ideasAvailable.ToProperty(this, x => x.IsIdeasModeAvailable);
        Disposables.Add(_isIdeasModeAvailable);

        // Leaving story/anthology (or closing the workspace) while in Ideas returns to Write.
        Disposables.Add(
            this.WhenAnyValue(x => x.IsIdeasModeAvailable)
                .Where(available => !available && ActiveMode == ShellMode.Ideas)
                .Subscribe(_ => ActiveMode = ShellMode.Write));

        SelectIdeasCommand = ReactiveCommand.Create(
            () => { ActiveMode = ShellMode.Ideas; },
            this.WhenAnyValue(x => x.IsIdeasModeAvailable));

        var canQuickCapture = ideasViewModel == null
            ? Observable.Return(false)
            : Observable.CombineLatest(
                this.WhenAnyValue(x => x.IsIdeasModeAvailable),
                ideasViewModel.WhenAnyValue(x => x.CanEdit),
                (available, canEdit) => available && canEdit);
        QuickCaptureIdeaCommand = ReactiveCommand.CreateFromTask(QuickCaptureIdeaAsync, canQuickCapture);
        Disposables.Add(QuickCaptureIdeaCommand.ThrownExceptions.Subscribe(ex =>
            notificationService.ShowError($"Failed to capture idea: {ex.Message}")));

        SelectPlanCommand = ReactiveCommand.Create(() => { ActiveMode = ShellMode.Plan; });
        SelectWriteCommand = ReactiveCommand.Create(() => { ActiveMode = ShellMode.Write; });
        SelectManageCommand = ReactiveCommand.Create(() => { ActiveMode = ShellMode.Manage; });
        ToggleGanttCommand = ReactiveCommand.Create(() =>
        {
            ActiveMode = IsGanttVisible ? ShellMode.Write : ShellMode.Manage;
        });

        ResetWriteLayoutCommand = ReactiveCommand.CreateFromTask(ResetWriteLayoutAsync);
        Disposables.Add(ResetWriteLayoutCommand.ThrownExceptions.Subscribe(ex =>
            notificationService.ShowError($"Failed to reset layout: {ex.Message}")));
        Disposables.Add(_layoutReset);

        // ── Right-rail pane aggregates ────────────────────────────────────────

        // Switch between ChapterInfoViewModel (chapter active) and BookStatsViewModel (part/book selected).
        _chapterPanelContent = Observable.CombineLatest(
                workspaceViewModel.WhenAnyValue(x => x.SelectedNode),
                workspaceViewModel.WhenAnyValue(x => x.HasWorkspace),
                (selected, hasWorkspace) =>
                {
                    if (!hasWorkspace) return (object?)null;
                    if (selected == null || selected.Node.Kind == NodeKind.Part)
                        return bookStatsViewModel;
                    return chapterInfoViewModel;
                })
            .ToProperty(this, x => x.ChapterPanelContent);
        Disposables.Add(_chapterPanelContent);

        _chapterPanelTitle = this.WhenAnyValue(x => x.ChapterPanelContent)
            .Select(content => content is BookStatsViewModel bsvm
                ? bsvm.WhenAnyValue(x => x.PanelTitle)
                : chapterInfoViewModel.WhenAnyValue(x => x.PanelTitle).Select(t => t.ToUpperInvariant()))
            .Switch()
            .ToProperty(this, x => x.ChapterPanelTitle, initialValue: "CHAPTER INFO");
        Disposables.Add(_chapterPanelTitle);

        // Combined observable drives both IsChapterPanelOpen and the right-rail aggregates.
        var chapterPanelOpenObs = Observable.CombineLatest(
            chapterInfoViewModel.WhenAnyValue(x => x.IsVisible),
            bookStatsViewModel.WhenAnyValue(x => x.IsVisible),
            (ci, bs) => ci || bs);

        _isChapterPanelOpen = chapterPanelOpenObs
            .ToProperty(this, x => x.IsChapterPanelOpen);
        Disposables.Add(_isChapterPanelOpen);

        _isAnyRightPaneOpen = Observable.CombineLatest(
                chapterPanelOpenObs,
                NotesViewModel.WhenAnyValue(x => x.IsVisible),
                AiChatViewModel.WhenAnyValue(x => x.IsVisible),
                (chPanel, notes, ai) => chPanel || notes || ai)
            .ToProperty(this, x => x.IsAnyRightPaneOpen);
        Disposables.Add(_isAnyRightPaneOpen);

        _isBothRightPanesOpen = Observable.CombineLatest(
                chapterPanelOpenObs,
                NotesViewModel.WhenAnyValue(x => x.IsVisible),
                AiChatViewModel.WhenAnyValue(x => x.IsVisible),
                (chPanel, notes, ai) => (chPanel ? 1 : 0) + (notes ? 1 : 0) + (ai ? 1 : 0) >= 2)
            .ToProperty(this, x => x.IsBothRightPanesOpen);
        Disposables.Add(_isBothRightPanesOpen);

        _isInfoNotesSplitterVisible = Observable.CombineLatest(
                chapterPanelOpenObs,
                NotesViewModel.WhenAnyValue(x => x.IsVisible),
                (chPanel, notes) => chPanel && notes)
            .ToProperty(this, x => x.IsInfoNotesSplitterVisible);
        Disposables.Add(_isInfoNotesSplitterVisible);

        _isNotesChatSplitterVisible = Observable.CombineLatest(
                NotesViewModel.WhenAnyValue(x => x.IsVisible),
                AiChatViewModel.WhenAnyValue(x => x.IsVisible),
                (notes, ai) => notes && ai)
            .ToProperty(this, x => x.IsNotesChatSplitterVisible);
        Disposables.Add(_isNotesChatSplitterVisible);

        _isEditorChapterBarVisible = Observable.CombineLatest(
                this.WhenAnyValue(x => x.ActiveMode),
                workspaceViewModel.WhenAnyValue(x => x.SelectedNode),
                editorViewModel.WhenAnyValue(x => x.ShowBookTxtWarning),
                (mode, selected, bookTxt) =>
                    mode == ShellMode.Write
                    && selected?.Node.Kind == NodeKind.Chapter
                    && !bookTxt)
            .ToProperty(this, x => x.IsEditorChapterBarVisible);
        Disposables.Add(_isEditorChapterBarVisible);

        EditorViewModel.HasWorkspace = WorkspaceViewModel.HasWorkspace;
        Disposables.Add(
            CorkboardViewModel.OpenChapterRequested
                .Subscribe(chapter =>
                {
                    WorkspaceViewModel.SelectedNode = chapter;
                    ActiveMode = ShellMode.Write;
                }));

        // Plan mode swaps between the Corkboard and the beat-board host as the project kind changes.
        if (planHostViewModel != null)
            Disposables.Add(
                workspaceViewModel.WhenAnyValue(x => x.Profile)
                    .Skip(1)
                    .Where(_ => ActiveMode == ShellMode.Plan)
                    .Subscribe(_ => this.RaisePropertyChanged(nameof(SecondaryCentreContent))));

        // Promoting an idea brings the author to the new story.
        if (ideasViewModel != null)
            Disposables.Add(
                ideasViewModel.StoryOpened
                    .Subscribe(_ => ActiveMode = ShellMode.Write));

        Disposables.Add(
            SupplementalDocsViewModel.DocumentOpened
                .Subscribe(_ =>
                {
                    if (ActiveMode != ShellMode.Research)
                        ActiveMode = ShellMode.Write;
                }));

        // ── Reactive window title ────────────────────────────────────────────
        // Format: "Hymnal", "Hymnal - Workspace", "Hymnal - Workspace - file.md", or with " *" when dirty.
        Disposables.Add(
            Observable.CombineLatest(
                editorViewModel.WhenAnyValue(x => x.IsDirty),
                editorViewModel.WhenAnyValue(x => x.ActiveNode),
                editorViewModel.WhenAnyValue(x => x.ActiveFilePath),
                workspaceViewModel.WhenAnyValue(x => x.WorkspaceName),
                (dirty, node, activeFilePath, workspaceName) =>
                {
                    if (string.IsNullOrEmpty(workspaceName))
                        return "Hymnal";

                    var fileName = node != null
                        ? Path.GetFileName(node.RelativePath)
                        : activeFilePath != null
                            ? Path.GetFileName(activeFilePath)
                            : null;

                    if (string.IsNullOrEmpty(fileName))
                        return $"Hymnal \u2014 {workspaceName}";

                    return dirty
                        ? $"Hymnal \u2014 {workspaceName} \u2014 {fileName} *"
                        : $"Hymnal \u2014 {workspaceName} \u2014 {fileName}";
                })
            .Subscribe(t => Title = t));

        // ── Notification banner with 5-second auto-dismiss ───────────────────
        // SerialDisposable ensures only the latest timer is active; a new notification resets the clock.
        var timerDisposable = new SerialDisposable();
        Disposables.Add(timerDisposable);

        Disposables.Add(
            notificationService.Notifications
                .Subscribe(n =>
                {
                    HasBanner = true;
                    BannerKind = n.Kind;
                    BannerTitle = n.Kind switch
                    {
                        Hymnal.Infrastructure.NotificationKind.Error => "Error",
                        Hymnal.Infrastructure.NotificationKind.Success => "Success",
                        Hymnal.Infrastructure.NotificationKind.Info => "Info",
                        _ => "Notice"
                    };
                    BannerMessage = n.Message;

                    timerDisposable.Disposable = Observable
                        .Timer(TimeSpan.FromSeconds(5))
                        .Subscribe(_ => HasBanner = false);
                }));

        // Start workspace init (loads last workspace + restores last chapter).
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await WorkspaceViewModel.InitAsync().ConfigureAwait(false);
        await NotesViewModel.RestoreSettingsAsync().ConfigureAwait(false);
        await ChapterInfoViewModel.RestoreSettingsAsync().ConfigureAwait(false);
        await SupplementalDocsViewModel.RestoreSettingsAsync().ConfigureAwait(false);
        await RestoreWriteLayoutAsync().ConfigureAwait(false);
        CorkboardViewModel.SetViewActive(ActiveMode == ShellMode.Plan);
        GanttViewModel.SetViewActive(ActiveMode == ShellMode.Manage);
    }

    private async Task EnterResearchModeAsync()
    {
        if (EditorViewModel.ActiveNode != null
            || EditorViewModel.IsBookSelected
            || EditorViewModel.ShowMissingChapterPrompt)
        {
            if (EditorViewModel.IsDirty)
            {
                try
                {
                    await EditorViewModel.SaveAsync();
                }
                catch
                {
                    return;
                }
            }

            EditorViewModel.CloseChapter();
            WorkspaceViewModel.ClearChapterSelectionForExternalDocument();
        }

        ActiveMode = ShellMode.Research;
    }

    private async Task QuickCaptureIdeaAsync()
    {
        if (IdeasViewModel == null)
            return;

        var request = await ShowQuickIdeaDialogAsync();
        if (request is not { } idea)
            return;

        await IdeasViewModel.NewIdeaAsync(idea.Title, idea.Premise);
    }

    /// <summary>
    /// Writes edits still waiting on a debounced save (story ideas, after any in-flight promote finishes,
    /// and beats) before the window closes, waiting at most <paramref name="timeout"/> in all. False when
    /// they could not be saved in time, so the view can ask whether to close anyway. Completes
    /// synchronously when nothing is pending.
    /// </summary>
    public async Task<bool> FlushPendingEditsBeforeCloseAsync(TimeSpan timeout)
    {
        if (IdeasViewModel == null && PlanHostViewModel == null)
            return true;

        try
        {
            var flush = FlushAllAsync();
            if (!flush.IsCompleted)
            {
                using var cancelDelay = new System.Threading.CancellationTokenSource();
                var finished = await Task.WhenAny(flush, Task.Delay(timeout, cancelDelay.Token));
                cancelDelay.Cancel();
                if (finished != flush)
                    return false;
            }
            return await flush;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Saves everything before an installed update restarts Hymnal: the open file (unless it changed on disk while
    /// it had edits, which needs the author's choice), then pending notes, schedule, idea and beat edits. False when
    /// anything couldn't be saved, so the restart doesn't happen.
    /// </summary>
    public async Task<bool> SaveAllBeforeRestartAsync(TimeSpan timeout)
    {
        if (EditorViewModel.IsDirty)
        {
            if (EditorViewModel.HasConflict || !EditorViewModel.CanSave)
                return false;
            try
            {
                await EditorViewModel.SaveAsync();
            }
            catch
            {
                return false; // SaveAsync has shown the error
            }
        }

        try
        {
            var notes = await NotesViewModel.FlushAsync();
            var schedule = await ChapterInfoViewModel.FlushScheduleAsync();
            var pending = await FlushPendingEditsBeforeCloseAsync(timeout);
            return notes && schedule && pending && !EditorViewModel.IsDirty;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> FlushAllAsync()
    {
        var ideas = IdeasViewModel == null ? Task.FromResult(true) : FlushAfterPromoteAsync(IdeasViewModel);
        var beats = PlanHostViewModel == null ? Task.FromResult(true) : PlanHostViewModel.FlushAsync();
        var results = await Task.WhenAll(ideas, beats);
        return results[0] && results[1];
    }

    /// <summary>Waits for an in-flight promote (it saves ideas itself), then writes pending idea edits.</summary>
    private static async Task<bool> FlushAfterPromoteAsync(IdeasViewModel ideas)
    {
        await ideas.PromoteCompletion;
        return await ideas.TryFlushAsync();
    }

    // ── Write layout persistence ──────────────────────────────────────────────

    private async Task RestoreWriteLayoutAsync()
    {
        try
        {
            var stored = await _settingsStore.GetAsync<WriteLayoutSettings>("writeLayout").ConfigureAwait(false);
            if (stored != null)
            {
                stored.LeftSidebarWidth  = Clamp(stored.LeftSidebarWidth,  WriteLayoutSettings.MinSidebarWidth, WriteLayoutSettings.MaxSidebarWidth);
                stored.RightSidebarWidth = Clamp(stored.RightSidebarWidth, WriteLayoutSettings.MinSidebarWidth, WriteLayoutSettings.MaxSidebarWidth);
                stored.LeftPaneTopStar     = Math.Max(0.01, stored.LeftPaneTopStar);
                stored.LeftPaneBottomStar  = Math.Max(0.01, stored.LeftPaneBottomStar);
                stored.RightPaneTopStar    = Math.Max(0.01, stored.RightPaneTopStar);
                stored.RightPaneBottomStar = Math.Max(0.01, stored.RightPaneBottomStar);
                stored.RightPaneChatStar   = Math.Max(0.01, stored.RightPaneChatStar);
                CurrentWriteLayout = stored;
            }
        }
        catch
        {
            // Non-fatal; defaults remain.
        }
    }

    /// <summary>
    /// Persists a new layout snapshot, called from code-behind after debouncing splitter moves.
    /// </summary>
    public async Task PersistWriteLayoutAsync(WriteLayoutSettings layout)
    {
        try
        {
            _currentWriteLayout = layout;
            await _settingsStore.SetAsync("writeLayout", layout).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; splitter position may not survive restart if storage fails.
        }
    }

    private async Task ResetWriteLayoutAsync()
    {
        var defaults = WriteLayoutSettings.CreateDefault();

        // WorkspaceViewModel and SupplementalDocsViewModel auto-persist on setter.
        _workspaceVm.IsChaptersPaneVisible = true;
        _supplementalDocsVm.IsVisible = false;

        // Notes and ChapterInfo have private setters; use the internal helpers.
        _notesVm.ApplyVisibility(false);
        await _notesVm.PersistVisibilityAsync().ConfigureAwait(false);
        _chapterInfoVm.ApplyVisibility(false);
        await _chapterInfoVm.PersistVisibilityAsync().ConfigureAwait(false);

        // Hide AI chat panel
        AiChatViewModel.IsVisible = false;

        // Persist and broadcast the geometry defaults.
        await PersistWriteLayoutAsync(defaults).ConfigureAwait(false);
        CurrentWriteLayout = defaults;
        _layoutReset.OnNext(defaults);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}

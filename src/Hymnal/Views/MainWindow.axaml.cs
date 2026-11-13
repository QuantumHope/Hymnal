using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hymnal.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Hymnal.Views;

public partial class MainWindow : Window
{
    private readonly CompositeDisposable _layoutDisposables = new();

    // Suppresses LayoutUpdated capture while we are programmatically applying stored/reset sizes.
    private bool _suppressLayoutCapture;
    private readonly Subject<Unit> _layoutChangedSubject = new();

    public MainWindow()
    {
        InitializeComponent();

        Closing += OnClosingFlushPendingEdits;

        Closed += (_, _) =>
        {
            _layoutDisposables.Dispose();
            _layoutChangedSubject.Dispose();
        };

        // RightPaneGrid row heights must track ChapterInfoViewModel.IsVisible and
        // NotesViewModel.IsVisible so that a hidden pane collapses to 0 and the
        // visible pane fills all available space.  RowDefinition.Height binding in
        // AXAML does not reliably inherit DataContext; we drive it from code-behind.
        //
        // ShellGrid column widths must also follow sidebar/pane visibility and the
        // active shell mode so Manage can hide both sidebars entirely.
        //
        // InitializeComponent() populates the named fields before DataContextChanged
        // fires, so the grid references are always valid.
        DataContextChanged += (_, _) =>
        {
            _layoutDisposables.Clear();

            if (DataContext is MainWindowViewModel vm)
            {
                SetupColumnWidths(vm);
                SetupLeftPaneRowHeights(vm);
                SetupRightPaneRowHeights(vm);
                SetupLayoutPersistence(vm);
                SetupFocusMode(vm);

                vm.ShowQuickIdeaDialogAsync = () => QuickIdeaDialog.ShowAsync(this);

                // About dialog — needs Window reference so it's wired here, not in the VM.
                _layoutDisposables.Add(
                    vm.ShowAboutCommand.Subscribe(_ =>
                    {
                        var dlg = new AboutDialog();
                        dlg.ShowDialog(this);
                    }));
            }
        };
    }

    private static readonly TimeSpan CloseFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Set once pending edits are saved (or the author chose to close anyway).</summary>
    private bool _closeAllowed;

    /// <summary>
    /// Holds the close until debounced edits (story ideas) are written; if they can't be written in
    /// time, asks whether to close anyway. Cancelling keeps the window open.
    /// </summary>
    private async void OnClosingFlushPendingEdits(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAllowed || DataContext is not MainWindowViewModel vm)
            return;

        var flush = vm.FlushPendingEditsBeforeCloseAsync(CloseFlushTimeout);
        if (flush.IsCompleted && flush.Result)
            return;

        e.Cancel = true;
        if (!await flush
            && !await ConfirmDialog.ShowAsync(this, "Unsaved Edits", "Idea or beat edits couldn't be saved. Close anyway?", "Close"))
            return;

        _closeAllowed = true;
        Close();
    }

    public static async Task ExecuteGitSyncActionAsync(GitPanelViewModel gitPanelViewModel, string? commitMessage)
        => await gitPanelViewModel.SyncAsync(commitMessage);

    private async void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.GitPanelViewModel.IsVisible)
            return;

        var gitPanel = vm.GitPanelViewModel;
        if (!gitPanel.CanSync)
            return;

        if (!gitPanel.ShouldOpenSyncDialog())
        {
            await ExecuteGitSyncActionAsync(gitPanel, null);
            return;
        }

        var dialog = new GitCommitDialog(
            gitPanel.CreateDefaultCommitMessage(),
            gitPanel.ChangedFiles);
        await dialog.ShowDialog(this);

        if (dialog.Result is not GitCommitDialogAction.Sync)
            return;

        await ExecuteGitSyncActionAsync(gitPanel, dialog.CommitMessage);
    }

    // ── Column widths ─────────────────────────────────────────────────────────
    // Merged from the former SetupModeChrome + SetupSidebarWidth + SetupRightPaneColumnWidth.
    // All column-width decisions now go through one place to avoid races between the
    // three former subscribers.

    private void SetupColumnWidths(MainWindowViewModel vm)
    {
        var grid = ShellGrid;
        if (grid is null || grid.ColumnDefinitions.Count < 5) return;

        void ApplyColumnWidths(ShellMode mode, bool anyLeftOpen, bool anyRightOpen, WriteLayoutSettings layout)
        {
            // Focus mode hides both rails too; the stored widths stay in the layout for when it ends (the layout
            // capture never saves a rail narrower than MinSidebarWidth, so the zero widths aren't persisted).
            if (mode is ShellMode.Manage or ShellMode.Plan or ShellMode.Research or ShellMode.Ideas || vm.IsFocusMode)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(0);
                grid.ColumnDefinitions[1].Width = new GridLength(0);
                grid.ColumnDefinitions[3].Width = new GridLength(0);
                grid.ColumnDefinitions[4].Width = new GridLength(0);
                return;
            }

            // Splitter columns are always 4px in Write mode.
            grid.ColumnDefinitions[1].Width = new GridLength(4);
            grid.ColumnDefinitions[3].Width = new GridLength(4);

            // Left sidebar: expanded uses stored width; collapsed uses 48px icon rail.
            grid.ColumnDefinitions[0].Width = anyLeftOpen
                ? new GridLength(layout.LeftSidebarWidth)
                : new GridLength(48);

            // Right sidebar: expanded uses stored width; collapsed uses 48px icon rail.
            grid.ColumnDefinitions[4].Width = anyRightOpen
                ? new GridLength(layout.RightSidebarWidth)
                : new GridLength(48);
        }

        ApplyColumnWidths(vm.ActiveMode, vm.IsAnyLeftPaneOpen, vm.IsAnyRightPaneOpen, vm.CurrentWriteLayout);

        _layoutDisposables.Add(
            vm.WhenAnyValue(
                    x => x.ActiveMode,
                    x => x.IsAnyLeftPaneOpen,
                    x => x.IsAnyRightPaneOpen,
                    x => x.CurrentWriteLayout)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(
                    t => ApplyColumnWidths(t.Item1, t.Item2, t.Item3, t.Item4),
                    _ => { /* non-fatal */ }));

        _layoutDisposables.Add(
            vm.WhenAnyValue(x => x.IsFocusMode)
                .Skip(1)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(
                    _ => ApplyColumnWidths(vm.ActiveMode, vm.IsAnyLeftPaneOpen, vm.IsAnyRightPaneOpen, vm.CurrentWriteLayout),
                    _ => { /* non-fatal */ }));
    }

    // ── Focus mode ────────────────────────────────────────────────────────────

    private WindowState _stateBeforeFocus = WindowState.Normal;

    /// <summary>Focus mode fills the screen; leaving it puts the window back as it was.</summary>
    private void SetupFocusMode(MainWindowViewModel vm)
    {
        _layoutDisposables.Add(
            vm.WhenAnyValue(x => x.IsFocusMode)
                .Skip(1)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(on =>
                {
                    // The layout capture runs on LayoutUpdated; keep it from seeing the rails mid-switch.
                    _suppressLayoutCapture = true;
                    try
                    {
                        if (on)
                        {
                            _stateBeforeFocus = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
                            WindowState = WindowState.FullScreen;
                        }
                        else if (WindowState == WindowState.FullScreen)
                        {
                            WindowState = _stateBeforeFocus;
                        }
                    }
                    finally
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            () => _suppressLayoutCapture = false, Avalonia.Threading.DispatcherPriority.Background);
                    }
                }, _ => { /* non-fatal */ }));
    }

    // ── Left-pane row heights ──────────────────────────────────────────────────

    private void SetupLeftPaneRowHeights(MainWindowViewModel vm)
    {
        var outerGrid = LeftPaneGrid;
        var chaptersSection = LeftChaptersSectionGrid;
        var docsSection = LeftDocsSectionGrid;
        if (outerGrid is null || chaptersSection is null || docsSection is null)
            return;

        void ApplyLayout(bool chaptersVisible, bool docsVisible, WriteLayoutSettings layout)
        {
            chaptersSection.RowDefinitions[1].Height =
                chaptersVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            docsSection.RowDefinitions[1].Height =
                docsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            if (chaptersVisible && docsVisible)
            {
                // Both open: restore stored split ratio.
                outerGrid.RowDefinitions[0].Height = new GridLength(layout.LeftPaneTopStar, GridUnitType.Star);
                outerGrid.RowDefinitions[2].Height = new GridLength(layout.LeftPaneBottomStar, GridUnitType.Star);
            }
            else if (chaptersVisible)
            {
                outerGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                outerGrid.RowDefinitions[2].Height = GridLength.Auto;
            }
            else if (docsVisible)
            {
                outerGrid.RowDefinitions[0].Height = GridLength.Auto;
                outerGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            }
        }

        ApplyLayout(
            vm.WorkspaceViewModel.IsChaptersPaneVisible,
            vm.SupplementalDocsViewModel.IsVisible,
            vm.CurrentWriteLayout);

        _layoutDisposables.Add(
            vm.WorkspaceViewModel
                .WhenAnyValue(x => x.IsChaptersPaneVisible)
                .CombineLatest(
                    vm.SupplementalDocsViewModel.WhenAnyValue(x => x.IsVisible),
                    vm.WhenAnyValue(x => x.CurrentWriteLayout),
                    (chapters, docs, layout) => (chapters, docs, layout))
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(
                    state => ApplyLayout(state.chapters, state.docs, state.layout),
                    _ => { /* non-fatal */ }));
    }

    // ── Right-pane row heights ─────────────────────────────────────────────────

    private const double BeatsRowMinHeight = 180;

    private void SetupRightPaneRowHeights(MainWindowViewModel vm)
    {
        var grid = RightPaneGrid;   // x:Name="RightPaneGrid" field from generated partial class
        if (grid is null) return;

        // Notes and AI Chat content visibility are driven from code-behind (compiled binding limitation in child views).
        if (NotesViewContent is not null)
        {
            NotesViewContent.IsVisible = vm.NotesViewModel.IsVisible;
            _layoutDisposables.Add(
                vm.NotesViewModel
                    .WhenAnyValue(x => x.IsVisible)
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(v => NotesViewContent.IsVisible = v));
        }

        if (AiChatViewContent is not null)
        {
            AiChatViewContent.IsVisible = vm.AiChatViewModel.IsVisible;
            _layoutDisposables.Add(
                vm.AiChatViewModel
                    .WhenAnyValue(x => x.IsVisible)
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(v => AiChatViewContent.IsVisible = v));
        }

        if (WriteBeatsViewContent is not null)
        {
            WriteBeatsViewContent.IsVisible = vm.IsBeatsPanelOpen;
            _layoutDisposables.Add(
                vm.WhenAnyValue(x => x.IsBeatsPanelOpen)
                    .ObserveOn(AvaloniaScheduler.Instance)
                    .Subscribe(v => WriteBeatsViewContent.IsVisible = v));
        }

        // ChapterPanelContentControl.IsVisible is bound in AXAML to IsChapterPanelOpen;
        // we only need to drive the row heights here.

        // Outlook-style layout: 7 rows — Info | splitter | Beats | splitter | Notes | splitter | AI Chat
        // Rows 0, 2, 4, 6: content rows (* or Auto — managed here). Beats exists for story/anthology only.

        void ApplyRowHeights(bool chPanelVisible, bool beatsVisible, bool notesVisible, bool chatVisible,
            WriteLayoutSettings layout)
        {
            var openCount = (chPanelVisible ? 1 : 0) + (beatsVisible ? 1 : 0) + (notesVisible ? 1 : 0) + (chatVisible ? 1 : 0);
            var usePersistedStars = openCount >= 2;

            GridLength Row(bool visible, double star) => visible
                ? new GridLength(usePersistedStars ? star : 1.0, GridUnitType.Star)
                : GridLength.Auto;

            grid.RowDefinitions[0].Height = Row(chPanelVisible, layout.RightPaneTopStar);
            grid.RowDefinitions[2].Height = Row(beatsVisible, layout.RightPaneBeatsStar);
            // Saved weights are often pixel-sized (hundreds) while a never-resized Beats row is 1, which would
            // squeeze it to nothing beside them; while open it keeps room for its header and a few beats.
            grid.RowDefinitions[2].MinHeight = beatsVisible ? BeatsRowMinHeight : 0;
            grid.RowDefinitions[4].Height = Row(notesVisible, layout.RightPaneBottomStar);
            grid.RowDefinitions[6].Height = Row(chatVisible, layout.RightPaneChatStar);
        }

        ApplyRowHeights(
            vm.IsChapterPanelOpen,
            vm.IsBeatsPanelOpen,
            vm.NotesViewModel.IsVisible,
            vm.AiChatViewModel.IsVisible,
            vm.CurrentWriteLayout);

        _layoutDisposables.Add(
            vm.WhenAnyValue(x => x.IsChapterPanelOpen, x => x.IsBeatsPanelOpen, (chPanel, beats) => (chPanel, beats))
                .CombineLatest(
                    vm.NotesViewModel.WhenAnyValue(x => x.IsVisible),
                    vm.AiChatViewModel.WhenAnyValue(x => x.IsVisible),
                    vm.WhenAnyValue(x => x.CurrentWriteLayout),
                    (top, notes, chat, layout) => (top.chPanel, top.beats, notes, chat, layout))
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(
                    state => ApplyRowHeights(state.chPanel, state.beats, state.notes, state.chat, state.layout),
                    _ => { /* non-fatal */ }));
    }

    // ── Layout persistence ────────────────────────────────────────────────────
    // Captures user-driven splitter positions (debounced) and persists them.
    // Also re-applies grid geometry when the stored layout changes (async restore / reset).

    private void SetupLayoutPersistence(MainWindowViewModel vm)
    {
        var shellGrid  = ShellGrid;
        var leftOuter  = LeftPaneGrid;
        var rightPane  = RightPaneGrid;

        if (shellGrid is null) return;

        // Re-apply geometry whenever CurrentWriteLayout changes (async restore + reset).
        // The subscriptions in SetupColumnWidths / SetupLeftPaneRowHeights /
        // SetupRightPaneRowHeights already react to CurrentWriteLayout, so we only
        // need to suppress the capture that LayoutUpdated would otherwise trigger.
        _layoutDisposables.Add(
            vm.WhenAnyValue(x => x.CurrentWriteLayout)
                .Skip(1)                        // skip the initial value; the setup calls already applied it
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => SuppressNextCapture(), _ => { /* non-fatal */ }));

        // When a reset fires, suppress capture to prevent re-persisting the defaults.
        _layoutDisposables.Add(
            vm.WriteLayoutReset
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => SuppressNextCapture(), _ => { /* non-fatal */ }));

        // Capture user-driven layout changes via LayoutUpdated, debounced.
        _layoutDisposables.Add(
            _layoutChangedSubject
                .Throttle(TimeSpan.FromMilliseconds(500), TaskPoolScheduler.Default)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => CaptureAndPersistLayout(vm, shellGrid, leftOuter, rightPane),
                    _ => { /* non-fatal */ }));

        shellGrid.LayoutUpdated += OnShellGridLayoutUpdated;
        _layoutDisposables.Add(Disposable.Create(() =>
            shellGrid.LayoutUpdated -= OnShellGridLayoutUpdated));
    }

    private void OnShellGridLayoutUpdated(object? sender, EventArgs e)
    {
        if (_suppressLayoutCapture) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.ActiveMode != ShellMode.Write) return;
        _layoutChangedSubject.OnNext(Unit.Default);
    }

    private void SuppressNextCapture()
    {
        _suppressLayoutCapture = true;
        Dispatcher.UIThread.Post(
            () => _suppressLayoutCapture = false,
            DispatcherPriority.Background);
    }

    private void CaptureAndPersistLayout(
        MainWindowViewModel vm,
        Grid shellGrid,
        Grid? leftOuter,
        Grid? rightPane)
    {
        // Start from the current persisted layout so we only overwrite visible panes.
        var current = vm.CurrentWriteLayout;
        var captured = new WriteLayoutSettings
        {
            LeftSidebarWidth  = current.LeftSidebarWidth,
            RightSidebarWidth = current.RightSidebarWidth,
            LeftPaneTopStar     = current.LeftPaneTopStar,
            LeftPaneBottomStar  = current.LeftPaneBottomStar,
            RightPaneTopStar    = current.RightPaneTopStar,
            RightPaneBeatsStar  = current.RightPaneBeatsStar,
            RightPaneBottomStar = current.RightPaneBottomStar,
            RightPaneChatStar   = current.RightPaneChatStar,
        };

        // Capture left sidebar width only when a pane is open (avoid persisting 48).
        if (vm.IsAnyLeftPaneOpen && shellGrid.ColumnDefinitions.Count > 0)
        {
            var w = shellGrid.ColumnDefinitions[0].Width;
            if (w.GridUnitType == GridUnitType.Pixel && w.Value >= WriteLayoutSettings.MinSidebarWidth)
                captured.LeftSidebarWidth = w.Value;
        }

        // Capture right sidebar width only when a pane is open.
        if (vm.IsAnyRightPaneOpen && shellGrid.ColumnDefinitions.Count >= 5)
        {
            var w = shellGrid.ColumnDefinitions[4].Width;
            if (w.GridUnitType == GridUnitType.Pixel && w.Value >= WriteLayoutSettings.MinSidebarWidth)
                captured.RightSidebarWidth = w.Value;
        }

        // Capture left-pane vertical split only when both panes are open.
        if (vm.IsBothLeftPanesOpen && leftOuter?.RowDefinitions.Count >= 3)
        {
            var top = leftOuter.RowDefinitions[0].Height;
            var bot = leftOuter.RowDefinitions[2].Height;
            if (top.IsStar && bot.IsStar && top.Value > 0 && bot.Value > 0)
            {
                captured.LeftPaneTopStar    = top.Value;
                captured.LeftPaneBottomStar = bot.Value;
            }
        }

        // Capture right-pane vertical star weights for each open content row.
        if (rightPane?.RowDefinitions.Count >= 7)
        {
            var top = rightPane.RowDefinitions[0].Height;
            if (top.IsStar && top.Value > 0)
                captured.RightPaneTopStar = top.Value;

            var beats = rightPane.RowDefinitions[2].Height;
            if (beats.IsStar && beats.Value > 0)
                captured.RightPaneBeatsStar = beats.Value;

            var mid = rightPane.RowDefinitions[4].Height;
            if (mid.IsStar && mid.Value > 0)
                captured.RightPaneBottomStar = mid.Value;

            var chat = rightPane.RowDefinitions[6].Height;
            if (chat.IsStar && chat.Value > 0)
                captured.RightPaneChatStar = chat.Value;
        }

        _ = vm.PersistWriteLayoutAsync(captured);
    }

    // ── Collection menu ───────────────────────────────────────────────────────
    // Name the open collection (book, story), open another collection, add a story that is in no collection,
    // publish to LeanPub.

    private async void CollectionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || DataContext is not MainWindowViewModel { CollectionsViewModel: { } collections } mainVm
            || !mainVm.WorkspaceViewModel.HasWorkspace)
            return;

        var workspace = mainVm.WorkspaceViewModel;
        var noun = workspace.Profile.CollectionNoun.ToLowerInvariant();
        await collections.RefreshAsync();

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem { Header = workspace.CollectionDisplayName, IsEnabled = false });
        flyout.Items.Add(new Separator());
        flyout.Items.Add(CollectionMenuItem(
            collections.IsOpenCollectionNamed ? $"Rename this {noun}…" : $"Name this {noun}…",
            () => NameOpenCollectionAsync(collections, workspace)));

        if (collections.SupportsMultiple)
        {
            foreach (var other in collections.Collections.Where(c => !c.IsActive).ToList())
                flyout.Items.Add(CollectionMenuItem($"Open “{other.Title}”",
                    () => OpenCollectionAsync(collections, workspace, other.Slug)));
            flyout.Items.Add(CollectionMenuItem("New collection…", () => NewCollectionAsync(collections, workspace)));

            var unassigned = await collections.UnassignedAsync();
            if (unassigned.Count > 0)
            {
                var add = new MenuItem { Header = $"Add a story in no collection ({unassigned.Count})" };
                foreach (var path in unassigned)
                    add.Items.Add(CollectionMenuItem(path, () => collections.AddToOpenAsync(path)));
                flyout.Items.Add(add);
            }
        }

        flyout.Items.Add(new Separator());
        flyout.Items.Add(CollectionMenuItem("Publish to LeanPub…",
            () => PublishDialog.ShowAsync(this, collections, workspace.CollectionDisplayName)));
        if (mainVm.SubmissionExportViewModel is { IsAvailable: true } export)
        {
            // Short Story: the story. Anthology: the story being written (or focused); a story's own
            // right-click menu in the sidebar exports any other.
            var label = workspace.Profile.Kind == Hymnal.Core.Models.ProjectKind.Story
                ? "Export for submission…"
                : "Export this story for submission…";
            flyout.Items.Add(CollectionMenuItem(label, () => SubmissionExportDialog.ShowAsync(this, export)));
        }
        flyout.ShowAt(button);
    }

    private static MenuItem CollectionMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private async Task<bool> NameOpenCollectionAsync(CollectionsViewModel collections, WorkspaceViewModel workspace,
        string? prompt = null)
    {
        var noun = workspace.Profile.CollectionNoun.ToLowerInvariant();
        var title = await FileNameDialog.ShowAsync(this, $"Name this {noun}",
            prompt ?? $"The {noun}'s title, shown in Hymnal's sidebar, Plan and Schedule.",
            collections.IsOpenCollectionNamed ? workspace.CollectionTitle : workspace.WorkspaceName);
        return title != null && await collections.RenameOpenAsync(title);
    }

    private async Task OpenCollectionAsync(CollectionsViewModel collections, WorkspaceViewModel workspace, string slug)
    {
        // The open list needs a folder of its own before another collection takes its place.
        if (!collections.IsOpenCollectionNamed
            && !await NameOpenCollectionAsync(collections, workspace,
                "Name this collection first, so its list of stories is kept when you open another."))
            return;
        await collections.OpenAsync(slug);
    }

    private async Task NewCollectionAsync(CollectionsViewModel collections, WorkspaceViewModel workspace)
    {
        var title = await FileNameDialog.ShowAsync(this, "New collection",
            "Its title. It starts empty: add stories from the shared folder once it is open.", "");
        if (title == null || await collections.CreateAsync(title) is not { } slug)
            return;
        if (await ConfirmDialog.ShowAsync(this, "New collection", $"Open “{title}” now?", "Open"))
            await OpenCollectionAsync(collections, workspace, slug);
    }

    // ── Chapter add button ────────────────────────────────────────────────────

    private async void AddChapterButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainWindowViewModel mainVm)
            return;

        var workspace = mainVm.WorkspaceViewModel;
        if (!workspace.HasWorkspace)
            return;

        var flyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = $"New {workspace.Profile.UnitNoun}…", Tag = "chapter" },
                new MenuItem { Header = $"New {workspace.Profile.PartNoun}…", Tag = "part" },
                new MenuItem { Header = "Include Existing File…", Tag = "include" }
            }
        };

        foreach (var item in flyout.Items.OfType<MenuItem>())
            item.Click += AddChapterMenuItem_Click;

        flyout.Closed += (_, _) =>
        {
            foreach (var item in flyout.Items.OfType<MenuItem>())
                item.Click -= AddChapterMenuItem_Click;
        };

        flyout.ShowAt(button);
    }

    private async void AddChapterMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel mainVm)
            return;

        var workspace = mainVm.WorkspaceViewModel;
        if (!workspace.HasWorkspace)
            return;

        var action = sender is MenuItem { Tag: string tag } ? tag : null;
        switch (action)
        {
            case "chapter":
                await CreateSidebarChapterAsync(workspace);
                break;
            case "part":
                await CreateSidebarPartAsync(workspace);
                break;
            case "include":
                await IncludeSidebarFileAsync(workspace);
                break;
        }
    }

    private async Task CreateSidebarChapterAsync(WorkspaceViewModel workspace)
    {
        var dialogResult = await NewChapterDialog.ShowAsync(
            this,
            NewManuscriptEntryKind.Chapter,
            $"New {workspace.Profile.UnitNoun}",
            $"Enter the new {workspace.Profile.UnitNoun.ToLowerInvariant()} path relative to the project folder.",
            $"{workspace.Profile.NewFileStem}.md");

        if (dialogResult is null)
            return;

        var title = dialogResult.Title
            ?? Path.GetFileNameWithoutExtension(dialogResult.FilePath).Replace('-', ' ').Replace('_', ' ');
        var content = $"# {title}\n\n";
        var index = workspace.GetBookEntryCount();

        await ExecuteCommandAsync(workspace.CreateChapterCommand.Execute(
            new CreateChapterRequest(dialogResult.FilePath, content, index)));
    }

    private async Task CreateSidebarPartAsync(WorkspaceViewModel workspace)
    {
        var dialogResult = await NewChapterDialog.ShowAsync(
            this,
            NewManuscriptEntryKind.Part,
            $"New {workspace.Profile.PartNoun}",
            $"Enter the {workspace.Profile.PartNoun.ToLowerInvariant()} divider path and title.",
            workspace.Profile.NewPartPath);

        if (dialogResult is null || string.IsNullOrWhiteSpace(dialogResult.Title))
            return;

        var index = workspace.GetBookEntryCount();
        await ExecuteCommandAsync(workspace.CreatePartCommand.Execute(
            new CreatePartRequest(dialogResult.FilePath, dialogResult.Title, index)));
    }

    private async Task IncludeSidebarFileAsync(WorkspaceViewModel workspace)
    {
        var absolutePath = await workspace.PickManuscriptFileAsync();
        if (string.IsNullOrWhiteSpace(absolutePath))
            return;

        var relativePath = workspace.ToManuscriptRelativePath(absolutePath);
        var index = workspace.GetBookEntryCount();
        await ExecuteCommandAsync(workspace.IncludeExistingFileCommand.Execute(
            new IncludeExistingChapterRequest(relativePath, index)));
    }

    private static async Task ExecuteCommandAsync(IObservable<Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }
}

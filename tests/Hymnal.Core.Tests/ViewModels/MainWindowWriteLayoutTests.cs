using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;
using ReactiveUI.Builder;
using Xunit;
using CoreUnit = Hymnal.Core.Common.Unit;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

/// <summary>
/// Tests for Write-tab layout persistence and reset.
/// </summary>
public sealed class MainWindowWriteLayoutTests : IDisposable
{
    private readonly TestContext _context = new();

    static MainWindowWriteLayoutTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentWriteLayout_StartsWithDefaults_WhenNoSettingsStored()
    {
        var window = _context.CreateMainWindow();

        Assert.Equal(WriteLayoutSettings.DefaultLeftSidebarWidth,  window.CurrentWriteLayout.LeftSidebarWidth);
        Assert.Equal(WriteLayoutSettings.DefaultRightSidebarWidth, window.CurrentWriteLayout.RightSidebarWidth);
        Assert.Equal(1.0, window.CurrentWriteLayout.LeftPaneTopStar);
        Assert.Equal(1.0, window.CurrentWriteLayout.LeftPaneBottomStar);
        Assert.Equal(1.0, window.CurrentWriteLayout.RightPaneTopStar);
        Assert.Equal(1.0, window.CurrentWriteLayout.RightPaneBottomStar);
    }

    // ── Persist + restore ─────────────────────────────────────────────────────

    [Fact]
    public async Task PersistWriteLayoutAsync_RoundTripsSettingsStore()
    {
        var window = _context.CreateMainWindow();

        var layout = new WriteLayoutSettings
        {
            LeftSidebarWidth    = 300,
            RightSidebarWidth   = 350,
            LeftPaneTopStar     = 2.0,
            LeftPaneBottomStar  = 1.0,
            RightPaneTopStar    = 1.5,
            RightPaneBottomStar = 0.5,
        };

        await window.PersistWriteLayoutAsync(layout);

        var stored = await _context.SettingsStore.GetAsync<WriteLayoutSettings>("writeLayout");
        Assert.NotNull(stored);
        Assert.Equal(300,  stored!.LeftSidebarWidth);
        Assert.Equal(350,  stored.RightSidebarWidth);
        Assert.Equal(2.0,  stored.LeftPaneTopStar);
        Assert.Equal(1.0,  stored.LeftPaneBottomStar);
        Assert.Equal(1.5,  stored.RightPaneTopStar);
        Assert.Equal(0.5,  stored.RightPaneBottomStar);
    }

    [Fact]
    public async Task CurrentWriteLayout_UpdatedByPersist()
    {
        var window = _context.CreateMainWindow();
        var layout = new WriteLayoutSettings { LeftSidebarWidth = 310, RightSidebarWidth = 360 };

        await window.PersistWriteLayoutAsync(layout);

        Assert.Equal(310, window.CurrentWriteLayout.LeftSidebarWidth);
        Assert.Equal(360, window.CurrentWriteLayout.RightSidebarWidth);
    }

    [Fact]
    public async Task RestoredLayout_AppliedAfterInitialize_WhenSettingsPreSeeded()
    {
        var preSeeded = new WriteLayoutSettings { LeftSidebarWidth = 400, RightSidebarWidth = 500 };
        await _context.SettingsStore.SetAsync("writeLayout", preSeeded);

        // Creating the main window triggers InitializeAsync which calls RestoreWriteLayoutAsync.
        var window = _context.CreateMainWindow();

        // Wait for the async restore to propagate.
        Assert.True(
            SpinWait.SpinUntil(
                () => window.CurrentWriteLayout.LeftSidebarWidth == 400,
                TimeSpan.FromSeconds(2)),
            "Expected LeftSidebarWidth to be restored to 400");

        Assert.Equal(500, window.CurrentWriteLayout.RightSidebarWidth);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetWriteLayoutCommand_RestoresDefaultPaneVisibility()
    {
        _context.EnableWorkspace();
        var window = _context.CreateMainWindow();

        // Open all panes (using the internal helpers or public setters).
        window.SupplementalDocsViewModel.IsVisible = true;
        window.NotesViewModel.ApplyVisibility(true);
        window.ChapterInfoViewModel.ApplyVisibility(true);

        await ExecuteCommandAsync(window.ResetWriteLayoutCommand.Execute());

        Assert.True(window.WorkspaceViewModel.IsChaptersPaneVisible,  "Chapters pane should remain open");
        Assert.False(window.SupplementalDocsViewModel.IsVisible,       "Docs pane should be closed");
        Assert.False(window.NotesViewModel.IsVisible,                  "Notes pane should be closed");
        Assert.False(window.ChapterInfoViewModel.IsVisible,            "Chapter Info pane should be closed");
    }

    [Fact]
    public async Task ResetWriteLayoutCommand_SetsCurrentWriteLayoutToDefaults()
    {
        var window = _context.CreateMainWindow();
        await window.PersistWriteLayoutAsync(new WriteLayoutSettings { LeftSidebarWidth = 400, RightSidebarWidth = 500 });

        await ExecuteCommandAsync(window.ResetWriteLayoutCommand.Execute());

        Assert.Equal(WriteLayoutSettings.DefaultLeftSidebarWidth,  window.CurrentWriteLayout.LeftSidebarWidth);
        Assert.Equal(WriteLayoutSettings.DefaultRightSidebarWidth, window.CurrentWriteLayout.RightSidebarWidth);
    }

    [Fact]
    public async Task ResetWriteLayoutCommand_PersistsDefaultsToSettingsStore()
    {
        var window = _context.CreateMainWindow();
        await window.PersistWriteLayoutAsync(new WriteLayoutSettings { LeftSidebarWidth = 400 });

        await ExecuteCommandAsync(window.ResetWriteLayoutCommand.Execute());

        var stored = await _context.SettingsStore.GetAsync<WriteLayoutSettings>("writeLayout");
        Assert.NotNull(stored);
        Assert.Equal(WriteLayoutSettings.DefaultLeftSidebarWidth, stored!.LeftSidebarWidth);
    }

    [Fact]
    public async Task ResetWriteLayoutCommand_FiresLayoutResetObservable()
    {
        var window = _context.CreateMainWindow();
        WriteLayoutSettings? fired = null;
        window.WriteLayoutReset.Take(1).Subscribe(l => fired = l);

        await ExecuteCommandAsync(window.ResetWriteLayoutCommand.Execute());

        Assert.NotNull(fired);
        Assert.Equal(WriteLayoutSettings.DefaultLeftSidebarWidth, fired!.LeftSidebarWidth);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task ExecuteCommandAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    public void Dispose() => _context.Dispose();

    // ── Test context ──────────────────────────────────────────────────────────

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        // FakeAppSettingsStore and FakeMetadataStore are defined as private classes below.
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public NotificationService NotificationService { get; } = new();
        private FakeMetadataStore MetadataStore { get; } = new();
        public FakeFolderPickerService FolderPicker { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; }

        public TestContext()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-layout-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(WorkspaceRoot, "manuscript"));

            Editor = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            Workspace = new WorkspaceViewModel(
                _owned.Track(new ManuscriptService(NotificationService)),
                new FakeBookTxtStructureService(),
                new FakeFilePickerService(),
                SettingsStore,
                FolderPicker,
                NotificationService,
                Editor,
                new ChapterRegistryService(MetadataStore),
                new PhaseDataService(MetadataStore),
                new TargetsService(MetadataStore),
                WordCountService,
                new WordCountHistoryService(MetadataStore));
        }

        public MainWindowViewModel CreateMainWindow()
        {
            var docs = new SupplementalDocsViewModel(
                Workspace,
                new SupplementalDocsService(MetadataStore),
                Editor,
                NotificationService,
                SettingsStore,
                new FakeFilePickerService());

            var gitPanel = _owned.Track(new GitPanelViewModel(Workspace, Editor, new FakeGitService(), NotificationService));

            return new MainWindowViewModel(
                Workspace,
                Editor,
                new NotesViewModel(Editor, Workspace, NotificationService, SettingsStore),
                new ChapterInfoViewModel(Editor, Workspace, SettingsStore, NotificationService, new WordCountHistoryService(MetadataStore)),
                new BookStatsViewModel(Workspace, new WordCountHistoryService(MetadataStore)),
                new GanttViewModel(Workspace, NotificationService),
                new CorkboardViewModel(Workspace, new FakeBookTxtStructureService(), new OrphanFileDiscoveryService(), SettingsStore, NotificationService, _owned.Track(new ManuscriptService(NotificationService))),
                new ResearchViewModel(Workspace, docs, Editor, AiTestDoubles.CreateStubAiChatViewModel(Editor, Workspace, NotificationService)),
                docs,
                gitPanel,
                NotificationService,
                SettingsStore,
                AiTestDoubles.CreateStubAiChatViewModel(Editor, Workspace, NotificationService),
                AiTestDoubles.CreateStubSettingsFactory(NotificationService));
        }

        public void EnableWorkspace()
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, Path.Combine(WorkspaceRoot, "manuscript"));
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), "test-workspace");
            Editor.HasWorkspace = true;
        }

        public void Dispose()
        {
            try
            {
                _owned.Dispose();
            }
            finally
            {
                DeleteTempFolders();
            }
        }

        private void DeleteTempFolders()
        {
            try { if (Directory.Exists(WorkspaceRoot)) Directory.Delete(WorkspaceRoot, true); }
            catch { /* best-effort cleanup */ }
        }

        private static void SetPrivateField(object target, string name, object? value)
        {
            var field = target.GetType().GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(target, value);
        }

        private static void SetPrivateProperty(object target, string name, object? value)
        {
            var type = target.GetType();
            while (type != null)
            {
                var prop = type.GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop?.SetMethod != null)
                {
                    prop.SetValue(target, value);
                    return;
                }
                type = type.BaseType;
            }
        }
    }

    private sealed class FakeAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key)
        {
            if (_values.TryGetValue(key, out var value) && value is T typed)
                return Task.FromResult<T?>(typed);
            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMetadataStore : IMetadataStore
    {
        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            return File.WriteAllTextAsync(absolutePath, content);
        }
    }

    private sealed class FakeFolderPickerService : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeBookTxtStructureService : IBookTxtStructureService
    {
        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath)
            => Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));
        public Task<Result<CoreUnit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> RemoveEntryAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
        public Task<Result<CoreUnit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
    }

    private sealed class FakeGitService : NoOpGitService;
}

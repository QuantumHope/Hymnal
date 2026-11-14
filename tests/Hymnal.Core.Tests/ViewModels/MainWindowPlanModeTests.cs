using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DynamicData.Binding;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;
using ReactiveUI;
using ReactiveUI.Builder;
using Unit = Hymnal.Core.Common.Unit;
using Xunit;

namespace Hymnal.ViewModels;

public sealed class MainWindowPlanModeTests
{
    static MainWindowPlanModeTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task SelectPlanCommand_ExposesPlanSurfaceEvenWithoutWorkspace()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();

        Assert.True(window.IsEditorVisible);
        Assert.False(window.IsCorkboardVisible);
        Assert.False(window.IsGanttVisible);
        Assert.True(WaitForFirstCanExecute(window.SelectResearchCommand));

        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());

        Assert.Equal(ShellMode.Plan, window.ActiveMode);
        Assert.True(window.IsCorkboardVisible);
        Assert.False(window.IsEditorVisible);
        Assert.False(window.IsGanttVisible);
    }

    [Fact]
    public async Task ShellModes_StillToggleWriteManageAndBack()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();

        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.True(window.IsCorkboardVisible);

        await ExecuteCommandAsync(window.SelectManageCommand.Execute());
        Assert.Equal(ShellMode.Manage, window.ActiveMode);
        Assert.True(window.IsGanttVisible);
        Assert.False(window.IsEditorVisible);
        Assert.False(window.IsCorkboardVisible);

        await ExecuteCommandAsync(window.SelectWriteCommand.Execute());
        Assert.Equal(ShellMode.Write, window.ActiveMode);
        Assert.True(window.IsEditorVisible);
        Assert.False(window.IsGanttVisible);
        Assert.False(window.IsCorkboardVisible);
    }

    [Fact]
    public async Task PickingAModeWhileSettingsAreOpen_ClosesSettingsAndShowsThatMode()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();

        await ExecuteCommandAsync(window.OpenSettingsCommand.Execute());
        Assert.True(window.IsSettingsOpen);
        Assert.Null(window.HighlightedMode); // no tab claims a view that Settings is covering

        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.False(window.IsSettingsOpen);
        Assert.Null(window.ActiveSettingsViewModel);
        Assert.Equal(ShellMode.Plan, window.HighlightedMode);

        // The mode that is already active closes Settings too
        await ExecuteCommandAsync(window.OpenSettingsCommand.Execute());
        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.False(window.IsSettingsOpen);
        Assert.Equal(ShellMode.Plan, window.ActiveMode);

        await ExecuteCommandAsync(window.OpenSettingsCommand.Execute());
        await ExecuteCommandAsync(window.SelectManageCommand.Execute());
        Assert.False(window.IsSettingsOpen);
        Assert.True(window.IsGanttVisible);

        await ExecuteCommandAsync(window.OpenSettingsCommand.Execute());
        await ExecuteCommandAsync(window.SelectWriteCommand.Execute());
        Assert.False(window.IsSettingsOpen);
        Assert.True(window.IsEditorVisible);

        await ExecuteCommandAsync(window.OpenSettingsCommand.Execute());
        await ExecuteCommandAsync(window.SelectResearchCommand.Execute());
        Assert.False(window.IsSettingsOpen);
        Assert.Equal(ShellMode.Research, window.ActiveMode);
    }

    [Fact]
    public async Task OpenCardCommand_SelectsChapterAndReturnsToWrite()
    {
        using var context = new TestContext();
        var chapter = context.AddChapter("part-one/chapter-one.md", "Chapter One", "# Chapter One\n\nBody text.");
        context.SeedWorkspace(chapter);

        var window = context.CreateMainWindow();
        var board = window.CorkboardViewModel;

        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.True(window.IsCorkboardVisible);

        var card = Assert.IsType<ChapterCardItemViewModel>(board.Items.Single(item => item.RelativePath == chapter.Node.RelativePath));

        await ExecuteCommandAsync(board.OpenCardCommand.Execute(card));

        Assert.True(SpinWait.SpinUntil(() => context.EditorViewModel.ActiveNode == chapter.Node, TimeSpan.FromSeconds(2)));
        Assert.Equal(ShellMode.Write, window.ActiveMode);
        Assert.True(window.IsEditorVisible);
        Assert.Same(chapter, context.Workspace.SelectedNode);
        Assert.Same(chapter.Node, context.EditorViewModel.ActiveNode);
    }

    private static async Task ExecuteCommandAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    private static bool WaitForFirstCanExecute(ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> command)
    {
        var observed = false;
        using var subscription = command.CanExecute.Subscribe(value => observed = value);
        return observed;
    }

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public NotificationService NotificationService { get; } = new();
        public FakeMetadataStore MetadataStore { get; } = new();
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public FakeFolderPickerService FolderPickerService { get; } = new();
        public FakeFilePickerService FilePickerService { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel EditorViewModel { get; }
        public ChapterRegistryService RegistryService { get; }
        public PhaseDataService PhaseDataService { get; }
        public TargetsService TargetsService { get; }
        public WordCountHistoryService HistoryService { get; }
        public ManuscriptService ManuscriptService { get; }
        public SpyWorkspaceViewModel Workspace { get; }
        public FakeBookTxtStructureService StructureService { get; } = new();
        public string WorkspaceRoot { get; }
        public string ManuscriptRoot { get; }

        public TestContext()
        {
            var root = Path.Combine(Path.GetTempPath(), "hymnal-plan-tests", Guid.NewGuid().ToString("N"));
            WorkspaceRoot = root;
            ManuscriptRoot = Path.Combine(root, "manuscript");
            Directory.CreateDirectory(ManuscriptRoot);

            EditorViewModel = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            RegistryService = new ChapterRegistryService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            HistoryService = new WordCountHistoryService(MetadataStore);
            ManuscriptService = _owned.Track(new ManuscriptService(NotificationService));
            Workspace = new SpyWorkspaceViewModel(
                ManuscriptService,
                StructureService,
                FilePickerService,
                SettingsStore,
                FolderPickerService,
                NotificationService,
                EditorViewModel,
                RegistryService,
                PhaseDataService,
                TargetsService,
                WordCountService,
                HistoryService);

            SeedWorkspace(Array.Empty<ChapterViewModel>());
            SetWorkspaceModel(false);
        }

        public ChapterViewModel AddChapter(string relativePath, string title, string content)
        {
            var absolutePath = Path.Combine(ManuscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, content);

            var node = new ChapterNode(relativePath, relativePath, title, NodeKind.Chapter, IsMissing: false, Index: 0);
            var chapter = new ChapterViewModel(
                node,
                uuid: Guid.NewGuid().ToString("N"),
                phaseData: null,
                TestMetadataStores.Sidecar(PhaseDataService, TargetsService),
                SettingsStore,
                NotificationService,
                WorkspaceRoot);

            return chapter;
        }

        public void SeedWorkspace(params ChapterViewModel[] nodes)
        {
            var collection = GetWorkspaceNodeCollection(Workspace);
            collection.Clear();
            foreach (var node in nodes)
                collection.Add(node);

            SetWorkspaceModel(nodes.Length > 0);
        }

        public MainWindowViewModel CreateMainWindow()
        {
            var docs = new SupplementalDocsViewModel(
                Workspace,
                new SupplementalDocsService(MetadataStore),
                EditorViewModel,
                NotificationService,
                SettingsStore,
                FilePickerService);

            return new MainWindowViewModel(
                Workspace,
                EditorViewModel,
                new NotesViewModel(EditorViewModel, Workspace, NotificationService, SettingsStore),
                new ChapterInfoViewModel(EditorViewModel, Workspace, SettingsStore, NotificationService, new WordCountHistoryService(MetadataStore)),
                new BookStatsViewModel(Workspace, new WordCountHistoryService(MetadataStore)),
                new GanttViewModel(Workspace, NotificationService),
                new CorkboardViewModel(Workspace, StructureService, new OrphanFileDiscoveryService(), SettingsStore, NotificationService, ManuscriptService),
                new ResearchViewModel(Workspace, docs, EditorViewModel, AiTestDoubles.CreateStubAiChatViewModel(EditorViewModel, Workspace, NotificationService)),
                docs,
                _owned.Track(new GitPanelViewModel(Workspace, EditorViewModel, new FakeGitService(), NotificationService)),
                NotificationService,
                SettingsStore,
                AiTestDoubles.CreateStubAiChatViewModel(EditorViewModel, Workspace, NotificationService),
                AiTestDoubles.CreateStubSettingsFactory(NotificationService));
        }

        public CorkboardViewModel CreateCorkboard()
            => new(Workspace, StructureService, new OrphanFileDiscoveryService(), SettingsStore, NotificationService, ManuscriptService);

        private void SetWorkspaceModel(bool hasWorkspace)
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);

            SetPrivateField(Workspace, "_model", model);
            SetPrivateField(Workspace, "_hasWorkspace", hasWorkspace);
            SetPrivateField(Workspace, "_workspaceName", Path.GetFileName(WorkspaceRoot));
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
            try
            {
                if (Directory.Exists(WorkspaceRoot))
                    Directory.Delete(WorkspaceRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private sealed class SpyWorkspaceViewModel : WorkspaceViewModel
    {
        public SpyWorkspaceViewModel(
            ManuscriptService manuscriptService,
            IBookTxtStructureService structureService,
            IFilePickerService filePicker,
            IAppSettingsStore settingsStore,
            IFolderPickerService folderPicker,
            INotificationService notificationService,
            EditorViewModel editor,
            ChapterRegistryService registryService,
            PhaseDataService phaseDataService,
            TargetsService targetsService,
            WordCountService wordCountService,
            WordCountHistoryService historyService)
            : base(manuscriptService, structureService, filePicker, settingsStore, folderPicker, notificationService, editor, registryService, phaseDataService, targetsService, wordCountService, historyService)
        {
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

        public Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<Unit>.Ok(Unit.Default));
    }

    private sealed class FakeGitService : NoOpGitService;

    private static IList GetWorkspaceNodeCollection(WorkspaceViewModel workspace)
    {
        var field = typeof(WorkspaceViewModel).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workspace node collection field was not found.");

        return (IList)(field.GetValue(workspace)
            ?? throw new InvalidOperationException("Workspace node collection was null."));
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on WorkspaceViewModel.");

        field.SetValue(target, value);
    }
}

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

public sealed class MainWindowResearchModeTests
{
    static MainWindowResearchModeTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task SelectResearchCommand_ExposesResearchSurfaceEvenWithoutWorkspace()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();

        Assert.True(window.IsEditorVisible);
        Assert.False(window.IsResearchVisible);

        await ExecuteCommandAsync(window.SelectResearchCommand.Execute());

        Assert.Equal(ShellMode.Research, window.ActiveMode);
        Assert.True(window.IsResearchVisible);
        Assert.False(window.IsEditorVisible);
        Assert.False(window.IsCorkboardVisible);
        Assert.False(window.IsGanttVisible);
    }

    [Fact]
    public async Task SelectResearchCommand_ClosesActiveChapterAndClearsSelection()
    {
        using var context = new TestContext();
        var chapter = context.AddChapter("part-one/chapter-one.md", "Chapter One", "# Chapter One\n\nBody.");
        context.SeedWorkspace(chapter);
        var window = context.CreateMainWindow();
        var chapterPath = Path.Combine(context.ManuscriptRoot, "part-one/chapter-one.md");
        await context.EditorViewModel.OpenChapterAsync(chapter.Node, chapterPath);
        context.Workspace.SelectedNode = chapter;

        await ExecuteCommandAsync(window.SelectResearchCommand.Execute());

        Assert.Equal(ShellMode.Research, window.ActiveMode);
        Assert.True(window.IsResearchVisible);
        Assert.Null(context.EditorViewModel.ActiveNode);
        Assert.Null(context.EditorViewModel.ActiveFilePath);
        Assert.True(context.EditorViewModel.ShowNoResearchDocPrompt);
        Assert.Null(context.Workspace.SelectedNode);
    }

    [Fact]
    public async Task DocumentOpened_FromResearchMode_DoesNotSwitchToWrite()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();
        var docs = window.SupplementalDocsViewModel;
        context.CreateDoc("outline.md", "doc body");
        context.EnableWorkspace();
        await docs.RefreshAsync();
        var doc = Assert.Single(docs.Nodes);

        await ExecuteCommandAsync(window.SelectResearchCommand.Execute());
        Assert.Equal(ShellMode.Research, window.ActiveMode);

        await ExecuteCommandAsync(docs.OpenDocCommand.Execute(doc));

        Assert.Equal(ShellMode.Research, window.ActiveMode);
        Assert.True(window.IsResearchVisible);
        Assert.Equal(doc.AbsolutePath, context.EditorViewModel.ActiveFilePath);
    }

    [Fact]
    public async Task DocumentOpened_FromWriteMode_SwitchesToWrite()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();
        var docs = window.SupplementalDocsViewModel;
        context.CreateDoc("outline.md", "doc body");
        context.EnableWorkspace();
        await docs.RefreshAsync();
        var doc = Assert.Single(docs.Nodes);

        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.Equal(ShellMode.Plan, window.ActiveMode);

        await ExecuteCommandAsync(docs.OpenDocCommand.Execute(doc));

        Assert.Equal(ShellMode.Write, window.ActiveMode);
        Assert.True(window.IsEditorVisible);
    }

    [Fact]
    public async Task ShellModes_StillToggleResearchWritePlanAndManage()
    {
        using var context = new TestContext();
        var window = context.CreateMainWindow();

        await ExecuteCommandAsync(window.SelectResearchCommand.Execute());
        Assert.True(window.IsResearchVisible);

        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.True(window.IsCorkboardVisible);
        Assert.False(window.IsResearchVisible);

        await ExecuteCommandAsync(window.SelectManageCommand.Execute());
        Assert.Equal(ShellMode.Manage, window.ActiveMode);
        Assert.True(window.IsGanttVisible);

        await ExecuteCommandAsync(window.SelectWriteCommand.Execute());
        Assert.Equal(ShellMode.Write, window.ActiveMode);
        Assert.True(window.IsEditorVisible);
        Assert.False(window.IsResearchVisible);
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
        public SpyWorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; }
        public string ManuscriptRoot { get; }
        public string DocsRoot { get; }

        public TestContext()
        {
            var root = Path.Combine(Path.GetTempPath(), "hymnal-research-tests", Guid.NewGuid().ToString("N"));
            WorkspaceRoot = root;
            ManuscriptRoot = Path.Combine(root, "manuscript");
            DocsRoot = Path.Combine(root, ".hymnal-data", "docs");
            Directory.CreateDirectory(ManuscriptRoot);
            Directory.CreateDirectory(DocsRoot);

            EditorViewModel = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            Workspace = new SpyWorkspaceViewModel(
                _owned.Track(new ManuscriptService(NotificationService)),
                new FakeBookTxtStructureService(),
                FilePickerService,
                SettingsStore,
                FolderPickerService,
                NotificationService,
                EditorViewModel,
                new ChapterRegistryService(MetadataStore),
                new PhaseDataService(MetadataStore),
                new TargetsService(MetadataStore),
                WordCountService,
                new WordCountHistoryService(MetadataStore));

            SetWorkspaceModel(false);
        }

        public void EnableWorkspace()
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);
            SetPrivateField(Workspace, "_model", model);
            SetPrivateField(Workspace, "_hasWorkspace", true);
            SetPrivateField(Workspace, "_workspaceName", Path.GetFileName(WorkspaceRoot));
            EditorViewModel.HasWorkspace = true;
        }

        public ChapterViewModel AddChapter(string relativePath, string title, string content)
        {
            var absolutePath = Path.Combine(ManuscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, content);

            var node = new ChapterNode(relativePath, relativePath, title, NodeKind.Chapter, IsMissing: false, Index: 0);
            return new ChapterViewModel(
                node,
                uuid: Guid.NewGuid().ToString("N"),
                phaseData: null,
                new PhaseDataService(MetadataStore),
                new TargetsService(MetadataStore),
                SettingsStore,
                NotificationService,
                WorkspaceRoot);
        }

        public void SeedWorkspace(params ChapterViewModel[] nodes)
        {
            var collection = GetWorkspaceNodeCollection(Workspace);
            collection.Clear();
            foreach (var node in nodes)
                collection.Add(node);

            SetWorkspaceModel(nodes.Length > 0);
            EditorViewModel.HasWorkspace = nodes.Length > 0;
        }

        public string CreateDoc(string relativePath, string content)
        {
            var path = Path.Combine(DocsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
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
                new NotesViewModel(EditorViewModel, Workspace, new NotesService(MetadataStore), NotificationService, SettingsStore),
                new ChapterInfoViewModel(EditorViewModel, Workspace, new PhaseDataService(MetadataStore), new TargetsService(MetadataStore), SettingsStore, NotificationService, new WordCountHistoryService(MetadataStore)),
                new BookStatsViewModel(Workspace, new WordCountHistoryService(MetadataStore), new TargetsService(MetadataStore)),
                new GanttViewModel(Workspace, new PhaseDataService(MetadataStore), NotificationService),
                new CorkboardViewModel(Workspace, new FakeBookTxtStructureService(), new OrphanFileDiscoveryService(), SettingsStore, NotificationService, _owned.Track(new ManuscriptService(NotificationService))),
                new ResearchViewModel(Workspace, docs, EditorViewModel, AiTestDoubles.CreateStubAiChatViewModel(EditorViewModel, Workspace, NotificationService)),
                docs,
                _owned.Track(new GitPanelViewModel(Workspace, EditorViewModel, new FakeGitService(), NotificationService)),
                NotificationService,
                SettingsStore,
                AiTestDoubles.CreateStubAiChatViewModel(EditorViewModel, Workspace, NotificationService),
                AiTestDoubles.CreateStubSettingsFactory(NotificationService));
        }

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
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;
using ReactiveUI.Builder;
using Xunit;
using CoreUnit = Hymnal.Core.Common.Unit;

namespace Hymnal.ViewModels;

public sealed class MainWindowSupplementalDocsTests : IDisposable
{
    private readonly TestContext _context = new();

    static MainWindowSupplementalDocsTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task OpenSupplementalDoc_FromPlanMode_ActivatesWriteModeAndUsesArbitraryEditorState()
    {
        _context.EnableWorkspace();
        var docPath = _context.CreateDoc("outline.md", "doc body");
        var window = _context.CreateMainWindow();
        await ExecuteCommandAsync(window.SelectPlanCommand.Execute());
        Assert.Equal(ShellMode.Plan, window.ActiveMode);
        await window.SupplementalDocsViewModel.RefreshAsync();
        var doc = Assert.Single(window.SupplementalDocsViewModel.Nodes);

        await ExecuteCommandAsync(window.SupplementalDocsViewModel.OpenDocCommand.Execute(doc));

        Assert.Equal(ShellMode.Write, window.ActiveMode);
        Assert.True(window.IsEditorVisible);
        Assert.Null(window.EditorViewModel.ActiveNode);
        Assert.Equal(docPath, window.EditorViewModel.ActiveFilePath);
        Assert.Null(window.WorkspaceViewModel.SelectedNode);
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

    public void Dispose() => _context.Dispose();

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public NotificationService NotificationService { get; } = new();
        public RecordingMetadataStore MetadataStore { get; } = new();
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public FakeFolderPickerService FolderPicker { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; }
        public string ManuscriptRoot { get; }
        public string DocsRoot { get; }

        public TestContext()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-main-docs-tests", Guid.NewGuid().ToString("N"));
            ManuscriptRoot = Path.Combine(WorkspaceRoot, "manuscript");
            DocsRoot = Path.Combine(WorkspaceRoot, ".hymnal-data", "docs");
            Directory.CreateDirectory(ManuscriptRoot);
            Directory.CreateDirectory(DocsRoot);

            Editor = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService));
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
            var docs = new SupplementalDocsViewModel(Workspace, new SupplementalDocsService(MetadataStore), Editor, NotificationService, SettingsStore, new FakeFilePickerService());
            var gitPanel = _owned.Track(new GitPanelViewModel(Workspace, Editor, new FakeGitService(), NotificationService));
            return new MainWindowViewModel(
                Workspace,
                Editor,
                new NotesViewModel(Editor, Workspace, new NotesService(MetadataStore), NotificationService, SettingsStore),
                new ChapterInfoViewModel(Editor, Workspace, new PhaseDataService(MetadataStore), new TargetsService(MetadataStore), SettingsStore, NotificationService, new WordCountHistoryService(MetadataStore)),
                new BookStatsViewModel(Workspace, new WordCountHistoryService(MetadataStore), new TargetsService(MetadataStore)),
                new GanttViewModel(Workspace, new PhaseDataService(MetadataStore), NotificationService),
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
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), Path.GetFileName(WorkspaceRoot));
            Editor.HasWorkspace = true;
        }

        public string CreateDoc(string relativePath, string content)
        {
            var path = Path.Combine(DocsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
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

    private sealed class RecordingMetadataStore : IMetadataStore
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
            => Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);

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

    private sealed class FakeGitService : NoOpGitService;

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
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

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found.");
        field.SetValue(target, value);
    }

    private static void SetPrivateProperty<T>(object target, string propertyName, T value)
    {
        var property = typeof(WorkspaceViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found.");
        property.SetValue(target, value);
    }
}

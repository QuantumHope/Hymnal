using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Infrastructure;
using ReactiveUI;
using ReactiveUI.Builder;
using Xunit;
using CoreUnit = Hymnal.Core.Common.Unit;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

public sealed class SupplementalDocsViewModelTests : IDisposable
{
    private readonly TestContext _context = new();

    static SupplementalDocsViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task WorkspaceChanged_LoadsDocsTreeWithoutAddingChapterNodes()
    {
        _context.CreateDoc("research/notes.md", "notes");
        var docs = _context.CreateDocsViewModel();

        _context.EnableWorkspaceViaBindModel();

        Assert.True(SpinWait.SpinUntil(() => docs.Nodes.Count == 1, TimeSpan.FromSeconds(2)));
        var folder = Assert.Single(docs.Nodes);
        Assert.Equal(SupplementalDocNodeKind.Folder, folder.Kind);
        Assert.Equal("research", folder.RelativePath);
        Assert.Equal("research/notes.md", Assert.Single(folder.Children).RelativePath);
        Assert.Empty(_context.Workspace.Nodes);
    }

    [Fact]
    public async Task CreateFolderAndFileCommands_CreateEntriesUnderSupplementalDocsRootAndOpenFile()
    {
        _context.EnableWorkspace();
        var docs = _context.CreateDocsViewModel();
        await docs.RefreshAsync();

        await ExecuteCommandAsync(docs.CreateFolderCommand.Execute("research"));
        docs.SelectedNode = Assert.Single(docs.Nodes);

        await ExecuteCommandAsync(docs.CreateFileCommand.Execute("outline.md"));

        var folder = Assert.Single(docs.Nodes);
        var file = Assert.Single(folder.Children);
        Assert.Equal("research/outline.md", file.RelativePath);
        Assert.True(File.Exists(Path.Combine(_context.DocsRoot, "research", "outline.md")));
        Assert.Null(_context.Editor.ActiveNode);
        Assert.Equal(file.AbsolutePath, _context.Editor.ActiveFilePath);
        Assert.False(_context.Editor.IsBookSelected);
    }

    [Fact]
    public async Task CreateFileCommand_WithInvalidName_ShowsNotificationAndDoesNotCreateFile()
    {
        _context.EnableWorkspace();
        var docs = _context.CreateDocsViewModel();
        await docs.RefreshAsync();

        await ExecuteCommandAsync(docs.CreateFileCommand.Execute("../escape.md"));

        Assert.Contains(_context.Notifications.Errors, message => message.Contains("must be a name, not a path"));
        Assert.Empty(docs.Nodes);
        Assert.False(File.Exists(Path.Combine(_context.DocsRoot, "escape.md")));
    }

    [Fact]
    public async Task OpenDocCommand_SavesDirtyChapterClearsChapterSelectionAndEmitsOpenRequest()
    {
        _context.EnableWorkspace();
        var chapter = _context.AddChapter("chapter-one.md", "Chapter One", "# Chapter One\n\nBefore.");
        _context.AddWorkspaceNode(chapter);
        await _context.Editor.OpenChapterAsync(chapter.Node, Path.Combine(_context.ManuscriptRoot, "chapter-one.md"));
        _context.SetWorkspaceSelectedNodeWithoutSwitch(chapter);
        _context.Editor.Text = "# Chapter One\n\nAfter.";
        var docPath = _context.CreateDoc("outline.md", "doc body");
        var docs = _context.CreateDocsViewModel();
        await docs.RefreshAsync();
        var doc = Assert.Single(docs.Nodes);
        SupplementalDocNode? opened = null;
        using var sub = docs.DocumentOpened.Subscribe(node => opened = node);

        await ExecuteCommandAsync(docs.OpenDocCommand.Execute(doc));

        var write = Assert.Single(_context.MetadataStore.Writes, write => write.Path == Path.Combine(_context.ManuscriptRoot, "chapter-one.md"));
        Assert.Equal(Path.Combine(_context.ManuscriptRoot, "chapter-one.md"), write.Path);
        Assert.Equal("# Chapter One\n\nAfter.", write.Content);
        Assert.Null(_context.Editor.ActiveNode);
        Assert.Equal(docPath, _context.Editor.ActiveFilePath);
        Assert.Null(_context.Workspace.SelectedNode);
        Assert.Same(doc, opened);
    }

    [Fact]
    public async Task OpenDocCommand_WhenDirtySaveFails_AbortsSwitchAndKeepsPreviousEditorState()
    {
        _context.EnableWorkspace();
        var chapter = _context.AddChapter("chapter-two.md", "Chapter Two", "# Chapter Two\n\nBefore.");
        _context.AddWorkspaceNode(chapter);
        var chapterPath = Path.Combine(_context.ManuscriptRoot, "chapter-two.md");
        await _context.Editor.OpenChapterAsync(chapter.Node, chapterPath);
        _context.SetWorkspaceSelectedNodeWithoutSwitch(chapter);
        _context.Editor.Text = "# Chapter Two\n\nUnsaved.";
        _context.CreateDoc("outline.md", "doc body");
        var docs = _context.CreateDocsViewModel();
        await docs.RefreshAsync();
        var doc = Assert.Single(docs.Nodes);
        _context.MetadataStore.FailWrites = true;
        var emitted = 0;
        using var sub = docs.DocumentOpened.Subscribe(_ => emitted++);

        await ExecuteCommandAsync(docs.OpenDocCommand.Execute(doc));

        Assert.Same(chapter.Node, _context.Editor.ActiveNode);
        Assert.Equal(chapterPath, _context.Editor.ActiveFilePath);
        Assert.Equal("# Chapter Two\n\nUnsaved.", _context.Editor.Text);
        Assert.Same(chapter, _context.Workspace.SelectedNode);
        Assert.Equal(0, emitted);
        Assert.Contains(_context.Notifications.Errors, message => message.Contains("Save failed"));
    }

    [Fact]
    public async Task ImportExistingFileCommand_CopiesSelectedFileAndOpensIt()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "hymnal-docs-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        var sourcePath = Path.Combine(sourceDir, "imported.md");
        await File.WriteAllTextAsync(sourcePath, "imported body");

        try
        {
            _context.EnableWorkspace();
            _context.FilePicker.NextFile = sourcePath;
            var docs = _context.CreateDocsViewModel();
            await docs.RefreshAsync();

            await ExecuteCommandAsync(docs.ImportExistingFileCommand.Execute());

            var file = Assert.Single(docs.Nodes);
            Assert.Equal("imported.md", file.RelativePath);
            Assert.True(File.Exists(Path.Combine(_context.DocsRoot, "imported.md")));
            Assert.Equal("imported body", await File.ReadAllTextAsync(Path.Combine(_context.DocsRoot, "imported.md")));
            Assert.Equal(Path.Combine(_context.DocsRoot, "imported.md"), file.AbsolutePath);
            Assert.Null(_context.Editor.ActiveNode);
            Assert.Equal(file.AbsolutePath, _context.Editor.ActiveFilePath);
        }
        finally
        {
            if (Directory.Exists(sourceDir))
                Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ImportExistingFileCommand_WhenPickerCancelled_DoesNothing()
    {
        _context.EnableWorkspace();
        _context.FilePicker.NextFile = null;
        var docs = _context.CreateDocsViewModel();
        await docs.RefreshAsync();

        await ExecuteCommandAsync(docs.ImportExistingFileCommand.Execute());

        Assert.Empty(docs.Nodes);
        Assert.Empty(_context.Notifications.Errors);
    }

    [Fact]
    public async Task DirtySupplementalDocsFile_IsSavedBeforeChapterSwitch()
    {
        _context.EnableWorkspace();
        var docPath = _context.CreateDoc("scratch.md", "before");
        await _context.Editor.OpenArbitraryFileAsync(docPath);
        _context.Editor.Text = "after";
        var chapter = _context.AddChapter("chapter-three.md", "Chapter Three", "# Chapter Three\n\nBody.");
        _context.AddWorkspaceNode(chapter);

        _context.Workspace.SelectedNode = chapter;

        Assert.True(SpinWait.SpinUntil(() => _context.Editor.ActiveNode == chapter.Node, TimeSpan.FromSeconds(2)));
        var write = Assert.Single(_context.MetadataStore.Writes);
        Assert.Equal(docPath, write.Path);
        Assert.Equal("after", write.Content);
        Assert.Equal(Path.Combine(_context.ManuscriptRoot, "chapter-three.md"), _context.Editor.ActiveFilePath);
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
        public FakeNotificationService Notifications { get; } = new();
        public RecordingMetadataStore MetadataStore { get; } = new();
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public FakeFolderPickerService FolderPicker { get; } = new();
        public FakeFilePickerService FilePicker { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; }
        public string ManuscriptRoot { get; }
        public string DocsRoot { get; }

        public TestContext()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-docs-vm-tests", Guid.NewGuid().ToString("N"));
            ManuscriptRoot = Path.Combine(WorkspaceRoot, "manuscript");
            DocsRoot = Path.Combine(WorkspaceRoot, ".hymnal-data", "docs");
            Directory.CreateDirectory(ManuscriptRoot);
            Directory.CreateDirectory(DocsRoot);

            Editor = _owned.Track(new EditorViewModel(MetadataStore, Notifications, WordCountService, new StoryFileWriter(MetadataStore)));
            Workspace = new WorkspaceViewModel(
                _owned.Track(new ManuscriptService(Notifications)),
                new FakeBookTxtStructureService(),
                FilePicker,
                SettingsStore,
                FolderPicker,
                Notifications,
                Editor,
                new ChapterRegistryService(MetadataStore),
                new PhaseDataService(MetadataStore),
                new TargetsService(MetadataStore),
                WordCountService,
                new WordCountHistoryService(MetadataStore));
        }

        public SupplementalDocsViewModel CreateDocsViewModel()
            => new(Workspace, new SupplementalDocsService(MetadataStore), Editor, Notifications, SettingsStore, FilePicker);

        public void EnableWorkspace()
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), Path.GetFileName(WorkspaceRoot));
            Editor.HasWorkspace = true;
        }

        public void EnableWorkspaceViaBindModel()
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);
            var method = typeof(WorkspaceViewModel).GetMethod("BindModel", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("BindModel was not found.");
            method.Invoke(Workspace, new object[] { model });
            Editor.HasWorkspace = true;
        }

        public string CreateDoc(string relativePath, string content)
        {
            var path = Path.Combine(DocsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public ChapterViewModel AddChapter(string relativePath, string title, string content)
        {
            var path = Path.Combine(ManuscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);

            var node = new ChapterNode(relativePath, relativePath, title, NodeKind.Chapter, IsMissing: false, Index: 0);
            return new ChapterViewModel(
                node,
                Guid.NewGuid().ToString("N"),
                phaseData: null,
                new PhaseDataService(MetadataStore),
                new TargetsService(MetadataStore),
                SettingsStore,
                Notifications,
                WorkspaceRoot);
        }

        public void AddWorkspaceNode(ChapterViewModel chapter)
        {
            var field = typeof(WorkspaceViewModel).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Workspace _nodes field not found.");
            var collection = (System.Collections.IList)field.GetValue(Workspace)!;
            collection.Add(chapter);
        }

        public void SetWorkspaceSelectedNodeWithoutSwitch(ChapterViewModel chapter)
            => SetPrivateField(Workspace, "_selectedNode", chapter);

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
        public List<(string Path, string Content)> Writes { get; } = new();
        public bool FailWrites { get; set; }

        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            if (FailWrites)
                throw new IOException("forced write failure");

            Writes.Add((absolutePath, content));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            return File.WriteAllTextAsync(absolutePath, content);
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message) { }
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

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public string? NextFile { get; set; }

        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult(NextFile);
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

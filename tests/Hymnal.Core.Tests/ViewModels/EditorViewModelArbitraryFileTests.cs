using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using DynamicData.Binding;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using ReactiveUI.Builder;
using Xunit;
using CoreUnit = Hymnal.Core.Common.Unit;

namespace Hymnal.ViewModels;

public sealed class EditorViewModelArbitraryFileTests : IDisposable
{
    private readonly string _root;
    private readonly string _manuscriptRoot;
    private readonly string _docsRoot;
    private readonly FakeNotificationService _notifications = new();
    private readonly FakeMetadataStore _metadataStore = new();
    private readonly FakeAppSettingsStore _settingsStore = new();
    private readonly FakeFolderPickerService _folderPicker = new();
    private readonly WordCountService _wordCountService = new();
    private readonly EditorViewModel _editor;
    private readonly WorkspaceViewModel _workspace;
    private readonly OwnedDisposables _owned = new();

    static EditorViewModelArbitraryFileTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    public EditorViewModelArbitraryFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hymnal-editor-doc-tests", Guid.NewGuid().ToString("N"));
        _manuscriptRoot = Path.Combine(_root, "manuscript");
        _docsRoot = Path.Combine(_root, ".hymnal-data", "docs");
        Directory.CreateDirectory(_manuscriptRoot);
        Directory.CreateDirectory(_docsRoot);

        _editor = _owned.Track(new EditorViewModel(_metadataStore, _notifications, _wordCountService, new StoryFileWriter(_metadataStore))
        {
            HasWorkspace = true
        });

        _workspace = new WorkspaceViewModel(
            _owned.Track(new ManuscriptService(_notifications)),
            new FakeBookTxtStructureService(),
            new FakeFilePickerService(),
            _settingsStore,
            _folderPicker,
            _notifications,
            _editor,
            new ChapterRegistryService(_metadataStore),
            new PhaseDataService(_metadataStore),
            new TargetsService(_metadataStore),
            _wordCountService,
            new WordCountHistoryService(_metadataStore));

        var model = new ManuscriptModel();
        model.SetRoots(_root, _manuscriptRoot);
        SetPrivateField(_workspace, "_model", model);
        SetPrivateField(_workspace, "_hasWorkspace", true);
        SetPrivateField(_workspace, "_workspaceName", Path.GetFileName(_root));
    }

    [Fact]
    public async Task OpenArbitraryFileAsync_LoadsPlainFileWithNoActiveChapter()
    {
        var docPath = CreateDoc("research.md", "# Research\n\nNotes.");

        await _editor.OpenArbitraryFileAsync(docPath);

        Assert.Null(_editor.ActiveNode);
        Assert.Equal(docPath, _editor.ActiveFilePath);
        Assert.Equal("# Research\n\nNotes.", _editor.Text);
        Assert.Equal(_editor.Text, _editor.OriginalText);
        Assert.False(_editor.IsBookSelected);
        Assert.False(_editor.ShowMissingChapterPrompt);
        Assert.False(_editor.HasConflict);
        Assert.True(_editor.ShowEditor);
        Assert.False(_editor.ShowNoChapterPrompt);
    }

    [Fact]
    public async Task SaveAsync_ForDirtyArbitraryFile_UsesMetadataStoreAndClearsConflict()
    {
        var docPath = CreateDoc("draft.md", "old");
        await _editor.OpenArbitraryFileAsync(docPath);
        _editor.Text = "new body";
        SetPrivateProperty(_editor, "HasConflict", true);
        SetPrivateProperty(_editor, "ConflictMessage", "external conflict");

        await _editor.SaveAsync();

        var write = Assert.Single(_metadataStore.Writes);
        Assert.Equal(docPath, write.Path);
        Assert.Equal("new body", write.Content);
        Assert.Equal("new body", _editor.OriginalText);
        Assert.False(_editor.IsDirty);
        Assert.False(_editor.HasConflict);
        Assert.Null(_editor.ConflictMessage);
    }

    [Fact]
    public async Task WorkspaceSwitchFromDirtyDocToChapter_SavesDocBeforeOpeningChapter()
    {
        var docPath = CreateDoc("scratch.md", "doc before");
        await _editor.OpenArbitraryFileAsync(docPath);
        _editor.Text = "doc after";

        var chapter = CreateChapter("chapter-one.md", "Chapter One", "# Chapter One\n\nChapter body.");
        AddWorkspaceNode(chapter);

        await InvokeTrySwitchChapterAsync(chapter);

        Assert.Equal(docPath, Assert.Single(_metadataStore.Writes).Path);
        Assert.Equal("doc after", _metadataStore.Writes[0].Content);
        Assert.Same(chapter.Node, _editor.ActiveNode);
        Assert.Equal(Path.Combine(_manuscriptRoot, "chapter-one.md"), _editor.ActiveFilePath);
        Assert.Equal("# Chapter One\n\nChapter body.", _editor.Text);
    }

    [Fact]
    public async Task TestSeamSwitchFromDirtyChapterToDoc_SavesChapterBeforeOpeningDoc()
    {
        var chapter = CreateChapter("chapter-two.md", "Chapter Two", "# Chapter Two\n\nBefore.");
        var chapterPath = Path.Combine(_manuscriptRoot, "chapter-two.md");
        await _editor.OpenChapterAsync(chapter.Node, chapterPath);
        _editor.Text = "# Chapter Two\n\nAfter.";

        var docPath = CreateDoc("outline.md", "doc body");
        await SaveBeforeOpenArbitraryFileAsync(_editor, docPath);

        var write = Assert.Single(_metadataStore.Writes);
        Assert.Equal(chapterPath, write.Path);
        Assert.Equal("# Chapter Two\n\nAfter.", write.Content);
        Assert.Null(_editor.ActiveNode);
        Assert.Equal(docPath, _editor.ActiveFilePath);
        Assert.Equal("doc body", _editor.Text);
    }

    [Fact]
    public async Task ExternalChangeOnCleanArbitraryFile_ReloadsBufferAndShowsInfo()
    {
        var docPath = CreateDoc("clean.md", "before");
        await _editor.OpenArbitraryFileAsync(docPath);
        await File.WriteAllTextAsync(docPath, "after external");

        await TriggerWatcherChangedAsync();

        Assert.True(SpinWait.SpinUntil(() => _editor.Text == "after external", TimeSpan.FromSeconds(2)));
        Assert.Equal("after external", _editor.OriginalText);
        Assert.False(_editor.HasConflict);
        Assert.Contains(_notifications.Infos, message => message.Contains("clean.md") && message.Contains("reloaded"));
    }

    [Fact]
    public async Task ExternalChangeOnDirtyArbitraryFile_SetsConflictMessage()
    {
        var docPath = CreateDoc("dirty.md", "before");
        await _editor.OpenArbitraryFileAsync(docPath);
        _editor.Text = "local edit";
        await File.WriteAllTextAsync(docPath, "external edit");

        await TriggerWatcherChangedAsync();

        Assert.True(SpinWait.SpinUntil(() => _editor.HasConflict, TimeSpan.FromSeconds(2)));
        Assert.Equal("local edit", _editor.Text);
        Assert.Contains("dirty.md", _editor.ConflictMessage);
        Assert.Contains("Reload from disk", _editor.ConflictMessage);
    }

    [Fact]
    public async Task AcceptExternalCommand_ForArbitraryFile_ReloadsFromDisk()
    {
        var docPath = CreateDoc("accept.md", "before");
        await _editor.OpenArbitraryFileAsync(docPath);
        _editor.Text = "local edit";
        SetPrivateProperty(_editor, "HasConflict", true);
        SetPrivateProperty(_editor, "ConflictMessage", "conflict");
        await File.WriteAllTextAsync(docPath, "external edit");

        await ExecuteCommandAsync(_editor.AcceptExternalCommand.Execute());

        Assert.Equal("external edit", _editor.Text);
        Assert.Equal("external edit", _editor.OriginalText);
        Assert.False(_editor.HasConflict);
        Assert.Null(_editor.ConflictMessage);
    }

    [UnixPermissionFact]
    public async Task SwitchToUnreadableChapter_ShowsErrorAndRestoresPreviousChapter()
    {
        var one = CreateChapter("one.md", "One", "# One\n\nFirst.");
        var two = CreateChapter("two.md", "Two", "# Two\n\nSecond.");
        AddWorkspaceNode(one);
        AddWorkspaceNode(two);
        await InvokeTrySwitchChapterAsync(one);
        Assert.Same(one.Node, _editor.ActiveNode);

        var twoPath = Path.Combine(_manuscriptRoot, "two.md");
        File.SetUnixFileMode(twoPath, UnixFileMode.None);
        try
        {
            await InvokeTrySwitchChapterAsync(two);
        }
        finally
        {
            File.SetUnixFileMode(twoPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Contains(_notifications.Errors, e => e.Contains("Two"));
        Assert.Same(one, _workspace.SelectedNode);
        Assert.Same(one.Node, _editor.ActiveNode);
        Assert.Equal("# One\n\nFirst.", _editor.Text);
        Assert.NotNull(typeof(EditorViewModel).GetField("_watcher", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_editor));
    }

    private string CreateDoc(string relativePath, string content)
    {
        var path = Path.Combine(_docsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ChapterViewModel CreateChapter(string relativePath, string title, string content)
    {
        var path = Path.Combine(_manuscriptRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var node = new ChapterNode(relativePath, relativePath, title, NodeKind.Chapter, IsMissing: false, Index: 0);
        return new ChapterViewModel(
            node,
            Guid.NewGuid().ToString("N"),
            phaseData: null,
            new PhaseDataService(_metadataStore),
            new TargetsService(_metadataStore),
            _settingsStore,
            _notifications,
            _root);
    }

    private void AddWorkspaceNode(ChapterViewModel chapter)
    {
        var field = typeof(WorkspaceViewModel).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workspace node collection field was not found.");

        var nodes = (IList)(field.GetValue(_workspace)
            ?? throw new InvalidOperationException("Workspace node collection was null."));
        nodes.Add(chapter);

        var byPath = typeof(WorkspaceViewModel).GetField("_nodesByPath", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workspace node lookup field was not found.");
        ((IDictionary<string, ChapterViewModel>)byPath.GetValue(_workspace)!)[chapter.Node.RelativePath] = chapter;
    }

    private async Task InvokeTrySwitchChapterAsync(ChapterViewModel chapter)
    {
        var method = typeof(WorkspaceViewModel).GetMethod("TrySwitchChapterAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TrySwitchChapterAsync was not found.");

        var task = (Task)(method.Invoke(_workspace, new object[] { chapter })
            ?? throw new InvalidOperationException("TrySwitchChapterAsync returned null."));
        await task;
    }

    private static async Task SaveBeforeOpenArbitraryFileAsync(EditorViewModel editor, string docPath)
    {
        if (editor.IsDirty)
            await editor.SaveAsync();

        await editor.OpenArbitraryFileAsync(docPath);
    }

    private Task TriggerWatcherChangedAsync() => _editor.ProcessExternalFileChangeNowAsync();

    private static async Task ExecuteCommandAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var type = target.GetType();
        FieldInfo? field = null;
        while (type != null && field == null)
        {
            field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            type = type.BaseType;
        }

        if (field == null)
            throw new InvalidOperationException($"Field '{fieldName}' was not found on {target.GetType().Name}.");

        field.SetValue(target, value);
    }

    private static void SetPrivateProperty<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().Name}.");

        property.SetValue(target, value);
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
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public List<string> Successes { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) => Successes.Add(message);
    }

    private sealed class FakeMetadataStore : IMetadataStore
    {
        public List<(string Path, string Content)> Writes { get; } = new();

        public async Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Writes.Add((absolutePath, content));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllTextAsync(absolutePath, content);
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
}

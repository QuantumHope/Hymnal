using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using ReactiveUI;
using ReactiveUI.Builder;
using Xunit;
using CoreUnit = Hymnal.Core.Common.Unit;

namespace Hymnal.Core.Tests.Views;

public sealed class SupplementalDocsViewSmokeTests
{
    static SupplementalDocsViewSmokeTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public void SupplementalDocsView_AndMainWindowWireDocsSection()
    {
        var docsViewAxaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/SupplementalDocsView.axaml"));
        Assert.Contains("x:Class=\"Hymnal.Views.SupplementalDocsView\"", docsViewAxaml);
        Assert.Contains("x:Name=\"DocsTree\"", docsViewAxaml);
        Assert.Contains("ItemsSource=\"{Binding Nodes}\"", docsViewAxaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedNode, Mode=TwoWay}\"", docsViewAxaml);
        Assert.Contains("x:Name=\"CreateDocsFolderButton\"", docsViewAxaml);
        Assert.Contains("x:Name=\"CreateDocsFileButton\"", docsViewAxaml);
        Assert.Contains("x:Name=\"ImportExistingFileButton\"", docsViewAxaml);

        var codeBehind = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/SupplementalDocsView.axaml.cs"));
        Assert.Contains("CreateFolderCommand.Execute", codeBehind);
        Assert.Contains("CreateFileCommand.Execute", codeBehind);
        Assert.Contains("ImportExistingFileCommand.Execute", codeBehind);
        Assert.Contains("OpenDocCommand.Execute", codeBehind);

        var mainWindowAxaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("Text=\"DOCS\"", mainWindowAxaml);
        Assert.Contains("x:Name=\"SupplementalDocsViewContent\"", mainWindowAxaml);
        Assert.Contains("DataContext=\"{Binding SupplementalDocsViewModel}\"", mainWindowAxaml);
        Assert.Contains("DataContext=\"{Binding WorkspaceViewModel}\"", mainWindowAxaml);
    }

    [Fact]
    public async Task SupplementalDocsView_CreateOpenSaveReloadPath_PreservesContentAndEditorIdentity()
    {
        using var context = new TestContext();
        var docsVm = context.CreateDocsViewModel(context.EditorViewModel);

        await docsVm.RefreshAsync();
        await docsVm.CreateFolderAsync("research");
        await docsVm.CreateFileAsync("notes.md");

        Assert.Single(docsVm.Nodes);
        Assert.Equal(SupplementalDocNodeKind.Folder, docsVm.Nodes[0].Kind);
        Assert.Equal("research", docsVm.Nodes[0].DisplayName);

        var created = FindNode(docsVm.Nodes, "research/notes.md");
        Assert.NotNull(created);
        Assert.True(File.Exists(created!.AbsolutePath));
        Assert.Equal(created.AbsolutePath, context.EditorViewModel.ActiveFilePath);
        Assert.Null(context.EditorViewModel.ActiveNode);
        Assert.False(context.EditorViewModel.IsBookSelected);

        const string expected = "# Research Notes\n\nRemember the DOCS sidebar smoke path.";
        context.EditorViewModel.Text = expected;
        await context.EditorViewModel.SaveAsync();

        using var reopenedEditor = new EditorViewModel(context.MetadataStore, context.NotificationService, context.WordCountService, new StoryFileWriter(context.MetadataStore));
        var reopenedDocsVm = context.CreateDocsViewModel(reopenedEditor);
        await reopenedDocsVm.RefreshAsync();

        var reopened = FindNode(reopenedDocsVm.Nodes, "research/notes.md");
        Assert.NotNull(reopened);
        await reopenedDocsVm.OpenDocAsync(reopened!);

        Assert.Equal(reopened!.AbsolutePath, reopenedEditor.ActiveFilePath);
        Assert.Null(reopenedEditor.ActiveNode);
        Assert.False(reopenedEditor.IsBookSelected);
        Assert.Equal(expected, reopenedEditor.Text);
        Assert.False(reopenedEditor.IsDirty);
        Assert.False(reopenedEditor.HasConflict);
    }

    // Manual desktop smoke checklist for interaction coverage that is brittle in headless tests:
    // 1. Open a workspace, expand the left sidebar, and verify CHAPTERS and DOCS are visually distinct.
    // 2. Use DOCS "+ Folder" to create "research".
    // 3. Select "research", use DOCS "+ File" to create "notes.md", and verify Write mode opens the existing editor.
    // 4. Type content, save, close/reopen the workspace, and verify research/notes.md remains visible with content intact.

    private static SupplementalDocNode? FindNode(IEnumerable<SupplementalDocNode> nodes, string relativePath)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return node;

            var child = FindNode(node.Children, relativePath);
            if (child != null)
                return child;
        }

        return null;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public NotificationService NotificationService { get; } = new();
        public FakeMetadataStore MetadataStore { get; } = new();
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public FakeFolderPickerService FolderPickerService { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel EditorViewModel { get; }
        public ChapterRegistryService RegistryService { get; }
        public PhaseDataService PhaseDataService { get; }
        public TargetsService TargetsService { get; }
        public WordCountHistoryService HistoryService { get; }
        public ManuscriptService ManuscriptService { get; }
        public SupplementalDocsService DocsService { get; }
        public WorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; }
        public string ManuscriptRoot { get; }

        public TestContext()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-docs-view-smoke-tests", Guid.NewGuid().ToString("N"));
            ManuscriptRoot = Path.Combine(WorkspaceRoot, "manuscript");
            Directory.CreateDirectory(ManuscriptRoot);
            File.WriteAllText(Path.Combine(ManuscriptRoot, "Book.txt"), string.Empty);

            EditorViewModel = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            RegistryService = new ChapterRegistryService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            HistoryService = new WordCountHistoryService(MetadataStore);
            ManuscriptService = _owned.Track(new ManuscriptService(NotificationService));
            DocsService = new SupplementalDocsService(MetadataStore);
            Workspace = new WorkspaceViewModel(
                ManuscriptService,
                new FakeBookTxtStructureService(),
                new FakeFilePickerService(),
                SettingsStore,
                FolderPickerService,
                NotificationService,
                EditorViewModel,
                RegistryService,
                PhaseDataService,
                TargetsService,
                WordCountService,
                HistoryService);

            SeedWorkspace();
        }

        public SupplementalDocsViewModel CreateDocsViewModel(EditorViewModel editor)
            => new(Workspace, DocsService, editor, NotificationService, SettingsStore, new FakeFilePickerService());

        public MainWindowViewModel CreateMainWindowViewModel()
        {
            var docsVm = CreateDocsViewModel(EditorViewModel);
            var corkboardVm = new CorkboardViewModel(Workspace, new FakeBookTxtStructureService(), new OrphanFileDiscoveryService(), SettingsStore, NotificationService, ManuscriptService);
            var gitPanelVm = _owned.Track(new GitPanelViewModel(Workspace, EditorViewModel, new FakeGitService(), NotificationService));
            return new MainWindowViewModel(
                Workspace,
                EditorViewModel,
                new NotesViewModel(EditorViewModel, Workspace, NotificationService, SettingsStore),
                new ChapterInfoViewModel(EditorViewModel, Workspace, SettingsStore, NotificationService, new WordCountHistoryService(MetadataStore)),
                new BookStatsViewModel(Workspace, new WordCountHistoryService(MetadataStore)),
                new GanttViewModel(Workspace, NotificationService),
                corkboardVm,
                new ResearchViewModel(Workspace, docsVm, EditorViewModel, AiTestDoubles.CreateStubAiChatViewModel(EditorViewModel, Workspace, NotificationService)),
                docsVm,
                gitPanelVm,
                NotificationService,
                SettingsStore,
                AiTestDoubles.CreateStubAiChatViewModel(EditorViewModel, Workspace, NotificationService),
                AiTestDoubles.CreateStubSettingsFactory(NotificationService));
        }

        private void SeedWorkspace()
        {
            var model = new ManuscriptModel();
            model.SetRoots(WorkspaceRoot, ManuscriptRoot);

            SetPrivateField(Workspace, "_model", model);
            SetPrivateField(Workspace, "_hasWorkspace", true);
            SetPrivateField(Workspace, "_workspaceName", Path.GetFileName(WorkspaceRoot));
            Workspace.RaisePropertyChanged(nameof(Workspace.HasWorkspace));
            Workspace.RaisePropertyChanged(nameof(Workspace.WorkspaceName));
        }

        public void Dispose()
        {
            try
            {
                _owned.Dispose();
            }
            finally
            {
                NotificationService.Dispose();
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

    private sealed class FakeGitService : NoOpGitService;

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
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on WorkspaceViewModel.");

        field.SetValue(target, value);
    }
}

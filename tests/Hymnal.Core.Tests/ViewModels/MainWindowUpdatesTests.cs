using System.Reflection;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure;
using Hymnal.Infrastructure.Updates;
using MattEland.HopeLab.Updates.ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// The main window's side of an installed update: the banner's unsaved-changes warning follows the editor, and the
/// save before restarting locks editing, writes the open file, and gives editing back when the restart is called off.
/// <para>
/// These drive a real editor, whose word count and preview post to the Avalonia dispatcher. So, like the other
/// editor-driving tests, they run in the AvaloniaUi collection and do all their work, disposal included, inside one
/// UI dispatch: nothing reaches the dispatcher from a pool thread while another test's dispatch is starting.
/// </para>
/// </summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowUpdatesTests
{
    static MainWindowUpdatesTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public Task Banner_gains_and_loses_the_unsaved_changes_warning_as_the_editor_gets_dirty_and_is_saved() =>
        Ui(async context =>
        {
            var window = context.Window;
            var updates = context.Updates;
            Assert.Null(window.Updates);
            window.Updates = updates; // as the app does, once the update preferences have loaded

            await updates.CheckAsync(userAsked: false);
            var raised = 0;
            updates.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UpdatesViewModel.BannerBody)) raised++; };

            await context.Editor.OpenArbitraryFileAsync(context.Doc("draft.md", "old"));
            Assert.DoesNotContain(HymnalUpdates.UnsavedChangesWarning, updates.BannerBody);

            raised = 0;
            context.Editor.Text = "new words";
            Assert.True(context.Editor.IsDirty);
            Assert.True(raised > 0, "BannerBody should be re-read when the editor becomes dirty");
            Assert.EndsWith(HymnalUpdates.UnsavedChangesWarning, updates.BannerBody);

            raised = 0;
            await context.Editor.SaveAsync();
            Assert.False(context.Editor.IsDirty);
            Assert.True(raised > 0, "BannerBody should be re-read when the editor is saved");
            Assert.DoesNotContain(HymnalUpdates.UnsavedChangesWarning, updates.BannerBody);

            // Taken away again, the old view model no longer follows the editor.
            window.Updates = null;
            raised = 0;
            context.Editor.Text = "more words";
            Assert.Equal(0, raised);
        });

    [Fact]
    public Task Saving_before_restart_locks_editing_writes_the_open_file_and_can_give_editing_back() =>
        Ui(async context =>
        {
            var window = context.Window;
            var path = context.Doc("draft.md", "old");
            await context.Editor.OpenArbitraryFileAsync(path);
            context.Editor.Text = "new words";
            var lockedDuringSave = false;
            window.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsRestarting) && window.IsRestarting) lockedDuringSave = true;
            };

            Assert.Null(await window.SaveAllBeforeRestartAsync(TimeSpan.FromSeconds(5)));

            Assert.True(lockedDuringSave);
            Assert.True(window.IsRestarting); // stays locked: Hymnal is about to exit
            Assert.False(window.HasUnsavedWork);
            Assert.Equal("new words", await File.ReadAllTextAsync(path));

            window.CancelRestart(); // the new copy wouldn't start
            Assert.False(window.IsRestarting);
        });

    [Fact]
    public Task A_failed_save_before_restart_gives_editing_back() =>
        Ui(async context =>
        {
            var window = context.Window;
            await context.Editor.OpenArbitraryFileAsync(context.Doc("draft.md", "old"));
            context.Editor.Text = "new words";
            // The file changed on disk while it had edits: that needs the author's choice, so nothing restarts.
            typeof(EditorViewModel).GetProperty(nameof(EditorViewModel.HasConflict))!.SetValue(context.Editor, true);

            Assert.Equal(UpdateRestart.CouldNotSave, await window.SaveAllBeforeRestartAsync(TimeSpan.FromSeconds(5)));

            Assert.False(window.IsRestarting);
            Assert.True(context.Editor.IsDirty);
            Assert.True(window.HasUnsavedWork);
        });

    /// <summary>Builds the view models, runs <paramref name="body"/> and disposes them, all in one UI dispatch.</summary>
    private static async Task Ui(Func<TestContext, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "hymnal-update-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
            {
                using var context = new TestContext(root);
                await body(context);
                return 0;
            }, TimeSpan.FromSeconds(40));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        private readonly string _root;
        private readonly NotificationService _notifications = new();
        private readonly AppSettingsStore _settings;
        private readonly MetadataStore _metadata = new();

        public EditorViewModel Editor { get; }
        public UpdatesViewModel Updates { get; }
        public MainWindowViewModel Window { get; }

        public TestContext(string root)
        {
            _root = root;
            Directory.CreateDirectory(Path.Combine(root, "manuscript"));
            Directory.CreateDirectory(Path.Combine(root, ".hymnal-data", "docs"));
            _settings = new AppSettingsStore(Path.Combine(root, "settings.json"));

            var wordCounts = new WordCountService();
            Editor = _owned.Track(new EditorViewModel(_metadata, _notifications, wordCounts, new StoryFileWriter(_metadata)) { HasWorkspace = true });
            var workspace = new WorkspaceViewModel(
                _owned.Track(new ManuscriptService(_notifications)),
                new BookTxtStructureService(_metadata),
                new NoFilePicker(),
                _settings,
                new NoFolderPicker(),
                _notifications,
                Editor,
                new ChapterRegistryService(_metadata),
                new PhaseDataService(_metadata),
                new TargetsService(_metadata),
                wordCounts,
                new WordCountHistoryService(_metadata));
            var model = new ManuscriptModel();
            model.SetRoots(root, Path.Combine(root, "manuscript"));
            typeof(WorkspaceViewModel).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(workspace, model);

            Updates = _owned.Track(HymnalUpdates.CreateViewModel(
                new FakeUpdateService { Offer = FakeUpdateService.SampleOffer() }, new InMemoryUpdateHost(), () => Editor.IsDirty));

            var docs = new SupplementalDocsViewModel(workspace, new SupplementalDocsService(_metadata), Editor, _notifications, _settings, new NoFilePicker());
            Window = new MainWindowViewModel(
                workspace,
                Editor,
                new NotesViewModel(Editor, workspace, _notifications, _settings),
                new ChapterInfoViewModel(Editor, workspace, _settings, _notifications, new WordCountHistoryService(_metadata)),
                new BookStatsViewModel(workspace, new WordCountHistoryService(_metadata)),
                new GanttViewModel(workspace, _notifications),
                new CorkboardViewModel(workspace, new BookTxtStructureService(_metadata), new OrphanFileDiscoveryService(), _settings, _notifications,
                    _owned.Track(new ManuscriptService(_notifications))),
                new ResearchViewModel(workspace, docs, Editor, AiTestDoubles.CreateStubAiChatViewModel(Editor, workspace, _notifications)),
                docs,
                _owned.Track(new GitPanelViewModel(workspace, Editor, new NoOpGitService(), _notifications)),
                _notifications,
                _settings,
                AiTestDoubles.CreateStubAiChatViewModel(Editor, workspace, _notifications),
                AiTestDoubles.CreateStubSettingsFactory(_notifications));
        }

        public string Doc(string name, string content)
        {
            var path = Path.Combine(_root, ".hymnal-data", "docs", name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            _owned.Dispose();
            _settings.Dispose();
        }
    }

    private sealed class NoFilePicker : Hymnal.Core.Interfaces.IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class NoFolderPicker : Hymnal.Core.Interfaces.IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }
}

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
/// </summary>
public sealed class MainWindowUpdatesTests : IDisposable
{
    private readonly OwnedDisposables _owned = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hymnal-update-tests", Guid.NewGuid().ToString("N"));
    private readonly NotificationService _notifications = new();
    private readonly AppSettingsStore _settings;
    private readonly MetadataStore _metadata = new();
    private readonly EditorViewModel _editor;
    private readonly WorkspaceViewModel _workspace;
    private readonly UpdatesViewModel _updates;
    private readonly MainWindowViewModel _window;

    static MainWindowUpdatesTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    public MainWindowUpdatesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "manuscript"));
        Directory.CreateDirectory(Path.Combine(_root, ".hymnal-data", "docs"));
        _settings = new AppSettingsStore(Path.Combine(_root, "settings.json"));

        var wordCounts = new WordCountService();
        _editor = _owned.Track(new EditorViewModel(_metadata, _notifications, wordCounts, new StoryFileWriter(_metadata)) { HasWorkspace = true });
        _workspace = new WorkspaceViewModel(
            _owned.Track(new ManuscriptService(_notifications)),
            new BookTxtStructureService(_metadata),
            new NoFilePicker(),
            _settings,
            new NoFolderPicker(),
            _notifications,
            _editor,
            new ChapterRegistryService(_metadata),
            new PhaseDataService(_metadata),
            new TargetsService(_metadata),
            wordCounts,
            new WordCountHistoryService(_metadata));
        var model = new ManuscriptModel();
        model.SetRoots(_root, Path.Combine(_root, "manuscript"));
        typeof(WorkspaceViewModel).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_workspace, model);

        _updates = HymnalUpdates.CreateViewModel(
            new FakeUpdateService { Offer = FakeUpdateService.SampleOffer() }, new InMemoryUpdateHost(), () => _editor.IsDirty);

        var docs = new SupplementalDocsViewModel(_workspace, new SupplementalDocsService(_metadata), _editor, _notifications, _settings, new NoFilePicker());
        _window = new MainWindowViewModel(
            _workspace,
            _editor,
            new NotesViewModel(_editor, _workspace, _notifications, _settings),
            new ChapterInfoViewModel(_editor, _workspace, _settings, _notifications, new WordCountHistoryService(_metadata)),
            new BookStatsViewModel(_workspace, new WordCountHistoryService(_metadata)),
            new GanttViewModel(_workspace, _notifications),
            new CorkboardViewModel(_workspace, new BookTxtStructureService(_metadata), new OrphanFileDiscoveryService(), _settings, _notifications,
                _owned.Track(new ManuscriptService(_notifications))),
            new ResearchViewModel(_workspace, docs, _editor, AiTestDoubles.CreateStubAiChatViewModel(_editor, _workspace, _notifications)),
            docs,
            _owned.Track(new GitPanelViewModel(_workspace, _editor, new NoOpGitService(), _notifications)),
            _notifications,
            _settings,
            AiTestDoubles.CreateStubAiChatViewModel(_editor, _workspace, _notifications),
            AiTestDoubles.CreateStubSettingsFactory(_notifications),
            updates: _updates);
    }

    [Fact]
    public async Task Banner_gains_and_loses_the_unsaved_changes_warning_as_the_editor_gets_dirty_and_is_saved()
    {
        await _updates.CheckAsync(userAsked: false);
        var raised = 0;
        _updates.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UpdatesViewModel.BannerBody)) raised++; };

        await _editor.OpenArbitraryFileAsync(Doc("draft.md", "old"));
        Assert.DoesNotContain(HymnalUpdates.UnsavedChangesWarning, _updates.BannerBody);

        raised = 0;
        _editor.Text = "new words";
        Assert.True(_editor.IsDirty);
        Assert.True(raised > 0, "BannerBody should be re-read when the editor becomes dirty");
        Assert.EndsWith(HymnalUpdates.UnsavedChangesWarning, _updates.BannerBody);

        raised = 0;
        await _editor.SaveAsync();
        Assert.False(_editor.IsDirty);
        Assert.True(raised > 0, "BannerBody should be re-read when the editor is saved");
        Assert.DoesNotContain(HymnalUpdates.UnsavedChangesWarning, _updates.BannerBody);
    }

    [Fact]
    public async Task Saving_before_restart_locks_editing_writes_the_open_file_and_can_give_editing_back()
    {
        var path = Doc("draft.md", "old");
        await _editor.OpenArbitraryFileAsync(path);
        _editor.Text = "new words";
        var lockedDuringSave = false;
        _window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsRestarting) && _window.IsRestarting) lockedDuringSave = true;
        };

        Assert.Null(await _window.SaveAllBeforeRestartAsync(TimeSpan.FromSeconds(5)));

        Assert.True(lockedDuringSave);
        Assert.True(_window.IsRestarting); // stays locked: Hymnal is about to exit
        Assert.False(_window.HasUnsavedWork);
        Assert.Equal("new words", await File.ReadAllTextAsync(path));

        _window.CancelRestart(); // the new copy wouldn't start
        Assert.False(_window.IsRestarting);
    }

    [Fact]
    public async Task A_failed_save_before_restart_gives_editing_back()
    {
        await _editor.OpenArbitraryFileAsync(Doc("draft.md", "old"));
        _editor.Text = "new words";
        // The file changed on disk while it had edits: that needs the author's choice, so nothing restarts.
        typeof(EditorViewModel).GetProperty(nameof(EditorViewModel.HasConflict))!.SetValue(_editor, true);

        Assert.Equal(UpdateRestart.CouldNotSave, await _window.SaveAllBeforeRestartAsync(TimeSpan.FromSeconds(5)));

        Assert.False(_window.IsRestarting);
        Assert.True(_editor.IsDirty);
        Assert.True(_window.HasUnsavedWork);
    }

    private string Doc(string name, string content)
    {
        var path = Path.Combine(_root, ".hymnal-data", "docs", name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            _updates.Dispose();
            _owned.Dispose();
            _settings.Dispose();
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
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

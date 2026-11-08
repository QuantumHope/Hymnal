using System.Reflection;
using System.Text;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Infrastructure.Ai;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

/// <summary>
/// The editor hides a story file's trailing <c>&lt;!-- hymnal:v1 … --&gt;</c> block (split on load, merged
/// back on save), acts as the <see cref="StoryFileWriter"/>'s open buffer, and counts prose only.
/// </summary>
public sealed class EditorEmbeddedBlockTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly OwnedDisposables _owned = new();
    private readonly RecordingNotifications _notifications = new();
    private readonly StoryFileWriter _writer = new(new MetadataStore());

    static EditorEmbeddedBlockTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    public EditorEmbeddedBlockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hymnal-editor-block-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            _owned.Dispose();
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static EmbeddedStoryMetadata Meta(string notes, ChapterStatus? status = ChapterStatus.Drafting) =>
        new("story-1", status, null, null, null, null, notes, null);

    private EditorViewModel CreateEditor(OwnedDisposables? owner = null)
    {
        var editor = (owner ?? _owned).Track(new EditorViewModel(
            new MetadataStore(), _notifications, new WordCountService(), _writer) { HasWorkspace = true });
        _writer.AttachBuffer(editor);
        return editor;
    }

    private string WriteFile(string name, string text) => WriteBytes(name, Encoding.UTF8.GetBytes(text));

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string ReadText(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path));

    private static ChapterNode Node(string name) => new(name, name, name, NodeKind.Chapter, IsMissing: false, Index: 0);

    private static string Json(EmbeddedStoryMetadata? m) => m is null ? "null" : StoryFileFormat.SerializeMetadata(m);

    private static void ApplyExternalFileChange(EditorViewModel editor, string content)
    {
        var method = typeof(EditorViewModel).GetMethod("ApplyExternalFileChange", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyExternalFileChange was not found.");
        method.Invoke(editor, [content]);
    }

    private static async Task ExecuteAsync(IObservable<System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(_ => { }, ex => completion.TrySetException(ex), () => completion.TrySetResult());
        await completion.Task;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("# Story\n\nIt was a dark night.\n", "\n")]
    [InlineData("# Story\r\n\r\nNo trailing newline.", "\r\n")]
    public async Task Opening_file_with_block_shows_prose_only_and_is_clean(string prose, string lineEnding)
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge(prose, Meta("n"), lineEnding));
        var editor = CreateEditor();

        await editor.OpenChapterAsync(Node("story.md"), path);

        Assert.Equal(prose, editor.Text);
        Assert.Equal(prose, editor.OriginalText);
        Assert.False(editor.IsDirty);
        Assert.Equal(Json(Meta("n")), Json(editor.CurrentMetadata));
        Assert.DoesNotContain("hymnal:v1", editor.Text);
    }

    [Fact]
    public async Task Opening_file_with_malformed_block_shows_it_as_prose_and_informs_once()
    {
        var text = "Prose.\n\n<!-- hymnal:v1\n{ not json\n-->\n";
        var path = WriteFile("story.md", text);
        var editor = CreateEditor();

        await editor.OpenChapterAsync(Node("story.md"), path);
        ApplyExternalFileChange(editor, text);

        Assert.Equal(text, editor.Text);
        Assert.Null(editor.CurrentMetadata);
        Assert.Single(_notifications.Infos, m => m.Contains("story.md") && m.Contains("damaged"));
    }

    [Fact]
    public async Task Opening_file_with_bom_hides_it_from_text_and_saves_it_back()
    {
        var path = WriteBytes("story.md", [.. Utf8Bom, .. Encoding.UTF8.GetBytes("# Title\n\nBody.")]);
        var editor = CreateEditor();

        await editor.OpenChapterAsync(Node("story.md"), path);
        Assert.Equal("# Title\n\nBody.", editor.Text);

        editor.Text = "# Title\n\nBody, edited.";
        await editor.SaveAsync();

        Assert.Equal([.. Utf8Bom, .. Encoding.UTF8.GetBytes("# Title\n\nBody, edited.")], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Opening_non_utf8_story_file_fails_without_changing_editor()
    {
        var path = WriteBytes("story.md", [0x48, 0xFF, 0x49]);
        var editor = CreateEditor();

        await Assert.ThrowsAsync<IOException>(() => editor.OpenChapterAsync(Node("story.md"), path));

        Assert.Null(editor.ActiveFilePath);
        Assert.False(editor.HoldsFile(path));
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Editing_prose_then_saving_keeps_the_block_bytes(string lineEnding)
    {
        var prose = $"# Story{lineEnding}{lineEnding}Before.";
        var original = StoryFileFormat.Merge(prose, Meta("keep"), lineEnding);
        var path = WriteFile("story.md", original);
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);

        var edited = $"# Story{lineEnding}{lineEnding}After, longer.";
        editor.Text = edited;
        await editor.SaveAsync();

        var saved = ReadText(path);
        Assert.StartsWith(edited, saved);
        Assert.Equal(original[prose.Length..], saved[edited.Length..]);
        Assert.Equal(edited, StoryFileFormat.Split(saved).Prose);
        Assert.False(editor.IsDirty);
    }

    [Theory]
    [InlineData("# Title\n\nBody.\n")]
    [InlineData("# Title\r\n\r\nBody.")]
    [InlineData("")]
    public async Task File_without_block_saves_byte_identical(string original)
    {
        var path = WriteFile("story.md", original);
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);
        Assert.Equal(original, editor.Text);
        Assert.Null(editor.CurrentMetadata);

        editor.Text = original + "x";
        editor.Text = original;
        editor.Text = original + " more";
        await editor.SaveAsync();

        Assert.Equal(Encoding.UTF8.GetBytes(original + " more"), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Arbitrary_file_and_book_txt_keep_block_looking_text_visible()
    {
        var text = StoryFileFormat.Merge("Research.", Meta("n"), "\n");
        var doc = WriteFile("doc.md", text);
        var book = WriteFile("Book.txt", text);
        var editor = CreateEditor();

        await editor.OpenArbitraryFileAsync(doc);
        Assert.Equal(text, editor.Text);
        Assert.False(editor.HoldsFile(doc));
        editor.Text = text + "more";
        await editor.SaveAsync();
        Assert.Equal(text + "more", ReadText(doc));

        await editor.SelectBookAsync(book);
        Assert.Equal(text, editor.Text);
        Assert.False(editor.HoldsFile(book));
    }

    // ── IOpenStoryBuffer ─────────────────────────────────────────────────────

    [Fact]
    public async Task Holds_file_matches_normalized_and_linked_spellings_only_while_open()
    {
        var path = WriteFile("story.md", "Prose.");
        var link = Path.Combine(_root, "link.md");
        File.CreateSymbolicLink(link, path);
        var other = WriteFile("other.md", "Other.");
        var editor = CreateEditor();

        Assert.False(editor.HoldsFile(path));
        await editor.OpenChapterAsync(Node("link.md"), link);

        Assert.True(editor.HoldsFile(link));
        Assert.True(editor.HoldsFile(path));
        Assert.True(editor.HoldsFile(Path.Combine(_root, "sub", "..", "story.md")));
        Assert.False(editor.HoldsFile(other));

        editor.CloseChapter();
        Assert.False(editor.HoldsFile(path));
        Assert.Null(editor.CurrentMetadata);
    }

    [Fact]
    public async Task Saving_through_a_link_keeps_the_link()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Prose.", Meta("n"), "\n"));
        var link = Path.Combine(_root, "link.md");
        File.CreateSymbolicLink(link, path);
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("link.md"), link);

        editor.Text = "Prose, edited.";
        await editor.SaveAsync();

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(StoryFileFormat.Merge("Prose, edited.", Meta("n"), "\n"), ReadText(path));
    }

    [Fact]
    public async Task Apply_metadata_on_dirty_buffer_saves_new_prose_and_new_metadata()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Old prose.", Meta("old"), "\n"));
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);
        editor.Text = "Unsaved prose.";

        var result = await editor.ApplyMetadataAsync(Meta("new", ChapterStatus.Editing));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(StoryFileFormat.Merge("Unsaved prose.", Meta("new", ChapterStatus.Editing), "\n"), ReadText(path));
        Assert.False(editor.IsDirty);
        Assert.Equal("Unsaved prose.", editor.Text);
        Assert.Equal(Json(Meta("new", ChapterStatus.Editing)), Json(editor.CurrentMetadata));
    }

    [Fact]
    public async Task Apply_null_metadata_saves_without_block()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Prose.\n", Meta("old"), "\n"));
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);

        var result = await editor.ApplyMetadataAsync(null);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Prose.\n", ReadText(path));
        Assert.Null(editor.CurrentMetadata);
    }

    [Fact]
    public async Task Apply_metadata_failure_keeps_previous_metadata()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Prose.", Meta("old"), "\n"));
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        try
        {
            var result = await editor.ApplyMetadataAsync(Meta("new"));

            Assert.False(result.IsSuccess);
            Assert.Equal(Json(Meta("old")), Json(editor.CurrentMetadata));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Writer_update_for_open_file_goes_through_editor_without_deadlock()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Prose.", Meta("old"), "\n"));
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);
        editor.Text = "Unsaved.";

        var result = await Task.Run(() => _writer.UpdateMetadataAsync(path, m => m! with { Notes = "via writer" }))
            .WaitAsync(Step);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(StoryFileFormat.Merge("Unsaved.", Meta("via writer"), "\n"), ReadText(path));
        Assert.False(editor.IsDirty);
        Assert.Equal("via writer", editor.CurrentMetadata?.Notes);
    }

    [Fact]
    public async Task Writer_update_for_open_file_on_ui_thread_completes()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Prose.", Meta("old"), "\n"));

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);
            await editor.OpenChapterAsync(Node("story.md"), path);
            editor.Text = "Unsaved on UI.";

            var update = Task.Run(() => _writer.UpdateMetadataAsync(path, m => m! with { Notes = "ui" }));
            await PlanTestHarness.PumpUntilAsync(() => update.IsCompleted);

            Assert.True(update.Result.IsSuccess, update.Result.Error);
            Assert.Equal("ui", editor.CurrentMetadata?.Notes);
            Assert.False(editor.IsDirty);
            return 0;
        }, TimeSpan.FromSeconds(30));

        Assert.Equal(StoryFileFormat.Merge("Unsaved on UI.", Meta("ui"), "\n"), ReadText(path));
    }

    [Fact]
    public async Task Opening_while_writer_update_is_pending_never_keeps_stale_metadata()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Prose.", Meta("old"), "\n"));
        var editor = CreateEditor();

        var hold = await _writer.HoldMetadataChangesAsync(path);
        var update = _writer.UpdateMetadataAsync(path, m => m! with { Notes = "pending" });
        var open = editor.OpenChapterAsync(Node("story.md"), path);
        await Task.Delay(50);
        hold.Dispose();
        await open.WaitAsync(Step);
        var result = await update.WaitAsync(Step);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("pending", editor.CurrentMetadata?.Notes);
        Assert.Equal("Prose.", editor.Text);
        Assert.Equal("pending", StoryFileFormat.Split(ReadText(path)).Metadata?.Notes);
    }

    // ── External changes ─────────────────────────────────────────────────────

    [Fact]
    public async Task External_change_with_new_block_reloads_prose_and_metadata()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Before.", Meta("old"), "\n"));

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);
            await editor.OpenChapterAsync(Node("story.md"), path);

            await File.WriteAllTextAsync(path, StoryFileFormat.Merge("After checkout.", Meta("checked out"), "\n"));

            await PlanTestHarness.PumpUntilAsync(() => editor.Text == "After checkout.");
            Assert.Equal("checked out", editor.CurrentMetadata?.Notes);
            Assert.False(editor.IsDirty);
            Assert.DoesNotContain("hymnal:v1", editor.Text);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task External_change_while_dirty_keeps_conflict_behaviour_and_accept_reloads_both()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Before.", Meta("old"), "\n"));
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);
        editor.Text = "Local edit.";
        var external = StoryFileFormat.Merge("External.", Meta("external"), "\n");
        await File.WriteAllTextAsync(path, external);

        ApplyExternalFileChange(editor, external);

        Assert.True(editor.HasConflict);
        Assert.Equal("Local edit.", editor.Text);
        Assert.Equal("old", editor.CurrentMetadata?.Notes);

        await ExecuteAsync(editor.AcceptExternalCommand.Execute());

        Assert.False(editor.HasConflict);
        Assert.Equal("External.", editor.Text);
        Assert.Equal("external", editor.CurrentMetadata?.Notes);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task External_change_on_clean_buffer_reloads_both()
    {
        var path = WriteFile("story.md", StoryFileFormat.Merge("Before.", Meta("old"), "\n"));
        var editor = CreateEditor();
        await editor.OpenChapterAsync(Node("story.md"), path);
        var external = StoryFileFormat.Merge("External.", Meta("external"), "\n");

        ApplyExternalFileChange(editor, external);

        Assert.Equal("External.", editor.Text);
        Assert.Equal("external", editor.CurrentMetadata?.Notes);
        Assert.False(editor.HasConflict);
    }

    // ── Word counts and AI context ───────────────────────────────────────────

    [Fact]
    public async Task Live_word_count_excludes_block()
    {
        var notes = string.Join(' ', Enumerable.Repeat("word", 50));
        var path = WriteFile("story.md", StoryFileFormat.Merge("One two three.", Meta(notes), "\n"));

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);
            await editor.OpenChapterAsync(Node("story.md"), path);

            await PlanTestHarness.PumpUntilAsync(() => editor.LiveWordCount == 3);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Hydration_word_count_excludes_block()
    {
        var notes = string.Join(' ', Enumerable.Repeat("word", 50));
        var root = PlanTestHarness.CreateWorkspace(PlanTestHarness.StoryJson,
            ("story.md", StoryFileFormat.Merge("# Story\n\nOne two three.", Meta(notes), "\n")));
        try
        {
            await PlanTestHarness.Ui(root, 1, async context =>
            {
                await PlanTestHarness.PumpUntilAsync(() => context.Node("story.md").WordCountKnown);
                Assert.Equal(new WordCountService().CountWords("# Story\n\nOne two three."), context.Node("story.md").WordCount);
            });
        }
        finally
        {
            PlanTestHarness.DeleteFolder(root);
        }
    }

    [Fact]
    public async Task Manuscript_context_reader_returns_prose_only()
    {
        WriteFile("story.md", StoryFileFormat.Merge("Prose only.\n", Meta("secret notes"), "\n"));

        var text = await new ManuscriptContextReader().ReadChapterTextAsync(_root, "story.md", CancellationToken.None);

        Assert.Equal("Prose only.\n", text);
    }

    private sealed class RecordingNotifications : INotificationService
    {
        private readonly object _gate = new();
        private readonly List<string> _infos = [];

        public IReadOnlyList<string> Infos
        {
            get { lock (_gate) return _infos.ToList(); }
        }

        public void ShowError(string message) { }
        public void ShowInfo(string message) { lock (_gate) _infos.Add(message); }
        public void ShowSuccess(string message) { }
    }
}

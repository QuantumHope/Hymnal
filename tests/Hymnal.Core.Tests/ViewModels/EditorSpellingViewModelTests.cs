using System.Reactive.Linq;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Spelling;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.Spelling;
using Hymnal.Spelling;

namespace Hymnal.ViewModels;

/// <summary>Spell check in the editor: off until asked for, on request for one file, or as you type.</summary>
[Collection("AvaloniaUi")]
public sealed class EditorSpellingViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly OwnedDisposables _owned = new();

    static EditorSpellingViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    public EditorSpellingViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hymnal-spelling-vm-tests", Guid.NewGuid().ToString("N"));
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

    internal static SpellingService CreateService(string? dictionaryFolder = null)
    {
        var prefs = new SpellCheckPreferences(null) { Language = "xx-TEST" };
        return new SpellingService(
            new DictionaryCatalog(dictionaryFolder ?? SpellCheckerTests.FixtureFolder, bundle: null),
            new ProjectDictionaryService(new MetadataStore()),
            prefs);
    }

    private EditorViewModel CreateEditor(OwnedDisposables owner, SpellingService? spelling, INotificationService? notifications = null)
    {
        var store = new MetadataStore();
        var writer = new StoryFileWriter(store);
        var editor = owner.Track(new EditorViewModel(store, notifications ?? new RecordingNotifications(), new WordCountService(), writer, spelling)
        {
            HasWorkspace = true,
        });
        writer.AttachBuffer(editor);
        return editor;
    }

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static ChapterNode Node(string name) => new(name, name, name, NodeKind.Chapter, IsMissing: false, Index: 0);

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var waited = 0;
        while (!condition())
        {
            if (waited >= timeoutMs)
                Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
            waited += 20;
        }
    }

    [Fact]
    public async Task Is_off_until_asked_for_and_reads_no_dictionary()
    {
        var path = WriteFile("one.md", "The qwzx cat.");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            using var service = CreateService();
            var editor = CreateEditor(owner, service);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));
            await Task.Delay(EditorSpellingViewModel.TypingPause * 2);

            Assert.True(editor.Spelling.IsAvailable);
            Assert.False(editor.Spelling.IsActive);
            Assert.Empty(editor.Spelling.Scan.Issues);
            Assert.Equal("", editor.Spelling.Summary);
            Assert.Equal(SpellingStatus.Idle, service.Status);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Check_spelling_marks_the_open_file_and_goes_to_the_first_issue()
    {
        var path = WriteFile("one.md", "The qwzx cat sat on the zzyx mat.");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            using var service = CreateService();
            var editor = CreateEditor(owner, service);
            var navigation = new List<int>();
            using var navSub = editor.Spelling.NavigationRequests.Subscribe(navigation.Add);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));

            await editor.Spelling.CheckSpellingCommand.Execute();
            await Until(() => editor.Spelling.Scan.Issues.Count == 2, "the scan");

            Assert.True(editor.Spelling.IsActive);
            Assert.True(editor.Spelling.CanClear);
            Assert.Equal(["qwzx", "zzyx"], editor.Spelling.Scan.Issues.Select(i => i.Word));
            Assert.Equal("2 spelling issues", editor.Spelling.Summary);
            Assert.Equal([1], navigation);
            Assert.Contains("quick", CreateServiceSuggestions(editor, "quikc"));

            await editor.Spelling.ClearCommand.Execute();
            await Until(() => editor.Spelling.Scan.Issues.Count == 0, "the marks to clear");
            Assert.False(editor.Spelling.IsActive);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    private static IReadOnlyList<string> CreateServiceSuggestions(EditorViewModel editor, string word) =>
        editor.Spelling.Suggest(word);

    [Fact]
    public async Task A_requested_check_ends_when_another_file_opens()
    {
        var one = WriteFile("one.md", "The qwzx cat.");
        var two = WriteFile("two.md", "The zzyx dog.");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            using var service = CreateService();
            var editor = CreateEditor(owner, service);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), one));
            await editor.Spelling.CheckSpellingCommand.Execute();
            await Until(() => editor.Spelling.Scan.Issues.Count == 1, "the scan");

            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("two.md"), two));
            await Until(() => !editor.Spelling.IsActive && editor.Spelling.Scan.Issues.Count == 0, "the check to end");
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Check_as_you_type_follows_edits_but_never_checks_book_txt()
    {
        var path = WriteFile("one.md", "The cat.");
        var book = WriteFile("Book.txt", "one.md\nqwzx.md\n");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            using var service = CreateService();
            service.Preferences.CheckAsYouType = true;
            var editor = CreateEditor(owner, service);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));

            await Until(() => editor.Spelling.IsActive && service.Status == SpellingStatus.Ready, "checking to start");
            Assert.False(editor.Spelling.CanClear);
            await Until(() => editor.Spelling.Summary == "No spelling issues", "a clean scan");

            editor.Text = "The cat qwzx.";
            await Until(() => editor.Spelling.Scan.Issues.Count == 1, "the edit to be checked");
            Assert.Equal("1 spelling issue", editor.Spelling.Summary);

            await PlanTestHarness.Within(editor.SelectBookAsync(book));
            await Until(() => !editor.Spelling.IsActive && editor.Spelling.Scan.Issues.Count == 0, "Book.txt to be left alone");
            Assert.False(await editor.Spelling.CheckSpellingCommand.CanExecute.FirstAsync());
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Adding_to_the_project_dictionary_and_ignoring_clear_marks()
    {
        var path = WriteFile("one.md", "Elara and the qwzx cat.");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            using var service = CreateService();
            await service.SetWorkspaceAsync(_root);
            var editor = CreateEditor(owner, service);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));
            await editor.Spelling.CheckSpellingCommand.Execute();
            await Until(() => editor.Spelling.Scan.Issues.Count == 2, "the scan");
            Assert.True(editor.Spelling.CanAddToDictionary);

            await editor.Spelling.AddToDictionaryCommand.Execute("Elara");
            await Until(() => editor.Spelling.Scan.Issues.Count == 1, "Elara to be accepted");
            Assert.Equal(["Elara"], service.ProjectWords);
            Assert.Contains("Elara", await File.ReadAllTextAsync(ProjectDictionaryService.PathFor(_root)));

            await editor.Spelling.IgnoreCommand.Execute("qwzx");
            await Until(() => editor.Spelling.Scan.Issues.Count == 0, "qwzx to be ignored");
            Assert.Equal("No spelling issues", editor.Spelling.Summary);
            Assert.Equal(["Elara"], service.ProjectWords); // ignoring doesn't touch the project dictionary
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Without_a_dictionary_a_check_explains_how_to_add_one()
    {
        var path = WriteFile("one.md", "The qwzx cat.");
        var empty = Path.Combine(_root, "no-dictionaries");
        Directory.CreateDirectory(empty);

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            using var service = CreateService(empty);
            var notifications = new RecordingNotifications();
            var editor = CreateEditor(owner, service, notifications);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));

            await editor.Spelling.CheckSpellingCommand.Execute();
            await Until(() => service.Status == SpellingStatus.Unavailable, "the load to fail");
            Assert.Equal("Spell check unavailable", editor.Spelling.Summary);
            Assert.Contains(empty, service.StatusMessage);

            // Asking again says why, instead of doing nothing.
            await editor.Spelling.CheckSpellingCommand.Execute();
            Assert.Contains(notifications.Infos, m => m.Contains(".dic"));
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void An_editor_without_a_spell_checker_offers_none()
    {
        using var owner = new OwnedDisposables();
        var editor = CreateEditor(owner, spelling: null);

        Assert.False(editor.Spelling.IsAvailable);
        Assert.False(editor.Spelling.IsActive);
        Assert.Empty(editor.Spelling.Suggest("qwzx"));
    }

    [Theory]
    [InlineData(false, false, 3, SpellingStatus.Ready, "")]
    [InlineData(true, false, 0, SpellingStatus.Loading, "Checking spelling…")]
    [InlineData(true, true, 0, SpellingStatus.Ready, "Checking spelling…")]
    [InlineData(true, false, 0, SpellingStatus.Ready, "No spelling issues")]
    [InlineData(true, true, 1, SpellingStatus.Ready, "1 spelling issue")]
    [InlineData(true, false, 12, SpellingStatus.Ready, "12 spelling issues")]
    [InlineData(true, false, 0, SpellingStatus.Unavailable, "Spell check unavailable")]
    public void Summary_describes_the_state(bool active, bool checking, int issues, SpellingStatus status, string expected)
    {
        var scan = new SpellingScan("", Enumerable.Range(0, issues).Select(i => new SpellingIssue(i, 1, "x")).ToList());

        Assert.Equal(expected, EditorSpellingViewModel.Describe(active, checking, scan, status));
    }

    internal sealed class RecordingNotifications : INotificationService
    {
        public List<string> Infos { get; } = [];
        public List<string> Errors { get; } = [];

        public void ShowError(string message) => Errors.Add(message);

        public void ShowInfo(string message) => Infos.Add(message);

        public void ShowSuccess(string message) { }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Closing the window saves what the author hasn't: the open file's text first, then pending notes.</summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowCloseSavesTests
{
    static MainWindowCloseSavesTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private static async Task OpenAsync(PlanTestContext context, string relativePath)
    {
        context.Workspace.SelectedNode = context.Node(relativePath);
        await PumpUntilAsync(() => context.Editor.ActiveFilePath?.EndsWith(relativePath, StringComparison.Ordinal) == true
                                   && !context.Editor.IsDirty);
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative)).Replace("\r\n", "\n");

    [Fact]
    public async Task Closing_saves_the_editors_unsaved_text()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nFirst draft.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await OpenAsync(context, "story.md");

                context.Editor.Text = "# Story\n\nTyped just before closing.\n";
                Assert.True(context.Editor.IsDirty);

                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(CloseTimeout)));
                Assert.False(context.Editor.IsDirty);
            });

            Assert.Contains("Typed just before closing.", Read(root, "story.md"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_never_writes_over_a_file_that_changed_on_disk_and_says_so()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nFirst draft.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await OpenAsync(context, "story.md");

                context.Editor.Text = "# Story\n\nLocal edit.\n";
                await File.WriteAllTextAsync(Path.Combine(root, "story.md"), "# Story\n\nChanged elsewhere.\n");
                await Within(context.Editor.ProcessExternalFileChangeNowAsync());
                Assert.True(context.Editor.HasConflict);

                // Not saved: the close asks instead, and explains why.
                Assert.False(await Within(window.FlushPendingEditsBeforeCloseAsync(CloseTimeout)));
                Assert.Contains("changed on disk", window.UnsavedOnCloseMessage);
            });

            Assert.Contains("Changed elsewhere.", Read(root, "story.md"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_saves_a_note_typed_just_before()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await OpenAsync(context, "story.md");
                await PumpUntilAsync(() => window.NotesViewModel.LoadError == null);

                // The debounced save hasn't run yet.
                window.NotesViewModel.Text = "A note typed just before closing.";
                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(CloseTimeout)));
            });

            var notes = Directory.GetFiles(Path.Combine(root, ".hymnal-data"), "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText);
            Assert.Contains(notes, text => text.Contains("A note typed just before closing."));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_before_the_watcher_reports_an_outside_change_still_never_writes_over_it()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nFirst draft.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await OpenAsync(context, "story.md");

                context.Editor.Text = "# Story\n\nLocal edit.\n";
                // Changed on disk, and the window closes before the watcher's debounce has flagged it.
                await File.WriteAllTextAsync(Path.Combine(root, "story.md"), "# Story\n\nChanged elsewhere.\n");

                Assert.False(await Within(window.FlushPendingEditsBeforeCloseAsync(CloseTimeout)));
                Assert.True(context.Editor.HasConflict);
                Assert.Contains("changed on disk", window.UnsavedOnCloseMessage);
            });

            Assert.Contains("Changed elsewhere.", Read(root, "story.md"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_after_keeping_my_edits_over_an_outside_change_saves_them()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nFirst draft.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await OpenAsync(context, "story.md");

                context.Editor.Text = "# Story\n\nLocal edit.\n";
                await File.WriteAllTextAsync(Path.Combine(root, "story.md"), "# Story\n\nChanged elsewhere.\n");
                await Within(context.Editor.ProcessExternalFileChangeNowAsync());
                Assert.True(context.Editor.HasConflict);
                await Within(context.Editor.KeepLocalCommand.Execute().ToTask());

                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(CloseTimeout)));
            });

            Assert.Contains("Local edit.", Read(root, "story.md"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_saves_a_note_even_when_saving_the_text_retitles_the_story()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await OpenAsync(context, "story.md");
                await PumpUntilAsync(() => window.NotesViewModel.LoadError == null);

                // A new heading retitles the story when the text is saved; the note is still waiting on its debounce.
                context.Editor.Text = "# A New Title\n\nText.\n";
                window.NotesViewModel.Text = "A note kept across the retitle.";
                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(CloseTimeout)));
            });

            var notes = Directory.GetFiles(Path.Combine(root, ".hymnal-data"), "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText);
            Assert.Contains(notes, text => text.Contains("A note kept across the retitle."));
        }
        finally { DeleteFolder(root); }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Views;
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
                Assert.Contains("changed on disk", MainWindow.CloseWithUnsavedMessage(window));
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
}

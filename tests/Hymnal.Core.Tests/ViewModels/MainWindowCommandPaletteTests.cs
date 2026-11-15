using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Markua.Editing;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>The command palette over a real workspace: what it lists and what choosing an entry does.</summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowCommandPaletteTests
{
    static MainWindowCommandPaletteTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Go_to_lists_the_projects_files_in_its_own_words_and_opens_one_in_Write()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("lighthouse.md", "# The Lighthouse\n\nText.\n"),
            ("harbour/part.md", "{class: part}\n# Harbour Tales\n"),
            ("harbour/arrival.md", "# Arrival\n\nText.\n"),
            ("harbour/departure.md", "# Departure\n\nText.\n"));
        try
        {
            await Ui(root, 4, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await Within(window.SelectPlanCommand.Execute().ToTask());

                await Within(window.ShowGoToCommand.Execute().ToTask());
                var palette = window.CommandPalette;
                Assert.True(palette.IsOpen);

                // The divider can't be opened; its scenes carry its title.
                Assert.Equal(["The Lighthouse", "Arrival", "Departure"], palette.Results.Select(r => r.Title));
                var arrival = palette.Results.Single(r => r.Title == "Arrival");
                Assert.Equal(context.Workspace.NounFor(context.Node("harbour/arrival.md")), arrival.Category);
                Assert.Contains("Harbour Tales", arrival.Detail);

                palette.Query = "harbour depart";
                Assert.Equal("Departure", palette.Selected?.Title);
                await Within(palette.RunSelectedCommand.Execute().ToTask());

                Assert.False(palette.IsOpen);
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.Equal("harbour/departure.md", context.Workspace.SelectedNode?.Node.RelativePath);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Commands_list_what_can_run_with_shortcuts_and_run_them()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost(), context.CreateWriteBeats());
                await PumpUntilAsync(() => window.IsBeatsSectionAvailable);

                await Within(window.ShowCommandsCommand.Execute().ToTask());
                var palette = window.CommandPalette;
                Assert.True(palette.IsCommandMode);
                Assert.All(palette.Results, r => Assert.Equal(PaletteEntryKind.Command, r.Kind));

                var titles = palette.Results.Select(r => r.Title).ToList();
                Assert.Contains("Go to Plan", titles);
                Assert.Contains("Toggle Beats", titles);           // a story has beats
                Assert.DoesNotContain("Save", titles);              // nothing to save
                Assert.DoesNotContain("Go to Ideas", titles);       // this window has no Ideas view model
                Assert.Equal("F11", palette.Results.Single(r => r.Title == "Focus Mode").Shortcut);

                palette.Query = ">focus";
                Assert.Equal("Focus Mode", palette.Selected?.Title);
                await Within(palette.RunSelectedCommand.Execute().ToTask());
                Assert.True(window.IsFocusMode);

                // Its title follows the state the next time the palette opens.
                await Within(window.ShowCommandsCommand.Execute().ToTask());
                Assert.Equal("Leave Focus Mode", palette.Results[0].Title); // and it was run last
                palette.Close();
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_novel_offers_no_beats_and_names_its_chapters()
    {
        var root = CreateWorkspace(NovelJson,
            ("one.md", "# One\n\nText.\n"),
            ("two.md", "# Two\n\nText.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost(), context.CreateWriteBeats());
                await context.SettleAsync();

                await Within(window.ShowCommandsCommand.Execute().ToTask());
                Assert.DoesNotContain(window.CommandPalette.Results, r => r.Title == "Toggle Beats");

                await Within(window.ShowGoToCommand.Execute().ToTask());
                Assert.Equal(["Chapter", "Chapter"], window.CommandPalette.Results.Select(r => r.Category));
                window.CommandPalette.Close();
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Insert_commands_are_listed_while_a_chapters_source_shows_and_apply_to_the_editor()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                var palette = window.CommandPalette;
                var editor = context.Editor;
                var surface = new FakeSurface();
                using var _ = editor.AttachSurface(surface);

                // Nothing open yet: nothing to insert into.
                await Within(window.ShowCommandsCommand.Execute().ToTask());
                Assert.DoesNotContain(palette.Results, r => r.Category == "Insert");
                palette.Close();

                context.Workspace.SelectedNode = context.Node("story.md");
                await PumpUntilAsync(() => editor.ActiveNode != null && editor.CanInsertMarkua);

                await Within(window.ShowCommandsCommand.Execute().ToTask());
                var bold = palette.Results.Single(r => r.Title == "Bold");
                Assert.Equal("Insert", bold.Category);
                Assert.Equal("Ctrl+B", bold.Shortcut);
                Assert.Equal("**text**", bold.Detail);
                Assert.Contains(palette.Results, r => r.Title == "Callout: Warning (W>)");

                surface.Text = editor.Text;
                surface.Selection = new TextSpan(surface.Text.IndexOf("Text", StringComparison.Ordinal), 4);
                palette.Query = ">bold";
                Assert.Equal("Bold", palette.Selected?.Title);
                await Within(palette.RunSelectedCommand.Execute().ToTask());
                Assert.Equal("# Story\n\n**Text**.\n", surface.Text);

                // Ctrl+B is bold while the editor has focus; the window's Ctrl+B (sidebar) stands down then.
                Assert.True(await window.ToggleSidebarShortcutCommand.CanExecute.FirstAsyncTask());
                editor.IsSourceFocused = true;
                Assert.False(await window.ToggleSidebarShortcutCommand.CanExecute.FirstAsyncTask());
                Assert.True(await window.ToggleSidebarCommand.CanExecute.FirstAsyncTask()); // menu and palette
                editor.IsSourceFocused = false;

                // Preview only hides the source, and Plan the editor: nothing to insert into.
                await Within(editor.SetViewModeCommand.Execute(EditorViewMode.Preview).ToTask());
                await Within(window.ShowCommandsCommand.Execute().ToTask());
                Assert.DoesNotContain(palette.Results, r => r.Category == "Insert");
                palette.Close();

                await Within(editor.SetViewModeCommand.Execute(EditorViewMode.Split).ToTask());
                Assert.True(editor.CanInsertMarkua);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                Assert.False(editor.CanInsertMarkua);
                Assert.False(editor.InsertSnippet(MarkuaSnippets.Get("bold")));
            });
        }
        finally { DeleteFolder(root); }
    }

    /// <summary>Stands in for EditorView: holds text and a selection, and applies edits to them.</summary>
    private sealed class FakeSurface : IEditorTextSurface
    {
        public bool IsReadyForEdits { get; set; } = true;
        public string Text { get; set; } = "";
        public TextSpan Selection { get; set; }

        public void Apply(TextEdit edit)
        {
            Text = edit.ApplyTo(Text);
            Selection = edit.Selection;
        }
    }

    [Fact]
    public async Task Esc_closes_the_palette_before_it_leaves_focus_mode()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                await Within(window.ToggleFocusModeCommand.Execute().ToTask());
                await Within(window.ShowGoToCommand.Execute().ToTask());
                await context.SettleAsync();

                Assert.True(await window.CommandPalette.CloseCommand.CanExecute.FirstAsyncTask());
                Assert.False(await window.ExitFocusModeCommand.CanExecute.FirstAsyncTask());

                await Within(window.CommandPalette.CloseCommand.Execute().ToTask());
                await context.SettleAsync();

                Assert.True(window.IsFocusMode);
                Assert.False(await window.CommandPalette.CloseCommand.CanExecute.FirstAsyncTask());
                Assert.True(await window.ExitFocusModeCommand.CanExecute.FirstAsyncTask());
            });
        }
        finally { DeleteFolder(root); }
    }
}

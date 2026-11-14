using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Write's rails collapse to their icon strips without closing their panes.</summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowRailCollapseTests
{
    static MainWindowRailCollapseTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Left_rail_collapses_and_comes_back_with_the_same_panes()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                window.WorkspaceViewModel.IsChaptersPaneVisible = true;
                window.SupplementalDocsViewModel.IsVisible = true;
                Assert.True(window.IsAnyLeftPaneOpen);

                await Within(window.ToggleSidebarCommand.Execute().ToTask());
                Assert.True(window.IsLeftRailCollapsed);
                Assert.False(window.IsAnyLeftPaneOpen);
                Assert.True(window.WorkspaceViewModel.IsChaptersPaneVisible);
                Assert.True(window.SupplementalDocsViewModel.IsVisible);
                Assert.True(await context.Settings.GetAsync<bool?>(MainWindowViewModel.LeftRailCollapsedKey));

                await Within(window.ToggleSidebarCommand.Execute().ToTask());
                Assert.False(window.IsLeftRailCollapsed);
                Assert.True(window.IsAnyLeftPaneOpen);
                Assert.True(window.IsBothLeftPanesOpen);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task An_icon_or_a_pane_shown_elsewhere_expands_a_collapsed_rail()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                window.WorkspaceViewModel.IsChaptersPaneVisible = true;
                window.SupplementalDocsViewModel.IsVisible = false;

                window.SetLeftRailCollapsed(true);
                await Within(window.OpenLeftPaneCommand.Execute("docs").ToTask());
                Assert.False(window.IsLeftRailCollapsed);
                Assert.True(window.SupplementalDocsViewModel.IsVisible);

                window.SetLeftRailCollapsed(true);
                window.SupplementalDocsViewModel.IsVisible = false;
                window.SupplementalDocsViewModel.IsVisible = true;
                Assert.False(window.IsLeftRailCollapsed);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Right_rail_collapses_and_a_pane_shortcut_expands_it_with_that_pane()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                window.AiChatViewModel.IsVisible = true;
                Assert.True(window.IsAnyRightPaneOpen);

                await Within(window.ToggleRightRailCommand.Execute().ToTask());
                Assert.True(window.IsRightRailCollapsed);
                Assert.False(window.IsAnyRightPaneOpen);
                Assert.True(window.AiChatViewModel.IsVisible);

                // Ctrl+Shift+A on a collapsed rail shows the chat rather than hiding it unseen.
                await Within(window.ToggleRightPaneCommand.Execute("chat").ToTask());
                Assert.False(window.IsRightRailCollapsed);
                Assert.True(window.AiChatViewModel.IsVisible);

                // On an expanded rail it toggles as before.
                await Within(window.ToggleRightPaneCommand.Execute("chat").ToTask());
                Assert.False(window.AiChatViewModel.IsVisible);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Resetting_the_layout_expands_both_rails()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                window.SetLeftRailCollapsed(true);
                window.SetRightRailCollapsed(true);

                await Within(window.ResetWriteLayoutCommand.Execute().ToTask());

                Assert.False(window.IsLeftRailCollapsed);
                Assert.False(window.IsRightRailCollapsed);
                Assert.True(window.IsAnyLeftPaneOpen);
            });
        }
        finally { DeleteFolder(root); }
    }
}

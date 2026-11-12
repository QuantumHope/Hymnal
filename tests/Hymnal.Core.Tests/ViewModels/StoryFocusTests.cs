using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>An anthology author can work on the whole collection or focus on one story.</summary>
[Collection("AvaloniaUi")]
public sealed class StoryFocusTests
{
    static StoryFocusTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string Lighthouse = "lighthouse.md";
    private const string LongPart = "long-story/part.md";
    private const string LongSceneA = "long-story/scene-a.md";
    private const string LongSceneB = "long-story/scene-b.md";

    [Fact]
    public async Task Focusing_a_scene_narrows_the_sidebar_to_its_story_and_back()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var workspace = context.Workspace;
                Assert.False(workspace.IsStoryFocused);
                Assert.Equal(4, workspace.VisibleNodes.Count);

                workspace.FocusStory(context.Node(LongSceneB));

                Assert.True(workspace.IsStoryFocused);
                Assert.Equal("The Long Story", workspace.FocusedStoryTitle);
                Assert.Equal([LongPart, LongSceneA, LongSceneB],
                    workspace.VisibleNodes.Select(n => n.Node.RelativePath));
                Assert.True(workspace.IsInFocus(context.Node(LongSceneA)));
                Assert.False(workspace.IsInFocus(context.Node(Lighthouse)));

                await Within(workspace.ShowWholeCollectionCommand.Execute().ToTask());
                Assert.False(workspace.IsStoryFocused);
                Assert.Equal(4, workspace.VisibleNodes.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Focusing_moves_the_editor_into_the_story()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var workspace = context.Workspace;
                workspace.SelectedNode = context.Node(LongSceneB);
                await context.SettleAsync();

                workspace.FocusStory(context.Node(Lighthouse));

                Assert.Equal([Lighthouse], workspace.VisibleNodes.Select(n => n.Node.RelativePath));
                Assert.Equal(Lighthouse, workspace.SelectedNode?.Node.RelativePath);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Schedule_shows_only_the_focused_story()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var gantt = context.CreateGantt();
                gantt.SetViewActive(true);
                await PumpUntilAsync(() => gantt.Rows.Count == 5);
                Assert.Equal("COLLECTION", gantt.Rows[0].Title);

                context.Workspace.FocusStory(context.Node(LongPart));
                await PumpUntilAsync(() => gantt.Rows.Count == 4);
                Assert.Equal("THE LONG STORY", gantt.Rows[0].Title);
                Assert.DoesNotContain(gantt.Rows, r => r.RelativePath == Lighthouse);

                context.Workspace.ShowWholeCollection();
                await PumpUntilAsync(() => gantt.Rows.Count == 5);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Plan_opens_on_the_focused_story()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                var window = context.CreateMainWindow(host);
                var part = context.Node(LongPart);

                context.Workspace.FocusStory(context.Node(LongSceneA));
                await Within(window.SelectPlanCommand.Execute().ToTask());

                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);
                Assert.Same(context.Board, host.Content);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Short_story_and_novel_cannot_focus()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                Assert.False(context.Workspace.Profile.SupportsStoryFocus);
                context.Workspace.FocusStory(context.Node("scene-two.md"));
                Assert.False(context.Workspace.IsStoryFocused);
                Assert.Equal(2, context.Workspace.VisibleNodes.Count);
                await Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    private static string CreateAnthology() =>
        CreateWorkspace(AnthologyJson,
            (Lighthouse, "# Lighthouse\n\nThe lamp turns.\n"),
            (LongPart, "{class: part}\n# The Long Story\n"),
            (LongSceneA, "# Scene A\n\nArrival.\n"),
            (LongSceneB, "# Scene B\n\nDeparture.\n"));
}

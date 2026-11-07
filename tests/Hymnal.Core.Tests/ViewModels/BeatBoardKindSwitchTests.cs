using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Settings' Short Story ↔ Anthology switch carries the beat board with the story.</summary>
[Collection("AvaloniaUi")]
public sealed class BeatBoardKindSwitchTests
{
    static BeatBoardKindSwitchTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string Story = BeatBoardService.ShortStoryBoardKey;

    [Fact]
    public async Task Story_to_anthology_moves_the_board_to_the_first_chapter()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == Story);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Title = "Typed just before switching";
                var first = context.Node("scene-one.md");

                await ApplyKindAsync(context, ProjectKind.Anthology);

                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
                await PumpUntilAsync(() => context.Board.BoardKey == null);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal("Typed just before switching", Assert.Single(await LoadFromDiskAsync(root, first.Uuid)).Title);
                Assert.Empty(context.Errors);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_to_story_moves_the_first_story_s_board()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("saga/part.md", "{class: part}\n# The Saga\n"),
            ("saga/one.md", "# One\n\nBegin.\n"),
            ("solo.md", "# Solo\n\nAlone.\n"));
        try
        {
            await Ui(root, 3, async context =>
            {
                var part = context.Node("saga/part.md");
                var solo = context.Node("solo.md");
                await SeedAsync(root, part.Uuid, new Beat("p", "Saga beat", "", null));
                await SeedAsync(root, solo.Uuid, new Beat("s", "Solo beat", "", null));

                await ApplyKindAsync(context, ProjectKind.Story);

                Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
                Assert.Equal("Saga beat", Assert.Single(await LoadFromDiskAsync(root, Story)).Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, part.Uuid)));
                Assert.Equal("Solo beat", Assert.Single(await LoadFromDiskAsync(root, solo.Uuid)).Title);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switch_skips_the_move_when_the_destination_has_a_board()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var keeper = context.Node("the-keeper.md");
                await SeedAsync(root, Story, new Beat("s", "Short story beat", "", null));
                await SeedAsync(root, keeper.Uuid, new Beat("k", "Earlier anthology beat", "", null));
                var story = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, Story));
                var existing = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, keeper.Uuid));

                await ApplyKindAsync(context, ProjectKind.Anthology);

                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
                Assert.Equal(story, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal(existing, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, keeper.Uuid)));
                Assert.Contains(context.Infos, m => m.Contains("The Keeper") && m.Contains("not moved"));
                Assert.Empty(context.Errors);
            });
        }
        finally { DeleteFolder(root); }
    }

    private static async Task ApplyKindAsync(PlanTestContext context, ProjectKind kind)
    {
        var settings = context.CreateSettings();
        settings.SelectedProjectKind = kind;
        Assert.True(await settings.ApplyProjectKindCommand.CanExecute.FirstAsync());
        var apply = settings.ApplyProjectKindCommand.Execute().ToTask();
        await PumpUntilAsync(() => apply.IsCompleted);
        await apply;
        await PumpUntilAsync(() => context.Workspace.Profile.Kind == kind);
    }
}

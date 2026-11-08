using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
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

    [Fact]
    public async Task Story_to_anthology_moves_the_board_to_a_leading_part()
    {
        var root = CreateWorkspace(StoryJson,
            ("saga/part.md", "{class: part}\n# The Saga\n"),
            ("saga/one.md", "# One\n\nBegin.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var part = context.Node("saga/part.md");
                await SeedAsync(root, Story, new Beat("s", "Story beat", "", null));

                await ApplyKindAsync(context, ProjectKind.Anthology);

                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal("Story beat", Assert.Single(await LoadFromDiskAsync(root, part.Uuid)).Title);
                Assert.Empty(context.Errors);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_to_anthology_skips_a_missing_first_chapter()
    {
        var root = CreateWorkspace(StoryJson,
            ("gone.md", "# Gone\n"),
            ("present.md", "# Present\n\nHere.\n"));
        File.Delete(Path.Combine(root, "gone.md"));
        try
        {
            await Ui(root, 2, async context =>
            {
                Assert.True(context.Node("gone.md").Node.IsMissing);
                var present = context.Node("present.md");
                await SeedAsync(root, Story, new Beat("s", "Story beat", "", null));

                await ApplyKindAsync(context, ProjectKind.Anthology);

                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal("Story beat", Assert.Single(await LoadFromDiskAsync(root, present.Uuid)).Title);
                Assert.Empty(context.Errors);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_to_story_skips_a_missing_first_story()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("gone.md", "# Gone\n"),
            ("present.md", "# Present\n\nHere.\n"));
        File.Delete(Path.Combine(root, "gone.md"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var gone = context.Node("gone.md");
                var present = context.Node("present.md");
                await SeedAsync(root, gone.Uuid, new Beat("g", "Gone beat", "", null));
                await SeedAsync(root, present.Uuid, new Beat("p", "Present beat", "", null));

                await ApplyKindAsync(context, ProjectKind.Story);

                Assert.Equal("Present beat", Assert.Single(await LoadFromDiskAsync(root, Story)).Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, present.Uuid)));
                Assert.True(File.Exists(BeatBoardService.BoardFilePath(root, gone.Uuid)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switch_keeps_the_board_when_pending_edits_cannot_be_saved()
    {
        FailingSaveService? boards = null;
        var root = CreateWorkspace(StoryJson, ("scene-one.md", "# Scene One\n\nDusk.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == Story);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                await Within(context.Board.FlushAsync());
                var saved = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, Story));
                var first = context.Node("scene-one.md");
                boards!.FailSaves = true;
                context.Board.Beats[0].Title = "Never saved";

                await ApplyKindAsync(context, ProjectKind.Anthology);

                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
                Assert.Equal(saved, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, Story)));
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, first.Uuid)));
                Assert.Contains("Beat edits couldn't be saved; beats were not moved.", context.Errors);
            }, store => boards = new FailingSaveService(new BeatBoardService(store)));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switch_with_no_story_keeps_the_board_and_says_so()
    {
        var root = CreateWorkspace(StoryJson, ("gone.md", "# Gone\n"));
        File.Delete(Path.Combine(root, "gone.md"));
        try
        {
            await Ui(root, 1, async context =>
            {
                await SeedAsync(root, Story, new Beat("s", "Story beat", "", null));
                var bytes = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, Story));

                await ApplyKindAsync(context, ProjectKind.Anthology);

                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Contains(context.Infos, m => m.Contains("not moved") && m.Contains("Short Story"));
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

    private sealed class FailingSaveService(IBeatBoardService inner) : IBeatBoardService
    {
        /// <summary>While true, saves fail without writing.</summary>
        public bool FailSaves { get; set; }

        public Task<Result<IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey) =>
            inner.LoadAsync(workspaceRoot, boardKey);

        public Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats) =>
            FailSaves
                ? Task.FromResult(Result<Unit>.Fail("The disk is full."))
                : inner.SaveAsync(workspaceRoot, boardKey, beats);

        public bool BoardExists(string workspaceRoot, string boardKey) => inner.BoardExists(workspaceRoot, boardKey);

        public Task<Result<Unit>> MoveBoardAsync(string workspaceRoot, string fromKey, string toKey) =>
            inner.MoveBoardAsync(workspaceRoot, fromKey, toKey);

        public Task<Result<Unit>> DeleteBoardAsync(string workspaceRoot, string boardKey) =>
            inner.DeleteBoardAsync(workspaceRoot, boardKey);
    }
}

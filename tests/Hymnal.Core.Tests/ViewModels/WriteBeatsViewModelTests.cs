using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Write mode's Beats panel: the story being written keeps its beats in view beside the editor.</summary>
[Collection("AvaloniaUi")]
public sealed class WriteBeatsViewModelTests
{
    static WriteBeatsViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string Lighthouse = "lighthouse.md";
    private const string LongPart = "long-story/part.md";
    private const string LongSceneA = "long-story/scene-a.md";
    private const string LongSceneB = "long-story/scene-b.md";

    [Fact]
    public async Task Novel_has_no_beats_panel()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var beats = context.CreateWriteBeats();
                await context.SettleAsync();

                Assert.False(beats.IsAvailable);
                Assert.False(beats.IsOpen);
                Assert.Null(beats.Board.BoardKey);
                Assert.False(await beats.ToggleCommand.CanExecute.FirstAsyncTask());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Short_story_shows_its_board_and_marks_the_beat_of_the_open_scene()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                await SeedAsync(root, BeatBoardService.ShortStoryBoardKey,
                    new Beat("a", "Arrival", "She reaches the tower.", "scene-one.md"),
                    new Beat("b", "The lamp fails", "", "scene-two.md"),
                    new Beat("c", "Unplaced", "", null));

                var beats = context.CreateWriteBeats();
                await PumpUntilAsync(() => beats.Board.Beats.Count == 3);

                Assert.True(beats.IsAvailable);
                Assert.True(beats.IsOpen);
                Assert.True(beats.HasStory);
                Assert.True(beats.HasBeats);
                Assert.Equal("Scene One", beats.StoryTitle);
                Assert.Null(beats.StoryUuid);

                context.Workspace.SelectedNode = context.Node("scene-two.md");
                await PumpUntilAsync(() => beats.Board.Beats[1].IsCurrent);
                Assert.False(beats.Board.Beats[0].IsCurrent);
                Assert.False(beats.Board.Beats[2].IsCurrent);

                // A beat's scene link opens that file in the editor.
                await Within(beats.OpenSceneCommand.Execute(beats.Board.Beats[0]).ToTask());
                Assert.Equal("scene-one.md", context.Workspace.SelectedNode?.Node.RelativePath);
                await PumpUntilAsync(() => beats.Board.Beats[0].IsCurrent && !beats.Board.Beats[1].IsCurrent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Short_story_without_beats_offers_to_plan_it()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                var beats = context.CreateWriteBeats();
                var window = context.CreateMainWindow(host, beats);
                await PumpUntilAsync(() => beats.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.True(beats.ShowsEmptyState);
                Assert.False(beats.HasBeats);

                await Within(beats.PlanStoryCommand.Execute().ToTask());
                Assert.Equal(ShellMode.Plan, window.ActiveMode);
                Assert.Same(host, window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_follows_the_story_of_the_selected_file()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var lighthouse = context.Node(Lighthouse);
                var part = context.Node(LongPart);
                await SeedAsync(root, lighthouse.Uuid, new Beat("l", "Lamp turns", "", null));
                await SeedAsync(root, part.Uuid,
                    new Beat("x", "Arrive", "", LongSceneA, context.Node(LongSceneA).Uuid),
                    new Beat("y", "Leave", "", LongSceneB, context.Node(LongSceneB).Uuid));

                var beats = context.CreateWriteBeats();
                context.Workspace.SelectedNode = null;
                await PumpUntilAsync(() => !beats.HasStory && beats.Board.BoardKey == null);

                // A scene inside a Part shows the Part's story.
                context.Workspace.SelectedNode = context.Node(LongSceneB);
                await PumpUntilAsync(() => beats.Board.BoardKey == part.Uuid && beats.Board.Beats.Count == 2);
                Assert.Equal("The Long Story", beats.StoryTitle);
                Assert.Equal(part.Uuid, beats.StoryUuid);
                await PumpUntilAsync(() => beats.Board.Beats[1].IsCurrent);
                Assert.False(beats.Board.Beats[0].IsCurrent);

                // Another scene of the same story keeps the board and moves the marker.
                context.Workspace.SelectedNode = context.Node(LongSceneA);
                await PumpUntilAsync(() => beats.Board.Beats[0].IsCurrent && !beats.Board.Beats[1].IsCurrent);
                Assert.Equal(part.Uuid, beats.Board.BoardKey);

                // A single-file story shows its own board.
                context.Workspace.SelectedNode = lighthouse;
                await PumpUntilAsync(() => beats.Board.BoardKey == lighthouse.Uuid && beats.Board.Beats.Count == 1);
                Assert.Equal("Lighthouse", beats.StoryTitle);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_plan_this_story_opens_plan_on_that_story()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                var beats = context.CreateWriteBeats();
                var window = context.CreateMainWindow(host, beats);
                var part = context.Node(LongPart);

                context.Workspace.SelectedNode = context.Node(LongSceneA);
                await PumpUntilAsync(() => beats.Board.BoardKey == part.Uuid);

                await Within(beats.PlanStoryCommand.Execute().ToTask());
                Assert.Equal(ShellMode.Plan, window.ActiveMode);
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);
                Assert.Same(context.Board, host.Content);
                Assert.True(host.ShowBreadcrumb);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edits_in_write_reach_plan_and_edits_in_plan_reach_write()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                await SeedAsync(root, BeatBoardService.ShortStoryBoardKey, new Beat("a", "Arrival", "", null));
                var host = context.CreateHost();
                var beats = context.CreateWriteBeats();
                await PumpUntilAsync(() => beats.Board.Beats.Count == 1 && context.Board.Beats.Count == 1);

                beats.Board.Beats[0].Note = "Written beside the draft";
                Assert.True(await Within(beats.FlushAsync()));
                await PumpUntilAsync(() => context.Board.Beats.Count == 1
                    && context.Board.Beats[0].Note == "Written beside the draft");

                context.Board.Beats[0].Title = "Arrival at dusk";
                Assert.True(await Within(context.Board.FlushAsync()));
                await PumpUntilAsync(() => beats.Board.Beats.Count == 1
                    && beats.Board.Beats[0].Title == "Arrival at dusk");
                Assert.Equal("Written beside the draft", beats.Board.Beats[0].Note);
            });

            var saved = Assert.Single(await LoadFromDiskAsync(root, BeatBoardService.ShortStoryBoardKey));
            Assert.Equal("Arrival at dusk", saved.Title);
            Assert.Equal("Written beside the draft", saved.Note);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Close_flush_writes_pending_write_panel_edits()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                await SeedAsync(root, BeatBoardService.ShortStoryBoardKey, new Beat("a", "Arrival", "", null));
                var host = context.CreateHost();
                var beats = context.CreateWriteBeats();
                var window = context.CreateMainWindow(host, beats);
                await PumpUntilAsync(() => beats.Board.Beats.Count == 1);

                beats.Board.Beats[0].Note = "Typed just before closing";
                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(TimeSpan.FromSeconds(5))));
            });

            Assert.Equal("Typed just before closing",
                Assert.Single(await LoadFromDiskAsync(root, BeatBoardService.ShortStoryBoardKey)).Note);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Toggle_hides_the_panel_and_the_right_rail_counts_it()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                var beats = context.CreateWriteBeats();
                var window = context.CreateMainWindow(host, beats);
                await PumpUntilAsync(() => beats.IsAvailable);

                Assert.True(window.IsBeatsSectionAvailable);
                await PumpUntilAsync(() => window.IsBeatsPanelOpen && window.IsAnyRightPaneOpen);

                await Within(beats.ToggleCommand.Execute().ToTask());
                Assert.False(beats.IsOpen);
                await PumpUntilAsync(() => !window.IsBeatsPanelOpen);
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

internal static class ObservableTestExtensions
{
    public static Task<T> FirstAsyncTask<T>(this IObservable<T> source) =>
        System.Reactive.Linq.Observable.FirstAsync(source).ToTask();
}

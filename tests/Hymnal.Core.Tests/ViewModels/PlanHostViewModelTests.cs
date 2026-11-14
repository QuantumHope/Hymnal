using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;
using Hymnal.Infrastructure;
using ReactiveUI;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class PlanHostViewModelTests
{
    static PlanHostViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    // Anthology layout: one single-file story, then a long story told as a Part with two scenes.
    private const string Lighthouse = "lighthouse.md";
    private const string LongPart = "long-story/part.md";
    private const string LongSceneA = "long-story/scene-a.md";
    private const string LongSceneB = "long-story/scene-b.md";

    [Fact]
    public async Task Novel_plan_content_is_corkboard()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                Assert.False(host.ShowFilesStrip);
                Assert.Null(context.Board.BoardKey);

                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                Assert.Same(context.Corkboard, window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_plan_opens_story_board()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.Same(context.Board, host.Content);
                Assert.Equal("The Keeper", context.Board.BoardTitle);
                Assert.Equal([new SceneOption("the-keeper.md", "The Keeper")], context.Board.SceneOptions);
                Assert.False(host.ShowBreadcrumb);
                Assert.False(host.ShowFilesStrip);

                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                Assert.Same(host, window.SecondaryCentreContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_with_two_files_shows_files_strip()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.Same(context.Board, host.Content);
                Assert.True(host.ShowFilesStrip);
                Assert.False(host.IsFilesStripExpanded);
                Assert.Same(context.Corkboard, host.Corkboard);
                Assert.Equal("Scene One", context.Board.BoardTitle);
                Assert.Equal(["scene-one.md", "scene-two.md"], context.Board.SceneOptions.Select(o => o.RelativePath));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_beats_request_drills_in_and_back_returns()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();
                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                Assert.Equal("Collection", host.BreadcrumbParent);
                Assert.False(await host.BackToCollectionCommand.CanExecute.FirstAsync());

                var story = context.Node(Lighthouse);
                var card = await context.CardAsync(Lighthouse);
                await Within(context.Corkboard.OpenCardCommand.Execute(card).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == story.Uuid);

                Assert.Same(context.Board, host.Content);
                Assert.True(host.ShowBreadcrumb);
                Assert.False(host.ShowFilesStrip);
                Assert.Equal("Lighthouse", context.Board.BoardTitle);
                Assert.Equal([new SceneOption(Lighthouse, "Lighthouse")], context.Board.SceneOptions);

                // An edit still waiting on its debounce is written to this story's board on the way out.
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Title = "Storm arrives";

                await Within(host.BackToCollectionCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == null);

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                var saved = await new BeatBoardService(new MetadataStore()).LoadAsync(root, story.Uuid);
                Assert.True(saved.IsSuccess, saved.Error);
                Assert.Equal("Storm arrives", Assert.Single(saved.Value!).Title);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_corkboard_speaks_of_stories_and_scenes_not_chapters()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                context.CreateHost();
                await context.SettleAsync();
                var board = context.Corkboard;

                var divider = await context.PartDividerAsync(LongPart);
                Assert.Equal("Scene", divider.Noun);
                Assert.Equal("(2 scenes)", divider.ChapterCountInParens);
                Assert.Equal("Add a scene to The Long Story", divider.AddTip);
                Assert.Equal("Story", (await context.CardAsync(Lighthouse)).Noun);
                Assert.Equal("Scene", (await context.CardAsync(LongSceneA)).Noun);

                Assert.Equal("Add a story", board.AddButtonTip);
                Assert.Equal("Double-click a story to plan its beats", board.OpenCardHint);
                Assert.DoesNotContain("chapter", board.EmptyBoardText, StringComparison.OrdinalIgnoreCase);

                board.BeginInlineCreate(0, null);
                var create = board.Items.OfType<InlineCreateItemViewModel>().Single();
                Assert.Equal("NEW STORY", create.Heading);
                Assert.Equal("Story title...", create.TitlePlaceholder);
                board.CancelInlineCreate();
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_corkboard_keeps_chapter_wording()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                await context.SettleAsync();
                Assert.Equal("Chapter", (await context.CardAsync("chapter-one.md")).Noun);
                Assert.Equal("Add chapter or part", context.Corkboard.AddButtonTip);
                Assert.Equal("Double-click a card to open in Write mode", context.Corkboard.OpenCardHint);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_part_story_scope_is_its_chapters()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();

                var part = context.Node(LongPart);
                var divider = await context.PartDividerAsync(LongPart);
                await Within(context.Corkboard.ShowBeatsCommand.Execute(divider).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);

                Assert.Same(context.Board, host.Content);
                Assert.Equal("The Long Story", context.Board.BoardTitle);
                Assert.Equal([LongSceneA, LongSceneB], context.Board.SceneOptions.Select(o => o.RelativePath));

                // A scene's Beats action opens the board of the story it belongs to.
                await Within(host.BackToCollectionCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == null);
                var scene = await context.CardAsync(LongSceneB);
                await Within(context.Corkboard.ShowBeatsCommand.Execute(scene).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_double_click_requests_beats_not_editor()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var beats = new List<ChapterViewModel>();
                var opened = new List<ChapterViewModel>();
                using var beatsSub = context.Corkboard.BeatsRequested.Subscribe(beats.Add);
                using var openSub = context.Corkboard.OpenChapterRequested.Subscribe(opened.Add);
                await context.SettleAsync();
                Assert.True(context.Corkboard.IsAnthology);

                // Story card and Part divider: double-click drills into beats.
                await Within(context.Corkboard.OpenCardCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                await Within(context.Corkboard.OpenCardCommand.Execute(await context.PartDividerAsync(LongPart)).ToTask());
                Assert.Equal([Lighthouse, LongPart], beats.Select(b => b.Node.RelativePath));
                Assert.Empty(opened);

                // A scene inside a long story opens in the editor; Open always does.
                await Within(context.Corkboard.OpenCardCommand.Execute(await context.CardAsync(LongSceneA)).ToTask());
                await Within(context.Corkboard.OpenInEditorCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                Assert.Equal([LongSceneA, Lighthouse], opened.Select(o => o.Node.RelativePath));
                Assert.Equal(2, beats.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_double_click_still_opens_editor()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var beats = new List<ChapterViewModel>();
                var opened = new List<ChapterViewModel>();
                using var beatsSub = context.Corkboard.BeatsRequested.Subscribe(beats.Add);
                using var openSub = context.Corkboard.OpenChapterRequested.Subscribe(opened.Add);
                await context.SettleAsync();
                Assert.False(context.Corkboard.IsAnthology);

                var card = await context.CardAsync("chapter-one.md");
                await Within(context.Corkboard.OpenCardCommand.Execute(card).ToTask());
                await Within(context.Corkboard.ShowBeatsCommand.Execute(card).ToTask());
                await Within(context.Corkboard.SelectCardCommand.Execute(card).ToTask());
                await Within(context.Corkboard.OpenSelectedCardCommand.Execute().ToTask());

                Assert.Equal(["chapter-one.md", "chapter-one.md"], opened.Select(o => o.Node.RelativePath));
                Assert.Empty(beats);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_to_novel_returns_plan_to_corkboard_and_closes_board()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.Same(host, window.SecondaryCentreContent);

                var story = context.Node(Lighthouse);
                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == story.Uuid);

                var changed = 0;
                window.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SecondaryCentreContent))
                        changed++;
                };

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Novel));
                Assert.True(save.IsSuccess, save.Error);
                var reload = await Within(context.Workspace.ReloadProjectAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await PumpUntilAsync(() => context.Board.BoardKey == null);

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
                Assert.Same(context.Corkboard, window.SecondaryCentreContent);
                Assert.True(changed > 0);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_story_to_anthology_closes_story_board()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Anthology));
                Assert.True(save.IsSuccess, save.Error);
                await Within(context.Workspace.ReloadProjectAsync());
                await PumpUntilAsync(() => context.Board.BoardKey == null);

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowBreadcrumb);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Closing_workspace_closes_board_and_shows_corkboard()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                await Within(context.Workspace.CloseWorkspaceCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == null && ReferenceEquals(host.Content, context.Corkboard));

                Assert.Same(context.Corkboard, host.Content);
                Assert.False(host.ShowFilesStrip);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Close_flush_writes_pending_beat_edits()
    {
        var root = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var host = context.CreateHost();
                var window = context.CreateMainWindow(host);
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Note = "Typed just before closing";

                Assert.True(await Within(window.FlushPendingEditsBeforeCloseAsync(TimeSpan.FromSeconds(5))));
            });

            var saved = await new BeatBoardService(new MetadataStore()).LoadAsync(root, BeatBoardService.ShortStoryBoardKey);
            Assert.True(saved.IsSuccess, saved.Error);
            Assert.Equal("Typed just before closing", Assert.Single(saved.Value!).Note);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_files_strip_content_is_corkboard_only_while_shown()
    {
        var single = CreateWorkspace(StoryJson, ("the-keeper.md", "# The Keeper\n\nShe climbs.\n"));
        var two = CreateWorkspace(StoryJson, ("scene-one.md", "# Scene One\n\nDusk.\n"), ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(single, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);
                Assert.False(host.ShowFilesStrip);
                Assert.Null(host.FilesStripContent);
            });
            await Ui(two, 2, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => host.ShowFilesStrip);
                Assert.Same(context.Corkboard, host.FilesStripContent);
            });
        }
        finally
        {
            DeleteFolder(single);
            DeleteFolder(two);
        }
    }

    [Fact]
    public async Task Anthology_and_novel_have_no_files_strip_content()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var host = context.CreateHost();
                await context.SettleAsync();
                Assert.Same(context.Corkboard, host.Content);
                Assert.Null(host.FilesStripContent);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_between_same_shape_story_workspaces_reopens_the_board_for_the_new_one()
    {
        var a = CreateWorkspace(StoryJson, ("story.md", "# Untitled\n\nA.\n"));
        var b = CreateWorkspace(StoryJson, ("story.md", "# Untitled\n\nB.\n"));
        try
        {
            await Ui(a, 1, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var closed = false;
                using var watch = context.Board.WhenAnyValue(x => x.BoardKey).Subscribe(key => closed |= key == null);
                await context.OpenWorkspaceInCurrentDispatchAsync(b);
                await PumpUntilAsync(() => context.Workspace.WorkspaceRoot == b && context.Workspace.Nodes.Count == 1);
                await PumpUntilAsync(() => closed && context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.True(context.Board.CanEdit);
                Assert.Same(context.Board, host.Content);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Title = "B's beat";
                Assert.True(await Within(context.Board.FlushAsync()));
            });

            Assert.Equal("B's beat", Assert.Single(await LoadFromDiskAsync(b, BeatBoardService.ShortStoryBoardKey)).Title);
            Assert.False(File.Exists(BeatBoardService.BoardFilePath(a, BeatBoardService.ShortStoryBoardKey)));
        }
        finally
        {
            DeleteFolder(a);
            DeleteFolder(b);
        }
    }

    [Fact]
    public async Task Switching_workspace_with_pending_edits_writes_them_to_the_first_workspace()
    {
        var a = CreateWorkspace(StoryJson, ("story.md", "# Untitled\n\nA.\n"));
        var b = CreateWorkspace(StoryJson, ("story.md", "# Untitled\n\nB.\n"));
        try
        {
            await Ui(a, 1, async context =>
            {
                context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Note = "Typed in A, still debouncing";

                var closed = false;
                using var watch = context.Board.WhenAnyValue(x => x.BoardKey).Subscribe(key => closed |= key == null);
                await context.OpenWorkspaceInCurrentDispatchAsync(b);
                await PumpUntilAsync(() => closed && context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.Empty(context.Board.Beats);
                Assert.True(await Within(context.Board.FlushAsync()));
            });

            Assert.Equal("Typed in A, still debouncing",
                Assert.Single(await LoadFromDiskAsync(a, BeatBoardService.ShortStoryBoardKey)).Note);
            Assert.False(File.Exists(BeatBoardService.BoardFilePath(b, BeatBoardService.ShortStoryBoardKey)));
        }
        finally
        {
            DeleteFolder(a);
            DeleteFolder(b);
        }
    }

    [Fact]
    public async Task Anthology_switching_a_to_b_to_a_keeps_each_story_s_edits_on_its_board()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                context.CreateHost();
                await context.SettleAsync();
                var lighthouse = context.Node(Lighthouse);
                var part = context.Node(LongPart);
                var lighthouseCard = await context.CardAsync(Lighthouse);
                var divider = await context.PartDividerAsync(LongPart);

                await Within(context.Corkboard.ShowBeatsCommand.Execute(lighthouseCard).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == lighthouse.Uuid);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Title = "Lamp fails";

                // Away to the long story and straight back, without waiting for either to open.
                await Within(context.Corkboard.ShowBeatsCommand.Execute(divider).ToTask());
                await Within(context.Corkboard.ShowBeatsCommand.Execute(lighthouseCard).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == lighthouse.Uuid && context.Board.Beats.Count == 1);
                Assert.Equal("Lamp fails", context.Board.Beats[0].Title);

                await Within(context.Corkboard.ShowBeatsCommand.Execute(divider).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);
                Assert.Empty(context.Board.Beats);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                context.Board.Beats[0].Title = "They meet";

                await Within(context.Corkboard.ShowBeatsCommand.Execute(lighthouseCard).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == lighthouse.Uuid && context.Board.Beats.Count == 1);
                Assert.Equal("Lamp fails", context.Board.Beats[0].Title);

                Assert.Equal("Lamp fails", Assert.Single(await LoadFromDiskAsync(root, lighthouse.Uuid)).Title);
                Assert.Equal("They meet", Assert.Single(await LoadFromDiskAsync(root, part.Uuid)).Title);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Reordering_scenes_updates_the_board_in_place_and_keeps_the_selection()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                var selected = context.Board.Beats[1];
                Assert.Same(selected, context.Board.Selected);

                var reopened = false;
                using var watch = context.Board.WhenAnyValue(x => x.BoardKey).Skip(1).Subscribe(_ => reopened = true);
                await File.WriteAllTextAsync(Path.Combine(root, "Book.txt"), "scene-two.md\nscene-one.md\n");
                var reload = await Within(context.Workspace.ReloadProjectAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await PumpUntilAsync(() => context.Board.BoardTitle == "Scene Two");

                Assert.Equal(["scene-two.md", "scene-one.md"], context.Board.SceneOptions.Select(o => o.RelativePath));
                Assert.False(reopened);
                Assert.Same(selected, context.Board.Selected);
                Assert.Equal(2, context.Board.Beats.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Excluded_scene_is_not_in_the_story_scope()
    {
        var root = CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Book.txt"), "scene-one.md\n");
            var excluded = await new ExclusionManifestService(new MetadataStore())
                .SaveAsync(root, new ExclusionManifest { ExcludedPaths = ["scene-two.md"] });
            Assert.True(excluded.IsSuccess, excluded.Error);

            await Ui(root, 2, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                Assert.Contains(context.Workspace.Nodes, n => n.Node.RelativePath == "scene-two.md" && n.Node.IsExcluded);
                Assert.Equal(["scene-one.md"], context.Board.SceneOptions.Select(o => o.RelativePath));
                Assert.False(host.ShowFilesStrip);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_that_becomes_a_part_keeps_its_beats_on_the_part_board()
    {
        var root = CreateWorkspace(AnthologyJson, (Lighthouse, "# Lighthouse\n\nThe lamp turns.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                context.CreateHost();
                await context.SettleAsync();
                var story = context.Node(Lighthouse);
                await SeedAsync(root, story.Uuid, new Beat("b-1", "Lamp fails", "", Lighthouse, story.Uuid));

                await WritePartBeforeAsync(root, "lighthouse/part.md", "# The Lighthouse Cycle", Lighthouse);
                var reload = await Within(context.Workspace.ReloadProjectAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await PumpUntilAsync(() => context.Workspace.Nodes.Count == 2);
                Assert.Equal(story.Uuid, context.Node(Lighthouse).Uuid);

                var part = context.Node("lighthouse/part.md");
                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.PartDividerAsync("lighthouse/part.md")).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);

                var beat = Assert.Single(context.Board.Beats);
                Assert.Equal("Lamp fails", beat.Title);
                Assert.False(beat.IsSceneMissing);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, story.Uuid)));
                Assert.Equal("Lamp fails", Assert.Single(await LoadFromDiskAsync(root, part.Uuid)).Title);
                Assert.Equal(["Moved beats from 'Lighthouse' to the story 'The Lighthouse Cycle'."], context.Infos);

                // Back and in again: nothing more to move, no second notice.
                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid && context.Board.Beats.Count == 1);
                Assert.Single(context.Infos);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Part_inserted_before_several_stories_adopts_only_the_first_scene_s_board()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("first.md", "# First\n\nOne.\n"),
            ("second.md", "# Second\n\nTwo.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                context.CreateHost();
                await context.SettleAsync();
                var first = context.Node("first.md");
                var second = context.Node("second.md");
                await SeedAsync(root, first.Uuid, new Beat("f", "First beat", "", null));
                await SeedAsync(root, second.Uuid, new Beat("s", "Second beat", "", null));

                await WritePartBeforeAsync(root, "part.md", "# Collected", "first.md", "second.md");
                await Within(context.Workspace.ReloadProjectAsync());
                await PumpUntilAsync(() => context.Workspace.Nodes.Count == 3);

                var part = context.Node("part.md");
                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.PartDividerAsync("part.md")).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid && context.Board.Beats.Count == 1);

                Assert.Equal("First beat", context.Board.Beats[0].Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, first.Uuid)));
                Assert.Equal("Second beat", Assert.Single(await LoadFromDiskAsync(root, second.Uuid)).Title);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Part_with_its_own_board_does_not_adopt_a_scene_board()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                context.CreateHost();
                await context.SettleAsync();
                var part = context.Node(LongPart);
                var scene = context.Node(LongSceneA);
                await SeedAsync(root, part.Uuid, new Beat("p", "Part beat", "", null));
                await SeedAsync(root, scene.Uuid, new Beat("s", "Scene beat", "", null));

                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.PartDividerAsync(LongPart)).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid && context.Board.Beats.Count == 1);

                Assert.Equal("Part beat", context.Board.Beats[0].Title);
                Assert.Equal("Scene beat", Assert.Single(await LoadFromDiskAsync(root, scene.Uuid)).Title);
                Assert.Empty(context.Infos);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Renaming_a_linked_scene_keeps_the_link_and_saves_the_new_path()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                context.CreateHost();
                await context.SettleAsync();
                var part = context.Node(LongPart);
                var scene = context.Node(LongSceneA);

                await Within(context.Corkboard.ShowBeatsCommand.Execute(await context.PartDividerAsync(LongPart)).ToTask());
                await PumpUntilAsync(() => context.Board.BoardKey == part.Uuid);
                await Within(context.Board.AddBeatCommand.Execute().ToTask());
                var beat = context.Board.Beats[0];
                await Within(context.Board.LinkSceneCommand.Execute((beat.Id, LongSceneA)).ToTask());
                Assert.Equal(scene.Uuid, Assert.Single(await LoadFromDiskAsync(root, part.Uuid)).SceneId);

                var rename = context.Workspace.RenameChapterCommand
                    .Execute(new RenameChapterRequest(LongSceneA, "Scene Alpha")).ToTask();
                await PumpUntilAsync(() => rename.IsCompleted);
                await rename;
                Assert.Empty(context.Errors);

                const string renamed = "long-story/scene-alpha.md";
                await PumpUntilAsync(() => context.Board.Beats.Count == 1 && context.Board.Beats[0].ScenePath == renamed);
                beat = context.Board.Beats[0];
                Assert.False(beat.IsSceneMissing);
                Assert.Equal(scene.Uuid, beat.SceneId);
                await PumpUntilAsync(() => context.Board.SceneOptions.Any(o => o.RelativePath == renamed));
                Assert.Equal("Scene Alpha", context.Board.SceneOptions.Single(o => o.RelativePath == renamed).Title);

                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                Assert.True(await Within(context.Board.FlushAsync()));
                var saved = Assert.Single(await LoadFromDiskAsync(root, part.Uuid));
                Assert.Equal(renamed, saved.ScenePath);
                Assert.Equal(scene.Uuid, saved.SceneId);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_enter_on_a_story_card_requests_beats_like_double_click()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 4, async context =>
            {
                var beats = new List<ChapterViewModel>();
                var opened = new List<ChapterViewModel>();
                using var beatsSub = context.Corkboard.BeatsRequested.Subscribe(beats.Add);
                using var openSub = context.Corkboard.OpenChapterRequested.Subscribe(opened.Add);
                await context.SettleAsync();

                await Within(context.Corkboard.SelectCardCommand.Execute(await context.CardAsync(Lighthouse)).ToTask());
                await Within(context.Corkboard.OpenSelectedCardCommand.Execute().ToTask());
                Assert.Equal([Lighthouse], beats.Select(b => b.Node.RelativePath));
                Assert.Empty(opened);

                // A scene inside a long story opens in the editor, as its double-click does.
                await Within(context.Corkboard.SelectCardCommand.Execute(await context.CardAsync(LongSceneA)).ToTask());
                await Within(context.Corkboard.OpenSelectedCardCommand.Execute().ToTask());
                Assert.Equal([LongSceneA], opened.Select(o => o.Node.RelativePath));
                Assert.Single(beats);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Beats_action_is_offered_for_stories_in_the_book_only()
    {
        var root = CreateAnthology();
        try
        {
            File.WriteAllText(Path.Combine(root, "cut-story.md"), "# Cut Story\n\nGone.\n");
            var excluded = await new ExclusionManifestService(new MetadataStore())
                .SaveAsync(root, new ExclusionManifest { ExcludedPaths = ["cut-story.md"] });
            Assert.True(excluded.IsSuccess, excluded.Error);

            await Ui(root, 5, async context =>
            {
                await context.SettleAsync();
                context.Corkboard.ShowExcludedFiles = true;
                await PumpUntilAsync(() => context.Corkboard.Items.OfType<ExcludedChapterCardItemViewModel>().Any());

                Assert.True((await context.CardAsync(Lighthouse)).ShowsBeatsAction);
                Assert.True((await context.CardAsync(LongSceneA)).ShowsBeatsAction);
                Assert.True((await context.PartDividerAsync(LongPart)).ShowsBeatsAction);
                Assert.All(context.Corkboard.Items.OfType<ExcludedChapterCardItemViewModel>(), cut =>
                {
                    Assert.False(cut.ShowsBeatsAction);
                    Assert.False(context.Corkboard.CanShowBeats(cut));
                });
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_offers_no_beats_action()
    {
        var root = CreateWorkspace(NovelJson, ("chapter-one.md", "# Chapter One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                await context.SettleAsync();
                var card = await context.CardAsync("chapter-one.md");
                Assert.False(card.ShowsBeatsAction);
                Assert.False(context.Corkboard.CanShowBeats(card));
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CreateAnthology() =>
        CreateWorkspace(AnthologyJson,
            (Lighthouse, "# Lighthouse\n\nThe lamp turns.\n"),
            (LongPart, "{class: part}\n# The Long Story\n"),
            (LongSceneA, "# Scene A\n\nArrival.\n"),
            (LongSceneB, "# Scene B\n\nDeparture.\n"));

    /// <summary>Rewrites Book.txt with a new Part (at <paramref name="partPath"/>) listed before <paramref name="stories"/>.</summary>
    private static async Task WritePartBeforeAsync(string root, string partPath, string heading, params string[] stories)
    {
        var full = Path.Combine(root, partPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "{class: part}\n" + heading + "\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Book.txt"), string.Join("\n", [partPath, .. stories]) + "\n");
    }
}

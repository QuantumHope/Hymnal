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
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

[Collection("AvaloniaUi")]
public sealed class BeatBoardViewModelTests
{
    static BeatBoardViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    private const string BoardA = "story";
    private const string BoardB = "other-board";
    private const string SceneOne = "scene-one.md";
    private const string SceneTwo = "scene-two.md";
    private const string SceneOneText = "# Scene One\n\nThe keeper climbs the stairs at dusk.\n";

    private static readonly IReadOnlyList<string> Scope = [SceneOne, SceneTwo];

    private static Beat B(string id, string title, string note = "", string? scene = null) => new(id, title, note, scene);

    [Fact]
    public async Task Open_empty_board_is_empty_and_lists_templates()
    {
        var root = CreateWorkspace();
        try
        {
            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));

                Assert.Equal(BoardA, vm.BoardKey);
                Assert.Equal("The Story", vm.BoardTitle);
                Assert.True(vm.IsEmpty);
                Assert.Empty(vm.Beats);
                Assert.False(vm.IsReadOnly);
                Assert.Same(BeatTemplateLoader.All, vm.Templates);
                Assert.Equal(
                    [new SceneOption(SceneOne, "Scene One"), new SceneOption(SceneTwo, "Scene Two")],
                    vm.SceneOptions);
                Assert.True(await vm.AddBeatCommand.CanExecute.FirstAsync());
            });

            Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, BoardA)));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Apply_template_to_empty_board_creates_beats_and_saves()
    {
        var root = CreateWorkspace();
        try
        {
            var expected = new[] { "Hook", "Inciting Incident", "Rising Complications", "Climax", "Resolution" };

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var prompts = new List<string>();
                vm.ConfirmAsync = request => { prompts.Add(request.Message); return Task.FromResult(false); };

                await Within(vm.ApplyTemplateCommand.Execute(new ApplyTemplateRequest("threeAct", TemplateApplyMode.Replace, null)).ToTask());

                Assert.Empty(prompts); // nothing to replace, nothing to confirm
                Assert.Equal(expected, vm.Beats.Select(b => b.Title));
                Assert.False(vm.IsEmpty);
                Assert.All(vm.Beats, b => Assert.False(string.IsNullOrWhiteSpace(b.Note)));
            });

            Assert.Equal(expected, (await LoadFromDiskAsync(root, BoardA)).Select(b => b.Title));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Apply_mice_with_thread()
    {
        var root = CreateWorkspace();
        try
        {
            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));

                // MICE needs a thread; without one nothing is applied.
                await Within(vm.ApplyTemplateCommand.Execute(new ApplyTemplateRequest("mice", TemplateApplyMode.Append, null)).ToTask());
                Assert.Empty(vm.Beats);
                Assert.NotEmpty(context.Notifications.Errors);

                await Within(vm.ApplyTemplateCommand.Execute(new ApplyTemplateRequest("mice", TemplateApplyMode.Append, "Inquiry")).ToTask());
                Assert.Equal(["Open: Inquiry", "Close: Inquiry"], vm.Beats.Select(b => b.Title));
            });

            Assert.Equal(["Open: Inquiry", "Close: Inquiry"], (await LoadFromDiskAsync(root, BoardA)).Select(b => b.Title));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Replace_requires_confirmation()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Mine"), B("b", "Also mine"));
            var before = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, BoardA));
            var request = new ApplyTemplateRequest("kishotenketsu", TemplateApplyMode.Replace, null);

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var prompts = new List<string>();

                vm.ConfirmAsync = request => { prompts.Add(request.Message); return Task.FromResult(false); };
                await Within(vm.ApplyTemplateCommand.Execute(request).ToTask());
                Assert.Single(prompts);
                Assert.Equal(["Mine", "Also mine"], vm.Beats.Select(b => b.Title));
                Assert.Equal(0, context.Boards.SaveCount);
                Assert.Equal(before, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, BoardA)));

                vm.ConfirmAsync = _ => Task.FromResult(true);
                await Within(vm.ApplyTemplateCommand.Execute(request).ToTask());
                Assert.Equal(["Introduction", "Development", "Twist", "Reconciliation"], vm.Beats.Select(b => b.Title));
            });

            Assert.Equal(["Introduction", "Development", "Twist", "Reconciliation"],
                (await LoadFromDiskAsync(root, BoardA)).Select(b => b.Title));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Append_keeps_existing()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Opening image"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var prompts = new List<string>();
                vm.ConfirmAsync = request => { prompts.Add(request.Message); return Task.FromResult(false); };

                await Within(vm.ApplyTemplateCommand.Execute(new ApplyTemplateRequest("kishotenketsu", TemplateApplyMode.Append, null)).ToTask());

                Assert.Empty(prompts);
                Assert.Equal(["Opening image", "Introduction", "Development", "Twist", "Reconciliation"],
                    vm.Beats.Select(b => b.Title));
            });

            var saved = await LoadFromDiskAsync(root, BoardA);
            Assert.Equal(["Opening image", "Introduction", "Development", "Twist", "Reconciliation"], saved.Select(b => b.Title));
            Assert.Equal("a", saved[0].Id);
            Assert.Equal(saved.Count, saved.Select(b => b.Id).Distinct().Count());
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edits_debounce_into_one_save()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Draft"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var card = vm.Beats.Single();

                card.Title = "First";
                card.Title = "Second";
                card.Note = "A note";
                card.Title = "Third";
                context.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(200));
                Assert.Equal(0, context.Boards.SaveCount);

                // The throttle itself starts the write; no explicit flush.
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                Assert.Equal(1, context.Boards.SaveCount);
                var saved = Assert.Single(context.Boards.LastSaved!);
                Assert.Equal("Third", saved.Title);
                Assert.Equal("A note", saved.Note);
                Assert.Equal(BoardA, context.Boards.LastSavedKey);

                Assert.True(await Within(vm.FlushAsync())); // let the write finish
                Assert.Equal(1, context.Boards.SaveCount);
            });

            var onDisk = Assert.Single(await LoadFromDiskAsync(root, BoardA));
            Assert.Equal("Third", onDisk.Title);
            Assert.Equal("A note", onDisk.Note);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_board_flushes_to_previous_key()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Board A beat"));
            var pathB = BeatBoardService.BoardFilePath(root, BoardB);

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                vm.Beats.Single().Title = "Edited on A";

                await Within(vm.OpenBoardAsync(BoardB, "Story B", Scope));

                Assert.Equal(BoardB, vm.BoardKey);
                Assert.Empty(vm.Beats);
                Assert.Equal(1, context.Boards.SaveCount);
                Assert.Equal(BoardA, context.Boards.LastSavedKey);

                // The debounce window passing afterwards writes nothing to either board.
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                Assert.True(await Within(vm.FlushAsync()));
                Assert.Equal(1, context.Boards.SaveCount);
            });

            Assert.Equal("Edited on A", Assert.Single(await LoadFromDiskAsync(root, BoardA)).Title);
            Assert.False(File.Exists(pathB));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edits_made_while_next_board_loads_go_to_the_board_they_were_made_on()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Board A beat"));
            await SeedAsync(root, BoardB, B("b", "Board B beat"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var cardA = vm.Beats.Single();

                var hold = context.Boards.HoldNextLoad(BoardB);
                var opening = vm.OpenBoardAsync(BoardB, "Story B", Scope);
                await PumpUntilAsync(() => context.Boards.IsHeld(BoardB));

                // Board A is still shown while B is read; this edit belongs to A.
                cardA.Title = "Late edit on A";
                hold.SetResult();
                await Within(opening);

                Assert.Equal(BoardB, vm.BoardKey);
                Assert.Equal(["Board B beat"], vm.Beats.Select(b => b.Title));
                Assert.Equal([BoardA], context.Boards.SavedKeys);
            });

            Assert.Equal("Late edit on A", Assert.Single(await LoadFromDiskAsync(root, BoardA)).Title);
            Assert.Equal("Board B beat", Assert.Single(await LoadFromDiskAsync(root, BoardB)).Title);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Add_beat_inserts_after_selected_and_selects_it()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "One"), B("b", "Two"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));

                vm.Selected = vm.Beats[0];
                await Within(vm.AddBeatCommand.Execute().ToTask());
                Assert.Equal(3, vm.Beats.Count);
                Assert.Same(vm.Beats[1], vm.Selected);

                vm.Selected = null;
                await Within(vm.AddBeatCommand.Execute().ToTask());
                Assert.Same(vm.Beats[3], vm.Selected);
            });

            var saved = await LoadFromDiskAsync(root, BoardA);
            Assert.Equal(4, saved.Count);
            Assert.Equal("a", saved[0].Id);
            Assert.Equal("b", saved[2].Id);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Move_beat_reorders_and_saves()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "One"), B("b", "Two"), B("c", "Three"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var moved = vm.Beats[0];

                await Within(vm.MoveBeatCommand.Execute((0, 2)).ToTask());

                Assert.Equal(["Two", "Three", "One"], vm.Beats.Select(b => b.Title));
                Assert.Same(moved, vm.Beats[2]);
            });

            Assert.Equal(["b", "c", "a"], (await LoadFromDiskAsync(root, BoardA)).Select(b => b.Id));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Keep or toss"), B("b", "Stays"));
            var prompts = new List<string>();

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                vm.Selected = vm.Beats[0];

                vm.ConfirmAsync = request => { prompts.Add(request.Message); return Task.FromResult(false); };
                await Within(vm.DeleteSelectedCommand.Execute().ToTask());
                Assert.Equal(2, vm.Beats.Count);
                Assert.Equal(0, context.Boards.SaveCount);

                vm.ConfirmAsync = _ => Task.FromResult(true);
                await Within(vm.DeleteSelectedCommand.Execute().ToTask());
                Assert.Equal(["Stays"], vm.Beats.Select(b => b.Title));
                Assert.Null(vm.Selected);
                Assert.False(await vm.DeleteSelectedCommand.CanExecute.FirstAsync());
            });

            Assert.Contains("Keep or toss", Assert.Single(prompts));
            Assert.Equal(["b"], (await LoadFromDiskAsync(root, BoardA)).Select(b => b.Id));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Link_scene_shows_word_count_and_status_label()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Hook"));
            var expectedCount = new WordCountService().CountWords(SceneOneText);

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var card = vm.Beats.Single();
                Assert.Null(card.ScenePath);
                Assert.Null(card.SceneWordCount);
                Assert.False(card.IsSceneMissing);

                await Within(vm.LinkSceneCommand.Execute(("a", (string?)SceneOne)).ToTask());

                var scene = context.Workspace.Nodes.Single(n => n.Node.RelativePath == SceneOne);
                await PumpUntilAsync(() => scene.WordCountKnown);
                Assert.Equal(SceneOne, card.ScenePath);
                Assert.Equal("Scene One", card.SceneTitle);
                Assert.Equal(expectedCount, card.SceneWordCount);
                Assert.Equal(scene.StatusLabel, card.SceneStatusLabel);
                Assert.False(card.IsSceneMissing);

                // Live updates from the linked chapter.
                scene.UpdateWordCount(1234);
                Assert.Equal(1234, card.SceneWordCount);

                var plannedLabel = card.SceneStatusLabel;
                await Within(scene.ChangeStatusCommand.Execute(ChapterStatus.Drafting).ToTask());
                Assert.NotEqual(plannedLabel, scene.StatusLabel);
                Assert.Equal(scene.StatusLabel, card.SceneStatusLabel);
            });

            Assert.Equal(SceneOne, Assert.Single(await LoadFromDiskAsync(root, BoardA)).ScenePath);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Link_scene_outside_scope_is_refused()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Hook"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA, [SceneTwo]));

                await Within(vm.LinkSceneCommand.Execute(("a", (string?)SceneOne)).ToTask());

                Assert.Null(vm.Beats.Single().ScenePath);
                Assert.Equal(0, context.Boards.SaveCount);
                Assert.NotEmpty(context.Notifications.Errors);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Unlink_scene_clears_link()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Hook", scene: SceneOne));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var card = vm.Beats.Single();
                Assert.Equal("Scene One", card.SceneTitle);

                await Within(vm.LinkSceneCommand.Execute(("a", (string?)null)).ToTask());

                Assert.Null(card.ScenePath);
                Assert.Null(card.SceneTitle);
                Assert.Null(card.SceneWordCount);
                Assert.Null(card.SceneStatusLabel);
                Assert.False(card.IsSceneMissing);
            });

            Assert.Null(Assert.Single(await LoadFromDiskAsync(root, BoardA)).ScenePath);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Linked_scene_removed_from_book_shows_missing()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Hook", scene: SceneOne), B("b", "Climax", scene: SceneTwo));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var hook = vm.Beats[0];
                Assert.False(hook.IsSceneMissing);

                using (context.Workspace.SuppressFileWatcher())
                    await File.WriteAllTextAsync(Path.Combine(root, "Book.txt"), SceneTwo + "\n");
                var reload = await Within(context.Workspace.ReloadCurrentWorkspaceAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await context.AwaitHydrationAsync();

                await PumpUntilAsync(() => hook.IsSceneMissing);
                Assert.Equal(2, vm.Beats.Count);
                Assert.Equal(SceneOne, hook.ScenePath);
                Assert.Null(hook.SceneWordCount);
                Assert.Null(hook.SceneStatusLabel);
                Assert.False(vm.Beats[1].IsSceneMissing);
                Assert.Empty(context.Notifications.Errors);

                await Within(vm.LinkSceneCommand.Execute(("a", (string?)null)).ToTask());
                Assert.Null(hook.ScenePath);
                Assert.False(hook.IsSceneMissing);
            });

            Assert.Null((await LoadFromDiskAsync(root, BoardA)).Single(b => b.Id == "a").ScenePath);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Malformed_board_is_read_only_and_never_written()
    {
        var root = CreateWorkspace();
        var path = BeatBoardService.BoardFilePath(root, BoardA);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not json");
        var before = await File.ReadAllBytesAsync(path);
        try
        {
            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));

                Assert.True(vm.IsReadOnly);
                Assert.Equal(BoardA, vm.BoardKey);
                Assert.Empty(vm.Beats);
                Assert.NotEmpty(context.Notifications.Infos);
                Assert.False(vm.CanEdit);
                Assert.False(await vm.AddBeatCommand.CanExecute.FirstAsync());
                Assert.False(await vm.DeleteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(await vm.MoveBeatCommand.CanExecute.FirstAsync());
                Assert.False(await vm.ApplyTemplateCommand.CanExecute.FirstAsync());
                Assert.False(await vm.LinkSceneCommand.CanExecute.FirstAsync());

                vm.ConfirmAsync = _ => Task.FromResult(true);
                await Within(vm.AddBeatAsync());
                await Within(vm.ApplyTemplateAsync(new ApplyTemplateRequest("threeAct", TemplateApplyMode.Replace, null)));
                Assert.Empty(vm.Beats);
                Assert.True(await Within(vm.FlushAsync()));
                await Within(vm.CloseBoardAsync());
                Assert.Equal(0, context.Boards.SaveCount);
            });

            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Workspace_close_flushes_and_clears()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Draft"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                vm.Beats.Single().Note = "Written just before closing";

                await Within(context.Workspace.CloseWorkspaceCommand.Execute().ToTask());
                await PumpUntilAsync(() => vm.BoardKey == null);

                Assert.Equal(1, context.Boards.SaveCount);
                Assert.Equal(BoardA, context.Boards.LastSavedKey);
                Assert.Equal("Written just before closing", Assert.Single(context.Boards.LastSaved!).Note);
                Assert.Empty(vm.Beats);
                Assert.True(vm.IsEmpty);
                Assert.Equal("", vm.BoardTitle);
                Assert.Empty(vm.SceneOptions);
                Assert.False(vm.CanEdit);
                Assert.False(await vm.AddBeatCommand.CanExecute.FirstAsync());
                Assert.True(await Within(vm.FlushAsync()));
            });

            Assert.Equal("Written just before closing", Assert.Single(await LoadFromDiskAsync(root, BoardA)).Note);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Card_scene_picker_links_and_unlinks_through_the_board()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Hook"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var card = Assert.Single(vm.Beats);

                Assert.Equal(
                    [BeatBoardViewModel.NoScene, new SceneOption(SceneOne, "Scene One"), new SceneOption(SceneTwo, "Scene Two")],
                    card.SceneChoices);
                Assert.Equal(BeatBoardViewModel.NoScene, card.SelectedScene);

                card.SelectedScene = card.SceneChoices[1];
                await PumpUntilAsync(() => card.ScenePath == SceneOne);
                Assert.Equal(card.SceneChoices[1], card.SelectedScene);
                Assert.True(await Within(vm.FlushAsync()));
                Assert.Equal(SceneOne, Assert.Single(await LoadFromDiskAsync(root, BoardA)).ScenePath);

                // A picker that loses its items writes null back; that never unlinks.
                card.SelectedScene = null;
                Assert.Equal(SceneOne, card.ScenePath);

                card.SelectedScene = BeatBoardViewModel.NoScene;
                await PumpUntilAsync(() => card.ScenePath == null);
                Assert.True(await Within(vm.FlushAsync()));
                Assert.Null(Assert.Single(await LoadFromDiskAsync(root, BoardA)).ScenePath);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Missing_scene_has_no_picker_selection_and_can_be_unlinked()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Hook", scene: "gone.md"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var card = Assert.Single(vm.Beats);

                Assert.True(card.IsSceneMissing);
                Assert.Null(card.SelectedScene);

                card.SelectedScene = BeatBoardViewModel.NoScene;
                await PumpUntilAsync(() => card.ScenePath == null);
                Assert.False(card.IsSceneMissing);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Template_options_build_apply_requests()
    {
        var root = CreateWorkspace();
        try
        {
            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));

                Assert.Equal(vm.Templates.Select(t => t.Key), vm.TemplateOptions.Select(o => o.Key));

                var threeAct = vm.TemplateOptions.Single(o => o.Key == "threeAct");
                Assert.False(threeAct.HasParameter);
                Assert.Equal(new ApplyTemplateRequest("threeAct", TemplateApplyMode.Replace, null), threeAct.ReplaceRequest);

                var mice = vm.TemplateOptions.Single(o => o.Key == "mice");
                Assert.True(mice.HasParameter);
                Assert.Equal(["Milieu", "Inquiry", "Character", "Event"], mice.ParameterOptions);
                Assert.Equal("Milieu", mice.SelectedParameter);

                mice.SelectedParameter = "Event";
                mice.SelectedParameter = null; // a picker writing null back is ignored
                Assert.Equal(new ApplyTemplateRequest("mice", TemplateApplyMode.Append, "Event"), mice.AppendRequest);

                await Within(vm.ApplyTemplateCommand.Execute(mice.AppendRequest).ToTask());
                Assert.Equal(["Open: Event", "Close: Event"], vm.Beats.Select(b => b.Title));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Delete_beat_command_deletes_that_card_after_confirm()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Keep"), B("b", "Drop"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var prompts = new List<string>();
                vm.ConfirmAsync = request => { prompts.Add(request.Message); return Task.FromResult(true); };

                await Within(vm.DeleteBeatCommand.Execute(vm.Beats[1]).ToTask());

                Assert.Single(prompts);
                Assert.Equal(["Keep"], vm.Beats.Select(b => b.Title));
            });

            Assert.Equal(["Keep"], (await LoadFromDiskAsync(root, BoardA)).Select(b => b.Title));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Delete_confirm_pending_across_a_board_switch_changes_neither_board()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "Keep me"));
            await SeedAsync(root, BoardB, B("b", "Board B beat"));
            var bytesA = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, BoardA));
            var bytesB = await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, BoardB));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                ConfirmRequest? asked = null;
                vm.ConfirmAsync = request => { asked = request; return answer.Task; };
                vm.Selected = vm.Beats[0];

                var delete = vm.DeleteSelectedCommand.Execute().ToTask();
                await PumpUntilAsync(() => asked != null);
                Assert.Equal("Delete Beat", asked!.Title);
                Assert.Equal("Delete", asked.ConfirmText);

                await Within(vm.OpenBoardAsync(BoardB, "Story B", Scope));
                answer.SetResult(true);
                await Within(delete);

                Assert.Equal(["Board B beat"], vm.Beats.Select(b => b.Title));
                Assert.Equal(0, context.Boards.SaveCount);
            });

            Assert.Equal(bytesA, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, BoardA)));
            Assert.Equal(bytesB, await File.ReadAllBytesAsync(BeatBoardService.BoardFilePath(root, BoardB)));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Failed_save_on_a_closed_board_does_not_fail_the_next_flush()
    {
        var root = CreateWorkspace();
        try
        {
            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                context.Boards.FailSaves = true;
                await Within(vm.AddBeatCommand.Execute().ToTask());
                Assert.False(await Within(vm.FlushAsync()));

                context.Boards.FailSaves = false;
                await Within(vm.OpenBoardAsync(BoardB, "Story B", Scope));
                Assert.True(await Within(vm.FlushAsync()));

                context.Boards.FailSaves = true;
                await Within(vm.AddBeatCommand.Execute().ToTask());
                Assert.False(await Within(vm.FlushAsync()));
                context.Boards.FailSaves = false;
                await Within(vm.CloseBoardAsync());
                Assert.True(await Within(vm.FlushAsync()));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Blank_template_cannot_be_appended_and_appending_it_changes_nothing()
    {
        var root = CreateWorkspace();
        try
        {
            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var blank = vm.TemplateOptions.Single(o => o.Key == "blank");
                Assert.False(blank.CanAppend);
                Assert.All(vm.TemplateOptions.Where(o => o.Key != "blank"), o => Assert.True(o.CanAppend));

                await Within(vm.ApplyTemplateCommand.Execute(blank.AppendRequest).ToTask());

                Assert.True(vm.IsEmpty);
                Assert.Equal(0, context.Boards.SaveCount);
            });

            Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, BoardA)));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Board_file_without_scene_ids_still_links_by_path()
    {
        var root = CreateWorkspace();
        try
        {
            var path = BeatBoardService.BoardFilePath(root, BoardA);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                $$"""{ "schemaVersion": 1, "beats": [ { "id": "a", "title": "Climb", "note": "", "scenePath": "{{SceneOne}}" } ] }""");
            var before = await File.ReadAllBytesAsync(path);

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var card = Assert.Single(vm.Beats);

                Assert.False(card.IsSceneMissing);
                Assert.Equal("Scene One", card.SceneTitle);
                Assert.Equal(SceneOne, card.ScenePath);
                Assert.Same(vm.SceneOptions[0], card.SelectedScene);
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                Assert.True(await Within(vm.FlushAsync()));
                Assert.Equal(0, context.Boards.SaveCount);
            });

            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Update_scope_keeps_cards_and_selection()
    {
        var root = CreateWorkspace();
        try
        {
            await SeedAsync(root, BoardA, B("a", "One"), B("b", "Two"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.OpenBoardAsync(BoardA));
                var cards = vm.Beats.ToList();
                vm.Selected = cards[1];

                vm.UpdateScope("Renamed Story", [SceneTwo, SceneOne]);

                Assert.Equal("Renamed Story", vm.BoardTitle);
                Assert.Equal([SceneTwo, SceneOne], vm.SceneOptions.Select(o => o.RelativePath));
                Assert.Equal(cards, vm.Beats);
                Assert.Same(cards[1], vm.Selected);
                Assert.Equal(0, context.Boards.SaveCount);
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Pumps the current UI dispatcher until <paramref name="predicate"/> holds; call on the UI thread.</summary>
    private static async Task PumpUntilAsync(Func<bool> predicate,
        [CallerArgumentExpression(nameof(predicate))] string? what = null)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static async Task Within(Task task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        if (await Task.WhenAny(task, Task.Delay(StepTimeout)) != task)
            Assert.Fail($"Timed out waiting for {what}.");
        await task;
    }

    private static async Task<T> Within<T>(Task<T> task, [CallerArgumentExpression(nameof(task))] string? what = null)
    {
        await Within((Task)task, what);
        return await task;
    }

    private static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-beats-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".hymnal-data"));
        File.WriteAllText(Path.Combine(root, "Book.txt"), $"{SceneOne}\n{SceneTwo}\n");
        File.WriteAllText(Path.Combine(root, SceneOne), SceneOneText);
        File.WriteAllText(Path.Combine(root, SceneTwo), "# Scene Two\n\nThe light goes out.\n");
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "project.json"), StoryJson);
        return root;
    }

    private static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task SeedAsync(string root, string key, params Beat[] beats)
    {
        var result = await new BeatBoardService(new MetadataStore()).SaveAsync(root, key, beats);
        Assert.True(result.IsSuccess, result.Error);
    }

    private static async Task<IReadOnlyList<Beat>> LoadFromDiskAsync(string root, string key)
    {
        var result = await new BeatBoardService(new MetadataStore()).LoadAsync(root, key);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    /// <summary>
    /// Opens <paramref name="root"/> and runs <paramref name="body"/> in one UI Dispatch (each Dispatch gets a
    /// fresh dispatcher, so opening, hydration and the test's UI work must share a job). Watchers and view
    /// models are disposed before the Dispatch ends.
    /// </summary>
    private static Task Ui(string root, Func<TestContext, Task> body) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var context = new TestContext(root);
            await Within(context.OpenWorkspaceInCurrentDispatchAsync());
            await PumpUntilAsync(() => context.Workspace.Nodes.Count == 2);
            await body(context);
            return 0;
        }, TimeSpan.FromSeconds(40));

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();

        public RecordingNotificationService Notifications { get; } = new();
        public CountingBeatBoardService Boards { get; }
        public HistoricalScheduler Scheduler { get; } = new();
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string folder)
        {
            var store = new MetadataStore();
            var exclusions = new ExclusionManifestService(store);
            var registry = new ChapterRegistryService(store);
            var wordCount = new WordCountService();
            var projectSettings = new ProjectSettingsService(store);
            Boards = new CountingBeatBoardService(new BeatBoardService(store));
            var editor = _owned.Track(new EditorViewModel(store, Notifications, wordCount, new StoryFileWriter(store)));
            var manuscript = _owned.Track(new ManuscriptService(Notifications, exclusions));

            Workspace = new WorkspaceViewModel(
                manuscript,
                new BookTxtStructureService(store, exclusions, registry),
                new StubFilePickerService(),
                new InMemoryAppSettingsStore(),
                new FixedFolderPickerService(folder),
                Notifications,
                editor,
                registry,
                new PhaseDataService(store),
                new TargetsService(store),
                wordCount,
                new WordCountHistoryService(store),
                exclusions,
                new OrphanFileDiscoveryService(),
                projectSettings,
                new ProjectInitializer(store, projectSettings),
                new NeverInitPrompt(),
                beatBoards: Boards);
        }

        public void Dispose() => _owned.Dispose();

        public async Task OpenWorkspaceInCurrentDispatchAsync()
        {
            var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
            await Within((Task)method.Invoke(Workspace, Array.Empty<object>())!);
            await AwaitHydrationAsync();
        }

        public async Task AwaitHydrationAsync()
        {
            var hydration = typeof(WorkspaceViewModel)
                .GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Workspace) as Task;
            if (hydration != null)
                await Within(hydration);
        }

        /// <summary>Creates a beat board view model and opens <paramref name="key"/>; call on the UI thread.</summary>
        public async Task<BeatBoardViewModel> OpenBoardAsync(string key, IReadOnlyList<string>? scope = null)
        {
            var vm = _owned.Track(new BeatBoardViewModel(Workspace, Notifications, Scheduler));
            // Run the workspace-change handler the constructor may have posted, so none runs mid-test.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Within(vm.OpenBoardAsync(key, "The Story", scope ?? Scope));
            return vm;
        }
    }

    private sealed class CountingBeatBoardService(IBeatBoardService inner) : IBeatBoardService
    {
        public int SaveCount => SavedKeys.Count;
        public List<string> SavedKeys { get; } = new();
        public string? LastSavedKey => SavedKeys.LastOrDefault();
        public IReadOnlyList<Beat>? LastSaved { get; private set; }

        private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);

        /// <summary>The next load of <paramref name="key"/> reads the file, then waits until the returned source is set.</summary>
        public TaskCompletionSource HoldNextLoad(string key)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _holds[key] = hold;
            return hold;
        }

        public bool IsHeld(string key) => _held.Contains(key);

        public async Task<Result<IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey)
        {
            var result = await inner.LoadAsync(workspaceRoot, boardKey);
            if (_holds.Remove(boardKey, out var hold))
            {
                _held.Add(boardKey);
                await hold.Task;
                _held.Remove(boardKey);
            }
            return result;
        }

        /// <summary>While true, saves fail without writing.</summary>
        public bool FailSaves { get; set; }

        public Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats)
        {
            SavedKeys.Add(boardKey);
            LastSaved = beats;
            if (FailSaves)
                return Task.FromResult(Result<Unit>.Fail("The disk is full."));
            return inner.SaveAsync(workspaceRoot, boardKey, beats);
        }

        public bool BoardExists(string workspaceRoot, string boardKey) => inner.BoardExists(workspaceRoot, boardKey);

        public Task<Result<Unit>> MoveBoardAsync(string workspaceRoot, string fromKey, string toKey) =>
            inner.MoveBoardAsync(workspaceRoot, fromKey, toKey);

        public Task<Result<Unit>> DeleteBoardAsync(string workspaceRoot, string boardKey) =>
            inner.DeleteBoardAsync(workspaceRoot, boardKey);
    }

    private sealed class NeverInitPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) { }
    }

    private sealed class InMemoryAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedFolderPickerService(string folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}

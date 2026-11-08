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
public sealed class IdeasViewModelTests : IDisposable
{
    /// <summary>Contexts created by the running test; their file watchers are released in <see cref="Dispose"/>.</summary>
    private static readonly List<TestContext> LiveContexts = new();

    public void Dispose()
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
    }

    static IdeasViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";
    private const string AnthologyJson = "{ \"schemaVersion\": 1, \"kind\": \"anthology\", \"storage\": \"sidecar\" }";

    private static readonly DateOnly Day1 = new(2026, 1, 1);
    private static readonly DateOnly Day2 = new(2026, 2, 1);
    private static readonly DateOnly Day3 = new(2026, 3, 1);

    private static StoryIdea Idea(string id, string title, DateOnly created, IdeaState state = IdeaState.New,
        params string[] tags) =>
        new(id, title, "", "", tags, null, state, created, null);

    [Fact]
    public async Task Load_hides_used_and_shelved_by_default()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root,
                Idea("a", "Alpha", Day1),
                Idea("b", "Bravo", Day1, IdeaState.Developing),
                Idea("c", "Charlie", Day1, IdeaState.Used),
                Idea("d", "Delta", Day1, IdeaState.Shelved));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                Assert.False(vm.ShowUsedAndShelved);
                Assert.Equal(["Alpha", "Bravo"], vm.Cards.Select(c => c.Title));

                vm.ShowUsedAndShelved = true;
                Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta"], vm.Cards.Select(c => c.Title));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Cards_sorted_newest_first()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root,
                Idea("a", "Old", Day1),
                Idea("b", "Zulu", Day3),
                Idea("c", "Middle", Day2),
                Idea("d", "Alpha", Day3));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                Assert.Equal(["Alpha", "Zulu", "Middle", "Old"], vm.Cards.Select(c => c.Title));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Tag_filter_is_case_insensitive()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root,
                Idea("a", "Ghost ship", Day1, IdeaState.New, "Horror", "sea"),
                Idea("b", "Love letter", Day2, IdeaState.New, "romance"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                Assert.Equal(["Horror", "romance", "sea"], vm.AllTags);

                vm.TagFilter = "horror";
                Assert.Equal(["Ghost ship"], vm.Cards.Select(c => c.Title));

                vm.TagFilter = null;
                Assert.Equal(2, vm.Cards.Count);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task New_idea_saves_and_selects()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                await Within(vm.NewIdeaCommand.Execute(("The Lighthouse", "A keeper sees a ship that sank.")).ToTask());

                Assert.Equal("The Lighthouse", vm.Selected?.Title);
                Assert.Contains(vm.Selected, vm.Cards);
            });

            var saved = Assert.Single(await LoadFromDiskAsync(root));
            Assert.Equal("The Lighthouse", saved.Title);
            Assert.Equal("A keeper sees a ship that sank.", saved.Premise);
            Assert.Equal(IdeaState.New, saved.State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edits_debounce_into_one_save()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Draft", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                context.Ideas.SaveCount = 0;

                vm.Selected.Title = "First";
                vm.Selected.Title = "Second";
                vm.Selected.Title = "Third";
                context.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(200));
                Assert.Equal(0, context.Ideas.SaveCount);

                // The throttle itself starts the write; no explicit flush.
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                Assert.Equal(1, context.Ideas.SaveCount);
                Assert.Equal("Third", Assert.Single(context.Ideas.LastSaved!).Title);

                await Within(vm.FlushAsync()); // let the write finish
                Assert.Equal(1, context.Ideas.SaveCount);
            });

            Assert.Equal("Third", Assert.Single(await LoadFromDiskAsync(root)).Title);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Changing_selection_flushes_pending_save()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "One", Day1), Idea("b", "Two", Day2));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                var one = vm.Cards.Single(c => c.Id == "a");
                vm.Selected = one;
                context.Ideas.SaveCount = 0;

                one.Notes = "Pending notes";
                vm.Selected = vm.Cards.Single(c => c.Id == "b");
                Assert.Equal(1, context.Ideas.SaveCount);
                Assert.Equal("Pending notes", context.Ideas.LastSaved!.Single(i => i.Id == "a").Notes);
                await Within(vm.FlushAsync()); // let the write finish

                // The debounce window passing afterwards does not write again.
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                await Within(vm.FlushAsync());
                Assert.Equal(1, context.Ideas.SaveCount);
            });

            Assert.Equal("Pending notes", (await LoadFromDiskAsync(root)).Single(i => i.Id == "a").Notes);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Keep or toss", Day1));
            var prompts = new List<string>();

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();

                vm.ConfirmAsync = message => { prompts.Add(message); return Task.FromResult(false); };
                await Within(vm.DeleteSelectedCommand.Execute().ToTask());
                Assert.Single(vm.Cards);
            });
            Assert.Single(prompts);
            Assert.Single(await LoadFromDiskAsync(root));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();

                vm.ConfirmAsync = _ => Task.FromResult(true);
                await Within(vm.DeleteSelectedCommand.Execute().ToTask());
                Assert.Empty(vm.Cards);
                Assert.Null(vm.Selected);
            });
            Assert.Empty(await LoadFromDiskAsync(root));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Malformed_file_sets_read_only_and_never_writes()
    {
        var root = CreateWorkspace(AnthologyJson);
        var ideasPath = StoryIdeasService.IdeasFilePath(root);
        File.WriteAllText(ideasPath, "{ this is not json");
        var before = await File.ReadAllBytesAsync(ideasPath);
        try
        {

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());

                Assert.True(vm.IsReadOnly);
                Assert.Contains(IdeasViewModel.ReadOnlyNotice, context.Notifications.Infos);
                Assert.Empty(vm.Cards);
                Assert.False(await vm.NewIdeaCommand.CanExecute.FirstAsync());
                Assert.False(await vm.DeleteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(await vm.PromoteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(vm.CanPromote);

                await Within(vm.NewIdeaAsync("Sneaky", ""));
                await Within(vm.FlushAsync());
                Assert.Equal(0, context.Ideas.SaveCount);
            });

            Assert.Equal(before, await File.ReadAllBytesAsync(ideasPath));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task CanPromote_false_for_short_story_profile()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();

                Assert.False(vm.CanPromote);
                Assert.False(await vm.PromoteSelectedCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_success_reloads_workspace_and_selects_story()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));
            string? suggested = null;

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                Assert.True(vm.CanPromote);
                vm.AskFileNameAsync = suggestion => { suggested = suggestion; return Task.FromResult<string?>(suggestion); };
                var opened = new List<string>();
                using var openedSubscription = vm.StoryOpened.Subscribe(opened.Add);

                await Within(vm.PromoteSelectedCommand.Execute().ToTask());

                Assert.Empty(context.Notifications.Errors);
                Assert.Equal(["the-lighthouse.md"], opened);
                Assert.False(vm.IsPromoting);
                Assert.Equal("the-lighthouse.md", context.Workspace.SelectedNode?.Node.RelativePath);
                await PumpUntilAsync(() => context.Editor.ActiveNode?.RelativePath == "the-lighthouse.md");
                Assert.Equal("the-lighthouse.md", context.Editor.ActiveNode?.RelativePath);
                Assert.Empty(vm.Cards);
                // The now-used idea is filtered out, so it isn't left selected behind the list.
                Assert.Null(vm.Selected);

                vm.ShowUsedAndShelved = true;
                Assert.Equal(IdeaState.Used, vm.Cards.Single().State);
            });

            Assert.Equal("the-lighthouse.md", suggested);
            Assert.Equal("# The Lighthouse\n\n", File.ReadAllText(Path.Combine(root, "the-lighthouse.md")));
            Assert.Equal(IdeaState.Used, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task CanPromote_false_while_ideas_belong_to_another_workspace()
    {
        var first = CreateWorkspace(AnthologyJson);
        var second = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(first, Idea("a", "The Lighthouse", Day1));
            await SeedAsync(second, Idea("b", "Bravo", Day1));

            await Ui(first, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                Assert.True(vm.CanPromote);
                var asked = false;
                vm.AskFileNameAsync = suggestion => { asked = true; return Task.FromResult<string?>(suggestion); };

                // Open the second workspace while its ideas are still being read: the first
                // workspace's cards are still shown and must not be promoted into the second.
                var hold = context.Ideas.HoldNextLoad(second);
                context.FolderPicker.Folder = second;
                await Within(context.OpenWorkspaceInCurrentDispatchAsync());
                await PumpUntilAsync(() => context.Ideas.IsHeld(second));

                Assert.Equal("The Lighthouse", vm.Selected?.Title);
                Assert.False(vm.CanPromote);
                await Within(vm.PromoteSelectedAsync());
                Assert.False(asked);

                hold.SetResult();
                await PumpUntilAsync(() => !context.Ideas.IsHeld(second));
                await Within(CurrentLoad(vm));
            });

            Assert.False(File.Exists(Path.Combine(second, "the-lighthouse.md")));
            Assert.Equal(IdeaState.New, Assert.Single(await LoadFromDiskAsync(first)).State);
        }
        finally
        {
            DeleteFolder(first);
            DeleteFolder(second);
        }
    }

    [Fact]
    public async Task CanPromote_false_for_idea_already_linked_to_a_story()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root,
                new StoryIdea("a", "Linked", "", "", [], null, IdeaState.Developing, Day1, "story-uuid"));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                var asked = false;
                vm.AskFileNameAsync = suggestion => { asked = true; return Task.FromResult<string?>(suggestion); };
                vm.Selected = vm.Cards.Single();

                Assert.False(vm.CanPromote);
                await Within(vm.PromoteSelectedAsync());
                Assert.False(asked);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_failure_shows_error()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                // A name already on disk collides, so nothing is created.
                vm.AskFileNameAsync = _ => Task.FromResult<string?>("story-one.md");

                await Within(vm.PromoteSelectedCommand.Execute().ToTask());

                Assert.Single(context.Notifications.Errors);
                Assert.Equal(IdeaState.New, vm.Cards.Single().State);
            });

            Assert.Equal(IdeaState.New, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_keeps_selection_when_the_used_idea_stays_visible()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.ShowUsedAndShelved = true;
                vm.Selected = vm.Cards.Single();
                vm.AskFileNameAsync = suggestion => Task.FromResult<string?>(suggestion);

                await Within(vm.PromoteSelectedCommand.Execute().ToTask());

                Assert.Empty(context.Notifications.Errors);
                Assert.Equal("a", vm.Selected?.Id);
                Assert.Equal(IdeaState.Used, vm.Selected!.State);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Edit_during_promote_is_not_saved_over_the_used_idea()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                var card = vm.Cards.Single();
                vm.Selected = card;
                vm.AskFileNameAsync = suggestion => Task.FromResult<string?>(suggestion);
                Assert.True(vm.CanEdit);

                // Hold the promoter's pre-flight read of ideas.json.
                var hold = context.Ideas.HoldNextLoad(root);
                var promote = vm.PromoteSelectedCommand.Execute().ToTask();
                await PumpUntilAsync(() => context.Ideas.IsHeld(root));

                Assert.True(vm.IsPromoting);
                Assert.False(vm.CanEdit);
                Assert.False(await vm.NewIdeaCommand.CanExecute.FirstAsync());
                Assert.False(await vm.DeleteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(await vm.PromoteSelectedCommand.CanExecute.FirstAsync());
                Assert.False(vm.CanPromote);

                card.Title = "Edited mid-promote";
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));

                hold.SetResult();
                await Within(promote);
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                await Within(vm.FlushAsync());

                Assert.Empty(context.Notifications.Errors);
                Assert.False(vm.IsPromoting);
                Assert.True(vm.CanEdit);
                Assert.True(await vm.NewIdeaCommand.CanExecute.FirstAsync());
            });

            var saved = Assert.Single(await LoadFromDiskAsync(root));
            Assert.Equal(IdeaState.Used, saved.State);
            Assert.NotNull(saved.StoryId);
            Assert.Equal("The Lighthouse", saved.Title);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Dispose_during_promote_finishes_the_promote_without_touching_disposed_subjects()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                vm.AskFileNameAsync = suggestion => Task.FromResult<string?>(suggestion);
                var opened = new List<string>();
                using var openedSubscription = vm.StoryOpened.Subscribe(opened.Add);

                // Hold the promoter's pre-flight read, then close the view (as the window would).
                var hold = context.Ideas.HoldNextLoad(root);
                var promote = vm.PromoteSelectedAsync();
                await PumpUntilAsync(() => context.Ideas.IsHeld(root));
                vm.Dispose();

                hold.SetResult();
                await Within(promote);

                Assert.Empty(opened);
                Assert.Empty(context.Notifications.Errors);
            });

            // The promote still completed on disk.
            Assert.True(File.Exists(Path.Combine(root, "the-lighthouse.md")));
            Assert.Equal(IdeaState.Used, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_failure_after_creating_the_file_still_lists_the_story()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1) with { Notes = "Some notes." });

            await Ui(root, async context =>
            {
                context.PromoterNotes = new ThrowingNotesService();
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                vm.AskFileNameAsync = suggestion => Task.FromResult<string?>(suggestion);
                var opened = new List<string>();
                using var openedSubscription = vm.StoryOpened.Subscribe(opened.Add);

                await Within(vm.PromoteSelectedCommand.Execute().ToTask());
                await PumpUntilAsync(() => context.Workspace.Nodes.Count == 2);

                Assert.Contains(context.Workspace.Nodes, n => n.Node.RelativePath == "the-lighthouse.md");
                var error = Assert.Single(context.Notifications.Errors);
                Assert.Contains("notes boom", error);
                Assert.Empty(opened);
                Assert.Equal(IdeaState.New, vm.Cards.Single().State);
            });

            Assert.True(File.Exists(Path.Combine(root, "the-lighthouse.md")));
            Assert.Equal(IdeaState.New, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_aborts_when_pending_edits_cannot_be_saved()
    {
        var root = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(root, Idea("a", "The Lighthouse", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                var asked = false;
                vm.AskFileNameAsync = suggestion => { asked = true; return Task.FromResult<string?>(suggestion); };

                context.Ideas.FailSaves = true;
                vm.Selected.Notes = "Unsaved";
                await Within(vm.PromoteSelectedCommand.Execute().ToTask());

                Assert.False(asked);
                Assert.Contains(context.Notifications.Errors, e => e.Contains("wasn't promoted"));
                Assert.Equal(IdeaState.New, vm.Cards.Single().State);
            });

            Assert.False(File.Exists(Path.Combine(root, "the-lighthouse.md")));
            Assert.Equal(IdeaState.New, Assert.Single(await LoadFromDiskAsync(root)).State);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Promote_finishing_after_another_workspace_opened_leaves_that_workspace_alone()
    {
        var first = CreateWorkspace(AnthologyJson);
        var second = CreateWorkspace(AnthologyJson);
        try
        {
            await SeedAsync(first, Idea("a", "The Lighthouse", Day1));
            await SeedAsync(second, Idea("b", "Bravo", Day1));

            await Ui(first, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                vm.AskFileNameAsync = suggestion => Task.FromResult<string?>(suggestion);
                var opened = new List<string>();
                using var openedSubscription = vm.StoryOpened.Subscribe(opened.Add);

                var hold = context.Ideas.HoldNextLoad(first);
                var promote = vm.PromoteSelectedCommand.Execute().ToTask();
                await PumpUntilAsync(() => context.Ideas.IsHeld(first));

                context.FolderPicker.Folder = second;
                await Within(context.OpenWorkspaceInCurrentDispatchAsync());
                var selectedBefore = context.Workspace.SelectedNode;

                hold.SetResult();
                await Within(promote);
                await Within(CurrentLoad(vm));

                Assert.Empty(context.Notifications.Errors);
                Assert.Empty(opened);
                Assert.Equal(Path.GetFullPath(second), Path.GetFullPath(context.Workspace.WorkspaceRoot!));
                Assert.Same(selectedBefore, context.Workspace.SelectedNode);
                Assert.DoesNotContain(context.Workspace.Nodes, n => n.Node.RelativePath == "the-lighthouse.md");
                Assert.Equal(["Bravo"], vm.Cards.Select(c => c.Title));
            });

            // The promote itself completed in the workspace it started in.
            Assert.True(File.Exists(Path.Combine(first, "the-lighthouse.md")));
            Assert.Equal(IdeaState.Used, Assert.Single(await LoadFromDiskAsync(first)).State);
            Assert.False(File.Exists(Path.Combine(second, "the-lighthouse.md")));
        }
        finally
        {
            DeleteFolder(first);
            DeleteFolder(second);
        }
    }

    [Fact]
    public async Task Profile_switch_to_novel_clears_cards()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Alpha", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                Assert.Single(vm.Cards);

                var save = await context.ProjectSettings.SaveAsync(root, new ProjectSettings(ProjectKind.Novel));
                Assert.True(save.IsSuccess, save.Error);
                var reload = await Within(context.Workspace.ReloadProjectAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await PumpUntilAsync(() => vm.Cards.Count == 0);

                Assert.Equal(ProjectKind.Novel, context.Workspace.Profile.Kind);
                Assert.Empty(vm.Cards);
                Assert.Null(vm.Selected);
                Assert.False(await vm.NewIdeaCommand.CanExecute.FirstAsync());
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Same_root_workspace_change_keeps_pending_edit_and_saves_it()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Draft", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                var card = vm.Cards.Single();
                vm.Selected = card;
                context.Ideas.SaveCount = 0;

                card.Title = "Edited during reload";
                var reorder = await Within(context.Workspace.ReorderNodesAsync()); // raises WorkspaceChanged, same root
                Assert.True(reorder.IsSuccess, reorder.Error);
                // Run the posted change handler, then wait for any reload it started.
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Within(CurrentLoad(vm));
                await Within(vm.FlushAsync());

                Assert.Same(card, vm.Selected);
                Assert.Equal("Edited during reload", vm.Selected!.Title);
                Assert.Equal(1, context.Ideas.SaveCount);
            });

            Assert.Equal("Edited during reload", Assert.Single(await LoadFromDiskAsync(root)).Title);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Opening_another_workspace_clears_tag_filter()
    {
        var first = CreateWorkspace(StoryJson);
        var second = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(first, Idea("a", "Ghost ship", Day1, IdeaState.New, "horror"));
            await SeedAsync(second, Idea("b", "Love letter", Day1, IdeaState.New, "romance"));

            await Ui(first, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.TagFilter = "horror";
                Assert.Single(vm.Cards);

                context.FolderPicker.Folder = second;
                await Within(context.OpenWorkspaceInCurrentDispatchAsync());
                await PumpUntilAsync(() => vm.Cards.Any(c => c.Id == "b"));

                Assert.Null(vm.TagFilter);
                Assert.Equal(["Love letter"], vm.Cards.Select(c => c.Title));
            });
        }
        finally
        {
            DeleteFolder(first);
            DeleteFolder(second);
        }
    }

    [Fact]
    public async Task Editor_states_exclude_used()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Fresh", Day1), Idea("b", "Spent", Day1, IdeaState.Used));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.ShowUsedAndShelved = true;
                Assert.DoesNotContain(IdeaState.Used, vm.States);

                var fresh = vm.Cards.Single(c => c.Id == "a");
                Assert.DoesNotContain(IdeaState.Used, fresh.StateOptions);
                fresh.State = IdeaState.Used;
                Assert.Equal(IdeaState.New, fresh.State);

                var spent = vm.Cards.Single(c => c.Id == "b");
                Assert.Contains(IdeaState.Used, spent.StateOptions);
                Assert.Equal(IdeaState.Used, spent.State);

                // A used idea's state is read-only.
                Assert.True(fresh.IsStateEditable);
                Assert.False(spent.IsStateEditable);
                spent.State = IdeaState.New;
                Assert.Equal(IdeaState.Used, spent.State);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_back_while_a_load_is_in_flight_shows_the_current_workspace()
    {
        var first = CreateWorkspace(StoryJson);
        var second = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(first, Idea("a", "Alpha", Day1));
            await SeedAsync(second, Idea("b", "Bravo", Day1));

            await Ui(first, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                var holdSecond = context.Ideas.HoldNextLoad(second);

                // A → B: B's ideas load is held in flight.
                context.FolderPicker.Folder = second;
                await Within(context.OpenWorkspaceInCurrentDispatchAsync());
                await PumpUntilAsync(() => context.Ideas.IsHeld(second));

                // B → A before B's load finishes.
                context.FolderPicker.Folder = first;
                await Within(context.OpenWorkspaceInCurrentDispatchAsync());
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                holdSecond.SetResult();
                await PumpUntilAsync(() => !context.Ideas.IsHeld(second));
                await Within(CurrentLoad(vm));

                Assert.Equal(["Alpha"], vm.Cards.Select(c => c.Title));
                Assert.Equal(context.Workspace.WorkspaceRoot, LoadedRoot(vm));
                Assert.Equal(Path.GetFullPath(first), Path.GetFullPath(LoadedRoot(vm)!));
            });
        }
        finally
        {
            DeleteFolder(first);
            DeleteFolder(second);
        }
    }

    [Fact]
    public async Task Edit_made_while_ideas_are_being_read_is_saved_not_dropped()
    {
        var root = CreateWorkspace(StoryJson);
        try
        {
            await SeedAsync(root, Idea("a", "Draft", Day1));

            await Ui(root, async context =>
            {
                var vm = await Within(context.CreateIdeasAsync());
                vm.Selected = vm.Cards.Single();
                context.Ideas.SaveCount = 0;

                var hold = context.Ideas.HoldNextLoad(root);
                var load = vm.LoadAsync();
                await PumpUntilAsync(() => context.Ideas.IsHeld(root));

                // The read already returned "Draft"; this edit must survive replacing the cards.
                vm.Selected!.Title = "Edited mid-load";
                hold.SetResult();
                await Within(load);

                Assert.Equal("Edited mid-load", vm.Cards.Single().Title);
                Assert.Equal("Edited mid-load", vm.Selected?.Title);

                // The reload loop normally writes the edit itself. If a change-triggered reload overlapped
                // and took the held read, the edit is still pending on the live card and the debounce writes it.
                context.Scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
                await Within(CurrentLoad(vm));
                await Within(vm.FlushAsync());
                Assert.Equal(1, context.Ideas.SaveCount);
            });

            Assert.Equal("Edited mid-load", Assert.Single(await LoadFromDiskAsync(root)).Title);
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Pumps the current UI dispatcher until <paramref name="predicate"/> holds (call on the UI thread);
    /// fails after <see cref="StepTimeout"/> so a stuck wait ends the Dispatch job instead of stalling the session.
    /// </summary>
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

    /// <summary>Awaits <paramref name="task"/>, failing fast after <see cref="StepTimeout"/> instead of hanging the UI session.</summary>
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

    private static string CreateWorkspace(string projectJson)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-ideas-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".hymnal-data"));
        File.WriteAllText(Path.Combine(root, "Book.txt"), "story-one.md\n");
        File.WriteAllText(Path.Combine(root, "story-one.md"), "# Story One\n");
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "project.json"), projectJson);
        return root;
    }

    /// <summary>Releases view models and file watchers first, so deleting the folder raises no watcher events.</summary>
    private static void DeleteFolder(string root)
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task SeedAsync(string root, params StoryIdea[] ideas)
    {
        var result = await new StoryIdeasService(new MetadataStore()).SaveAsync(root, ideas);
        Assert.True(result.IsSuccess, result.Error);
    }

    private static async Task<IReadOnlyList<StoryIdea>> LoadFromDiskAsync(string root)
    {
        var result = await new StoryIdeasService(new MetadataStore()).LoadAsync(root);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    /// <summary>
    /// Opens <paramref name="root"/> and runs <paramref name="body"/> in one UI Dispatch. Each Dispatch gets a
    /// fresh dispatcher, so opening, hydration and the test's own UI work must share a single job.
    /// </summary>
    private static Task Ui(string root, Func<TestContext, Task> body) =>
        RunOnUiThreadWithPumpingAsync(async () =>
        {
            var context = new TestContext(root);
            await Within(context.OpenWorkspaceInCurrentDispatchAsync());
            await PumpUntilAsync(() => context.Workspace.Nodes.Count == 1);
            await body(context);
            return 0;
        });

    private static Task<T> RunOnUiThreadWithPumpingAsync<T>(Func<Task<T>> action) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(action, TimeSpan.FromSeconds(40));

    private static string? LoadedRoot(IdeasViewModel vm) =>
        (string?)typeof(IdeasViewModel)
            .GetField("_loadedRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm);

    private static Task CurrentLoad(IdeasViewModel vm) =>
        (Task)typeof(IdeasViewModel)
            .GetField("_currentLoad", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private static Task? HydrationTask(WorkspaceViewModel workspace) =>
        typeof(WorkspaceViewModel)
            .GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(workspace) as Task;

    private sealed class TestContext : IDisposable
    {
        private readonly ManuscriptService _manuscript;
        private readonly MetadataStore _store = new();
        private readonly BookTxtStructureService _structure;
        private readonly ChapterRegistryService _registry;
        private readonly PhaseDataService _phases;
        private readonly TargetsService _targets;
        private readonly List<IdeasViewModel> _ideasViewModels = new();

        /// <summary>Notes service the workspace's metadata store (and so the promoter) uses; null: the real one.</summary>
        public INotesService? PromoterNotes { get; set; }

        public RecordingNotificationService Notifications { get; } = new();
        public ProjectSettingsService ProjectSettings { get; }
        public CountingIdeasService Ideas { get; }
        public HistoricalScheduler Scheduler { get; } = new();
        public MutableFolderPickerService FolderPicker { get; }
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string folder)
        {
            var store = _store;
            var exclusions = new ExclusionManifestService(store);
            var registry = _registry = new ChapterRegistryService(store);
            var phases = _phases = new PhaseDataService(store);
            var targets = _targets = new TargetsService(store);
            var wordCount = new WordCountService();
            var structure = _structure = new BookTxtStructureService(store, exclusions, registry);
            ProjectSettings = new ProjectSettingsService(store);
            Ideas = new CountingIdeasService(new StoryIdeasService(store));
            var editor = Editor = new EditorViewModel(store, Notifications, wordCount, new StoryFileWriter(store));
            FolderPicker = new MutableFolderPickerService { Folder = folder };
            _manuscript = new ManuscriptService(Notifications, exclusions);
            LiveContexts.Add(this);

            Workspace = new WorkspaceViewModel(
                _manuscript,
                structure,
                new StubFilePickerService(),
                new InMemoryAppSettingsStore(),
                FolderPicker,
                Notifications,
                editor,
                registry,
                phases,
                targets,
                wordCount,
                new WordCountHistoryService(store),
                exclusions,
                new OrphanFileDiscoveryService(),
                ProjectSettings,
                new ProjectInitializer(store, ProjectSettings),
                new NeverInitPrompt(),
                new SwitchableNotesService(this, new NotesService(store)));
        }

        private sealed class SwitchableNotesService(TestContext context, INotesService real) : INotesService
        {
            public Task<string> LoadAsync(string absoluteNotesPath) =>
                (context.PromoterNotes ?? real).LoadAsync(absoluteNotesPath);

            public Task SaveAsync(string absoluteNotesPath, string content) =>
                (context.PromoterNotes ?? real).SaveAsync(absoluteNotesPath, content);

            public Task DeleteAsync(string absoluteNotesPath) =>
                (context.PromoterNotes ?? real).DeleteAsync(absoluteNotesPath);
        }

        public void Dispose()
        {
            foreach (var vm in _ideasViewModels) vm.Dispose();
            Editor.Dispose();
            _manuscript.Dispose();
        }

        /// <summary>
        /// Opens <see cref="FolderPicker"/>'s folder and waits for hydration inside the current Dispatch:
        /// each Dispatch gets a fresh dispatcher, so the node rebuild it posts would be lost once the job returns.
        /// </summary>
        public async Task OpenWorkspaceInCurrentDispatchAsync()
        {
            var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
            await Within((Task)method.Invoke(Workspace, Array.Empty<object>())!);
            if (HydrationTask(Workspace) is { } hydration)
                await Within(hydration);
        }

        /// <summary>Creates and loads an ideas view model; call on the UI thread.</summary>
        public async Task<IdeasViewModel> CreateIdeasAsync()
        {
            var promoter = new StoryIdeaPromoter(_structure, _registry, Ideas);
            var vm = new IdeasViewModel(Workspace, Ideas, promoter, Notifications, Scheduler);
            _ideasViewModels.Add(vm);
            await Within(vm.LoadAsync());
            // Run the change handler the constructor posted, so no stray reload starts mid-test.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Within(CurrentLoad(vm));
            return vm;
        }
    }

    private sealed class CountingIdeasService(IStoryIdeasService inner) : IStoryIdeasService
    {
        public int SaveCount { get; set; }
        public IReadOnlyList<StoryIdea>? LastSaved { get; private set; }
        public bool FailSaves { get; set; }

        private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);

        /// <summary>The next load of <paramref name="root"/> reads the file, then waits until the returned source is set.</summary>
        public TaskCompletionSource HoldNextLoad(string root)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _holds[Key(root)] = hold;
            return hold;
        }

        /// <summary>True while a load of <paramref name="root"/> is waiting on its hold.</summary>
        public bool IsHeld(string root) => _held.Contains(Key(root));

        public async Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot)
        {
            var result = await inner.LoadAsync(workspaceRoot);
            var key = Key(workspaceRoot);
            if (_holds.Remove(key, out var hold))
            {
                _held.Add(key);
                await hold.Task;
                _held.Remove(key);
            }
            return result;
        }

        private static string Key(string root) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        public Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas)
        {
            SaveCount++;
            LastSaved = ideas;
            if (FailSaves)
                return Task.FromResult(Result<Unit>.Fail("Disk is full."));
            return inner.SaveAsync(workspaceRoot, ideas);
        }
    }

    private sealed class ThrowingNotesService : INotesService
    {
        public Task<string> LoadAsync(string absoluteNotesPath) => Task.FromResult("");
        public Task SaveAsync(string absoluteNotesPath, string content) => throw new IOException("notes boom");
        public Task DeleteAsync(string absoluteNotesPath) => throw new IOException("notes boom");
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

    private sealed class MutableFolderPickerService : IFolderPickerService
    {
        public string? Folder { get; set; }
        public Task<string?> PickFolderAsync() => Task.FromResult(Folder);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}

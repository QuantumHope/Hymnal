using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Xunit;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Settings → Project: the "Keep everything in the story file" option and its migrations.</summary>
[Collection("AvaloniaUi")]
public sealed class SettingsStorageTests
{
    static SettingsStorageTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string EmbeddedJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"embedded\" }";
    private const string StoryFile = "story.md";
    private const string StoryText = "# The Lighthouse\n\nThe keeper climbs the stairs at dusk.\n";
    private const string Story = BeatBoardService.ShortStoryBoardKey;

    private static string ProjectJsonPath(string root) => ProjectSettingsService.ProjectFilePath(root);
    private static string NotesPath(string root) => INotesService.DeriveNotesPath(root, StoryFile);

    private static StorySplit ReadStory(string root) =>
        StoryFileFormat.Split(File.ReadAllText(Path.Combine(root, StoryFile)));

    private static string WithBlock(string prose, EmbeddedStoryMetadata block) => StoryFileFormat.Merge(prose, block, "\n");

    // ── Visibility and availability ───────────────────────────────────────────

    [Fact]
    public async Task Option_is_shown_for_short_story_only()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, context =>
            {
                var settings = context.CreateSettings();
                Assert.True(settings.IsStorageOptionVisible);
                Assert.True(settings.IsStorageOptionEnabled);
                Assert.Null(settings.StorageOptionExplanation);
                Assert.False(settings.KeepInStoryFile);

                settings.SelectedProjectKind = ProjectKind.Novel;
                Assert.False(settings.IsStorageOptionVisible);
                settings.SelectedProjectKind = ProjectKind.Anthology;
                Assert.False(settings.IsStorageOptionVisible);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Option_is_disabled_with_two_story_files_counting_a_missing_one()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText), ("gone.md", "# Gone\n"));
        File.Delete(Path.Combine(root, "gone.md"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var settings = context.CreateSettings();
                Assert.True(settings.IsStorageOptionVisible);
                Assert.False(settings.IsStorageOptionEnabled);
                Assert.Equal(SettingsViewModel.StorageOptionUnavailable, settings.StorageOptionExplanation);

                settings.KeepInStoryFile = true;
                Assert.False(settings.IsStorageOptionEnabled);
                Assert.Equal(SettingsViewModel.StorageOptionUnavailable, settings.StorageOptionExplanation);
                Assert.False(await settings.ApplyProjectKindCommand.CanExecute.FirstAsync());
                Assert.Null(settings.ProjectChangeSummary);
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Turning it on and off ─────────────────────────────────────────────────

    [Fact]
    public async Task Turning_it_on_migrates_then_saves_and_reloads()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                var store = new MetadataStore();
                await new PhaseDataService(store).UpsertAsync(root, uuid, _ => new PhaseData { Status = ChapterStatus.Editing });
                await new TargetsService(store).UpsertAsync(root, uuid, new WordCountTarget { MinWords = 1000, MaxWords = 7500 });
                await new NotesService(store).SaveAsync(NotesPath(root), "Remember the lamp.");
                await SeedAsync(root, Story, new Beat("b1", "Hook", "", null));

                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                Assert.Equal(SettingsViewModel.MoveIntoFileSummary, settings.ProjectChangeSummary);
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal(StorageMode.Embedded, context.Workspace.Profile.Storage);
                Assert.Contains("\"embedded\"", await File.ReadAllTextAsync(ProjectJsonPath(root)));

                var split = ReadStory(root);
                Assert.Equal(StoryText, split.Prose);
                Assert.Equal(uuid, split.Metadata?.Id);
                Assert.Equal(ChapterStatus.Editing, split.Metadata?.Status);
                Assert.Equal(7500, split.Metadata?.Target?.MaxWords);
                Assert.Equal("Remember the lamp.", split.Metadata?.Notes);
                Assert.Equal("Hook", Assert.Single(split.Metadata!.Beats!).Title);
                Assert.False(File.Exists(NotesPath(root)));
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));

                // The reloaded workspace reads the story's data from its file.
                await PumpUntilAsync(() => context.Node(StoryFile).Status == ChapterStatus.Editing);
                Assert.Equal(7500, context.Node(StoryFile).Target?.MaxWords);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Turning_it_off_moves_the_data_into_project_data()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, WithBlock(StoryText,
            new EmbeddedStoryMetadata(null, ChapterStatus.Drafting, null, null, null, null, "Lamp.",
                [new Beat("b1", "Hook", "", null)]))));
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                var settings = context.CreateSettings();
                Assert.True(settings.KeepInStoryFile);
                settings.KeepInStoryFile = false;
                Assert.Equal(SettingsViewModel.MoveOutOfFileSummary, settings.ProjectChangeSummary);
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal(StorageMode.Sidecar, context.Workspace.Profile.Storage);
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
                Assert.Equal(ChapterStatus.Drafting, (await new PhaseDataService(new MetadataStore()).LoadAsync(root))[uuid].Status);
                Assert.Equal("Lamp.", await File.ReadAllTextAsync(NotesPath(root)));
                Assert.Equal("Hook", Assert.Single(await LoadFromDiskAsync(root, Story)).Title);
                await PumpUntilAsync(() => context.Node(StoryFile).Status == ChapterStatus.Drafting);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Turning_it_on_saves_unsaved_prose_of_the_open_story_with_the_block()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                context.Editor.Text = StoryText + "\nA gull cries.\n";
                Assert.True(context.Editor.IsDirty);

                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                var split = ReadStory(root);
                Assert.Equal(StoryText + "\nA gull cries.\n", split.Prose);
                Assert.Equal(node.Uuid, split.Metadata?.Id);
                Assert.False(context.Editor.IsDirty);
                Assert.DoesNotContain("hymnal:v1", context.Editor.Text);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Notes_typed_just_before_turning_it_on_move_with_the_rest()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var notes = context.CreateNotes();
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                notes.Text = "Typed just before.";

                var settings = context.CreateSettings(notes.FlushAsync);
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal("Typed just before.", ReadStory(root).Metadata?.Notes);
                Assert.False(File.Exists(NotesPath(root)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Schedule_edited_just_before_turning_it_on_moves_with_the_rest()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var info = context.CreateChapterInfo();
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                await PumpUntilAsync(() => info.ChapterTitle != null);
                info.PhaseScheduleRows.First(r => r.PhaseName == "Drafting").StartDate = "2026-11-01";

                var settings = context.CreateSettings(info.FlushScheduleAsync);
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal("2026-11-01", ReadStory(root).Metadata?.Schedule?["Drafting"].StartDate);
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Failures ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_error_is_shown_and_project_json_is_unchanged()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        await File.WriteAllBytesAsync(Path.Combine(root, StoryFile), [0x43, 0x61, 0x66, 0xE9, 0x0A]); // Latin-1
        try
        {
            await Ui(root, 1, async context =>
            {
                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Contains(context.Errors, e => e.Contains("UTF-8"));
                Assert.Equal(StoryJson, await File.ReadAllTextAsync(ProjectJsonPath(root)));
                Assert.Equal(StorageMode.Sidecar, context.Workspace.Profile.Storage);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Pending_edits_that_cannot_be_saved_stop_the_migration()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                await new NotesService(new MetadataStore()).SaveAsync(NotesPath(root), "Remember the lamp.");
                var settings = context.CreateSettings(() => Task.FromResult(false));
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Contains("Pending edits couldn't be saved, so the story's data was not moved.", context.Errors);
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
                Assert.Equal("Remember the lamp.", await File.ReadAllTextAsync(NotesPath(root)));
                Assert.Equal(StoryJson, await File.ReadAllTextAsync(ProjectJsonPath(root)));
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Leaving Short Story while embedded (Ruling 2) ─────────────────────────

    [Fact]
    public async Task Switching_to_anthology_moves_the_data_out_first_then_moves_the_board()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, WithBlock(StoryText,
            new EmbeddedStoryMetadata(null, ChapterStatus.Editing, null, null, null, null, "Lamp.",
                [new Beat("b1", "Hook", "", null)]))));
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                var settings = context.CreateSettings();
                settings.SelectedProjectKind = ProjectKind.Anthology;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
                Assert.Equal(StorageMode.Sidecar, context.Workspace.Profile.Storage);
                var json = await File.ReadAllTextAsync(ProjectJsonPath(root));
                Assert.Contains("\"anthology\"", json);
                Assert.Contains("\"sidecar\"", json);
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
                Assert.Equal("Hook", Assert.Single(await LoadFromDiskAsync(root, uuid)).Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal("Lamp.", await File.ReadAllTextAsync(NotesPath(root)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_kind_is_abandoned_when_the_data_cannot_move_out()
    {
        var damaged = StoryText + "\n<!-- hymnal:v1\n{ not json\n-->\n";
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, damaged));
        try
        {
            await Ui(root, 1, async context =>
            {
                var settings = context.CreateSettings();
                settings.SelectedProjectKind = ProjectKind.Novel;
                await ApplyAsync(context, settings);

                Assert.Contains(context.Errors, e => e.Contains("could not be read"));
                Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
                Assert.Equal(EmbeddedJson, await File.ReadAllTextAsync(ProjectJsonPath(root)));
                Assert.Equal(damaged, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Ruling 9: commit point, best-effort cleanup ───────────────────────────

    [Fact]
    public async Task Cleanup_failing_after_the_phase_entry_still_switches_with_all_data_visible()
    {
        var store = new HookStore();
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                await SeedSidecarAsync(root, uuid);
                // Removing the target from targets.json fails; the phase entry before it was already removed.
                store.OnWrite = path => path.EndsWith("targets.json", StringComparison.Ordinal)
                    ? throw new IOException("The disk is full.")
                    : Task.CompletedTask;

                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Contains(SettingsViewModel.CleanupLeftoversMessage, context.Infos);
                Assert.Contains("\"embedded\"", await File.ReadAllTextAsync(ProjectJsonPath(root)));
                Assert.Equal(StorageMode.Embedded, context.Workspace.Profile.Storage);
                await PumpUntilAsync(() => context.Node(StoryFile).Status == ChapterStatus.Editing);
                Assert.Equal(7500, context.Node(StoryFile).Target?.MaxWords);
                var story = context.Node(StoryFile).Story;
                Assert.Equal("Remember the lamp.", await context.Workspace.MetadataStore.LoadNotesAsync(root, story));
                Assert.False(File.Exists(NotesPath(root)));

                // Turning it off again mirrors the block (the source of truth) over the leftover: nothing is erased.
                store.OnWrite = null;
                var off = context.CreateSettings();
                off.KeepInStoryFile = false;
                await ApplyAsync(context, off);

                Assert.Empty(context.Errors);
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
                Assert.Equal(ChapterStatus.Editing, (await new PhaseDataService(new MetadataStore()).LoadAsync(root))[uuid].Status);
                Assert.Equal(7500, (await new TargetsService(new MetadataStore()).LoadAsync(root))[uuid].MaxWords);
                Assert.Equal("Remember the lamp.", await File.ReadAllTextAsync(NotesPath(root)));
                Assert.Equal("Hook", Assert.Single(await LoadFromDiskAsync(root, Story)).Title);
            }, store: store);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_to_story_in_the_file_carries_the_story_s_board_into_the_block_and_back()
    {
        var root = CreateWorkspace(AnthologyJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                await SeedAsync(root, uuid, new Beat("b1", "Anthology beat", "", null));

                var settings = context.CreateSettings();
                settings.SelectedProjectKind = ProjectKind.Story;
                Assert.True(settings.IsStorageOptionVisible);
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
                Assert.Equal(StorageMode.Embedded, context.Workspace.Profile.Storage);
                Assert.Equal("Anthology beat", Assert.Single(ReadStory(root).Metadata!.Beats!).Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, uuid)));
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));

                var off = context.CreateSettings();
                off.KeepInStoryFile = false;
                await ApplyAsync(context, off);

                Assert.Empty(context.Errors);
                Assert.Equal("Anthology beat", Assert.Single(await LoadFromDiskAsync(root, Story)).Title);
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_failed_reload_after_saving_never_writes_to_the_old_location()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var story = context.Node(StoryFile).Story;
                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                // The reload can't find Book.txt.
                File.Delete(Path.Combine(root, "Book.txt"));
                await ApplyAsync(context, settings);

                Assert.Contains(context.Errors, e => e.Contains("reopen the project"));
                Assert.Contains("\"embedded\"", await File.ReadAllTextAsync(ProjectJsonPath(root)));
                await Assert.ThrowsAsync<IOException>(() =>
                    context.Workspace.MetadataStore.SaveNotesAsync(root, story, "Typed after."));
                Assert.False(File.Exists(NotesPath(root)));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notes_saved_while_the_data_moves_are_refused_never_lost_to_cleanup(bool intoTheFile)
    {
        var store = new HookStore();
        var root = CreateWorkspace(intoTheFile ? StoryJson : EmbeddedJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var story = context.Node(StoryFile).Story;
                Exception? refused = null;
                var saved = false;
                // Mid-move: while the block is written into the file, or while project.json commits the move out.
                var destination = intoTheFile ? Path.Combine(root, StoryFile) : ProjectJsonPath(root);
                store.OnWrite = async path =>
                {
                    if (saved || path != destination) return;
                    saved = true;
                    try { await context.Workspace.MetadataStore.SaveNotesAsync(root, story, "Typed during apply."); }
                    catch (IOException ex) { refused = ex; }
                };

                var settings = context.CreateSettings();
                settings.KeepInStoryFile = intoTheFile;
                await ApplyAsync(context, settings);

                Assert.True(saved);
                Assert.Equal(intoTheFile ? StorageMode.Embedded : StorageMode.Sidecar, context.Workspace.Profile.Storage);
                var kept = await context.Workspace.MetadataStore.LoadNotesAsync(root, context.Node(StoryFile).Story);
                if (refused is null)
                    Assert.Equal("Typed during apply.", kept);
                else
                    Assert.Equal(SettingsViewModel.MovingStoryDataMessage, refused.Message);

                // Once reloaded, edits are accepted again.
                await context.Workspace.MetadataStore.SaveNotesAsync(root, context.Node(StoryFile).Story, "After.");
                Assert.Equal("After.", await context.Workspace.MetadataStore.LoadNotesAsync(root, context.Node(StoryFile).Story));
            }, store: store);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_save_that_reaches_the_store_during_the_final_flush_is_refused_or_moved_never_lost_to_cleanup()
    {
        var store = new HookStore();
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var story = context.Node(StoryFile).Story;
                IStoryMetadataStore? seenByOtherSave = null;
                Exception? refused = null;
                var flushes = 0;
                var settings = context.CreateSettings(async () =>
                {
                    // The final flush (after the board move) saves its own edit; meanwhile a debounced save on
                    // another flow reaches the store and is still writing after the destination was read.
                    if (++flushes == 2)
                    {
                        await context.Workspace.MetadataStore.SaveNotesAsync(root, story, "Flushed.");
                        seenByOtherSave = context.Workspace.MetadataStore;
                    }
                    return true;
                });
                store.OnWrite = async path =>
                {
                    if (seenByOtherSave is not { } other || path != Path.Combine(root, StoryFile)) return;
                    seenByOtherSave = null;
                    try { await InAnotherFlow(() => other.SaveNotesAsync(root, story, "Other save.")); }
                    catch (IOException ex) { refused = ex; }
                };

                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Equal(2, flushes);
                Assert.Empty(context.Errors);
                Assert.Equal(StorageMode.Embedded, context.Workspace.Profile.Storage);
                var kept = ReadStory(root).Metadata?.Notes;
                if (refused is null)
                    Assert.Equal("Other save.", kept);
                else
                {
                    Assert.Equal(SettingsViewModel.MovingStoryDataMessage, refused.Message);
                    Assert.Equal("Flushed.", kept);
                }
            }, store: store);
        }
        finally { DeleteFolder(root); }

        static Task InAnotherFlow(Func<Task> save)
        {
            using (System.Threading.ExecutionContext.SuppressFlow())
                return Task.Run(save);
        }
    }

    [Fact]
    public async Task A_failure_after_the_commit_points_the_store_at_the_new_mode_and_asks_for_a_reopen()
    {
        BoardsThrowingOnMove? boards = null;
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, WithBlock(StoryText,
            new EmbeddedStoryMetadata(null, null, null, null, null, null, "In the block.", [new Beat("b1", "Hook", "", null)]))));
        try
        {
            await Ui(root, 1, async context =>
            {
                var chapter = context.Node(StoryFile);
                var settings = context.CreateSettings();
                settings.SelectedProjectKind = ProjectKind.Anthology;
                // Moving the board for the new kind runs after project.json is saved in the new mode.
                boards!.ThrowOnMove = true;
                await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(context, settings));
                await context.SettleAsync();

                Assert.Contains(context.Errors, e => e.StartsWith("Project change failed", StringComparison.Ordinal));
                Assert.Contains("\"anthology\"", await File.ReadAllTextAsync(ProjectJsonPath(root)));
                Assert.True(context.Workspace.NeedsReopen);
                Assert.IsNotType<RefusingStoryMetadataStore>(context.Workspace.MetadataStore);
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
                Assert.Equal("In the block.", await File.ReadAllTextAsync(NotesPath(root)));

                // Sidebar edits go to the new mode's location, never back into the story file.
                await chapter.ChangeStatusCommand.Execute(ChapterStatus.Polishing).ToTask();
                await context.SettleAsync();
                Assert.Equal(StoryText, await File.ReadAllTextAsync(Path.Combine(root, StoryFile)));
                Assert.Equal(ChapterStatus.Polishing,
                    (await new PhaseDataService(new MetadataStore()).LoadAsync(root))[chapter.Uuid].Status);
            }, boards: store => boards = new BoardsThrowingOnMove(new BeatBoardService(store)));
        }
        finally { DeleteFolder(root); }
    }

    /// <summary>The real board service, whose <see cref="MoveBoardAsync"/> throws once <see cref="ThrowOnMove"/> is set.</summary>
    private sealed class BoardsThrowingOnMove(BeatBoardService inner) : IBeatBoardService
    {
        public bool ThrowOnMove { get; set; }

        public Task<Hymnal.Core.Common.Result<System.Collections.Generic.IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey) =>
            inner.LoadAsync(workspaceRoot, boardKey);

        public Task<Hymnal.Core.Common.Result<Hymnal.Core.Common.Unit>> SaveAsync(
            string workspaceRoot, string boardKey, System.Collections.Generic.IReadOnlyList<Beat> beats) =>
            inner.SaveAsync(workspaceRoot, boardKey, beats);

        public bool BoardExists(string workspaceRoot, string boardKey) => inner.BoardExists(workspaceRoot, boardKey);

        public Task<Hymnal.Core.Common.Result<Hymnal.Core.Common.Unit>> MoveBoardAsync(string workspaceRoot, string fromKey, string toKey) =>
            ThrowOnMove ? throw new InvalidOperationException("The board move broke.") : inner.MoveBoardAsync(workspaceRoot, fromKey, toKey);

        public Task<Hymnal.Core.Common.Result<Hymnal.Core.Common.Unit>> DeleteBoardAsync(string workspaceRoot, string boardKey) =>
            inner.DeleteBoardAsync(workspaceRoot, boardKey);
    }

    [Fact]
    public async Task Sidebar_status_and_target_edits_after_a_failed_reload_never_reach_project_data()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var chapter = context.Node(StoryFile);
                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                File.Delete(Path.Combine(root, "Book.txt"));
                await ApplyAsync(context, settings);
                Assert.Contains("\"embedded\"", await File.ReadAllTextAsync(ProjectJsonPath(root)));
                var errorsBefore = context.Errors.Count();

                await RunIgnoringErrorAsync(chapter.ChangeStatusCommand.Execute(ChapterStatus.Polishing).ToTask());
                await RunIgnoringErrorAsync(chapter.SetTargetCommand
                    .Execute(new WordCountTarget { MinWords = 100, MaxWords = 200 }).ToTask());
                await context.SettleAsync();

                Assert.True(context.Errors.Count() >= errorsBefore + 2);
                Assert.DoesNotContain(chapter.Uuid, ReadIfExists(Path.Combine(root, ".hymnal-data", "phases.json")));
                Assert.DoesNotContain(chapter.Uuid, ReadIfExists(Path.Combine(root, ".hymnal-data", "targets.json")));
            });
        }
        finally { DeleteFolder(root); }

        static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

        static async Task RunIgnoringErrorAsync(Task run)
        {
            try { await run; }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task After_a_failed_reload_nothing_can_be_applied_until_reopened_and_the_block_keeps_its_data()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        var bookTxt = Path.Combine(root, "Book.txt");
        try
        {
            await Ui(root, 1, async context =>
            {
                await SeedSidecarAsync(root, context.Node(StoryFile).Uuid);
                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                var book = await File.ReadAllTextAsync(bookTxt);
                File.Delete(bookTxt);
                await ApplyAsync(context, settings);
                Assert.Equal("Remember the lamp.", ReadStory(root).Metadata?.Notes);

                // Book.txt is back, but the session still shows the old (sidecar) profile.
                await File.WriteAllTextAsync(bookTxt, book);
                var again = context.CreateSettings();
                Assert.True(context.Workspace.NeedsReopen);
                Assert.Equal(SettingsViewModel.ReopenRequired, again.ReopenRequiredMessage);
                Assert.False(again.IsStorageOptionEnabled);
                again.KeepInStoryFile = true;
                Assert.False(await again.ApplyProjectKindCommand.CanExecute.FirstAsync());
                again.SelectedProjectKind = ProjectKind.Novel;
                Assert.False(await again.ApplyProjectKindCommand.CanExecute.FirstAsync());
                Assert.Equal("Remember the lamp.", ReadStory(root).Metadata?.Notes);

                await context.OpenWorkspaceInCurrentDispatchAsync();
                await PumpUntilAsync(() => context.Workspace.Nodes.Count == 1);
                Assert.False(context.Workspace.NeedsReopen);
                Assert.Equal(StorageMode.Embedded, context.Workspace.Profile.Storage);
                Assert.Null(context.CreateSettings().ReopenRequiredMessage);
                Assert.Equal("Remember the lamp.",
                    await context.Workspace.MetadataStore.LoadNotesAsync(root, context.Node(StoryFile).Story));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Apply_takes_the_current_storage_from_project_json_not_a_stale_profile()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, WithBlock(StoryText,
            new EmbeddedStoryMetadata(null, null, null, null, null, null, "In the block.", null))));
        try
        {
            await Ui(root, 1, async context =>
            {
                // project.json already says embedded, but the session still has the sidecar profile.
                await File.WriteAllTextAsync(ProjectJsonPath(root), EmbeddedJson);
                Assert.Equal(StorageMode.Sidecar, context.Workspace.Profile.Storage);

                var settings = context.CreateSettings();
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Empty(context.Errors);
                Assert.Equal("In the block.", ReadStory(root).Metadata?.Notes);
                Assert.Equal(StorageMode.Embedded, context.Workspace.Profile.Storage);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_board_move_is_undone_when_the_block_cannot_be_written()
    {
        var root = CreateWorkspace(AnthologyJson, (StoryFile, StoryText));
        await File.WriteAllBytesAsync(Path.Combine(root, StoryFile), [0x43, 0x61, 0x66, 0xE9, 0x0A]); // Latin-1
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                await SeedAsync(root, uuid, new Beat("b1", "Anthology beat", "", null));

                var settings = context.CreateSettings();
                settings.SelectedProjectKind = ProjectKind.Story;
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.Contains(context.Errors, e => e.Contains("UTF-8"));
                Assert.Equal("Anthology beat", Assert.Single(await LoadFromDiskAsync(root, uuid)).Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal(AnthologyJson, await File.ReadAllTextAsync(ProjectJsonPath(root)));
                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_board_move_is_undone_when_project_json_cannot_be_saved()
    {
        var store = new HookStore();
        var root = CreateWorkspace(AnthologyJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var uuid = context.Node(StoryFile).Uuid;
                await SeedAsync(root, uuid, new Beat("b1", "Anthology beat", "", null));
                store.OnWrite = path => path == ProjectJsonPath(root)
                    ? throw new IOException("The disk is full.")
                    : Task.CompletedTask;

                var settings = context.CreateSettings();
                settings.SelectedProjectKind = ProjectKind.Story;
                settings.KeepInStoryFile = true;
                await ApplyAsync(context, settings);

                Assert.NotEmpty(context.Errors);
                Assert.Equal("Anthology beat", Assert.Single(await LoadFromDiskAsync(root, uuid)).Title);
                Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, Story)));
                Assert.Equal(AnthologyJson, await File.ReadAllTextAsync(ProjectJsonPath(root)));
                Assert.Equal(ProjectKind.Anthology, context.Workspace.Profile.Kind);
            }, store: store);
        }
        finally { DeleteFolder(root); }
    }

    // ── Flushes wait for a save already writing (minor 3) ─────────────────────

    [Fact]
    public async Task Notes_flush_waits_for_a_debounced_save_already_writing()
    {
        var store = new HookStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var notes = context.CreateNotes();
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                store.OnWrite = path =>
                {
                    if (path != NotesPath(root)) return Task.CompletedTask;
                    started.TrySetResult();
                    return release.Task;
                };
                notes.Text = "Typed.";
                await Within(started.Task);

                var flush = notes.FlushAsync();
                await context.SettleAsync();
                Assert.False(flush.IsCompleted);

                release.SetResult();
                Assert.True(await Within(flush));
                Assert.Equal("Typed.", await File.ReadAllTextAsync(NotesPath(root)));
            }, store: store);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Schedule_flush_waits_for_a_save_already_writing()
    {
        var store = new HookStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var info = await OpenChapterInfoAsync(context, root);
                store.OnWrite = path =>
                {
                    if (!path.EndsWith("phases.json", StringComparison.Ordinal)) return Task.CompletedTask;
                    started.TrySetResult();
                    return release.Task;
                };
                info.PhaseScheduleRows.First(r => r.PhaseName == "Drafting").StartDate = "2026-11-01";
                await Within(started.Task);

                var flush = info.FlushScheduleAsync();
                await context.SettleAsync();
                Assert.False(flush.IsCompleted);

                release.SetResult();
                Assert.True(await Within(flush));
                Assert.Contains("2026-11-01", await File.ReadAllTextAsync(Path.Combine(root, ".hymnal-data", "phases.json")));
            }, store: store);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Schedule_flush_reports_a_failed_debounced_save()
    {
        var store = new HookStore();
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var info = await OpenChapterInfoAsync(context, root);
                store.OnWrite = path => path.EndsWith("phases.json", StringComparison.Ordinal)
                    ? throw new IOException("The disk is full.")
                    : Task.CompletedTask;
                info.PhaseScheduleRows.First(r => r.PhaseName == "Drafting").StartDate = "2026-11-01";
                await PumpUntilAsync(() => context.Errors.Any(e => e.Contains("Schedule save failed")));

                Assert.False(await Within(info.FlushScheduleAsync()));
            }, store: store);
        }
        finally { DeleteFolder(root); }
    }

    private static async Task<ChapterInfoViewModel> OpenChapterInfoAsync(PlanTestContext context, string root)
    {
        var info = context.CreateChapterInfo();
        var node = context.Node(StoryFile);
        await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
        await PumpUntilAsync(() => info.ChapterTitle != null);
        return info;
    }

    private static async Task SeedSidecarAsync(string root, string uuid)
    {
        var store = new MetadataStore();
        await new PhaseDataService(store).UpsertAsync(root, uuid, _ => new PhaseData { Status = ChapterStatus.Editing });
        await new TargetsService(store).UpsertAsync(root, uuid, new WordCountTarget { MinWords = 1000, MaxWords = 7500 });
        await new NotesService(store).SaveAsync(NotesPath(root), "Remember the lamp.");
        await SeedAsync(root, Story, new Beat("b1", "Hook", "", null));
    }

    /// <summary>The real store, running <see cref="OnWrite"/> (which may wait or throw) before each write.</summary>
    private sealed class HookStore : IMetadataStore
    {
        private readonly MetadataStore _inner = new();
        public Func<string, Task>? OnWrite { get; set; }

        public async Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            if (OnWrite is { } hook)
                await hook(absolutePath);
            await _inner.WriteTextAtomicAsync(absolutePath, content);
        }
    }

    private static async Task ApplyAsync(PlanTestContext context, SettingsViewModel settings)
    {
        Assert.True(await settings.ApplyProjectKindCommand.CanExecute.FirstAsync());
        var apply = settings.ApplyProjectKindCommand.Execute().ToTask();
        await PumpUntilAsync(() => apply.IsCompleted);
        await apply;
        await context.SettleAsync();
    }
}

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

    private static async Task ApplyAsync(PlanTestContext context, SettingsViewModel settings)
    {
        Assert.True(await settings.ApplyProjectKindCommand.CanExecute.FirstAsync());
        var apply = settings.ApplyProjectKindCommand.Execute().ToTask();
        await PumpUntilAsync(() => apply.IsCompleted);
        await apply;
        await context.SettleAsync();
    }
}

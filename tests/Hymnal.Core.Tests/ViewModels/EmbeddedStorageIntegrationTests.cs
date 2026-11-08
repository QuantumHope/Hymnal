using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Xunit;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>
/// End-to-end routing of per-story data through the workspace's story metadata store: a single-file Short
/// Story with embedded storage keeps its status, target, notes and story board in its file's block; sidecar
/// storage keeps writing the same sidecar files as before.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class EmbeddedStorageIntegrationTests
{
    static EmbeddedStorageIntegrationTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string EmbeddedJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"embedded\" }";
    private const string StoryFile = "story.md";
    private const string StoryText = "# The Lighthouse\n\nThe keeper climbs the stairs at dusk.\n";
    private const string MultiFileNotice =
        "Keep everything in the story file only covers single-file stories; other files use project data.";

    private static string PhasesPath(string root) => Path.Combine(root, ".hymnal-data", "phases.json");
    private static string TargetsPath(string root) => Path.Combine(root, ".hymnal-data", "targets.json");
    private static string NotesPath(string root, string file) => INotesService.DeriveNotesPath(root, file);

    private static StorySplit ReadStory(string root, string file = StoryFile) =>
        StoryFileFormat.Split(File.ReadAllText(Path.Combine(root, file)));

    private static bool PhasesHave(string root, string uuid) =>
        File.Exists(PhasesPath(root)) && File.ReadAllText(PhasesPath(root)).Contains(uuid, StringComparison.Ordinal);

    // ── Embedded: each kind of data lands in the block and survives a reopen ──

    [Fact]
    public async Task Status_change_from_sidebar_writes_the_block_and_no_phases_entry()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            string uuid = "";
            await Ui(root, 1, async context =>
            {
                var node = context.Node(StoryFile);
                uuid = node.Uuid;
                await Within(node.ChangeStatusCommand.Execute(ChapterStatus.Drafting).ToTask());
                Assert.Empty(context.Errors);
            });

            var split = ReadStory(root);
            Assert.Equal(StoryText, split.Prose);
            Assert.Equal(ChapterStatus.Drafting, split.Metadata?.Status);
            Assert.Equal(uuid, split.Metadata?.Id);
            Assert.False(PhasesHave(root, uuid));

            await Ui(root, 1, context =>
            {
                Assert.Equal(ChapterStatus.Drafting, context.Node(StoryFile).Status);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Target_set_writes_the_block_and_reloads()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            var target = new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" };
            await Ui(root, 1, async context =>
            {
                await Within(context.Node(StoryFile).SetTargetCommand.Execute(target).ToTask());
                Assert.Empty(context.Errors);
            });

            var split = ReadStory(root);
            Assert.Equal(StoryText, split.Prose);
            Assert.Equal(7500, split.Metadata?.Target?.MaxWords);
            Assert.Equal("shortStory", split.Metadata?.Target?.Preset);
            Assert.False(File.Exists(TargetsPath(root)));

            await Ui(root, 1, context =>
            {
                var reloaded = context.Node(StoryFile).Target;
                Assert.Equal(1000, reloaded?.MinWords);
                Assert.Equal(7500, reloaded?.MaxWords);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Notes_typed_for_the_open_story_go_to_the_block_without_dirtying_the_editor()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var notes = context.CreateNotes();
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));

                notes.Text = "Remember the lamp.";
                await PumpUntilAsync(() => ReadStory(root).Metadata?.Notes == "Remember the lamp.");

                Assert.False(context.Editor.IsDirty);
                Assert.Equal(StoryText, context.Editor.Text);
                Assert.DoesNotContain("hymnal:v1", context.Editor.Text);
                Assert.Empty(context.Errors);
            });

            Assert.Equal(StoryText, ReadStory(root).Prose);
            Assert.False(File.Exists(NotesPath(root, StoryFile)));

            await Ui(root, 1, async context =>
            {
                var notes = context.CreateNotes();
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                await PumpUntilAsync(() => notes.Text == "Remember the lamp.");
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Beats_edited_go_to_the_block_and_reload()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                await Within(context.Board.OpenBoardAsync(BeatBoardService.ShortStoryBoardKey, "The Story", [StoryFile]));
                await Within(context.Board.AddBeatAsync());
                Assert.True(await Within(context.Board.FlushAsync()));
                Assert.Empty(context.Errors);
            });

            var split = ReadStory(root);
            Assert.Equal(StoryText, split.Prose);
            var beat = Assert.Single(split.Metadata?.Beats ?? []);
            Assert.False(File.Exists(BeatBoardService.BoardFilePath(root, BeatBoardService.ShortStoryBoardKey)));

            await Ui(root, 1, async context =>
            {
                await Within(context.Board.OpenBoardAsync(BeatBoardService.ShortStoryBoardKey, "The Story", [StoryFile]));
                Assert.Equal(beat.Id, Assert.Single(context.Board.Beats).Id);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Status_change_while_the_story_is_open_keeps_its_unsaved_prose()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            const string edited = StoryText + "\nA new paragraph, not yet saved.\n";
            await Ui(root, 1, async context =>
            {
                var node = context.Node(StoryFile);
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                context.Editor.Text = edited;
                Assert.True(context.Editor.IsDirty);

                await Within(node.ChangeStatusCommand.Execute(ChapterStatus.Editing).ToTask());

                Assert.Equal(edited, context.Editor.Text);
                Assert.False(context.Editor.IsDirty);
                Assert.Empty(context.Errors);
            });

            var split = ReadStory(root);
            Assert.Equal(edited, split.Prose);
            Assert.Equal(ChapterStatus.Editing, split.Metadata?.Status);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Chapter_info_and_gantt_edits_write_the_block()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, async context =>
            {
                var node = context.Node(StoryFile);
                var info = context.CreateChapterInfo();
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                await PumpUntilAsync(() => info.ChapterTitle == node.Node.Title);
                await Within(info.SetStatusCommand.Execute(ChapterStatus.Drafting).ToTask());
                Assert.Equal(ChapterStatus.Drafting, ReadStory(root).Metadata?.Status);

                var gantt = context.CreateGantt();
                var row = gantt.Rows.First(r => r.IsChapter && r.RelativePath == StoryFile);
                row.EditableStartDate = new DateTime(2026, 3, 1);
                await Within(gantt.SaveInlineCellAsync(row, GanttEditableColumn.StartDate));
                Assert.Empty(context.Errors);
            });

            var block = ReadStory(root).Metadata;
            Assert.Equal(ChapterStatus.Drafting, block?.Status);
            Assert.Equal("2026-03-01", block?.Schedule?["Drafting"].StartDate);
            Assert.Equal(StoryText, ReadStory(root).Prose);
            Assert.False(File.Exists(PhasesPath(root)));
        }
        finally { DeleteFolder(root); }
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renaming_the_file_outside_Hymnal_keeps_its_data_through_the_block_id()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            string uuid = "";
            await Ui(root, 1, async context =>
            {
                var node = context.Node(StoryFile);
                uuid = node.Uuid;
                await Within(node.ChangeStatusCommand.Execute(ChapterStatus.Editing).ToTask());
            });

            // Renamed and retitled outside Hymnal, so nothing but the block links it to its registry entry.
            var renamed = "renamed.md";
            var text = File.ReadAllText(Path.Combine(root, StoryFile)).Replace("# The Lighthouse", "# The Keeper");
            File.WriteAllText(Path.Combine(root, renamed), text);
            File.Delete(Path.Combine(root, StoryFile));
            File.WriteAllText(Path.Combine(root, "Book.txt"), renamed + "\n");

            await Ui(root, 1, context =>
            {
                var node = context.Node(renamed);
                Assert.Equal(ChapterStatus.Editing, node.Status);
                Assert.Equal(uuid, node.Uuid);
                return Task.CompletedTask;
            });

            var registry = await new ChapterRegistryService(new MetadataStore()).LoadAsync(root);
            Assert.Equal(renamed, registry[uuid].CurrentPath);
            Assert.False(registry[uuid].Orphaned);
            Assert.Single(registry.Values, e => e.CurrentPath == renamed);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Block_id_that_differs_from_the_registry_is_adopted()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            string registryUuid = "";
            await Ui(root, 1, context =>
            {
                registryUuid = context.Node(StoryFile).Uuid;
                return Task.CompletedTask;
            });

            const string blockId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
            var block = new EmbeddedStoryMetadata(blockId, ChapterStatus.Done, null, null, null, null, null, null);
            File.WriteAllText(Path.Combine(root, StoryFile), StoryFileFormat.Merge(StoryText, block, "\n"));

            await Ui(root, 1, context =>
            {
                var node = context.Node(StoryFile);
                Assert.Equal(blockId, node.Uuid);
                Assert.Equal(ChapterStatus.Done, node.Status);
                return Task.CompletedTask;
            });

            var registry = await new ChapterRegistryService(new MetadataStore()).LoadAsync(root);
            Assert.Equal(StoryFile, registry[blockId].CurrentPath);
            Assert.DoesNotContain(registryUuid, registry.Keys);
        }
        finally { DeleteFolder(root); }
    }

    // ── More than one file ───────────────────────────────────────────────────

    [Fact]
    public async Task Second_file_shows_the_notice_once_and_its_status_goes_to_the_sidecar()
    {
        const string second = "second.md";
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText), (second, "# Second\n\nMore.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                Assert.Single(context.Infos, i => i == MultiFileNotice);

                var reload = await Within(context.Workspace.ReloadCurrentWorkspaceAsync());
                Assert.True(reload.IsSuccess, reload.Error);
                await context.SettleAsync();
                Assert.Single(context.Infos, i => i == MultiFileNotice);

                var primary = context.Node(StoryFile);
                var other = context.Node(second);
                await Within(primary.ChangeStatusCommand.Execute(ChapterStatus.Drafting).ToTask());
                await Within(other.ChangeStatusCommand.Execute(ChapterStatus.Editing).ToTask());

                Assert.Equal(ChapterStatus.Drafting, ReadStory(root).Metadata?.Status);
                Assert.Null(ReadStory(root, second).Metadata);
                Assert.Equal("# Second\n\nMore.\n", File.ReadAllText(Path.Combine(root, second)));
                Assert.True(PhasesHave(root, other.Uuid));
                Assert.False(PhasesHave(root, primary.Uuid));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Single_file_story_shows_no_notice()
    {
        var root = CreateWorkspace(EmbeddedJson, (StoryFile, StoryText));
        try
        {
            await Ui(root, 1, context =>
            {
                Assert.DoesNotContain(MultiFileNotice, context.Infos);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Sidecar regression: the same flows write the same sidecar files as before ──

    [Fact]
    public async Task Sidecar_storage_writes_the_same_sidecar_files_as_the_services_and_leaves_the_story_alone()
    {
        var root = CreateWorkspace(StoryJson, (StoryFile, StoryText));
        var reference = Path.Combine(Path.GetTempPath(), $"hymnal-sidecar-ref-{Guid.NewGuid():N}");
        try
        {
            var target = new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" };
            string uuid = "";
            PhaseData? phase = null;
            IReadOnlyList<Beat> beats = [];
            await Ui(root, 1, async context =>
            {
                var node = context.Node(StoryFile);
                uuid = node.Uuid;
                await Within(node.ChangeStatusCommand.Execute(ChapterStatus.Drafting).ToTask());
                phase = node.PhaseData;
                await Within(node.SetTargetCommand.Execute(target).ToTask());

                var notes = context.CreateNotes();
                await Within(context.Editor.OpenChapterAsync(node.Node, Path.Combine(root, StoryFile)));
                notes.Text = "Remember the lamp.";
                await PumpUntilAsync(() => File.Exists(NotesPath(root, StoryFile)));

                await Within(context.Board.OpenBoardAsync(BeatBoardService.ShortStoryBoardKey, "The Story", [StoryFile]));
                await Within(context.Board.AddBeatAsync());
                Assert.True(await Within(context.Board.FlushAsync()));
                beats = context.Board.Beats.Select(b => b.ToBeat()).ToList();
                Assert.Empty(context.Errors);
            });

            Assert.Equal(StoryText, File.ReadAllText(Path.Combine(root, StoryFile)));

            // The sidecar files are byte-for-byte what the sidecar services write for the same data.
            var store = new MetadataStore();
            Directory.CreateDirectory(Path.Combine(reference, ".hymnal-data"));
            await new PhaseDataService(store).UpsertAsync(reference, uuid, _ => phase!);
            await new TargetsService(store).UpsertAsync(reference, uuid, target);
            await new NotesService(store).SaveAsync(NotesPath(reference, StoryFile), "Remember the lamp.");
            Assert.True((await new BeatBoardService(store).SaveAsync(reference, BeatBoardService.ShortStoryBoardKey, beats)).IsSuccess);

            Assert.Equal(File.ReadAllBytes(PhasesPath(reference)), File.ReadAllBytes(PhasesPath(root)));
            Assert.Equal(File.ReadAllBytes(TargetsPath(reference)), File.ReadAllBytes(TargetsPath(root)));
            Assert.Equal(File.ReadAllBytes(NotesPath(reference, StoryFile)), File.ReadAllBytes(NotesPath(root, StoryFile)));
            Assert.Equal(
                File.ReadAllBytes(BeatBoardService.BoardFilePath(reference, BeatBoardService.ShortStoryBoardKey)),
                File.ReadAllBytes(BeatBoardService.BoardFilePath(root, BeatBoardService.ShortStoryBoardKey)));
        }
        finally
        {
            DeleteFolder(root);
            DeleteFolder(reference);
        }
    }
}

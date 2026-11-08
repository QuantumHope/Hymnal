using System.Text;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class StoryMetadataStoreTests
{
    private const string Prose = "# The Story\r\n\r\nOnce upon a time.";

    private static async Task WithTempDirAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-storystore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static SidecarStoryMetadataStore NewSidecar()
    {
        var store = new MetadataStore();
        return new SidecarStoryMetadataStore(
            new PhaseDataService(store), new TargetsService(store), new NotesService(store), new BeatBoardService(store));
    }

    private static EmbeddedStoryMetadataStore NewEmbedded(StoryRef? primary)
    {
        var store = new EmbeddedStoryMetadataStore(NewSidecar(), new StoryFileWriter(new MetadataStore()));
        if (primary is not null)
            store.SetPrimaryStory(primary);
        return store;
    }

    private static StoryRef WriteStory(string root, string relativePath, string content, string? uuid = null)
    {
        var absolute = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return new StoryRef(uuid ?? Guid.NewGuid().ToString(), relativePath, absolute);
    }

    private static string Sidecar(string root, params string[] parts) =>
        Path.Combine([root, ".hymnal-data", .. parts]);

    private static EmbeddedStoryMetadata? BlockOf(StoryRef story) =>
        StoryFileFormat.Split(File.ReadAllText(story.AbsolutePath)).Metadata;

    private static string Block(string json) => "Prose.\n\n<!-- hymnal:v1\n" + json + "\n-->\n";

    private static readonly Dictionary<string, PhaseScheduleEntry> SampleSchedule = new()
    {
        ["Drafting"] = new PhaseScheduleEntry { StartDate = "2026-11-01", EndDate = "2026-11-20", Progress = 40 }
    };

    private static PhaseData SamplePhaseData() => new()
    {
        Status = ChapterStatus.Drafting,
        PhaseStartDate = "2026-11-01",
        PhaseEndDate = "2026-11-30",
        Schedule = SampleSchedule
    };

    private static readonly WordCountTarget SampleTarget = new() { Preset = "shortStory", MinWords = 1000, MaxWords = 7500 };

    private static readonly IReadOnlyList<Beat> SampleBeats =
    [
        new Beat("b1", "Hook", "Open strong", null),
        new Beat("b2", "Turn", "", "story.md", "scene-uuid")
    ];

    // ── Sidecar: byte-for-byte equal to today's services ─────────────────────

    [Fact]
    public Task Sidecar_writes_the_same_files_as_the_raw_services() => WithTempDirAsync(async dir =>
    {
        var viaStore = Path.Combine(dir, "store");
        var viaServices = Path.Combine(dir, "services");
        var story = new StoryRef("uuid-1", "manuscript/ch1.md", Path.Combine(viaStore, "manuscript", "ch1.md"));

        var sidecar = NewSidecar();
        await sidecar.UpsertPhaseDataAsync(viaStore, story, _ => SamplePhaseData());
        await sidecar.SetTargetAsync(viaStore, story, SampleTarget);
        await sidecar.SaveNotesAsync(viaStore, story, "Some notes");
        Assert.True((await sidecar.SaveBeatsAsync(viaStore, BeatBoardService.ShortStoryBoardKey, SampleBeats)).IsSuccess);
        Assert.True((await sidecar.SaveBeatsAsync(viaStore, "other-board", SampleBeats)).IsSuccess);

        var metadata = new MetadataStore();
        await new PhaseDataService(metadata).UpsertAsync(viaServices, "uuid-1", _ => SamplePhaseData());
        await new TargetsService(metadata).UpsertAsync(viaServices, "uuid-1", SampleTarget);
        await new NotesService(metadata).SaveAsync(INotesService.DeriveNotesPath(viaServices, "manuscript/ch1.md"), "Some notes");
        var beats = new BeatBoardService(metadata);
        await beats.SaveAsync(viaServices, BeatBoardService.ShortStoryBoardKey, SampleBeats);
        await beats.SaveAsync(viaServices, "other-board", SampleBeats);

        string[] files =
        [
            Path.Combine(".hymnal-data", "phases.json"),
            Path.Combine(".hymnal-data", "targets.json"),
            Path.Combine(".hymnal-data", "notes", "manuscript_ch1.md"),
            Path.Combine(".hymnal-data", "beats", "story.json"),
            Path.Combine(".hymnal-data", "beats", "other-board.json")
        ];
        foreach (var file in files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(viaServices, file)), File.ReadAllBytes(Path.Combine(viaStore, file)));
    });

    [Fact]
    public Task Sidecar_round_trips_through_the_raw_services() => WithTempDirAsync(async dir =>
    {
        var story = new StoryRef("uuid-1", "ch1.md", Path.Combine(dir, "ch1.md"));
        var sidecar = NewSidecar();
        await sidecar.UpsertPhaseDataAsync(dir, story, _ => SamplePhaseData());
        await sidecar.SetTargetAsync(dir, story, SampleTarget);
        await sidecar.SaveNotesAsync(dir, story, "Notes");
        await sidecar.SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, SampleBeats);

        var phases = await sidecar.LoadPhaseDataAsync(dir, [story]);
        Assert.Equal(ChapterStatus.Drafting, phases["uuid-1"].Status);
        Assert.Equal(SampleTarget.MaxWords, (await sidecar.LoadTargetsAsync(dir, [story]))["uuid-1"].MaxWords);
        Assert.Equal("Notes", await sidecar.LoadNotesAsync(dir, story));
        Assert.Equal(SampleBeats, (await sidecar.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).Value);

        await sidecar.SetTargetAsync(dir, story, null);
        Assert.Empty(await sidecar.LoadTargetsAsync(dir, [story]));
    });

    // ── Embedded: primary file's block ───────────────────────────────────────

    [Fact]
    public Task Embedded_round_trips_everything_into_the_primary_block_and_writes_no_sidecar() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(story);

        await store.UpsertPhaseDataAsync(dir, story, current =>
        {
            Assert.Null(current);
            return SamplePhaseData();
        });
        await store.SetTargetAsync(dir, story, SampleTarget);
        await store.SaveNotesAsync(dir, story, "Remember the ending.");
        Assert.True((await store.SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, SampleBeats)).IsSuccess);

        var block = BlockOf(story)!;
        Assert.Equal("uuid-1", block.Id);
        Assert.Equal(ChapterStatus.Drafting, block.Status);
        Assert.Equal(new DateOnly(2026, 11, 1), block.PhaseStartDate);
        Assert.Equal(new DateOnly(2026, 11, 30), block.PhaseEndDate);
        Assert.Equal(SampleSchedule["Drafting"], block.Schedule!["Drafting"]);
        Assert.Equal(7500, block.Target!.MaxWords);
        Assert.Equal("Remember the ending.", block.Notes);
        Assert.Equal(SampleBeats, block.Beats);
        Assert.Equal(Prose, StoryFileFormat.Split(File.ReadAllText(story.AbsolutePath)).Prose);
        Assert.False(Directory.Exists(Sidecar(dir)));

        // A fresh store reads everything back from the file alone.
        var reloaded = NewEmbedded(story);
        var phase = (await reloaded.LoadPhaseDataAsync(dir, [story]))["uuid-1"];
        Assert.Equal(ChapterStatus.Drafting, phase.Status);
        Assert.Equal("2026-11-01", phase.PhaseStartDate);
        Assert.Equal("2026-11-30", phase.PhaseEndDate);
        Assert.Equal(SampleSchedule["Drafting"], phase.Schedule!["Drafting"]);
        var target = (await reloaded.LoadTargetsAsync(dir, [story]))["uuid-1"];
        Assert.Equal(("shortStory", 1000, 7500), (target.Preset!, target.MinWords!.Value, target.MaxWords!.Value));
        Assert.Equal("Remember the ending.", await reloaded.LoadNotesAsync(dir, story));
        Assert.Equal(SampleBeats, (await reloaded.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).Value);
        Assert.False(Directory.Exists(Sidecar(dir)));
    });

    [Fact]
    public Task Embedded_upsert_passes_current_block_data_and_keeps_other_fields() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(story);
        await store.UpsertPhaseDataAsync(dir, story, _ => SamplePhaseData());
        await store.SaveNotesAsync(dir, story, "Keep me");

        PhaseData? seen = null;
        await store.UpsertPhaseDataAsync(dir, story, current =>
        {
            seen = current;
            return new PhaseData { Status = ChapterStatus.Editing, PhaseStartDate = current!.PhaseStartDate };
        });

        Assert.Equal(ChapterStatus.Drafting, seen!.Status);
        var block = BlockOf(story)!;
        Assert.Equal(ChapterStatus.Editing, block.Status);
        Assert.Equal(new DateOnly(2026, 11, 1), block.PhaseStartDate);
        Assert.Null(block.PhaseEndDate);
        Assert.Null(block.Schedule);
        Assert.Equal("Keep me", block.Notes);
    });

    [Fact]
    public Task Embedded_clearing_target_and_notes_removes_them_from_the_block() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(story);
        await store.SetTargetAsync(dir, story, SampleTarget);
        await store.SaveNotesAsync(dir, story, "Notes");

        await store.SetTargetAsync(dir, story, null);
        await store.SaveNotesAsync(dir, story, "");

        var block = BlockOf(story)!;
        Assert.Null(block.Target);
        Assert.Null(block.Notes);
        Assert.Empty(await store.LoadTargetsAsync(dir, [story]));
        Assert.Equal("", await store.LoadNotesAsync(dir, story));
    });

    [Fact]
    public Task Embedded_keeps_an_existing_block_id() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Block("""{ "id": "block-id", "notes": "n" }"""), "registry-uuid");
        var store = NewEmbedded(story);

        await store.SetTargetAsync(dir, story, SampleTarget);

        Assert.Equal("block-id", BlockOf(story)!.Id);
    });

    [Fact]
    public Task Embedded_new_block_for_a_story_without_a_known_uuid_leaves_the_id_for_a_later_write() =>
        WithTempDirAsync(async dir =>
        {
            var unknown = WriteStory(dir, "story.md", "Prose.\n", uuid: "");
            var store = NewEmbedded(unknown);

            await store.SaveNotesAsync(dir, unknown, "n");
            Assert.Null(BlockOf(unknown)!.Id);

            store.SetPrimaryStory(unknown with { Uuid = "known" });
            await store.SetTargetAsync(dir, unknown, SampleTarget);
            Assert.Equal("known", BlockOf(unknown)!.Id);
        });

    [Fact]
    public Task Embedded_primary_block_wins_over_a_stale_sidecar_entry() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var sidecar = NewSidecar();
        await sidecar.UpsertPhaseDataAsync(dir, story, _ => new PhaseData { Status = ChapterStatus.Done });
        await sidecar.SetTargetAsync(dir, story, SampleTarget);
        await sidecar.SaveNotesAsync(dir, story, "sidecar notes");

        var store = NewEmbedded(story);

        Assert.Empty(await store.LoadPhaseDataAsync(dir, [story]));
        Assert.Empty(await store.LoadTargetsAsync(dir, [story]));
        Assert.Equal("", await store.LoadNotesAsync(dir, story));
    });

    [Fact]
    public Task Embedded_non_primary_file_uses_sidecar() => WithTempDirAsync(async dir =>
    {
        var primary = WriteStory(dir, "story.md", Prose, "uuid-1");
        var other = WriteStory(dir, "extra.md", "Extra prose.", "uuid-2");
        var store = NewEmbedded(primary);

        await store.UpsertPhaseDataAsync(dir, other, _ => SamplePhaseData());
        await store.SetTargetAsync(dir, other, SampleTarget);
        await store.SaveNotesAsync(dir, other, "Other notes");
        await store.UpsertPhaseDataAsync(dir, primary, _ => new PhaseData { Status = ChapterStatus.Editing });

        Assert.Equal("Extra prose.", File.ReadAllText(other.AbsolutePath));
        var sidecarPhases = await new PhaseDataService(new MetadataStore()).LoadAsync(dir);
        Assert.Equal(["uuid-2"], sidecarPhases.Keys);
        Assert.Equal("Other notes", File.ReadAllText(Sidecar(dir, "notes", "extra.md")));

        var phases = await store.LoadPhaseDataAsync(dir, [primary, other]);
        Assert.Equal(ChapterStatus.Editing, phases["uuid-1"].Status);
        Assert.Equal(ChapterStatus.Drafting, phases["uuid-2"].Status);
        var targets = await store.LoadTargetsAsync(dir, [primary, other]);
        Assert.Equal(["uuid-2"], targets.Keys);
        Assert.Equal("Other notes", await store.LoadNotesAsync(dir, other));
    });

    private static async Task AssertEveryStoryWriteRefusedAsync(
        EmbeddedStoryMetadataStore store, string dir, StoryRef story, string reason)
    {
        Assert.Equal(reason, (await Assert.ThrowsAsync<IOException>(
            () => store.UpsertPhaseDataAsync(dir, story, _ => SamplePhaseData()))).Message);
        Assert.Equal(reason, (await Assert.ThrowsAsync<IOException>(
            () => store.SetTargetAsync(dir, story, SampleTarget))).Message);
        Assert.Equal(reason, (await Assert.ThrowsAsync<IOException>(
            () => store.SaveNotesAsync(dir, story, "n"))).Message);
        Assert.Equal(reason, (await Assert.ThrowsAsync<IOException>(
            () => store.LoadNotesAsync(dir, story))).Message);
        var save = await store.SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, SampleBeats);
        Assert.Equal(reason, save.Error);
        var load = await store.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey);
        Assert.Equal(reason, load.Error);
    }

    [Fact]
    public Task Embedded_before_the_primary_is_set_refuses_every_story_and_writes_nothing() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(null);

        await AssertEveryStoryWriteRefusedAsync(store, dir, story, EmbeddedStoryMetadataStore.NotLoadedMessage);

        Assert.Equal(Prose, File.ReadAllText(story.AbsolutePath));
        Assert.False(Directory.Exists(Sidecar(dir)));
    });

    [Fact]
    public Task Embedded_with_no_story_file_refuses_every_story_and_writes_nothing() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "notes-only.md", Prose, "uuid-1");
        var store = NewEmbedded(null);
        store.SetNoStoryFile();

        await AssertEveryStoryWriteRefusedAsync(store, dir, story, "Add a story file to keep beats in it.");

        Assert.False(Directory.Exists(Sidecar(dir)));
    });

    [Fact]
    public Task Embedded_unavailable_primary_refuses_its_data_and_other_files_still_use_sidecar() => WithTempDirAsync(async dir =>
    {
        var primary = WriteStory(dir, "story.md", Block("""{ "id": "uuid-1", "status": "done" }"""), "uuid-1");
        var other = WriteStory(dir, "other.md", "Other.\n", "uuid-2");
        var store = NewEmbedded(null);
        store.SetPrimaryUnavailable(primary, "Couldn't read it.");
        var before = File.ReadAllText(primary.AbsolutePath);

        await AssertEveryStoryWriteRefusedAsync(store, dir, primary, "Couldn't read it.");
        await store.UpsertPhaseDataAsync(dir, other, _ => SamplePhaseData());

        Assert.Equal(before, File.ReadAllText(primary.AbsolutePath));
        var phases = await store.LoadPhaseDataAsync(dir, [primary, other]);
        Assert.False(phases.ContainsKey("uuid-1"));
        Assert.Equal(ChapterStatus.Drafting, phases["uuid-2"].Status);
        Assert.Empty(await store.LoadTargetsAsync(dir, [primary, other]));
    });

    [Fact]
    public Task Embedded_matches_the_primary_ignoring_path_case() => WithTempDirAsync(async dir =>
    {
        var primary = WriteStory(dir, "Story.md", Prose, "uuid-1");
        var store = NewEmbedded(primary);

        await store.SaveNotesAsync(dir, primary with { RelativePath = "story.md" }, "n");

        Assert.Equal("n", BlockOf(primary)!.Notes);
        Assert.False(Directory.Exists(Sidecar(dir, "notes")));
    });

    [Fact]
    public Task Embedded_new_block_takes_the_primary_uuid_not_the_callers() => WithTempDirAsync(async dir =>
    {
        var primary = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(primary);

        await store.SetTargetAsync(dir, primary with { Uuid = "" }, SampleTarget);

        Assert.Equal("uuid-1", BlockOf(primary)!.Id);
    });

    [Fact]
    public Task Embedded_other_board_keys_go_to_sidecar() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(story);

        Assert.True((await store.SaveBeatsAsync(dir, "other-board", SampleBeats)).IsSuccess);

        Assert.Equal(Prose, File.ReadAllText(story.AbsolutePath));
        Assert.True(File.Exists(Sidecar(dir, "beats", "other-board.json")));
        Assert.Equal(SampleBeats, (await store.LoadBeatsAsync(dir, "other-board")).Value);
        Assert.Empty((await store.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).Value!);
    });

    [Fact]
    public Task Embedded_story_board_ignores_the_sidecar_board_file() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        await NewSidecar().SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, SampleBeats);
        var store = NewEmbedded(story);

        Assert.Empty((await store.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).Value!);
        Assert.True((await store.SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, [SampleBeats[0]])).IsSuccess);

        Assert.Equal([SampleBeats[0]], BlockOf(story)!.Beats);
        Assert.Equal(SampleBeats, (await NewSidecar().LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).Value);
    });

    [Fact]
    public Task Embedded_save_beats_rejects_missing_beats() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(story);

        var result = await store.SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, [SampleBeats[0], null!]);

        Assert.False(result.IsSuccess);
        Assert.Equal(Prose, File.ReadAllText(story.AbsolutePath));
    });

    // ── Embedded: guarding block values ──────────────────────────────────────

    [Fact]
    public Task Embedded_malformed_block_makes_story_board_read_only_and_is_kept() => WithTempDirAsync(async dir =>
    {
        var content = Block("""{ "id": "x", "beats": [ """);
        var story = WriteStory(dir, "story.md", content, "uuid-1");
        var store = NewEmbedded(story);

        var result = await store.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey);

        Assert.False(result.IsSuccess);
        Assert.Empty(await store.LoadPhaseDataAsync(dir, [story]));
        Assert.Equal("", await store.LoadNotesAsync(dir, story));
        Assert.Equal(content, File.ReadAllText(story.AbsolutePath));
    });

    [Theory]
    [InlineData("""[ null ]""")]
    [InlineData("""[ { "title": "No id", "note": "" } ]""")]
    [InlineData("""[ { "id": "  ", "title": "Blank id", "note": "" } ]""")]
    [InlineData("""[ { "id": "a", "note": "" } ]""")]
    [InlineData("""[ { "id": "a", "title": "One", "note": "" }, { "id": "a", "title": "Two", "note": "" } ]""")]
    public Task Embedded_invalid_block_beats_fail_the_story_board(string beats) => WithTempDirAsync(async dir =>
    {
        var content = Block("""{ "id": "x", "beats": """ + beats + " }");
        var story = WriteStory(dir, "story.md", content, "uuid-1");
        Assert.NotNull(StoryFileFormat.Split(content).Metadata);

        var result = await NewEmbedded(story).LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey);

        Assert.False(result.IsSuccess);
    });

    [Fact]
    public Task Embedded_block_beat_with_null_note_reads_as_empty() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Block("""{ "beats": [ { "id": "a", "title": "Hook" } ] }"""), "uuid-1");

        var result = await NewEmbedded(story).LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey);

        Assert.Equal([new Beat("a", "Hook", "", null)], result.Value);
    });

    [Fact]
    public Task Embedded_undefined_status_reads_as_none() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Block("""{ "status": 99 }"""), "uuid-1");
        var dated = WriteStory(dir, "dated.md", Block("""{ "status": 99, "phaseStartDate": "2026-11-01" }"""), "uuid-2");

        Assert.Empty(await NewEmbedded(story).LoadPhaseDataAsync(dir, [story]));
        var phase = (await NewEmbedded(dated).LoadPhaseDataAsync(dir, [dated]))["uuid-2"];
        Assert.True(Enum.IsDefined(phase.Status));
        Assert.Equal("2026-11-01", phase.PhaseStartDate);
    });

    [Fact]
    public Task Embedded_unparseable_legacy_date_is_stored_as_none_without_crashing() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        var store = NewEmbedded(story);

        await store.UpsertPhaseDataAsync(dir, story, _ => new PhaseData
        {
            Status = ChapterStatus.Drafting, PhaseStartDate = "sometime in March", PhaseEndDate = "2026-12-01"
        });

        var block = BlockOf(story)!;
        Assert.Equal(ChapterStatus.Drafting, block.Status);
        Assert.Null(block.PhaseStartDate);
        Assert.Equal(new DateOnly(2026, 12, 1), block.PhaseEndDate);
    });

    [Fact]
    public Task Embedded_write_to_missing_primary_file_throws() => WithTempDirAsync(async dir =>
    {
        var story = new StoryRef("uuid-1", "gone.md", Path.Combine(dir, "gone.md"));
        var store = NewEmbedded(story);

        await Assert.ThrowsAsync<IOException>(() => store.SaveNotesAsync(dir, story, "lost?"));
        Assert.False(File.Exists(story.AbsolutePath));
    });

    [Fact]
    public Task Embedded_unreadable_primary_makes_loads_throw() => WithTempDirAsync(async dir =>
    {
        var story = new StoryRef("uuid-1", "story.md", Path.Combine(dir, "story.md"));
        File.WriteAllBytes(story.AbsolutePath, [0x41, 0xFF, 0xFE, 0x42]); // not valid UTF-8
        var store = NewEmbedded(story);

        await Assert.ThrowsAsync<IOException>(() => store.LoadNotesAsync(dir, story));
        await Assert.ThrowsAsync<IOException>(() => store.LoadPhaseDataAsync(dir, [story]));
        await Assert.ThrowsAsync<IOException>(() => store.LoadTargetsAsync(dir, [story]));
        Assert.False((await store.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).IsSuccess);
    });

    [Fact]
    public Task Embedded_read_only_primary_write_throws() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");
        File.SetAttributes(story.AbsolutePath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => NewEmbedded(story).SaveNotesAsync(dir, story, "n"));
            Assert.Equal(Prose, File.ReadAllText(story.AbsolutePath));
        }
        finally
        {
            File.SetAttributes(story.AbsolutePath, FileAttributes.Normal);
        }
    });

    [Fact]
    public Task Embedded_story_board_save_onto_malformed_block_fails_and_keeps_the_file() => WithTempDirAsync(async dir =>
    {
        var content = Block("""{ "id": "x", "beats": [ """);
        var story = WriteStory(dir, "story.md", content, "uuid-1");

        var result = await NewEmbedded(story).SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, SampleBeats);

        Assert.False(result.IsSuccess);
        Assert.Equal(content, File.ReadAllText(story.AbsolutePath));
    });

    [Fact]
    public Task Embedded_drops_primary_sidecar_entry_even_when_primary_is_not_listed() => WithTempDirAsync(async dir =>
    {
        var primary = WriteStory(dir, "story.md", Prose, "uuid-1");
        var other = WriteStory(dir, "extra.md", "Extra.", "uuid-2");
        var sidecar = NewSidecar();
        await sidecar.UpsertPhaseDataAsync(dir, primary, _ => new PhaseData { Status = ChapterStatus.Done });
        await sidecar.UpsertPhaseDataAsync(dir, other, _ => new PhaseData { Status = ChapterStatus.Editing });
        await sidecar.SetTargetAsync(dir, primary, SampleTarget);
        await sidecar.SetTargetAsync(dir, other, SampleTarget);
        var store = NewEmbedded(primary);

        Assert.Equal(["uuid-2"], (await store.LoadPhaseDataAsync(dir, [other])).Keys);
        Assert.Equal(["uuid-2"], (await store.LoadTargetsAsync(dir, [other])).Keys);
    });

    [Fact]
    public Task Embedded_matches_primary_by_path_when_its_uuid_was_adopted() => WithTempDirAsync(async dir =>
    {
        var registered = WriteStory(dir, "story.md", Block("""{ "id": "block-id", "status": "editing" }"""), "registry-uuid");
        var adopted = registered with { Uuid = "block-id" };
        var sidecar = NewSidecar();
        await sidecar.UpsertPhaseDataAsync(dir, registered, _ => new PhaseData { Status = ChapterStatus.Done });
        await sidecar.UpsertPhaseDataAsync(dir, adopted, _ => new PhaseData { Status = ChapterStatus.Done });
        var store = NewEmbedded(registered);

        var phases = await store.LoadPhaseDataAsync(dir, [adopted]);
        Assert.Equal(["block-id"], phases.Keys);
        Assert.Equal(ChapterStatus.Editing, phases["block-id"].Status);

        await store.SaveNotesAsync(dir, adopted, "via adopted ref");
        Assert.Equal("via adopted ref", BlockOf(registered)!.Notes);
        Assert.Equal("block-id", BlockOf(registered)!.Id);
        Assert.False(Directory.Exists(Sidecar(dir, "notes")));
    });

    [Fact]
    public Task Embedded_write_preserves_crlf_prose_bytes() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", Prose, "uuid-1");

        await NewEmbedded(story).SetTargetAsync(dir, story, SampleTarget);

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(story.AbsolutePath));
        Assert.StartsWith(Prose + "\r\n\r\n<!-- hymnal:v1\r\n", text);
    });

    // ── ResolvePrimary ───────────────────────────────────────────────────────

    private static readonly StoryRef First = new("u1", "a.md", "/w/a.md");
    private static readonly StoryRef Second = new("u2", "b.md", "/w/b.md");

    [Fact]
    public void ResolvePrimary_with_no_files_is_null() =>
        Assert.Null(StoryMetadataStoreFactory.ResolvePrimary([], _ => throw new InvalidOperationException()));

    [Fact]
    public void ResolvePrimary_without_blocks_is_the_first_file() =>
        Assert.Equal(First, StoryMetadataStoreFactory.ResolvePrimary([First, Second], _ => null));

    [Fact]
    public void ResolvePrimary_prefers_the_file_with_a_block() =>
        Assert.Equal(Second, StoryMetadataStoreFactory.ResolvePrimary(
            [First, Second],
            s => s == Second ? new EmbeddedStoryMetadata("u2", null, null, null, null, null, null, null) : null));

    [Fact]
    public void ResolvePrimary_with_several_blocks_is_the_first_with_a_block()
    {
        var third = new StoryRef("u3", "c.md", "/w/c.md");
        var block = new EmbeddedStoryMetadata(null, null, null, null, null, null, null, null);
        Assert.Equal(Second, StoryMetadataStoreFactory.ResolvePrimary([First, Second, third], s => s == First ? null : block));
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectKind.Story, StorageMode.Embedded, true)]
    [InlineData(ProjectKind.Story, StorageMode.Sidecar, false)]
    [InlineData(ProjectKind.Novel, StorageMode.Embedded, false)]
    [InlineData(ProjectKind.Novel, StorageMode.Sidecar, false)]
    [InlineData(ProjectKind.Anthology, StorageMode.Embedded, false)]
    [InlineData(ProjectKind.Anthology, StorageMode.Sidecar, false)]
    public void Factory_selects_embedded_only_for_embedded_stories(ProjectKind kind, StorageMode storage, bool embedded)
    {
        var store = StoryMetadataStoreFactory.Create(
            new ProjectSettings(kind, storage), NewSidecar(), new StoryFileWriter(new MetadataStore()));

        if (embedded)
            Assert.IsType<EmbeddedStoryMetadataStore>(store);
        else
            Assert.IsType<SidecarStoryMetadataStore>(store);
    }

    // ── Deferred: calls wait for the store being prepared ────────────────────

    [Fact]
    public Task Deferred_waits_for_the_prepared_store_and_then_uses_it() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", "Prose.\n", "uuid-1");
        var prepared = new TaskCompletionSource<IStoryMetadataStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new DeferredStoryMetadataStore(prepared.Task);

        var save = deferred.SaveNotesAsync(dir, story, "embedded notes");
        await Task.Delay(50);
        Assert.False(save.IsCompleted);

        // The primary is set before the store is handed over, so the early save lands in the block.
        var embedded = NewEmbedded(null);
        embedded.SetPrimaryStory(story);
        prepared.SetResult(embedded);
        await save;

        Assert.Equal("embedded notes", BlockOf(story)!.Notes);
        Assert.Equal("embedded notes", await deferred.LoadNotesAsync(dir, story));
        Assert.False(File.Exists(Sidecar(dir, "notes", "story.md")));
    });

    [Fact]
    public Task Deferred_forwards_every_member_to_the_prepared_store() => WithTempDirAsync(async dir =>
    {
        var story = WriteStory(dir, "story.md", "Prose.\n", "uuid-1");
        var deferred = new DeferredStoryMetadataStore(Task.FromResult<IStoryMetadataStore>(NewSidecar()));

        await deferred.UpsertPhaseDataAsync(dir, story, _ => SamplePhaseData());
        await deferred.SetTargetAsync(dir, story, SampleTarget);
        await deferred.SaveNotesAsync(dir, story, "n");
        Assert.True((await deferred.SaveBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey, SampleBeats)).IsSuccess);

        Assert.Equal(ChapterStatus.Drafting, (await deferred.LoadPhaseDataAsync(dir, [story]))["uuid-1"].Status);
        Assert.Equal(7500, (await deferred.LoadTargetsAsync(dir, [story]))["uuid-1"].MaxWords);
        Assert.Equal("n", await deferred.LoadNotesAsync(dir, story));
        Assert.Equal(SampleBeats.Count, (await deferred.LoadBeatsAsync(dir, BeatBoardService.ShortStoryBoardKey)).Value!.Count);
    });
}

using Hymnal.Core.Common;
using System.Text;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class StorageMigratorTests
{
    private const string Prose = "# The Story\r\n\r\nOnce upon a time.";
    private const string ProjectJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";

    private static readonly PhaseData SamplePhase = new()
    {
        Status = ChapterStatus.Editing,
        PhaseStartDate = "2026-11-01",
        PhaseEndDate = "2026-11-20",
        Schedule = new Dictionary<string, PhaseScheduleEntry>
        {
            ["Drafting"] = new() { StartDate = "2026-10-01", EndDate = "2026-10-31", Progress = 100 },
            ["Editing"] = new() { StartDate = "2026-11-01", EndDate = "2026-11-20", Progress = 40 }
        }
    };

    private static readonly WordCountTarget SampleTarget = new() { Preset = "shortStory", MinWords = 1000, MaxWords = 7500 };

    private const string SampleNotes = "Remember the lighthouse.\nAnd the gull.";

    private static readonly Beat[] SampleBeats =
    [
        new("b1", "Hook", "A storm.", "story.md", "scene-1"),
        new("b2", "Payoff", "", null)
    ];

    // ── Lossless, both directions ─────────────────────────────────────────────

    [Fact]
    public async Task Sidecar_to_embedded_moves_every_field_and_removes_only_the_story_s_sidecar_entries()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        // Another story's entries and another board stay where they are.
        await env.Phases.UpsertAsync(env.Root, "other", _ => new PhaseData { Status = ChapterStatus.Done });
        await env.Targets.UpsertAsync(env.Root, "other", new WordCountTarget { MinWords = 5 });
        await env.Notes.SaveAsync(INotesService.DeriveNotesPath(env.Root, "other.md"), "Other notes");
        Assert.True((await env.Boards.SaveAsync(env.Root, "other", [new Beat("o", "Other", "", null)])).IsSuccess);

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.True(result.IsSuccess, result.Error);
        var split = StoryFileFormat.Split(await File.ReadAllTextAsync(story.AbsolutePath));
        Assert.Equal(Prose, split.Prose);
        var block = split.Metadata!;
        Assert.Equal(story.Uuid, block.Id);
        Assert.Equal(ChapterStatus.Editing, block.Status);
        Assert.Equal(new DateOnly(2026, 11, 1), block.PhaseStartDate);
        Assert.Equal(new DateOnly(2026, 11, 20), block.PhaseEndDate);
        AssertSchedule(SamplePhase.Schedule!, block.Schedule!);
        AssertTarget(SampleTarget, block.Target!);
        Assert.Equal(SampleNotes, block.Notes);
        Assert.Equal(SampleBeats, block.Beats!);

        Assert.False((await env.Phases.LoadAsync(env.Root)).ContainsKey(story.Uuid));
        Assert.False((await env.Targets.LoadAsync(env.Root)).ContainsKey(story.Uuid));
        Assert.False(File.Exists(env.NotesPath(story)));
        Assert.False(File.Exists(BeatBoardService.BoardFilePath(env.Root, BeatBoardService.ShortStoryBoardKey)));

        Assert.Equal(ChapterStatus.Done, (await env.Phases.LoadAsync(env.Root))["other"].Status);
        Assert.Equal(5, (await env.Targets.LoadAsync(env.Root))["other"].MinWords);
        Assert.Equal("Other notes", await env.Notes.LoadAsync(INotesService.DeriveNotesPath(env.Root, "other.md")));
        Assert.True(env.Boards.BoardExists(env.Root, "other"));
    }

    [Fact]
    public async Task Embedded_to_sidecar_moves_every_field_and_strips_the_block()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        Assert.True((await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);

        var result = await env.MigrateAsync(story, StorageMode.Embedded, StorageMode.Sidecar);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(Prose, await File.ReadAllTextAsync(story.AbsolutePath));
        await env.AssertSidecarHoldsSampleAsync(story);
    }

    [Fact]
    public async Task Embedded_to_sidecar_replaces_stale_sidecar_entries_with_the_block()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        Assert.True((await env.Writer.UpdateMetadataAsync(story.AbsolutePath, _ =>
            new EmbeddedStoryMetadata(story.Uuid, ChapterStatus.Drafting, null, null, null, null, null, null))).IsSuccess);
        // Left behind from an earlier failed migration: not what the author sees, so it is replaced.
        await env.Targets.UpsertAsync(env.Root, story.Uuid, SampleTarget);
        await env.Notes.SaveAsync(env.NotesPath(story), "Stale notes");

        var result = await env.MigrateAsync(story, StorageMode.Embedded, StorageMode.Sidecar);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ChapterStatus.Drafting, (await env.Phases.LoadAsync(env.Root))[story.Uuid].Status);
        Assert.False((await env.Targets.LoadAsync(env.Root)).ContainsKey(story.Uuid));
        Assert.False(File.Exists(env.NotesPath(story)));
    }

    [Fact]
    public async Task Sidecar_to_embedded_without_data_gives_the_file_its_identity_only()
    {
        using var env = new Env();
        var story = env.WriteStory("Prose.\n");

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.True(result.IsSuccess, result.Error);
        var block = StoryFileFormat.Split(await File.ReadAllTextAsync(story.AbsolutePath)).Metadata!;
        Assert.Equal(new EmbeddedStoryMetadata(story.Uuid, null, null, null, null, null, null, null), block);
    }

    [Fact]
    public async Task Same_mode_does_nothing()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Sidecar);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(Prose, await File.ReadAllTextAsync(story.AbsolutePath));
        await env.AssertSidecarHoldsSampleAsync(story);
    }

    // ── Failures never lose data ──────────────────────────────────────────────

    [Fact]
    public async Task Sidecar_to_embedded_failing_to_write_the_block_leaves_the_sidecar_intact()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        var before = env.Snapshot();
        env.Store.FailWhen = path => path == story.AbsolutePath;

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.False(result.IsSuccess);
        Assert.Contains("Nothing was removed", result.Error);
        Assert.Equal(before, env.Snapshot());
    }

    [Fact]
    public async Task Sidecar_to_embedded_cleanup_failing_after_the_phase_entry_leaves_all_data_visible_in_the_new_mode()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        Assert.True((await env.Migrator.WriteDestinationAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        // project.json now says embedded (the caller's commit point); then the target can't be removed.
        env.Store.FailWhen = path => path.EndsWith("targets.json", StringComparison.Ordinal);

        var cleanup = await env.Migrator.CleanupSourceAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.False(cleanup.IsSuccess);
        Assert.Contains("target", cleanup.Error);
        // The rest was still removed; only the target is left behind.
        Assert.False((await env.Phases.LoadAsync(env.Root)).ContainsKey(story.Uuid));
        Assert.True((await env.Targets.LoadAsync(env.Root)).ContainsKey(story.Uuid));
        Assert.False(File.Exists(env.NotesPath(story)));
        Assert.False(env.Boards.BoardExists(env.Root, BeatBoardService.ShortStoryBoardKey));
        await env.AssertVisibleAsync(env.EmbeddedStore(story), story);
    }

    [Fact]
    public async Task Toggling_back_after_a_partial_cleanup_never_erases_data()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        Assert.True((await env.Migrator.WriteDestinationAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        env.Store.FailWhen = path => path.EndsWith("targets.json", StringComparison.Ordinal);
        Assert.False((await env.Migrator.CleanupSourceAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        env.Store.FailWhen = null;

        Assert.True((await env.MigrateAsync(story, StorageMode.Embedded, StorageMode.Sidecar)).IsSuccess);
        Assert.Equal(Prose, await File.ReadAllTextAsync(story.AbsolutePath));
        await env.AssertSidecarHoldsSampleAsync(story);

        Assert.True((await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        await env.AssertVisibleAsync(env.EmbeddedStore(story), story);
        Assert.False((await env.Targets.LoadAsync(env.Root)).ContainsKey(story.Uuid));
    }

    [Fact]
    public async Task Stopping_after_any_step_leaves_all_data_visible_in_the_mode_project_json_names()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);

        // Sidecar → embedded. Stopped after the destination write: project.json still says sidecar.
        Assert.True((await env.Migrator.WriteDestinationAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        await env.AssertVisibleAsync(env.SidecarStore, story);
        // Stopped after project.json was saved as embedded, before cleanup.
        await env.AssertVisibleAsync(env.EmbeddedStore(story), story);
        Assert.True((await env.Migrator.CleanupSourceAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);

        // Embedded → sidecar, the same two stopping points.
        Assert.True((await env.Migrator.WriteDestinationAsync(env.Root, story, StorageMode.Embedded, StorageMode.Sidecar)).IsSuccess);
        await env.AssertVisibleAsync(env.EmbeddedStore(story), story);
        await env.AssertVisibleAsync(env.SidecarStore, story);
    }

    [Fact]
    public async Task Sidecar_to_embedded_with_unreadable_project_data_writes_nothing()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        await File.WriteAllTextAsync(Path.Combine(env.Root, ".hymnal-data", "targets.json"), "{ nope");
        var before = env.Snapshot();

        var result = await env.Migrator.WriteDestinationAsync(env.Root, story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.False(result.IsSuccess);
        Assert.Contains("Nothing was changed", result.Error);
        Assert.Equal(before, env.Snapshot());
    }

    [Fact]
    public async Task Embedded_to_sidecar_failing_partway_keeps_the_block_and_reports_what_was_copied()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        Assert.True((await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        var file = await File.ReadAllBytesAsync(story.AbsolutePath);
        env.Store.FailWhen = path => path.Contains(Path.Combine(".hymnal-data", "notes"), StringComparison.Ordinal);

        var result = await env.MigrateAsync(story, StorageMode.Embedded, StorageMode.Sidecar);

        Assert.False(result.IsSuccess);
        Assert.Contains("status and schedule", result.Error);
        Assert.Contains("target", result.Error);
        Assert.Contains("Nothing was removed", result.Error);
        Assert.Equal(file, await File.ReadAllBytesAsync(story.AbsolutePath));
        Assert.Equal(ProjectJson, await File.ReadAllTextAsync(env.ProjectJsonPath));
    }

    [Fact]
    public async Task Embedded_to_sidecar_cleanup_failing_to_strip_the_block_leaves_all_data_visible_in_the_new_mode()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        Assert.True((await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded)).IsSuccess);
        Assert.True((await env.Migrator.WriteDestinationAsync(env.Root, story, StorageMode.Embedded, StorageMode.Sidecar)).IsSuccess);
        var file = await File.ReadAllBytesAsync(story.AbsolutePath);
        env.Store.FailWhen = path => path == story.AbsolutePath;

        var cleanup = await env.Migrator.CleanupSourceAsync(env.Root, story, StorageMode.Embedded, StorageMode.Sidecar);

        Assert.False(cleanup.IsSuccess);
        Assert.Contains("data block", cleanup.Error);
        Assert.Equal(file, await File.ReadAllBytesAsync(story.AbsolutePath));
        await env.AssertSidecarHoldsSampleAsync(story);
    }

    [Fact]
    public async Task Sidecar_to_embedded_refuses_a_file_that_is_not_utf8()
    {
        using var env = new Env();
        var story = env.WriteStory("");
        await File.WriteAllBytesAsync(story.AbsolutePath, [0x43, 0x61, 0x66, 0xE9, 0x0A]); // "Café" in Latin-1
        await env.SeedSidecarAsync(story);
        var before = env.Snapshot();

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.False(result.IsSuccess);
        Assert.Contains("UTF-8", result.Error);
        Assert.Equal(before, env.Snapshot());
    }

    [Fact]
    public async Task Sidecar_to_embedded_refuses_an_unreadable_story_board()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose);
        await env.SeedSidecarAsync(story);
        await File.WriteAllTextAsync(BeatBoardService.BoardFilePath(env.Root, BeatBoardService.ShortStoryBoardKey), "{ nope");
        var before = env.Snapshot();

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.False(result.IsSuccess);
        Assert.Equal(before, env.Snapshot());
    }

    [Fact]
    public async Task Embedded_to_sidecar_refuses_a_damaged_block()
    {
        using var env = new Env();
        var story = env.WriteStory("Prose.\n\n<!-- hymnal:v1\n{ not json\n-->\n");
        var before = env.Snapshot();

        var result = await env.MigrateAsync(story, StorageMode.Embedded, StorageMode.Sidecar);

        Assert.False(result.IsSuccess);
        Assert.Contains("could not be read", result.Error);
        Assert.Equal(before, env.Snapshot());
    }

    [Fact]
    public async Task Embedded_to_sidecar_refuses_a_file_that_is_not_utf8()
    {
        using var env = new Env();
        var story = env.WriteStory("");
        await File.WriteAllBytesAsync(story.AbsolutePath, [0x43, 0x61, 0x66, 0xE9, 0x0A]); // "Café" in Latin-1
        var before = env.Snapshot();

        var result = await env.MigrateAsync(story, StorageMode.Embedded, StorageMode.Sidecar);

        Assert.False(result.IsSuccess);
        Assert.Contains("UTF-8", result.Error);
        Assert.Equal(before, env.Snapshot());
    }

    [Fact]
    public async Task Sidecar_to_embedded_keeps_unknown_fields_of_an_existing_block()
    {
        using var env = new Env();
        var story = env.WriteStory(Prose + "\r\n\r\n<!-- hymnal:v1\r\n{ \"id\": \"old\", \"futureField\": 7 }\r\n-->\r\n");
        await env.SeedSidecarAsync(story);

        var result = await env.MigrateAsync(story, StorageMode.Sidecar, StorageMode.Embedded);

        Assert.True(result.IsSuccess, result.Error);
        var block = StoryFileFormat.Split(await File.ReadAllTextAsync(story.AbsolutePath)).Metadata!;
        Assert.Equal(SampleNotes, block.Notes);
        Assert.Equal(7, block.Extra!["futureField"].GetInt32());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertSchedule(
        IReadOnlyDictionary<string, PhaseScheduleEntry> expected, IReadOnlyDictionary<string, PhaseScheduleEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, entry) in expected)
            Assert.Equal(entry, actual[key]);
    }

    private static void AssertTarget(WordCountTarget expected, WordCountTarget actual)
    {
        Assert.Equal(expected.Preset, actual.Preset);
        Assert.Equal(expected.MinWords, actual.MinWords);
        Assert.Equal(expected.MaxWords, actual.MaxWords);
    }

    /// <summary>Writes through the real store unless <see cref="FailWhen"/> matches the path.</summary>
    private sealed class FailingStore : IMetadataStore
    {
        private readonly MetadataStore _inner = new();
        public Func<string, bool>? FailWhen { get; set; }

        public Task WriteTextAtomicAsync(string absolutePath, string content) =>
            FailWhen?.Invoke(absolutePath) == true
                ? throw new IOException("The disk is full.")
                : _inner.WriteTextAtomicAsync(absolutePath, content);
    }

    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hymnal-migrate-" + Guid.NewGuid().ToString("N"));
        public FailingStore Store { get; } = new();
        public PhaseDataService Phases { get; }
        public TargetsService Targets { get; }
        public NotesService Notes { get; }
        public BeatBoardService Boards { get; }
        public StoryFileWriter Writer { get; }
        public StorageMigrator Migrator { get; }
        public string ProjectJsonPath => Path.Combine(Root, ".hymnal-data", "project.json");

        public Env()
        {
            Directory.CreateDirectory(Path.Combine(Root, ".hymnal-data"));
            File.WriteAllText(ProjectJsonPath, ProjectJson);
            Phases = new PhaseDataService(Store);
            Targets = new TargetsService(Store);
            Notes = new NotesService(Store);
            Boards = new BeatBoardService(Store);
            Writer = new StoryFileWriter(Store);
            Migrator = new StorageMigrator(Phases, Targets, Notes, Boards, Writer);
        }

        public StoryRef WriteStory(string content)
        {
            var path = Path.Combine(Root, "story.md");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return new StoryRef(Guid.NewGuid().ToString(), "story.md", path);
        }

        /// <summary>A whole migration: destination, then (after the caller's commit) cleanup.</summary>
        public async Task<Result<Unit>> MigrateAsync(StoryRef story, StorageMode from, StorageMode to)
        {
            var written = await Migrator.WriteDestinationAsync(Root, story, from, to);
            return written.IsSuccess ? await Migrator.CleanupSourceAsync(Root, story, from, to) : written;
        }

        public SidecarStoryMetadataStore SidecarStore => new(Phases, Targets, Notes, Boards);

        public EmbeddedStoryMetadataStore EmbeddedStore(StoryRef story)
        {
            var store = new EmbeddedStoryMetadataStore(SidecarStore, Writer);
            store.SetPrimaryStory(story);
            return store;
        }

        /// <summary>Asserts <paramref name="store"/> shows every sample value for <paramref name="story"/>.</summary>
        public async Task AssertVisibleAsync(IStoryMetadataStore store, StoryRef story)
        {
            var phase = (await store.LoadPhaseDataAsync(Root, [story]))[story.Uuid];
            Assert.Equal(SamplePhase.Status, phase.Status);
            Assert.Equal(SamplePhase.PhaseStartDate, phase.PhaseStartDate);
            AssertSchedule(SamplePhase.Schedule!, phase.Schedule!);
            AssertTarget(SampleTarget, (await store.LoadTargetsAsync(Root, [story]))[story.Uuid]);
            Assert.Equal(SampleNotes, await store.LoadNotesAsync(Root, story));
            var beats = await store.LoadBeatsAsync(Root, BeatBoardService.ShortStoryBoardKey);
            Assert.True(beats.IsSuccess, beats.Error);
            Assert.Equal(SampleBeats, beats.Value!);
        }

        public string NotesPath(StoryRef story) => INotesService.DeriveNotesPath(Root, story.RelativePath);

        public async Task SeedSidecarAsync(StoryRef story)
        {
            await Phases.UpsertAsync(Root, story.Uuid, _ => SamplePhase);
            await Targets.UpsertAsync(Root, story.Uuid, SampleTarget);
            await Notes.SaveAsync(NotesPath(story), SampleNotes);
            Assert.True((await Boards.SaveAsync(Root, BeatBoardService.ShortStoryBoardKey, SampleBeats)).IsSuccess);
        }

        public async Task AssertSidecarHoldsSampleAsync(StoryRef story)
        {
            var phase = (await Phases.LoadAsync(Root))[story.Uuid];
            Assert.Equal(SamplePhase.Status, phase.Status);
            Assert.Equal(SamplePhase.PhaseStartDate, phase.PhaseStartDate);
            Assert.Equal(SamplePhase.PhaseEndDate, phase.PhaseEndDate);
            AssertSchedule(SamplePhase.Schedule!, phase.Schedule!);
            AssertTarget(SampleTarget, (await Targets.LoadAsync(Root))[story.Uuid]);
            Assert.Equal(SampleNotes, await Notes.LoadAsync(NotesPath(story)));
            var beats = await Boards.LoadAsync(Root, BeatBoardService.ShortStoryBoardKey);
            Assert.True(beats.IsSuccess, beats.Error);
            Assert.Equal(SampleBeats, beats.Value!);
        }

        /// <summary>Every file under the workspace (lock files aside), path → contents.</summary>
        public string Snapshot() => string.Join("\n", Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Root, path) + ": " + Convert.ToBase64String(File.ReadAllBytes(path))));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* best effort */ }
        }
    }
}

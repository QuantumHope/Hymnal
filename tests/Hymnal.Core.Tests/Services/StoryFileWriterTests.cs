using System.Text;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.Core.Tests.Services;

public class StoryFileWriterTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Wraps the real store, counting writes and optionally delaying them to widen race windows.</summary>
    private sealed class CountingStore(TimeSpan delay = default) : IMetadataStore
    {
        private readonly MetadataStore _inner = new();
        private int _writes;

        public int Writes => _writes;

        public async Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Interlocked.Increment(ref _writes);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            await _inner.WriteTextAtomicAsync(absolutePath, content);
        }
    }

    private sealed class FakeBuffer(string heldPath, EmbeddedStoryMetadata? current) : IOpenStoryBuffer
    {
        public List<EmbeddedStoryMetadata?> Applied { get; } = [];

        public EmbeddedStoryMetadata? CurrentMetadata { get; private set; } = current;

        public bool HoldsFile(string absolutePath) => absolutePath == heldPath;

        public Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata)
        {
            Applied.Add(metadata);
            CurrentMetadata = metadata;
            return Task.FromResult(Result<Unit>.Ok(Unit.Default));
        }
    }

    private static async Task WithTempDirAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-storywriter-" + Guid.NewGuid().ToString("N"));
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

    private static EmbeddedStoryMetadata Metadata(string? notes = null, ChapterStatus? status = ChapterStatus.Drafting) =>
        new("story-1", status, null, null, null, null, notes, null);

    private static string WriteBytes(string dir, byte[] bytes)
    {
        var path = Path.Combine(dir, "story.md");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteText(string dir, string text) => WriteBytes(dir, Encoding.UTF8.GetBytes(text));

    private static string ReadText(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path));

    private static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];

    [Theory]
    [InlineData("# Title\n\nFirst paragraph.\n")]
    [InlineData("# Title\r\n\r\nFirst paragraph.\r\n")]
    [InlineData("# Title\r\n\r\nNo trailing newline.")]
    [InlineData("No line break at all")]
    public Task Update_closed_file_without_block_appends_block_and_keeps_prose_bytes(string prose) =>
        WithTempDirAsync(async dir =>
        {
            var path = WriteText(dir, prose);
            var writer = new StoryFileWriter(new MetadataStore());

            var result = await writer.UpdateMetadataAsync(path, _ => Metadata("hello"));

            Assert.True(result.IsSuccess, result.Error);
            var text = ReadText(path);
            Assert.StartsWith(prose, text);
            var split = StoryFileFormat.Split(text);
            Assert.Equal(prose, split.Prose);
            Assert.Equal("hello", split.Metadata?.Notes);
            if (prose.Contains("\r\n"))
                Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        });

    [Theory]
    [InlineData("# Title\n\nFirst paragraph.\n", "\n")]
    [InlineData("# Title\r\n\r\nFirst paragraph.\r\n", "\r\n")]
    [InlineData("# Title\r\n\r\nNo trailing newline.", "\r\n")]
    public Task Update_closed_file_with_block_replaces_metadata_and_keeps_prose_bytes(string prose, string lineEnding) =>
        WithTempDirAsync(async dir =>
        {
            var path = WriteText(dir, StoryFileFormat.Merge(prose, Metadata("old"), lineEnding));
            var writer = new StoryFileWriter(new MetadataStore());
            EmbeddedStoryMetadata? seen = null;

            var result = await writer.UpdateMetadataAsync(path, current =>
            {
                seen = current;
                return current! with { Notes = "new" };
            });

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal("old", seen?.Notes);
            Assert.Equal(StoryFileFormat.Merge(prose, Metadata("new"), lineEnding), ReadText(path));
        });

    [Fact]
    public Task Update_closed_file_keeps_utf8_byte_order_mark() => WithTempDirAsync(async dir =>
    {
        var proseBytes = Concat(Utf8Bom, Encoding.UTF8.GetBytes("# Tïtle\r\n\r\nText."));
        var path = WriteBytes(dir, proseBytes);
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata());

        Assert.True(result.IsSuccess, result.Error);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(proseBytes, bytes[..proseBytes.Length]);
        Assert.Equal("\r\n\r\n<!-- hymnal:v1\r\n", Encoding.UTF8.GetString(bytes[proseBytes.Length..(proseBytes.Length + 20)]));
    });

    [Fact]
    public Task Update_closed_file_that_is_not_valid_utf8_fails_without_writing() => WithTempDirAsync(async dir =>
    {
        byte[] latin1 = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n'];
        var path = WriteBytes(dir, latin1);
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata());

        Assert.False(result.IsSuccess);
        Assert.Equal(0, store.Writes);
        Assert.Equal(latin1, File.ReadAllBytes(path));
    });

    [Fact]
    public Task Update_closed_file_with_malformed_block_keeps_it_and_appends_new_block() => WithTempDirAsync(async dir =>
    {
        const string original = "Prose.\n\n<!-- hymnal:v1\n{ not json\n-->\n";
        var path = WriteText(dir, original);
        var writer = new StoryFileWriter(new MetadataStore());
        var seen = Metadata("sentinel");

        var result = await writer.UpdateMetadataAsync(path, current =>
        {
            seen = current!;
            return Metadata("fresh");
        });

        Assert.True(result.IsSuccess, result.Error);
        Assert.Null(seen);
        var text = ReadText(path);
        Assert.StartsWith(original, text);
        var split = StoryFileFormat.Split(text);
        Assert.Equal(original, split.Prose);
        Assert.Equal("fresh", split.Metadata?.Notes);
    });

    [Fact]
    public Task Update_missing_file_fails() => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "missing.md");
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata());

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(path));
    });

    [Fact]
    public Task Update_open_file_goes_to_buffer_and_never_touches_disk() => WithTempDirAsync(async dir =>
    {
        const string onDisk = "Saved prose.\n";
        var path = WriteText(dir, onDisk);
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);
        var buffer = new FakeBuffer(path, Metadata("held"));
        writer.AttachBuffer(buffer);
        EmbeddedStoryMetadata? seen = null;

        var result = await writer.UpdateMetadataAsync(path, current =>
        {
            seen = current;
            return current! with { Status = ChapterStatus.Editing };
        });

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("held", seen?.Notes);
        var applied = Assert.Single(buffer.Applied);
        Assert.Equal(ChapterStatus.Editing, applied?.Status);
        Assert.Equal("held", applied?.Notes);
        Assert.Equal(0, store.Writes);
        Assert.Equal(onDisk, ReadText(path));
    });

    [Fact]
    public Task Update_after_buffer_detached_writes_to_disk() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.");
        var writer = new StoryFileWriter(new MetadataStore());
        var buffer = new FakeBuffer(path, null);
        writer.AttachBuffer(buffer);
        writer.AttachBuffer(null);

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata("disk"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(buffer.Applied);
        Assert.Equal("disk", StoryFileFormat.Split(ReadText(path)).Metadata?.Notes);
    });

    [Fact]
    public Task Update_for_other_file_than_buffer_writes_to_disk() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.");
        var writer = new StoryFileWriter(new MetadataStore());
        var buffer = new FakeBuffer(Path.Combine(dir, "other.md"), Metadata());
        writer.AttachBuffer(buffer);

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata("disk"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(buffer.Applied);
        Assert.Equal("disk", StoryFileFormat.Split(ReadText(path)).Metadata?.Notes);
    });

    [Fact]
    public Task Concurrent_updates_to_same_closed_file_lose_nothing() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.\n");
        var writer = new StoryFileWriter(new CountingStore(TimeSpan.FromMilliseconds(10)));
        const int count = 12;

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() =>
            writer.UpdateMetadataAsync(path, current =>
                Metadata(((current?.Notes is { } n ? int.Parse(n) : 0) + 1).ToString())))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        var split = StoryFileFormat.Split(ReadText(path));
        Assert.Equal("Prose.\n", split.Prose);
        Assert.Equal(count.ToString(), split.Metadata?.Notes);
    });

    [Fact]
    public Task Concurrent_updates_to_open_file_lose_nothing() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.\n");
        var writer = new StoryFileWriter(new MetadataStore());
        var buffer = new SlowBuffer(path);
        writer.AttachBuffer(buffer);
        const int count = 8;

        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() =>
            writer.UpdateMetadataAsync(path, current =>
                Metadata(((current?.Notes is { } n ? int.Parse(n) : 0) + 1).ToString())))));

        Assert.Equal(count.ToString(), buffer.CurrentMetadata?.Notes);
    });

    private sealed class SlowBuffer(string heldPath) : IOpenStoryBuffer
    {
        public EmbeddedStoryMetadata? CurrentMetadata { get; private set; }

        public bool HoldsFile(string absolutePath) => absolutePath == heldPath;

        public async Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata)
        {
            await Task.Delay(5);
            CurrentMetadata = metadata;
            return Result<Unit>.Ok(Unit.Default);
        }
    }

    [Theory]
    [InlineData("# Title\n\nText.\n", "\n")]
    [InlineData("# Title\r\n\r\nNo trailing newline.", "\r\n")]
    public Task Write_file_merges_prose_and_metadata(string prose, string lineEnding) => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "story.md");
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.WriteFileAsync(path, prose, Metadata("n"), lineEnding);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(StoryFileFormat.Merge(prose, Metadata("n"), lineEnding), ReadText(path));
    });

    [Fact]
    public Task Write_file_without_metadata_writes_prose_only() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "old");
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.WriteFileAsync(path, "new prose\n", null, "\n");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("new prose\n", ReadText(path));
    });

    [Fact]
    public Task Write_file_ignores_attached_buffer() => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "story.md");
        var writer = new StoryFileWriter(new MetadataStore());
        var buffer = new FakeBuffer(path, Metadata());
        writer.AttachBuffer(buffer);

        var result = await writer.WriteFileAsync(path, "Prose.", Metadata("saved"), "\n");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(buffer.Applied);
        Assert.Equal("saved", StoryFileFormat.Split(ReadText(path)).Metadata?.Notes);
    });

    [Theory]
    [InlineData("# Title\n\nText.\n", "\n")]
    [InlineData("# Title\r\n\r\nText.\r\n", "\r\n")]
    [InlineData("# Title\r\n\r\nNo trailing newline.", "\r\n")]
    [InlineData("", "\n")]
    public Task Remove_strips_block_and_restores_exact_prose(string prose, string lineEnding) =>
        WithTempDirAsync(async dir =>
        {
            var path = WriteText(dir, StoryFileFormat.Merge(prose, Metadata("gone"), lineEnding));
            var writer = new StoryFileWriter(new MetadataStore());

            var result = await writer.RemoveMetadataAsync(path);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(prose, ReadText(path));
        });

    [Fact]
    public Task Remove_keeps_byte_order_mark() => WithTempDirAsync(async dir =>
    {
        var proseBytes = Concat(Utf8Bom, Encoding.UTF8.GetBytes("Text.\n"));
        var path = WriteBytes(dir, proseBytes);
        var writer = new StoryFileWriter(new MetadataStore());
        Assert.True((await writer.UpdateMetadataAsync(path, _ => Metadata())).IsSuccess);

        var result = await writer.RemoveMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(proseBytes, File.ReadAllBytes(path));
    });

    [Theory]
    [InlineData("Plain prose, no block.\n")]
    [InlineData("Prose.\n\n<!-- hymnal:v1\n{ not json\n-->\n")]
    public Task Remove_without_recognized_block_does_not_write(string original) => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, original);
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);

        var result = await writer.RemoveMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, store.Writes);
        Assert.Equal(original, ReadText(path));
    });

    [Fact]
    public Task Remove_missing_file_fails() => WithTempDirAsync(async dir =>
    {
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.RemoveMetadataAsync(Path.Combine(dir, "missing.md"));

        Assert.False(result.IsSuccess);
    });

    [Fact]
    public Task Remove_open_file_goes_to_buffer() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, StoryFileFormat.Merge("Prose.", Metadata(), "\n"));
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);
        var buffer = new FakeBuffer(path, Metadata());
        writer.AttachBuffer(buffer);

        var result = await writer.RemoveMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Null(Assert.Single(buffer.Applied));
        Assert.Equal(0, store.Writes);
    });

    [Fact]
    public Task Remove_open_file_without_metadata_is_noop() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.");
        var writer = new StoryFileWriter(new MetadataStore());
        var buffer = new FakeBuffer(path, null);
        writer.AttachBuffer(buffer);

        var result = await writer.RemoveMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(buffer.Applied);
    });

    [Fact]
    public Task Read_returns_metadata_when_block_present() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, StoryFileFormat.Merge("Prose.\r\n", Metadata("read me"), "\r\n"));
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.ReadMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("read me", result.Value?.Notes);
        Assert.Equal("story-1", result.Value?.Id);
    });

    [Fact]
    public Task Read_returns_null_without_block() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.");
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.ReadMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Null(result.Value);
    });

    [Fact]
    public Task Read_missing_file_fails() => WithTempDirAsync(async dir =>
    {
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.ReadMetadataAsync(Path.Combine(dir, "missing.md"));

        Assert.False(result.IsSuccess);
    });

    [Fact]
    public Task Read_open_file_returns_buffer_metadata() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, StoryFileFormat.Merge("Prose.", Metadata("disk"), "\n"));
        var writer = new StoryFileWriter(new MetadataStore());
        writer.AttachBuffer(new FakeBuffer(path, Metadata("held")));

        var result = await writer.ReadMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("held", result.Value?.Notes);
    });

    [Fact]
    public Task Read_malformed_block_returns_null_and_read_file_reports_it() => WithTempDirAsync(async dir =>
    {
        const string original = "Prose.\n\n<!-- hymnal:v1\n{ not json\n-->\n";
        var path = WriteText(dir, original);
        var writer = new StoryFileWriter(new MetadataStore());

        var metadata = await writer.ReadMetadataAsync(path);
        var split = await writer.ReadFileAsync(path);

        Assert.True(metadata.IsSuccess, metadata.Error);
        Assert.Null(metadata.Value);
        Assert.True(split.IsSuccess, split.Error);
        Assert.True(split.Value!.HadMalformedBlock);
        Assert.Equal(original, split.Value.Prose);
    });

    /// <summary>A fact that only runs on Linux (symbolic links need no special privileges there).</summary>
    private sealed class LinuxOnlyFactAttribute : FactAttribute
    {
        public LinuxOnlyFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "Linux only";
        }
    }

    private sealed class RecordingBuffer : IOpenStoryBuffer
    {
        public List<string> Asked { get; } = [];

        public EmbeddedStoryMetadata? CurrentMetadata => null;

        public bool HoldsFile(string absolutePath)
        {
            Asked.Add(absolutePath);
            return false;
        }

        public Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata) =>
            Task.FromResult(Result<Unit>.Ok(Unit.Default));
    }

    private static Func<EmbeddedStoryMetadata?, EmbeddedStoryMetadata> Increment =>
        current => Metadata(((current?.Notes is { } n ? int.Parse(n) : 0) + 1).ToString());

    [LinuxOnlyFact]
    public Task Update_through_symlink_updates_real_file_and_keeps_link() => WithTempDirAsync(async dir =>
    {
        var real = WriteText(dir, "Real prose.\n");
        var link = Path.Combine(dir, "link.md");
        File.CreateSymbolicLink(link, real);
        var writer = new StoryFileWriter(new MetadataStore());

        var result = await writer.UpdateMetadataAsync(link, _ => Metadata("via link"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        var split = StoryFileFormat.Split(ReadText(real));
        Assert.Equal("Real prose.\n", split.Prose);
        Assert.Equal("via link", split.Metadata?.Notes);
        Assert.Equal("via link", (await writer.ReadMetadataAsync(link)).Value?.Notes);
    });

    [LinuxOnlyFact]
    public Task Updates_through_link_and_real_path_share_one_lock() => WithTempDirAsync(async dir =>
    {
        var real = WriteText(dir, "Prose.\n");
        var link = Path.Combine(dir, "link.md");
        File.CreateSymbolicLink(link, real);
        var writer = new StoryFileWriter(new CountingStore(TimeSpan.FromMilliseconds(10)));
        const int count = 10;

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
            writer.UpdateMetadataAsync(i % 2 == 0 ? link : real, Increment))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(count.ToString(), StoryFileFormat.Split(ReadText(real)).Metadata?.Notes);
    });

    [Fact]
    public Task Update_read_only_file_fails_without_writing() => WithTempDirAsync(async dir =>
    {
        const string original = "Locked prose.\n";
        var path = WriteText(dir, original);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);
        try
        {
            var update = await writer.UpdateMetadataAsync(path, _ => Metadata());
            var write = await writer.WriteFileAsync(path, "Other.", Metadata(), "\n");

            Assert.False(update.IsSuccess);
            Assert.False(write.IsSuccess);
            Assert.Equal(0, store.Writes);
            Assert.Equal(original, ReadText(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [UnixPermissionFact]
    public Task Update_file_without_user_write_permission_fails_without_writing() => WithTempDirAsync(async dir =>
    {
        const string original = "Locked prose.\n";
        var path = WriteText(dir, original);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata());

        Assert.False(result.IsSuccess);
        Assert.Equal(0, store.Writes);
        Assert.Equal(original, ReadText(path));
    });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0name.md")]
    public async Task Invalid_paths_fail_from_every_method(string path)
    {
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);

        Assert.False((await writer.ReadMetadataAsync(path)).IsSuccess);
        Assert.False((await writer.ReadFileAsync(path)).IsSuccess);
        Assert.False((await writer.UpdateMetadataAsync(path, _ => Metadata())).IsSuccess);
        Assert.False((await writer.WriteFileAsync(path, "x", null, "\n")).IsSuccess);
        Assert.False((await writer.RemoveMetadataAsync(path)).IsSuccess);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public Task Update_that_changes_nothing_does_not_write() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, StoryFileFormat.Merge("Prose.\r\n", Metadata("same"), "\r\n"));
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        var store = new CountingStore();
        var writer = new StoryFileWriter(store);

        var result = await writer.UpdateMetadataAsync(path, current => current! with { });

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, store.Writes);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    });

    [Fact]
    public Task Non_normalized_and_relative_paths_reach_buffer_as_full_path() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.");
        var writer = new StoryFileWriter(new MetadataStore());
        var buffer = new RecordingBuffer();
        writer.AttachBuffer(buffer);
        var dotted = Path.Combine(dir, "sub", "..", ".", "story.md");
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);

        Assert.True((await writer.UpdateMetadataAsync(dotted, _ => Metadata())).IsSuccess);
        Assert.True((await writer.ReadMetadataAsync(relative)).IsSuccess);

        Assert.Equal([path, path], buffer.Asked);
    });

    [Fact]
    public Task Updates_through_different_spellings_of_one_path_serialize() => WithTempDirAsync(async dir =>
    {
        var path = WriteText(dir, "Prose.\n");
        var dotted = Path.Combine(dir, "sub", "..", "story.md");
        var writer = new StoryFileWriter(new CountingStore(TimeSpan.FromMilliseconds(10)));
        const int count = 10;

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
            writer.UpdateMetadataAsync(i % 2 == 0 ? path : dotted, Increment))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        Assert.Equal(count.ToString(), StoryFileFormat.Split(ReadText(path)).Metadata?.Notes);
    });

    [Fact]
    public Task Read_file_keeps_byte_order_mark_in_prose() => WithTempDirAsync(async dir =>
    {
        var path = WriteBytes(dir, Concat(Utf8Bom, Encoding.UTF8.GetBytes("Text.")));
        var writer = new StoryFileWriter(new MetadataStore());

        var split = await writer.ReadFileAsync(path);

        Assert.True(split.IsSuccess, split.Error);
        Assert.Equal("﻿Text.", split.Value!.Prose);
    });
}

public class StoryFileWriterHoldTests
{
    private static EmbeddedStoryMetadata Metadata(string notes) => new("story-1", null, null, null, null, null, notes, null);

    private static async Task WithTempDirAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hymnal-storywriter-hold-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public Task Hold_delays_metadata_update_until_released() => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "story.md");
        File.WriteAllText(path, "Prose.");
        var writer = new StoryFileWriter(new MetadataStore());

        var hold = await writer.HoldMetadataChangesAsync(path);
        var update = writer.UpdateMetadataAsync(path, _ => Metadata("held"));
        await Task.Delay(100);

        Assert.False(update.IsCompleted);
        Assert.Equal("Prose.", File.ReadAllText(path));

        hold.Dispose();
        var result = await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("held", StoryFileFormat.Split(File.ReadAllText(path)).Metadata?.Notes);
    });

    [Fact]
    public Task Hold_on_two_spellings_of_one_file_takes_its_lock_once() => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "story.md");
        File.WriteAllText(path, "Prose.");
        var dotted = Path.Combine(dir, "sub", "..", "story.md");
        var writer = new StoryFileWriter(new MetadataStore());

        var hold = await writer.HoldMetadataChangesAsync(path, dotted).WaitAsync(TimeSpan.FromSeconds(5));
        hold.Dispose();

        var result = await writer.UpdateMetadataAsync(path, _ => Metadata("after")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.Error);
    });

    [Fact]
    public Task Hold_ignores_null_and_invalid_paths() => WithTempDirAsync(async dir =>
    {
        var writer = new StoryFileWriter(new MetadataStore());

        using var hold = await writer.HoldMetadataChangesAsync(null, "", "bad\0path").WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>Holds its file until a change arrives, then reports it closed meanwhile (editor closed it).</summary>
    private sealed class ClosingBuffer(string path) : IOpenStoryBuffer
    {
        private volatile bool _held = true;

        public EmbeddedStoryMetadata? CurrentMetadata => Metadata("buffer");

        public bool HoldsFile(string absolutePath) => _held && absolutePath == path;

        public Task<Result<Unit>> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata)
        {
            _held = false;
            return Task.FromResult(Result<Unit>.Fail("The story file is no longer open in the editor."));
        }
    }

    [Fact]
    public Task Update_falls_back_to_closed_file_when_buffer_closed_it_meanwhile() => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "story.md");
        File.WriteAllText(path, StoryFileFormat.Merge("Prose.", Metadata("disk"), "\n"));
        var writer = new StoryFileWriter(new MetadataStore());
        writer.AttachBuffer(new ClosingBuffer(path));

        var result = await writer.UpdateMetadataAsync(path, m => m! with { Notes = m.Notes + "+1" });

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(StoryFileFormat.Merge("Prose.", Metadata("disk+1"), "\n"), File.ReadAllText(path));
    });

    [Fact]
    public Task Remove_falls_back_to_closed_file_when_buffer_closed_it_meanwhile() => WithTempDirAsync(async dir =>
    {
        var path = Path.Combine(dir, "story.md");
        File.WriteAllText(path, StoryFileFormat.Merge("Prose.", Metadata("disk"), "\n"));
        var writer = new StoryFileWriter(new MetadataStore());
        writer.AttachBuffer(new ClosingBuffer(path));

        var result = await writer.RemoveMetadataAsync(path);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Prose.", File.ReadAllText(path));
    });

    [Fact]
    public Task Try_read_flags_files_that_are_not_utf8() => WithTempDirAsync(async dir =>
    {
        var bad = Path.Combine(dir, "latin1.md");
        File.WriteAllBytes(bad, [0x63, 0x61, 0x66, 0xE9]);
        var writer = new StoryFileWriter(new MetadataStore());

        var notUtf8 = await writer.TryReadFileAsync(bad);
        var missing = await writer.TryReadFileAsync(Path.Combine(dir, "missing.md"));

        Assert.False(notUtf8.Result.IsSuccess);
        Assert.True(notUtf8.NotUtf8);
        Assert.False(missing.Result.IsSuccess);
        Assert.False(missing.NotUtf8);
    });

    [SymlinkFact]
    public Task Try_resolve_path_makes_full_and_resolves_links() => WithTempDirAsync(dir =>
    {
        var target = Path.Combine(dir, "story.md");
        File.WriteAllText(target, "Prose.");
        var link = Path.Combine(dir, "link.md");
        File.CreateSymbolicLink(link, target);

        Assert.True(StoryFileWriter.TryResolvePath(Path.Combine(dir, "sub", "..", "link.md"), out var full, out var real));
        Assert.Equal(link, full);
        Assert.Equal(target, real);
        Assert.False(StoryFileWriter.TryResolvePath("", out _, out _));
        return Task.CompletedTask;
    });
}

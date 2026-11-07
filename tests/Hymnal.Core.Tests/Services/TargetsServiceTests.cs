using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class TargetsServiceTests
{
    // -------------------------------------------------------------------------
    // FakeMetadataStore — captures the last written content without touching disk
    // -------------------------------------------------------------------------

    private sealed class FakeMetadataStore : IMetadataStore
    {
        public string? LastPath { get; private set; }
        public string? LastContent { get; private set; }

        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            LastPath = absolutePath;
            LastContent = content;
            return Task.CompletedTask;
        }
    }

    // -------------------------------------------------------------------------
    // RealFileMetadataStore — for round-trip tests that actually hit disk
    // -------------------------------------------------------------------------

    private sealed class RealFileMetadataStore : IMetadataStore
    {
        public async Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            var dir = Path.GetDirectoryName(absolutePath)!;
            Directory.CreateDirectory(dir);
            var tmp = absolutePath + ".tmp";
            await File.WriteAllTextAsync(tmp, content);
            File.Move(tmp, absolutePath, overwrite: true);
        }
    }

    // -------------------------------------------------------------------------
    // LoadAsync: missing file returns empty dict
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyDict()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new TargetsService(new RealFileMetadataStore());
            var result = await svc.LoadAsync(tempDir);
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // UpsertAsync: new uuid persists target
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_NewUuid_PersistsTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new TargetsService(new RealFileMetadataStore());
            var uuid = Guid.NewGuid().ToString();
            var target = new WordCountTarget { MinWords = 1000, MaxWords = 5000 };

            await svc.UpsertAsync(tempDir, uuid, target);

            var loaded = await svc.LoadAsync(tempDir);
            Assert.True(loaded.ContainsKey(uuid));
            Assert.Equal(1000, loaded[uuid].MinWords);
            Assert.Equal(5000, loaded[uuid].MaxWords);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // UpsertAsync: existing uuid overwrites
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_ExistingUuid_Overwrites()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new TargetsService(new RealFileMetadataStore());
            var uuid = Guid.NewGuid().ToString();

            await svc.UpsertAsync(tempDir, uuid, new WordCountTarget { MaxWords = 2000 });
            await svc.UpsertAsync(tempDir, uuid, new WordCountTarget { MaxWords = 9000 });

            var loaded = await svc.LoadAsync(tempDir);
            Assert.Single(loaded);
            Assert.Equal(9000, loaded[uuid].MaxWords);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // UpsertAsync: null target removes the entry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_NullTarget_RemovesEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new TargetsService(new RealFileMetadataStore());
            var uuid = Guid.NewGuid().ToString();

            await svc.UpsertAsync(tempDir, uuid, new WordCountTarget { MaxWords = 3000 });
            await svc.UpsertAsync(tempDir, uuid, null);

            var loaded = await svc.LoadAsync(tempDir);
            Assert.Empty(loaded);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // GetTarget: missing key returns null
    // -------------------------------------------------------------------------

    [Fact]
    public void GetTarget_MissingKey_ReturnsNull()
    {
        var svc = new TargetsService(new FakeMetadataStore());
        var targets = new Dictionary<string, WordCountTarget>();

        var result = svc.GetTarget(targets, "nonexistent-uuid");

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Round-trip: save and load preserves MinWords and MaxWords
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_SaveAndLoad_PreservesMinMaxValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new TargetsService(new RealFileMetadataStore());
            var uuid = Guid.NewGuid().ToString();
            var targets = new Dictionary<string, WordCountTarget>
            {
                [uuid] = new WordCountTarget { MinWords = 500, MaxWords = 4000 }
            };

            await svc.SaveAsync(tempDir, targets);
            var loaded = await svc.LoadAsync(tempDir);

            Assert.True(loaded.ContainsKey(uuid));
            Assert.Equal(500, loaded[uuid].MinWords);
            Assert.Equal(4000, loaded[uuid].MaxWords);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Targets_round_trip_preset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new TargetsService(new RealFileMetadataStore());
            await svc.UpsertAsync(tempDir, "u1",
                new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" });
            var loaded = await svc.LoadAsync(tempDir);
            Assert.Equal("shortStory", loaded["u1"].Preset);
            Assert.Equal(1000, loaded["u1"].MinWords);
            Assert.Equal(7500, loaded["u1"].MaxWords);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Targets_load_without_preset_field()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(tempDir, ".hymnal-data"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, ".hymnal-data", "targets.json"),
                "{\"schemaVersion\":1,\"targets\":{\"u1\":{\"minWords\":500,\"maxWords\":4000}}}");
            var loaded = await new TargetsService(new RealFileMetadataStore()).LoadAsync(tempDir);
            Assert.Null(loaded["u1"].Preset);
            Assert.Equal(500, loaded["u1"].MinWords);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

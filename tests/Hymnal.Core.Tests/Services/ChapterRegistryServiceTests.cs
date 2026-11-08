using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class ChapterRegistryServiceTests
{
    // -------------------------------------------------------------------------
    // In-memory fake IMetadataStore
    // -------------------------------------------------------------------------

    private sealed class FakeMetadataStore : IMetadataStore
    {
        public Dictionary<string, string> Written { get; } = new();

        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Written[absolutePath] = content;
            return Task.CompletedTask;
        }
    }

    // -------------------------------------------------------------------------
    // AssignUuid
    // -------------------------------------------------------------------------

    [Fact]
    public void AssignUuid_NewPath_AssignsGuidAndMarksNew()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry>();

        var (uuid, wasNew) = svc.AssignUuid(registry, "chapter-01.md");

        Assert.True(wasNew);
        Assert.True(Guid.TryParse(uuid, out _));
        Assert.Single(registry);
        Assert.Equal("chapter-01.md", registry[uuid].CurrentPath);
    }

    [Fact]
    public void AssignUuid_ExistingPath_ReturnsSameUuid()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry>();

        var (uuid1, wasNew1) = svc.AssignUuid(registry, "chapter-01.md");
        var (uuid2, wasNew2) = svc.AssignUuid(registry, "chapter-01.md");

        Assert.True(wasNew1);
        Assert.False(wasNew2);
        Assert.Equal(uuid1, uuid2);
        Assert.Single(registry); // no duplicate entry
    }

    // -------------------------------------------------------------------------
    // ReconcileOrphans
    // -------------------------------------------------------------------------

    [Fact]
    public void ReconcileOrphans_RemovesOrphanedEntries()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var uuid = Guid.NewGuid().ToString();
        var registry = new Dictionary<string, ChapterRegistryEntry>
        {
            [uuid] = new ChapterRegistryEntry { Uuid = uuid, CurrentPath = "chapter-01.md", Orphaned = false }
        };

        // Active paths does NOT include chapter-01.md
        var result = svc.ReconcileOrphans(registry, Array.Empty<string>());

        Assert.True(result[uuid].Orphaned);
    }

    [Fact]
    public void ReconcileOrphans_RestoredEntry_ClearsOrphanFlag()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var uuid = Guid.NewGuid().ToString();
        var registry = new Dictionary<string, ChapterRegistryEntry>
        {
            [uuid] = new ChapterRegistryEntry { Uuid = uuid, CurrentPath = "chapter-01.md", Orphaned = true }
        };

        // Active paths now includes chapter-01.md
        var result = svc.ReconcileOrphans(registry, new[] { "chapter-01.md" });

        Assert.False(result[uuid].Orphaned);
    }

    // -------------------------------------------------------------------------
    // Round-trip with real temp file
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_SaveAndLoad_FidelityPreserved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Use the real MetadataStore-like behaviour: write via a real file
            var realStore = new RealFileMetadataStore();
            var svc = new ChapterRegistryService(realStore);

            var uuid = Guid.NewGuid().ToString();
            var original = new Dictionary<string, ChapterRegistryEntry>
            {
                [uuid] = new ChapterRegistryEntry
                {
                    Uuid = uuid,
                    CurrentPath = "part-1/chapter-01.md",
                    Orphaned = false
                }
            };

            await svc.SaveAsync(tempDir, original);
            var loaded = await svc.LoadAsync(tempDir);

            Assert.True(loaded.ContainsKey(uuid));
            Assert.Equal(uuid, loaded[uuid].Uuid);
            Assert.Equal("part-1/chapter-01.md", loaded[uuid].CurrentPath);
            Assert.False(loaded[uuid].Orphaned);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_UnknownSchemaVersion_ThrowsInvalidDataException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var dataDir = Path.Combine(tempDir, ".hymnal-data");
            Directory.CreateDirectory(dataDir);
            var registryPath = Path.Combine(dataDir, "chapter-registry.json");

            // Write JSON with an unsupported schemaVersion
            await File.WriteAllTextAsync(registryPath,
                """{"schemaVersion":99,"entries":{}}""");

            var svc = new ChapterRegistryService(new FakeMetadataStore());

            await Assert.ThrowsAsync<InvalidDataException>(() => svc.LoadAsync(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // AdoptUuid
    // -------------------------------------------------------------------------

    private static ChapterRegistryEntry Entry(string uuid, string path, bool orphaned = false, string? title = null) =>
        new() { Uuid = uuid, CurrentPath = path, Orphaned = orphaned, Title = title };

    [Fact]
    public void AdoptUuid_ReplacesThePathsEntryWithTheAdoptedUuid()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry>
        {
            ["old"] = Entry("old", "story.md", title: "Story"),
            ["other"] = Entry("other", "other.md")
        };

        var updated = svc.AdoptUuid(registry, "story.md", "block");

        Assert.Equal(["block", "other"], updated.Keys.Order());
        Assert.Equal("story.md", updated["block"].CurrentPath);
        Assert.Equal("block", updated["block"].Uuid);
        Assert.Equal("Story", updated["block"].Title);
        Assert.False(updated["block"].Orphaned);
    }

    [Fact]
    public void AdoptUuid_MovesAnOrphanedEntryUnderTheAdoptedUuidToThePath()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry>
        {
            ["block"] = Entry("block", "before-rename.md", orphaned: true, title: "Old title"),
            ["fresh"] = Entry("fresh", "renamed.md")
        };

        var updated = svc.AdoptUuid(registry, "renamed.md", "block");

        var entry = Assert.Single(updated).Value;
        Assert.Equal("block", entry.Uuid);
        Assert.Equal("renamed.md", entry.CurrentPath);
        Assert.False(entry.Orphaned);
    }

    [Fact]
    public void AdoptUuid_LeavesTheRegistryWhenThePathAlreadyHasTheUuid()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry> { ["block"] = Entry("block", "story.md") };

        Assert.Same(registry, svc.AdoptUuid(registry, "story.md", "block"));
    }

    [Fact]
    public void AdoptUuid_NeverTakesTheUuidOfAnotherFileInTheBook()
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry>
        {
            ["mine"] = Entry("mine", "copy.md"),
            ["block"] = Entry("block", "original.md")
        };

        Assert.Same(registry, svc.AdoptUuid(registry, "copy.md", "block"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AdoptUuid_IgnoresABlankUuid(string blank)
    {
        var svc = new ChapterRegistryService(new FakeMetadataStore());
        var registry = new Dictionary<string, ChapterRegistryEntry> { ["mine"] = Entry("mine", "story.md") };

        Assert.Same(registry, svc.AdoptUuid(registry, "story.md", blank));
    }

    // -------------------------------------------------------------------------
    // Minimal real-filesystem IMetadataStore for round-trip tests
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
}

using System.Text.Json;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class PhaseDataServiceTests
{
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

    // -------------------------------------------------------------------------
    // Round-trip fidelity
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_SaveAndLoad_FidelityPreserved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new PhaseDataService(new RealFileMetadataStore());
            var uuid = Guid.NewGuid().ToString();

            var phases = new Dictionary<string, PhaseData>
            {
                [uuid] = new PhaseData
                {
                    Status = ChapterStatus.Drafting,
                    PhaseStartDate = "2025-01-01"
                }
            };

            await svc.SaveAsync(tempDir, phases);
            var loaded = await svc.LoadAsync(tempDir);

            Assert.True(loaded.ContainsKey(uuid));
            Assert.Equal(ChapterStatus.Drafting, loaded[uuid].Status);
            Assert.Equal("2025-01-01", loaded[uuid].PhaseStartDate);
            Assert.Null(loaded[uuid].PhaseEndDate);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Absent file returns empty dict
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Load_AbsentFile_ReturnsEmptyDict()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Do NOT create phases.json — the directory exists but the file does not
            var svc = new PhaseDataService(new RealFileMetadataStore());

            var result = await svc.LoadAsync(tempDir);

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Unknown schema version
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Load_UnknownSchemaVersion_ThrowsInvalidDataException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var dataDir = Path.Combine(tempDir, ".hymnal-data");
            Directory.CreateDirectory(dataDir);
            var phasesPath = Path.Combine(dataDir, "phases.json");

            await File.WriteAllTextAsync(phasesPath,
                """{"schemaVersion":99,"phases":{}}""");

            var svc = new PhaseDataService(new RealFileMetadataStore());

            await Assert.ThrowsAsync<InvalidDataException>(() => svc.LoadAsync(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Null PhaseEndDate is omitted from JSON
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Save_NullPhaseEndDate_OmittedFromJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var captured = new CaptureMetadataStore();
            var svc = new PhaseDataService(captured);
            var uuid = Guid.NewGuid().ToString();

            var phases = new Dictionary<string, PhaseData>
            {
                [uuid] = new PhaseData
                {
                    Status = ChapterStatus.Editing,
                    PhaseStartDate = "2025-06-01",
                    PhaseEndDate = null
                }
            };

            await svc.SaveAsync(tempDir, phases);

            Assert.True(captured.LastContent is not null,
                "SaveAsync should have called WriteTextAtomicAsync.");
            Assert.DoesNotContain("phaseEndDate", captured.LastContent,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Capture store — records the last written JSON payload
    // -------------------------------------------------------------------------

    private sealed class CaptureMetadataStore : IMetadataStore
    {
        public string? LastContent { get; private set; }

        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            LastContent = content;
            return Task.CompletedTask;
        }
    }

    // -------------------------------------------------------------------------
    // RemoveAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RemoveAsync_RemovesOnlyThatEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new PhaseDataService(new RealFileMetadataStore());
            await svc.UpsertAsync(tempDir, "a", _ => new PhaseData { Status = ChapterStatus.Drafting });
            await svc.UpsertAsync(tempDir, "b", _ => new PhaseData { Status = ChapterStatus.Done });

            await svc.RemoveAsync(tempDir, "a");

            var loaded = await svc.LoadAsync(tempDir);
            Assert.False(loaded.ContainsKey("a"));
            Assert.Equal(ChapterStatus.Done, loaded["b"].Status);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveAsync_WritesNothingWhenTheEntryIsAbsent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var captured = new CaptureMetadataStore();
            var svc = new PhaseDataService(captured);

            await svc.RemoveAsync(tempDir, "absent");

            Assert.Null(captured.LastContent);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

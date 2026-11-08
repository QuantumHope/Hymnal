using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class BookTxtStructureServiceTests
{
    private static (string Root, string BookTxtPath) CreateWorkspace(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
        {
            var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(absolutePath, content);
        }

        return (root, Path.Combine(root, "Book.txt"));
    }

    private static BookTxtStructureService CreateService(
        IMetadataStore? metadataStore = null,
        IExclusionManifestService? exclusionManifestService = null)
    {
        var store = metadataStore ?? new MetadataStore();
        return new BookTxtStructureService(store, exclusionManifestService ?? new ExclusionManifestService(store));
    }

    private static async Task<string[]> LoadExcludedPathsAsync(string workspaceRoot)
    {
        var result = await new ExclusionManifestService(new MetadataStore()).LoadAsync(workspaceRoot);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!.ExcludedPaths;
    }

    private static async Task SaveRegistryAsync(string workspaceRoot, params ChapterRegistryEntry[] entries)
    {
        var registry = entries.ToDictionary(entry => entry.Uuid, entry => entry);
        await new ChapterRegistryService(new MetadataStore()).SaveAsync(workspaceRoot, registry);
    }

    private static async Task<Dictionary<string, ChapterRegistryEntry>> LoadRegistryAsync(string workspaceRoot)
        => await new ChapterRegistryService(new MetadataStore()).LoadAsync(workspaceRoot);

    private static async Task SeedUuidSidecarsAsync(string workspaceRoot, string uuid)
    {
        var store = new MetadataStore();
        await new PhaseDataService(store).SaveAsync(workspaceRoot, new Dictionary<string, PhaseData>
        {
            [uuid] = new PhaseData { Status = ChapterStatus.Drafting }
        });
        await new TargetsService(store).SaveAsync(workspaceRoot, new Dictionary<string, WordCountTarget>
        {
            [uuid] = new WordCountTarget { MinWords = 1000, MaxWords = 1500 }
        });
        await new WordCountHistoryService(store).AppendAsync(workspaceRoot, uuid, "2026-06-04", 1234);

        var notesDirectory = Path.Combine(workspaceRoot, ".hymnal-data", "notes");
        Directory.CreateDirectory(notesDirectory);
        await File.WriteAllTextAsync(Path.Combine(notesDirectory, uuid + ".md"), "UUID keyed note");
    }

    private static async Task AssertUuidSidecarsAsync(string workspaceRoot, string uuid)
    {
        var store = new MetadataStore();
        var phases = await new PhaseDataService(store).LoadAsync(workspaceRoot);
        Assert.Equal(ChapterStatus.Drafting, phases[uuid].Status);

        var targets = await new TargetsService(store).LoadAsync(workspaceRoot);
        Assert.Equal(1000, targets[uuid].MinWords);
        Assert.Equal(1500, targets[uuid].MaxWords);

        var history = await new WordCountHistoryService(store).GetAllAsync(workspaceRoot);
        var historyEntry = Assert.Single(history, entry => entry.Uuid == uuid);
        Assert.Equal("2026-06-04", historyEntry.Date);
        Assert.Equal(1234, historyEntry.WordCount);

        Assert.Equal("UUID keyed note", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, ".hymnal-data", "notes", uuid + ".md")));
    }

    private static string AbsolutePath(string workspaceRoot, string relativePath)
        => Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ManifestPath(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".hymnal-data", "exclusions.json");

    private static string ReadBookTxt(string bookTxtPath) => File.ReadAllText(bookTxtPath);

    private static string[] ReadBookTxtLines(string bookTxtPath) => File.ReadAllLines(bookTxtPath);

    [Fact]
    public async Task ReorderEntryAsync_MovesEntryWithinPart()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 2);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-two.md",
                "part-one/chapter-one.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_MovesEntryAcrossParts_AndKeepsBlankLines()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\n\npart-one/chapter-one.md\npart-one/chapter-two.md\n\npart-two/part.md\npart-two/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-three.md", "# Chapter Three"));

        try
        {
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-two/chapter-three.md", 1);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                string.Empty,
                "part-two/chapter-three.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                string.Empty,
                "part-two/part.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.True(File.Exists(Path.Combine(workspace.Root, "part-two", "chapter-three.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_MovesPartBlockBeforeAnotherPartBlock()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md\npart-three/part.md\npart-three/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"),
            ("part-three/part.md", "{class: part}\n# Part Three"),
            ("part-three/chapter-three.md", "# Chapter Three"));

        try
        {
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-three/part.md", 2);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-three/part.md",
                "part-three/chapter-three.md",
                "part-two/part.md",
                "part-two/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_MovesPartBlockAfterAnotherPartBlockAndPreservesBlankLines()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\n\npart-one/chapter-one.md\n\npart-two/part.md\npart-two/chapter-two.md\n\npart-three/part.md\npart-three/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"),
            ("part-three/part.md", "{class: part}\n# Part Three"),
            ("part-three/chapter-three.md", "# Chapter Three"));

        try
        {
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-one/part.md", 4);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-two/part.md",
                "part-two/chapter-two.md",
                string.Empty,
                "part-one/part.md",
                string.Empty,
                "part-one/chapter-one.md",
                string.Empty,
                "part-three/part.md",
                "part-three/chapter-three.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_PartBlockRejectsChapterTargetWithoutWriting()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"));

        try
        {
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-two/part.md", 1);

            Assert.False(result.IsSuccess);
            Assert.Contains("Part reorder operation failed for 'part-two/part.md'", result.Error);
            Assert.Contains("Book.txt validation", result.Error);
            Assert.Contains("requested reorder index 1 targets non-Part entry 'part-one/chapter-one.md'", result.Error);
            Assert.Equal(originalBookTxt, ReadBookTxt(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_PartBlockRejectsOutOfRangeIndexWithoutWriting()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-one/part.md", 2);

            Assert.False(result.IsSuccess);
            Assert.Contains("Part reorder operation failed for 'part-one/part.md'", result.Error);
            Assert.Contains("requested reorder index 2 is out of range", result.Error);
            Assert.Equal(originalBookTxt, ReadBookTxt(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_PartBlockPreservesAllEntriesWithoutDuplicatesAfterReload()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md\npart-two/part.md\npart-two/chapter-three.md\npart-three/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-three.md", "# Chapter Three"),
            ("part-three/part.md", "{class: part}\n# Part Three"));

        try
        {
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-one/part.md", 5);
            var reload = await service.ReadNormalizedEntriesAsync(workspace.BookTxtPath);

            Assert.True(result.IsSuccess, result.Error);
            Assert.True(reload.IsSuccess, reload.Error);
            Assert.Equal(new[]
            {
                "part-two/part.md",
                "part-two/chapter-three.md",
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-three/part.md"
            }, reload.Value);
            Assert.Equal(reload.Value!.Count, reload.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AddExistingEntryAsync_InsertsExistingFileAtIndex()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-three.md", "# Chapter Three"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            var service = CreateService();

            var result = await service.AddExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-two.md", 2);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AddExistingEntryAfterPartAsync_InsertsAfterPartDivider()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\n\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            var service = CreateService();

            var result = await service.AddExistingEntryAfterPartAsync(workspace.BookTxtPath, "part-one/chapter-two.md", "part-one/part.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                string.Empty,
                "part-one/chapter-two.md",
                "part-one/chapter-one.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_ExcludeEntryAsync_RemovesBookLineAndAddsManifestPath()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.ExcludeEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[] { "part-one/part.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.True(File.Exists(Path.Combine(workspace.Root, "part-one", "chapter-one.md")));
            Assert.Equal(new[] { "part-one/chapter-one.md" }, await LoadExcludedPathsAsync(workspace.Root));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAsync_InsertsAtIndexAndRemovesManifestPath()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            var manifest = new ExclusionManifestService(new MetadataStore());
            var exclude = await manifest.ExcludeAsync(workspace.Root, "part-one/chapter-one.md");
            Assert.True(exclude.IsSuccess, exclude.Error);
            var service = CreateService();

            var result = await service.IncludeExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 1);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Empty(await LoadExcludedPathsAsync(workspace.Root));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAfterPartAsync_InsertsAfterPartAndRemovesManifestPath()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\n\npart-one/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            var manifest = new ExclusionManifestService(new MetadataStore());
            var exclude = await manifest.ExcludeAsync(workspace.Root, "part-one/chapter-one.md");
            Assert.True(exclude.IsSuccess, exclude.Error);
            var service = CreateService();

            var result = await service.IncludeExistingEntryAfterPartAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-one/part.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                string.Empty,
                "part-one/chapter-one.md",
                "part-one/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Empty(await LoadExcludedPathsAsync(workspace.Root));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAsync_RejectsDuplicateBookTxtEntry()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var manifest = new RecordingManifestService();
            var service = CreateService(exclusionManifestService: manifest);

            var result = await service.IncludeExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 1);

            Assert.False(result.IsSuccess);
            Assert.Contains("already exists", result.Error);
            Assert.Equal(0, manifest.IncludeCalls);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAsync_RejectsMissingFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"));

        try
        {
            var manifest = new RecordingManifestService();
            var service = CreateService(exclusionManifestService: manifest);

            var result = await service.IncludeExistingEntryAsync(workspace.BookTxtPath, "part-one/missing.md", 1);

            Assert.False(result.IsSuccess);
            Assert.Contains("does not exist", result.Error);
            Assert.Equal(0, manifest.IncludeCalls);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAsync_RejectsInvalidIndex()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var manifest = new RecordingManifestService();
            var service = CreateService(exclusionManifestService: manifest);

            var result = await service.IncludeExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 2);

            Assert.False(result.IsSuccess);
            Assert.Contains("out of range", result.Error);
            Assert.Equal(0, manifest.IncludeCalls);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAfterPartAsync_RejectsMissingPartPath()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var manifest = new RecordingManifestService();
            var service = CreateService(exclusionManifestService: manifest);

            var result = await service.IncludeExistingEntryAfterPartAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/part.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("Part entry 'part-two/part.md' was not found", result.Error);
            Assert.Equal(0, manifest.IncludeCalls);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_ExcludeEntryAsync_RejectsPathOutsideWorkspace()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"));

        try
        {
            var manifest = new RecordingManifestService();
            var service = CreateService(exclusionManifestService: manifest);

            var result = await service.ExcludeEntryAsync(workspace.BookTxtPath, "../escape.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("outside the manuscript root", result.Error);
            Assert.Equal(0, manifest.ExcludeCalls);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_ExcludeEntryAsync_PrunesStaleManifestPathsWhenSaving()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(workspace.Root))!);
            await File.WriteAllTextAsync(
                ManifestPath(workspace.Root),
                "{\"schemaVersion\":1,\"excludedPaths\":[\"part-one/stale.md\"]}");
            var service = CreateService();

            var result = await service.ExcludeEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[] { "part-one/chapter-one.md" }, await LoadExcludedPathsAsync(workspace.Root));
            Assert.DoesNotContain("stale.md", await File.ReadAllTextAsync(ManifestPath(workspace.Root)));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAsync_PrunesStaleManifestPathsWhenSaving()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(workspace.Root))!);
            await File.WriteAllTextAsync(
                ManifestPath(workspace.Root),
                "{\"schemaVersion\":1,\"excludedPaths\":[\"part-one/chapter-one.md\",\"part-one/stale.md\"]}");
            var service = CreateService();

            var result = await service.IncludeExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 1);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Empty(await LoadExcludedPathsAsync(workspace.Root));
            Assert.DoesNotContain("stale.md", await File.ReadAllTextAsync(ManifestPath(workspace.Root)));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_ExcludeEntryAsync_DoesNotUpdateManifestWhenBookTxtWriteFails()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var manifest = new RecordingManifestService();
            var service = CreateService(new ThrowingMetadataStore(), manifest);

            var result = await service.ExcludeEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("Book.txt", result.Error);
            Assert.Contains("Book.txt write or validation", result.Error);
            Assert.Equal(0, manifest.ExcludeCalls);
            Assert.Equal(new[] { "part-one/part.md", "part-one/chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_ExcludeEntryAsync_ReturnsManifestPhaseFailureAfterBookTxtWrite()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService(exclusionManifestService: new FailingManifestService());

            var result = await service.ExcludeEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("manifest save after Book.txt write", result.Error);
            Assert.Equal(new[] { "part-one/part.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task IncludeExclude_IncludeExistingEntryAsync_ReturnsManifestPhaseFailureAfterBookTxtWrite()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService(exclusionManifestService: new FailingManifestService());

            var result = await service.IncludeExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 1);

            Assert.False(result.IsSuccess);
            Assert.Contains("manifest save after Book.txt write", result.Error);
            Assert.Equal(new[] { "part-one/part.md", "part-one/chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_MovesAcrossParts_UpdatesBookTxtRegistryAndManifest()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"));

        try
        {
            const string uuid = "chapter-uuid-1";
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = uuid,
                CurrentPath = "part-one/chapter-one.md",
                Orphaned = false,
                Title = "Chapter One"
            });
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(workspace.Root))!);
            await File.WriteAllTextAsync(
                ManifestPath(workspace.Root),
                "{\"schemaVersion\":1,\"excludedPaths\":[\"part-two/chapter-one.md\"]}");
            var service = CreateService();

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                2);

            Assert.True(result.IsSuccess, result.Error);
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-two/part.md",
                "part-two/chapter-one.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));

            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.True(registry.ContainsKey(uuid));
            Assert.Equal("part-two/chapter-one.md", registry[uuid].CurrentPath);
            Assert.Equal("Chapter One", registry[uuid].Title);
            Assert.Empty(await LoadExcludedPathsAsync(workspace.Root));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task CoreContract_ExcludeIncludeMoveAndReloadRead_PreservesManifestRegistryAndBookTxt()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md\npart-two/part.md\npart-two/chapter-three.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-one/excluded.md", "# Excluded"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-three.md", "# Chapter Three"));

        try
        {
            const string movedUuid = "chapter-uuid-2";
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = movedUuid,
                CurrentPath = "part-one/chapter-two.md",
                Orphaned = false,
                Title = "Chapter Two"
            });
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(workspace.Root))!);
            await File.WriteAllTextAsync(
                ManifestPath(workspace.Root),
                "{\"schemaVersion\":1,\"excludedPaths\":[\"part-one/excluded.md\",\"part-two/chapter-two.md\"]}");
            var service = CreateService();

            var exclude = await service.ExcludeEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md");
            Assert.True(exclude.IsSuccess, exclude.Error);

            var include = await service.IncludeExistingEntryAfterPartAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/part.md");
            Assert.True(include.IsSuccess, include.Error);

            var move = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-two.md",
                "part-two/chapter-two.md",
                newIndex: 4);
            Assert.True(move.IsSuccess, move.Error);

            var reloadRead = await service.ReadNormalizedEntriesAsync(workspace.BookTxtPath);
            Assert.True(reloadRead.IsSuccess, reloadRead.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-two/part.md",
                "part-one/chapter-one.md",
                "part-two/chapter-three.md",
                "part-two/chapter-two.md"
            }, reloadRead.Value);

            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-two.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-two.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));

            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.True(registry.ContainsKey(movedUuid));
            Assert.Equal("part-two/chapter-two.md", registry[movedUuid].CurrentPath);
            Assert.False(registry[movedUuid].Orphaned);

            Assert.Equal(new[] { "part-one/excluded.md" }, await LoadExcludedPathsAsync(workspace.Root));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_ManifestSaveFailureLeavesCommittedMoveOnDisk()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"));

        try
        {
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "chapter-uuid-1",
                CurrentPath = "part-one/chapter-one.md",
                Orphaned = false,
                Title = "Chapter One"
            });
            var service = CreateService(exclusionManifestService: new FailingManifestService());

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                2);

            Assert.False(result.IsSuccess);
            Assert.Contains("manifest save after file move, Book.txt write, and registry update", result.Error);
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-two/part.md",
                "part-two/chapter-one.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));

            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.Equal("part-two/chapter-one.md", registry["chapter-uuid-1"].CurrentPath);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_TargetConflictFailsWithoutWrites()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md\npart-two/chapter-one.md"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/chapter-one.md", "# Existing Target"));

        try
        {
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "chapter-uuid-1",
                CurrentPath = "part-one/chapter-one.md"
            });
            var service = CreateService();

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                1);

            Assert.False(result.IsSuccess);
            Assert.Contains("target entry already exists", result.Error);
            Assert.Equal(originalBookTxt, ReadBookTxt(workspace.BookTxtPath));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.Equal("# Existing Target", File.ReadAllText(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.Equal("part-one/chapter-one.md", registry["chapter-uuid-1"].CurrentPath);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_BookTxtWriteFailureRollsFileBackAndReportsPhase()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md\npart-two/part.md"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"));

        try
        {
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "chapter-uuid-1",
                CurrentPath = "part-one/chapter-one.md"
            });
            var service = CreateService(new PathAwareThrowingMetadataStore(path => path.EndsWith("Book.txt", StringComparison.OrdinalIgnoreCase)));

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                1);

            Assert.False(result.IsSuccess);
            Assert.Contains("Book.txt write", result.Error);
            Assert.Contains("rollback attempted and manuscript state was restored", result.Error);
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
            Assert.Equal(originalBookTxt, ReadBookTxt(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_BookTxtWriteFailureReportsRollbackFailure()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md\npart-two/part.md"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"));

        try
        {
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "chapter-uuid-1",
                CurrentPath = "part-one/chapter-one.md"
            });
            var sourceAbsolutePath = AbsolutePath(workspace.Root, "part-one/chapter-one.md");
            var service = CreateService(new PathAwareThrowingMetadataStore(
                path => path.EndsWith("Book.txt", StringComparison.OrdinalIgnoreCase),
                beforeThrow: path => Directory.CreateDirectory(sourceAbsolutePath)));

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                1);

            Assert.False(result.IsSuccess);
            Assert.Contains("Book.txt write", result.Error);
            Assert.Contains("rollback attempted but rollback failed", result.Error);
            Assert.Contains("part-one/chapter-one.md", result.Error);
            Assert.Contains("part-two/chapter-one.md", result.Error);
            Assert.True(Directory.Exists(sourceAbsolutePath));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_RegistrySaveFailureRestoresBookTxtAndFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md\npart-two/part.md"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"));

        try
        {
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "chapter-uuid-1",
                CurrentPath = "part-one/chapter-one.md"
            });
            var service = CreateService(new PathAwareThrowingMetadataStore(
                path => path.EndsWith("chapter-registry.json", StringComparison.OrdinalIgnoreCase)));

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                1);

            Assert.False(result.IsSuccess);
            Assert.Contains("registry update", result.Error);
            Assert.Contains("rollback attempted and manuscript state was restored", result.Error);
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
            Assert.Equal(new[] { "part-one/chapter-one.md", "part-two/part.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.Equal("part-one/chapter-one.md", registry["chapter-uuid-1"].CurrentPath);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_AmbiguousRegistryIdentityFailsBeforeFileMove()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            await SaveRegistryAsync(
                workspace.Root,
                new ChapterRegistryEntry { Uuid = "chapter-uuid-1", CurrentPath = "part-one/chapter-one.md" },
                new ChapterRegistryEntry { Uuid = "chapter-uuid-2", CurrentPath = "part-one/chapter-one.md" });
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            var service = CreateService();

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                0);

            Assert.False(result.IsSuccess);
            Assert.Contains("registry validation", result.Error);
            Assert.Contains("ambiguous", result.Error);
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-two/chapter-one.md")));
            Assert.Equal(originalBookTxt, ReadBookTxt(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_RejectsInvalidReplacementPath()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "../escape.md",
                0);

            Assert.False(result.IsSuccess);
            Assert.Contains("outside the manuscript root", result.Error);
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task PathMove_MoveEntryAsync_RejectsMissingSourceFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md"));

        try
        {
            var service = CreateService();

            var result = await service.MoveEntryAsync(
                workspace.BookTxtPath,
                "part-one/chapter-one.md",
                "part-two/chapter-one.md",
                0);

            Assert.False(result.IsSuccess);
            Assert.Contains("source file", result.Error);
            Assert.Contains("does not exist", result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateNewChapterAsync_CreatesFileAndBookEntry()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.CreateNewChapterAsync(workspace.BookTxtPath, "part-one/chapter-two.md", "# Chapter Two", 2);

            Assert.True(result.IsSuccess, result.Error);
            Assert.True(File.Exists(Path.Combine(workspace.Root, "part-one", "chapter-two.md")));
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameEntryAsync_ChapterRename_MovesFileRewritesBookTxtHeadingAndPreservesUuidMetadataAfterReload()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One\n\nBody"));

        try
        {
            const string uuid = "chapter-uuid-rename";
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "part-uuid",
                CurrentPath = "part-one/part.md",
                Orphaned = false,
                Title = "Part One"
            }, new ChapterRegistryEntry
            {
                Uuid = uuid,
                CurrentPath = "part-one/chapter-one.md",
                Orphaned = false,
                Title = "Chapter One"
            });
            await SeedUuidSidecarsAsync(workspace.Root, uuid);
            var service = CreateService();

            var result = await service.RenameEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", "part-one/chapter-renamed.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.False(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-renamed.md")));
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-renamed.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.StartsWith("# Chapter Renamed", File.ReadAllText(AbsolutePath(workspace.Root, "part-one/chapter-renamed.md")));

            using var manuscriptService = new ManuscriptService(new FakeNotificationService());
            var reload = await manuscriptService.LoadWorkspaceAsync(workspace.Root);
            Assert.True(reload.IsSuccess, reload.Error);
            var renamedNode = Assert.Single(reload.Value!.Nodes.Items, node => node.RelativePath == "part-one/chapter-renamed.md");
            Assert.Equal("Chapter Renamed", renamedNode.Title);

            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.Equal("part-one/chapter-renamed.md", registry[uuid].CurrentPath);
            await AssertUuidSidecarsAsync(workspace.Root, uuid);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    public async Task RenameEntryAsync_ChapterRename_KeepsBomLineEndingsTrailingNewlineAndBlock(string le, bool bom)
    {
        var block = new EmbeddedStoryMetadata("story-id", ChapterStatus.Editing, null, null, null, null, "Keep me.", null);
        var prose = $"{(bom ? "\uFEFF" : "")}Intro line{le}# Chapter One{le}{le}Body{le}";
        var workspace = CreateWorkspace(("Book.txt", "chapter-one.md"));
        var source = AbsolutePath(workspace.Root, "chapter-one.md");
        await File.WriteAllBytesAsync(source,
            new System.Text.UTF8Encoding(false).GetBytes(StoryFileFormat.Merge(prose, block, le)));

        try
        {
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "story-id", CurrentPath = "chapter-one.md", Orphaned = false, Title = "Chapter One"
            });
            var result = await CreateService().RenameEntryAsync(workspace.BookTxtPath, "chapter-one.md", "chapter-renamed.md");

            Assert.True(result.IsSuccess, result.Error);
            var text = new System.Text.UTF8Encoding(false).GetString(
                await File.ReadAllBytesAsync(AbsolutePath(workspace.Root, "chapter-renamed.md")));
            var expectedProse = $"{(bom ? "\uFEFF" : "")}Intro line{le}# Chapter Renamed{le}{le}Body{le}";
            Assert.Equal(StoryFileFormat.Merge(expectedProse, block, le), text);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameEntryAsync_ChapterRename_InsertsHeadingWithTheFileLineEnding()
    {
        var workspace = CreateWorkspace(("Book.txt", "chapter-one.md"), ("chapter-one.md", "Body\r\nMore\r\n"));
        try
        {
            await SaveRegistryAsync(workspace.Root, new ChapterRegistryEntry
            {
                Uuid = "story-id", CurrentPath = "chapter-one.md", Orphaned = false, Title = "Chapter One"
            });
            var result = await CreateService().RenameEntryAsync(workspace.BookTxtPath, "chapter-one.md", "chapter-renamed.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal("# Chapter Renamed\r\nBody\r\nMore\r\n",
                await File.ReadAllTextAsync(AbsolutePath(workspace.Root, "chapter-renamed.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameEntryAsync_PartFolderRename_MovesFolderRewritesDescendantsHeadingAndRegistryPaths()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/nested/chapter-two.md\npart-two/part.md"),
            ("part-one/part.md", "{class: part}\n\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/nested/chapter-two.md", "# Chapter Two"),
            ("part-two/part.md", "{class: part}\n# Part Two"));

        try
        {
            const string partUuid = "part-uuid-rename";
            const string chapterUuid = "chapter-uuid-inside-part";
            const string nestedUuid = "nested-chapter-uuid";
            await SaveRegistryAsync(workspace.Root,
                new ChapterRegistryEntry { Uuid = partUuid, CurrentPath = "part-one/part.md", Orphaned = false, Title = "Part One" },
                new ChapterRegistryEntry { Uuid = chapterUuid, CurrentPath = "part-one/chapter-one.md", Orphaned = false, Title = "Chapter One" },
                new ChapterRegistryEntry { Uuid = nestedUuid, CurrentPath = "part-one/nested/chapter-two.md", Orphaned = false, Title = "Chapter Two" },
                new ChapterRegistryEntry { Uuid = "part-two-uuid", CurrentPath = "part-two/part.md", Orphaned = false, Title = "Part Two" });
            await SeedUuidSidecarsAsync(workspace.Root, chapterUuid);
            var service = CreateService();

            var result = await service.RenameEntryAsync(workspace.BookTxtPath, "part-one/part.md", "renamed-part/part.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.False(Directory.Exists(AbsolutePath(workspace.Root, "part-one")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "renamed-part/part.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "renamed-part/chapter-one.md")));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "renamed-part/nested/chapter-two.md")));
            Assert.Equal(new[]
            {
                "renamed-part/part.md",
                "renamed-part/chapter-one.md",
                "renamed-part/nested/chapter-two.md",
                "part-two/part.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Contains("# Renamed Part", File.ReadAllText(AbsolutePath(workspace.Root, "renamed-part/part.md")));

            using var manuscriptService = new ManuscriptService(new FakeNotificationService());
            var reload = await manuscriptService.LoadWorkspaceAsync(workspace.Root);
            Assert.True(reload.IsSuccess, reload.Error);
            Assert.Equal("Renamed Part", reload.Value!.Nodes.Items.Single(node => node.RelativePath == "renamed-part/part.md").Title);

            var registry = await LoadRegistryAsync(workspace.Root);
            Assert.Equal("renamed-part/part.md", registry[partUuid].CurrentPath);
            Assert.Equal("renamed-part/chapter-one.md", registry[chapterUuid].CurrentPath);
            Assert.Equal("renamed-part/nested/chapter-two.md", registry[nestedUuid].CurrentPath);
            await AssertUuidSidecarsAsync(workspace.Root, chapterUuid);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameEntryAsync_TargetConflictRejectsBeforeMutation()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md\npart-one/chapter-renamed.md"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-renamed.md", "# Existing Target"));

        try
        {
            var originalBookTxt = ReadBookTxt(workspace.BookTxtPath);
            await SaveRegistryAsync(workspace.Root,
                new ChapterRegistryEntry { Uuid = "source-uuid", CurrentPath = "part-one/chapter-one.md" },
                new ChapterRegistryEntry { Uuid = "target-uuid", CurrentPath = "part-one/chapter-renamed.md" });
            var service = CreateService();

            var result = await service.RenameEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", "part-one/chapter-renamed.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("conflict validation", result.Error);
            Assert.Equal(originalBookTxt, ReadBookTxt(workspace.BookTxtPath));
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.Equal("# Existing Target", File.ReadAllText(AbsolutePath(workspace.Root, "part-one/chapter-renamed.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameEntryAsync_MissingSourceRejectsBeforeMutation()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md"));

        try
        {
            var service = CreateService();

            var result = await service.RenameEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", "part-one/chapter-renamed.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("file move validation", result.Error);
            Assert.Contains("source file", result.Error);
            Assert.Equal(new[] { "part-one/chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameEntryAsync_CaseOnlyRenameRejectsConsistently()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/chapter-one.md"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.RenameEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", "part-one/Chapter-One.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("case-only path renames are not supported", result.Error);
            Assert.True(File.Exists(AbsolutePath(workspace.Root, "part-one/chapter-one.md")));
            Assert.Equal(new[] { "part-one/chapter-one.md" }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveEntryAsync_RemovesBookLineWithoutDeletingFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.RemoveEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[] { "part-one/part.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.True(File.Exists(Path.Combine(workspace.Root, "part-one", "chapter-one.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteChapterFileAsync_RemovesBookLineAndFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.DeleteChapterFileAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[] { "part-one/part.md" }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.False(File.Exists(Path.Combine(workspace.Root, "part-one", "chapter-one.md")));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadNormalizedEntriesAsync_FailsWhenBookTxtIsMissing()
    {
        var workspace = CreateWorkspace();

        try
        {
            var service = CreateService();

            var result = await service.ReadNormalizedEntriesAsync(workspace.BookTxtPath);

            Assert.False(result.IsSuccess);
            Assert.Contains(workspace.BookTxtPath, result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadNormalizedEntriesAsync_FailsOnDuplicateEntries()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"));

        try
        {
            var service = CreateService();

            var result = await service.ReadNormalizedEntriesAsync(workspace.BookTxtPath);

            Assert.False(result.IsSuccess);
            Assert.Contains("Duplicate Book.txt entry", result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AddExistingEntryAsync_RejectsTraversalOutsideManuscriptRoot()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"));

        try
        {
            var service = CreateService();

            var result = await service.AddExistingEntryAsync(workspace.BookTxtPath, "../escape.md", 1);

            Assert.False(result.IsSuccess);
            Assert.Contains("outside the manuscript root", result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AddExistingEntryAsync_RejectsMissingFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md"),
            ("part-one/part.md", "{class: part}\n# Part One"));

        try
        {
            var service = CreateService();

            var result = await service.AddExistingEntryAsync(workspace.BookTxtPath, "part-one/chapter-missing.md", 1);

            Assert.False(result.IsSuccess);
            Assert.Contains("does not exist", result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_RejectsUnknownPath()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-one/chapter-missing.md", 0);

            Assert.False(result.IsSuccess);
            Assert.Contains("was not found", result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteChapterFileAsync_RejectsMissingFile()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"));

        try
        {
            var service = CreateService();

            var result = await service.DeleteChapterFileAsync(workspace.BookTxtPath, "part-one/chapter-one.md");

            Assert.False(result.IsSuccess);
            Assert.Contains("does not exist", result.Error);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ReorderEntryAsync_LeavesOriginalBookTxtWhenAtomicWriteFails()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var original = ReadBookTxt(workspace.BookTxtPath);
            var service = CreateService(new ThrowingMetadataStore());

            var result = await service.ReorderEntryAsync(workspace.BookTxtPath, "part-one/chapter-one.md", 0);

            Assert.False(result.IsSuccess);
            Assert.Equal(original, ReadBookTxt(workspace.BookTxtPath));
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateNewPartAsync_CreatesPartFileAndInsertsIntoBookTxt()
    {
        var workspace = CreateWorkspace(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"));

        try
        {
            var service = CreateService();

            var result = await service.CreateNewPartAsync(
                workspace.BookTxtPath,
                "part-two/part.md",
                "Part Two",
                index: 2);

            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-two/part.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));

            var partPath = Path.Combine(workspace.Root, "part-two", "part.md");
            Assert.True(File.Exists(partPath));
            var content = File.ReadAllText(partPath);
            Assert.Contains("{class: part}", content);
            Assert.Contains("# Part Two", content);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public void ShowError(string message)
        {
        }

        public void ShowInfo(string message)
        {
        }

        public void ShowSuccess(string message)
        {
        }
    }

    private sealed class RecordingManifestService : IExclusionManifestService
    {
        public int IncludeCalls { get; private set; }

        public int ExcludeCalls { get; private set; }

        public Task<Result<ExclusionManifest>> LoadAsync(string workspaceRoot)
            => Task.FromResult(Result<ExclusionManifest>.Ok(new ExclusionManifest()));

        public Task<Result<ExclusionManifest>> SaveAsync(string workspaceRoot, ExclusionManifest manifest)
            => Task.FromResult(Result<ExclusionManifest>.Ok(manifest));

        public Task<Result<ExclusionManifest>> ExcludeAsync(string workspaceRoot, string relativePath)
        {
            ExcludeCalls++;
            return Task.FromResult(Result<ExclusionManifest>.Ok(new ExclusionManifest
            {
                ExcludedPaths = new[] { relativePath.Replace('\\', '/') }
            }));
        }

        public Task<Result<ExclusionManifest>> IncludeAsync(string workspaceRoot, string relativePath)
        {
            IncludeCalls++;
            return Task.FromResult(Result<ExclusionManifest>.Ok(new ExclusionManifest()));
        }
    }

    private sealed class FailingManifestService : IExclusionManifestService
    {
        public Task<Result<ExclusionManifest>> LoadAsync(string workspaceRoot)
            => Task.FromResult(Result<ExclusionManifest>.Fail("simulated manifest load failure"));

        public Task<Result<ExclusionManifest>> SaveAsync(string workspaceRoot, ExclusionManifest manifest)
            => Task.FromResult(Result<ExclusionManifest>.Fail("simulated manifest save failure"));

        public Task<Result<ExclusionManifest>> ExcludeAsync(string workspaceRoot, string relativePath)
            => Task.FromResult(Result<ExclusionManifest>.Fail("simulated manifest save failure"));

        public Task<Result<ExclusionManifest>> IncludeAsync(string workspaceRoot, string relativePath)
            => Task.FromResult(Result<ExclusionManifest>.Fail("simulated manifest save failure"));
    }

    private sealed class PathAwareThrowingMetadataStore : IMetadataStore
    {
        private readonly Func<string, bool> _shouldThrow;
        private readonly Action<string>? _beforeThrow;
        private readonly MetadataStore _inner = new();

        public PathAwareThrowingMetadataStore(Func<string, bool> shouldThrow, Action<string>? beforeThrow = null)
        {
            _shouldThrow = shouldThrow;
            _beforeThrow = beforeThrow;
        }

        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            if (_shouldThrow(absolutePath))
            {
                _beforeThrow?.Invoke(absolutePath);
                throw new IOException($"simulated atomic write failure for {Path.GetFileName(absolutePath)}");
            }

            return _inner.WriteTextAtomicAsync(absolutePath, content);
        }
    }

    private sealed class ThrowingMetadataStore : IMetadataStore
    {
        public Task WriteTextAtomicAsync(string absolutePath, string content)
            => throw new IOException("simulated atomic write failure");
    }
}

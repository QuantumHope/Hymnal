using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;

namespace Hymnal.Core.Tests.Infrastructure;

public class NotesServiceTests
{
    private static string MakeTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyString()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new MetadataStore();
            var sut = new NotesService(store);

            var result = await sut.LoadAsync(Path.Combine(dir, "nonexistent.md"));

            Assert.Equal(string.Empty, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesFileViaMetadataStore()
    {
        var dir = MakeTempDir();
        try
        {
            var notesPath = Path.Combine(dir, ".hymnal-data", "notes", "chapter_01.md");
            var store = new MetadataStore();
            var sut = new NotesService(store);

            await sut.SaveAsync(notesPath, "My notes");

            Assert.True(File.Exists(notesPath));
            Assert.Equal("My notes", await File.ReadAllTextAsync(notesPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DeriveNotesPath_ReplacesSlashesWithUnderscores()
    {
        var workspaceRoot = Path.Combine("C:", "Projects", "book");
        var result = INotesService.DeriveNotesPath(workspaceRoot, "part1/chapter01.md");

        Assert.DoesNotContain("/", Path.GetFileName(result));
        Assert.Equal("part1_chapter01.md", Path.GetFileName(result));
    }

    [Fact]
    public void DeriveNotesPath_PlacesUnderHymnalDataNotes()
    {
        var workspaceRoot = Path.Combine("C:", "Projects", "book");
        var result = INotesService.DeriveNotesPath(workspaceRoot, "chapter.md");

        var expected = Path.Combine(workspaceRoot, ".hymnal-data", "notes", "chapter.md");
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task LoadAsync_ExistingFile_ReturnsContent()
    {
        var dir = MakeTempDir();
        try
        {
            var notesPath = Path.Combine(dir, "note.md");
            await File.WriteAllTextAsync(notesPath, "saved notes");
            var store = new MetadataStore();
            var sut = new NotesService(store);

            var result = await sut.LoadAsync(notesPath);

            Assert.Equal("saved notes", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile_AndIgnoresAMissingOneOrFolder()
    {
        var dir = MakeTempDir();
        try
        {
            var sut = new NotesService(new MetadataStore());
            var notesPath = Path.Combine(dir, ".hymnal-data", "notes", "chapter_01.md");
            await sut.SaveAsync(notesPath, "Notes.");

            await sut.DeleteAsync(notesPath);
            Assert.False(File.Exists(notesPath));

            await sut.DeleteAsync(notesPath);
            await sut.DeleteAsync(Path.Combine(dir, "no-such-folder", "x.md"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

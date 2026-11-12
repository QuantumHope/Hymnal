using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;
using Xunit;

namespace Hymnal.Core.Tests.Services;

public sealed class CollectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hymnal-collections-" + Guid.NewGuid().ToString("N"));
    private readonly CollectionService _service = new(new MetadataStore());

    public CollectionServiceTests()
    {
        Directory.CreateDirectory(_root);
        Write("Book.txt", "lighthouse.md\nlong/part.md\nlong/scene-a.md\n");
        Write("lighthouse.md", "# Lighthouse\n\nThe lamp turns.\n");
        Write("long/part.md", "{class: part}\n# The Long Story\n");
        Write("long/scene-a.md", "# Scene A\n\nArrival.\n");
        Write("harbour.md", "# Harbour\n\nBoats.\n");
        Write("README.md", "# Repo readme\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Naming_the_open_collection_gives_it_a_folder_and_a_title()
    {
        Assert.Null(await _service.GetActiveSlugAsync(_root));

        var slug = await _service.SetActiveTitleAsync(_root, "Night Shifts & Other Stories");

        Assert.True(slug.IsSuccess, slug.Error);
        Assert.Equal("night-shifts-other-stories", slug.Value);
        Assert.Equal("Night Shifts & Other Stories", await _service.GetActiveTitleAsync(_root));
        Assert.True(File.Exists(Path.Combine(_root, "collections", slug.Value!, "collection.json")));

        // Renaming keeps the folder.
        Assert.Equal(slug.Value, (await _service.SetActiveTitleAsync(_root, "Night Shifts")).Value);
        Assert.Equal("Night Shifts", await _service.GetActiveTitleAsync(_root));
    }

    [Fact]
    public async Task Switching_swaps_book_txt_and_keeps_both_lists()
    {
        var first = (await _service.SetActiveTitleAsync(_root, "First")).Value!;
        Write(".hymnal-data/exclusions.json", "{\"schemaVersion\":1,\"excludedPaths\":[\"harbour.md\"]}");
        var second = (await _service.CreateAsync(_root, "Second")).Value!;

        Assert.True((await _service.SwitchAsync(_root, second)).IsSuccess);
        Assert.Equal("", Read("Book.txt"));
        Assert.False(File.Exists(Path.Combine(_root, ".hymnal-data", "exclusions.json")));
        Assert.Equal("lighthouse.md\nlong/part.md\nlong/scene-a.md\n", Read($"collections/{first}/Book.txt"));
        Assert.Contains("harbour.md", Read($"collections/{first}/exclusions.json"));
        Assert.False(File.Exists(Path.Combine(_root, "collections", second, "Book.txt")));

        Write("Book.txt", "harbour.md\n");
        Assert.True((await _service.SwitchAsync(_root, first)).IsSuccess);
        Assert.Equal("lighthouse.md\nlong/part.md\nlong/scene-a.md\n", Read("Book.txt"));
        Assert.Contains("harbour.md", Read(".hymnal-data/exclusions.json"));
        Assert.Equal("harbour.md\n", Read($"collections/{second}/Book.txt"));

        var list = (await _service.ListAsync(_root)).Value!;
        Assert.Equal([("First", true, 3), ("Second", false, 1)], list.Select(c => (c.Title, c.IsActive, c.EntryCount)));
    }

    [Fact]
    public async Task Switching_needs_the_open_collection_named_first()
    {
        var other = (await _service.CreateAsync(_root, "Other")).Value!;
        var result = await _service.SwitchAsync(_root, other);
        Assert.False(result.IsSuccess);
        Assert.Contains("Name the open collection", result.Error);
        Assert.Equal("lighthouse.md\nlong/part.md\nlong/scene-a.md\n", Read("Book.txt"));
    }

    [Fact]
    public async Task An_interrupted_switch_is_finished_on_recover()
    {
        var first = (await _service.SetActiveTitleAsync(_root, "First")).Value!;
        var second = (await _service.CreateAsync(_root, "Second")).Value!;
        Write($"collections/{second}/Book.txt", "harbour.md\n");
        // Interrupted right after the commit point: the open list was saved and Second recorded as active.
        Write($"collections/{first}/Book.txt", Read("Book.txt"));
        Write(".hymnal-data/collections.json", $"{{\"schemaVersion\":1,\"active\":\"{second}\"}}");

        await _service.RecoverAsync(_root);

        Assert.Equal("harbour.md\n", Read("Book.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "collections", second, "Book.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "collections", first, "Book.txt")));
    }

    [Fact]
    public async Task Unassigned_stories_are_in_no_collection()
    {
        Assert.Equal(["harbour.md"], (await _service.UnassignedStoriesAsync(_root)).Value);

        await _service.SetActiveTitleAsync(_root, "First");
        var second = (await _service.CreateAsync(_root, "Second")).Value!;
        Write($"collections/{second}/Book.txt", "harbour.md\n");

        Assert.Empty((await _service.UnassignedStoriesAsync(_root)).Value!);
    }

    [Fact]
    public async Task Preview_lists_entries_and_flags_missing_files()
    {
        Write("Book.txt", "lighthouse.md\nlong/part.md\nlong/scene-a.md\ngone.md\n");
        await _service.SetActiveTitleAsync(_root, "Night Shifts");

        var preview = (await _service.PreviewAsync(_root, _root)).Value!;

        Assert.Equal("Night Shifts", preview.CollectionTitle);
        Assert.Equal(["lighthouse.md", "long/part.md", "long/scene-a.md", "gone.md"], preview.Entries.Select(e => e.RelativePath));
        Assert.True(preview.Entries[1].IsPart);
        Assert.False(preview.Entries[3].Exists);
        Assert.Contains(preview.Problems, p => p.Contains("gone.md"));
        Assert.True(preview.CanPublish);
    }

    [Fact]
    public async Task Publish_copies_the_collection_into_manuscript_and_prunes_what_it_no_longer_lists()
    {
        Write("lighthouse.md", StoryFileFormat.Merge("# Lighthouse\n\nThe lamp turns.\n",
            new Hymnal.Core.Models.EmbeddedStoryMetadata("id-1", Hymnal.Core.Models.ChapterStatus.Editing,
                null, null, null, null, null, null), "\n"));
        Assert.Contains("hymnal:v1", Read("lighthouse.md"));
        Write("resources/images/cover.png", "png");

        var published = await _service.PublishAsync(_root, _root);

        Assert.True(published.IsSuccess, published.Error);
        Assert.Equal("lighthouse.md\nlong/part.md\nlong/scene-a.md\n", Read("manuscript/Book.txt"));
        Assert.DoesNotContain("hymnal:v1", Read("manuscript/lighthouse.md"));
        Assert.Equal("{class: part}\n# The Long Story\n", Read("manuscript/long/part.md"));
        Assert.True(File.Exists(Path.Combine(_root, "manuscript", "resources", "images", "cover.png")));

        // The published copy isn't a story of the workspace.
        Assert.Equal(["harbour.md"], (await _service.UnassignedStoriesAsync(_root)).Value);
        var orphans = await new OrphanFileDiscoveryService().DiscoverAsync(_root,
            ["lighthouse.md", "long/part.md", "long/scene-a.md"]);
        Assert.Equal(["harbour.md", "README.md"], orphans.Select(o => o.RelativePath));

        Write("Book.txt", "lighthouse.md\n");
        var again = (await _service.PublishAsync(_root, _root)).Value!;
        Assert.Equal(2, again.FilesRemoved);
        Assert.False(File.Exists(Path.Combine(_root, "manuscript", "long", "scene-a.md")));
    }

    [Fact]
    public async Task Publish_never_overwrites_a_manuscript_folder_hymnal_did_not_write()
    {
        Write("manuscript/Book.txt", "mine.md\n");

        var preview = (await _service.PreviewAsync(_root, _root)).Value!;
        Assert.False(preview.CanPublish);
        Assert.NotNull(preview.BlockedReason);
        Assert.False((await _service.PublishAsync(_root, _root)).IsSuccess);
        Assert.Equal("mine.md\n", Read("manuscript/Book.txt"));
    }

    [Fact]
    public void Projects_already_in_leanpub_layout_publish_as_they_are()
    {
        var manuscript = Path.Combine(_root, "manuscript");
        Assert.False(CollectionService.SupportsCollections(_root, manuscript));
        Assert.Contains("already", CollectionService.PublishBlockedReason(_root, manuscript));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) =>
        File.ReadAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
}

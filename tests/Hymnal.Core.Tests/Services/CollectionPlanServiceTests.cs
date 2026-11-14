using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

/// <summary>The planner's view of an anthology: a library of stories, and collections that order some of them.</summary>
public sealed class CollectionPlanServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hymnal-plan-" + Guid.NewGuid().ToString("N"));
    private readonly CollectionService _collections;
    private readonly CollectionPlanService _planner;

    public CollectionPlanServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".hymnal-data"));
        var store = new MetadataStore();
        _collections = new CollectionService(store);
        _planner = new CollectionPlanService(store, _collections, new WordCountService());

        Write("lighthouse.md", "# Lighthouse\n\nThe lamp turns slowly.\n");
        Write("harbour.md", "# Harbour\n\nBoats.\n");
        Write("drift.md", "# Drift\n\nNothing lists this.\n");
        Write("long/part.md", "{class: part}\n\n# The Long Story\n");
        Write("long/a.md", "# Arrival\n\nOne two.\n");
        Write("long/b.md", "# Departure\n\nThree.\n");
        Write("Book.txt", "lighthouse.md\nlong/part.md\nlong/a.md\nlong/b.md\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative)).Replace("\r\n", "\n");

    private async Task<CollectionPlan> LoadAsync()
    {
        var plan = await _planner.LoadAsync(_root);
        Assert.True(plan.IsSuccess, plan.Error);
        return plan.Value!;
    }

    [Fact]
    public async Task Load_ReadsStoriesOnce_WithMultiSceneStoriesWhole_AndUnlistedFilesAsTheirOwnStories()
    {
        var plan = await LoadAsync();

        var open = Assert.Single(plan.Collections);
        Assert.True(open.Ref.IsOpen);
        Assert.False(open.IsNamed);
        Assert.Equal(["lighthouse.md", "long/part.md"], open.StoryKeys);

        var long_ = plan.Stories.Single(s => s.Key == "long/part.md");
        Assert.True(long_.IsMultiScene);
        Assert.Equal("The Long Story", long_.Title);
        Assert.Equal(2, long_.SceneCount);
        Assert.Equal(["long/part.md", "long/a.md", "long/b.md"], long_.Lines);

        // Not listed anywhere, so in the library on their own; the scenes of a listed story are not.
        Assert.Contains(plan.Stories, s => s.Key == "harbour.md");
        Assert.Contains(plan.Stories, s => s.Key == "drift.md");
        Assert.DoesNotContain(plan.Stories, s => s.Key == "long/a.md");
        Assert.Empty(plan.CollectionsOf("drift.md"));
        // Counted as the sidebar counts (the heading line included), so the numbers match everywhere.
        Assert.Equal(6, plan.Stories.Single(s => s.Key == "lighthouse.md").Words);
    }

    [Fact]
    public async Task A_story_can_be_in_several_collections_and_moves_with_its_scenes()
    {
        Assert.True((await _collections.SetActiveTitleAsync(_root, "Night Shifts")).IsSuccess);
        var harbourSlug = (await _collections.CreateAsync(_root, "Harbour Tales")).Value!;
        var harbour = CollectionRef.Other(harbourSlug);
        var plan = await LoadAsync();
        var long_ = plan.Stories.Single(s => s.Key == "long/part.md");

        // Copy: in both collections.
        Assert.True((await _planner.InsertAsync(_root, harbour, long_, 0)).IsSuccess);
        plan = await LoadAsync();
        Assert.Equal(2, plan.CollectionsOf("long/part.md").Count());
        Assert.Equal("long/part.md\nlong/a.md\nlong/b.md\n", Read($"collections/{harbourSlug}/Book.txt"));

        // Move the lighthouse out of the open collection into Harbour Tales, ahead of the long story.
        var lighthouse = plan.Stories.Single(s => s.Key == "lighthouse.md");
        Assert.True((await _planner.MoveAsync(_root, CollectionRef.Open, harbour, lighthouse, 0)).IsSuccess);
        Assert.Equal("long/part.md\nlong/a.md\nlong/b.md\n", Read("Book.txt"));
        Assert.Equal("lighthouse.md\nlong/part.md\nlong/a.md\nlong/b.md\n", Read($"collections/{harbourSlug}/Book.txt"));
    }

    [Fact]
    public async Task Insert_into_the_same_list_reorders_and_remove_leaves_the_files()
    {
        var plan = await LoadAsync();
        var long_ = plan.Stories.Single(s => s.Key == "long/part.md");
        var harbour = plan.Stories.Single(s => s.Key == "harbour.md");

        // One-file stories reorder among themselves.
        Assert.True((await _planner.InsertAsync(_root, CollectionRef.Open, harbour, 0)).IsSuccess);
        Assert.Equal("harbour.md\nlighthouse.md\nlong/part.md\nlong/a.md\nlong/b.md\n", Read("Book.txt"));

        // A one-file story can't follow a divider (it would become one of its scenes): it stays ahead of it.
        Assert.True((await _planner.InsertAsync(_root, CollectionRef.Open, harbour, 99)).IsSuccess);
        Assert.Equal("lighthouse.md\nharbour.md\nlong/part.md\nlong/a.md\nlong/b.md\n", Read("Book.txt"));
        Assert.True((await _planner.InsertAsync(_root, CollectionRef.Open, long_, 0)).IsSuccess);
        Assert.Equal("lighthouse.md\nharbour.md\nlong/part.md\nlong/a.md\nlong/b.md\n", Read("Book.txt"));
        Assert.True((await _planner.RemoveAsync(_root, CollectionRef.Open, harbour)).IsSuccess);

        Assert.True((await _planner.RemoveAsync(_root, CollectionRef.Open, long_)).IsSuccess);
        Assert.Equal("lighthouse.md\n", Read("Book.txt"));
        Assert.True(File.Exists(Path.Combine(_root, "long/a.md")));

        // Back in the library as a story of its own.
        plan = await LoadAsync();
        Assert.Contains(plan.Stories, s => s.Key == "long/part.md");
        Assert.Empty(plan.CollectionsOf("long/part.md"));
    }
}

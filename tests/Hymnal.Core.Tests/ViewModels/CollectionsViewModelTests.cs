using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Several collections over one folder of stories, switched in a real workspace.</summary>
[Collection("AvaloniaUi")]
public sealed class CollectionsViewModelTests
{
    static CollectionsViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Naming_switching_and_adding_a_story_in_no_collection()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("lighthouse.md", "# Lighthouse\n\nThe lamp turns.\n"),
            ("harbour.md", "# Harbour\n\nBoats.\n"));
        // Book.txt lists only the lighthouse; the harbour story is in no collection.
        await File.WriteAllTextAsync(Path.Combine(root, "Book.txt"), "lighthouse.md\n");
        try
        {
            await Ui(root, 1, async context =>
            {
                var workspace = context.Workspace;
                var collections = new CollectionsViewModel(workspace, new CollectionService(new MetadataStore()),
                    context.Notifications);
                Assert.True(collections.SupportsMultiple);
                Assert.Equal("Collection", workspace.CollectionDisplayName);

                Assert.True(await Within(collections.RenameOpenAsync("Night Shifts")));
                Assert.Equal("Night Shifts", workspace.CollectionDisplayName);
                Assert.Equal(["harbour.md"], await Within(collections.UnassignedAsync()));

                await Within(collections.RefreshAsync());
                Assert.False(collections.HasSeveral);
                var harbourSlug = await Within(collections.CreateAsync("Harbour Tales"));
                Assert.NotNull(harbourSlug);
                await Within(collections.RefreshAsync());
                Assert.True(collections.HasSeveral);
                Assert.True(await Within(collections.OpenAsync(harbourSlug!)));
                await PumpUntilAsync(() => workspace.CollectionTitle == "Harbour Tales");
                Assert.Equal("", await File.ReadAllTextAsync(Path.Combine(root, "Book.txt")));

                await Within(collections.AddToOpenAsync("harbour.md"));
                await PumpUntilAsync(() => File.ReadAllText(Path.Combine(root, "Book.txt")).Contains("harbour.md"));
                Assert.Empty(await Within(collections.UnassignedAsync()));

                var nightSlug = collections.Collections.Single(c => c.Title == "Night Shifts").Slug;
                Assert.True(await Within(collections.OpenAsync(nightSlug)));
                await PumpUntilAsync(() => workspace.CollectionTitle == "Night Shifts");
                Assert.Equal("lighthouse.md\n", await File.ReadAllTextAsync(Path.Combine(root, "Book.txt")));
                Assert.Contains(workspace.Nodes, n => n.Node.RelativePath == "lighthouse.md" && !n.Node.IsExcluded);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task New_collection_opens_at_once_and_an_unnamed_open_one_keeps_its_list_under_the_folder_name()
    {
        var root = CreateWorkspace(AnthologyJson, ("lighthouse.md", "# Lighthouse\n\nThe lamp turns.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var workspace = context.Workspace;
                var collections = new CollectionsViewModel(workspace, new CollectionService(new MetadataStore()),
                    context.Notifications);
                Assert.False(collections.IsOpenCollectionNamed);
                var folderName = collections.DefaultTitleForOpen;
                Assert.Equal(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), folderName);

                Assert.NotNull(await Within(collections.CreateAndOpenAsync("Harbour Tales")));
                await PumpUntilAsync(() => workspace.CollectionTitle == "Harbour Tales");

                // The collection that was open kept its list, under the folder's name.
                await Within(collections.RefreshAsync());
                var kept = collections.Collections.Single(c => !c.IsActive);
                Assert.Equal(folderName, kept.Title);
                Assert.Equal(1, kept.EntryCount);
            });
        }
        finally { DeleteFolder(root); }
    }
}

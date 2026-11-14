using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>The story planner: a library of stories and every collection side by side, edited by dragging.</summary>
[Collection("AvaloniaUi")]
public sealed class CollectionPlannerViewModelTests
{
    static CollectionPlannerViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private static CollectionPlannerViewModel Create(PlanTestContext context, out CollectionsViewModel collections)
    {
        var store = new MetadataStore();
        var service = new CollectionService(store);
        collections = new CollectionsViewModel(context.Workspace, service, context.Notifications);
        return new CollectionPlannerViewModel(context.Workspace,
            new CollectionPlanService(store, service, new WordCountService()),
            new ChapterRegistryService(store), context.Notifications, collections, editor: context.Editor);
    }

    private static string CreateAnthology()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("lighthouse.md", "# Lighthouse\n\nThe lamp turns.\n"),
            ("harbour.md", "# Harbour\n\nBoats.\n"));
        // Book.txt lists only the lighthouse; the harbour story is in no collection.
        File.WriteAllText(Path.Combine(root, "Book.txt"), "lighthouse.md\n");
        return root;
    }

    [Fact]
    public async Task Library_lists_every_story_and_dots_show_where_each_lives()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 1, async context =>
            {
                var planner = Create(context, out _);
                await Within(planner.RefreshAsync());

                Assert.Equal(["Lighthouse", "Harbour"], planner.Library.Select(s => s.Title));
                var open = Assert.Single(planner.Columns);
                Assert.True(open.IsOpen);
                Assert.Equal(["Lighthouse"], open.Entries.Select(e => e.Story.Title));

                var lighthouse = planner.Library.Single(s => s.Title == "Lighthouse");
                var harbour = planner.Library.Single(s => s.Title == "Harbour");
                Assert.Single(lighthouse.Dots);
                Assert.True(harbour.IsUnshelved);
                Assert.NotNull(lighthouse.Uuid);
                Assert.True(lighthouse.HasStatus);

                planner.ShowUnshelvedOnly = true;
                Assert.Equal(["Harbour"], planner.VisibleLibrary.Select(s => s.Title));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Stories_move_and_copy_between_collections_and_the_open_one_reloads_Write()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 1, async context =>
            {
                var planner = Create(context, out var collections);
                Assert.True(await Within(collections.RenameOpenAsync("Night Shifts")));
                Assert.NotNull(await Within(collections.CreateAsync("Harbour Tales")));
                await Within(planner.RefreshAsync());
                Assert.Equal(2, planner.Columns.Count);
                var night = planner.Columns.Single(c => c.IsOpen);
                var harbourTales = planner.Columns.Single(c => !c.IsOpen);
                Assert.NotEqual(night.ColourIndex, harbourTales.ColourIndex);

                // Library → a collection: added.
                var harbour = planner.Library.Single(s => s.Title == "Harbour");
                Assert.True(await Within(planner.AddAsync(harbour, harbourTales, 0)));
                harbourTales = planner.Columns.Single(c => !c.IsOpen);
                Assert.Equal(["Harbour"], harbourTales.Entries.Select(e => e.Story.Title));

                // Copy the lighthouse across (Ctrl): in both, two dots.
                var lighthouse = planner.Library.Single(s => s.Title == "Lighthouse");
                night = planner.Columns.Single(c => c.IsOpen);
                Assert.True(await Within(planner.MoveAsync(lighthouse, night, harbourTales, 0, copy: true)));
                Assert.Equal(2, planner.Library.Single(s => s.Title == "Lighthouse").Dots.Count);

                // Move the harbour into the open collection: Write's list (the workspace) follows.
                harbour = planner.Library.Single(s => s.Title == "Harbour");
                harbourTales = planner.Columns.Single(c => !c.IsOpen);
                night = planner.Columns.Single(c => c.IsOpen);
                Assert.True(await Within(planner.MoveAsync(harbour, harbourTales, night, 1, copy: false)));
                await PumpUntilAsync(() => context.Workspace.Nodes.Any(n => n.Node.RelativePath == "harbour.md"));
                Assert.Equal("lighthouse.md\nharbour.md\n", File.ReadAllText(Path.Combine(root, "Book.txt")).Replace("\r\n", "\n"));
                Assert.Equal(["Lighthouse"], planner.Columns.Single(c => !c.IsOpen).Entries.Select(e => e.Story.Title));

                // Unticking a collection in the story panel takes it out; the story stays in the library.
                planner.Selected = planner.Library.Single(s => s.Title == "Lighthouse");
                var toggle = planner.Toggles.Single(t => !t.Column.IsOpen);
                Assert.True(toggle.IsMember);
                toggle.IsMember = false;
                await PumpUntilAsync(() => planner.Columns.Single(c => !c.IsOpen).IsEmpty);
                Assert.Equal("Lighthouse", planner.Selected?.Title);
                Assert.Single(planner.Selected!.Dots);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Write_opens_a_story_in_the_open_collection_and_beats_ask_by_registry_id()
    {
        var root = CreateAnthology();
        try
        {
            await Ui(root, 1, async context =>
            {
                var planner = Create(context, out _);
                await Within(planner.RefreshAsync());
                ChapterViewModel? opened = null;
                string? beats = null;
                using var a = planner.OpenChapterRequested.Subscribe(c => opened = c);
                using var b = planner.BeatsRequested.Subscribe(u => beats = u);

                var lighthouse = planner.Library.Single(s => s.Title == "Lighthouse");
                planner.Selected = lighthouse;
                Assert.True(planner.SelectedIsInOpen);
                Assert.True(planner.CanPlanSelectedBeats);
                planner.Write(lighthouse);
                planner.PlanBeats(lighthouse);
                Assert.Equal("lighthouse.md", opened?.Node.RelativePath);
                Assert.Equal(lighthouse.Uuid, beats);

                // A story in no collection isn't in Write's list.
                var harbour = planner.Library.Single(s => s.Title == "Harbour");
                planner.Selected = harbour;
                Assert.False(planner.SelectedIsInOpen);
                opened = null;
                planner.Write(harbour);
                Assert.Null(opened);
                Assert.Contains(context.Infos, m => m.Contains("Open a collection"));
            });
        }
        finally { DeleteFolder(root); }
    }
}

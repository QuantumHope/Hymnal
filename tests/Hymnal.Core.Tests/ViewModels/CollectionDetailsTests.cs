using System.IO;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Theming;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>
/// Plan's Details tab, how Plan, the sidebar and the palette reach it, renaming collections, and the palette's
/// collection and theme entries.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class CollectionDetailsTests
{
    static CollectionDetailsTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private static readonly (string, string)[] AnthologyFiles =
    [
        ("lighthouse.md", "# The Lighthouse\n\nThe lamp turns.\n"),
        ("harbour/part.md", "{class: part}\n# Harbour Tales\n"),
        ("harbour/arrival.md", "# Arrival\n\nBoats.\n"),
        ("harbour/departure.md", "# Departure\n\nMore boats.\n"),
    ];

    /// <summary>Names the open collection and adds a second, empty one, before the workspace opens.</summary>
    private static async Task<string> TwoCollectionsAsync(string root)
    {
        var service = new CollectionService(new MetadataStore());
        Assert.True((await service.SetActiveTitleAsync(root, "Storms & Salt")).IsSuccess);
        return (await service.CreateAsync(root, "Winter Pieces")).Value!;
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_counts_use_the_status_each_row_shows()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            await Ui(root, 4, context =>
            {
                // Polishing is a novel status; an anthology shows it as Revising and must count it there.
                context.Node("lighthouse.md").ApplyPhaseData(new PhaseData { Status = ChapterStatus.Polishing });
                var details = context.CreateDetails();
                details.Rebuild();

                Assert.Equal(["Idea 1", "Revising 1"], details.StatusCounts.Select(c => c.Text).Order());
                Assert.Equal(2, details.StatusCounts.Sum(c => c.Count));
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task An_anthology_lists_its_stories_in_order_with_their_scenes()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            await Ui(root, 4, context =>
            {
                var details = context.CreateDetails();
                details.Rebuild();

                Assert.Equal("COLLECTION", details.NounLabel);
                Assert.Equal(["The Lighthouse", "Harbour Tales"], details.Rows.Select(r => r.Title));
                Assert.Equal(["1", "2"], details.Rows.Select(r => r.Number));
                Assert.All(details.Rows, r => Assert.Equal("STORY", r.NounLabel));
                Assert.Equal("2 scenes", details.Rows[1].Detail);
                Assert.StartsWith("2 stories · ", details.Summary);
                Assert.Equal("STORIES, IN ORDER", details.ContentsTitle);
                var idea = Assert.Single(details.StatusCounts);
                Assert.Equal("Idea 2", idea.Text);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_novel_lists_parts_and_their_chapters()
    {
        var root = CreateWorkspace(NovelJson,
            ("one/part.md", "{class: part}\n# Part One\n"),
            ("one/a.md", "# Arrival\n\nText.\n"),
            ("one/b.md", "# Bargain\n\nText.\n"));
        try
        {
            await Ui(root, 3, context =>
            {
                var details = context.CreateDetails();
                details.Rebuild();

                Assert.Equal("BOOK", details.NounLabel);
                Assert.Equal(["Part One", "Arrival", "Bargain"], details.Rows.Select(r => r.Title));
                Assert.Equal([true, false, false], details.Rows.Select(r => r.IsDivider));
                Assert.Equal([false, true, true], details.Rows.Select(r => r.IsIndented));
                Assert.Equal(["", "1", "2"], details.Rows.Select(r => r.Number));
                Assert.StartsWith("2 chapters · ", details.Summary);
                Assert.Equal("CONTENTS, IN ORDER", details.ContentsTitle);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_story_told_in_scenes_opens_on_its_first_scene()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            await Ui(root, 4, context =>
            {
                var details = context.CreateDetails();
                details.Rebuild();
                ChapterViewModel? opened = null;
                using var _ = details.OpenRequested.Subscribe(n => opened = n);

                details.OpenRowCommand.Execute(details.Rows[1]).Subscribe();

                Assert.Equal("harbour/arrival.md", opened?.Node.RelativePath);
                return Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Renaming ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_title_is_renamed_in_place()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            await Ui(root, 4, async context =>
            {
                var collections = context.CreateCollections();
                var details = context.CreateDetails(collections);
                details.Rebuild();
                Assert.False(details.IsNamed);
                Assert.Equal("Collection", details.Title);

                await Within(details.BeginRenameCommand.Execute().ToTask());
                Assert.True(details.IsEditingTitle);
                details.EditTitle = "  Storms & Salt ";
                await Within(details.CommitRenameCommand.Execute().ToTask());

                Assert.False(details.IsEditingTitle);
                Assert.True(details.IsNamed);
                Assert.Equal("Storms & Salt", details.Title);
                Assert.Equal("Storms & Salt", context.Workspace.CollectionTitle);
                Assert.Equal("Storms & Salt",
                    await new CollectionService(new MetadataStore()).GetActiveTitleAsync(root));
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_collection_that_is_not_open_can_be_renamed()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            var other = await TwoCollectionsAsync(root);
            await Ui(root, 4, async context =>
            {
                var collections = context.CreateCollections();
                await Within(collections.RefreshAsync());

                Assert.True(await Within(collections.RenameAsync(other, "Long Nights")));

                Assert.Equal(["Storms & Salt", "Long Nights"], collections.Collections.Select(c => c.Title));
                Assert.Equal("Storms & Salt", context.Workspace.CollectionTitle);

                // The open one, by slug or with none, renames through the workspace too.
                Assert.True(await Within(collections.RenameAsync(null, "Salt")));
                Assert.Equal("Salt", context.Workspace.CollectionTitle);
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── Plan's tabs ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_has_a_Details_tab_beside_the_story_beats()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var details = context.CreateDetails();
                var host = context.CreateHost(details);
                await context.SettleAsync();

                Assert.True(host.ShowsViewSwitch);
                Assert.Equal("Beats", host.BoardTabLabel);
                Assert.False(host.ShowsCardsTab);
                Assert.Same(context.Board, host.Content);
                Assert.True(host.IsBoardTabSelected);

                host.ShowDetails();
                Assert.Same(details, host.Content);
                Assert.True(details.IsActive);
                Assert.False(host.IsBoardTabSelected);

                host.ShowBoardTab();
                Assert.Same(context.Board, host.Content);
                Assert.False(details.IsActive);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_novel_plans_through_the_host_when_it_has_Details()
    {
        var root = CreateWorkspace(NovelJson, ("one.md", "# One\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var details = context.CreateDetails();
                var host = context.CreateHost(details);
                var window = context.CreateMainWindow(host);
                await Within(window.SelectPlanCommand.Execute().ToTask());
                await context.SettleAsync();

                Assert.Same(host, window.SecondaryCentreContent);
                Assert.Same(context.Corkboard, host.Content);
                Assert.Equal("Cards", host.BoardTabLabel);
                Assert.True(host.ShowsViewSwitch);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task The_sidebar_collection_row_opens_Details_in_Plan()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            await Ui(root, 4, async context =>
            {
                var details = context.CreateDetails();
                var host = context.CreateHost(details);
                var window = context.CreateMainWindow(host);
                Assert.Equal(ShellMode.Write, window.ActiveMode);

                await Within(context.Workspace.ShowCollectionDetailsCommand.Execute().ToTask());
                await context.SettleAsync();

                Assert.Equal(ShellMode.Plan, window.ActiveMode);
                Assert.True(host.IsDetailsView);
                Assert.Same(details, host.Content);

                // A row opens its file in Write.
                details.OpenRowCommand.Execute(details.Rows[0]).Subscribe();
                await context.SettleAsync();
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.Equal("lighthouse.md", context.Workspace.SelectedNode?.Node.RelativePath);
            });
        }
        finally { DeleteFolder(root); }
    }

    // ── The palette ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_palette_goes_to_a_collection_by_name_opening_it_first()
    {
        var root = CreateWorkspace(AnthologyJson, AnthologyFiles);
        try
        {
            await TwoCollectionsAsync(root);
            await Ui(root, 4, async context =>
            {
                var collections = context.CreateCollections();
                await Within(collections.RefreshAsync());
                var details = context.CreateDetails(collections);
                var host = context.CreateHost(details);
                var window = context.CreateMainWindow(host, collections: collections);

                await Within(window.ShowGoToCommand.Execute().ToTask());
                var palette = window.CommandPalette;
                var found = palette.Results.Where(r => r.Category == "Collection").Select(r => r.Title).ToList();
                Assert.Equal(["Storms & Salt", "Winter Pieces"], found);

                palette.Query = "winter";
                Assert.Equal("Winter Pieces", palette.Selected?.Title);
                await Within(palette.RunSelectedCommand.Execute().ToTask());
                await context.SettleAsync();

                Assert.Equal("Winter Pieces", context.Workspace.CollectionTitle);
                Assert.Equal(ShellMode.Plan, window.ActiveMode);
                Assert.True(host.IsDetailsView);
                Assert.Equal("Winter Pieces", details.Title);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task The_palette_offers_every_theme_and_marks_the_current_one()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var themes = new FakeThemes();
                var window = context.CreateMainWindow(context.CreateHost(), themes: themes);

                await Within(window.ShowCommandsCommand.Execute().ToTask());
                var palette = window.CommandPalette;
                var themeRows = palette.Results.Where(r => r.Category == "Theme").ToList();
                Assert.Equal(ThemeCatalog.All.Count, themeRows.Count);
                Assert.StartsWith("Current theme", themeRows.Single(r => r.Title == $"Theme: {themes.Current.DisplayName}").Detail);

                palette.Query = ">theme mordor";
                var mordor = ThemeCatalog.All.First(t => t.Id.Contains("mordor"));
                Assert.Equal($"Theme: {mordor.DisplayName}", palette.Selected?.Title);
                await Within(palette.RunSelectedCommand.Execute().ToTask());

                Assert.Equal([mordor.Id], themes.Selected);
            });
        }
        finally { DeleteFolder(root); }
    }

    private sealed class FakeThemes : IThemeService
    {
        public List<string> Selected { get; } = new();
        public IReadOnlyList<ThemeInfo> Available => ThemeCatalog.All;
        public ThemeInfo Current => ThemeCatalog.Default;

        public Task SelectAsync(string id)
        {
            Selected.Add(id);
            return Task.CompletedTask;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;

namespace Hymnal.ViewModels;

/// <summary>
/// Statuses, Gantt phases and labels shown in the UI come from the workspace's
/// <see cref="ProjectProfile"/>; Novel keeps the original lists.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class ProfileDrivenUiTests : IDisposable
{
    /// <summary>Contexts created by the running test; their file watchers are released in <see cref="Dispose"/>.</summary>
    private static readonly List<TestContext> LiveContexts = new();

    public void Dispose()
    {
        foreach (var context in LiveContexts) context.Dispose();
        LiveContexts.Clear();
    }

    static ProfileDrivenUiTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private const string StoryProjectJson = "{ \"schemaVersion\": 1, \"kind\": \"story\", \"storage\": \"sidecar\" }";

    [Fact]
    public async Task Story_chapter_info_status_options_are_four()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var info = context.CreateChapterInfo();

            Assert.Equal(["Idea", "Drafting", "Revising", "Done"], info.StatusOptions.Select(o => o.Label));
            Assert.Equal(
                [ChapterStatus.Planned, ChapterStatus.Drafting, ChapterStatus.Editing, ChapterStatus.Done],
                info.StatusOptions.Select(o => o.Value));
            Assert.Equal(["Drafting", "Editing"], info.PhaseScheduleRows.Select(r => r.PhaseName));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_chapter_info_status_options_are_seven()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 1);
            var info = context.CreateChapterInfo();

            Assert.Equal(
                Enum.GetValues<ChapterStatus>().Select(s => s.ToString()),
                info.StatusOptions.Select(o => o.Label));
            Assert.Equal(Enum.GetValues<ChapterStatus>(), info.StatusOptions.Select(o => o.Value));
            Assert.Equal(GanttProjection.PhaseNames, info.PhaseScheduleRows.Select(r => r.PhaseName));
            Assert.Equal("Chapter Info", info.PanelTitle);
            Assert.Equal("Toggle _Chapter Info", info.ToggleMenuHeader);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_chapter_info_panel_title_is_story_info()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var info = context.CreateChapterInfo();

            Assert.Equal("Story Info", info.PanelTitle);
            Assert.Equal("Toggle _Story Info", info.ToggleMenuHeader);
            Assert.Equal("Story Info (F3)", info.ToggleToolTip);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_gantt_rows_have_two_phase_segments()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var gantt = new GanttViewModel(context.Workspace, context.Notifications);

            var chapterRows = gantt.Rows.Where(r => r.IsChapter).ToList();
            Assert.Equal(2, chapterRows.Count);
            Assert.All(chapterRows, row =>
            {
                Assert.Equal(["Drafting", "Editing"], row.PhaseSegments.Select(s => s.PhaseName));
                Assert.Equal(["Idea", "Drafting", "Revising", "Done"], row.AvailableStatuses.Select(o => o.Label));
            });
            Assert.Equal(["Idea", "Drafting", "Revising", "Done"], gantt.AvailableStatuses.Select(o => o.Label));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Story_gantt_row_keeps_stored_status_when_display_bucket_is_reselected()
    {
        var profile = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));
        var node = new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0);
        var row = new GanttRowViewModel(
            GanttProjection.Project(node, new PhaseData { Status = ChapterStatus.Polishing }, profile), profile);

        Assert.Equal(ChapterStatus.Editing, row.EditableDisplayStatus);
        Assert.Equal("Revising", row.StatusDisplay);

        row.EditableDisplayStatus = ChapterStatus.Editing;
        Assert.Equal(ChapterStatus.Polishing, row.EditableStatus);

        row.EditableDisplayStatus = ChapterStatus.Done;
        Assert.Equal(ChapterStatus.Done, row.EditableStatus);
    }

    [Fact]
    public async Task Story_sidebar_status_repick_of_displayed_bucket_keeps_stored_status()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var phaseService = new PhaseDataService(store);
            var stored = new PhaseData { Status = ChapterStatus.Polishing, PhaseStartDate = "2026-01-01" };
            var vm = CreateStoryChapter(root, store, phaseService, stored);

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await vm.ChangeStatusCommand.Execute(ChapterStatus.Editing);
                return 0;
            });

            Assert.Equal(ChapterStatus.Polishing, vm.Status);
            Assert.Same(stored, vm.PhaseData);
            Assert.Empty(await phaseService.LoadAsync(root));

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await vm.ChangeStatusCommand.Execute(ChapterStatus.Done);
                return 0;
            });

            Assert.Equal(ChapterStatus.Done, vm.Status);
            Assert.Equal(ChapterStatus.Done, (await phaseService.LoadAsync(root))["uuid-1"].Status);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_chapter_info_advance_from_polishing_closes_editing_segment()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var node = await SeedFirstNodeAsync(context, new PhaseData
            {
                Status = ChapterStatus.Polishing,
                Schedule = new() { ["Editing"] = new PhaseScheduleEntry { StartDate = "2026-01-01", Progress = 40 } }
            });
            var info = await context.CreateChapterInfoForAsync(node);

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await info.SetStatusCommand.Execute(ChapterStatus.Done);
                return 0;
            });

            var saved = (await context.PhaseService.LoadAsync(root))[node.Uuid];
            Assert.Equal(ChapterStatus.Done, saved.Status);
            var editing = saved.Schedule!["Editing"];
            Assert.Equal("2026-01-01", editing.StartDate);
            Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), editing.EndDate);
            Assert.Equal(100.0, editing.Progress);
            Assert.False(saved.Schedule.ContainsKey("Polishing"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_chapter_detail_advance_from_polishing_closes_editing_segment()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var phaseService = new PhaseDataService(store);
            var stored = new PhaseData
            {
                Status = ChapterStatus.Polishing,
                Schedule = new() { ["Editing"] = new PhaseScheduleEntry { StartDate = "2026-01-01" } }
            };
            await phaseService.UpsertAsync(root, "uuid-1", _ => stored);
            var detail = new ChapterDetailViewModel(
                CreateStoryChapter(root, store, phaseService, stored), TestMetadataStores.Sidecar(phaseService), root, new RecordingNotificationService());

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await detail.SetStatusCommand.Execute(ChapterStatus.Done);
                return 0;
            });

            var saved = (await phaseService.LoadAsync(root))["uuid-1"];
            Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), saved.Schedule!["Editing"].EndDate);
            Assert.Equal(100.0, saved.Schedule["Editing"].Progress);
            Assert.False(saved.Schedule.ContainsKey("Polishing"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_gantt_inline_date_edit_on_polishing_row_lands_on_editing_segment()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var node = await SeedFirstNodeAsync(context, new PhaseData { Status = ChapterStatus.Polishing });
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                var gantt = new GanttViewModel(context.Workspace, context.Notifications);
                var row = gantt.Rows.First(r => r.IsChapter && r.RelativePath == node.Node.RelativePath);
                row.EditableStartDate = new DateTime(2026, 3, 1);
                await gantt.SaveInlineCellAsync(row, GanttEditableColumn.StartDate);
                return 0;
            });

            var saved = (await context.PhaseService.LoadAsync(root))[node.Uuid];
            Assert.Equal(ChapterStatus.Polishing, saved.Status);
            Assert.Equal("2026-03-01", saved.Schedule!["Editing"].StartDate);
            Assert.False(saved.Schedule.ContainsKey("Polishing"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_gantt_inline_date_edit_on_polishing_row_lands_on_polishing_segment()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 1);
            var node = await SeedFirstNodeAsync(context, new PhaseData { Status = ChapterStatus.Polishing });
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                var gantt = new GanttViewModel(context.Workspace, context.Notifications);
                var row = gantt.Rows.First(r => r.IsChapter);
                row.EditableStartDate = new DateTime(2026, 3, 1);
                await gantt.SaveInlineCellAsync(row, GanttEditableColumn.StartDate);
                return 0;
            });

            var saved = (await context.PhaseService.LoadAsync(root))[node.Uuid];
            Assert.Equal("2026-03-01", saved.Schedule!["Polishing"].StartDate);
            Assert.False(saved.Schedule.ContainsKey("Editing"));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Gantt_legend_follows_profile_phases()
    {
        var storyRoot = CreateStoryFolder();
        var novelRoot = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            using var story = await OpenAsync(storyRoot, expectedNodes: 2);
            var storyGantt = new GanttViewModel(story.Workspace, story.Notifications);
            Assert.Equal(["Drafting", "Revising"], storyGantt.LegendItems.Select(i => i.Label));
            Assert.Equal(["#38BDF8", "#9D4EDD"], storyGantt.LegendItems.Select(i => i.ColorHex));

            using var novel = await OpenAsync(novelRoot, expectedNodes: 1);
            var novelGantt = new GanttViewModel(novel.Workspace, novel.Notifications);
            Assert.Equal(GanttProjection.PhaseNames, novelGantt.LegendItems.Select(i => i.Label));
            Assert.Equal(
                ["#9589B0", "#38BDF8", "#9D4EDD", "#F5C842", "#E91E8C"],
                novelGantt.LegendItems.Select(i => i.ColorHex));
        }
        finally
        {
            DeleteFolder(storyRoot);
            DeleteFolder(novelRoot);
        }
    }

    [Fact]
    public async Task Story_chapter_info_schedule_save_keeps_hidden_phase_entries()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var node = await SeedFirstNodeAsync(context, new PhaseData
            {
                Status = ChapterStatus.Drafting,
                Schedule = new() { ["Polishing"] = new PhaseScheduleEntry { StartDate = "2025-05-01", EndDate = "2025-05-09" } }
            });
            var info = await context.CreateChapterInfoForAsync(node);

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                info.PhaseScheduleRows.Single(r => r.PhaseName == "Drafting").StartDate = "2026-02-01";
                await info.SaveScheduleCommand.Execute();
                return 0;
            });

            var saved = (await context.PhaseService.LoadAsync(root))[node.Uuid];
            Assert.Equal("2026-02-01", saved.Schedule!["Drafting"].StartDate);
            Assert.Equal("2025-05-01", saved.Schedule["Polishing"].StartDate);
            Assert.Equal("2025-05-09", saved.Schedule["Polishing"].EndDate);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_chapter_detail_schedule_save_keeps_hidden_phase_entries()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var phaseService = new PhaseDataService(store);
            var stored = new PhaseData
            {
                Status = ChapterStatus.Drafting,
                Schedule = new() { ["Polishing"] = new PhaseScheduleEntry { StartDate = "2025-05-01" } }
            };
            await phaseService.UpsertAsync(root, "uuid-1", _ => stored);
            var detail = new ChapterDetailViewModel(
                CreateStoryChapter(root, store, phaseService, stored), TestMetadataStores.Sidecar(phaseService), root, new RecordingNotificationService());

            await RunOnUiThreadWithPumpingAsync(() =>
            {
                detail.PhaseScheduleRows.Single(r => r.PhaseName == "Drafting").StartDate = "2026-02-01";
                detail.RequestImmediateScheduleSave();
                return Task.FromResult(0);
            });

            PhaseData? saved = null;
            await WaitUntilAsync(
                () =>
                {
                    saved = phaseService.LoadAsync(root).GetAwaiter().GetResult()["uuid-1"];
                    return saved.Schedule?.GetValueOrDefault("Drafting")?.StartDate == "2026-02-01";
                },
                () => "Drafting row edit was not saved.");
            Assert.Equal("2025-05-01", saved!.Schedule!["Polishing"].StartDate);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Story_card_status_display_maps_polishing_to_revising()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var chapter = new ChapterViewModel(
                new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0),
                "uuid-1",
                new PhaseData { Status = ChapterStatus.Polishing },
                TestMetadataStores.Sidecar(new PhaseDataService(store), new TargetsService(store)),
                new InMemoryAppSettingsStore(),
                new RecordingNotificationService(),
                root,
                target: null,
                profile: ProjectProfile.For(new ProjectSettings(ProjectKind.Story)));

            var card = new CardViewModel(chapter);

            Assert.Equal(ChapterStatus.Polishing, card.Status);
            Assert.Equal("Revising", card.StatusDisplay);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Story_book_status_summary_buckets_by_display_status()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var nodes = context.Workspace.Nodes.ToList();

            await RunOnUiThreadWithPumpingAsync(() =>
            {
                nodes[0].ApplyPhaseData(new PhaseData { Status = ChapterStatus.Polishing });
                nodes[1].ApplyPhaseData(new PhaseData { Status = ChapterStatus.Editing });
                return Task.FromResult(0);
            });

            await WaitUntilAsync(
                () => context.Workspace.BookStatusSummary.Count == 1,
                () => "Expected a single status bucket; got " +
                      string.Join(", ", context.Workspace.BookStatusSummary.Select(s => $"{s.Status}={s.Count}")));

            var bucket = Assert.Single(context.Workspace.BookStatusSummary);
            Assert.Equal(ChapterStatus.Editing, bucket.Status);
            Assert.Equal(2, bucket.Count);

            // Display mapping never rewrites the stored status.
            Assert.Equal(ChapterStatus.Polishing, nodes[0].Status);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Switching_novel_to_story_reselects_rebuilt_node_and_chapter_info_follows()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 1);
            var oldNode = await SeedFirstNodeAsync(context, new PhaseData { Status = ChapterStatus.Outlining });
            Assert.Equal("Outlining", oldNode.StatusLabel);

            var info = context.CreateChapterInfo();
            // Select and wait in one Dispatch so the chapter switch's UI continuations aren't lost.
            var loaded = await RunOnUiThreadWithPumpingAsync(async () =>
            {
                context.Workspace.SelectedNode = oldNode;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTime.UtcNow < deadline && info.ChapterTitle != oldNode.Node.Title)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    await Task.Delay(25);
                }
                return info.ChapterTitle == oldNode.Node.Title;
            });
            Assert.True(loaded, "Chapter info did not load the chapter.");
            Assert.Equal("Chapter Info", info.PanelTitle);

            var saved = await new ProjectSettingsService(new MetadataStore())
                .SaveAsync(root, new ProjectSettings(ProjectKind.Story));
            Assert.True(saved.IsSuccess, saved.Error);

            var reload = await RunOnUiThreadWithPumpingAsync(() => context.Workspace.ReloadProjectAsync());
            Assert.True(reload.IsSuccess, reload.Error);

            var selected = context.Workspace.SelectedNode;
            Assert.NotNull(selected);
            Assert.NotSame(oldNode, selected);
            Assert.Contains(selected, context.Workspace.Nodes);
            Assert.Equal(ProjectKind.Story, context.Workspace.Profile.Kind);
            Assert.Equal(ProjectKind.Story, selected!.Profile.Kind);
            Assert.Equal(ChapterStatus.Outlining, selected.Status);
            Assert.Equal("Idea", selected.StatusLabel);

            Assert.Equal(ProjectKind.Story, info.Profile.Kind);
            Assert.Equal("Story Info", info.PanelTitle);
            Assert.Equal(ChapterStatus.Planned, info.DisplayStatus);
            Assert.Equal("Idea", info.StatusOptions.Single(o => o.Value == info.DisplayStatus).Label);

            // The panel is attached to the rebuilt view-model, not the disposed one.
            await RunOnUiThreadWithPumpingAsync(() =>
            {
                selected.UpdateWordCount(321);
                return Task.FromResult(0);
            });
            Assert.Equal(321, info.WordCount);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Status_summary_buckets_carry_profile_labels()
    {
        var root = CreateStoryFolder();
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 2);
            var nodes = context.Workspace.Nodes.ToList();

            await RunOnUiThreadWithPumpingAsync(() =>
            {
                nodes[0].ApplyPhaseData(new PhaseData { Status = ChapterStatus.Outlining });
                nodes[1].ApplyPhaseData(new PhaseData { Status = ChapterStatus.Reviewing });
                return Task.FromResult(0);
            });

            await WaitUntilAsync(
                () => context.Workspace.BookStatusSummary.Count == 2,
                () => "Expected two status buckets.");

            Assert.Equal(
                ["Idea", "Revising"],
                context.Workspace.BookStatusSummary.OrderBy(s => s.Status).Select(s => s.DisplayLabel));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Novel_status_summary_labels_are_enum_names()
    {
        var root = CreateFolder(("Book.txt", "chapter-one.md\n"), ("chapter-one.md", "# One\n"));
        try
        {
            using var context = await OpenAsync(root, expectedNodes: 1);
            var node = context.Workspace.Nodes.Single();
            await RunOnUiThreadWithPumpingAsync(() =>
            {
                node.ApplyPhaseData(new PhaseData { Status = ChapterStatus.Polishing });
                return Task.FromResult(0);
            });

            await WaitUntilAsync(
                () => context.Workspace.BookStatusSummary.Any(s => s.Status == ChapterStatus.Polishing),
                () => "Expected a Polishing bucket.");
            Assert.Equal("Polishing", Assert.Single(context.Workspace.BookStatusSummary).DisplayLabel);
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Story_gantt_row_label_and_active_phase_follow_display_status()
    {
        var profile = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));
        var node = new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0);
        var row = new GanttRowViewModel(
            GanttProjection.Project(node, new PhaseData
            {
                Status = ChapterStatus.Polishing,
                Schedule = new() { ["Editing"] = new PhaseScheduleEntry { StartDate = "2026-02-01" } }
            }, profile), profile);

        Assert.Equal(ChapterStatus.Editing, row.DisplayStatus);
        Assert.Equal("Revising", row.StatusLabel);
        Assert.True(row.IsActivePhase("Editing"));
        Assert.False(row.IsActivePhase("Polishing"));
        Assert.Equal("Editing", row.CurrentSegment?.PhaseName);
    }

    [Fact]
    public void Novel_gantt_row_label_and_active_phase_use_stored_status()
    {
        var node = new ChapterNode("ch.md", "ch.md", "Chapter", NodeKind.Chapter, IsMissing: false, Index: 0);
        var row = new GanttRowViewModel(
            GanttProjection.Project(node, new PhaseData { Status = ChapterStatus.Polishing }, ProjectProfile.Novel),
            ProjectProfile.Novel);

        Assert.Equal(ChapterStatus.Polishing, row.DisplayStatus);
        Assert.Equal("Polishing", row.StatusLabel);
        Assert.True(row.IsActivePhase("Polishing"));
        Assert.False(row.IsActivePhase("Editing"));
    }

    [Fact]
    public async Task Sidebar_status_change_keeps_phase_schedules()
    {
        var root = CreateFolder(("Book.txt", "scene.md\n"), ("scene.md", "# Scene\n"));
        try
        {
            var store = new MetadataStore();
            var phaseService = new PhaseDataService(store);
            var stored = new PhaseData
            {
                Status = ChapterStatus.Drafting,
                Schedule = new()
                {
                    ["Drafting"] = new PhaseScheduleEntry { StartDate = "2026-01-01", EndDate = "2026-01-20" },
                    ["Editing"] = new PhaseScheduleEntry { StartDate = "2026-02-01" }
                }
            };
            await phaseService.UpsertAsync(root, "uuid-1", _ => stored);
            var vm = CreateStoryChapter(root, store, phaseService, stored);

            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await vm.ChangeStatusCommand.Execute(ChapterStatus.Editing);
                return 0;
            });

            var saved = (await phaseService.LoadAsync(root))["uuid-1"];
            Assert.Equal(ChapterStatus.Editing, saved.Status);
            Assert.NotNull(saved.Schedule);
            Assert.Equal("2026-01-01", saved.Schedule!["Drafting"].StartDate);
            Assert.Equal("2026-01-20", saved.Schedule["Drafting"].EndDate);
            Assert.Equal("2026-02-01", saved.Schedule["Editing"].StartDate);
            Assert.NotNull(vm.PhaseData?.Schedule);
        }
        finally { DeleteFolder(root); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChapterViewModel CreateStoryChapter(
        string root, MetadataStore store, PhaseDataService phaseService, PhaseData phaseData) =>
        new(
            new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0),
            "uuid-1",
            phaseData,
            TestMetadataStores.Sidecar(phaseService, new TargetsService(store)),
            new InMemoryAppSettingsStore(),
            new RecordingNotificationService(),
            root,
            target: null,
            profile: ProjectProfile.For(new ProjectSettings(ProjectKind.Story)));

    /// <summary>Persists <paramref name="phaseData"/> for the first chapter node and applies it to the VM.</summary>
    private static async Task<ChapterViewModel> SeedFirstNodeAsync(TestContext context, PhaseData phaseData)
    {
        var node = context.Workspace.Nodes.First(n => n.Node.Kind == NodeKind.Chapter);
        await WaitUntilAsync(() => !string.IsNullOrEmpty(node.Uuid), () => "Chapter UUID was not assigned.");
        await context.PhaseService.UpsertAsync(context.Workspace.WorkspaceRoot, node.Uuid, _ => phaseData);
        await RunOnUiThreadWithPumpingAsync(() =>
        {
            node.ApplyPhaseData(phaseData);
            return Task.FromResult(0);
        });
        return node;
    }

    private static string CreateStoryFolder() => CreateFolder(
        ("Book.txt", "scene-one.md\nscene-two.md\n"),
        ("scene-one.md", "# Scene One\n"),
        ("scene-two.md", "# Scene Two\n"),
        (".hymnal-data/project.json", StoryProjectJson));

    private static string CreateFolder(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-profile-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (relativePath, content) in files)
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static void DeleteFolder(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task<TestContext> OpenAsync(string root, int expectedNodes)
    {
        var context = new TestContext(root);
        var method = typeof(WorkspaceViewModel).GetMethod("OpenWorkspaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate OpenWorkspaceAsync.");
        await RunOnUiThreadWithPumpingAsync(async () =>
        {
            await (Task)method.Invoke(context.Workspace, Array.Empty<object>())!;
            // Wait for hydration inside this Dispatch: each Dispatch gets a fresh dispatcher, so
            // the node rebuild it posts would be lost once this job returns.
            if (HydrationTask(context.Workspace) is { } hydration)
                await hydration;
            return 0;
        });
        await WaitUntilAsync(
            () => context.Workspace.Nodes.Count == expectedNodes,
            () => $"Workspace nodes did not reach {expectedNodes}; actual {context.Workspace.Nodes.Count}. " +
                  $"Errors: {string.Join(" | ", context.Notifications.Errors)}");
        return context;
    }

    private static Task? HydrationTask(WorkspaceViewModel workspace) =>
        typeof(WorkspaceViewModel)
            .GetField("_hydrationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(workspace) as Task;

    /// <summary>Runs <paramref name="action"/> on the headless UI thread, keeping its dispatcher loop alive until done.</summary>
    private static Task<T> RunOnUiThreadWithPumpingAsync<T>(Func<Task<T>> action) =>
        ReactiveUiTestBootstrap.RunOnUiThreadAsync(action, TimeSpan.FromSeconds(40));

    /// <summary>Polls on the UI thread so background <c>Dispatcher.UIThread.InvokeAsync</c> work can run.</summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, Func<string> failureMessage)
    {
        var satisfied = await RunOnUiThreadWithPumpingAsync(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && !predicate())
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            return predicate();
        });

        if (!satisfied)
            Assert.Fail(failureMessage());
    }

    private sealed class TestContext : IDisposable
    {
        private readonly ManuscriptService _manuscript;

        private readonly OwnedDisposables _owned = new();

        public void Dispose() => _owned.Dispose();

        private readonly MetadataStore _store = new();
        private readonly InMemoryAppSettingsStore _settings = new();
        private readonly EditorViewModel _editor;

        public RecordingNotificationService Notifications { get; } = new();
        public PhaseDataService PhaseService { get; }
        public WorkspaceViewModel Workspace { get; }

        public TestContext(string folder)
        {
            var exclusions = new ExclusionManifestService(_store);
            var registry = new ChapterRegistryService(_store);
            var wordCount = new WordCountService();
            var projectSettings = new ProjectSettingsService(_store);
            PhaseService = new PhaseDataService(_store);
            _editor = _owned.Track(new EditorViewModel(_store, Notifications, wordCount, new StoryFileWriter(_store)));
            _manuscript = _owned.Track(new ManuscriptService(Notifications, exclusions));
            LiveContexts.Add(this);

            Workspace = new WorkspaceViewModel(
                _manuscript,
                new BookTxtStructureService(_store, exclusions, registry),
                new StubFilePickerService(),
                _settings,
                new FixedFolderPickerService(folder),
                Notifications,
                _editor,
                registry,
                PhaseService,
                new TargetsService(_store),
                wordCount,
                new WordCountHistoryService(_store),
                exclusions,
                new OrphanFileDiscoveryService(),
                projectSettings,
                new ProjectInitializer(_store, projectSettings),
                new DecliningProjectInitPrompt());
        }

        /// <summary>Creates a ChapterInfoViewModel with <paramref name="node"/> open in the editor.</summary>
        public async Task<ChapterInfoViewModel> CreateChapterInfoForAsync(ChapterViewModel node)
        {
            var info = CreateChapterInfo();
            var path = Path.Combine(Workspace.ManuscriptRoot, node.Node.RelativePath);
            await RunOnUiThreadWithPumpingAsync(async () =>
            {
                await _editor.OpenChapterAsync(node.Node, path);
                return 0;
            });
            await WaitUntilAsync(() => info.ChapterTitle == node.Node.Title, () => "Chapter info did not load the chapter.");
            return info;
        }

        public ChapterInfoViewModel CreateChapterInfo() => new(
            _editor,
            Workspace,
            _settings,
            Notifications,
            new WordCountHistoryService(_store));
    }

    private sealed class DecliningProjectInitPrompt : IProjectInitPrompt
    {
        public Task<bool> ConfirmInitializeAsync(string folderPath) => Task.FromResult(false);
        public Task<ProjectKind?> ChooseProjectKindAsync() => Task.FromResult<ProjectKind?>(null);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message) { }
    }

    private sealed class InMemoryAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedFolderPickerService(string folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }
}

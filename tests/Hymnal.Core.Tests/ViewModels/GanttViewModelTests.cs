using System.Collections.ObjectModel;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using NSubstitute;
using ReactiveUI.Builder;

namespace Hymnal.Core.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="GanttProjection"/> — the pure projection/date-parsing logic
/// that backs <c>GanttViewModel</c>. Kept in Hymnal.Core.Tests because the projection
/// is a Core-layer concern; the ViewModel is a thin reactive wrapper around it.
/// </summary>
[Collection("AvaloniaUi")]
public class GanttViewModelTests
{
    static GanttViewModelTests()
    {
        ReactiveUiTestBootstrap.EnsureInitialized();
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChapterNode MakeNode(
        string title      = "Chapter One",
        NodeKind kind     = NodeKind.Chapter,
        string path       = "ch01.md",
        int index         = 0) =>
        new ChapterNode(path, path, title, kind, IsMissing: false, Index: index);

    private static PhaseData MakePhase(
        ChapterStatus status = ChapterStatus.Drafting,
        string? start        = null,
        string? end          = null) =>
        new PhaseData { Status = status, PhaseStartDate = start, PhaseEndDate = end };

    private static WorkspaceViewModel CreateWorkspace(params ChapterViewModel[] nodes)
    {
        var workspace = (WorkspaceViewModel)RuntimeHelpers.GetUninitializedObject(typeof(WorkspaceViewModel));
        var backing = new ObservableCollection<ChapterViewModel>(nodes);
        var readOnly = new ReadOnlyObservableCollection<ChapterViewModel>(backing);

        var nodesField = typeof(WorkspaceViewModel).GetField(
            "<Nodes>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate WorkspaceViewModel.Nodes backing field.");

        nodesField.SetValue(workspace, readOnly);

        // Uninitialized objects skip field initializers; give the workspace its default profile.
        typeof(WorkspaceViewModel)
            .GetField("_profile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(workspace, ProjectProfile.Novel);
        return workspace;
    }

    private static GanttRowViewModel ChapterRow(GanttViewModel gantt, int index = 0) =>
        gantt.Rows.Where(r => r.IsChapter).ElementAt(index);

    private static GanttRowViewModel PartRow(GanttViewModel gantt, int index = 0) =>
        gantt.Rows.Where(r => r.IsPart).ElementAt(index);

    private static ChapterViewModel CreateChapterViewModel(
        PhaseData? phaseData,
        string title = "Chapter One",
        string path = "ch01.md",
        string uuid = "uuid-1")
    {
        var metadataStore = Substitute.For<IMetadataStore>();
        var phaseDataService = new PhaseDataService(metadataStore);
        var targetsService = new TargetsService(metadataStore);
        var settingsStore = Substitute.For<IAppSettingsStore>();
        var notificationService = Substitute.For<INotificationService>();

        return new ChapterViewModel(
            MakeNode(title: title, path: path),
            uuid,
            phaseData,
            TestMetadataStores.Sidecar(phaseDataService, targetsService),
            settingsStore,
            notificationService,
            workspaceRoot: Path.Combine(Path.GetTempPath(), "hymnal-gantt-tests"));
    }

    // ── ParseDate ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDate_ValidIso_ReturnsParsedDate()
    {
        var result = GanttProjection.ParseDate("2024-03-15");
        Assert.Equal(new DateOnly(2024, 3, 15), result);
    }

    [Fact]
    public void ParseDate_Null_ReturnsNull()
    {
        Assert.Null(GanttProjection.ParseDate(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDate_EmptyOrWhitespace_ReturnsNull(string input)
    {
        Assert.Null(GanttProjection.ParseDate(input));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2024/03/15")]
    [InlineData("15-03-2024")]
    [InlineData("2024-13-01")] // month 13 is invalid
    public void ParseDate_InvalidFormat_ReturnsNull(string input)
    {
        Assert.Null(GanttProjection.ParseDate(input));
    }

    // ── Project — identity ────────────────────────────────────────────────────

    [Fact]
    public void Project_TitleAndPath_MappedFromNode()
    {
        var node = MakeNode(title: "The Title", path: "chapter-02.md");
        var row  = GanttProjection.Project(node, null);

        Assert.Equal("The Title",      row.Title);
        Assert.Equal("chapter-02.md",  row.RelativePath);
    }

    [Fact]
    public void Project_ChapterKind_PreservedInRow()
    {
        var row = GanttProjection.Project(MakeNode(kind: NodeKind.Chapter), null);
        Assert.Equal(NodeKind.Chapter, row.Kind);
    }

    [Fact]
    public void Project_PartKind_PreservedInRow()
    {
        var row = GanttProjection.Project(MakeNode(kind: NodeKind.Part), null);
        Assert.Equal(NodeKind.Part, row.Kind);
    }

    // ── Project — date parsing ────────────────────────────────────────────────

    [Fact]
    public void Project_ValidDates_ParsedCorrectly()
    {
        var phase = MakePhase(start: "2024-01-10", end: "2024-03-20");
        var row   = GanttProjection.Project(MakeNode(), phase);

        Assert.Equal(new DateOnly(2024, 1, 10),  row.StartDate);
        Assert.Equal(new DateOnly(2024, 3, 20),  row.EndDate);
        Assert.False(row.IsMissingDates);
    }

    [Fact]
    public void Project_NullPhaseData_YieldsMissingDates()
    {
        var row = GanttProjection.Project(MakeNode(), phase: null);

        Assert.Null(row.StartDate);
        Assert.Null(row.EndDate);
        Assert.True(row.IsMissingDates);
    }

    [Fact]
    public void Project_EmptyDateStrings_YieldsMissingDates()
    {
        var phase = MakePhase(start: "", end: "");
        var row   = GanttProjection.Project(MakeNode(), phase);

        Assert.True(row.IsMissingDates);
    }

    [Fact]
    public void Project_OnlyStartDatePresent_YieldsMissingDates()
    {
        var phase = MakePhase(start: "2024-01-10", end: null);
        var row   = GanttProjection.Project(MakeNode(), phase);

        Assert.NotNull(row.StartDate);
        Assert.Null(row.EndDate);
        Assert.False(row.IsMissingDates);
    }

    [Fact]
    public void Project_OnlyEndDatePresent_YieldsMissingDates()
    {
        var phase = MakePhase(start: null, end: "2024-06-01");
        var row   = GanttProjection.Project(MakeNode(), phase);

        Assert.Null(row.StartDate);
        Assert.NotNull(row.EndDate);
        Assert.False(row.IsMissingDates);
    }

    [Fact]
    public void Project_InvalidDateStrings_YieldsMissingDates()
    {
        var phase = MakePhase(start: "not-a-date", end: "also-bad");
        var row   = GanttProjection.Project(MakeNode(), phase);

        Assert.Null(row.StartDate);
        Assert.Null(row.EndDate);
        Assert.True(row.IsMissingDates);
    }

    // ── Project — status ──────────────────────────────────────────────────────

    [Fact]
    public void Project_NullPhaseData_DefaultsToOutliningStatus()
    {
        var row = GanttProjection.Project(MakeNode(), phase: null);
        Assert.Equal(ChapterStatus.Outlining, row.Status);
    }

    [Theory]
    [InlineData(ChapterStatus.Drafting)]
    [InlineData(ChapterStatus.Editing)]
    [InlineData(ChapterStatus.Done)]
    public void Project_ExplicitStatus_PreservedInRow(ChapterStatus status)
    {
        var phase = MakePhase(status: status, start: "2024-01-01", end: "2024-12-31");
        var row   = GanttProjection.Project(MakeNode(), phase);

        Assert.Equal(status, row.Status);
    }

    // ── GanttViewModel projection refresh ─────────────────────────────────────

    [Fact]
    public void GanttViewModel_MissingDatesStillProduceRows()
    {
        var chapter = CreateChapterViewModel(phaseData: MakePhase(start: null, end: null));
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        Assert.Equal(2, gantt.Rows.Count);
        var row = ChapterRow(gantt);
        Assert.True(row.IsMissingDates);
        Assert.Equal(chapter.Node.Title, row.Title);
    }

    [Fact]
    public void GanttViewModel_RefreshesWhenChapterPhaseDataChanges()
    {
        var initialPhase = MakePhase(status: ChapterStatus.Drafting, start: null, end: null);
        var chapter = CreateChapterViewModel(phaseData: initialPhase);
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        var row = ChapterRow(gantt);
        Assert.True(row.IsMissingDates);
        Assert.Equal(ChapterStatus.Drafting, row.Status);

        chapter.ApplyPhaseData(MakePhase(
            status: ChapterStatus.Reviewing,
            start: "2024-02-10",
            end: "2024-03-15"));

        var updated = SpinWait.SpinUntil(
            () =>
            {
                row = ChapterRow(gantt);
                return row.Status == ChapterStatus.Reviewing
                    && row.StartDate == new DateOnly(2024, 2, 10)
                    && row.EndDate == new DateOnly(2024, 3, 15)
                    && !row.IsMissingDates;
            },
            TimeSpan.FromSeconds(2));

        Assert.True(updated, "Gantt rows did not refresh after ChapterViewModel.ApplyPhaseData().");
    }

    [Fact]
    public async Task GanttViewModel_SaveInlineCellAsync_NoOpAgainstPersistedDates_DoesNotRaiseValidationError()
    {
        var metadataStore = Substitute.For<IMetadataStore>();
        var phaseDataService = new PhaseDataService(metadataStore);
        var targetsService = new TargetsService(metadataStore);
        var settingsStore = Substitute.For<IAppSettingsStore>();
        var notificationService = Substitute.For<INotificationService>();

        var chapter = new ChapterViewModel(
            MakeNode(title: "Chapter One", path: "ch01.md"),
            "uuid-1",
            MakePhase(status: ChapterStatus.Drafting, start: "2024-03-10", end: "2024-03-01"),
            TestMetadataStores.Sidecar(phaseDataService, targetsService),
            settingsStore,
            notificationService,
            workspaceRoot: Path.Combine(Path.GetTempPath(), "hymnal-gantt-tests"));

        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, notificationService);
        var row = ChapterRow(gantt);

        await gantt.SaveInlineCellAsync(row, GanttEditableColumn.StartDate);

        notificationService.DidNotReceive().ShowError("End date cannot be before start date.");
        Assert.DoesNotContain(metadataStore.ReceivedCalls(), call =>
            string.Equals(call.GetMethodInfo().Name, nameof(IMetadataStore.WriteTextAtomicAsync), StringComparison.Ordinal));
    }

    // ── Part rollup rows ──────────────────────────────────────────────────────

    [Fact]
    public void GanttViewModel_PartRow_SpansMinStartToMaxEndOfChildChapters()
    {
        var part = CreateChapterViewModel(
            phaseData: null,
            title: "Part One",
            path: "part-01.md",
            uuid: "uuid-part");
        // Swap the Part node in — helper creates Chapter by default.
        var partNode = new ChapterNode("part-01.md", "part-01.md", "Part One", NodeKind.Part, IsMissing: false, Index: 0);

        var ch1 = CreateChapterViewModel(
            phaseData: MakePhase(start: "2024-02-01", end: "2024-04-30"),
            title: "Chapter One", path: "ch01.md", uuid: "uuid-ch1");
        var ch2 = CreateChapterViewModel(
            phaseData: MakePhase(start: "2024-01-15", end: "2024-05-31"),
            title: "Chapter Two", path: "ch02.md", uuid: "uuid-ch2");

        // Inject the Part node into the ChapterViewModel via field reflection.
        InjectNode(part, partNode);

        var workspace = CreateWorkspace(part, ch1, ch2);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        Assert.Equal(4, gantt.Rows.Count);
        var partRow = PartRow(gantt);

        Assert.True(partRow.IsPart);
        // Start = min(2024-02-01, 2024-01-15) = 2024-01-15
        Assert.Equal(new DateOnly(2024, 1, 15), partRow.StartDate);
        // End = max(2024-04-30, 2024-05-31) = 2024-05-31
        Assert.Equal(new DateOnly(2024, 5, 31), partRow.EndDate);
        Assert.False(partRow.IsMissingDates);
    }

    [Fact]
    public void GanttViewModel_PartRow_CompletionPercentage_ReflectsDoneChapters()
    {
        var part = CreateChapterViewModel(
            phaseData: null,
            title: "Part One", path: "part-01.md", uuid: "uuid-part");
        InjectNode(part, new ChapterNode("part-01.md", "part-01.md", "Part One", NodeKind.Part, IsMissing: false, Index: 0));

        var ch1 = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Done, start: "2024-01-01", end: "2024-03-01"),
            title: "Ch1", path: "ch01.md", uuid: "uuid-ch1");
        var ch2 = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Drafting, start: "2024-01-01", end: "2024-04-01"),
            title: "Ch2", path: "ch02.md", uuid: "uuid-ch2");
        var ch3 = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Done, start: "2024-01-01", end: "2024-05-01"),
            title: "Ch3", path: "ch03.md", uuid: "uuid-ch3");

        var workspace = CreateWorkspace(part, ch1, ch2, ch3);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        var partRow = PartRow(gantt);
        Assert.True(partRow.IsPart);
        // 2 out of 3 chapters are Done → ~0.667
        Assert.Equal(2.0 / 3.0, partRow.CompletionPercentage, precision: 10);
    }

    [Fact]
    public void GanttViewModel_PartRow_NoChildren_ZeroCompletionAndMissingDates()
    {
        var part = CreateChapterViewModel(
            phaseData: null,
            title: "Part One", path: "part-01.md", uuid: "uuid-part");
        InjectNode(part, new ChapterNode("part-01.md", "part-01.md", "Part One", NodeKind.Part, IsMissing: false, Index: 0));

        var workspace = CreateWorkspace(part);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        var partRow = PartRow(gantt);
        Assert.True(partRow.IsPart);
        Assert.True(partRow.IsMissingDates);
        Assert.Equal(0.0, partRow.CompletionPercentage);
    }

    [Fact]
    public void GanttViewModel_PartRow_StopsAtNextPart()
    {
        // Part1 → ch1, ch2; Part2 → ch3
        var part1 = CreateChapterViewModel(phaseData: null, title: "Part 1", path: "part-01.md", uuid: "p1");
        InjectNode(part1, new ChapterNode("part-01.md", "part-01.md", "Part 1", NodeKind.Part, IsMissing: false, Index: 0));

        var ch1 = CreateChapterViewModel(phaseData: MakePhase(start: "2024-01-01", end: "2024-02-28"), title: "Ch1", path: "ch01.md", uuid: "ch1");
        var ch2 = CreateChapterViewModel(phaseData: MakePhase(start: "2024-03-01", end: "2024-04-30"), title: "Ch2", path: "ch02.md", uuid: "ch2");

        var part2 = CreateChapterViewModel(phaseData: null, title: "Part 2", path: "part-02.md", uuid: "p2");
        InjectNode(part2, new ChapterNode("part-02.md", "part-02.md", "Part 2", NodeKind.Part, IsMissing: false, Index: 3));

        var ch3 = CreateChapterViewModel(phaseData: MakePhase(start: "2024-06-01", end: "2024-12-31"), title: "Ch3", path: "ch03.md", uuid: "ch3");

        var workspace = CreateWorkspace(part1, ch1, ch2, part2, ch3);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        Assert.Equal(6, gantt.Rows.Count);
        var row1 = PartRow(gantt, 0);
        var row2 = PartRow(gantt, 1);

        Assert.True(row1.IsPart);
        // Part 1 spans ch1+ch2 only: 2024-01-01 to 2024-04-30
        Assert.Equal(new DateOnly(2024, 1, 1),  row1.StartDate);
        Assert.Equal(new DateOnly(2024, 4, 30), row1.EndDate);

        Assert.True(row2.IsPart);
        // Part 2 spans ch3 only: 2024-06-01 to 2024-12-31
        Assert.Equal(new DateOnly(2024, 6, 1),   row2.StartDate);
        Assert.Equal(new DateOnly(2024, 12, 31), row2.EndDate);
    }

    // ── EditDatesCommand / RowEditRequested ───────────────────────────────────

    [Fact]
    public void GanttViewModel_RowEditRequested_FiresWhenEditDatesCommandExecutedOnChapterRow()
    {
        var chapter = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Drafting, start: "2024-01-01", end: "2024-06-30"),
            title: "Chapter One", path: "ch01.md");
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        GanttRowViewModel? received = null;
        gantt.RowEditRequested.Subscribe(vm => received = vm);

        ChapterRow(gantt).EditDatesCommand.Execute().Subscribe();

        var fired = SpinWait.SpinUntil(() => received != null, TimeSpan.FromSeconds(2));
        Assert.True(fired, "RowEditRequested did not emit after EditDatesCommand.Execute().");
        Assert.Equal("Chapter One", received!.Title);
        Assert.True(received.IsChapter);
    }

    [Fact]
    public void GanttViewModel_RowEditRequested_DoesNotFireForPartRows()
    {
        var part = CreateChapterViewModel(phaseData: null, title: "Part One", path: "part-01.md", uuid: "p1");
        InjectNode(part, new ChapterNode("part-01.md", "part-01.md", "Part One", NodeKind.Part, IsMissing: false, Index: 0));
        var ch1 = CreateChapterViewModel(
            phaseData: MakePhase(start: "2024-01-01", end: "2024-03-01"),
            title: "Ch1", path: "ch01.md", uuid: "ch1");

        var workspace = CreateWorkspace(part, ch1);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        var editFired = false;
        gantt.RowEditRequested.Subscribe(_ => editFired = true);

        // Execute the Part row's command directly — should NOT propagate to RowEditRequested.
        PartRow(gantt).EditDatesCommand.Execute().Subscribe();

        // Allow 500 ms for any async dispatch; it should still be false.
        Thread.Sleep(500);
        Assert.False(editFired, "RowEditRequested should not fire when a Part row's EditDatesCommand is executed.");
    }

    [Fact]
    public void GanttRowViewModel_EditDatesCommand_ExposedAndExecutable()
    {
        var data = new GanttRowData(
            RelativePath: "ch01.md", Title: "Ch1", Kind: NodeKind.Chapter,
            Status: ChapterStatus.Drafting,
            StartDate: new DateOnly(2024, 1, 1), EndDate: new DateOnly(2024, 3, 1),
            IsMissingDates: false);

        var vm = new GanttRowViewModel(data);

        Assert.NotNull(vm.EditDatesCommand);

        // Subscribe to the command output, execute it, then wait for the result.
        var executed = false;
        vm.EditDatesCommand.Subscribe(_ => executed = true);
        vm.EditDatesCommand.Execute().Subscribe();

        var completed = SpinWait.SpinUntil(() => executed, TimeSpan.FromSeconds(2));
        Assert.True(completed, "EditDatesCommand did not emit a result after Execute().");
    }

    // ── IsEditingDates / editing state ────────────────────────────────────────

    [Fact]
    public void GanttViewModel_IsEditingDates_SetTrueWhenRowEditRequested()
    {
        var chapter = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Drafting, start: "2024-01-01", end: "2024-06-30"),
            title: "Chapter One", path: "ch01.md");
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        Assert.False(gantt.IsEditingDates);

        ChapterRow(gantt).EditDatesCommand.Execute().Subscribe();

        var opened = SpinWait.SpinUntil(() => gantt.IsEditingDates, TimeSpan.FromSeconds(2));
        Assert.True(opened, "IsEditingDates did not become true after EditDatesCommand.");
        Assert.NotNull(gantt.EditingRow);
        Assert.Equal("Chapter One", gantt.EditingRow!.Title);
    }

    [Fact]
    public void GanttViewModel_OpenEditForRow_PopulatesEditDates_FromRowStartEnd()
    {
        var chapter = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Drafting, start: "2024-03-10", end: "2024-09-15"),
            title: "Chapter Two", path: "ch02.md");
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        ChapterRow(gantt).EditDatesCommand.Execute().Subscribe();

        SpinWait.SpinUntil(() => gantt.IsEditingDates, TimeSpan.FromSeconds(2));

        Assert.True(gantt.EditStartDate.HasValue);
        Assert.True(gantt.EditEndDate.HasValue);
        Assert.Equal(new DateTime(2024, 3, 10), gantt.EditStartDate!.Value.Date);
        Assert.Equal(new DateTime(2024, 9, 15), gantt.EditEndDate!.Value.Date);
    }

    [Fact]
    public void GanttViewModel_OpenEditForRow_MissingDates_EditDatesAreNull()
    {
        var chapter = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Drafting, start: null, end: null),
            title: "No Dates", path: "ch03.md");
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        ChapterRow(gantt).EditDatesCommand.Execute().Subscribe();

        SpinWait.SpinUntil(() => gantt.IsEditingDates, TimeSpan.FromSeconds(2));

        Assert.Null(gantt.EditStartDate);
        Assert.Null(gantt.EditEndDate);
    }

    [Fact]
    public void GanttViewModel_CancelEditCommand_ClearsState()
    {
        var chapter = CreateChapterViewModel(
            phaseData: MakePhase(status: ChapterStatus.Drafting, start: "2024-01-01", end: "2024-06-30"),
            title: "Chapter One", path: "ch01.md");
        var workspace = CreateWorkspace(chapter);
        var gantt = new GanttViewModel(workspace, Substitute.For<INotificationService>());

        ChapterRow(gantt).EditDatesCommand.Execute().Subscribe();
        SpinWait.SpinUntil(() => gantt.IsEditingDates, TimeSpan.FromSeconds(2));
        Assert.True(gantt.IsEditingDates);

        gantt.CancelEditCommand.Execute().Subscribe();

        var cleared = SpinWait.SpinUntil(() => !gantt.IsEditingDates, TimeSpan.FromSeconds(2));
        Assert.True(cleared, "IsEditingDates was not cleared by CancelEditCommand.");
        Assert.Null(gantt.EditingRow);
        Assert.Null(gantt.EditStartDate);
        Assert.Null(gantt.EditEndDate);
    }

    // ── ChapterViewModel.UpdateDatesAsync ──────────────────────────────────────

    [Fact]
    public async Task ChapterViewModel_UpdateDatesAsync_PersistsAndAppliesPhaseData()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hymnal-updateDates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, ".hymnal-data"));
        try
        {
            var store = new Hymnal.Core.Infrastructure.MetadataStore();
            var phaseDataService = new PhaseDataService(store);
            var targetsService = new TargetsService(store);
            var settingsStore = Substitute.For<IAppSettingsStore>();
            var notificationService = Substitute.For<INotificationService>();

            var node = MakeNode(title: "Chapter A", path: "ch-a.md");
            var initialPhase = MakePhase(status: ChapterStatus.Drafting, start: "2024-01-01", end: "2024-03-31");
            var vm = new ChapterViewModel(
                node, "uuid-a", initialPhase,
                TestMetadataStores.Sidecar(phaseDataService, targetsService), settingsStore,
                notificationService, workspaceRoot: tempRoot);

            await vm.UpdateDatesAsync("2025-06-01", "2025-09-30");

            // Verify persistence by re-loading from the written file (avoids UIThread dispatch).
            var phases = await phaseDataService.LoadAsync(tempRoot);
            Assert.True(phases.TryGetValue("uuid-a", out var saved), "No entry found for uuid-a.");
            Assert.Equal("2025-06-01", saved?.PhaseStartDate);
            Assert.Equal("2025-09-30", saved?.PhaseEndDate);
            // Status should be preserved from the initial phase.
            Assert.Equal(ChapterStatus.Drafting, saved?.Status);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ── Gantt row reorder ────────────────────────────────────────────────────

    [Fact]
    public async Task GanttViewModel_MoveRowAfterAsync_DelegatesIncludedChapterMoveToWorkspaceReorderCommand()
    {
        using var context = ReorderTestContext.Create(
            PartNode("part-one/part.md", "Part One", 0),
            ChapterNodeForReorder("part-one/chapter-one.md", "Chapter One", 1),
            ChapterNodeForReorder("part-one/chapter-two.md", "Chapter Two", 2));
        var gantt = context.CreateGanttViewModel();

        await gantt.MoveRowAfterAsync(ChapterRow(gantt, 0), ChapterRow(gantt, 1));

        var call = Assert.Single(context.StructureService.ReorderCalls);
        Assert.Equal("part-one/chapter-one.md", call.ChapterPath);
        Assert.Equal(2, call.NewIndex);
    }

    [Fact]
    public async Task GanttViewModel_MoveSelectedRowUpCommand_DelegatesUsingPreviousChapterNeighbor()
    {
        using var context = ReorderTestContext.Create(
            PartNode("part-one/part.md", "Part One", 0),
            ChapterNodeForReorder("part-one/chapter-one.md", "Chapter One", 1),
            ChapterNodeForReorder("part-one/chapter-two.md", "Chapter Two", 2));
        var gantt = context.CreateGanttViewModel();
        gantt.SelectedRow = ChapterRow(gantt, 1);

        await ExecuteCommandAsync(gantt.MoveSelectedRowUpCommand.Execute());

        var call = Assert.Single(context.StructureService.ReorderCalls);
        Assert.Equal("part-one/chapter-two.md", call.ChapterPath);
        Assert.Equal(1, call.NewIndex);
    }

    [Fact]
    public async Task GanttViewModel_MoveRowBeforeAsync_RejectsPartRollupTargetBeforeWorkspaceCommand()
    {
        using var context = ReorderTestContext.Create(
            PartNode("part-one/part.md", "Part One", 0),
            ChapterNodeForReorder("part-one/chapter-one.md", "Chapter One", 1));
        var gantt = context.CreateGanttViewModel();

        await gantt.MoveRowBeforeAsync(ChapterRow(gantt), PartRow(gantt));

        Assert.Empty(context.StructureService.ReorderCalls);
    }

    [Fact]
    public async Task GanttViewModel_MoveRowAfterAsync_RejectsExcludedChapterBeforeWorkspaceCommand()
    {
        using var context = ReorderTestContext.Create(
            ChapterNodeForReorder("chapter-one.md", "Chapter One", 0),
            ChapterNodeForReorder("chapter-two.md", "Chapter Two", 1) with { IsExcluded = true });
        var gantt = context.CreateGanttViewModel();

        await gantt.MoveRowAfterAsync(ChapterRow(gantt, 1), ChapterRow(gantt, 0));

        Assert.Empty(context.StructureService.ReorderCalls);
    }

    [Fact]
    public async Task GanttViewModel_MoveRowBeforeAsync_RejectsAbsentTargetRowBeforeWorkspaceCommand()
    {
        using var context = ReorderTestContext.Create(
            ChapterNodeForReorder("chapter-one.md", "Chapter One", 0),
            ChapterNodeForReorder("chapter-two.md", "Chapter Two", 1));
        var gantt = context.CreateGanttViewModel();
        var staleTarget = new GanttRowViewModel(GanttProjection.Project(
            ChapterNodeForReorder("chapter-two.md", "Chapter Two", 1),
            MakePhase(start: "2024-01-01", end: "2024-01-31")));

        await gantt.MoveRowBeforeAsync(ChapterRow(gantt, 0), staleTarget);

        Assert.Empty(context.StructureService.ReorderCalls);
    }

    [Fact]
    public async Task GanttViewModel_MoveRowAfterAsync_RejectsNoOpSelfDropBeforeWorkspaceCommand()
    {
        using var context = ReorderTestContext.Create(
            ChapterNodeForReorder("chapter-one.md", "Chapter One", 0),
            ChapterNodeForReorder("chapter-two.md", "Chapter Two", 1));
        var gantt = context.CreateGanttViewModel();
        var row = ChapterRow(gantt, 0);

        await gantt.MoveRowAfterAsync(row, row);

        Assert.Empty(context.StructureService.ReorderCalls);
    }

    [Fact]
    public async Task GanttViewModel_MoveRowAfterAsync_ReordersBookTxtWithinPartAndFreshProjectionReflectsPersistedOrder()
    {
        var workspace = CreateWorkspaceOnDisk(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md\npart-one/chapter-three.md\npart-two/part.md\npart-two/chapter-four.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"),
            ("part-one/chapter-three.md", "# Chapter Three"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-four.md", "# Chapter Four"));

        IntegrationWorkspaceContext? context = null;
        IntegrationWorkspaceContext? freshContext = null;
        try
        {
            context = new IntegrationWorkspaceContext(workspace.Root);
            await context.OpenWorkspaceAsync();
            await WaitForVisibleOrderAsync(context.Workspace,
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md",
                "part-two/part.md",
                "part-two/chapter-four.md");

            var gantt = context.CreateGanttViewModel();
            var chapterOne = gantt.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-one.md", StringComparison.OrdinalIgnoreCase));
            var chapterThree = gantt.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-three.md", StringComparison.OrdinalIgnoreCase));

            await gantt.MoveRowAfterAsync(chapterOne, chapterThree);

            var call = Assert.Single(context.StructureService.ReorderCalls);
            Assert.Equal("part-one/chapter-one.md", call.Path);
            Assert.Equal(3, call.NewIndex);
            Assert.True(call.WatcherWasSuppressed);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md",
                "part-one/chapter-one.md",
                "part-two/part.md",
                "part-two/chapter-four.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
            Assert.Equal("part-one/chapter-one.md", gantt.SelectedRow?.RelativePath);
            Assert.Empty(context.NotificationService.Errors);
            context.Dispose();
            context = null;

            freshContext = new IntegrationWorkspaceContext(workspace.Root);
            await freshContext.OpenWorkspaceAsync();
            await WaitForVisibleOrderAsync(freshContext.Workspace,
                "part-one/part.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md",
                "part-one/chapter-one.md",
                "part-two/part.md",
                "part-two/chapter-four.md");

            var freshGantt = freshContext.CreateGanttViewModel();
            Assert.Equal(new[]
            {
                "__book__",
                "part-one/part.md",
                "part-one/chapter-two.md",
                "part-one/chapter-three.md",
                "part-one/chapter-one.md",
                "part-two/part.md",
                "part-two/chapter-four.md"
            }, freshGantt.Rows.Select(row => row.RelativePath));
        }
        finally
        {
            context?.Dispose();
            freshContext?.Dispose();
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task GanttViewModel_MoveRowAfterAsync_CrossPartAttemptShowsWorkspaceErrorAndLeavesBookTxtUnchanged()
    {
        var workspace = CreateWorkspaceOnDisk(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-two/part.md\npart-two/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-two/part.md", "{class: part}\n# Part Two"),
            ("part-two/chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new IntegrationWorkspaceContext(workspace.Root);
            await context.OpenWorkspaceAsync();

            var gantt = context.CreateGanttViewModel();
            var source = gantt.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-one.md", StringComparison.OrdinalIgnoreCase));
            var target = gantt.Rows.Single(row => string.Equals(row.RelativePath, "part-two/chapter-two.md", StringComparison.OrdinalIgnoreCase));

            await gantt.MoveRowAfterAsync(source, target);

            Assert.Empty(context.StructureService.ReorderCalls);
            var error = Assert.Single(context.NotificationService.Errors);
            Assert.Contains("part-one/chapter-one.md", error);
            Assert.Contains("cannot be moved across Part sections", error);
            Assert.Contains("part-one/part.md", error);
            Assert.Contains("part-two/part.md", error);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-two/part.md",
                "part-two/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public async Task GanttViewModel_ReorderCommands_IgnoreBookAndPartRowsWithoutMutatingBookTxt()
    {
        var workspace = CreateWorkspaceOnDisk(
            ("Book.txt", "part-one/part.md\npart-one/chapter-one.md\npart-one/chapter-two.md"),
            ("part-one/part.md", "{class: part}\n# Part One"),
            ("part-one/chapter-one.md", "# Chapter One"),
            ("part-one/chapter-two.md", "# Chapter Two"));

        try
        {
            using var context = new IntegrationWorkspaceContext(workspace.Root);
            await context.OpenWorkspaceAsync();

            var gantt = context.CreateGanttViewModel();
            var bookRow = gantt.Rows.Single(row => row.IsBook);
            var partRow = gantt.Rows.Single(row => row.IsPart);
            var chapterRow = gantt.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-one.md", StringComparison.OrdinalIgnoreCase));

            await gantt.MoveRowAfterAsync(bookRow, chapterRow);
            await gantt.MoveRowAfterAsync(chapterRow, partRow);
            gantt.SelectedRow = partRow;
            await ExecuteCommandAsync(gantt.MoveSelectedRowUpCommand.Execute());
            await ExecuteCommandAsync(gantt.MoveSelectedRowDownCommand.Execute());

            Assert.Empty(context.StructureService.ReorderCalls);
            Assert.Empty(context.NotificationService.Errors);
            Assert.Equal(new[]
            {
                "part-one/part.md",
                "part-one/chapter-one.md",
                "part-one/chapter-two.md"
            }, ReadBookTxtLines(workspace.BookTxtPath));
        }
        finally
        {
            DeleteWorkspace(workspace.Root);
        }
    }

    [Fact]
    public void GanttViewModel_Source_DoesNotReferenceDirectBookTxtPersistencePath()
    {
        var source = File.ReadAllText(FindRepositoryFile("src/Hymnal/ViewModels/GanttViewModel.cs"));

        Assert.DoesNotContain("IBookTxtStructureService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BookTxtStructureService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteTextAtomicAsync", source, StringComparison.Ordinal);
    }

    private static ChapterNode ChapterNodeForReorder(string path, string title, int index) =>
        new(path, path, title, NodeKind.Chapter, IsMissing: false, Index: index);

    private static ChapterNode PartNode(string path, string title, int index) =>
        new(path, path, title, NodeKind.Part, IsMissing: false, Index: index);

    private static async Task ExecuteCommandAsync(IObservable<global::System.Reactive.Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));

        await completion.Task;
    }

    private static (string Root, string BookTxtPath) CreateWorkspaceOnDisk(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hymnal-gantt-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
            WriteWorkspaceFile(root, relativePath, content);

        return (root, Path.Combine(root, "Book.txt"));
    }

    private static void WriteWorkspaceFile(string root, string relativePath, string content)
    {
        var absolutePath = AbsolutePath(root, relativePath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(absolutePath, content);
    }

    private static string AbsolutePath(string workspaceRoot, string relativePath) =>
        Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string[] ReadBookTxtLines(string bookTxtPath) => File.ReadAllLines(bookTxtPath);

    private static void DeleteWorkspace(string root)
    {
        if (!Directory.Exists(root))
            return;

        // File watchers may still hold handles briefly after Dispose() — retry with backoff.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (1 << attempt));
            }
        }
    }

    private static async Task WaitForVisibleOrderAsync(WorkspaceViewModel workspace, params string[] expectedPaths)
    {
        await WaitUntilAsync(
            () => workspace.VisibleNodes.Select(node => node.Node.RelativePath).SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase),
            $"Workspace visible nodes did not reach expected order {string.Join(", ", expectedPaths)}; actual order was {string.Join(", ", workspace.VisibleNodes.Select(node => node.Node.RelativePath))}.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;

            ReactiveUiTestBootstrap.RunOnUiThread(() => { });
            await Task.Delay(25);
        }

        Assert.Fail(failureMessage);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private sealed class IntegrationWorkspaceContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public RecordingNotificationService NotificationService { get; } = new();
        public MetadataStore MetadataStore { get; } = new();
        public InMemoryAppSettingsStore SettingsStore { get; } = new();
        public PhaseDataService PhaseDataService { get; }
        public TargetsService TargetsService { get; }
        public WordCountService WordCountService { get; } = new();
        public StubFolderPickerService FolderPickerService { get; }
        public StubFilePickerService FilePickerService { get; } = new();
        public ExclusionManifestService ExclusionManifestService { get; }
        public ManuscriptService ManuscriptService { get; }
        public OrphanFileDiscoveryService OrphanFileDiscoveryService { get; } = new();
        public ChapterRegistryService RegistryService { get; }
        public EditorViewModel Editor { get; }
        public RecordingStructureService StructureService { get; }
        public WorkspaceViewModel Workspace { get; }

        public IntegrationWorkspaceContext(string workspaceRoot)
        {
            FolderPickerService = new StubFolderPickerService(workspaceRoot);
            ExclusionManifestService = new ExclusionManifestService(MetadataStore);
            RegistryService = new ChapterRegistryService(MetadataStore);
            PhaseDataService = new PhaseDataService(MetadataStore);
            TargetsService = new TargetsService(MetadataStore);
            Editor = _owned.Track(new EditorViewModel(MetadataStore, NotificationService, WordCountService, new StoryFileWriter(MetadataStore)));
            var history = new WordCountHistoryService(MetadataStore);
            ManuscriptService = _owned.Track(new ManuscriptService(NotificationService, ExclusionManifestService));
            StructureService = new RecordingStructureService();
            StructureService.Configure(new BookTxtStructureService(MetadataStore, ExclusionManifestService, RegistryService), ManuscriptService);

            Workspace = new WorkspaceViewModel(
                ManuscriptService,
                StructureService,
                FilePickerService,
                SettingsStore,
                FolderPickerService,
                NotificationService,
                Editor,
                RegistryService,
                PhaseDataService,
                TargetsService,
                WordCountService,
                history,
                ExclusionManifestService,
                OrphanFileDiscoveryService);
        }

        public GanttViewModel CreateGanttViewModel() =>
            new(Workspace, NotificationService);

        public void Dispose() => _owned.Dispose();

        public async Task OpenWorkspaceAsync()
        {
            var result = await ManuscriptService.LoadWorkspaceAsync(FolderPickerService.WorkspaceRoot);
            Assert.True(result.IsSuccess, result.Error);

            var model = result.Value!;
            var activeNodes = model.Nodes.Items.OrderBy(node => node.Index).ToList();
            var activePaths = activeNodes.Select(node => node.RelativePath).ToList();
            var projectionMethod = typeof(WorkspaceViewModel).GetMethod("ProjectSidebarNodesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate WorkspaceViewModel sidebar projection method.");
            var projectionTask = (Task<IReadOnlyList<ChapterNode>>)projectionMethod.Invoke(Workspace, new object[] { model, activeNodes, activePaths })!;
            var projectedNodes = await projectionTask;

            var registry = await RegistryService.LoadAsync(model.WorkspaceRoot);
            registry = RegistryService.ReconcileOrphans(registry, activePaths);
            foreach (var node in activeNodes)
                RegistryService.AssignUuid(registry, node.RelativePath, node.Title);
            await RegistryService.SaveAsync(model.WorkspaceRoot, registry);

            var phases = await PhaseDataService.LoadAsync(model.WorkspaceRoot);
            var targets = await TargetsService.LoadAsync(model.WorkspaceRoot);
            SeedWorkspace(model, projectedNodes, registry, phases, targets);
        }

        private void SeedWorkspace(
            ManuscriptModel model,
            IReadOnlyList<ChapterNode> nodes,
            IReadOnlyDictionary<string, ChapterRegistryEntry> registry,
            IReadOnlyDictionary<string, PhaseData> phases,
            Dictionary<string, WordCountTarget> targets)
        {
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), Path.GetFileName(model.WorkspaceRoot));

            var workspaceNodes = GetPrivateList<ChapterViewModel>(Workspace, "_nodes");
            var visibleNodes = GetPrivateList<ChapterViewModel>(Workspace, "_visibleNodes");
            var lookup = GetPrivateDictionary<string, ChapterViewModel>(Workspace, "_nodesByPath");

            foreach (var node in workspaceNodes.Where(node => node is IDisposable).Cast<IDisposable>())
                node.Dispose();

            workspaceNodes.Clear();
            visibleNodes.Clear();
            lookup.Clear();

            foreach (var node in nodes.OrderBy(node => node.Index))
            {
                var uuid = registry.Values.FirstOrDefault(entry => string.Equals(entry.CurrentPath, node.RelativePath, StringComparison.OrdinalIgnoreCase))?.Uuid ?? string.Empty;
                phases.TryGetValue(uuid, out var phaseData);
                var target = string.IsNullOrWhiteSpace(uuid) ? null : TargetsService.GetTarget(targets, uuid);
                var vm = new ChapterViewModel(
                    node,
                    uuid,
                    phaseData,
                    TestMetadataStores.Sidecar(PhaseDataService, TargetsService),
                    SettingsStore,
                    NotificationService,
                    model.WorkspaceRoot,
                    target);
                workspaceNodes.Add(vm);
                visibleNodes.Add(vm);
                lookup[node.RelativePath] = vm;
            }
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public List<string> Successes { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) => Successes.Add(message);
    }

    private sealed class InMemoryAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key)
        {
            if (_values.TryGetValue(key, out var value) && value is T typed)
                return Task.FromResult<T?>(typed);

            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFolderPickerService : IFolderPickerService
    {
        private readonly string _workspaceRoot;

        public StubFolderPickerService(string workspaceRoot) => _workspaceRoot = workspaceRoot;

        public string WorkspaceRoot => _workspaceRoot;

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(_workspaceRoot);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingStructureService : IBookTxtStructureService
    {
        private IBookTxtStructureService? _inner;
        private ManuscriptService? _manuscriptService;

        public List<ReorderCallWithWatcher> ReorderCalls { get; } = new();

        public void Configure(IBookTxtStructureService inner, ManuscriptService manuscriptService)
        {
            _inner = inner;
            _manuscriptService = manuscriptService;
        }

        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath) =>
            Inner.ReadNormalizedEntriesAsync(bookTxtPath);

        public async Task<Result<Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
        {
            ReorderCalls.Add(new ReorderCallWithWatcher(bookTxtPath, chapterPath, newIndex, ReadSuppressCount() > 0));
            return await Inner.ReorderEntryAsync(bookTxtPath, chapterPath, newIndex);
        }

        public Task<Result<Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath) =>
            Inner.RenameEntryAsync(bookTxtPath, existingPath, replacementPath);

        public Task<Result<Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Inner.AddExistingEntryAsync(bookTxtPath, chapterPath, index);

        public Task<Result<Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Inner.AddExistingEntryAfterPartAsync(bookTxtPath, chapterPath, partPath);

        public Task<Result<Unit>> IncludeExistingEntryAsync(string bookTxtPath, string chapterPath, int index) =>
            Inner.IncludeExistingEntryAsync(bookTxtPath, chapterPath, index);

        public Task<Result<Unit>> IncludeExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) =>
            Inner.IncludeExistingEntryAfterPartAsync(bookTxtPath, chapterPath, partPath);

        public Task<Result<Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index) =>
            Inner.CreateNewChapterAsync(bookTxtPath, chapterPath, content, index);

        public Task<Result<Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index) =>
            Inner.CreateNewPartAsync(bookTxtPath, partPath, title, index);

        public Task<Result<Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath) =>
            Inner.RemoveEntryAsync(bookTxtPath, chapterPath);

        public Task<Result<Unit>> ExcludeEntryAsync(string bookTxtPath, string chapterPath) =>
            Inner.ExcludeEntryAsync(bookTxtPath, chapterPath);

        public Task<Result<Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath) =>
            Inner.DeleteChapterFileAsync(bookTxtPath, chapterPath);

        private IBookTxtStructureService Inner => _inner ?? throw new InvalidOperationException("Recording structure service was not configured.");

        private int ReadSuppressCount()
        {
            if (_manuscriptService == null)
                return 0;

            var field = typeof(ManuscriptService).GetField("_suppressCount", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate ManuscriptService watcher suppression field.");
            return (int)field.GetValue(_manuscriptService)!;
        }
    }

    private sealed record ReorderCallWithWatcher(string BookTxtPath, string Path, int NewIndex, bool WatcherWasSuppressed);

    private static void SetPrivateField<T>(WorkspaceViewModel workspace, string fieldName, T value)
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
        field.SetValue(workspace, value);
    }

    private static void SetPrivateProperty<T>(WorkspaceViewModel workspace, string propertyName, T value)
    {
        var property = typeof(WorkspaceViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel property '{propertyName}'.");
        property.SetValue(workspace, value);
    }

    private static IList<T> GetPrivateList<T>(WorkspaceViewModel workspace, string fieldName)
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
        return (IList<T>)field.GetValue(workspace)!;
    }

    private static IDictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(WorkspaceViewModel workspace, string fieldName)
        where TKey : notnull
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
        return (IDictionary<TKey, TValue>)field.GetValue(workspace)!;
    }

    private sealed class ReorderTestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        private readonly string _workspaceRoot;
        private readonly PhaseDataService _phaseDataService;
        private readonly TargetsService _targetsService;
        private readonly IAppSettingsStore _settingsStore;
        private readonly INotificationService _notificationService;

        public RecordingBookTxtStructureService StructureService { get; }
        public WorkspaceViewModel Workspace { get; }

        private ReorderTestContext(string workspaceRoot, IReadOnlyList<ChapterNode> nodes)
        {
            _workspaceRoot = workspaceRoot;
            var metadataStore = new MetadataStore();
            _phaseDataService = new PhaseDataService(metadataStore);
            _targetsService = new TargetsService(metadataStore);
            _settingsStore = Substitute.For<IAppSettingsStore>();
            _notificationService = Substitute.For<INotificationService>();
            StructureService = new RecordingBookTxtStructureService();

            var manuscriptService = _owned.Track(new ManuscriptService(
                _notificationService,
                new ExclusionManifestService(metadataStore)));
            var editor = _owned.Track(new EditorViewModel(metadataStore, _notificationService, new WordCountService(), new StoryFileWriter(metadataStore)));

            Workspace = new WorkspaceViewModel(
                manuscriptService,
                StructureService,
                Substitute.For<IFilePickerService>(),
                _settingsStore,
                Substitute.For<IFolderPickerService>(),
                _notificationService,
                editor,
                new ChapterRegistryService(metadataStore),
                _phaseDataService,
                _targetsService,
                new WordCountService(),
                new WordCountHistoryService(metadataStore),
                new ExclusionManifestService(metadataStore),
                new OrphanFileDiscoveryService());

            SeedWorkspace(nodes);
        }

        public static ReorderTestContext Create(params ChapterNode[] nodes)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hymnal-gantt-reorder-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new ReorderTestContext(root, nodes);
        }

        public GanttViewModel CreateGanttViewModel() =>
            new(Workspace, _notificationService);

        private void SeedWorkspace(IReadOnlyList<ChapterNode> nodes)
        {
            var model = new ManuscriptModel();
            model.SetRoots(_workspaceRoot, _workspaceRoot);
            model.Load(nodes);

            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);

            var workspaceNodes = GetPrivateList<ChapterViewModel>(Workspace, "_nodes");
            var visibleNodes = GetPrivateList<ChapterViewModel>(Workspace, "_visibleNodes");
            var lookup = GetPrivateDictionary<string, ChapterViewModel>(Workspace, "_nodesByPath");

            workspaceNodes.Clear();
            visibleNodes.Clear();
            lookup.Clear();

            foreach (var node in nodes.OrderBy(node => node.Index))
            {
                var vm = new ChapterViewModel(
                    node,
                    $"uuid-{node.Index}",
                    MakePhase(start: "2024-01-01", end: "2024-01-31"),
                    TestMetadataStores.Sidecar(_phaseDataService, _targetsService),
                    _settingsStore,
                    _notificationService,
                    _workspaceRoot);
                workspaceNodes.Add(vm);
                visibleNodes.Add(vm);
                lookup[node.RelativePath] = vm;
            }
        }

        private static void SetPrivateField<T>(WorkspaceViewModel workspace, string fieldName, T value)
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            field.SetValue(workspace, value);
        }

        private static void SetPrivateProperty<T>(WorkspaceViewModel workspace, string propertyName, T value)
        {
            var property = typeof(WorkspaceViewModel).GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel property '{propertyName}'.");
            property.SetValue(workspace, value);
        }

        private static IList<T> GetPrivateList<T>(WorkspaceViewModel workspace, string fieldName)
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            return (IList<T>)field.GetValue(workspace)!;
        }

        private static IDictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(WorkspaceViewModel workspace, string fieldName)
            where TKey : notnull
        {
            var field = typeof(WorkspaceViewModel).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Unable to locate WorkspaceViewModel field '{fieldName}'.");
            return (IDictionary<TKey, TValue>)field.GetValue(workspace)!;
        }

        public void Dispose()
        {
            try
            {
                _owned.Dispose();
            }
            finally
            {
                if (Directory.Exists(_workspaceRoot))
                    Directory.Delete(_workspaceRoot, recursive: true);
            }
        }
    }

    private sealed class RecordingBookTxtStructureService : IBookTxtStructureService
    {
        public List<ReorderCall> ReorderCalls { get; } = new();

        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));

        public Task<Result<Hymnal.Core.Common.Unit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
        {
            ReorderCalls.Add(new ReorderCall(bookTxtPath, chapterPath, newIndex));
            return Task.FromResult(Result<Hymnal.Core.Common.Unit>.Fail("Stop after recording reorder."));
        }

        public Task<Result<Hymnal.Core.Common.Unit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath) => NotSupported();
        public Task<Result<Hymnal.Core.Common.Unit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index) => NotSupported();
        public Task<Result<Hymnal.Core.Common.Unit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath) => NotSupported();
        public Task<Result<Hymnal.Core.Common.Unit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index) => NotSupported();
        public Task<Result<Hymnal.Core.Common.Unit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index) => NotSupported();
        public Task<Result<Hymnal.Core.Common.Unit>> RemoveEntryAsync(string bookTxtPath, string chapterPath) => NotSupported();
        public Task<Result<Hymnal.Core.Common.Unit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath) => NotSupported();

        private static Task<Result<Hymnal.Core.Common.Unit>> NotSupported() =>
            Task.FromResult(Result<Hymnal.Core.Common.Unit>.Fail("Unexpected structure service call."));
    }

    private sealed record ReorderCall(string BookTxtPath, string ChapterPath, int NewIndex);

    // ── Helpers for Part rollup tests ─────────────────────────────────────────

    private static void InjectNode(ChapterViewModel vm, ChapterNode node)
    {
        // ChapterViewModel.Node has a 'private set' backed by '_node'.
        var field = typeof(ChapterViewModel).GetField(
            "_node",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate ChapterViewModel._node backing field.");
        field.SetValue(vm, node);
    }
}

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Builder;
using Xunit;

namespace Hymnal.Core.Tests.Views;

[Collection("AvaloniaUi")]
public sealed class GanttViewSmokeTests
{
    static GanttViewSmokeTests()
    {
        ReactiveUiTestBootstrap.EnsureInitialized();
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public void GanttView_XamlDeclaresGridKeyboardHandlerAndCanvasReorderHook()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/GanttView.axaml"));
        var codeBehind = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/GanttView.axaml.cs"));

        Assert.Contains("KeyDown=\"ChapterGrid_KeyDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowReorderRequested=\"TimelineCanvas_RowReorderRequested\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ChapterGrid_PointerPressed", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ChapterGrid_PointerMoved", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ChapterGrid_PointerReleased", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MoveSelectedRowUpCommand", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MoveSelectedRowDownCommand", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("BookTxtStructureService", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteTextAtomicAsync", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void GanttView_TimelineRowReorderHandler_DelegatesToViewModelReorderPath()
    {
        ReactiveUiTestBootstrap.RunOnUiThread(() =>
        {
            var workspace = CreateWorkspaceWithReorderSpy(
                out var reorderRequests,
                CreateNodeViewModel("part-one/part.md", "Part One", NodeKind.Part),
                CreateNodeViewModel("part-one/chapter-one.md", "Chapter One"),
                CreateNodeViewModel("part-one/chapter-two.md", "Chapter Two"));

            var viewModel = new GanttViewModel(
                workspace,
                new PhaseDataService(Substitute.For<IMetadataStore>()),
                Substitute.For<INotificationService>());

            var sourceRow = viewModel.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-two.md", StringComparison.OrdinalIgnoreCase));
            var targetRow = viewModel.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-one.md", StringComparison.OrdinalIgnoreCase));

            var view = new GanttView { DataContext = viewModel };
            LayoutView(view);

            var args = new GanttCanvas.GanttRowReorderRequestedEventArgs(sourceRow, targetRow, dropBeforeTarget: true);
            InvokePrivateAsync(view, "HandleTimelineRowReorderAsync", args).GetAwaiter().GetResult();

            var request = Assert.Single(reorderRequests);
            Assert.Equal("part-one/chapter-two.md", request.RelativePath);
            Assert.Equal("part-one/chapter-one.md", request.BeforeRelativePath);
            Assert.Null(request.AfterRelativePath);
            Assert.Same(viewModel.SelectedRow, view.FindControl<DataGrid>("ChapterGrid")!.SelectedItem);
        });
    }

    [Fact]
    public void GanttView_GridRowDragHandlers_DelegateToViewModelReorderPath()
    {
        ReactiveUiTestBootstrap.RunOnUiThread(() =>
        {
            var workspace = CreateWorkspaceWithReorderSpy(
                out var reorderRequests,
                CreateNodeViewModel("part-one/part.md", "Part One", NodeKind.Part),
                CreateNodeViewModel("part-one/chapter-one.md", "Chapter One"),
                CreateNodeViewModel("part-one/chapter-two.md", "Chapter Two"));

            var viewModel = new GanttViewModel(
                workspace,
                new PhaseDataService(Substitute.For<IMetadataStore>()),
                Substitute.For<INotificationService>());

            var view = new GanttView { DataContext = viewModel };
            LayoutView(view);

            var sourcePoint = new Point(12, 36 + 3 * 28 + 14);
            var targetPoint = new Point(12, 36 + 2 * 28 + 4);
            InvokePrivate(view, "ChapterGrid_PointerPressedStateForTests", sourcePoint, "part-one/chapter-two.md");
            InvokePrivate(view, "ChapterGrid_PointerMovedStateForTests", targetPoint);
            InvokePrivateAsync(view, "ChapterGrid_PointerReleasedStateForTests", targetPoint).GetAwaiter().GetResult();

            var request = Assert.Single(reorderRequests);
            Assert.Equal("part-one/chapter-two.md", request.RelativePath);
            Assert.Equal("part-one/chapter-one.md", request.BeforeRelativePath);
            Assert.Null(request.AfterRelativePath);
            Assert.Same(viewModel.SelectedRow, view.FindControl<DataGrid>("ChapterGrid")!.SelectedItem);
        });
    }

    [Fact]
    public void GanttView_RowMoveShortcutHelper_DelegatesForEditableChapterRowsAndIgnoresPartRows()
    {
        ReactiveUiTestBootstrap.RunOnUiThread(() =>
        {
            var workspace = CreateWorkspaceWithReorderSpy(
                out var reorderRequests,
                CreateNodeViewModel("part-one/part.md", "Part One", NodeKind.Part),
                CreateNodeViewModel("part-one/chapter-one.md", "Chapter One"),
                CreateNodeViewModel("part-one/chapter-two.md", "Chapter Two"));

            var viewModel = new GanttViewModel(
                workspace,
                new PhaseDataService(Substitute.For<IMetadataStore>()),
                Substitute.For<INotificationService>());

            var view = new GanttView { DataContext = viewModel };
            LayoutView(view);

            var chapterGrid = view.FindControl<DataGrid>("ChapterGrid")!;
            var chapterRow = viewModel.Rows.Single(row => string.Equals(row.RelativePath, "part-one/chapter-two.md", StringComparison.OrdinalIgnoreCase));
            chapterGrid.SelectedItem = chapterRow;

            var handled = InvokePrivateAsync<bool>(view, "TryHandleRowMoveShortcutAsync", Key.Up, KeyModifiers.Alt)
                .GetAwaiter()
                .GetResult();

            Assert.True(handled);
            var request = Assert.Single(reorderRequests);
            Assert.Equal("part-one/chapter-two.md", request.RelativePath);
            Assert.Equal("part-one/chapter-one.md", request.BeforeRelativePath);
            Assert.Same(viewModel.SelectedRow, chapterGrid.SelectedItem);

            reorderRequests.Clear();
            var partRow = viewModel.Rows.Single(row => row.IsPart && !row.IsBook);
            chapterGrid.SelectedItem = partRow;
            viewModel.SelectedRow = partRow;

            handled = InvokePrivateAsync<bool>(view, "TryHandleRowMoveShortcutAsync", Key.Up, KeyModifiers.Alt)
                .GetAwaiter()
                .GetResult();

            Assert.False(handled);
            Assert.Empty(reorderRequests);
        });
    }

    private static ChapterViewModel CreateNodeViewModel(
        string relativePath,
        string title,
        NodeKind kind = NodeKind.Chapter)
    {
        var metadataStore = Substitute.For<IMetadataStore>();
        var phaseDataService = new PhaseDataService(metadataStore);
        var targetsService = new TargetsService(metadataStore);
        var settingsStore = Substitute.For<IAppSettingsStore>();
        var notificationService = Substitute.For<INotificationService>();

        return new ChapterViewModel(
            new ChapterNode(relativePath, relativePath, title, kind, IsMissing: false, Index: 0),
            Guid.NewGuid().ToString("N"),
            new PhaseData { Status = ChapterStatus.Drafting },
            phaseDataService,
            targetsService,
            settingsStore,
            notificationService,
            workspaceRoot: Path.Combine(Path.GetTempPath(), "hymnal-gantt-view-smoke-tests"));
    }

    private static WorkspaceViewModel CreateWorkspaceWithReorderSpy(
        out Collection<ReorderCardRequest> reorderRequests,
        params ChapterViewModel[] nodes)
    {
        var workspace = (WorkspaceViewModel)RuntimeHelpers.GetUninitializedObject(typeof(WorkspaceViewModel));
        var backing = new ObservableCollection<ChapterViewModel>(nodes);
        var readOnly = new ReadOnlyObservableCollection<ChapterViewModel>(backing);

        SetAutoPropertyBackingField(workspace, "Nodes", readOnly);
        typeof(WorkspaceViewModel)
            .GetField("_profile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(workspace, ProjectProfile.Novel);

        var requests = new Collection<ReorderCardRequest>();
        reorderRequests = requests;
        var reorderCommand = ReactiveCommand.CreateFromTask<ReorderCardRequest>(request =>
        {
            requests.Add(request);
            return Task.CompletedTask;
        });

        SetAutoPropertyBackingField(workspace, "ReorderChapterCommand", reorderCommand);
        return workspace;
    }

    private static void InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        method.Invoke(target, args);
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        var result = method.Invoke(target, args)
            ?? throw new InvalidOperationException($"Method '{methodName}' returned null.");

        if (result is not Task task)
            throw new InvalidOperationException($"Method '{methodName}' did not return Task.");

        await task;
    }

    private static async Task<T> InvokePrivateAsync<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        var result = method.Invoke(target, args)
            ?? throw new InvalidOperationException($"Method '{methodName}' returned null.");

        if (result is not Task<T> task)
            throw new InvalidOperationException($"Method '{methodName}' did not return Task<{typeof(T).Name}>.");

        return await task;
    }

    private static void SetAutoPropertyBackingField(object target, string propertyName, object value)
    {
        var field = target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Backing field for '{propertyName}' was not found.");

        field.SetValue(target, value);
    }

    private static void LayoutView(Control view)
    {
        view.ApplyTemplate();
        view.Measure(new Size(1280, 800));
        view.Arrange(new Rect(0, 0, 1280, 800));
        view.UpdateLayout();
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
}

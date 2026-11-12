using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hymnal.Core.Models;
using Hymnal.ViewModels;

namespace Hymnal.Views;

public partial class SidebarView : UserControl
{
    private ChapterViewModel? _dragSource;
    private string? _dragSourcePath;
    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStart;
    private bool _dragOperationStarted;
    private Panel? _lastDropIndicator;

    public SidebarView()
    {
        InitializeComponent();
    }

    public static bool CanRenameFromSidebar(ChapterNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return !node.IsExcluded
            && !node.IsMissing
            && !string.IsNullOrWhiteSpace(node.RelativePath)
            && (node.Kind == NodeKind.Chapter || node.Kind == NodeKind.Part);
    }

    private void ChapterContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var canRename = menu.DataContext is ChapterViewModel chapter && CanRenameFromSidebar(chapter.Node);
        SetNamedItemVisibility(menu, "RenameMenuItem", canRename);
        SetNamedItemVisibility(menu, "RenameSeparator", canRename);
    }

    private async void RenameNode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;

        if (sender is not MenuItem { DataContext: ChapterViewModel chapter })
            return;

        if (!CanRenameFromSidebar(chapter.Node))
            return;

        var noun = vm.NounFor(chapter).ToLowerInvariant();
        var dialogTitle = $"Rename {noun}";
        var prompt = chapter.Node.Kind == NodeKind.Part
            ? $"Enter the new sidebar title for this {noun}:"
            : $"Enter the new sidebar title for this {noun}:";

        var replacementTitle = await PromptForTextAsync(dialogTitle, prompt, chapter.Node.Title);
        if (string.IsNullOrWhiteSpace(replacementTitle))
            return;

        replacementTitle = replacementTitle.Trim();
        if (string.Equals(replacementTitle, chapter.Node.Title, StringComparison.Ordinal))
            return;

        if (chapter.Node.Kind == NodeKind.Part)
        {
            await ExecuteCommandAsync(vm.RenamePartCommand.Execute(
                new RenamePartRequest(chapter.Node.RelativePath, replacementTitle)));
            return;
        }

        await ExecuteCommandAsync(vm.RenameChapterCommand.Execute(
            new RenameChapterRequest(chapter.Node.RelativePath, replacementTitle)));
    }

    private async void RemoveFromBook_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        if (sender is MenuItem { DataContext: ChapterViewModel chapter })
            await ExecuteCommandAsync(vm.RemoveMissingEntryCommand.Execute(chapter.Node.RelativePath));
    }

    private async void ExcludeFromBook_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        if (sender is MenuItem { DataContext: ChapterViewModel chapter })
        {
            if (chapter.Node.IsExcluded || chapter.Node.IsMissing || chapter.Node.Kind != Hymnal.Core.Models.NodeKind.Chapter)
                return;

            await ExecuteCommandAsync(vm.RemoveFromBookCommand.Execute(chapter.Node.RelativePath));
        }
    }

    private void FocusStory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel vm && sender is MenuItem { DataContext: ChapterViewModel chapter })
            vm.FocusStory(chapter);
    }

    private async void IncludeInBook_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
        if (sender is MenuItem { DataContext: ChapterViewModel chapter })
        {
            if (!chapter.Node.IsExcluded || chapter.Node.IsMissing || chapter.Node.Kind != Hymnal.Core.Models.NodeKind.Chapter)
                return;

            await ExecuteCommandAsync(vm.IncludeExcludedChapterCommand.Execute(chapter.Node.RelativePath));
        }
    }

    // ── Drag-and-drop ─────────────────────────────────────────────────────────

    private void ChapterRow_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid)
            return;

        if (DragDrop.GetAllowDrop(grid))
            return;

        DragDrop.SetAllowDrop(grid, true);
        DragDrop.AddDragOverHandler(grid, Row_DragOver);
        DragDrop.AddDragLeaveHandler(grid, Row_DragLeave);
        DragDrop.AddDropHandler(grid, Row_Drop);

        grid.AddHandler(PointerPressedEvent, Row_PointerPressed,
            RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        grid.AddHandler(PointerMovedEvent, Row_PointerMoved,
            RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        grid.AddHandler(PointerReleasedEvent, Row_PointerReleased,
            RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChapterViewModel chapter)
            return;

        var point = e.GetCurrentPoint(control);

        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            if (control.ContextMenu is ContextMenu menu)
                menu.Open(control);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (!CanDragFromSidebar(chapter.Node))
            return;

        _dragSource = chapter;
        _dragPressArgs = e;
        _dragStart = e.GetPosition(this);
        _dragOperationStarted = false;
        e.Pointer.Capture(control);
    }

    private async void Row_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null || _dragOperationStarted)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < 4 && Math.Abs(point.Y - _dragStart.Y) < 4)
            return;

        _dragOperationStarted = true;
        _dragSourcePath = _dragSource.Node.RelativePath;
        e.Pointer.Capture(null);

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(_dragSource.Node.RelativePath));

            if (_dragPressArgs is not null)
                await DragDrop.DoDragDropAsync(_dragPressArgs, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
            ClearDragState();
        }
    }

    private void Row_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragOperationStarted)
            ClearDragState();
        e.Pointer.Capture(null);
    }

    private void Row_DragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Grid grid || grid.DataContext is not ChapterViewModel target)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var visibleNodes = GetVisibleSidebarNodes();
        var dropBefore = ShouldDropBefore(target.Node.RelativePath);
        if (string.IsNullOrWhiteSpace(_dragSourcePath) ||
            _dragSource is null ||
            !CanDropOnSidebar(_dragSource.Node, target.Node, visibleNodes, dropBefore))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        if (grid.Parent is not Panel panel)
            return;

        ClearDropIndicator();

        SetInsertionLine(panel, dropBefore);
        _lastDropIndicator = panel;
    }

    private void Row_DragLeave(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
    }

    private async void Row_Drop(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (DataContext is not WorkspaceViewModel vm)
            return;

        if (sender is not Grid grid || grid.DataContext is not ChapterViewModel target)
            return;

        var draggedPath = _dragSourcePath;
        var visibleNodeModels = GetVisibleSidebarNodes();
        var dropBefore = ShouldDropBefore(target.Node.RelativePath);
        if (string.IsNullOrWhiteSpace(draggedPath) ||
            _dragSource is null ||
            !CanDropOnSidebar(_dragSource.Node, target.Node, visibleNodeModels, dropBefore))
            return;

        var visibleNodes = vm.VisibleNodes.Select(n => n.Node.RelativePath).ToList();
        var sourceIndex = visibleNodes.FindIndex(p => string.Equals(p, draggedPath, StringComparison.OrdinalIgnoreCase));
        var targetIndex = visibleNodes.FindIndex(p => string.Equals(p, target.Node.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0)
            return;

        var request = sourceIndex < targetIndex
            ? new ReorderCardRequest(draggedPath, AfterRelativePath: target.Node.RelativePath)
            : new ReorderCardRequest(draggedPath, BeforeRelativePath: target.Node.RelativePath);

        await ExecuteCommandAsync(vm.ReorderChapterCommand.Execute(request));
    }

    private async Task<string?> PromptForTextAsync(string title, string prompt, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = GetResource<IBrush>("Cora.Surface.Panel")
                ?? new SolidColorBrush(Color.Parse("#120932"))
        };

        var input = new TextBox
        {
            Text = initialValue,
            FontSize = 12,
            Padding = new Thickness(8, 6),
            Background = GetResource<IBrush>("Cora.Surface.Void")
                ?? new SolidColorBrush(Color.Parse("#0F0828")),
            Foreground = GetResource<IBrush>("Cora.Text")
                ?? Brushes.White,
            BorderBrush = GetResource<IBrush>("Cora.Border.Subtle")
                ?? new SolidColorBrush(Color.Parse("#2D1B5E")),
            BorderThickness = new Thickness(1)
        };

        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? submitted = null;

        void CloseWithResult(string? value)
        {
            if (!result.Task.IsCompleted)
                result.TrySetResult(value);

            if (dialog.IsVisible)
                dialog.Close();
        }

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Background = GetResource<IBrush>("Cora.Accent.Store")
                ?? new SolidColorBrush(Color.Parse("#9D4EDD")),
            Foreground = GetResource<IBrush>("Cora.IconInk")
                ?? Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6)
        };
        ok.Click += (_, _) =>
        {
            submitted = input.Text?.Trim();
            CloseWithResult(submitted);
        };

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(12, 6)
        };
        cancel.Click += (_, _) => CloseWithResult(null);

        dialog.Content = new Border
        {
            BorderBrush = GetResource<IBrush>("Cora.Border.Subtle")
                ?? new SolidColorBrush(Color.Parse("#2D1B5E")),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        Foreground = GetResource<IBrush>("Cora.Text")
                            ?? Brushes.White,
                        TextWrapping = TextWrapping.Wrap
                    },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel }
                    }
                }
            }
        };

        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        dialog.Closed += (_, _) =>
        {
            if (!result.Task.IsCompleted)
                result.TrySetResult(submitted);
        };

        _ = dialog.ShowDialog(owner);
        return await result.Task;
    }

    private static async Task ExecuteCommandAsync(IObservable<Unit> execution)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = execution.Subscribe(
            _ => { },
            ex => completion.TrySetException(ex),
            () => completion.TrySetResult(null));
        await completion.Task;
    }

    private bool ShouldDropBefore(string targetRelativePath)
    {
        if (DataContext is not WorkspaceViewModel vm || string.IsNullOrWhiteSpace(_dragSourcePath))
            return true;

        var paths = vm.VisibleNodes.Select(n => n.Node.RelativePath).ToList();
        var sourceIndex = paths.FindIndex(p => string.Equals(p, _dragSourcePath, StringComparison.OrdinalIgnoreCase));
        var targetIndex = paths.FindIndex(p => string.Equals(p, targetRelativePath, StringComparison.OrdinalIgnoreCase));
        return sourceIndex > targetIndex;
    }

    private IReadOnlyList<ChapterNode> GetVisibleSidebarNodes()
    {
        return DataContext is WorkspaceViewModel vm
            ? vm.VisibleNodes.Select(node => node.Node).ToList()
            : Array.Empty<ChapterNode>();
    }

    public static bool CanDragFromSidebar(ChapterNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return !node.IsExcluded
            && !node.IsMissing
            && !string.IsNullOrWhiteSpace(node.RelativePath)
            && (node.Kind == NodeKind.Chapter || node.Kind == NodeKind.Part);
    }

    public static bool CanDropOnSidebar(
        ChapterNode source,
        ChapterNode target,
        IReadOnlyList<ChapterNode>? visibleNodes = null,
        bool? dropBefore = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (!CanDragFromSidebar(source) || !CanDragFromSidebar(target))
            return false;

        if (string.Equals(source.RelativePath, target.RelativePath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (source.Kind == NodeKind.Part)
            return CanDropPartOnSidebarPart(target, visibleNodes, dropBefore);

        if (source.Kind == NodeKind.Chapter)
            return CanDropChapterOnSidebarChapter(source, target, visibleNodes);

        return false;
    }

    private static bool CanDropPartOnSidebarPart(
        ChapterNode target,
        IReadOnlyList<ChapterNode>? visibleNodes,
        bool? dropBefore)
    {
        if (target.Kind != NodeKind.Part)
            return false;

        if (dropBefore != false || visibleNodes is null || visibleNodes.Count == 0)
            return true;

        var targetIndex = FindSidebarNodeIndex(visibleNodes, target.RelativePath);
        if (targetIndex < 0)
            return false;

        return FindNextPartIndex(visibleNodes, targetIndex + 1) >= 0;
    }

    private static bool CanDropChapterOnSidebarChapter(
        ChapterNode source,
        ChapterNode target,
        IReadOnlyList<ChapterNode>? visibleNodes)
    {
        if (target.Kind != NodeKind.Chapter)
            return false;

        if (visibleNodes is null || visibleNodes.Count == 0)
            return true;

        var sourceIndex = FindSidebarNodeIndex(visibleNodes, source.RelativePath);
        var targetIndex = FindSidebarNodeIndex(visibleNodes, target.RelativePath);
        if (sourceIndex < 0 || targetIndex < 0)
            return false;

        var sourcePart = FindContainingPartPath(visibleNodes, sourceIndex);
        var targetPart = FindContainingPartPath(visibleNodes, targetIndex);
        return string.Equals(sourcePart, targetPart, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindSidebarNodeIndex(IReadOnlyList<ChapterNode> nodes, string relativePath)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int FindNextPartIndex(IReadOnlyList<ChapterNode> nodes, int startIndex)
    {
        for (var i = startIndex; i < nodes.Count; i++)
        {
            if (!nodes[i].IsExcluded && !nodes[i].IsMissing && nodes[i].Kind == NodeKind.Part)
                return i;
        }

        return -1;
    }

    private static string? FindContainingPartPath(IReadOnlyList<ChapterNode> nodes, int nodeIndex)
    {
        string? currentPart = null;
        for (var i = 0; i <= nodeIndex && i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (!node.IsExcluded && !node.IsMissing && node.Kind == NodeKind.Part)
                currentPart = node.RelativePath;
        }

        return currentPart;
    }

    private void ClearDropIndicator()
    {
        if (_lastDropIndicator != null)
        {
            SetInsertionLine(_lastDropIndicator, null);
            _lastDropIndicator = null;
        }
    }

    private static void SetInsertionLine(Panel panel, bool? dropBefore)
    {
        foreach (var child in panel.Children)
        {
            if (child is not Border border)
                continue;
            if (border.Name == "InsertBefore")
                border.IsVisible = dropBefore == true;
            else if (border.Name == "InsertAfter")
                border.IsVisible = dropBefore == false;
        }
    }

    private static void SetNamedItemVisibility(ContextMenu menu, string name, bool isVisible)
    {
        foreach (var item in menu.Items.OfType<Control>())
        {
            if (item.Name != name)
                continue;

            item.IsVisible = isVisible;
            return;
        }
    }

    private T? GetResource<T>(string key) where T : class
    {
        return this.TryFindResource(key, out var value) && value is T typed
            ? typed
            : null;
    }

    private void ClearDragState()
    {
        _dragSource = null;
        _dragSourcePath = null;
        _dragPressArgs = null;
        _dragOperationStarted = false;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// How Write's rails size, collapse and expand:
/// <list type="bullet">
/// <item>An open rail is 160–600 px wide (<see cref="WriteLayoutSettings"/>); a collapsed one is its 48 px icon
/// strip and can't be stretched; the editor always keeps <see cref="EditorMinWidth"/>.</item>
/// <item>Collapsing and expanding animate (ease-out, <see cref="RailAnimationMs"/>).</item>
/// <item>Dragging an open rail's splitter well past its minimum collapses it on release; dragging a collapsed
/// rail's splitter outwards expands it. The splitter turns to the "snap" colour while letting go would do so.</item>
/// <item>Double-clicking a rail's splitter collapses or expands that rail.</item>
/// <item>When the window gets too narrow for both rails and the editor, the rails give way (down to their
/// minimum) rather than pushing the right rail off-screen. The stored widths are kept.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    internal const double RailSplitterWidth = 6;
    internal const double CollapsedRailWidth = 48;
    internal const double EditorMinWidth = 320;
    internal const double SectionMinHeight = 120;
    private const double RailAnimationMs = 160;

    /// <summary>Letting go of an open rail narrower than this collapses it.</summary>
    internal const double CollapseBelowWidth = WriteLayoutSettings.MinSidebarWidth - 60;

    /// <summary>Dragging a collapsed rail out wider than this expands it.</summary>
    internal const double ExpandAboveWidth = CollapsedRailWidth + 72;

    private enum RailState { Hidden, Collapsed, Open }

    private readonly Dictionary<ColumnDefinition, DispatcherTimer> _railAnimations = new();
    private bool _railsShown;
    private bool _lastLeftOpen;
    private bool _lastRightOpen;
    private WriteLayoutSettings? _lastRailLayout;

    private static void SetRailConstraints(ColumnDefinition column, RailState state)
    {
        switch (state)
        {
            case RailState.Open:
                column.MinWidth = WriteLayoutSettings.MinSidebarWidth;
                column.MaxWidth = WriteLayoutSettings.MaxSidebarWidth;
                break;
            case RailState.Collapsed:
                column.MinWidth = CollapsedRailWidth;
                column.MaxWidth = CollapsedRailWidth;
                break;
            default:
                column.MinWidth = 0;
                column.MaxWidth = double.PositiveInfinity;
                break;
        }
    }

    private void ApplyRail(ColumnDefinition column, bool open, double storedWidth, bool apply, bool animate)
    {
        if (!apply)
            return;
        var target = open ? storedWidth : CollapsedRailWidth;
        var state = open ? RailState.Open : RailState.Collapsed;
        if (animate && IsVisible)
        {
            AnimateRail(column, target, () => SetRailConstraints(column, state));
            return;
        }
        StopRailAnimation(column);
        SetRailConstraints(column, RailState.Hidden);
        column.Width = new GridLength(target);
        SetRailConstraints(column, state);
    }

    private void StopRailAnimation(ColumnDefinition column)
    {
        if (_railAnimations.Remove(column, out var running))
            running.Stop();
    }

    private void AnimateRail(ColumnDefinition column, double to, Action done)
    {
        StopRailAnimation(column);
        var from = column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;
        SetRailConstraints(column, RailState.Hidden);
        if (Math.Abs(from - to) < 1)
        {
            column.Width = new GridLength(to);
            done();
            return;
        }

        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / RailAnimationMs);
            var eased = 1 - Math.Pow(1 - t, 3);
            column.Width = new GridLength(from + (to - from) * eased);
            if (t < 1)
                return;
            timer.Stop();
            _railAnimations.Remove(column);
            done();
            // The final width is the stored one (or the strip); don't let the capture see the last frames.
            SuppressNextCapture();
        };
        _railAnimations[column] = timer;
        timer.Start();
    }

    private void SetupRailInteractions(MainWindowViewModel vm)
    {
        var grid = ShellGrid;
        if (grid is null || grid.ColumnDefinitions.Count < 5)
            return;

        WireRailSplitter(vm, LeftRailSplitter, grid.ColumnDefinitions[0], isLeft: true);
        WireRailSplitter(vm, RightRailSplitter, grid.ColumnDefinitions[4], isLeft: false);

        grid.SizeChanged += (_, e) => KeepRailsClearOfEditor(vm, grid, e.NewSize.Width);
    }

    private void WireRailSplitter(MainWindowViewModel vm, GridSplitter splitter, ColumnDefinition column, bool isLeft)
    {
        double startWidth = 0, startX = 0, intended = 0;
        var dragging = false;
        var wasOpen = false;

        bool IsOpen() => isLeft ? vm.IsAnyLeftPaneOpen : vm.IsAnyRightPaneOpen;
        bool WouldSnap() => wasOpen ? intended < CollapseBelowWidth : intended > ExpandAboveWidth;

        splitter.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed || e.ClickCount > 1)
                return;
            dragging = true;
            wasOpen = IsOpen();
            startWidth = column.ActualWidth;
            startX = e.GetPosition(this).X;
            intended = startWidth;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        splitter.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!dragging)
                return;
            var dx = e.GetPosition(this).X - startX;
            intended = startWidth + (isLeft ? dx : -dx);
            splitter.Classes.Set("snap", WouldSnap());
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        void EndDrag()
        {
            if (!dragging)
                return;
            dragging = false;
            var snap = WouldSnap();
            splitter.Classes.Set("snap", false);
            if (!snap)
                return;
            if (wasOpen)
            {
                // The column stopped at its minimum; start the collapse from there.
                if (isLeft) vm.SetLeftRailCollapsed(true);
                else vm.SetRightRailCollapsed(true);
            }
            else if (isLeft)
            {
                vm.ExpandLeftRail();
            }
            else
            {
                vm.ExpandRightRail();
            }
        }

        splitter.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel, handledEventsToo: true);
        splitter.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel, handledEventsToo: true);

        splitter.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            if (isLeft)
                vm.ToggleSidebarCommand.Execute().Subscribe(_ => { }, _ => { });
            else
                vm.ToggleRightRailCommand.Execute().Subscribe(_ => { }, _ => { });
        };
    }

    /// <summary>
    /// When the window is too narrow for both rails and the editor's minimum, narrow the open rails (the right
    /// one first) down to their minimum. Only the on-screen widths change; the capture is suppressed so the
    /// stored widths come back when the window is wide again (on the next open/close or restart).
    /// </summary>
    private void KeepRailsClearOfEditor(MainWindowViewModel vm, Grid grid, double width)
    {
        if (!_railsShown || _railAnimations.Count > 0 || width <= 0)
            return;
        var left = grid.ColumnDefinitions[0];
        var right = grid.ColumnDefinitions[4];
        var excess = left.ActualWidth + right.ActualWidth + 2 * RailSplitterWidth + EditorMinWidth - width;
        if (excess <= 0)
            return;

        SuppressNextCapture();
        foreach (var (column, open) in new[] { (right, vm.IsAnyRightPaneOpen), (left, vm.IsAnyLeftPaneOpen) })
        {
            if (!open || excess <= 0)
                continue;
            var give = Math.Min(excess, column.ActualWidth - WriteLayoutSettings.MinSidebarWidth);
            if (give <= 0)
                continue;
            column.Width = new GridLength(column.ActualWidth - give);
            excess -= give;
        }
    }
}

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// The story planner's drag and drop (all decisions are <see cref="CollectionPlannerViewModel"/>'s): a story dragged
/// from the library onto a column is added; from one column to another it moves (Ctrl copies); within a column it
/// reorders; onto the library it is taken out of the column it came from. While dragging, the column shows where it
/// would land and what dropping would do.
/// </summary>
public partial class CollectionPlannerView : UserControl
{
    private PlannerStoryViewModel? _dragStory;
    private PlannerColumnViewModel? _dragFrom;
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _dragging;

    public CollectionPlannerView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(LibraryPanel, true);
        DragDrop.AddDragOverHandler(LibraryPanel, Library_DragOver);
        DragDrop.AddDragLeaveHandler(LibraryPanel, Library_DragLeave);
        DragDrop.AddDropHandler(LibraryPanel, Library_Drop);
    }

    private CollectionPlannerViewModel? Planner => DataContext as CollectionPlannerViewModel;
    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // ── Selecting and starting a drag ─────────────────────────────────────────

    private void LibraryCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PlannerStoryViewModel story } control)
            Press(story, null, control, e);
    }

    private void Entry_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PlannerEntryViewModel entry } control)
            Press(entry.Story, entry.Column, control, e);
    }

    private void Press(PlannerStoryViewModel story, PlannerColumnViewModel? from, Control control, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || Planner is not { } planner)
            return;
        planner.Selected = story;
        _dragStory = story;
        _dragFrom = from;
        _pressArgs = e;
        _pressPoint = e.GetPosition(this);
        _dragging = false;
    }

    private async void Story_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStory is null || _dragging || _pressArgs is null)
            return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) < 5 && Math.Abs(point.Y - _pressPoint.Y) < 5)
            return;

        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(_dragStory.Key));
            await DragDrop.DoDragDropAsync(_pressArgs, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            EndDrag();
        }
    }

    private void Story_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            EndDrag();
    }

    private void Story_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var story = sender switch
        {
            Control { DataContext: PlannerStoryViewModel s } => s,
            Control { DataContext: PlannerEntryViewModel entry } => entry.Story,
            _ => null,
        };
        Planner?.PlanBeats(story);
    }

    private void EndDrag()
    {
        _dragStory = null;
        _dragFrom = null;
        _pressArgs = null;
        _dragging = false;
        ClearDropFeedback();
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    private void Column_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border column || column.Tag is "wired")
            return;
        column.Tag = "wired";
        DragDrop.AddDragOverHandler(column, Column_DragOver);
        DragDrop.AddDragLeaveHandler(column, Column_DragLeave);
        DragDrop.AddDropHandler(column, Column_Drop);
    }

    private enum DropKind { None, Add, Move, Copy, Reorder }

    private (DropKind Kind, int Index, string Hint) Plan(Border column, PlannerColumnViewModel target, DragEventArgs e)
    {
        if (_dragStory is not { } story)
            return (DropKind.None, 0, "");

        var index = DropIndex(column, e.GetPosition(column));
        var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var sameColumn = _dragFrom != null && ReferenceEquals(_dragFrom, target);
        var alreadyThere = target.Entries.Any(x => x.Story == story);

        DropKind kind;
        string hint;
        if (sameColumn)
            (kind, hint) = (DropKind.Reorder, "Reorder");
        else if (alreadyThere)
            return (DropKind.None, index, "Already in this collection");
        else if (_dragFrom == null)
            (kind, hint) = (DropKind.Add, "Add here");
        else if (copy)
            (kind, hint) = (DropKind.Copy, "Copy here (it stays in both)");
        else
            (kind, hint) = (DropKind.Move, "Move here · Ctrl to copy");

        // Book.txt reads the files after a divider as its scenes, so a one-file story lands ahead of those stories.
        var oneFileCount = target.Entries.Count(x => !x.Story.Story.IsMultiScene && x.Story != story);
        if (!story.Story.IsMultiScene && index > oneFileCount)
            hint += " · before stories told in scenes";
        return (kind, index, hint);
    }

    private void Column_DragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: PlannerColumnViewModel target } column)
            return;
        var (kind, index, hint) = Plan(column, target, e);
        e.DragEffects = kind switch
        {
            DropKind.None => DragDropEffects.None,
            DropKind.Add or DropKind.Copy => DragDropEffects.Copy,
            _ => DragDropEffects.Move,
        };
        column.Classes.Set("drop-target", kind != DropKind.None);
        target.DropHint = hint;
        ShowInsertLine(column, kind == DropKind.None ? -1 : index);
    }

    private void Column_DragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border { DataContext: PlannerColumnViewModel target } column)
        {
            column.Classes.Set("drop-target", false);
            target.DropHint = "";
            ShowInsertLine(column, -1);
        }
    }

    private async void Column_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: PlannerColumnViewModel target } column
            || _dragStory is not { } story || Planner is not { } planner)
            return;
        var (kind, index, _) = Plan(column, target, e);
        var from = _dragFrom;
        ClearDropFeedback();
        switch (kind)
        {
            case DropKind.Add:
                await planner.AddAsync(story, target, index);
                break;
            case DropKind.Move or DropKind.Copy or DropKind.Reorder when from != null:
                await planner.MoveAsync(story, from, target, index, copy: kind == DropKind.Copy);
                break;
        }
    }

    /// <summary>Where in the column a drop lands: before the first entry whose middle is below the pointer.</summary>
    private static int DropIndex(Border column, Point point)
    {
        var entries = EntryBorders(column).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var top = entries[i].TranslatePoint(new Point(0, 0), column);
            if (top is { } p && point.Y < p.Y + entries[i].Bounds.Height / 2)
                return i;
        }
        return entries.Count;
    }

    private static System.Collections.Generic.IEnumerable<Border> EntryBorders(Visual column) =>
        column.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("story") && b.DataContext is PlannerEntryViewModel);

    private static void ShowInsertLine(Visual column, int index)
    {
        foreach (var gap in column.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("entry-gap")))
            gap.Classes.Set("insert", gap.DataContext is PlannerEntryViewModel entry && entry.Index == index);
    }

    private void ClearDropFeedback()
    {
        foreach (var column in this.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("column")))
        {
            column.Classes.Set("drop-target", false);
            ShowInsertLine(column, -1);
            if (column.DataContext is PlannerColumnViewModel vm)
                vm.DropHint = "";
        }
        LibraryPanel.Classes.Set("drop-target", false);
        LibraryHint.Text = LibraryHintText;
    }

    // ── Library: drop a column's story here to take it out of that collection ─

    private const string LibraryHintText =
        "Every story in the folder. Drag one onto a collection to add it; a dot marks each collection it's in.";

    private void Library_DragOver(object? sender, DragEventArgs e)
    {
        var removing = _dragFrom != null && _dragStory != null;
        e.DragEffects = removing ? DragDropEffects.Move : DragDropEffects.None;
        LibraryPanel.Classes.Set("drop-target", removing);
        if (removing)
            LibraryHint.Text = $"Drop to take “{_dragStory!.Title}” out of {(_dragFrom!.Title.Length > 0 ? $"“{_dragFrom.Title}”" : "the open collection")}. Its files stay.";
    }

    private void Library_DragLeave(object? sender, DragEventArgs e)
    {
        LibraryPanel.Classes.Set("drop-target", false);
        LibraryHint.Text = LibraryHintText;
    }

    private async void Library_Drop(object? sender, DragEventArgs e)
    {
        var story = _dragStory;
        var from = _dragFrom;
        ClearDropFeedback();
        if (story != null && from != null && Planner is { } planner)
            await planner.RemoveAsync(story, from);
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void AllFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (Planner is { } planner)
            planner.ShowUnshelvedOnly = false;
    }

    private void UnshelvedFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (Planner is { } planner)
            planner.ShowUnshelvedOnly = true;
    }

    private async void OpenCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PlannerColumnViewModel { Ref.Slug: { } slug } }
            && Owner is { DataContext: MainWindowViewModel main } owner)
        {
            await CollectionMenu.OpenAsync(owner, main, slug);
            if (Planner is { } planner)
                await planner.RefreshAsync();
        }
    }

    private async void NewCollection_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is { DataContext: MainWindowViewModel main } owner)
        {
            await CollectionMenu.NewAsync(owner, main);
            if (Planner is { } planner)
                await planner.RefreshAsync();
        }
    }

    private void PlanBeats_Click(object? sender, RoutedEventArgs e) => Planner?.PlanBeats(Planner.Selected);

    private void Write_Click(object? sender, RoutedEventArgs e) => Planner?.Write(Planner.Selected);
}

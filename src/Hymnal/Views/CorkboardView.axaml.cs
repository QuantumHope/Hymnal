using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hymnal.ViewModels;

namespace Hymnal.Views;

public partial class CorkboardView : UserControl
{
    private ChapterCardItemViewModel? _dragSource;
    private string? _dragSourcePath;
    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStart;
    private bool _dragOperationStarted;
    private Panel? _lastDropIndicator;
    /// <summary>Anthology: a Part header click waiting to see whether it is the first half of a double-click.</summary>
    private DispatcherTimer? _pendingPartToggle;

    public CorkboardView()
    {
        InitializeComponent();
    }

    public static bool CanDragFromCorkboard(CorkboardItemViewModel? item)
    {
        return item is ChapterCardItemViewModel chapter
            && chapter.IsVisibleOnBoard
            && !chapter.IsMissing
            && !string.IsNullOrWhiteSpace(chapter.RelativePath);
    }

    public static bool CanDropOnCorkboardCard(CorkboardItemViewModel? source, ChapterCardItemViewModel? target)
    {
        return source is ChapterCardItemViewModel sourceCard
            && target is not null
            && CanDragFromCorkboard(sourceCard)
            && CanDragFromCorkboard(target)
            && !string.Equals(sourceCard.RelativePath, target.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanDropIntoCorkboardPart(CorkboardItemViewModel? source, PartDividerItemViewModel? targetPart)
    {
        return source is ChapterCardItemViewModel sourceCard
            && targetPart is not null
            && CanDragFromCorkboard(sourceCard)
            && !string.IsNullOrWhiteSpace(targetPart.RelativePath);
    }

    public static bool CanDropIntoCorkboardEmptyPart(CorkboardItemViewModel? source, EmptyPartHintItemViewModel? emptyPart)
    {
        return source is ChapterCardItemViewModel sourceCard
            && emptyPart is not null
            && emptyPart.IsVisibleOnBoard
            && CanDragFromCorkboard(sourceCard)
            && !string.IsNullOrWhiteSpace(emptyPart.RelativePath);
    }

    public static CorkboardDropRequest? CreateCardDropRequest(
        ChapterCardItemViewModel? source,
        ChapterCardItemViewModel? target,
        bool dropBefore)
    {
        if (!CanDropOnCorkboardCard(source, target))
            return null;

        return dropBefore
            ? new CorkboardDropRequest(
                source!.RelativePath,
                TargetPartPath: NormalizePartPath(target!.OwningPartPath),
                BeforeRelativePath: target.RelativePath)
            : new CorkboardDropRequest(
                source!.RelativePath,
                TargetPartPath: NormalizePartPath(target!.OwningPartPath),
                AfterRelativePath: target.RelativePath);
    }

    public static CorkboardDropRequest? CreatePartDropRequest(
        ChapterCardItemViewModel? source,
        PartDividerItemViewModel? targetPart)
    {
        if (!CanDropIntoCorkboardPart(source, targetPart))
            return null;

        return new CorkboardDropRequest(source!.RelativePath, TargetPartPath: targetPart!.RelativePath);
    }

    public static CorkboardDropRequest? CreateEmptyPartDropRequest(
        ChapterCardItemViewModel? source,
        EmptyPartHintItemViewModel? emptyPart)
    {
        if (!CanDropIntoCorkboardEmptyPart(source, emptyPart))
            return null;

        return new CorkboardDropRequest(source!.RelativePath, TargetPartPath: emptyPart!.RelativePath);
    }

    private static string? NormalizePartPath(string? partPath)
        => string.IsNullOrWhiteSpace(partPath) ? null : partPath;

    private async void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is not CorkboardViewModel vm || vm.SelectedCard is null)
            return;

        e.Handled = true;
        await ExecuteCommandAsync(vm.OpenSelectedCardCommand.Execute());
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChapterCardItemViewModel card)
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

        // A press on one of the card's own buttons (Beats, Open) is a click, not a drag.
        if (e.Source is Visual source
            && source.FindAncestorOfType<Button>(includeSelf: true) is { } pressed
            && !ReferenceEquals(pressed, control))
            return;

        if (!CanDragFromCorkboard(card))
            return;

        _dragSource = card;
        _dragPressArgs = e;
        _dragStart = e.GetPosition(this);
        _dragOperationStarted = false;
        e.Pointer.Capture(control);
    }

    private async void Card_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null || _dragOperationStarted)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < 4 && Math.Abs(point.Y - _dragStart.Y) < 4)
            return;

        _dragOperationStarted = true;
        _dragSourcePath = _dragSource.RelativePath;
        e.Pointer.Capture(null);

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(_dragSource.RelativePath));

            if (_dragPressArgs is not null)
                await DragDrop.DoDragDropAsync(_dragPressArgs, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicator();
            ClearDragState();
        }
    }

    private void Card_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (DragDrop.GetAllowDrop(button))
            return;

        DragDrop.SetAllowDrop(button, true);
        DragDrop.AddDragOverHandler(button, Card_DragOver);
        DragDrop.AddDragLeaveHandler(button, Card_DragLeave);
        DragDrop.AddDropHandler(button, Card_Drop);

        button.AddHandler(PointerPressedEvent, Card_PointerPressed,
            RoutingStrategies.Direct | RoutingStrategies.Bubble, true);
        button.AddHandler(PointerMovedEvent, Card_PointerMoved,
            RoutingStrategies.Direct | RoutingStrategies.Bubble, true);
        button.AddHandler(PointerReleasedEvent, Card_PointerReleased,
            RoutingStrategies.Direct | RoutingStrategies.Bubble, true);
    }

    private void PartHeader_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
            return;

        if (DragDrop.GetAllowDrop(control))
            return;

        DragDrop.SetAllowDrop(control, true);
        DragDrop.AddDragOverHandler(control, PartHeader_DragOver);
        DragDrop.AddDragLeaveHandler(control, PartHeader_DragLeave);
        DragDrop.AddDropHandler(control, PartHeader_Drop);
    }

    private void EmptyPartHint_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
            return;

        if (DragDrop.GetAllowDrop(control))
            return;

        DragDrop.SetAllowDrop(control, true);
        DragDrop.AddDragOverHandler(control, EmptyPartHint_DragOver);
        DragDrop.AddDragLeaveHandler(control, EmptyPartHint_DragLeave);
        DragDrop.AddDropHandler(control, EmptyPartHint_Drop);
    }

    private async void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChapterCardItemViewModel card)
        {
            if (!_dragOperationStarted)
                ClearDragState();
            return;
        }

        e.Pointer.Capture(null);

        if (_dragOperationStarted)
            return;

        if (DataContext is CorkboardViewModel vm)
        {
            var point = e.GetCurrentPoint(control);
            if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased
                && ReferenceEquals(_dragSource, card))
            {
                await ExecuteCommandAsync(vm.SelectCardCommand.Execute(card));
            }
        }

        ClearDragState();
    }

    private async void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ChapterCardItemViewModel card }
            || DataContext is not CorkboardViewModel vm)
            return;

        e.Handled = true;
        await ExecuteCommandAsync(vm.OpenCardCommand.Execute(card));
    }

    /// <summary>
    /// Toggles the Part. In an anthology a double-click opens the story's beats instead, so the toggle waits
    /// out the double-click time and a second click cancels it (no collapse-and-expand flicker).
    /// </summary>
    private async void PartHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PartDividerItemViewModel part }
            || DataContext is not CorkboardViewModel vm)
            return;

        if (!vm.IsAnthology)
        {
            await ExecuteCommandAsync(vm.TogglePartExpandedCommand.Execute(part));
            return;
        }

        if (_pendingPartToggle is { } pending)
        {
            pending.Stop();
            _pendingPartToggle = null;
            if (ReferenceEquals(pending.Tag, part))
                return;
        }

        var interval = Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse)
            ?? TimeSpan.FromMilliseconds(500);
        var timer = new DispatcherTimer { Interval = interval, Tag = part };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_pendingPartToggle, timer))
                _pendingPartToggle = null;
            await ExecuteCommandAsync(vm.TogglePartExpandedCommand.Execute(part));
        };
        _pendingPartToggle = timer;
        timer.Start();
    }

    private async void PartHeader_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: PartDividerItemViewModel part }
            || DataContext is not CorkboardViewModel vm
            || !vm.IsAnthology)
            return;

        e.Handled = true;
        _pendingPartToggle?.Stop();
        _pendingPartToggle = null;
        await ExecuteCommandAsync(vm.OpenCardCommand.Execute(part));
    }

    private void Card_DragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChapterCardItemViewModel target)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (_dragSource is null || string.IsNullOrWhiteSpace(_dragSourcePath) || !CanDropOnCorkboardCard(_dragSource, target))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        var panel = button.Parent as Panel;
        if (panel == null)
            return;

        ClearDropIndicator();

        var dropBefore = ShouldDropBefore(target.RelativePath);
        SetInsertionLine(panel, dropBefore);
        _lastDropIndicator = panel;
    }

    private void Card_DragLeave(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
    }

    private void PartHeader_DragOver(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (sender is not Control control || control.DataContext is not PartDividerItemViewModel targetPart)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (!CanDropIntoCorkboardPart(_dragSource, targetPart))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void PartHeader_DragLeave(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
    }

    private async void PartHeader_Drop(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (DataContext is not CorkboardViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not PartDividerItemViewModel targetPart)
            return;

        var request = CreatePartDropRequest(_dragSource, targetPart);
        if (request is null)
            return;

        await ExecuteCommandAsync(vm.DropCardCommand.Execute(request));
    }

    private void EmptyPartHint_DragOver(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (sender is not Control control || control.DataContext is not EmptyPartHintItemViewModel emptyPart)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (!CanDropIntoCorkboardEmptyPart(_dragSource, emptyPart))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void EmptyPartHint_DragLeave(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
    }

    private async void EmptyPartHint_Drop(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (DataContext is not CorkboardViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not EmptyPartHintItemViewModel emptyPart)
            return;

        var request = CreateEmptyPartDropRequest(_dragSource, emptyPart);
        if (request is null)
            return;

        await ExecuteCommandAsync(vm.DropCardCommand.Execute(request));
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

    private bool ShouldDropBefore(string targetRelativePath)
    {
        if (DataContext is not CorkboardViewModel vm || string.IsNullOrWhiteSpace(_dragSourcePath))
            return true;

        var chapterCards = vm.Items
            .OfType<ChapterCardItemViewModel>()
            .Select(item => item.RelativePath)
            .ToList();

        var sourceIndex = chapterCards.FindIndex(p => string.Equals(p, _dragSourcePath, StringComparison.OrdinalIgnoreCase));
        var targetIndex = chapterCards.FindIndex(p => string.Equals(p, targetRelativePath, StringComparison.OrdinalIgnoreCase));

        return sourceIndex > targetIndex;
    }

    private async void Card_Drop(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();

        if (DataContext is not CorkboardViewModel vm)
            return;

        if (sender is not Control control || control.DataContext is not ChapterCardItemViewModel target)
            return;

        var request = CreateCardDropRequest(_dragSource, target, ShouldDropBefore(target.RelativePath));
        if (request is null)
            return;

        await ExecuteCommandAsync(vm.DropCardCommand.Execute(request));
    }

    private async void RenameCard_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetCardFromMenu(sender, out var card) || DataContext is not CorkboardViewModel vm)
            return;

        var initial = card.RelativePath;
        var replacement = await PromptForTextAsync("Rename chapter", "Enter the new Book.txt path:", initial);
        if (string.IsNullOrWhiteSpace(replacement) || string.Equals(replacement, initial, StringComparison.OrdinalIgnoreCase))
            return;

        await ExecuteCommandAsync(vm.RenameCardCommand.Execute(new RenameCardRequest(initial, replacement)));
    }

    private void NewChapter_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetCardFromMenu(sender, out var card) || DataContext is not CorkboardViewModel vm)
            return;

        var placement = CreateInlineCreatePlacementAfterChapter(vm, card);
        vm.BeginInlineCreate(placement.InsertIndex, placement.Part);
    }

    private async void IncludeExistingChapter_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetCardFromMenu(sender, out var card) || DataContext is not CorkboardViewModel vm)
            return;

        var path = await PromptForTextAsync("Include existing chapter", "Enter the existing chapter path:", string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            return;

        var request = CreateIncludeExistingChapterRequestAfterCard(vm, card, path);
        await ExecuteCommandAsync(vm.IncludeExistingChapterCommand.Execute(request));
    }

    private async void RemoveFromBook_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetCardFromMenu(sender, out var card) || DataContext is not CorkboardViewModel vm)
            return;

        await ExecuteCommandAsync(vm.RemoveFromBookCommand.Execute(new RemoveChapterRequest(card.RelativePath)));
    }

    // ── Inline chapter creation handlers ────────────────────────────────────

    private static void InlineCreate_Loaded(object? sender, RoutedEventArgs e)
    {
        (sender as TextBox)?.Focus();
    }

    private async void InlineCreate_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || DataContext is not CorkboardViewModel vm)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.CommitInlineCreateAsync(tb.Text ?? string.Empty);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelInlineCreate();
        }
    }

    private async void BoardAddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CorkboardViewModel vm)
            return;

        var unit = vm.Profile.UnitNoun;
        var flyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = $"New {unit}", Tag = "chapter" },
                new MenuItem { Header = $"New {unit} (advanced)…", Tag = "chapter-advanced" },
                new MenuItem { Header = $"New {vm.Profile.PartNoun}…", Tag = "part" },
                new MenuItem { Header = "Include Existing File…", Tag = "include" }
            }
        };

        foreach (var item in flyout.Items.OfType<MenuItem>())
            item.Click += BoardAddMenuItem_Click;

        flyout.Closed += (_, _) =>
        {
            foreach (var item in flyout.Items.OfType<MenuItem>())
                item.Click -= BoardAddMenuItem_Click;
        };

        if (sender is Control control)
            flyout.ShowAt(control);
    }

    private async void BoardAddMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CorkboardViewModel vm)
            return;

        var action = sender is MenuItem { Tag: string tag } ? tag : null;
        switch (action)
        {
            case "chapter":
                {
                    var placement = CreateInlineCreatePlacementAtBookLevel(vm);
                    vm.BeginInlineCreate(placement.InsertIndex, placement.Part);
                    break;
                }
            case "chapter-advanced":
                await CreateBoardChapterAsync(vm, part: null);
                break;
            case "part":
                await CreateBoardPartAsync(vm);
                break;
            case "include":
                await IncludeBoardFileAsync(vm, part: null);
                break;
        }
    }

    private async void PartAddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PartDividerItemViewModel part }
            || DataContext is not CorkboardViewModel vm)
            return;

        var unit = vm.Profile.UnitNoun;
        var partNoun = vm.Profile.PartNoun;
        var flyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = $"New {unit} in {partNoun}", Tag = ("chapter", part) },
                new MenuItem { Header = $"New {unit} in {partNoun} (advanced)…", Tag = ("chapter-advanced", part) },
                new MenuItem { Header = "Include Existing File…", Tag = ("include", part) }
            }
        };

        foreach (var item in flyout.Items.OfType<MenuItem>())
            item.Click += PartAddMenuItem_Click;

        flyout.Closed += (_, _) =>
        {
            foreach (var item in flyout.Items.OfType<MenuItem>())
                item.Click -= PartAddMenuItem_Click;
        };

        if (sender is Control control)
            flyout.ShowAt(control);
    }

    private async void PartAddMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CorkboardViewModel vm
            || sender is not MenuItem { Tag: ValueTuple<string, PartDividerItemViewModel> tag })
            return;

        var (action, part) = tag;
        switch (action)
        {
            case "chapter":
                {
                    var placement = CreateInlineCreatePlacementAfterPart(vm, part);
                    vm.BeginInlineCreate(placement.InsertIndex, placement.Part);
                    break;
                }
            case "chapter-advanced":
                await CreateBoardChapterAsync(vm, part);
                break;
            case "include":
                await IncludeBoardFileAsync(vm, part);
                break;
        }
    }

    private async Task CreateBoardChapterAsync(CorkboardViewModel vm, PartDividerItemViewModel? part)
    {
        var suggestedPath = part == null
            ? "new-chapter.md"
            : BuildSuggestedNewChapterPath(part.RelativePath);

        var dialogResult = await NewChapterDialog.ShowAsync(
            TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("No window owner."),
            NewManuscriptEntryKind.Chapter,
            $"New {vm.Profile.UnitNoun}",
            part == null
                ? $"Enter the new {vm.Profile.UnitNoun.ToLowerInvariant()} path relative to the manuscript root."
                : $"Enter the new {vm.Profile.UnitNoun.ToLowerInvariant()} path for {part.Title}.",
            suggestedPath);

        if (dialogResult is null)
            return;

        var title = dialogResult.Title
            ?? Path.GetFileNameWithoutExtension(dialogResult.FilePath).Replace('-', ' ').Replace('_', ' ');
        var content = $"# {title}\n\n";

        var index = part == null
            ? vm.GetBookChapterInsertIndex()
            : vm.GetInsertIndexAfterPart(part.RelativePath);

        await ExecuteCommandAsync(vm.CreateChapterCommand.Execute(
            new CreateChapterRequest(dialogResult.FilePath, content, index)));
    }

    private async Task CreateBoardPartAsync(CorkboardViewModel vm)
    {
        var dialogResult = await NewChapterDialog.ShowAsync(
            TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("No window owner."),
            NewManuscriptEntryKind.Part,
            $"New {vm.Profile.PartNoun}",
            $"Enter the {vm.Profile.PartNoun.ToLowerInvariant()} divider path and title.",
            "part-two/part.md");

        if (dialogResult is null || string.IsNullOrWhiteSpace(dialogResult.Title))
            return;

        await ExecuteCommandAsync(vm.CreatePartCommand.Execute(
            new CreatePartRequest(dialogResult.FilePath, dialogResult.Title, vm.GetBookInsertIndex())));
    }

    private async Task IncludeBoardFileAsync(CorkboardViewModel vm, PartDividerItemViewModel? part)
    {
        var absolutePath = await vm.PickManuscriptFileAsync();
        if (string.IsNullOrWhiteSpace(absolutePath))
            return;

        var relativePath = vm.ToManuscriptRelativePath(absolutePath);
        if (part != null)
        {
            await ExecuteCommandAsync(vm.IncludeExistingChapterCommand.Execute(
                new IncludeExistingChapterRequest(relativePath, PartPath: part.RelativePath)));
        }
        else
        {
            await ExecuteCommandAsync(vm.IncludeExistingChapterCommand.Execute(
                new IncludeExistingChapterRequest(relativePath, vm.GetBookChapterInsertIndex())));
        }
    }

    private async void IncludeExcludedCard_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetExcludedCardFromMenu(sender, out var excluded) || DataContext is not CorkboardViewModel vm)
            return;

        var request = CreateIncludeExcludedCardRequest(vm, excluded);
        await ExecuteCommandAsync(vm.IncludeExistingChapterCommand.Execute(request));
    }

    private static bool TryGetExcludedCardFromMenu(object? sender, out ExcludedChapterCardItemViewModel excluded)
    {
        if (sender is MenuItem { DataContext: ExcludedChapterCardItemViewModel menuCard })
        {
            excluded = menuCard;
            return true;
        }

        excluded = null!;
        return false;
    }

    public static InlineCreatePlacement CreateInlineCreatePlacementAfterChapter(
        CorkboardViewModel vm,
        ChapterCardItemViewModel card)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(card);

        PartDividerItemViewModel? owningPart = null;
        if (!string.IsNullOrWhiteSpace(card.OwningPartPath))
        {
            owningPart = vm.Items
                .OfType<PartDividerItemViewModel>()
                .FirstOrDefault(p => string.Equals(p.RelativePath, card.OwningPartPath, StringComparison.OrdinalIgnoreCase));
        }

        return new InlineCreatePlacement(vm.GetInsertIndexAfterChapter(card.RelativePath), owningPart);
    }

    public static InlineCreatePlacement CreateInlineCreatePlacementAfterPart(
        CorkboardViewModel vm,
        PartDividerItemViewModel part)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(part);

        return new InlineCreatePlacement(vm.GetInsertIndexAfterPart(part.RelativePath), part);
    }

    public static InlineCreatePlacement CreateInlineCreatePlacementAtBookLevel(CorkboardViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return new InlineCreatePlacement(vm.GetBookChapterInsertIndex(), Part: null);
    }

    public static IncludeExistingChapterRequest CreateIncludeExistingChapterRequestAfterCard(
        CorkboardViewModel vm,
        ChapterCardItemViewModel card,
        string chapterPath)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterPath);

        return new IncludeExistingChapterRequest(chapterPath, vm.GetInsertIndexAfterChapter(card.RelativePath));
    }

    public static IncludeExistingChapterRequest CreateIncludeExcludedCardRequest(
        CorkboardViewModel vm,
        ExcludedChapterCardItemViewModel excluded)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(excluded);

        return !string.IsNullOrWhiteSpace(excluded.OwningPartPath)
            ? new IncludeExistingChapterRequest(excluded.RelativePath, PartPath: excluded.OwningPartPath)
            : new IncludeExistingChapterRequest(excluded.RelativePath, vm.GetBookChapterInsertIndex());
    }

    public sealed record InlineCreatePlacement(int InsertIndex, PartDividerItemViewModel? Part);

    private async void DeleteChapter_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetCardFromMenu(sender, out var card) || DataContext is not CorkboardViewModel vm)
            return;

        var confirmed = await ConfirmAsync(
            "Delete chapter file",
            $"Delete the chapter file for '{card.RelativePath}'? This cannot be undone.");

        if (!confirmed)
            return;

        await ExecuteCommandAsync(vm.DeleteChapterCommand.Execute(new DeleteChapterRequest(card.RelativePath, Confirmed: true)));
    }

    private static bool TryGetCardFromMenu(object? sender, out ChapterCardItemViewModel card)
    {
        if (sender is MenuItem { DataContext: ChapterCardItemViewModel menuCard })
        {
            card = menuCard;
            return true;
        }

        card = null!;
        return false;
    }

    private void ClearDragState()
    {
        _dragSource = null;
        _dragSourcePath = null;
        _dragPressArgs = null;
        _dragOperationStarted = false;
    }

    private static string BuildSuggestedNewChapterPath(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        return string.IsNullOrWhiteSpace(directory)
            ? "new-chapter.md"
            : $"{directory}/new-chapter.md";
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
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#120932"))
        };

        var input = new TextBox
        {
            Text = initialValue,
            FontSize = 12,
            Padding = new Thickness(8, 6),
            Background = GetResource<Avalonia.Media.IBrush>("SurfaceBaseBrush")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0F0828")),
            Foreground = GetResource<Avalonia.Media.IBrush>("OnSurfaceBrush")
                ?? Avalonia.Media.Brushes.White,
            BorderBrush = GetResource<Avalonia.Media.IBrush>("BorderSubtleBrush")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D1B5E")),
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
            Background = GetResource<Avalonia.Media.IBrush>("SynthwavePurpleBrush")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9D4EDD")),
            Foreground = GetResource<Avalonia.Media.IBrush>("OnSurfaceBrush")
                ?? Avalonia.Media.Brushes.White,
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
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D1B5E")),
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
                        Foreground = GetResource<Avalonia.Media.IBrush>("OnSurfaceBrush")
                            ?? Avalonia.Media.Brushes.White,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel }
                    }
                }
            }
        };

        dialog.Closed += (_, _) =>
        {
            if (!result.Task.IsCompleted)
                result.TrySetResult(submitted);
        };

        _ = dialog.ShowDialog(owner);
        return await result.Task;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return false;

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#120932"))
        };

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = false;

        void CloseWithResult(bool value)
        {
            confirmed = value;
            if (!result.Task.IsCompleted)
                result.TrySetResult(value);

            if (dialog.IsVisible)
                dialog.Close();
        }

        var ok = new Button
        {
            Content = "Delete",
            IsDefault = true,
            Background = GetResource<Avalonia.Media.IBrush>("OrangeBrush")
                ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F97316")),
            Foreground = GetResource<Avalonia.Media.IBrush>("OnSurfaceBrush")
                ?? Avalonia.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6)
        };
        ok.Click += (_, _) => CloseWithResult(true);

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(12, 6)
        };
        cancel.Click += (_, _) => CloseWithResult(false);

        dialog.Content = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D1B5E")),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        Foreground = GetResource<Avalonia.Media.IBrush>("OnSurfaceBrush")
                            ?? Avalonia.Media.Brushes.White,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel }
                    }
                }
            }
        };

        dialog.Closed += (_, _) =>
        {
            if (!result.Task.IsCompleted)
                result.TrySetResult(confirmed);
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

    private T? GetResource<T>(string key) where T : class
    {
        return this.TryFindResource(key, out var value) && value is T typed
            ? typed
            : null;
    }
}

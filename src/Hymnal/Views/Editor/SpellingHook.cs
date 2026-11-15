using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Hymnal.ViewModels;
using ReactiveUI;

namespace Hymnal.Views.Editor;

/// <summary>
/// Connects a <see cref="TextEditor"/> to its <see cref="EditorSpellingViewModel"/>: draws the marks of each
/// scan that still matches the buffer, offers fixes when a marked word is right-clicked, and moves to the next or
/// previous mark (selecting the word and opening the same menu) for F8 / Shift+F8.
/// <para>
/// The word being typed is never marked: a mark ending at the caret right where the last edit happened is
/// held back until the caret moves on.
/// </para>
/// </summary>
internal sealed class SpellingHook : IDisposable
{
    private readonly TextEditor _editor;
    private readonly EditorSpellingViewModel _spelling;
    private readonly Func<string> _bufferText;
    private readonly SpellingRenderer _renderer;
    private readonly CompositeDisposable _subscriptions = new();
    private TextDocument? _document;
    private int _lastEditEnd = -1;
    private bool _marksVisible = true;
    private ContextMenu? _menu;

    public SpellingHook(TextEditor editor, EditorSpellingViewModel spelling, Func<string> bufferText, IBrush markBrush)
    {
        _editor = editor;
        _spelling = spelling;
        _bufferText = bufferText;
        _renderer = new SpellingRenderer(markBrush) { Suppress = IsBeingTyped };

        TextView.BackgroundRenderers.Add(_renderer);
        AttachDocument(editor.Document);
        editor.DocumentChanged += OnDocumentChanged;
        editor.TextArea.Caret.PositionChanged += OnCaretMoved;
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _subscriptions.Add(spelling.WhenAnyValue(s => s.Scan).Subscribe(Apply));
        _subscriptions.Add(spelling.NavigationRequests.Subscribe(Navigate));
    }

    private TextView TextView => _editor.TextArea.TextView;

    /// <summary>The number of marks currently held (tests).</summary>
    internal int MarkCount => _renderer.Count;

    /// <summary>Hides or shows the marks (Focus Mode) without forgetting them.</summary>
    public bool MarksVisible
    {
        get => _marksVisible;
        set
        {
            if (_marksVisible == value)
                return;
            _marksVisible = value;
            _renderer.IsVisible = value;
            Redraw();
        }
    }

    /// <summary>Changes the underline colour (a theme change).</summary>
    public void SetMarkBrush(IBrush brush)
    {
        _renderer.Pen = new Pen(brush, _renderer.Pen.Thickness);
        Redraw();
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _menu?.Close();
        _editor.DocumentChanged -= OnDocumentChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretMoved;
        _editor.TextArea.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        AttachDocument(null);
        _renderer.Clear();
        try { TextView.BackgroundRenderers.Remove(_renderer); } catch { /* the view may be gone */ }
    }

    // ── Scans ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a scan's marks if it was made from the text now in the buffer; a scan of older text is dropped (a
    /// newer one is already on its way, since the text changed).
    /// </summary>
    internal void Apply(SpellingScan scan)
    {
        if (_document is null)
            return;

        if (scan.Issues.Count == 0)
        {
            _renderer.Clear();
        }
        else
        {
            var text = _bufferText();
            if (!ReferenceEquals(text, scan.Text) && !string.Equals(text, scan.Text, StringComparison.Ordinal))
                return;
            if (_document.TextLength != scan.Text.Length)
                return;
            _renderer.SetIssues(_document, scan.Issues);
        }

        Redraw();
    }

    private bool IsBeingTyped(SpellingMark mark)
    {
        var caret = _editor.TextArea.Caret.Offset;
        return caret == mark.EndOffset && _lastEditEnd == mark.EndOffset;
    }

    // ── Fixes ────────────────────────────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TextView).Properties.IsRightButtonPressed || !_marksVisible)
            return;

        var position = TextView.GetPositionFloor(e.GetPosition(TextView) + TextView.ScrollOffset);
        if (position is not { } pos || _document is null)
            return;

        var mark = _renderer.FindAt(_document.GetOffset(pos.Location));
        if (mark is null)
            return;

        e.Handled = true;
        _editor.TextArea.Caret.Offset = mark.StartOffset;
        OpenMenu(mark, select: false);
    }

    private void Navigate(int direction)
    {
        if (_document is null)
            return;

        var caret = _editor.TextArea.Caret.Offset;
        // Forward: a selected mark (the one F8 just went to) is skipped; from a bare caret, a mark starting right
        // there counts. Back: the last mark ending before the selection or caret.
        var hasSelection = _editor.SelectionLength > 0;
        var from = direction >= 0
            ? (hasSelection ? _editor.SelectionStart : caret - 1)
            : (hasSelection ? _editor.SelectionStart : caret);
        var mark = _renderer.FindNext(from, direction);
        if (mark is null)
            return;

        _marksVisible = true;
        _renderer.IsVisible = true;
        _editor.Select(mark.StartOffset, mark.Length);
        var location = _document.GetLocation(mark.StartOffset);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.TextArea.Focus();
        Redraw();
        OpenMenu(mark, select: true);
    }

    private void OpenMenu(SpellingMark mark, bool select)
    {
        _menu?.Close();
        var items = BuildMenuItems(mark);
        if (items.Count == 0)
            return;

        var menu = new ContextMenu { ItemsSource = items };
        if (WordRect(mark) is { } rect)
        {
            menu.Placement = PlacementMode.AnchorAndGravity;
            menu.PlacementAnchor = PopupAnchor.BottomLeft;
            menu.PlacementGravity = PopupGravity.BottomRight;
            menu.PlacementRect = rect;
        }

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
                _menu = null;
            _editor.TextArea.Focus();
        };
        _menu = menu;
        menu.Open(TextView);
    }

    private List<Control> BuildMenuItems(SpellingMark mark)
    {
        var items = new List<Control>();
        var word = mark.Word;

        var suggestions = _spelling.Suggest(word);
        foreach (var suggestion in suggestions)
        {
            var replacement = suggestion;
            var item = new MenuItem { Header = replacement, FontWeight = FontWeight.SemiBold };
            item.Click += (_, _) => Replace(mark, replacement);
            items.Add(item);
        }

        if (suggestions.Count == 0)
            items.Add(new MenuItem { Header = "No suggestions", IsEnabled = false });

        items.Add(new Separator());

        var add = new MenuItem
        {
            Header = $"Add “{word}” to Project Dictionary",
            IsEnabled = _spelling.CanAddToDictionary,
        };
        ToolTip.SetTip(add, _spelling.CanAddToDictionary
            ? "Accept this word in this manuscript (kept in .hymnal-data/dictionary.txt)"
            : "Open a workspace to keep a project dictionary");
        add.Click += (_, _) => _spelling.AddToDictionaryCommand.Execute(word).Subscribe(_ => { }, _ => { });
        items.Add(add);

        var ignore = new MenuItem { Header = $"Ignore “{word}”" };
        ToolTip.SetTip(ignore, "Accept this word until Hymnal closes");
        ignore.Click += (_, _) => _spelling.IgnoreCommand.Execute(word).Subscribe(_ => { }, _ => { });
        items.Add(ignore);

        return items;
    }

    private void Replace(SpellingMark mark, string replacement)
    {
        if (_document is null || !_renderer.IsLive(mark))
            return;
        var start = mark.StartOffset;
        _document.Replace(start, mark.Length, replacement);
        _editor.Select(start + replacement.Length, 0);
        _editor.TextArea.Caret.Offset = start + replacement.Length;
    }

    /// <summary>The word's box in text view coordinates, to hang the menu under; null when it isn't laid out.</summary>
    private Rect? WordRect(SpellingMark mark)
    {
        try
        {
            TextView.EnsureVisualLines();
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(TextView, mark))
                return rect;
        }
        catch
        {
            // Fall back to the pointer position.
        }

        return null;
    }

    // ── Document tracking ────────────────────────────────────────────────────

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        AttachDocument(_editor.Document);
        _renderer.Clear();
        Redraw();
    }

    private void AttachDocument(TextDocument? document)
    {
        if (_document is not null)
            _document.Changed -= OnTextChanged;
        _document = document;
        _lastEditEnd = -1;
        if (_document is not null)
            _document.Changed += OnTextChanged;
    }

    private void OnTextChanged(object? sender, DocumentChangeEventArgs e) =>
        _lastEditEnd = e.Offset + e.InsertionLength;

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        // A word held back while it was typed shows once the caret leaves it.
        if (_renderer.Count > 0)
            Redraw();
    }

    private void Redraw()
    {
        try { TextView.InvalidateLayer(KnownLayer.Selection); } catch { /* not attached */ }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace Hymnal.Views;

/// <summary>
/// The main window's Edit menu: Undo, Redo, Cut, Copy, Paste, Delete and Select All for whichever text the author
/// was last typing in — the manuscript editor (AvaloniaEdit) or any <see cref="TextBox"/> in the window (notes,
/// chapter info, ideas, beats, chat). Each control keeps its own undo history, so Undo in the notes never touches
/// the chapter.
/// </summary>
/// <remarks>
/// The menu only shows the shortcuts: the text controls already handle the keys themselves, so the window has no
/// key bindings for them. Opening the menu can move focus onto it, so the menu acts on the text control that had
/// focus before that, and puts focus back there before running a command.
/// </remarks>
public sealed class EditMenu : IDisposable
{
    private readonly TopLevel _window;
    private readonly MenuItem _root;
    private readonly List<EditCommand> _commands = new();

    private EditTarget? _target;

    // A TextBox clears its selection when it loses focus (ClearSelectionOnLostFocus), which opening the menu can
    // cause. So the target box's selection is recorded while it has focus, and put back for Cut, Copy and Delete.
    private TextBox? _watchedBox;
    private TextBoxSelection? _boxSelection;

    private EditMenu(MenuItem root, TopLevel window)
    {
        _root = root;
        _window = window;

        Undo = Add(t => t.CanUndo, t => t.Undo());
        Redo = Add(t => t.CanRedo, t => t.Redo());
        Cut = Add(t => t.CanCut, t => t.Cut());
        Copy = Add(t => t.CanCopy, t => t.Copy());
        Paste = Add(t => t.CanPaste, t => t.Paste());
        Delete = Add(t => t.CanDelete, t => t.Delete());
        SelectAll = Add(t => t.CanSelectAll, t => t.SelectAll());

        window.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        root.SubmenuOpened += OnSubmenuOpened;
    }

    public ICommand Undo { get; }
    public ICommand Redo { get; }
    public ICommand Cut { get; }
    public ICommand Copy { get; }
    public ICommand Paste { get; }
    public ICommand Delete { get; }
    public ICommand SelectAll { get; }

    /// <summary>The text control the commands act on, or null when the author last focused something else.</summary>
    internal Control? Target => _target?.Control;

    /// <summary>
    /// Fills <paramref name="root"/> with the Edit items and starts following focus in <paramref name="window"/>.
    /// Dispose the result when the window closes.
    /// </summary>
    public static EditMenu Attach(MenuItem root, TopLevel window)
    {
        var menu = new EditMenu(root, window);
        // The shortcuts shown are the platform's (Ctrl+Y redo on Windows, Cmd on macOS), which the controls obey.
        var keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;

        root.Items.Clear();
        root.Items.Add(Item("_Undo", menu.Undo, keys?.Undo, Key.Z));
        root.Items.Add(Item("_Redo", menu.Redo, keys?.Redo, Key.Y));
        root.Items.Add(new Separator());
        root.Items.Add(Item("Cu_t", menu.Cut, keys?.Cut, Key.X));
        root.Items.Add(Item("_Copy", menu.Copy, keys?.Copy, Key.C));
        root.Items.Add(Item("_Paste", menu.Paste, keys?.Paste, Key.V));
        root.Items.Add(new MenuItem { Header = "_Delete", Command = menu.Delete, InputGesture = new KeyGesture(Key.Delete) });
        root.Items.Add(new Separator());
        root.Items.Add(Item("Select _All", menu.SelectAll, keys?.SelectAll, Key.A));
        return menu;
    }

    private static MenuItem Item(string header, ICommand command, IReadOnlyList<KeyGesture>? gestures, Key ctrlKey) =>
        new()
        {
            Header = header,
            Command = command,
            InputGesture = gestures?.FirstOrDefault() ?? new KeyGesture(ctrlKey, KeyModifiers.Control),
        };

    /// <summary>Re-reads what the target can do, so the items grey out correctly.</summary>
    public void Refresh()
    {
        if (_target != null && !_target.IsUsable)
            SetTarget(null);
        RestoreBoxSelection();
        foreach (var command in _commands)
            command.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _window.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
        _root.SubmenuOpened -= OnSubmenuOpened;
        SetTarget(null);
    }

    private EditCommand Add(Func<EditTarget, bool> canExecute, Action<EditTarget> execute)
    {
        var command = new EditCommand(this, canExecute, execute);
        _commands.Add(command);
        return command;
    }

    private void OnSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, _root))
            Refresh();
    }

    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        var element = e.Source as Visual;
        var target = EditTarget.For(element);
        if (target != null)
        {
            if (_target?.Control != target.Control)
                SetTarget(target);
        }
        else if (!IsInMenu(element))
        {
            // Focus moved to a button, list or card: there is no text to edit until the author types somewhere.
            SetTarget(null);
        }
        // The items' enabled state is re-read when the menu opens (Refresh), so nothing to raise here.
    }

    private void SetTarget(EditTarget? target)
    {
        _target = target;
        var box = target?.Control as TextBox;
        if (box == _watchedBox)
            return;

        if (_watchedBox != null)
            _watchedBox.PropertyChanged -= OnWatchedBoxPropertyChanged;
        _watchedBox = box;
        _boxSelection = null;
        if (box != null)
        {
            box.PropertyChanged += OnWatchedBoxPropertyChanged;
            RecordBoxSelection(box);
        }
    }

    private void OnWatchedBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsFocused &&
            (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty
                                                          || e.Property == TextBox.TextProperty))
            RecordBoxSelection(box);
    }

    private void RecordBoxSelection(TextBox box) =>
        _boxSelection = new TextBoxSelection(box.SelectionStart, box.SelectionEnd, box.Text ?? string.Empty);

    /// <summary>
    /// Puts back a selection the target box dropped when focus left it, provided its text hasn't changed since.
    /// </summary>
    private void RestoreBoxSelection()
    {
        if (_watchedBox is not { } box || _boxSelection is not { } saved || saved.Start == saved.End)
            return;
        if (box.SelectionStart == box.SelectionEnd && (box.Text ?? string.Empty) == saved.Text)
        {
            box.SelectionStart = saved.Start;
            box.SelectionEnd = saved.End;
        }
    }

    /// <summary>True when <paramref name="element"/> is a menu or one of its items (where focus sits while a menu is open).</summary>
    internal static bool IsInMenu(Visual? element)
    {
        for (var v = element; v != null; v = v.GetVisualParent())
        {
            if (v is MenuItem or MenuBase)
                return true;
        }
        return false;
    }

    private void Run(Action<EditTarget> execute)
    {
        Refresh();
        if (_target is not { } target)
            return;
        target.Control.Focus();
        RestoreBoxSelection();
        execute(target);
        Refresh();
    }

    private bool CanRun(Func<EditTarget, bool> canExecute) => _target is { IsUsable: true } t && canExecute(t);

    private readonly record struct TextBoxSelection(int Start, int End, string Text);

    private sealed class EditCommand(EditMenu menu, Func<EditTarget, bool> canExecute, Action<EditTarget> execute)
        : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => menu.CanRun(canExecute);

        public void Execute(object? parameter) => menu.Run(execute);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One text control, seen through the operations the Edit menu needs.</summary>
    private abstract class EditTarget
    {
        public abstract Control Control { get; }

        public bool IsUsable => Control.IsAttachedToVisualTree() && Control.IsEffectivelyVisible
                                && Control.IsEffectivelyEnabled;

        public abstract bool CanUndo { get; }
        public abstract bool CanRedo { get; }
        public abstract bool CanCut { get; }
        public abstract bool CanCopy { get; }
        public abstract bool CanPaste { get; }
        public abstract bool CanDelete { get; }
        public abstract bool CanSelectAll { get; }

        public abstract void Undo();
        public abstract void Redo();
        public abstract void Cut();
        public abstract void Copy();
        public abstract void Paste();
        public abstract void Delete();
        public abstract void SelectAll();

        /// <summary>The text control <paramref name="focused"/> belongs to, or null if it isn't in one.</summary>
        public static EditTarget? For(Visual? focused)
        {
            for (var v = focused; v != null; v = v.GetVisualParent())
            {
                switch (v)
                {
                    case TextBox box:
                        return new TextBoxTarget(box);
                    case TextArea area:
                        return area.FindAncestorOfType<TextEditor>() is { } owner ? new TextEditorTarget(owner) : null;
                    case TextEditor editor:
                        return new TextEditorTarget(editor);
                }
            }
            return null;
        }
    }

    private sealed class TextBoxTarget(TextBox box) : EditTarget
    {
        public override Control Control => box;

        private bool HasSelection => box.SelectionStart != box.SelectionEnd;

        public override bool CanUndo => box.IsUndoEnabled && box.CanUndo;
        public override bool CanRedo => box.IsUndoEnabled && box.CanRedo;
        public override bool CanCut => box.CanCut;
        public override bool CanCopy => box.CanCopy;
        public override bool CanPaste => box.CanPaste;
        public override bool CanDelete => !box.IsReadOnly && HasSelection;
        public override bool CanSelectAll => !string.IsNullOrEmpty(box.Text);

        public override void Undo() => box.Undo();
        public override void Redo() => box.Redo();
        public override void Cut() => box.Cut();
        public override void Copy() => box.Copy();
        public override void Paste() => box.Paste();
        public override void Delete() => box.SelectedText = string.Empty;
        public override void SelectAll() => box.SelectAll();
    }

    private sealed class TextEditorTarget(TextEditor editor) : EditTarget
    {
        public override Control Control => editor.TextArea;

        public override bool CanUndo => editor.CanUndo;
        public override bool CanRedo => editor.CanRedo;
        public override bool CanCut => editor.CanCut;
        public override bool CanCopy => editor.CanCopy;
        public override bool CanPaste => editor.CanPaste;
        public override bool CanDelete => editor.CanDelete && editor.SelectionLength > 0;
        public override bool CanSelectAll => editor.CanSelectAll && editor.Document?.TextLength > 0;

        public override void Undo() => editor.Undo();
        public override void Redo() => editor.Redo();
        public override void Cut() => editor.Cut();
        public override void Copy() => editor.Copy();
        public override void Paste() => editor.Paste();
        public override void Delete() => editor.Delete();
        public override void SelectAll() => editor.SelectAll();
    }
}

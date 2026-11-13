using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using Hymnal.Core.Audio;

namespace Hymnal.Views.Editor;

/// <summary>
/// Raises a typing sound when the user types or deletes text in a <see cref="TextEditor"/>.
/// <para>
/// A sound needs both halves: an input event that could edit (typed text, Enter, Tab, Backspace, Delete) arms a
/// pending sound, and the document actually changing during that event plays it. So arrow keys, selection,
/// shortcuts such as undo or paste, programmatic loads and Backspace at the start of the document all stay silent.
/// </para>
/// </summary>
internal sealed class TypingSoundHook : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Action<TypingSoundKind, float> _play;
    private TypingSoundKind? _pending;
    private int _armId;

    public TypingSoundHook(TextEditor editor, Action<TypingSoundKind, float> play)
    {
        _editor = editor;
        _play = play;

        // Tunnel: arm before the text area handles the input and edits the document.
        _editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _editor.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        _editor.TextChanged += OnTextChanged;
    }

    /// <summary>
    /// The sound a key press may make by itself. Printable characters arrive as text input instead, and keys
    /// held with Ctrl, Alt or Meta are shortcuts, except word-deleting Ctrl+Backspace / Ctrl+Delete.
    /// </summary>
    internal static TypingSoundKind? ForKey(Key key, KeyModifiers modifiers)
    {
        var shortcut = (modifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        var control = (modifiers & KeyModifiers.Control) != 0;
        return key switch
        {
            Key.Back or Key.Delete when !shortcut => TypingSoundKind.Backspace,
            Key.Enter when !shortcut && !control => TypingSoundKind.Return,
            Key.Tab when !shortcut && !control => TypingSoundKind.Space,
            _ => null,
        };
    }

    public void Dispose()
    {
        _editor.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _editor.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        _editor.TextChanged -= OnTextChanged;
        _pending = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => Arm(ForKey(e.Key, e.KeyModifiers));

    private void OnTextInput(object? sender, TextInputEventArgs e) => Arm(TypingSoundClassifier.ForTypedText(e.Text));

    private void Arm(TypingSoundKind? kind)
    {
        _pending = kind;
        if (kind is null)
            return;

        // Disarm once this input event has been handled, wherever in the route the edit happens. The id keeps a
        // late disarm from cancelling the next keystroke's sound.
        var id = ++_armId;
        Dispatcher.UIThread.Post(() =>
        {
            if (_armId == id)
                _pending = null;
        }, DispatcherPriority.Input);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_pending is not { } kind)
            return;

        _pending = null;
        _play(kind, CaretPan());
    }

    /// <summary>Where the caret sits across the visible line, as a stereo position.</summary>
    private float CaretPan()
    {
        try
        {
            var textView = _editor.TextArea.TextView;
            var width = textView.Bounds.Width;
            if (width <= 0)
                return 0f;
            var caret = _editor.TextArea.Caret.CalculateCaretRectangle();
            return TypingSoundVoicer.CarriagePan((caret.X - textView.ScrollOffset.X) / width);
        }
        catch (Exception)
        {
            // Layout may not be ready (first keystroke after a load); the centre is fine.
            return 0f;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Hymnal.Core.Markua.Editing;
using ReactiveUI;

namespace Hymnal.ViewModels;

// The Insert menu: Markua snippets applied to the selection of whichever editor surface is on screen.
public partial class EditorViewModel
{
    private readonly List<IEditorTextSurface> _surfaces = new();

    private ObservableAsPropertyHelper<bool> _canInsertMarkua = null!;

    /// <summary>
    /// True when an Insert command can run: a chapter or doc (not Book.txt) is open, its source is showing (not
    /// Preview only), Write or Research is on screen, and nothing (Settings) covers it.
    /// </summary>
    public bool CanInsertMarkua => _canInsertMarkua.Value;

    private bool _isSourceFocused;
    /// <summary>True while keyboard focus is in an editor's source text (set by <c>EditorView</c>).</summary>
    public bool IsSourceFocused
    {
        get => _isSourceFocused;
        set => this.RaiseAndSetIfChanged(ref _isSourceFocused, value);
    }

    private bool _isSourceCovered;
    /// <summary>True while something covers the editor without hiding it (the Settings overlay).</summary>
    public bool IsSourceCovered
    {
        get => _isSourceCovered;
        set => this.RaiseAndSetIfChanged(ref _isSourceCovered, value);
    }

    private ObservableAsPropertyHelper<bool> _takesInsertShortcuts = null!;
    /// <summary>
    /// True while the editor has focus and can take an insert, so its shortcuts (Ctrl+B bold) win over the
    /// window's (Ctrl+B sidebar). Window key bindings run before the editor sees the key, so the window's
    /// commands for shared gestures can't execute while this is true.
    /// </summary>
    public bool TakesInsertShortcuts => _takesInsertShortcuts.Value;

    /// <summary>Applies a snippet from <see cref="MarkuaSnippets"/> to the visible editor's selection.</summary>
    public ReactiveCommand<MarkuaSnippet, Unit> InsertSnippetCommand { get; private set; } = null!;

    private void InitializeInsertCommands()
    {
        _canInsertMarkua = this.WhenAnyValue(x => x.IsPreviewAvailable, x => x.ShowSourcePane,
                x => x.IsEditorSurfaceVisible, x => x.IsSourceCovered,
                (previewable, source, surface, covered) => previewable && source && surface && !covered)
            .ToProperty(this, x => x.CanInsertMarkua);
        Disposables.Add(_canInsertMarkua);

        _takesInsertShortcuts = this.WhenAnyValue(x => x.IsSourceFocused, x => x.CanInsertMarkua,
                (focused, canInsert) => focused && canInsert)
            .ToProperty(this, x => x.TakesInsertShortcuts);
        Disposables.Add(_takesInsertShortcuts);

        InsertSnippetCommand = ReactiveCommand.Create<MarkuaSnippet>(snippet => InsertSnippet(snippet),
            this.WhenAnyValue(x => x.CanInsertMarkua));
    }

    /// <summary>
    /// Registers an editor view as a place Insert commands can apply to, until the returned handle is disposed.
    /// The most recently attached surface that is ready wins.
    /// </summary>
    public IDisposable AttachSurface(IEditorTextSurface surface)
    {
        _surfaces.Add(surface);
        return Disposable.Create(() => _surfaces.Remove(surface));
    }

    /// <summary>
    /// Applies <paramref name="snippet"/> to <paramref name="surface"/> (an editor's own shortcut), or to the
    /// visible editor. Returns false when nothing could take it.
    /// </summary>
    public bool InsertSnippet(MarkuaSnippet snippet, IEditorTextSurface? surface = null)
    {
        if (!CanInsertMarkua)
            return false;
        surface ??= _surfaces.LastOrDefault(s => s.IsReadyForEdits);
        if (surface is not { IsReadyForEdits: true })
            return false;

        surface.Apply(snippet.Apply(surface.Text, surface.Selection));
        return true;
    }
}

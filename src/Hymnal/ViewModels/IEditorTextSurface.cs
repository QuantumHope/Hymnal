using Hymnal.Core.Markua.Editing;

namespace Hymnal.ViewModels;

/// <summary>
/// An on-screen text editor bound to an <see cref="EditorViewModel"/>: what the Insert commands read (the text
/// and selection, which only the view holds) and where they apply their edit. <c>EditorView</c> implements it;
/// Write and Research each have one over the same view model.
/// </summary>
public interface IEditorTextSurface
{
    /// <summary>True while the editor's source pane is on screen and can take an edit.</summary>
    bool IsReadyForEdits { get; }

    string Text { get; }

    TextSpan Selection { get; }

    /// <summary>Applies <paramref name="edit"/> as one undo step, selects its result and focuses the editor.</summary>
    void Apply(TextEdit edit);
}

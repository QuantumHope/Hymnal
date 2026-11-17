using Hymnal.Core.Models;

namespace Hymnal.ViewModels;

/// <summary>
/// A change the author made to the editor buffer. <paramref name="Node"/> is the chapter it was made in (null for a
/// research or supplemental document); <paramref name="IsBookTxt"/> is true for an edit to Book.txt.
/// </summary>
public sealed record ProseEdit(ChapterNode? Node, bool IsBookTxt, string Text);

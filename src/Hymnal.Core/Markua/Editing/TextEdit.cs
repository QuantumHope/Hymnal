namespace Hymnal.Core.Markua.Editing;

/// <summary>A range of editor text: a selection, or a caret when <see cref="Length"/> is 0.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

    public static TextSpan Caret(int offset) => new(offset, 0);
}

/// <summary>
/// One replacement in the editor's text: <see cref="Length"/> characters at <see cref="Offset"/> become
/// <see cref="Text"/>, and then <see cref="Selection"/> (in the new text) is selected. One edit is one undo step.
/// </summary>
public sealed record TextEdit(int Offset, int Length, string Text, TextSpan Selection)
{
    /// <summary>The text after the edit (the editor applies it to its document instead; this is for tests).</summary>
    public string ApplyTo(string source) => source.Remove(Offset, Length).Insert(Offset, Text);
}

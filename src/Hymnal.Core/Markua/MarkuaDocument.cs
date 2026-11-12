namespace Hymnal.Core.Markua;

/// <summary>
/// A parsed Markua manuscript file, ready to be laid out by the preview. Produced by <see cref="MarkuaParser"/>.
/// The model is deliberately small: it holds what changes how the text looks in a Leanpub book, not every
/// detail of the Markua spec (attributes the preview has no use for are dropped).
/// </summary>
public sealed record MarkuaDocument(IReadOnlyList<MarkuaBlock> Blocks, IReadOnlyList<MarkuaFootnote> Footnotes)
{
    public static MarkuaDocument Empty { get; } = new([], []);
}

/// <summary>A footnote definition (<c>[^label]: text</c>), numbered in order of first reference.</summary>
public sealed record MarkuaFootnote(int Number, string Label, IReadOnlyList<MarkuaRun> Runs);

/// <summary>Text styles a run can carry; combined as flags (<c>***x***</c> is Bold | Italic).</summary>
[Flags]
public enum MarkuaTextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strikethrough = 8,
    Superscript = 16,
    Subscript = 32,
    Link = 64,
    FootnoteReference = 128,
}

/// <summary>
/// One run of inline text with a single style. A run whose <see cref="IsLineBreak"/> is set is a forced line
/// break (in Markua every single newline inside a paragraph is one) and has empty text.
/// </summary>
public sealed record MarkuaRun(string Text, MarkuaTextStyle Style = MarkuaTextStyle.None, string? Url = null)
{
    public bool IsLineBreak { get; init; }

    public static MarkuaRun LineBreak { get; } = new("") { IsLineBreak = true };

    public bool Has(MarkuaTextStyle style) => (Style & style) == style;
}

public enum MarkuaAlignment
{
    Default,
    Left,
    Center,
    Right,
}

/// <summary>Base type for block-level elements.</summary>
public abstract record MarkuaBlock;

/// <summary>
/// A heading. Level 1 is a chapter, 2 a section and so on. <see cref="IsPart"/> marks a level-1 heading
/// preceded by <c>{class: part}</c>, which Leanpub sets as a Part title page.
/// </summary>
public sealed record MarkuaHeading(int Level, IReadOnlyList<MarkuaRun> Runs, bool IsPart = false) : MarkuaBlock;

public sealed record MarkuaParagraph(IReadOnlyList<MarkuaRun> Runs, MarkuaAlignment Alignment = MarkuaAlignment.Default)
    : MarkuaBlock;

public sealed record MarkuaBlockQuote(IReadOnlyList<MarkuaBlock> Blocks) : MarkuaBlock;

/// <summary>The flavours of aside and blurb Markua defines; each Leanpub sets with its own box or icon.</summary>
public enum MarkuaCalloutKind
{
    Aside,
    Blurb,
    Center,
    Warning,
    Tip,
    Error,
    Information,
    Question,
    Discussion,
    Exercise,
}

/// <summary>An aside (<c>A&gt;</c>, <c>{aside}</c>) or a blurb (<c>B&gt;</c>, <c>W&gt;</c>, <c>{blurb}</c> …).</summary>
public sealed record MarkuaCallout(MarkuaCalloutKind Kind, IReadOnlyList<MarkuaBlock> Blocks) : MarkuaBlock;

public sealed record MarkuaCodeBlock(string Code, string? Language) : MarkuaBlock;

public sealed record MarkuaList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MarkuaBlock>> Items) : MarkuaBlock;

/// <summary>A scene break (<c>* * *</c>, <c>***</c>, <c>---</c>).</summary>
public sealed record MarkuaSceneBreak : MarkuaBlock;

/// <summary>An explicit <c>{pagebreak}</c>.</summary>
public sealed record MarkuaPageBreak : MarkuaBlock;

/// <summary>A block resource (<c>![caption](source)</c> on its own line): an image, or a video/audio placeholder.</summary>
public sealed record MarkuaResource(string Caption, string Source) : MarkuaBlock
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"];

    public bool IsImage =>
        ImageExtensions.Contains(Path.GetExtension(Source.Split('?', '#')[0]), StringComparer.OrdinalIgnoreCase);
}

public sealed record MarkuaTable(
    IReadOnlyList<IReadOnlyList<MarkuaRun>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkuaRun>>> Rows,
    IReadOnlyList<MarkuaAlignment> Alignments) : MarkuaBlock;

namespace Hymnal.Core.Models;

public sealed class WordCountTarget
{
    public int? MinWords { get; init; }
    public int? MaxWords { get; init; }
    public string? Preset { get; init; }
}

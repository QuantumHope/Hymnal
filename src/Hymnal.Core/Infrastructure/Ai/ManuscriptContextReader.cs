using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Infrastructure.Ai;

/// <summary>
/// Read-only access to manuscript content for context building.
/// Reuses BookTxtParser; never writes to any file. Chapter text is prose only (no metadata block).
/// </summary>
public sealed class ManuscriptContextReader : IManuscriptContextReader
{
    public async Task<string> ReadChapterTextAsync(
        string workspaceRoot, string chapterRelativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var absolutePath = Path.Combine(workspaceRoot, chapterRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolutePath))
            return string.Empty;
        var text = await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
        // Prose only: a story file's trailing metadata block is Hymnal data, not manuscript text.
        return StoryFileFormat.Split(text).Prose;
    }

    public async Task<IReadOnlyList<(string RelativePath, string Title, bool IsPart)>> ReadBookOrderAsync(
        string bookTxtPath, CancellationToken ct)
    {
        if (!File.Exists(bookTxtPath))
            return Array.Empty<(string, string, bool)>();

        var lines = await File.ReadAllLinesAsync(bookTxtPath, ct).ConfigureAwait(false);
        var folderPath = Path.GetDirectoryName(bookTxtPath)!;
        var nodes = await BookTxtParser.ParseAsync(folderPath, lines, ct).ConfigureAwait(false);

        return nodes
            .Select(n => (n.RelativePath, n.Title, n.Kind == NodeKind.Part))
            .ToList();
    }
}

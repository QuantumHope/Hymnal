using Hymnal.Core.Interfaces;

namespace Hymnal.Core.Infrastructure;

/// <summary>
/// File-backed notes service. Reads notes directly from disk and delegates writes
/// to <see cref="IMetadataStore"/> for atomic temp-file-then-rename safety.
/// </summary>
public sealed class NotesService : INotesService
{
    private readonly IMetadataStore _store;

    public NotesService(IMetadataStore store)
    {
        _store = store;
    }

    /// <inheritdoc/>
    public async Task<string> LoadAsync(string absoluteNotesPath)
    {
        if (!File.Exists(absoluteNotesPath))
            return string.Empty;

        return await File.ReadAllTextAsync(absoluteNotesPath).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string absoluteNotesPath, string content)
    {
        await _store.WriteTextAtomicAsync(absoluteNotesPath, content).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string absoluteNotesPath)
    {
        if (File.Exists(absoluteNotesPath))
            File.Delete(absoluteNotesPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public static string DeriveNotesPath(string workspaceRoot, string chapterRelativePath) =>
        INotesService.DeriveNotesPath(workspaceRoot, chapterRelativePath);
}

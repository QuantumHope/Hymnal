namespace Hymnal.Core.Interfaces;

/// <summary>
/// Loads and saves per-chapter freeform notes to the .hymnal-data/notes/ directory.
/// </summary>
public interface INotesService
{
    /// <summary>
    /// Loads note text from <paramref name="absoluteNotesPath"/>.
    /// Returns an empty string when the file does not yet exist.
    /// </summary>
    Task<string> LoadAsync(string absoluteNotesPath);

    /// <summary>
    /// Saves <paramref name="content"/> to <paramref name="absoluteNotesPath"/> atomically.
    /// </summary>
    Task SaveAsync(string absoluteNotesPath, string content);

    /// <summary>
    /// Deletes the notes file at <paramref name="absoluteNotesPath"/>; does nothing when it does not exist.
    /// Throws when it exists and cannot be deleted.
    /// </summary>
    Task DeleteAsync(string absoluteNotesPath);

    /// <summary>
    /// Derives the canonical notes file path for a given workspace and chapter.
    /// Result: <c>{workspaceRoot}/.hymnal-data/notes/{chapterRelativePath}</c>
    /// where path separators in <paramref name="chapterRelativePath"/> are replaced with underscores.
    /// </summary>
    static string DeriveNotesPath(string workspaceRoot, string chapterRelativePath)
    {
        var safe = chapterRelativePath
            .Replace('/', '_')
            .Replace('\\', '_');
        return Path.Combine(workspaceRoot, ".hymnal-data", "notes", safe);
    }
}

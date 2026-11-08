using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

public interface IBeatBoardService
{
    Task<Result<IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey);
    Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats);

    /// <summary>True when <paramref name="boardKey"/> is valid and its board file exists.</summary>
    bool BoardExists(string workspaceRoot, string boardKey);

    /// <summary>
    /// Moves board <paramref name="fromKey"/> to <paramref name="toKey"/> with one rename, which never
    /// replaces an existing file. Fails, changing nothing, when the source is missing or the destination
    /// already exists.
    /// </summary>
    Task<Result<Unit>> MoveBoardAsync(string workspaceRoot, string fromKey, string toKey);

    /// <summary>Deletes board <paramref name="boardKey"/>'s file; succeeds without doing anything when there is none.</summary>
    Task<Result<Unit>> DeleteBoardAsync(string workspaceRoot, string boardKey);
}

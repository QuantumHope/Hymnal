using Hymnal.Core.Common;
using Hymnal.Core.Models.Sprints;

namespace Hymnal.Core.Interfaces;

/// <summary>
/// Notes the author leaves for their next session, kept per workspace in <c>.hymnal-data/future-you.json</c>.
/// A malformed file is reported and never overwritten.
/// </summary>
public interface IFutureYouNotesService
{
    /// <summary>All notes, oldest first.</summary>
    Task<Result<IReadOnlyList<FutureYouNote>>> LoadAsync(string workspaceRoot);

    /// <summary>The newest note, or null when there is none (or the file can't be read).</summary>
    Task<FutureYouNote?> LatestAsync(string workspaceRoot);

    Task<Result<Unit>> AddAsync(string workspaceRoot, FutureYouNote note);
}

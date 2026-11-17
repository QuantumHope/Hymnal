using Hymnal.Core.Common;
using Hymnal.Core.Models.Sprints;

namespace Hymnal.Core.Interfaces;

/// <summary>
/// The global writer profile (<c>%AppData%/Hymnal/writer-profile.json</c>): every sprint in every workspace. A
/// malformed file is reported and never overwritten.
/// </summary>
public interface IWriterProfileStore
{
    Task<Result<IReadOnlyList<SprintSessionRecord>>> LoadSessionsAsync();

    /// <summary>Appends a session. Fails, leaving the file untouched, when the existing file can't be read.</summary>
    Task<Result<Unit>> AppendSessionAsync(SprintSessionRecord session);
}

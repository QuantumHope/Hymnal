using Hymnal.Core.Common;
using Hymnal.Core.Models.Runs;

namespace Hymnal.Core.Interfaces;

/// <summary>
/// The global writer profile (<c>%AppData%/Hymnal/writer-profile.json</c>): every run in every workspace. A
/// malformed file is reported and never overwritten.
/// </summary>
public interface IWriterProfileStore
{
    Task<Result<IReadOnlyList<RunRecord>>> LoadRunsAsync();

    /// <summary>Appends a session. Fails, leaving the file untouched, when the existing file can't be read.</summary>
    Task<Result<Unit>> AppendRunAsync(RunRecord session);
}

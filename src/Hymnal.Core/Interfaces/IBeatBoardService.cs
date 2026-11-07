using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

public interface IBeatBoardService
{
    Task<Result<IReadOnlyList<Beat>>> LoadAsync(string workspaceRoot, string boardKey);
    Task<Result<Unit>> SaveAsync(string workspaceRoot, string boardKey, IReadOnlyList<Beat> beats);
}

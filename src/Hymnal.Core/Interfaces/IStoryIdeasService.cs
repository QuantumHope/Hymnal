using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

public interface IStoryIdeasService
{
    Task<Result<IReadOnlyList<StoryIdea>>> LoadAsync(string workspaceRoot);
    Task<Result<Unit>> SaveAsync(string workspaceRoot, IReadOnlyList<StoryIdea> ideas);
}

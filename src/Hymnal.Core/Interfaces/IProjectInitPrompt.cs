using Hymnal.Core.Models;

namespace Hymnal.Core.Interfaces;

/// <summary>
/// Asks the user whether to initialize a folder that has no Book.txt, and which project kind to create.
/// </summary>
public interface IProjectInitPrompt
{
    /// <summary>Returns true when the user agrees to initialize <paramref name="folderPath"/> as a project.</summary>
    Task<bool> ConfirmInitializeAsync(string folderPath);

    /// <summary>Returns the chosen project kind, or null when the user cancels.</summary>
    Task<ProjectKind?> ChooseProjectKindAsync();
}

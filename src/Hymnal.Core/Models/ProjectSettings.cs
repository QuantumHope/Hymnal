namespace Hymnal.Core.Models;

public sealed record ProjectSettings(
    ProjectKind Kind = ProjectKind.Novel,
    StorageMode Storage = StorageMode.Sidecar)
{
    public static ProjectSettings Default { get; } = new();
}

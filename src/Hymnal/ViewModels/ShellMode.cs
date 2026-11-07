namespace Hymnal.ViewModels;

/// <summary>
/// High-level centre-panel navigation modes for the main shell.
/// Write is the default authoring surface; Manage hosts the Gantt view; Ideas hosts story
/// ideas (story and anthology projects only). Edit is reserved for later expansion.
/// </summary>
public enum ShellMode
{
    Research,
    Ideas,
    Plan,
    Write,
    Manage,
    Edit,
}

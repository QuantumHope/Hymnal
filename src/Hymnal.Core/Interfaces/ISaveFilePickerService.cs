namespace Hymnal.Core.Interfaces;

/// <summary>Opens a native "save as" dialog and returns the chosen path, or null when cancelled.</summary>
public interface ISaveFilePickerService
{
    /// <param name="suggestedName">File name offered in the dialog.</param>
    /// <param name="suggestedStartDirectory">Folder the dialog opens in, when it exists.</param>
    /// <param name="typeName">Name of the file type, e.g. "Word document".</param>
    /// <param name="extension">Extension without the dot, e.g. "docx".</param>
    Task<string?> PickSaveFileAsync(string suggestedName, string? suggestedStartDirectory, string typeName, string extension);
}

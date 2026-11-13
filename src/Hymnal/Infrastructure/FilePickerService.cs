using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Hymnal.Core.Interfaces;

namespace Hymnal.Infrastructure;

public class FilePickerService : IFilePickerService, ISaveFilePickerService
{
    private readonly Func<TopLevel?> _topLevelAccessor;

    public FilePickerService(Func<TopLevel?> topLevelAccessor)
    {
        _topLevelAccessor = topLevelAccessor;
    }

    public async Task<string?> PickFileAsync(string? suggestedStartDirectory = null)
    {
        var topLevel = _topLevelAccessor();
        if (topLevel == null)
            return null;

        var options = new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } },
                FilePickerFileTypes.All
            }
        };

        if (!string.IsNullOrWhiteSpace(suggestedStartDirectory) && Directory.Exists(suggestedStartDirectory))
        {
            try
            {
                var folder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(suggestedStartDirectory);
                if (folder != null)
                    options.SuggestedStartLocation = folder;
            }
            catch
            {
                // Non-fatal; picker opens without a suggested folder.
            }
        }

        var results = await topLevel.StorageProvider.OpenFilePickerAsync(options);

        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    public async Task<string?> PickSaveFileAsync(
        string suggestedName, string? suggestedStartDirectory, string typeName, string extension)
    {
        var topLevel = _topLevelAccessor();
        if (topLevel == null)
            return null;

        var options = new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + extension } } }
        };

        if (!string.IsNullOrWhiteSpace(suggestedStartDirectory) && Directory.Exists(suggestedStartDirectory))
        {
            try
            {
                var folder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(suggestedStartDirectory);
                if (folder != null)
                    options.SuggestedStartLocation = folder;
            }
            catch
            {
                // Non-fatal; picker opens without a suggested folder.
            }
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        return file?.Path.LocalPath;
    }
}

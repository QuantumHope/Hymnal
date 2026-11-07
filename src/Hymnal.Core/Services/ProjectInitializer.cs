using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class ProjectInitializer(IMetadataStore store, ProjectSettingsService settings)
{
    public static bool NeedsInitialization(string folderPath) =>
        !File.Exists(Path.Combine(folderPath, "Book.txt")) &&
        !File.Exists(Path.Combine(folderPath, "manuscript", "Book.txt"));

    public async Task<Result<Unit>> InitializeAsync(string folderPath, ProjectKind kind)
    {
        if (!NeedsInitialization(folderPath))
            return Result<Unit>.Fail("This folder already contains a Book.txt.");

        try
        {
            var files = Directory.EnumerateFiles(folderPath)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => string.Equals(Path.GetExtension(n), ".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                var starter = kind switch
                {
                    ProjectKind.Story => "story.md",
                    ProjectKind.Anthology => "story-01.md",
                    _ => "chapter-01.md"
                };
                await store.WriteTextAtomicAsync(Path.Combine(folderPath, starter), "# Untitled\n").ConfigureAwait(false);
                files.Add(starter);
            }

            var bookTxt = string.Concat(files.Select(f => f + "\n"));
            await store.WriteTextAtomicAsync(Path.Combine(folderPath, "Book.txt"), bookTxt).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return Result<Unit>.Fail($"Project could not be initialized: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit>.Fail($"Project could not be initialized: {ex.Message}");
        }

        return await settings.SaveAsync(folderPath, new ProjectSettings(kind)).ConfigureAwait(false);
    }
}

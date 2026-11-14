using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public sealed class OrphanFileDiscoveryService : IOrphanFileDiscoveryService
{
    private static readonly HashSet<string> ExcludedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { ".hymnal-data" };

    public Task<IReadOnlyList<OrphanFileInfo>> DiscoverAsync(
        string manuscriptRoot,
        IReadOnlyList<string> bookTxtEntries)
    {
        if (string.IsNullOrWhiteSpace(manuscriptRoot))
            return Task.FromResult<IReadOnlyList<OrphanFileInfo>>(Array.Empty<OrphanFileInfo>());

        if (!Directory.Exists(manuscriptRoot))
            return Task.FromResult<IReadOnlyList<OrphanFileInfo>>(Array.Empty<OrphanFileInfo>());

        var manuscriptRootFull = Path.GetFullPath(manuscriptRoot);
        var registered = new HashSet<string>(
            bookTxtEntries.Select(NormalizeRelativePath),
            StringComparer.OrdinalIgnoreCase);

        var orphans = new List<OrphanFileInfo>();

        foreach (var absolutePath in Directory.EnumerateFiles(
                     manuscriptRootFull,
                     "*.md",
                     SearchOption.AllDirectories))
        {
            if (IsUnderExcludedDirectory(manuscriptRootFull, absolutePath))
                continue;

            // Hymnal's own folders at the top: other collections' lists, and the copy published for LeanPub.
            var topLevel = Path.GetRelativePath(manuscriptRootFull, absolutePath)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (string.Equals(topLevel, CollectionService.CollectionsFolder, StringComparison.OrdinalIgnoreCase)
                || CollectionService.IsPublishedCopy(manuscriptRootFull, topLevel))
                continue;

            var relativePath = Path.GetRelativePath(manuscriptRootFull, absolutePath)
                .Replace('\\', '/');

            if (string.Equals(relativePath, "Book.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            if (registered.Contains(relativePath))
                continue;

            var title = ReadTitle(absolutePath);
            var partFolder = DetectPartFolder(relativePath);
            orphans.Add(new OrphanFileInfo(relativePath, title, partFolder));
        }

        orphans.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<OrphanFileInfo>>(orphans.AsReadOnly());
    }

    private static string NormalizeRelativePath(string path) =>
        path.Trim().Replace('\\', '/');

    private static bool IsUnderExcludedDirectory(string manuscriptRootFull, string absolutePath)
    {
        var relative = Path.GetRelativePath(manuscriptRootFull, absolutePath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ExcludedDirectoryNames.Contains(segment))
                return true;
        }

        return false;
    }

    private static string? DetectPartFolder(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.IndexOf('/');
        if (slashIndex <= 0)
            return null;

        return normalized[..slashIndex];
    }

    private static string ReadTitle(string absolutePath)
    {
        try
        {
            var content = File.ReadAllText(absolutePath);
            return BookTxtParser.ExtractTitleFromText(content)
                   ?? Path.GetFileNameWithoutExtension(absolutePath);
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(absolutePath);
        }
    }
}

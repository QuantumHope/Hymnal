namespace Hymnal.Core.Markua;

/// <summary>
/// Finds the local file a Markua resource (<c>![caption](source)</c>) points at. Leanpub looks for resources in
/// the manuscript's <c>resources/</c> folder; authors also write paths relative to the chapter file, so both are
/// tried, from the chapter's folder up through its two parents.
/// </summary>
public static class MarkuaResourceResolver
{
    /// <returns>The full path of the first candidate that exists, or null (also for web URLs, which the preview
    /// never fetches).</returns>
    public static string? Resolve(string? baseDirectory, string source, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(source) || IsRemote(source))
            return null;

        var relative = Uri.UnescapeDataString(source.Split('?', '#')[0]).Replace('\\', '/');

        // A document must never make the preview reach another machine: probing a share such as
        // //host/share/x.png (\\host\share\x.png) can start network access and Windows authentication.
        if (IsNetworkPath(relative) || relative.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        if (Path.IsPathRooted(relative))
            return fileExists(relative) ? relative : null;

        if (string.IsNullOrEmpty(baseDirectory))
            return null;

        // Only a workspace that itself lives on a share may resolve to one (e.g. via ../ out of a local folder).
        var baseIsShare = IsNetworkPath(baseDirectory.Replace('\\', '/'));
        foreach (var candidate in Candidates(baseDirectory, relative))
        {
            if (!baseIsShare && IsNetworkPath(candidate.Replace('\\', '/')))
                continue;

            if (fileExists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>True for a UNC / network-share path written with forward slashes: //host/share/…</summary>
    private static bool IsNetworkPath(string path) => path.StartsWith("//", StringComparison.Ordinal);

    public static bool IsRemote(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Candidates(string baseDirectory, string relative)
    {
        var directory = baseDirectory;
        for (var depth = 0; depth < 3 && !string.IsNullOrEmpty(directory); depth++)
        {
            yield return Path.GetFullPath(Path.Combine(directory, relative));
            yield return Path.GetFullPath(Path.Combine(directory, "resources", relative));
            directory = Path.GetDirectoryName(directory);
        }
    }
}

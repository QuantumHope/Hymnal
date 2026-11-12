using System.Text;
using System.Text.Json;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Several collections over one shared folder of stories, and publishing one to LeanPub.
/// <para>
/// Layout (Book.txt at the workspace root):
/// <list type="bullet">
/// <item><c>collections/&lt;slug&gt;/collection.json</c> — <c>{schemaVersion, title}</c>, one folder per collection.</item>
/// <item>The <b>open</b> collection's list is the workspace's own <c>Book.txt</c> (and its excluded entries
/// <c>.hymnal-data/exclusions.json</c>), so everything else in Hymnal works on it unchanged.</item>
/// <item>Every other collection keeps its list in <c>collections/&lt;slug&gt;/Book.txt</c> (and
/// <c>exclusions.json</c>). Switching swaps them.</item>
/// <item><c>.hymnal-data/collections.json</c> — <c>{schemaVersion, active}</c>, the open collection's slug.</item>
/// <item><c>manuscript/</c> — what LeanPub reads: a published copy of one collection (Book.txt and the files
/// it lists, without Hymnal's embedded data), marked by <c>manuscript/.hymnal-published.json</c>.</item>
/// </list>
/// A switch never loses a list: the open list is saved to its folder first, then the new active slug is
/// recorded (the commit point), then the new list is put in place and its stored copy removed. A stored copy
/// of the active collection found on open means a switch was interrupted; <see cref="RecoverAsync"/> finishes it.
/// </para>
/// </summary>
public sealed class CollectionService(IMetadataStore store)
{
    public const string CollectionsFolder = "collections";
    public const string PublishFolder = "manuscript";
    public const string PublishMarker = ".hymnal-published.json";
    private const string BookTxt = "Book.txt";
    private const string ExclusionsFile = "exclusions.json";
    private const string CollectionFile = "collection.json";
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // ── Collections ───────────────────────────────────────────────────────────

    /// <summary>
    /// Collections need Book.txt at the workspace root: the stories live there and <c>manuscript/</c> is free
    /// for the published copy.
    /// </summary>
    public static bool SupportsCollections(string workspaceRoot, string manuscriptRoot) =>
        SamePath(workspaceRoot, manuscriptRoot);

    /// <summary>The open collection's slug, or null when the workspace has never named a collection.</summary>
    public async Task<string?> GetActiveSlugAsync(string root)
    {
        var path = ActiveFilePath(root);
        if (!File.Exists(path))
            return null;
        try
        {
            var doc = JsonSerializer.Deserialize<ActiveFile>(await File.ReadAllTextAsync(path).ConfigureAwait(false), Json);
            return doc?.Active is { Length: > 0 } slug && IsValidSlug(slug) ? slug : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every collection, the open one first, then by title.</summary>
    public async Task<Result<IReadOnlyList<CollectionInfo>>> ListAsync(string root)
    {
        try
        {
            var active = await GetActiveSlugAsync(root).ConfigureAwait(false);
            var folder = Path.Combine(root, CollectionsFolder);
            var list = new List<CollectionInfo>();
            if (Directory.Exists(folder))
            {
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    var slug = Path.GetFileName(dir);
                    if (!IsValidSlug(slug) || !File.Exists(Path.Combine(dir, CollectionFile)))
                        continue;
                    var isActive = slug == active;
                    var lines = await ReadListAsync(isActive ? Path.Combine(root, BookTxt) : Path.Combine(dir, BookTxt))
                        .ConfigureAwait(false);
                    list.Add(new CollectionInfo(slug, await ReadTitleAsync(dir, slug).ConfigureAwait(false), isActive,
                        lines.Count));
                }
            }
            return Result<IReadOnlyList<CollectionInfo>>.Ok(list
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<CollectionInfo>>.Fail($"The collections could not be read: {ex.Message}");
        }
    }

    /// <summary>The open collection's title, or null when it has none.</summary>
    public async Task<string?> GetActiveTitleAsync(string root)
    {
        var slug = await GetActiveSlugAsync(root).ConfigureAwait(false);
        if (slug == null)
            return null;
        var dir = Path.Combine(root, CollectionsFolder, slug);
        return File.Exists(Path.Combine(dir, CollectionFile)) ? await ReadTitleAsync(dir, slug).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Names the open collection. The first time, this gives it its folder under <c>collections/</c> and makes
    /// it the active one; later it only changes the title (the folder name stays).
    /// </summary>
    public async Task<Result<string>> SetActiveTitleAsync(string root, string title)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0)
            return Result<string>.Fail("A collection needs a title.");
        try
        {
            var slug = await GetActiveSlugAsync(root).ConfigureAwait(false);
            if (slug == null || !Directory.Exists(Path.Combine(root, CollectionsFolder, slug)))
            {
                slug = UniqueSlug(root, title);
                await WriteTitleAsync(root, slug, title).ConfigureAwait(false);
                await WriteActiveAsync(root, slug).ConfigureAwait(false);
            }
            else
            {
                await WriteTitleAsync(root, slug, title).ConfigureAwait(false);
            }
            return Result<string>.Ok(slug);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail($"The collection could not be named: {ex.Message}");
        }
    }

    /// <summary>Adds an empty collection (not opened). Returns its slug.</summary>
    public async Task<Result<string>> CreateAsync(string root, string title)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0)
            return Result<string>.Fail("A collection needs a title.");
        try
        {
            var slug = UniqueSlug(root, title);
            await store.WriteTextAtomicAsync(Path.Combine(root, CollectionsFolder, slug, BookTxt), "").ConfigureAwait(false);
            await WriteTitleAsync(root, slug, title).ConfigureAwait(false);
            return Result<string>.Ok(slug);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail($"The collection could not be created: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens collection <paramref name="slug"/>: the open list (Book.txt and excluded entries) is saved to its
    /// folder and <paramref name="slug"/>'s list takes its place. The open collection must have a folder (be
    /// named) first, so its list has somewhere to go.
    /// </summary>
    public async Task<Result<Unit>> SwitchAsync(string root, string slug)
    {
        if (!IsValidSlug(slug) || !File.Exists(Path.Combine(root, CollectionsFolder, slug, CollectionFile)))
            return Result<Unit>.Fail($"There is no collection '{slug}'.");
        try
        {
            var active = await GetActiveSlugAsync(root).ConfigureAwait(false);
            if (active == slug)
                return Result<Unit>.Ok(Unit.Default);
            if (active == null || !Directory.Exists(Path.Combine(root, CollectionsFolder, active)))
                return Result<Unit>.Fail("Name the open collection first, so its list is kept when you switch.");

            // 1. Keep the open list in its own folder (nothing is removed yet).
            var openDir = Path.Combine(root, CollectionsFolder, active);
            await CopyTextAsync(Path.Combine(root, BookTxt), Path.Combine(openDir, BookTxt), emptyWhenMissing: true)
                .ConfigureAwait(false);
            await CopyOrRemoveAsync(ExclusionsPath(root), Path.Combine(openDir, ExclusionsFile)).ConfigureAwait(false);

            // 2. The commit point: from here the new collection is the open one.
            await WriteActiveAsync(root, slug).ConfigureAwait(false);

            // 3. Put its list in place and drop its stored copy.
            await RecoverAsync(root).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"The collection could not be opened: {ex.Message}");
        }
    }

    /// <summary>
    /// Finishes a switch that was interrupted after its commit point: a stored list in the active collection's
    /// folder is the one to open, so it replaces Book.txt (and the excluded entries) and is removed.
    /// Does nothing when there is none. Call before the workspace reads Book.txt.
    /// </summary>
    public async Task RecoverAsync(string root)
    {
        var active = await GetActiveSlugAsync(root).ConfigureAwait(false);
        if (active == null)
            return;
        var dir = Path.Combine(root, CollectionsFolder, active);
        var storedBook = Path.Combine(dir, BookTxt);
        if (!File.Exists(storedBook))
            return;

        await CopyTextAsync(storedBook, Path.Combine(root, BookTxt), emptyWhenMissing: true).ConfigureAwait(false);
        await CopyOrRemoveAsync(Path.Combine(dir, ExclusionsFile), ExclusionsPath(root)).ConfigureAwait(false);
        File.Delete(storedBook);
        var storedExclusions = Path.Combine(dir, ExclusionsFile);
        if (File.Exists(storedExclusions))
            File.Delete(storedExclusions);
    }

    /// <summary>
    /// Story files (<c>.md</c>) in the workspace that no collection lists: not the open Book.txt, and not any
    /// other collection's. Workspace-relative, forward slashes, in path order.
    /// </summary>
    public async Task<Result<IReadOnlyList<string>>> UnassignedStoriesAsync(string root)
    {
        try
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in await ReadListAsync(Path.Combine(root, BookTxt)).ConfigureAwait(false))
                listed.Add(line);
            var folder = Path.Combine(root, CollectionsFolder);
            if (Directory.Exists(folder))
                foreach (var dir in Directory.EnumerateDirectories(folder))
                    foreach (var line in await ReadListAsync(Path.Combine(dir, BookTxt)).ConfigureAwait(false))
                        listed.Add(line);

            var stories = StoryFiles(root)
                .Where(path => !listed.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Result<IReadOnlyList<string>>.Ok(stories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<string>>.Fail($"The story files could not be read: {ex.Message}");
        }
    }

    // ── LeanPub ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What LeanPub would get for collection <paramref name="slug"/> (null: the open list): its Book.txt
    /// entries in order, which are story dividers, and which files are missing.
    /// </summary>
    public async Task<Result<PublishPreview>> PreviewAsync(string workspaceRoot, string manuscriptRoot, string? slug = null)
    {
        try
        {
            var active = await GetActiveSlugAsync(manuscriptRoot).ConfigureAwait(false);
            var isOpen = slug == null || slug == active;
            var bookPath = isOpen
                ? Path.Combine(manuscriptRoot, BookTxt)
                : Path.Combine(manuscriptRoot, CollectionsFolder, slug!, BookTxt);
            var lines = await ReadListAsync(bookPath).ConfigureAwait(false);
            var nodes = await BookTxtParser.ParseAsync(manuscriptRoot, lines).ConfigureAwait(false);
            var entries = nodes
                .Select(n => new PublishEntry(n.RelativePath, n.Title, n.Kind == NodeKind.Part, !n.IsMissing))
                .ToList();

            var problems = entries.Where(e => !e.Exists)
                .Select(e => $"'{e.RelativePath}' is listed but the file is missing; it will be left out.")
                .ToList();
            if (entries.Count == 0)
                problems.Add("The collection lists no stories yet.");

            var title = isOpen
                ? await GetActiveTitleAsync(manuscriptRoot).ConfigureAwait(false)
                : await ReadTitleAsync(Path.Combine(manuscriptRoot, CollectionsFolder, slug!), slug!).ConfigureAwait(false);

            return Result<PublishPreview>.Ok(new PublishPreview(
                title ?? "", entries, problems, PublishBlockedReason(workspaceRoot, manuscriptRoot)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<PublishPreview>.Fail($"The collection could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies collection <paramref name="slug"/> (null: the open list) into <c>manuscript/</c> for LeanPub: its
    /// Book.txt, each file it lists (prose only, without Hymnal's embedded data) at the same relative path, and
    /// a <c>resources/</c> folder when there is one. Files a previous publish copied that the new list no longer
    /// has are removed; anything else in <c>manuscript/</c> is left alone.
    /// </summary>
    public async Task<Result<PublishResult>> PublishAsync(string workspaceRoot, string manuscriptRoot, string? slug = null)
    {
        var preview = await PreviewAsync(workspaceRoot, manuscriptRoot, slug).ConfigureAwait(false);
        if (!preview.IsSuccess)
            return Result<PublishResult>.Fail(preview.Error!);
        if (preview.Value!.BlockedReason is { } blocked)
            return Result<PublishResult>.Fail(blocked);

        var target = Path.Combine(manuscriptRoot, PublishFolder);
        try
        {
            var previous = await ReadMarkerAsync(target).ConfigureAwait(false);
            var written = new List<string>();

            var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            foreach (var entry in preview.Value.Entries.Where(e => e.Exists))
            {
                if (!FullPath(target, entry.RelativePath).StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                    return Result<PublishResult>.Fail(
                        $"'{entry.RelativePath}' is outside the project; LeanPub can only read files inside manuscript/.");
                var text = await File.ReadAllTextAsync(FullPath(manuscriptRoot, entry.RelativePath)).ConfigureAwait(false);
                var prose = StoryFileFormat.Split(text).Prose;
                await store.WriteTextAtomicAsync(FullPath(target, entry.RelativePath), prose).ConfigureAwait(false);
                written.Add(entry.RelativePath);
            }

            var resources = Path.Combine(manuscriptRoot, "resources");
            if (Directory.Exists(resources))
            {
                foreach (var file in Directory.EnumerateFiles(resources, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(manuscriptRoot, file).Replace('\\', '/');
                    var destination = FullPath(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                    written.Add(relative);
                }
            }

            var book = string.Join("\n", preview.Value.Entries.Where(e => e.Exists).Select(e => e.RelativePath)) + "\n";
            await store.WriteTextAtomicAsync(Path.Combine(target, BookTxt), book).ConfigureAwait(false);

            var keep = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            foreach (var stale in previous.Where(p => !keep.Contains(p)))
            {
                var path = FullPath(target, stale);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }

            var marker = new PublishMarkerFile { SchemaVersion = SchemaVersion, Collection = slug, Files = written };
            await store.WriteTextAtomicAsync(Path.Combine(target, PublishMarker), JsonSerializer.Serialize(marker, Json))
                .ConfigureAwait(false);
            return Result<PublishResult>.Ok(new PublishResult(written.Count, removed, target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<PublishResult>.Fail($"The collection could not be published: {ex.Message}");
        }
    }

    /// <summary>Why <c>manuscript/</c> can't take a published copy here; null when it can.</summary>
    public static string? PublishBlockedReason(string workspaceRoot, string manuscriptRoot)
    {
        if (!SamePath(workspaceRoot, manuscriptRoot))
            return "This project already keeps its Book.txt in manuscript/, which is what LeanPub reads: it is published as it is.";
        var target = Path.Combine(manuscriptRoot, PublishFolder);
        if (File.Exists(Path.Combine(target, BookTxt)) && !File.Exists(Path.Combine(target, PublishMarker)))
            return "manuscript/ already holds a Book.txt that Hymnal didn't write, so it won't be overwritten.";
        return null;
    }

    /// <summary>True when <paramref name="topLevelFolder"/> (a top-level folder name) is Hymnal's published copy.</summary>
    public static bool IsPublishedCopy(string manuscriptRoot, string topLevelFolder) =>
        string.Equals(topLevelFolder, PublishFolder, StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(manuscriptRoot, PublishFolder, PublishMarker));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IEnumerable<string> StoryFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var first = relative.Split('/')[0];
            if (first.StartsWith('.') || string.Equals(first, CollectionsFolder, StringComparison.OrdinalIgnoreCase)
                || IsPublishedCopy(root, first) || string.Equals(relative, "README.md", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return relative;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadListAsync(string bookPath)
    {
        if (!File.Exists(bookPath))
            return [];
        var lines = await File.ReadAllLinesAsync(bookPath).ConfigureAwait(false);
        return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Replace('\\', '/')).ToList();
    }

    private async Task CopyTextAsync(string from, string to, bool emptyWhenMissing)
    {
        var text = File.Exists(from) ? await File.ReadAllTextAsync(from).ConfigureAwait(false) : emptyWhenMissing ? "" : null;
        if (text != null)
            await store.WriteTextAtomicAsync(to, text).ConfigureAwait(false);
    }

    /// <summary>Makes <paramref name="to"/> a copy of <paramref name="from"/>, or removes it when there is no source.</summary>
    private async Task CopyOrRemoveAsync(string from, string to)
    {
        if (File.Exists(from))
            await store.WriteTextAtomicAsync(to, await File.ReadAllTextAsync(from).ConfigureAwait(false)).ConfigureAwait(false);
        else if (File.Exists(to))
            File.Delete(to);
    }

    private static async Task<string> ReadTitleAsync(string dir, string slug)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<CollectionFileDoc>(
                await File.ReadAllTextAsync(Path.Combine(dir, CollectionFile)).ConfigureAwait(false), Json);
            return doc?.Title is { Length: > 0 } title ? title : slug;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return slug;
        }
    }

    private Task WriteTitleAsync(string root, string slug, string title) =>
        store.WriteTextAtomicAsync(Path.Combine(root, CollectionsFolder, slug, CollectionFile),
            JsonSerializer.Serialize(new CollectionFileDoc { SchemaVersion = SchemaVersion, Title = title }, Json));

    private Task WriteActiveAsync(string root, string slug) =>
        store.WriteTextAtomicAsync(ActiveFilePath(root),
            JsonSerializer.Serialize(new ActiveFile { SchemaVersion = SchemaVersion, Active = slug }, Json));

    private static async Task<IReadOnlyList<string>> ReadMarkerAsync(string target)
    {
        var path = Path.Combine(target, PublishMarker);
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<PublishMarkerFile>(await File.ReadAllTextAsync(path).ConfigureAwait(false), Json)
                ?.Files ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string UniqueSlug(string root, string title)
    {
        var builder = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
            builder.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '-');
        var slug = string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0)
            slug = "collection";
        if (slug.Length > 60)
            slug = slug[..60].TrimEnd('-');

        var candidate = slug;
        for (var n = 2; Directory.Exists(Path.Combine(root, CollectionsFolder, candidate)); n++)
            candidate = $"{slug}-{n}";
        return candidate;
    }

    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.All(c => (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c == '-');

    private static string ActiveFilePath(string root) => Path.Combine(root, ".hymnal-data", "collections.json");

    private static string ExclusionsPath(string root) => Path.Combine(root, ".hymnal-data", ExclusionsFile);

    private static string FullPath(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class ActiveFile
    {
        public int SchemaVersion { get; set; }
        public string? Active { get; set; }
    }

    private sealed class CollectionFileDoc
    {
        public int SchemaVersion { get; set; }
        public string? Title { get; set; }
    }

    private sealed class PublishMarkerFile
    {
        public int SchemaVersion { get; set; }
        public string? Collection { get; set; }
        public List<string> Files { get; set; } = [];
    }
}

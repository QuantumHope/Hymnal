using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// One story as the planner sees it: a single file, or a divider (<c>{class: part}</c>) with the scene files after it.
/// </summary>
/// <param name="Key">Its first Book.txt line (the file, or the divider): how every collection refers to it.</param>
/// <param name="Lines">The Book.txt lines it takes up in a collection: the key, then any scene files.</param>
/// <param name="Words">Readable words across its files (prose only, never a <c>hymnal:v1</c> block).</param>
public sealed record PlannedStory(string Key, string Title, bool IsMultiScene, IReadOnlyList<string> Lines, bool IsMissing, int Words)
{
    public int SceneCount => IsMultiScene ? Lines.Count - 1 : 1;
}

/// <summary>Which collection: the open one (the workspace's Book.txt) or another by its folder slug.</summary>
public sealed record CollectionRef(string? Slug, bool IsOpen)
{
    public static CollectionRef Open { get; } = new(null, true);
    public static CollectionRef Other(string slug) => new(slug, false);
}

/// <summary>A collection on the planner: its stories, by key, in order.</summary>
public sealed record PlannedCollection(CollectionRef Ref, string Title, bool IsNamed, IReadOnlyList<string> StoryKeys);

/// <summary>Every story in the workspace (the library) and every collection that orders some of them.</summary>
public sealed record CollectionPlan(IReadOnlyList<PlannedStory> Stories, IReadOnlyList<PlannedCollection> Collections)
{
    /// <summary>The collections that list <paramref name="key"/>.</summary>
    public IEnumerable<PlannedCollection> CollectionsOf(string key) =>
        Collections.Where(c => c.StoryKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// The collection planner's model of an anthology: stories exist on their own (a library of story files), and
/// each collection is an ordered list of some of them, so a story can be in no collection or in several.
/// Reads every collection's Book.txt the way the rest of Hymnal does (<see cref="BookTxtParser"/>: a divider and the
/// files after it, up to the next divider, are one story), and edits one list at a time by whole stories, so a
/// multi-scene story always moves with its scenes. Lists are written atomically through <see cref="IMetadataStore"/>.
/// The open collection's list is the workspace's Book.txt; the caller reloads the workspace after changing it.
/// </summary>
public sealed class CollectionPlanService(IMetadataStore store, CollectionService collections, WordCountService words)
{
    private const string BookTxt = "Book.txt";

    public static string ListPath(string root, CollectionRef collection) =>
        collection.IsOpen
            ? Path.Combine(root, BookTxt)
            : Path.Combine(root, CollectionService.CollectionsFolder, collection.Slug!, BookTxt);

    public async Task<Result<CollectionPlan>> LoadAsync(string root)
    {
        try
        {
            var stories = new Dictionary<string, PlannedStory>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var planned = new List<PlannedCollection>();

            var list = await collections.ListAsync(root).ConfigureAwait(false);
            var infos = list.IsSuccess ? list.Value! : [];
            if (!infos.Any(c => c.IsActive))
            {
                var title = await collections.GetActiveTitleAsync(root).ConfigureAwait(false);
                planned.Add(await ReadCollectionAsync(root, CollectionRef.Open, title ?? "", isNamed: !string.IsNullOrWhiteSpace(title),
                    stories, order).ConfigureAwait(false));
            }
            foreach (var info in infos)
                planned.Add(await ReadCollectionAsync(root,
                    info.IsActive ? CollectionRef.Open with { Slug = info.Slug } : CollectionRef.Other(info.Slug),
                    info.Title, isNamed: true, stories, order).ConfigureAwait(false));

            // Story files no collection lists join the library as stories of their own.
            var listedFiles = new HashSet<string>(stories.Values.SelectMany(s => s.Lines), StringComparer.OrdinalIgnoreCase);
            var free = await collections.UnassignedStoriesAsync(root).ConfigureAwait(false);
            if (free.IsSuccess)
                foreach (var path in free.Value!.Where(p => !listedFiles.Contains(p)))
                {
                    var node = BookTxtParser.Parse(root, [path])[0];
                    stories[path] = await StoryAsync(root, node, [path]).ConfigureAwait(false);
                    order.Add(path);
                }

            return Result<CollectionPlan>.Ok(new CollectionPlan(order.Select(k => stories[k]).ToList(), planned));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<CollectionPlan>.Fail($"The collections could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts <paramref name="story"/> into <paramref name="target"/> before its <paramref name="index"/>-th story
    /// (clamped; already there = a reorder). Lines of the story elsewhere in that list are taken out first.
    /// </summary>
    public async Task<Result<Unit>> InsertAsync(string root, CollectionRef target, PlannedStory story, int index)
    {
        try
        {
            var blocks = await ReadBlocksAsync(root, target).ConfigureAwait(false);
            var existing = blocks.FindIndex(b => Same(b[0], story.Key));
            if (existing >= 0)
            {
                blocks.RemoveAt(existing);
                if (existing < index)
                    index--;
            }
            // Its scene files can't also sit loose in the same list.
            var lines = new HashSet<string>(story.Lines, StringComparer.OrdinalIgnoreCase);
            for (var i = blocks.Count - 1; i >= 0; i--)
                if (blocks[i].Count == 1 && lines.Contains(blocks[i][0]))
                {
                    blocks.RemoveAt(i);
                    if (i < index)
                        index--;
                }
            blocks.Insert(Math.Clamp(index, 0, blocks.Count), story.Lines.ToList());
            await WriteBlocksAsync(root, target, blocks).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"“{story.Title}” couldn't be added: {ex.Message}");
        }
    }

    /// <summary>Takes <paramref name="story"/> (with its scenes) out of <paramref name="target"/>; its files are untouched.</summary>
    public async Task<Result<Unit>> RemoveAsync(string root, CollectionRef target, PlannedStory story)
    {
        try
        {
            var blocks = await ReadBlocksAsync(root, target).ConfigureAwait(false);
            if (blocks.RemoveAll(b => Same(b[0], story.Key)) > 0)
                await WriteBlocksAsync(root, target, blocks).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Unit>.Fail($"“{story.Title}” couldn't be removed: {ex.Message}");
        }
    }

    /// <summary>Moves <paramref name="story"/> from one collection to another: added to the target first, so a failure never loses it.</summary>
    public async Task<Result<Unit>> MoveAsync(string root, CollectionRef from, CollectionRef to, PlannedStory story, int index)
    {
        var added = await InsertAsync(root, to, story, index).ConfigureAwait(false);
        if (!added.IsSuccess || SameCollection(from, to))
            return added;
        return await RemoveAsync(root, from, story).ConfigureAwait(false);
    }

    public static bool SameCollection(CollectionRef a, CollectionRef b) =>
        a.IsOpen ? b.IsOpen : !b.IsOpen && string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase);

    // ── Reading ───────────────────────────────────────────────────────────────

    private async Task<PlannedCollection> ReadCollectionAsync(string root, CollectionRef collection, string title, bool isNamed,
        Dictionary<string, PlannedStory> stories, List<string> order)
    {
        var keys = new List<string>();
        foreach (var block in await ReadNodeBlocksAsync(root, collection).ConfigureAwait(false))
        {
            var key = block[0].RelativePath;
            keys.Add(key);
            if (stories.ContainsKey(key))
                continue;
            stories[key] = await StoryAsync(root, block[0], block.Select(n => n.RelativePath).ToList()).ConfigureAwait(false);
            order.Add(key);
        }
        return new PlannedCollection(collection, title, isNamed, keys);
    }

    private async Task<PlannedStory> StoryAsync(string root, ChapterNode head, IReadOnlyList<string> lines)
    {
        var total = 0;
        foreach (var line in lines)
        {
            var path = Path.Combine(root, line);
            if (File.Exists(path))
                total += words.CountWords(StoryFileFormat.Split(await File.ReadAllTextAsync(path).ConfigureAwait(false)).Prose);
        }
        return new PlannedStory(head.RelativePath, head.Title, head.Kind == NodeKind.Part, lines, head.IsMissing, total);
    }

    /// <summary>A list's nodes grouped into stories: a file before the first divider alone, a divider with its scenes.</summary>
    private static async Task<List<List<ChapterNode>>> ReadNodeBlocksAsync(string root, CollectionRef collection)
    {
        var nodes = BookTxtParser.Parse(root, await ReadLinesAsync(ListPath(root, collection)).ConfigureAwait(false));
        var blocks = new List<List<ChapterNode>>();
        List<ChapterNode>? part = null;
        foreach (var node in nodes)
        {
            if (node.Kind == NodeKind.Part)
            {
                part = [node];
                blocks.Add(part);
            }
            else if (part != null)
            {
                part.Add(node);
            }
            else
            {
                blocks.Add([node]);
            }
        }
        return blocks;
    }

    private static async Task<List<List<string>>> ReadBlocksAsync(string root, CollectionRef collection) =>
        (await ReadNodeBlocksAsync(root, collection).ConfigureAwait(false))
            .Select(b => b.Select(n => n.RelativePath).ToList())
            .ToList();

    private static async Task<IReadOnlyList<string>> ReadLinesAsync(string path)
    {
        if (!File.Exists(path))
            return [];
        var lines = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
        return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Replace('\\', '/')).ToList();
    }

    /// <summary>
    /// Book.txt can't hold a one-file story after a multi-scene one: the files after a divider are its scenes, so the
    /// story would be read (and published) as one. Lists are written with the one-file stories first, each group in
    /// the order given.
    /// </summary>
    public static IReadOnlyList<T> BookOrder<T>(IEnumerable<T> stories, Func<T, bool> isMultiScene)
    {
        var list = stories.ToList();
        return list.Where(s => !isMultiScene(s)).Concat(list.Where(isMultiScene)).ToList();
    }

    private async Task WriteBlocksAsync(string root, CollectionRef collection, List<List<string>> blocks)
    {
        // A block of more than one line is a divider with its scenes; a lone divider is also multi-scene.
        var ordered = BookOrder(blocks, b => b.Count > 1 || IsDivider(root, b[0]));
        var text = string.Concat(ordered.SelectMany(b => b).Select(l => l + "\n"));
        await store.WriteTextAtomicAsync(ListPath(root, collection), text).ConfigureAwait(false);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsDivider(string root, string line) =>
        BookTxtParser.Parse(root, [line])[0].Kind == NodeKind.Part;
}

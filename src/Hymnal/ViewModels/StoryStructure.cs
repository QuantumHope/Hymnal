using System;
using System.Collections.Generic;
using System.Linq;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.ViewModels;

/// <summary>
/// How the Book.txt node list reads as stories, shared by Plan mode and Write mode's Beats panel.
/// A Short Story is the whole book: every chapter file is one of its scenes. In an anthology a top-level
/// chapter file is a story told in one file, and a Part is a story told in scenes: the chapter files after
/// it, up to the next Part.
/// </summary>
public static class StoryStructure
{
    /// <summary>A story and the board that holds its beats.</summary>
    /// <param name="Key">Beat board key: <see cref="BeatBoardService.ShortStoryBoardKey"/> or the story's registry UUID.</param>
    /// <param name="Story">The anthology story's node (a chapter or a Part); null for a Short Story.</param>
    /// <param name="Scenes">The story's scene files a beat may link to, in Book.txt order.</param>
    public sealed record StoryBoardRef(string Key, string Title, ChapterViewModel? Story, IReadOnlyList<ChapterViewModel> Scenes)
    {
        public IReadOnlyList<string> Scope => Scenes.Select(s => s.Node.RelativePath).ToList();
    }

    /// <summary>Title used for a Short Story whose first file has no title.</summary>
    public const string DefaultStoryTitle = "Story";

    /// <summary>Chapter files a beat may link to: in Book.txt and present on disk.</summary>
    public static IEnumerable<ChapterViewModel> IncludedChapters(IEnumerable<ChapterViewModel> nodes) =>
        nodes.Where(n => n.Node.Kind == NodeKind.Chapter && !n.Node.IsExcluded && !n.Node.IsMissing);

    /// <summary>
    /// Chapter files the story is made of, as Book.txt lists them (not excluded), including any missing from disk:
    /// what a manuscript needs, so an export can report a missing file rather than leave it out.
    /// </summary>
    public static IEnumerable<ChapterViewModel> ListedChapters(IEnumerable<ChapterViewModel> nodes) =>
        nodes.Where(n => n.Node.Kind == NodeKind.Chapter && !n.Node.IsExcluded);

    /// <summary>The scene files of an anthology story: itself, or the chapter files inside its Part (present ones only).</summary>
    public static IReadOnlyList<ChapterViewModel> StoryScenes(IReadOnlyList<ChapterViewModel> nodes, ChapterViewModel story) =>
        StoryFiles(nodes, story, IncludedChapters);

    /// <summary>Every scene file of an anthology story Book.txt lists, missing ones included (see <see cref="ListedChapters"/>).</summary>
    public static IReadOnlyList<ChapterViewModel> ListedStoryScenes(IReadOnlyList<ChapterViewModel> nodes, ChapterViewModel story) =>
        StoryFiles(nodes, story, ListedChapters);

    private static IReadOnlyList<ChapterViewModel> StoryFiles(IReadOnlyList<ChapterViewModel> nodes, ChapterViewModel story,
        Func<IEnumerable<ChapterViewModel>, IEnumerable<ChapterViewModel>> keep)
    {
        if (story.Node.Kind == NodeKind.Chapter)
            return keep([story]).ToList();

        var partIndex = IndexOf(nodes, story);
        return partIndex < 0
            ? []
            : keep(nodes.Skip(partIndex + 1).TakeWhile(n => n.Node.Kind != NodeKind.Part)).ToList();
    }

    /// <summary>
    /// The anthology story <paramref name="node"/> belongs to: a Part is its own story, a chapter file inside
    /// a Part belongs to that Part, and a chapter file before any Part is a story by itself. Null when the
    /// story is excluded from the book.
    /// </summary>
    public static ChapterViewModel? AnthologyStoryOf(IReadOnlyList<ChapterViewModel> nodes, ChapterViewModel node)
    {
        var story = node;
        if (node.Node.Kind == NodeKind.Chapter)
        {
            var index = IndexOf(nodes, node);
            for (var i = index - 1; i >= 0; i--)
            {
                if (nodes[i].Node.Kind == NodeKind.Part)
                {
                    story = nodes[i];
                    break;
                }
            }
        }
        return story.Node.IsExcluded ? null : story;
    }

    /// <summary>The Short Story: the board under the constant key, every included chapter file as a scene.</summary>
    public static StoryBoardRef ShortStory(IReadOnlyList<ChapterViewModel> nodes)
    {
        var scenes = IncludedChapters(nodes).ToList();
        var title = scenes.FirstOrDefault()?.Node.Title is { Length: > 0 } first ? first : DefaultStoryTitle;
        return new StoryBoardRef(BeatBoardService.ShortStoryBoardKey, title, null, scenes);
    }

    /// <summary>An anthology story's board, or null when it has no usable key.</summary>
    public static StoryBoardRef? AnthologyStory(IReadOnlyList<ChapterViewModel> nodes, ChapterViewModel story) =>
        BeatBoardService.IsValidBoardKey(story.Uuid)
            ? new StoryBoardRef(story.Uuid, story.Node.Title, story, StoryScenes(nodes, story))
            : null;

    private static int IndexOf(IReadOnlyList<ChapterViewModel> nodes, ChapterViewModel node)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (ReferenceEquals(nodes[i], node))
                return i;
        return -1;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hymnal.Core.Models;

/// <summary>
/// Immutable description of how a project kind (Novel, Short Story, Anthology) behaves:
/// visible statuses, Gantt phases, UI nouns, target presets and feature switches.
/// </summary>
public sealed class ProjectProfile
{
    internal static readonly string[] NovelPhases =
        ["Outlining", "Drafting", "Editing", "Polishing", "Reviewing"];

    private static readonly string[] ShortFormPhases = ["Drafting", "Editing"];

    private static readonly StatusOption[] NovelStatuses =
        Enum.GetValues<ChapterStatus>().Select(s => new StatusOption(s, s.ToString())).ToArray();

    private static readonly StatusOption[] ShortFormStatuses =
    [
        new(ChapterStatus.Planned, "Idea"),
        new(ChapterStatus.Drafting, "Drafting"),
        new(ChapterStatus.Editing, "Revising"),
        new(ChapterStatus.Done, "Done")
    ];

    private static readonly TargetPreset[] ShortFormPresets =
    [
        new("flash", "Flash", 0, 1000),
        new("shortStory", "Short Story", 1000, 7500),
        new("novelette", "Novelette", 7500, 17500),
        new("novella", "Novella", 17500, 40000),
        new("custom", "Custom", null, null)
    ];

    public static ProjectProfile Novel { get; } = new(
        ProjectKind.Novel, "Chapter", "Part", "Book", "Chapter Info",
        "BOOK STATS", "BY PART", "PART STATS", "BY CHAPTER",
        showsBookStats: true, allowsPartTargets: false,
        NovelStatuses, NovelPhases, ChapterStatus.Outlining, []);

    private static ProjectProfile Story { get; } = ShortForm(ProjectKind.Story);
    private static ProjectProfile Anthology { get; } = ShortForm(ProjectKind.Anthology);

    private readonly Dictionary<string, TargetPreset> _presetsByKey;

    private ProjectProfile(
        ProjectKind kind, string unitNoun, string partNoun, string collectionNoun, string infoPanelTitle,
        string bookStatsTitle, string bookStatsBarLabel, string partStatsTitle, string partStatsBarLabel,
        bool showsBookStats, bool allowsPartTargets,
        IReadOnlyList<StatusOption> statuses, IReadOnlyList<string> phases,
        ChapterStatus defaultStatus, IReadOnlyList<TargetPreset> presets,
        StorageMode storage = StorageMode.Sidecar)
    {
        Kind = kind;
        Storage = storage;
        UnitNoun = unitNoun;
        PartNoun = partNoun;
        CollectionNoun = collectionNoun;
        InfoPanelTitle = infoPanelTitle;
        BookStatsTitle = bookStatsTitle;
        BookStatsBarLabel = bookStatsBarLabel;
        PartStatsTitle = partStatsTitle;
        PartStatsBarLabel = partStatsBarLabel;
        ShowsBookStats = showsBookStats;
        AllowsPartTargets = allowsPartTargets;
        VisibleStatuses = statuses;
        GanttPhases = phases;
        DefaultStatus = defaultStatus;
        TargetPresets = presets;
        _presetsByKey = presets.ToDictionary(p => p.Key, StringComparer.Ordinal);
    }

    private static ProjectProfile ShortForm(ProjectKind kind) =>
        kind == ProjectKind.Anthology
            ? new(kind, "Story", "Story with scenes", "Collection", "Story Info",
                "COLLECTION STATS", "BY STORY", "STORY STATS", "BY SCENE",
                showsBookStats: true, allowsPartTargets: true,
                ShortFormStatuses, ShortFormPhases, ChapterStatus.Planned, ShortFormPresets)
            : new(kind, "Scene", "Section", "Story", "Story Info",
                "", "", "", "",
                showsBookStats: false, allowsPartTargets: false,
                ShortFormStatuses, ShortFormPhases, ChapterStatus.Planned, ShortFormPresets);

    public static ProjectProfile For(ProjectSettings settings)
    {
        var baseProfile = settings.Kind switch
        {
            ProjectKind.Story => Story,
            ProjectKind.Anthology => Anthology,
            _ => Novel
        };
        return baseProfile.Storage == settings.Storage ? baseProfile : baseProfile.WithStorage(settings.Storage);
    }

    private ProjectProfile WithStorage(StorageMode storage) =>
        new(Kind, UnitNoun, PartNoun, CollectionNoun, InfoPanelTitle,
            BookStatsTitle, BookStatsBarLabel, PartStatsTitle, PartStatsBarLabel,
            ShowsBookStats, AllowsPartTargets, VisibleStatuses, GanttPhases, DefaultStatus, TargetPresets, storage);

    public ProjectKind Kind { get; }
    public StorageMode Storage { get; }
    public string UnitNoun { get; }
    public string PartNoun { get; }
    public string CollectionNoun { get; }

    /// <summary>
    /// What a <see cref="PartNoun"/> holds: chapters in a novel's part, scenes in a story. Plan mode uses it
    /// for counts and "add" actions inside a Part.
    /// </summary>
    public string SubunitNoun => Kind == ProjectKind.Novel ? "Chapter" : "Scene";
    // ── Wording ───────────────────────────────────────────────────────────────
    // Every user-facing noun for the book's structure comes from here, so a short story or anthology
    // never speaks of chapters, parts or books.

    /// <summary>Plural of <see cref="UnitNoun"/>: Chapters, Scenes, Stories.</summary>
    public string UnitPlural => Kind switch
    {
        ProjectKind.Story => "Scenes",
        ProjectKind.Anthology => "Stories",
        _ => "Chapters"
    };

    /// <summary>Plural of <see cref="SubunitNoun"/>: Chapters, Scenes.</summary>
    public string SubunitPlural => Kind == ProjectKind.Novel ? "Chapters" : "Scenes";

    /// <summary>
    /// What one Book.txt entry is called. A Part divider is a <see cref="PartNoun"/> in a novel or short story
    /// and a story in an anthology; a file inside a Part is a <see cref="SubunitNoun"/>; any other file is a
    /// <see cref="UnitNoun"/>.
    /// </summary>
    public string NounFor(NodeKind kind, bool insidePart) => kind switch
    {
        NodeKind.Part => Kind == ProjectKind.Anthology ? "Story" : PartNoun,
        _ when insidePart => SubunitNoun,
        _ => UnitNoun
    };

    /// <summary>
    /// True when the author can focus on one story: an anthology narrows the sidebar, Plan and Schedule to it.
    /// </summary>
    public bool SupportsStoryFocus => Kind == ProjectKind.Anthology;

    /// <summary>The sidebar list of the book's contents: CHAPTERS, SCENES, STORIES.</summary>
    public string ContentsTitle => UnitPlural.ToUpperInvariant();

    /// <summary>Tooltip of the sidebar's + button.</summary>
    public string AddContentsTip => Kind switch
    {
        ProjectKind.Story => "Add a scene or section",
        ProjectKind.Anthology => "Add a story",
        _ => "Add chapter or part"
    };

    /// <summary>The editor's empty state title when nothing is open.</summary>
    public string NothingOpenTitle => Kind switch
    {
        ProjectKind.Story => "No scene open",
        ProjectKind.Anthology => "No story open",
        _ => "No chapter open"
    };

    /// <summary>The editor's empty state hint when nothing is open.</summary>
    public string NothingOpenHint => Kind switch
    {
        ProjectKind.Story => "Select a scene in the sidebar to write it here.",
        ProjectKind.Anthology => "Select a story or one of its scenes in the sidebar to write it here.",
        _ => "Select a chapter in the sidebar to load its Markua content here."
    };

    /// <summary>A side panel's empty state, e.g. "Open a scene to start taking notes."</summary>
    public string OpenSomethingTo(string purpose) => Kind switch
    {
        ProjectKind.Story => $"Open a scene to {purpose}.",
        ProjectKind.Anthology => $"Open a story or scene to {purpose}.",
        _ => $"Open a chapter to {purpose}."
    };

    /// <summary>Sidebar/Plan menu: put an excluded file back, e.g. "Include in story".</summary>
    public string IncludeInCollectionLabel => $"Include in {CollectionNoun.ToLowerInvariant()}";

    /// <summary>Sidebar menu: leave a file out of the book without deleting it.</summary>
    public string ExcludeFromCollectionLabel => $"Exclude from {CollectionNoun.ToLowerInvariant()}";

    /// <summary>AI chat scope buttons: the open file, its Part, the whole book.</summary>
    public string AiScopeUnitLabel => Kind == ProjectKind.Novel ? "CH" : "SCENE";
    public string AiScopePartLabel => Kind switch
    {
        ProjectKind.Story => "SECTION",
        ProjectKind.Anthology => "STORY",
        _ => "PART"
    };
    public string AiScopeAllLabel => Kind switch
    {
        ProjectKind.Story => "STORY",
        ProjectKind.Anthology => "ALL",
        _ => "BOOK"
    };

    /// <summary>The Schedule's first column and its whole-book rollup row.</summary>
    public string ScheduleItemHeader => Kind switch
    {
        ProjectKind.Story => "SCENE",
        ProjectKind.Anthology => "STORY",
        _ => "CHAPTER"
    };
    public string ScheduleRollupLabel => CollectionNoun.ToUpperInvariant();

    /// <summary>A new file's suggested name: new-chapter.md, new-scene.md, new-story.md.</summary>
    public string NewFileStem => $"new-{UnitNoun.ToLowerInvariant()}";

    /// <summary>A new divider's suggested path.</summary>
    public string NewPartPath => Kind switch
    {
        ProjectKind.Story => "section-two/section.md",
        ProjectKind.Anthology => "new-story/story.md",
        _ => "part-two/part.md"
    };

    public string InfoPanelTitle { get; }
    public string BookStatsTitle { get; }
    public string BookStatsBarLabel { get; }
    public string PartStatsTitle { get; }
    public string PartStatsBarLabel { get; }
    public bool ShowsBookStats { get; }
    public bool AllowsPartTargets { get; }
    public bool ShowsIdeas => Kind != ProjectKind.Novel;
    public bool UsesBeatBoard => Kind != ProjectKind.Novel;
    public IReadOnlyList<StatusOption> VisibleStatuses { get; }
    public IReadOnlyList<string> GanttPhases { get; }
    public ChapterStatus DefaultStatus { get; }
    public IReadOnlyList<TargetPreset> TargetPresets { get; }

    /// <summary>Maps a stored status onto one visible in this profile (display only; never rewrites storage).</summary>
    public ChapterStatus DisplayStatus(ChapterStatus stored)
    {
        if (Kind == ProjectKind.Novel) return stored;
        return stored switch
        {
            ChapterStatus.Outlining => ChapterStatus.Planned,
            ChapterStatus.Polishing or ChapterStatus.Reviewing => ChapterStatus.Editing,
            _ => stored
        };
    }

    public string LabelFor(ChapterStatus stored)
    {
        var display = DisplayStatus(stored);
        return VisibleStatuses.First(s => s.Value == display).Label;
    }

    public ChapterStatus? NextStatus(ChapterStatus stored)
    {
        var display = DisplayStatus(stored);
        for (var i = 0; i < VisibleStatuses.Count - 1; i++)
        {
            if (VisibleStatuses[i].Value == display)
                return VisibleStatuses[i + 1].Value;
        }
        return null;
    }

    public bool IsTrackablePhase(ChapterStatus stored) => GanttPhases.Contains(stored.ToString());

    public string? PresetLabel(string? key) =>
        key is not null && _presetsByKey.TryGetValue(key, out var p) ? p.Label : null;
}

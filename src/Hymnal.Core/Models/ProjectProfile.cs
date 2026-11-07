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
            : new(kind, "Scene", "Part", "Story", "Story Info",
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
    public string InfoPanelTitle { get; }
    public string BookStatsTitle { get; }
    public string BookStatsBarLabel { get; }
    public string PartStatsTitle { get; }
    public string PartStatsBarLabel { get; }
    public bool ShowsBookStats { get; }
    public bool AllowsPartTargets { get; }
    public bool ShowsIdeas => Kind != ProjectKind.Novel;
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

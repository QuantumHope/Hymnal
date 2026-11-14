using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Submission;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>A story on the planner: one card in the library, and the same object behind its entry in every column.</summary>
public sealed class PlannerStoryViewModel : ReactiveObject
{
    private bool _isSelected;

    public PlannerStoryViewModel(PlannedStory story, string? uuid, ChapterStatus? status, string statusLabel)
    {
        Story = story;
        Uuid = uuid;
        Status = status;
        StatusLabel = statusLabel;
    }

    public PlannedStory Story { get; }
    public string Key => Story.Key;
    public string Title => Story.Title;

    /// <summary>Registry id: its status, beats and notes hang off it. Null for a file Hymnal has never listed.</summary>
    public string? Uuid { get; }

    public ChapterStatus? Status { get; }
    public string StatusLabel { get; }
    public bool HasStatus => Status != null;

    public string WordsText => Story.Words.ToString("N0", CultureInfo.CurrentCulture) + " w";
    public string Category => ManuscriptWordCount.Category(Story.Words);
    public string ScenesText => Story.IsMultiScene ? $"{Story.SceneCount} scenes" : "";
    public bool IsMissing => Story.IsMissing;

    /// <summary>The collections it is in, as coloured dots; empty = on no shelf.</summary>
    public ObservableCollection<PlannerMembershipDot> Dots { get; } = new();
    public bool IsUnshelved => Dots.Count == 0;
    public string ShelfTip => Dots.Count == 0
        ? "In no collection"
        : "In " + string.Join(", ", Dots.Select(d => d.Title));

    public int BeatCount { get; set; }
    public IReadOnlyList<string> BeatTitles { get; set; } = [];
    public string BeatsText => BeatCount == 0 ? "No beats yet" : BeatCount == 1 ? "1 beat" : $"{BeatCount} beats";

    /// <summary>Selected on the planner: its card and every column entry for it are highlighted.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    internal void RaiseShelfChanged()
    {
        this.RaisePropertyChanged(nameof(IsUnshelved));
        this.RaisePropertyChanged(nameof(ShelfTip));
    }
}

/// <summary>One collection a story is in, drawn as a dot in that collection's colour.</summary>
public sealed record PlannerMembershipDot(string Title, int ColourIndex);

/// <summary>A story's place in one collection's column.</summary>
public sealed class PlannerEntryViewModel(PlannerStoryViewModel story, PlannerColumnViewModel column, int number)
{
    public PlannerStoryViewModel Story { get; } = story;
    public PlannerColumnViewModel Column { get; } = column;
    public int Number { get; } = number;
    public int Index => Number - 1;
}

/// <summary>A collection's column: its stories in book order, totals, and whether it is the one open in Write.</summary>
public sealed class PlannerColumnViewModel : ReactiveObject
{
    private string _dropHint = "";

    public PlannerColumnViewModel(PlannedCollection collection, int colourIndex)
    {
        Collection = collection;
        ColourIndex = colourIndex;
    }

    public PlannedCollection Collection { get; }
    public CollectionRef Ref => Collection.Ref;
    public string Title => Collection.Title;
    public bool IsOpen => Collection.Ref.IsOpen;
    public bool IsNamed => Collection.IsNamed;
    public bool CanOpen => !IsOpen && Collection.Ref.Slug != null;
    public int ColourIndex { get; }

    public ObservableCollection<PlannerEntryViewModel> Entries { get; } = new();
    public bool IsEmpty => Entries.Count == 0;

    public string CountText => Entries.Count == 1 ? "1 story" : $"{Entries.Count} stories";
    public string WordsText =>
        Entries.Sum(e => e.Story.Story.Words).ToString("N0", CultureInfo.CurrentCulture) + " words";

    /// <summary>While something is dragged over the column: what dropping it would do ("Move here", "Copy here").</summary>
    public string DropHint
    {
        get => _dropHint;
        set
        {
            this.RaiseAndSetIfChanged(ref _dropHint, value);
            this.RaisePropertyChanged(nameof(HasDropHint));
        }
    }

    public bool HasDropHint => DropHint.Length > 0;
}

/// <summary>The story panel's checkbox for one collection: ticking adds the story to it, clearing removes it.</summary>
public sealed class PlannerMembershipToggle : ReactiveObject
{
    private readonly CollectionPlannerViewModel _planner;
    private bool _isMember;

    public PlannerMembershipToggle(CollectionPlannerViewModel planner, PlannerStoryViewModel story,
        PlannerColumnViewModel column, bool isMember)
    {
        _planner = planner;
        Story = story;
        Column = column;
        _isMember = isMember;
    }

    public PlannerStoryViewModel Story { get; }
    public PlannerColumnViewModel Column { get; }
    public string Title => Column.Title;
    public int ColourIndex => Column.ColourIndex;

    public bool IsMember
    {
        get => _isMember;
        set
        {
            if (_isMember == value)
                return;
            this.RaiseAndSetIfChanged(ref _isMember, value);
            _ = value
                ? _planner.AddAsync(Story, Column, Column.Entries.Count)
                : _planner.RemoveAsync(Story, Column);
        }
    }
}

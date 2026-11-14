using System;

using System.Collections.Generic;

using System.Linq;

using Hymnal.Core.Models;

using ReactiveUI;



namespace Hymnal.ViewModels;



public enum CardDisplaySize
{
    Tiny,
    Medium,
    Large,
    List
}



public enum CorkboardItemKind

{

    PartDivider,

    EmptyPartHint,

    ChapterCard,

    ExcludedChapterCard,

    InlineCreate

}



/// <summary>

/// Discriminated corkboard row wrapper for the mixed set of part dividers,

/// empty-part hints, and chapter cards.

/// </summary>

public abstract class CorkboardItemViewModel : ViewModelBase, IDisposable

{

    public abstract CorkboardItemKind Kind { get; }



    public virtual bool IsSelectableCard => false;



    public virtual string Title => string.Empty;



    public virtual string RelativePath => string.Empty;



    public virtual bool IsMissing => false;



    public virtual CardViewModel? Card => null;



    public virtual string OwningPartPath => string.Empty;



    public virtual bool IsVisibleOnBoard => true;

    private string _noun = "Chapter";
    /// <summary>
    /// The profile's noun for what this item counts or creates: the unit for a top-level card, the subunit
    /// for a Part and what goes inside it (Chapter in a novel; Story or Scene in an anthology).
    /// </summary>
    public string Noun
    {
        get => _noun;
        internal set
        {
            if (_noun == value)
                return;
            this.RaiseAndSetIfChanged(ref _noun, value);
            OnNounChanged();
        }
    }

    /// <summary>Raises change notifications for text built from <see cref="Noun"/>.</summary>
    protected virtual void OnNounChanged() { }

    /// <summary>Lower-case plural of <paramref name="noun"/>: "scenes", "stories".</summary>
    internal static string Plural(string noun)
    {
        var lower = noun.ToLowerInvariant();
        return lower.EndsWith('y') && lower.Length > 1 && !"aeiou".Contains(lower[^2])
            ? lower[..^1] + "ies"
            : lower + "s";
    }

    private bool _showsBeatsAction;
    /// <summary>Anthology: true when the item's story (itself or its Part) is in the book, so Beats applies.</summary>
    public bool ShowsBeatsAction
    {
        get => _showsBeatsAction;
        internal set => this.RaiseAndSetIfChanged(ref _showsBeatsAction, value);
    }



    public static IReadOnlyList<CorkboardItemViewModel> Project(

        IReadOnlyList<ChapterViewModel> chapters,

        IReadOnlyDictionary<string, bool> partExpandedState,

        IReadOnlyList<OrphanFileInfo> orphanFiles,

        bool showExcludedFiles)

    {

        var items = new List<CorkboardItemViewModel>();

        PartDividerItemViewModel? currentPart = null;

        var isFirstPart = true;

        var orphansByFolder = GroupOrphansByFolder(orphanFiles);

        var excludedWorkspacePaths = chapters
            .Where(chapter => chapter.Node.Kind == NodeKind.Chapter && chapter.Node.IsExcluded)
            .Select(chapter => chapter.Node.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);



        for (var i = 0; i < chapters.Count; i++)

        {

            var chapter = chapters[i];



            if (chapter.Node.Kind == NodeKind.Part)

            {

                if (currentPart != null)

                    AppendExcludedCards(items, currentPart, orphansByFolder, excludedWorkspacePaths, showExcludedFiles);



                var partPath = chapter.Node.RelativePath;

                var chapterCount = CountChaptersInPart(chapters, i);

                var isExpanded = partExpandedState.TryGetValue(partPath, out var expanded)

                    ? expanded

                    : true;



                currentPart = new PartDividerItemViewModel(chapter, chapterCount, isFirstPart, isExpanded);

                items.Add(currentPart);

                isFirstPart = false;



                if (chapterCount == 0)

                    items.Add(new EmptyPartHintItemViewModel(chapter, currentPart));



                continue;

            }



            if (chapter.Node.Kind == NodeKind.Chapter)

            {

                if (chapter.Node.IsExcluded)

                {

                    excludedWorkspacePaths.Add(chapter.Node.RelativePath);



                    if (showExcludedFiles)

                        items.Add(new ExcludedChapterCardItemViewModel(ToExcludedOrphan(chapter), currentPart));



                    continue;

                }



                items.Add(new ChapterCardItemViewModel(new CardViewModel(chapter), currentPart));

            }

        }



        if (currentPart != null)

            AppendExcludedCards(items, currentPart, orphansByFolder, excludedWorkspacePaths, showExcludedFiles);



        if (showExcludedFiles && orphansByFolder.TryGetValue(string.Empty, out var rootOrphans))

        {

            foreach (var orphan in rootOrphans)

            {

                if (excludedWorkspacePaths.Contains(orphan.RelativePath))

                    continue;



                items.Add(new ExcludedChapterCardItemViewModel(orphan, null));

            }

        }



        return items;

    }



    private static Dictionary<string, List<OrphanFileInfo>> GroupOrphansByFolder(

        IReadOnlyList<OrphanFileInfo> orphanFiles)

    {

        var grouped = new Dictionary<string, List<OrphanFileInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var orphan in orphanFiles)

        {

            var key = orphan.DetectedPartFolder ?? string.Empty;

            if (!grouped.TryGetValue(key, out var list))

            {

                list = new List<OrphanFileInfo>();

                grouped[key] = list;

            }



            list.Add(orphan);

        }



        return grouped;

    }



    private static void AppendExcludedCards(

        ICollection<CorkboardItemViewModel> items,

        PartDividerItemViewModel part,

        IReadOnlyDictionary<string, List<OrphanFileInfo>> orphansByFolder,

        ISet<string> excludedWorkspacePaths,

        bool showExcludedFiles)

    {

        if (!showExcludedFiles)

            return;



        var folder = GetPartFolderKey(part.RelativePath);

        if (!orphansByFolder.TryGetValue(folder, out var orphans))

            return;



        foreach (var orphan in orphans)

        {

            if (excludedWorkspacePaths.Contains(orphan.RelativePath))

                continue;



            items.Add(new ExcludedChapterCardItemViewModel(orphan, part));

        }

    }



    private static OrphanFileInfo ToExcludedOrphan(ChapterViewModel chapter)

        => new(

            chapter.Node.RelativePath,

            chapter.Node.Title,

            DetectPartFolder(chapter.Node.RelativePath));



    private static string? DetectPartFolder(string relativePath)

    {

        var normalized = relativePath.Replace('\\', '/');

        var slashIndex = normalized.IndexOf('/');

        if (slashIndex <= 0)

            return null;



        return normalized[..slashIndex];

    }



    private static string GetPartFolderKey(string partRelativePath)

    {

        var normalized = partRelativePath.Replace('\\', '/');

        var slashIndex = normalized.IndexOf('/');

        return slashIndex > 0 ? normalized[..slashIndex] : string.Empty;

    }



    private static int CountChaptersInPart(IReadOnlyList<ChapterViewModel> chapters, int partIndex)

    {

        var count = 0;

        for (var j = partIndex + 1; j < chapters.Count; j++)

        {

            if (chapters[j].Node.Kind == NodeKind.Part)

                break;



            if (chapters[j].Node.Kind == NodeKind.Chapter)

                count++;

        }



        return count;

    }



    public static void DisposeItems(IEnumerable<CorkboardItemViewModel> items)

    {

        foreach (var item in items)

            item.Dispose();

    }



    private bool _disposed;



    public virtual void Dispose()

    {

        if (_disposed)

            return;



        _disposed = true;

        Disposables.Dispose();

    }

}



public abstract class PartOwnedCorkboardItemViewModel : CorkboardItemViewModel

{

    private bool _isVisibleOnBoard = true;



    public override string OwningPartPath { get; }



    public override bool IsVisibleOnBoard => _isVisibleOnBoard;



    protected PartOwnedCorkboardItemViewModel(string owningPartPath, PartDividerItemViewModel? owningPart)

    {

        OwningPartPath = owningPartPath;

        AttachToPart(owningPart);

    }



    private void SetVisibleOnBoard(bool visible) =>
        this.RaiseAndSetIfChanged(ref _isVisibleOnBoard, visible, nameof(IsVisibleOnBoard));

    protected void AttachToPart(PartDividerItemViewModel? owningPart)

    {

        if (owningPart is null)
        {
            SetVisibleOnBoard(true);
            return;
        }

        SetVisibleOnBoard(owningPart.IsExpanded);
        Disposables.Add(
            owningPart.WhenAnyValue(x => x.IsExpanded)
                .Subscribe(SetVisibleOnBoard));

    }

}



public sealed class PartDividerItemViewModel : CorkboardItemViewModel

{

    public ChapterViewModel Part { get; }



    public int ChapterCount { get; }



    public bool IsFirstPart { get; }



    public string ChapterCountDisplay =>
        ChapterCount == 1 ? $"1 {Noun.ToLowerInvariant()}" : $"{ChapterCount} {Plural(Noun)}";

    /// <summary>Tooltip for the header, which shows or hides what is in the Part.</summary>
    public string ToggleTip => $"Click to show or hide the {Plural(Noun)} in {Title}";

    /// <summary>Tooltip for the header's + button.</summary>
    public string AddTip => $"Add a {Noun.ToLowerInvariant()} to {Title}";

    protected override void OnNounChanged()
    {
        this.RaisePropertyChanged(nameof(ChapterCountDisplay));
        this.RaisePropertyChanged(nameof(ChapterCountInParens));
        this.RaisePropertyChanged(nameof(ToggleTip));
        this.RaisePropertyChanged(nameof(AddTip));
    }

    public string ChapterCountInParens => $"({ChapterCountDisplay})";



    public string ChevronGlyph => IsExpanded ? "\u25BE" : "\u25B8";



    private bool _isExpanded = true;

    public bool IsExpanded

    {

        get => _isExpanded;

        set
        {
            var old = _isExpanded;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            if (old != _isExpanded)
                this.RaisePropertyChanged(nameof(ChevronGlyph));
        }
    }



    public override CorkboardItemKind Kind => CorkboardItemKind.PartDivider;



    public override string Title => Part.Node.Title;



    public override string RelativePath => Part.Node.RelativePath;



    public PartDividerItemViewModel(

        ChapterViewModel part,

        int chapterCount,

        bool isFirstPart,

        bool isExpanded)

    {

        Part = part;

        ChapterCount = chapterCount;

        IsFirstPart = isFirstPart;

        _isExpanded = isExpanded;

    }

}



public sealed class EmptyPartHintItemViewModel : PartOwnedCorkboardItemViewModel

{

    public ChapterViewModel Part { get; }



    public string HintText => $"No {Plural(Noun)} in {Part.Node.Title}";

    protected override void OnNounChanged() => this.RaisePropertyChanged(nameof(HintText));



    public override CorkboardItemKind Kind => CorkboardItemKind.EmptyPartHint;



    public override string Title => Part.Node.Title;



    public override string RelativePath => Part.Node.RelativePath;



    public EmptyPartHintItemViewModel(ChapterViewModel part, PartDividerItemViewModel? owningPart)

        : base(owningPart?.RelativePath ?? string.Empty, owningPart)

    {

        Part = part;

    }

}



public sealed class ChapterCardItemViewModel : PartOwnedCorkboardItemViewModel

{

    public override CorkboardItemKind Kind => CorkboardItemKind.ChapterCard;



    public override bool IsSelectableCard => true;



    public override string Title => Card.Title;



    public override string RelativePath => Card.RelativePath;



    public override bool IsMissing => Card.IsMissing;



    public override CardViewModel Card { get; }



    public ChapterCardItemViewModel(CardViewModel card, PartDividerItemViewModel? owningPart)

        : base(owningPart?.RelativePath ?? string.Empty, owningPart)

    {

        Card = card;

    }



    public override void Dispose()

    {

        Card.Dispose();

        base.Dispose();

    }

}



public sealed class ExcludedChapterCardItemViewModel : PartOwnedCorkboardItemViewModel

{

    public OrphanFileInfo Orphan { get; }



    public override CorkboardItemKind Kind => CorkboardItemKind.ExcludedChapterCard;



    public override string Title => Orphan.Title;



    public override string RelativePath => Orphan.RelativePath;



    public string ExcludedLabel => "(excluded)";



    public ExcludedChapterCardItemViewModel(OrphanFileInfo orphan, PartDividerItemViewModel? owningPart)

        : base(owningPart?.RelativePath ?? string.Empty, owningPart)

    {

        Orphan = orphan;

    }

}



/// <summary>
/// Transient placeholder item that renders an inline TextBox for fast chapter creation.
/// Inserted into the corkboard Items list by <see cref="CorkboardViewModel.BeginInlineCreate"/>
/// and removed on commit or cancel.
/// </summary>
public sealed class InlineCreateItemViewModel : PartOwnedCorkboardItemViewModel

{

    private string _titleText = string.Empty;

    public string TitleText

    {

        get => _titleText;

        set => this.RaiseAndSetIfChanged(ref _titleText, value);

    }



    public int BookTxtInsertIndex { get; }

    public string? PartPath { get; }

    /// <summary>The card's caption: "NEW STORY", "NEW SCENE".</summary>
    public string Heading => $"NEW {Noun.ToUpperInvariant()}";

    public string TitlePlaceholder => $"{Noun} title...";

    protected override void OnNounChanged()
    {
        this.RaisePropertyChanged(nameof(Heading));
        this.RaisePropertyChanged(nameof(TitlePlaceholder));
    }



    public override CorkboardItemKind Kind => CorkboardItemKind.InlineCreate;



    public InlineCreateItemViewModel(int insertIndex, string? partPath, PartDividerItemViewModel? owningPart)

        : base(owningPart?.RelativePath ?? string.Empty, owningPart)

    {

        BookTxtInsertIndex = insertIndex;

        PartPath = partPath;

    }

}



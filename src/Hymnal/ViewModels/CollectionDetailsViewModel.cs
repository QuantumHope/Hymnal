using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using DynamicData;
using DynamicData.Binding;
using Hymnal.Core.Models;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Hymnal.ViewModels;

/// <summary>One row of the collection's contents: a story (anthology) or a part/chapter (novel, short story).</summary>
public sealed record CollectionDetailsRow(
    ChapterViewModel Node,
    string Number,
    string Title,
    string Noun,
    string? Detail,
    ChapterStatus Status,
    string StatusLabel,
    string WordsText,
    bool IsDivider,
    bool IsIndented)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public string NounLabel => Noun.ToUpperInvariant();
}

/// <summary>Another collection of the workspace, shown as a chip that opens it.</summary>
public sealed record CollectionChip(string Slug, string Title, string CountText);

/// <summary>A status and how many of the collection's units are in it.</summary>
public sealed record CollectionStatusCount(ChapterStatus Status, string Label, int Count)
{
    public string Text => $"{Label} {Count}";
}

/// <summary>
/// Plan's Details tab: the open collection (book, story) at a glance — its title, renamed in place, its size and
/// progress, what it holds in order, and the workspace's other collections. Every project kind has one.
/// </summary>
public sealed class CollectionDetailsViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly CollectionsViewModel? _collections;
    private readonly Subject<ChapterViewModel> _openRequested = new();

    public CollectionDetailsViewModel(
        WorkspaceViewModel workspace,
        CollectionsViewModel? collections = null,
        IScheduler? scheduler = null,
        SubmissionExportViewModel? export = null)
    {
        _workspace = workspace;
        _collections = collections;
        Export = export;

        BeginRenameCommand = ReactiveCommand.Create(BeginRename, this.WhenAnyValue(x => x.CanRename));
        CommitRenameCommand = ReactiveCommand.CreateFromTask(CommitRenameAsync);
        CancelRenameCommand = ReactiveCommand.Create(() => { IsEditingTitle = false; });
        OpenRowCommand = ReactiveCommand.Create<CollectionDetailsRow>(row => OpenRow(row));
        OpenCollectionCommand = ReactiveCommand.CreateFromTask<string>(OpenCollectionAsync);

        // While shown, follow the book: titles, statuses, word counts and the list itself.
        Disposables.Add(
            Observable.Merge(
                    workspace.Nodes.ToObservableChangeSet()
                        .AutoRefresh(n => n.Node)
                        .AutoRefresh(n => n.Status)
                        .AutoRefresh(n => n.EffectiveWordCount)
                        .Select(_ => Unit.Default),
                    workspace.WhenAnyValue(x => x.Profile, x => x.CollectionDisplayName, x => x.HasWorkspace)
                        .Select(_ => Unit.Default))
                .Throttle(TimeSpan.FromMilliseconds(120), scheduler ?? AvaloniaScheduler.Instance)
                .ObserveOn(scheduler ?? AvaloniaScheduler.Instance)
                .Where(_ => IsActive)
                .Subscribe(_ => Rebuild()));

        if (collections != null)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => { if (IsActive) RebuildChips(); };
            collections.Collections.CollectionChanged += handler;
            Disposables.Add(System.Reactive.Disposables.Disposable.Create(
                () => collections.Collections.CollectionChanged -= handler));
        }
    }

    /// <summary>Set by Plan while the Details tab shows; the contents are only kept current while it is.</summary>
    public bool IsActive { get; set; }

    /// <summary>Asks to open a file in Write (a story with scenes opens its first scene).</summary>
    public IObservable<ChapterViewModel> OpenRequested => _openRequested.AsObservable();

    // ── Header ────────────────────────────────────────────────────────────────

    private string _title = "";
    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    private bool _isNamed;
    /// <summary>False while the collection has no title of its own (the title shown is the project's noun).</summary>
    public bool IsNamed
    {
        get => _isNamed;
        private set => this.RaiseAndSetIfChanged(ref _isNamed, value);
    }

    private string _nounLabel = "";
    /// <summary>BOOK, STORY or COLLECTION.</summary>
    public string NounLabel
    {
        get => _nounLabel;
        private set => this.RaiseAndSetIfChanged(ref _nounLabel, value);
    }

    private string _summary = "";
    /// <summary>"7 stories · 42,180 words".</summary>
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    private IReadOnlyList<CollectionStatusCount> _statusCounts = [];
    public IReadOnlyList<CollectionStatusCount> StatusCounts
    {
        get => _statusCounts;
        private set => this.RaiseAndSetIfChanged(ref _statusCounts, value);
    }

    private string _contentsTitle = "";
    /// <summary>"STORIES, IN ORDER", "CHAPTERS, IN ORDER".</summary>
    public string ContentsTitle
    {
        get => _contentsTitle;
        private set => this.RaiseAndSetIfChanged(ref _contentsTitle, value);
    }

    private IReadOnlyList<CollectionDetailsRow> _rows = [];
    public IReadOnlyList<CollectionDetailsRow> Rows
    {
        get => _rows;
        private set
        {
            this.RaiseAndSetIfChanged(ref _rows, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => Rows.Count == 0;

    private string _emptyText = "";
    public string EmptyText
    {
        get => _emptyText;
        private set => this.RaiseAndSetIfChanged(ref _emptyText, value);
    }

    // ── Renaming ──────────────────────────────────────────────────────────────

    public bool CanRename => _collections != null && _workspace.HasWorkspace;

    private bool _isEditingTitle;
    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        set => this.RaiseAndSetIfChanged(ref _isEditingTitle, value);
    }

    private string _editTitle = "";
    public string EditTitle
    {
        get => _editTitle;
        set => this.RaiseAndSetIfChanged(ref _editTitle, value ?? "");
    }

    public ReactiveCommand<Unit, Unit> BeginRenameCommand { get; }
    public ReactiveCommand<Unit, Unit> CommitRenameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRenameCommand { get; }

    private void BeginRename()
    {
        EditTitle = IsNamed ? Title : _collections?.DefaultTitleForOpen ?? "";
        IsEditingTitle = true;
    }

    private async Task CommitRenameAsync()
    {
        if (!IsEditingTitle)
            return;
        var title = EditTitle.Trim();
        IsEditingTitle = false;
        if (_collections == null || title.Length == 0 || (IsNamed && title == Title))
            return;
        if (await _collections.RenameOpenAsync(title))
            Rebuild();
    }

    // ── Other collections ─────────────────────────────────────────────────────

    private IReadOnlyList<CollectionChip> _otherCollections = [];
    public IReadOnlyList<CollectionChip> OtherCollections
    {
        get => _otherCollections;
        private set
        {
            this.RaiseAndSetIfChanged(ref _otherCollections, value);
            this.RaisePropertyChanged(nameof(HasOtherCollections));
        }
    }

    public bool HasOtherCollections => OtherCollections.Count > 0;

    /// <summary>Submission export, for the Details tab's Export button; null when the app runs without it.</summary>
    public SubmissionExportViewModel? Export { get; }

    /// <summary>A Short Story exports as a whole from here; an anthology's stories export from their own menus.</summary>
    public bool CanExportStory => Export?.IsAvailable == true && _workspace.Profile.Kind == ProjectKind.Story;

    /// <summary>The collections the workspace keeps can be published to LeanPub.</summary>
    public bool CanPublish => _collections != null && _workspace.HasWorkspace;

    /// <summary>True when this workspace can hold several collections (an anthology with Book.txt at its root).</summary>
    public bool SupportsMultiple => _collections?.SupportsMultiple == true;

    public ReactiveCommand<CollectionDetailsRow, Unit> OpenRowCommand { get; }
    public ReactiveCommand<string, Unit> OpenCollectionCommand { get; }

    private async Task OpenCollectionAsync(string slug)
    {
        if (_collections != null && await _collections.OpenAsync(slug))
            Rebuild();
    }

    private void OpenRow(CollectionDetailsRow row)
    {
        var target = row.Node;
        if (target.Node.Kind == NodeKind.Part)
        {
            var nodes = _workspace.Nodes;
            target = StoryStructure.StoryScenes(nodes, target).FirstOrDefault()!;
            if (target == null)
                return;
        }
        if (target.Node.IsMissing)
            return;
        _openRequested.OnNext(target);
    }

    // ── Building ──────────────────────────────────────────────────────────────

    /// <summary>Reads the collection again; Plan calls it when the tab is shown.</summary>
    public void Rebuild()
    {
        var profile = _workspace.Profile;
        IsNamed = !string.IsNullOrWhiteSpace(_workspace.CollectionTitle);
        Title = _workspace.CollectionDisplayName;
        NounLabel = profile.CollectionNoun.ToUpperInvariant();
        this.RaisePropertyChanged(nameof(CanRename));
        this.RaisePropertyChanged(nameof(SupportsMultiple));
        this.RaisePropertyChanged(nameof(CanExportStory));
        this.RaisePropertyChanged(nameof(CanPublish));

        var nodes = _workspace.Nodes.Where(n => !n.Node.IsExcluded).ToList();
        var rows = new List<CollectionDetailsRow>();
        List<ChapterViewModel> units;
        if (profile.Kind == ProjectKind.Anthology)
        {
            // A story is a divider (told in scenes) or a file before any divider.
            units = [];
            var insidePart = false;
            foreach (var node in nodes)
            {
                if (node.Node.Kind == NodeKind.Part)
                    insidePart = true;
                else if (insidePart)
                    continue;
                units.Add(node);
            }
            var number = 0;
            foreach (var story in units)
            {
                number++;
                string? detail = null;
                if (story.Node.Kind == NodeKind.Part)
                {
                    var scenes = StoryStructure.ListedStoryScenes(_workspace.Nodes, story).Count;
                    detail = scenes == 1 ? "1 scene" : $"{scenes} scenes";
                }
                else if (story.Node.IsMissing)
                {
                    detail = "Missing file";
                }
                rows.Add(Row(story, number.ToString(CultureInfo.CurrentCulture), profile.UnitNoun, detail,
                    divider: false, indented: false));
            }
        }
        else
        {
            units = nodes.Where(n => n.Node.Kind == NodeKind.Chapter).ToList();
            var number = 0;
            var insidePart = false;
            foreach (var node in nodes)
            {
                if (node.Node.Kind == NodeKind.Part)
                {
                    insidePart = true;
                    rows.Add(Row(node, "", profile.PartNoun, null, divider: true, indented: false));
                    continue;
                }
                number++;
                rows.Add(Row(node, number.ToString(CultureInfo.CurrentCulture), profile.NounFor(NodeKind.Chapter, insidePart),
                    node.Node.IsMissing ? "Missing file" : null, divider: false, indented: insidePart));
            }
        }
        Rows = rows;

        var unitWord = profile.Kind == ProjectKind.Anthology
            ? profile.UnitNoun
            : profile.NounFor(NodeKind.Chapter, insidePart: nodes.Any(n => n.Node.Kind == NodeKind.Part));
        var words = units.Sum(u => u.EffectiveWordCount);
        Summary = units.Count == 0
            ? $"Nothing in this {profile.CollectionNoun.ToLowerInvariant()} yet"
            : $"{Count(units.Count, unitWord)} · {words.ToString("N0", CultureInfo.CurrentCulture)} words";
        ContentsTitle = $"{(profile.Kind == ProjectKind.Anthology ? profile.UnitPlural : "Contents")}, in order".ToUpperInvariant();
        EmptyText = _workspace.HasWorkspace
            ? $"Add a {unitWord.ToLowerInvariant()} from Write's sidebar (+) to start this {profile.CollectionNoun.ToLowerInvariant()}."
            : "Open a workspace to see its details.";

        StatusCounts = profile.VisibleStatuses
            .Select(option => new CollectionStatusCount(option.Value, option.Label, units.Count(u => u.Status == option.Value)))
            .Where(c => c.Count > 0)
            .ToList();

        RebuildChips();
    }

    private void RebuildChips()
    {
        this.RaisePropertyChanged(nameof(SupportsMultiple));
        OtherCollections = _collections == null
            ? []
            : _collections.Collections
                .Where(c => !c.IsActive)
                .Select(c => new CollectionChip(c.Slug, c.Title, Count(c.EntryCount, "entry", "entries")))
                .ToList();
    }

    private static CollectionDetailsRow Row(ChapterViewModel node, string number, string noun, string? detail,
        bool divider, bool indented) =>
        new(node, number, node.Node.Title, noun, detail, node.Status, node.StatusLabel,
            node.EffectiveWordCount.ToString("N0", CultureInfo.CurrentCulture), divider, indented);

    private static string Count(int n, string singular, string? plural = null)
    {
        var word = n == 1 ? singular : plural ?? Plural(singular);
        return $"{n.ToString("N0", CultureInfo.CurrentCulture)} {word.ToLowerInvariant()}";
    }

    private static string Plural(string noun) =>
        noun.EndsWith('y') && !noun.EndsWith("ey", StringComparison.OrdinalIgnoreCase) ? noun[..^1] + "ies" : noun + "s";

    public void Dispose()
    {
        _openRequested.OnCompleted();
        _openRequested.Dispose();
        Disposables.Dispose();
    }
}

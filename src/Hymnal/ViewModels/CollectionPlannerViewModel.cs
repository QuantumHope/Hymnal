using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Plan mode for an anthology: the story planner. Stories live in a library of their own; collections are columns
/// that each order some of them, so a story can be in no collection (a hollow ring) or in several (a dot per
/// collection, in that collection's colour). Dragging a story in from the library adds it, between columns moves it
/// (Ctrl copies), within a column reorders, and back to the library takes it out of that collection; the story panel's
/// checkboxes do the same explicitly. The open collection's list is the workspace's Book.txt, so a change to it saves
/// pending edits first and reloads the workspace after.
/// </summary>
public sealed class CollectionPlannerViewModel : ViewModelBase
{
    /// <summary>Collections are coloured from the theme's chart series (<c>Cora.Viz.Series1</c>…<c>7</c>).</summary>
    public const int ColourCount = 7;

    private readonly WorkspaceViewModel _workspace;
    private readonly CollectionPlanService _planner;
    private readonly ChapterRegistryService _registry;
    private readonly INotificationService _notifications;
    private readonly CollectionsViewModel? _collections;
    private readonly Func<Task<bool>>? _flushEdits;
    private readonly EditorViewModel? _editor;
    private readonly Subject<string> _beatsRequested = new();
    private readonly Subject<ChapterViewModel> _openChapterRequested = new();

    private PlannerStoryViewModel? _selected;
    private bool _showUnshelvedOnly;
    private bool _isBusy;
    private int _loadVersion;

    public CollectionPlannerViewModel(
        WorkspaceViewModel workspace,
        CollectionPlanService planner,
        ChapterRegistryService registry,
        INotificationService notifications,
        CollectionsViewModel? collections = null,
        Func<Task<bool>>? flushEdits = null,
        EditorViewModel? editor = null)
    {
        _workspace = workspace;
        _planner = planner;
        _registry = registry;
        _notifications = notifications;
        _collections = collections;
        _flushEdits = flushEdits;
        _editor = editor;
        Disposables.Add(_beatsRequested);
        Disposables.Add(_openChapterRequested);
    }

    public ObservableCollection<PlannerStoryViewModel> Library { get; } = new();
    public ObservableCollection<PlannerStoryViewModel> VisibleLibrary { get; } = new();
    public ObservableCollection<PlannerColumnViewModel> Columns { get; } = new();
    public ObservableCollection<PlannerMembershipToggle> Toggles { get; } = new();
    public ObservableCollection<string> SelectedBeatTitles { get; } = new();

    /// <summary>The story panel's story; selecting it highlights its entries in every column.</summary>
    public PlannerStoryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
                return;
            if (_selected != null)
                _selected.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (value != null)
                value.IsSelected = true;
            RebuildSelection();
        }
    }

    public bool HasSelection => Selected != null;
    public bool SelectedIsInOpen => Selected != null && OpenColumn?.Entries.Any(e => e.Story == Selected) == true;
    public bool CanPlanSelectedBeats => Selected?.Uuid != null && BeatBoardService.IsValidBoardKey(Selected.Uuid);

    /// <summary>Library filter: only the stories in no collection.</summary>
    public bool ShowUnshelvedOnly
    {
        get => _showUnshelvedOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _showUnshelvedOnly, value);
            RebuildVisibleLibrary();
        }
    }

    public string LibraryCountText => Library.Count == 1 ? "1 story" : $"{Library.Count} stories";
    public int UnshelvedCount => Library.Count(s => s.IsUnshelved);
    public string UnshelvedText => $"In no collection ({UnshelvedCount})";

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>Plan this story's beats (its registry id); Plan mode drills into the board.</summary>
    public IObservable<string> BeatsRequested => _beatsRequested.AsObservable();

    /// <summary>Write this story: the chapter to open in Write (its first scene for a multi-scene story).</summary>
    public IObservable<ChapterViewModel> OpenChapterRequested => _openChapterRequested.AsObservable();

    private PlannerColumnViewModel? OpenColumn => Columns.FirstOrDefault(c => c.IsOpen);

    // ── Loading ───────────────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        if (!_workspace.HasWorkspace || _workspace.Profile.Kind != ProjectKind.Anthology)
        {
            Apply([], []);
            return;
        }

        var root = _workspace.WorkspaceRoot;
        var loaded = await _planner.LoadAsync(root);
        if (version != _loadVersion)
            return;
        if (!loaded.IsSuccess)
        {
            _notifications.ShowError(loaded.Error!);
            return;
        }
        var plan = loaded.Value!;

        var uuids = await UuidsByPathAsync(root);
        var refs = plan.Stories
            .Where(s => uuids.ContainsKey(s.Key))
            .Select(s => new StoryRef(uuids[s.Key], s.Key, Path.Combine(root, s.Key)))
            .ToList();
        Dictionary<string, PhaseData> phases;
        try
        {
            phases = refs.Count == 0 ? [] : await _workspace.MetadataStore.LoadPhaseDataAsync(root, refs);
        }
        catch (Exception)
        {
            phases = [];
        }

        var profile = _workspace.Profile;
        var stories = new List<PlannerStoryViewModel>();
        foreach (var story in plan.Stories)
        {
            var uuid = uuids.GetValueOrDefault(story.Key);
            // A registered story with nothing saved yet shows the default status, as the sidebar does.
            ChapterStatus? status = uuid == null ? null
                : phases.TryGetValue(uuid, out var phase) ? phase.Status : profile.DefaultStatus;
            var vm = new PlannerStoryViewModel(story, uuid, status, status is { } s ? profile.LabelFor(s) : "");
            if (uuid != null && BeatBoardService.IsValidBoardKey(uuid))
            {
                var beats = await _workspace.MetadataStore.LoadBeatsAsync(root, uuid);
                if (beats.IsSuccess)
                {
                    vm.BeatCount = beats.Value!.Count;
                    vm.BeatTitles = beats.Value!.Select(b => b.Title).ToList();
                }
            }
            stories.Add(vm);
        }
        if (version != _loadVersion)
            return;
        Apply(stories, plan.Collections);
    }

    private async Task<Dictionary<string, string>> UuidsByPathAsync(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // A listed (not orphaned) entry wins over an orphaned one for the same path.
            foreach (var entry in (await _registry.LoadAsync(root)).Values.OrderBy(e => e.Orphaned ? 0 : 1))
                if (!string.IsNullOrEmpty(entry.CurrentPath))
                    map[entry.CurrentPath.Replace('\\', '/')] = entry.Uuid;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
        {
            // No registry: stories show without status or beats.
        }
        return map;
    }

    private void Apply(IReadOnlyList<PlannerStoryViewModel> stories, IReadOnlyList<PlannedCollection> collections)
    {
        var selectedKey = Selected?.Key;
        var byKey = stories.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        // Colours follow the collection, not its place: the open one doesn't change colour when another opens.
        var colourOrder = collections
            .OrderBy(c => c.Ref.Slug ?? "", StringComparer.OrdinalIgnoreCase)
            .Select((c, i) => (c, i))
            .ToDictionary(x => x.c, x => x.i % ColourCount + 1);

        Columns.Clear();
        foreach (var collection in collections)
        {
            var column = new PlannerColumnViewModel(collection, colourOrder[collection]);
            var number = 1;
            foreach (var key in collection.StoryKeys)
                if (byKey.TryGetValue(key, out var story))
                {
                    column.Entries.Add(new PlannerEntryViewModel(story, column, number++));
                    story.Dots.Add(new PlannerMembershipDot(collection.Title.Length > 0 ? collection.Title : "Collection", column.ColourIndex));
                }
            Columns.Add(column);
        }

        Library.Clear();
        foreach (var story in stories)
        {
            story.RaiseShelfChanged();
            Library.Add(story);
        }
        RebuildVisibleLibrary();
        this.RaisePropertyChanged(nameof(LibraryCountText));
        this.RaisePropertyChanged(nameof(UnshelvedCount));
        this.RaisePropertyChanged(nameof(UnshelvedText));

        // The old story objects are gone; re-select the same story (by key) among the new ones.
        _selected = null;
        Selected = selectedKey != null && byKey.TryGetValue(selectedKey, out var again) ? again : null;
        this.RaisePropertyChanged(nameof(Selected));
        RebuildSelection();
    }

    private void RebuildVisibleLibrary()
    {
        VisibleLibrary.Clear();
        foreach (var story in Library.Where(s => !ShowUnshelvedOnly || s.IsUnshelved))
            VisibleLibrary.Add(story);
    }

    private void RebuildSelection()
    {
        Toggles.Clear();
        SelectedBeatTitles.Clear();
        if (Selected is { } story)
        {
            foreach (var column in Columns)
                Toggles.Add(new PlannerMembershipToggle(this, story, column, column.Entries.Any(e => e.Story == story)));
            foreach (var title in story.BeatTitles.Take(8))
                SelectedBeatTitles.Add(title);
        }
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(SelectedIsInOpen));
        this.RaisePropertyChanged(nameof(CanPlanSelectedBeats));
    }

    // ── Changes ───────────────────────────────────────────────────────────────

    /// <summary>Adds <paramref name="story"/> to <paramref name="column"/> before its <paramref name="index"/>-th story.</summary>
    public Task<bool> AddAsync(PlannerStoryViewModel story, PlannerColumnViewModel column, int index) =>
        ChangeAsync(column.IsOpen, () => _planner.InsertAsync(_workspace.WorkspaceRoot, column.Ref, story.Story, index),
            $"“{story.Title}” added to {Name(column)}.");

    /// <summary>
    /// Moves <paramref name="story"/> from one column to another (or reorders it within one); with
    /// <paramref name="copy"/> it stays where it was too.
    /// </summary>
    public Task<bool> MoveAsync(PlannerStoryViewModel story, PlannerColumnViewModel from, PlannerColumnViewModel to,
        int index, bool copy)
    {
        var same = CollectionPlanService.SameCollection(from.Ref, to.Ref);
        var root = _workspace.WorkspaceRoot;
        return ChangeAsync(from.IsOpen || to.IsOpen,
            () => copy || same
                ? _planner.InsertAsync(root, to.Ref, story.Story, index)
                : _planner.MoveAsync(root, from.Ref, to.Ref, story.Story, index),
            same ? null : copy ? $"“{story.Title}” is now in {Name(to)} too." : $"“{story.Title}” moved to {Name(to)}.");
    }

    /// <summary>Takes <paramref name="story"/> out of <paramref name="column"/>; it stays in the library.</summary>
    public Task<bool> RemoveAsync(PlannerStoryViewModel story, PlannerColumnViewModel column) =>
        ChangeAsync(column.IsOpen, () => _planner.RemoveAsync(_workspace.WorkspaceRoot, column.Ref, story.Story),
            $"“{story.Title}” taken out of {Name(column)}.");

    private static string Name(PlannerColumnViewModel column) =>
        column.Title.Length > 0 ? $"“{column.Title}”" : "the open collection";

    private async Task<bool> ChangeAsync(bool touchesOpen, Func<Task<Result<Unit>>> change, string? done)
    {
        if (IsBusy)
            return false;
        IsBusy = true;
        try
        {
            if (touchesOpen)
            {
                // The open list is Book.txt: save what is pending before it changes under the workspace.
                if (_flushEdits != null && !await _flushEdits())
                {
                    _notifications.ShowError("Some edits couldn't be saved, so the collection was not changed.");
                    await RefreshAsync();
                    return false;
                }
                if (_editor?.IsDirty == true)
                    await _editor.SaveAsync();
            }

            var result = await change();
            if (!result.IsSuccess)
                _notifications.ShowError(result.Error!);
            else if (done != null)
                _notifications.ShowInfo(done);

            if (touchesOpen)
                await _workspace.ReloadProjectAsync();
            if (_collections != null)
                await _collections.RefreshAsync();
            await RefreshAsync();
            return result.IsSuccess;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Story panel actions ───────────────────────────────────────────────────

    public void PlanBeats(PlannerStoryViewModel? story)
    {
        if (story?.Uuid is { } uuid && BeatBoardService.IsValidBoardKey(uuid))
            _beatsRequested.OnNext(uuid);
    }

    /// <summary>Opens the story in Write: only a story in the open collection is in Write's list.</summary>
    public void Write(PlannerStoryViewModel? story)
    {
        if (story == null)
            return;
        var path = story.Story.IsMultiScene && story.Story.Lines.Count > 1 ? story.Story.Lines[1] : story.Key;
        var node = _workspace.Nodes.FirstOrDefault(n =>
            string.Equals(n.Node.RelativePath, path, StringComparison.OrdinalIgnoreCase) && n.Node.Kind == NodeKind.Chapter);
        if (node != null)
            _openChapterRequested.OnNext(node);
        else
            _notifications.ShowInfo($"Open a collection with “{story.Title}” in it to write it.");
    }
}

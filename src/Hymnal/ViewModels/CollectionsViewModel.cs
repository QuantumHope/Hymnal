using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// The workspace's collections (see <see cref="CollectionService"/>): naming the open one, opening another,
/// adding stories that are in no collection, and publishing to LeanPub. A Novel or Short Story has one book or
/// story, which can be named and published; only an anthology at the workspace root can hold several.
/// </summary>
public sealed class CollectionsViewModel : ViewModelBase
{
    private readonly WorkspaceViewModel _workspace;
    private readonly CollectionService _service;
    private readonly INotificationService _notifications;
    private readonly Func<Task<bool>>? _flushEdits;
    private readonly EditorViewModel? _editor;

    public CollectionsViewModel(
        WorkspaceViewModel workspace,
        CollectionService service,
        INotificationService notifications,
        Func<Task<bool>>? flushEdits = null,
        EditorViewModel? editor = null)
    {
        _editor = editor;
        _workspace = workspace;
        _service = service;
        _notifications = notifications;
        _flushEdits = flushEdits;

        Disposables.Add(workspace.WorkspaceChanged.Subscribe(changed => _ = RefreshAsync()));
    }

    public ObservableCollection<CollectionInfo> Collections { get; } = new();

    /// <summary>True when this workspace can hold several collections.</summary>
    public bool SupportsMultiple =>
        _workspace.HasWorkspace && _workspace.Profile.SupportsMultipleCollections
        && CollectionService.SupportsCollections(_workspace.WorkspaceRoot, _workspace.ManuscriptRoot);

    /// <summary>True when the open collection has a title (and so a folder to keep its list in).</summary>
    public bool IsOpenCollectionNamed => !string.IsNullOrWhiteSpace(_workspace.CollectionTitle);

    private string Root => _workspace.WorkspaceRoot;

    public async Task RefreshAsync()
    {
        if (!_workspace.HasWorkspace)
        {
            Collections.Clear();
            HasSeveral = false;
            return;
        }
        var list = await _service.ListAsync(Root);
        Collections.Clear();
        if (list.IsSuccess)
            foreach (var c in list.Value!)
                Collections.Add(c);
        HasSeveral = Collections.Count > 1;
        await RefreshShelfAsync();
    }

    // ── Collections shelf (Plan) ──────────────────────────────────────────────

    /// <summary>Every collection as a card with its stories, the open one first (Plan's collections shelf).</summary>
    public ObservableCollection<CollectionCard> Cards { get; } = new();

    /// <summary>Story files no collection lists, workspace-relative (the shelf offers to add them to the open one).</summary>
    public ObservableCollection<string> Unassigned { get; } = new();

    private bool _canHoldSeveral;

    /// <summary>True when this workspace can hold several collections, so Plan shows the shelf.</summary>
    public bool CanHoldSeveral
    {
        get => _canHoldSeveral;
        private set => this.RaiseAndSetIfChanged(ref _canHoldSeveral, value);
    }

    private bool _hasUnassigned;
    public bool HasUnassigned
    {
        get => _hasUnassigned;
        private set => this.RaiseAndSetIfChanged(ref _hasUnassigned, value);
    }

    /// <summary>How many story titles a card lists before "+ N more".</summary>
    public const int CardStoryLimit = 6;

    private async Task RefreshShelfAsync()
    {
        var supports = SupportsMultiple;
        var cards = new List<CollectionCard>();
        var unassigned = new List<string>();
        if (supports)
        {
            // An unnamed open collection has no folder yet, so the list doesn't include it; it still gets a card.
            if (!Collections.Any(c => c.IsActive))
                cards.Add(await CardAsync(null, _workspace.CollectionDisplayName, isActive: true, isNamed: false));
            foreach (var c in Collections)
                cards.Add(await CardAsync(c.Slug, c.Title, c.IsActive, isNamed: true));
            var free = await _service.UnassignedStoriesAsync(Root);
            if (free.IsSuccess)
                unassigned.AddRange(free.Value!);
        }

        Cards.Clear();
        foreach (var card in cards)
            Cards.Add(card);
        Unassigned.Clear();
        foreach (var path in unassigned)
            Unassigned.Add(path);
        HasUnassigned = Unassigned.Count > 0;
        CanHoldSeveral = supports;
    }

    private async Task<CollectionCard> CardAsync(string? slug, string title, bool isActive, bool isNamed)
    {
        // The open collection's list is the workspace's Book.txt (preview with no slug); others keep their own.
        var preview = await _service.PreviewAsync(Root, _workspace.ManuscriptRoot, isActive ? null : slug);
        var stories = preview.IsSuccess ? StoryTitles(preview.Value!.Entries) : [];
        return new CollectionCard(slug, title, isActive, isNamed,
            stories.Take(CardStoryLimit).ToList(),
            Math.Max(0, stories.Count - CardStoryLimit),
            stories.Count == 1 ? "1 story" : $"{stories.Count} stories");
    }

    /// <summary>
    /// The stories a collection's list holds, as <see cref="StoryStructure"/> reads an anthology: a file before the
    /// first divider is a story by itself, a divider is a story told in the scenes after it.
    /// </summary>
    internal static IReadOnlyList<string> StoryTitles(IReadOnlyList<PublishEntry> entries)
    {
        var titles = new List<string>();
        var inPart = false;
        foreach (var entry in entries)
        {
            if (entry.IsPart)
            {
                inPart = true;
                titles.Add(entry.Title);
            }
            else if (!inPart)
            {
                titles.Add(entry.Title);
            }
        }
        return titles;
    }

    private bool _hasSeveral;

    /// <summary>True when the workspace holds two or more collections (the collection bar shows them all).</summary>
    public bool HasSeveral
    {
        get => _hasSeveral;
        private set => this.RaiseAndSetIfChanged(ref _hasSeveral, value);
    }

    /// <summary>Names (or renames) the open collection, book or story.</summary>
    public async Task<bool> RenameOpenAsync(string title)
    {
        var result = await _service.SetActiveTitleAsync(Root, title);
        if (!result.IsSuccess)
        {
            _notifications.ShowError(result.Error!);
            return false;
        }
        _workspace.CollectionTitle = title.Trim();
        await RefreshAsync();
        return true;
    }

    /// <summary>Adds an empty collection; returns its slug, or null when it couldn't be created.</summary>
    public async Task<string?> CreateAsync(string title)
    {
        var result = await _service.CreateAsync(Root, title);
        if (!result.IsSuccess)
        {
            _notifications.ShowError(result.Error!);
            return null;
        }
        await RefreshAsync();
        return result.Value;
    }

    /// <summary>
    /// Opens collection <paramref name="slug"/>: pending notes, schedule and beat edits are saved first, then
    /// the lists are swapped and the workspace reloaded on the new one.
    /// </summary>
    public async Task<bool> OpenAsync(string slug)
    {
        if (_flushEdits != null && !await _flushEdits())
        {
            _notifications.ShowError("Some edits couldn't be saved, so the collection was not switched.");
            return false;
        }

        var switched = await _service.SwitchAsync(Root, slug);
        if (!switched.IsSuccess)
        {
            _notifications.ShowError(switched.Error!);
            return false;
        }

        _workspace.ShowWholeCollection();
        var reloaded = await _workspace.ReloadProjectAsync();
        await RefreshAsync();
        return reloaded.IsSuccess;
    }

    /// <summary>Story files that no collection lists.</summary>
    public async Task<IReadOnlyList<string>> UnassignedAsync()
    {
        if (!_workspace.HasWorkspace)
            return [];
        var result = await _service.UnassignedStoriesAsync(Root);
        return result.IsSuccess ? result.Value! : [];
    }

    /// <summary>Adds a story that is in no collection to the end of the open one.</summary>
    public async Task AddToOpenAsync(string relativePath)
    {
        try
        {
            await _workspace.IncludeExistingFileCommand
                .Execute(new IncludeExistingChapterRequest(relativePath, _workspace.GetBookEntryCount()))
                .ToTask();
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"'{relativePath}' couldn't be added: {ex.Message}");
        }
    }

    public async Task<PublishPreview?> PreviewAsync()
    {
        var result = await _service.PreviewAsync(Root, _workspace.ManuscriptRoot);
        if (result.IsSuccess)
            return result.Value;
        _notifications.ShowError(result.Error!);
        return null;
    }

    /// <summary>Copies the open collection into manuscript/ for LeanPub (pending edits are saved first).</summary>
    public async Task<PublishResult?> PublishAsync()
    {
        if (_flushEdits != null)
            await _flushEdits();
        if (_editor?.IsDirty == true)
            await _editor.SaveAsync();

        var result = await _service.PublishAsync(Root, _workspace.ManuscriptRoot);
        if (result.IsSuccess)
            return result.Value;
        _notifications.ShowError(result.Error!);
        return null;
    }
}

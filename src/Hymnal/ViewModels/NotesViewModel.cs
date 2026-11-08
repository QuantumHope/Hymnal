using System;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Manages the Chapter Notes side-panel. Observes the active chapter, loads / clears notes on
/// chapter transitions, and debounce-saves text changes (1 500 ms idle) with chapter-switch
/// safety (CancellationToken reset on each load).
/// <para>
/// Notes are read and written through the workspace's <see cref="IStoryMetadataStore"/>. For an embedded story
/// that is the open file's block, so a notes save is an editor save: it writes any unsaved prose too and leaves
/// the editor clean, but never changes the prose or marks it dirty.
/// </para>
/// </summary>
public class NotesViewModel : ViewModelBase, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly WorkspaceViewModel _workspaceViewModel;

    // ── Backing fields ────────────────────────────────────────────────────────

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            this.RaiseAndSetIfChanged(ref _text, value);
            _pendingText = value;
            _saveSubject.OnNext(value);
        }
    }

    private string? _loadError;
    /// <summary>
    /// Why the shown chapter's notes couldn't be loaded; null when they were. While set the notes are shown empty
    /// and read-only, so neither another chapter's notes nor text typed over unread notes is ever saved for it.
    /// </summary>
    public string? LoadError
    {
        get => _loadError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _loadError, value);
            this.RaisePropertyChanged(nameof(IsReadOnly));
        }
    }

    /// <summary>True while the shown chapter's notes couldn't be loaded (see <see cref="LoadError"/>).</summary>
    public bool IsReadOnly => _loadError is not null;

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private string? _chapterTitle;
    public string? ChapterTitle
    {
        get => _chapterTitle;
        private set => this.RaiseAndSetIfChanged(ref _chapterTitle, value);
    }

    // ── Auto-save plumbing ────────────────────────────────────────────────────

    private readonly Subject<string> _saveSubject = new();

    /// <summary>Text typed but not yet saved (null when nothing is pending); taken by whichever save runs first.</summary>
    private string? _pendingText;

    /// <summary>One notes save at a time, so a flush waits for one already writing.</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>Tracks which chapter node was loaded most-recently (the toggle needs one).</summary>
    private ChapterNode? _loadedNode;

    /// <summary>
    /// The loaded chapter's file as the metadata store sees it, captured on the UI thread with the node; the flush
    /// handler saves to it, and a chapter switch cancels saves for the previous one.
    /// </summary>
    private StoryRef? _loadedStory;

    /// <summary>
    /// Cancelled and replaced every time a new chapter is loaded.  Prevents in-flight saves
    /// from writing after the chapter has already changed.
    /// </summary>
    private CancellationTokenSource _saveCts = new();

    // ── Toggle command ────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public NotesViewModel(
        EditorViewModel editorViewModel,
        WorkspaceViewModel workspaceViewModel,
        INotificationService notificationService,
        IAppSettingsStore settingsStore)
    {
        _workspaceViewModel = workspaceViewModel;
        _notificationService = notificationService;
        _settingsStore = settingsStore;

        try
        {
            _isVisible = false;
        }
        catch
        {
            _isVisible = false;
        }

        // ── Toggle: only when a chapter is active ────────────────────────────
        var hasActiveNode = editorViewModel
            .WhenAnyValue(x => x.ActiveNode)
            .Select(n => n != null);

        ToggleCommand = ReactiveCommand.Create(
            () =>
            {
                if (_loadedNode != null)
                {
                    IsVisible = !IsVisible;
                    _ = PersistNotesVisibilityAsync(IsVisible);
                }
            },
            canExecute: hasActiveNode);

        Disposables.Add(
            ToggleCommand.ThrownExceptions
                .Subscribe(ex => _notificationService.ShowError($"Toggle notes failed: {ex.Message}")));

        // ── Chapter observation ──────────────────────────────────────────────
        Disposables.Add(
            editorViewModel
                .WhenAnyValue(x => x.ActiveNode)
                .Subscribe(node => OnActiveNodeChanged(node)));

        // ── Throttled auto-save ──────────────────────────────────────────────
        Disposables.Add(
            _saveSubject
                .Throttle(TimeSpan.FromMilliseconds(1500), TaskPoolScheduler.Default)
                .Subscribe(text => _ = SavePendingAsync()));
    }

    public async Task RestoreSettingsAsync()
    {
        try
        {
            _isVisible = await _settingsStore.GetAsync<bool?>("notesVisible").ConfigureAwait(false) ?? false;
            this.RaisePropertyChanged(nameof(IsVisible));
        }
        catch
        {
            _isVisible = false;
            this.RaisePropertyChanged(nameof(IsVisible));
        }
    }

    // ── Chapter transition handler ────────────────────────────────────────────

    private void OnActiveNodeChanged(ChapterNode? node)
    {
        // Cancel any pending save for the previous chapter.
        _pendingText = null;
        _saveCts.Cancel();
        _saveCts.Dispose();
        _saveCts = new CancellationTokenSource();

        if (node == null)
        {
            _loadedNode = null;
            _loadedStory = null;
            _text = "";
            this.RaisePropertyChanged(nameof(Text));
            LoadError = null;
            ChapterTitle = null;
            IsVisible = false;
            return;
        }

        ChapterTitle = node.Title;
        _loadedNode = node;
        var story = _loadedStory = _workspaceViewModel.StoryRefFor(node);

        var wasVisible = IsVisible;
        _ = LoadNotesAsync(story, wasVisible);
    }

    private async Task LoadNotesAsync(StoryRef story, bool wasVisible)
    {
        var ct = _saveCts.Token;
        try
        {
            var workspaceRoot = _workspaceViewModel.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot))
                return;

            var content = await _workspaceViewModel.MetadataStore.LoadNotesAsync(workspaceRoot, story).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            // Update Text without triggering auto-save (set backing field directly, then notify).
            _text = content;
            this.RaisePropertyChanged(nameof(Text));
            LoadError = null;

            // Restore panel visibility only if it was shown before the chapter switch.
            IsVisible = wasVisible;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Never leave the previous chapter's notes showing under this chapter's title.
            _text = "";
            this.RaisePropertyChanged(nameof(Text));
            LoadError = $"Notes couldn't be loaded: {ex.Message}";
            _notificationService.ShowError($"Failed to load notes: {ex.Message}");
        }
    }

    // ── Flush save ────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves notes typed but not yet saved (waiting on the debounce) now. True when nothing was pending or the
    /// save succeeded; false (after showing the error) when it failed.
    /// </summary>
    public Task<bool> FlushAsync() => SavePendingAsync();

    private async Task<bool> SavePendingAsync()
    {
        var storyAtSave = _loadedStory;
        var ct = _saveCts.Token;
        if (storyAtSave == null || ct.IsCancellationRequested) return true;

        // A save already writing finishes first; if it fails, its text is pending again and retried here.
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SavePendingLockedAsync(storyAtSave, ct).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> SavePendingLockedAsync(StoryRef storyAtSave, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return true;

        var text = Interlocked.Exchange(ref _pendingText, null);
        if (text == null) return true;

        try
        {
            var workspaceRoot = _workspaceViewModel.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot)) return true;

            await _workspaceViewModel.MetadataStore.SaveNotesAsync(workspaceRoot, storyAtSave, text).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // The chapter changed while saving: its pending text was discarded with it.
            if (ct.IsCancellationRequested) return true;

            // Still unsaved: keep it pending unless newer text has been typed meanwhile.
            Interlocked.CompareExchange(ref _pendingText, text, null);
            _notificationService.ShowError($"Failed to save notes: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets <see cref="IsVisible"/> without requiring an active chapter.
    /// Used by the shell reset-layout command and tests.
    /// </summary>
    public void ApplyVisibility(bool visible)
    {
        _isVisible = visible;
        this.RaisePropertyChanged(nameof(IsVisible));
    }

    /// <summary>Persists the current <see cref="IsVisible"/> value to the settings store.</summary>
    public Task PersistVisibilityAsync() => PersistNotesVisibilityAsync(_isVisible);

    private async Task PersistNotesVisibilityAsync(bool value)
    {
        try
        {
            await _settingsStore.SetAsync("notesVisible", value).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal; preference may not persist across sessions if storage is unavailable.
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disposables.Dispose();
        _saveSubject.OnCompleted();
        _saveSubject.Dispose();
        _saveCts.Cancel();
        _saveCts.Dispose();
    }
}

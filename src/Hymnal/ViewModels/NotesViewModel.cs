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
            _saveSubject.OnNext(value);
        }
    }

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
                .Subscribe(text => _ = FlushSaveAsync(text)));
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
        _saveCts.Cancel();
        _saveCts.Dispose();
        _saveCts = new CancellationTokenSource();

        if (node == null)
        {
            _loadedNode = null;
            _loadedStory = null;
            _text = "";
            this.RaisePropertyChanged(nameof(Text));
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

            // Restore panel visibility only if it was shown before the chapter switch.
            IsVisible = wasVisible;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _notificationService.ShowError($"Failed to load notes: {ex.Message}");
        }
    }

    // ── Flush save ────────────────────────────────────────────────────────────

    private async Task FlushSaveAsync(string text)
    {
        var storyAtSave = _loadedStory;
        if (storyAtSave == null) return;

        var ct = _saveCts.Token;
        if (ct.IsCancellationRequested) return;

        try
        {
            var workspaceRoot = _workspaceViewModel.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot)) return;

            await _workspaceViewModel.MetadataStore.SaveNotesAsync(workspaceRoot, storyAtSave, text).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _notificationService.ShowError($"Failed to save notes: {ex.Message}");
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

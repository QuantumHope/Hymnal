using System;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;
using ReactiveUI.Avalonia;
using CoreUnit = Hymnal.Core.Common.Unit;
using StoryResult = Hymnal.Core.Common.Result<Hymnal.Core.Common.Unit>;

namespace Hymnal.ViewModels;

/// <summary>
/// Single-buffer chapter editor state. One instance per application lifetime (singleton).
/// Manages open chapter content, dirty state, atomic save, FileSystemWatcher, and conflict detection.
/// <para>
/// Chapter files are story files: a trailing <c>&lt;!-- hymnal:v1 … --&gt;</c> metadata block (see
/// <see cref="StoryFileFormat"/>) is split off on load, held beside the buffer (<see cref="Text"/> is prose only)
/// and merged back on save through <see cref="StoryFileWriter.WriteFileAsync"/>, in every project kind. A UTF-8
/// byte order mark is kept out of <see cref="Text"/> and written back. A damaged block is not recognized and so
/// stays visible as prose. Book.txt and arbitrary (research) files are read and saved as plain text.
/// </para>
/// <para>
/// As the writer's <see cref="IOpenStoryBuffer"/>, metadata changes for the open chapter come here and are
/// saved together with any unsaved prose.
/// </para>
/// </summary>
public class EditorViewModel : ViewModelBase, IDisposable, IOpenStoryBuffer
{
    private const char ByteOrderMark = '\uFEFF';

    private readonly IMetadataStore _metadataStore;
    private readonly INotificationService _notificationService;
    private readonly WordCountService _wordCountService;
    private readonly StoryFileWriter _storyWriter;

    /// <summary>
    /// The story file in the buffer: its identity (for <see cref="HoldsFile"/>) and what a save merges back
    /// around <see cref="Text"/>. Immutable and replaced whole, so other threads can read it; null when no
    /// chapter file is open.
    /// </summary>
    private sealed record StoryBuffer(
        string FullPath, string RealPath, EmbeddedStoryMetadata? Metadata, string LineEnding, bool HasByteOrderMark);

    private volatile StoryBuffer? _story;

    // Bumped (on the UI thread) by every load and save, so an external-change read that the buffer has
    // since moved past is dropped instead of applied.
    private int _bufferGeneration;
    private bool _damagedBlockNoticeShown;

    private FileSystemWatcher? _watcher;
    private Timer? _watcherDebounceTimer;
    private bool _disposed;
    private bool _suppressDirtyTracking;
    private bool _isDirtyFlag;

    // ── Active chapter ──────────────────────────────────────────────────────

    private ChapterNode? _activeNode;
    public ChapterNode? ActiveNode
    {
        get => _activeNode;
        private set => this.RaiseAndSetIfChanged(ref _activeNode, value);
    }

    private string? _activeFilePath;
    public string? ActiveFilePath
    {
        get => _activeFilePath;
        private set => this.RaiseAndSetIfChanged(ref _activeFilePath, value);
    }

    // ── Buffer content ──────────────────────────────────────────────────────

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;

            this.RaiseAndSetIfChanged(ref _text, value);
            if (!_suppressDirtyTracking)
                SetDirtyFlag(true);
        }
    }

    private string _originalText = "";
    public string OriginalText
    {
        get => _originalText;
        private set => this.RaiseAndSetIfChanged(ref _originalText, value);
    }

    // ── Derived state (OAPH) ────────────────────────────────────────────────

    public bool IsDirty
    {
        get => _isDirtyFlag;
        private set => this.RaiseAndSetIfChanged(ref _isDirtyFlag, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _canSave;
    public bool CanSave => _canSave.Value;

    private readonly ObservableAsPropertyHelper<bool> _hasActiveChapter;
    public bool HasActiveChapter => _hasActiveChapter.Value;

    private readonly ObservableAsPropertyHelper<bool> _showWorkspacePrompt;
    public bool ShowWorkspacePrompt => _showWorkspacePrompt.Value;

    private readonly ObservableAsPropertyHelper<bool> _showNoChapterPrompt;
    public bool ShowNoChapterPrompt => _showNoChapterPrompt.Value;

    private readonly ObservableAsPropertyHelper<bool> _showNoResearchDocPrompt;
    public bool ShowNoResearchDocPrompt => _showNoResearchDocPrompt.Value;

    private bool _isResearchSurface;
    /// <summary>True when the editor is hosted on the RESEARCH surface (docs only, no chapters).</summary>
    public bool IsResearchSurface
    {
        get => _isResearchSurface;
        set => this.RaiseAndSetIfChanged(ref _isResearchSurface, value);
    }

    private bool _hasWorkspace;
    public bool HasWorkspace
    {
        get => _hasWorkspace;
        set => this.RaiseAndSetIfChanged(ref _hasWorkspace, value);
    }

    // ── Conflict state ──────────────────────────────────────────────────────

    private bool _hasConflict;
    public bool HasConflict
    {
        get => _hasConflict;
        private set => this.RaiseAndSetIfChanged(ref _hasConflict, value);
    }

    private string? _conflictMessage;
    public string? ConflictMessage
    {
        get => _conflictMessage;
        private set => this.RaiseAndSetIfChanged(ref _conflictMessage, value);
    }

    // ── Special editor states ────────────────────────────────────────────────

    /// <summary>True when Book.txt is open in the editor (book line was clicked).</summary>
    private bool _isBookSelected;
    public bool IsBookSelected
    {
        get => _isBookSelected;
        private set => this.RaiseAndSetIfChanged(ref _isBookSelected, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _showEditor;
    /// <summary>True when there is a chapter or Book.txt loaded — controls editor visibility.</summary>
    public bool ShowEditor => _showEditor.Value;

    private readonly ObservableAsPropertyHelper<bool> _showBookTxtWarning;
    /// <summary>True when Book.txt is open; drives the warning banner in EditorView.</summary>
    public bool ShowBookTxtWarning => _showBookTxtWarning.Value;

    /// <summary>True when the selected chapter is referenced in Book.txt but missing on disk.</summary>
    private bool _showMissingChapterPrompt;
    public bool ShowMissingChapterPrompt
    {
        get => _showMissingChapterPrompt;
        private set => this.RaiseAndSetIfChanged(ref _showMissingChapterPrompt, value);
    }

    private readonly ObservableAsPropertyHelper<string?> _missingChapterMessage;
    /// <summary>Formatted warning message shown in the missing-chapter overlay.</summary>
    public string? MissingChapterMessage => _missingChapterMessage.Value;

    // ── Word count ──────────────────────────────────────────────────────────

    private readonly ObservableAsPropertyHelper<int> _liveWordCount;
    public int LiveWordCount => _liveWordCount.Value;

    // ── Saved signal ─────────────────────────────────────────────────────────

    private readonly Subject<Unit> _savedSubject = new();
    /// <summary>Fires a Unit after every successful atomic save.</summary>
    public IObservable<Unit> Saved => _savedSubject.AsObservable();

    // ── Commands ────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>Accept the external version: reload from disk, clear HasConflict.</summary>
    public ReactiveCommand<Unit, Unit> AcceptExternalCommand { get; }

    /// <summary>Keep local edits: clear HasConflict / ConflictMessage only.</summary>
    public ReactiveCommand<Unit, Unit> KeepLocalCommand { get; }

    /// <param name="storyFileWriter">Saves chapter files with their metadata block; the composition root shares
    /// one with the rest of the app and attaches this editor as its buffer. Defaults to a private writer over
    /// <paramref name="metadataStore"/>.</param>
    public EditorViewModel(IMetadataStore metadataStore, INotificationService notificationService,
        WordCountService wordCountService, StoryFileWriter? storyFileWriter = null)
    {
        _metadataStore = metadataStore;
        _notificationService = notificationService;
        _wordCountService = wordCountService;
        _storyWriter = storyFileWriter ?? new StoryFileWriter(metadataStore);

        _canSave = this.WhenAnyValue(x => x.IsDirty, x => x.ActiveFilePath, (dirty, path) => dirty && path != null)
            .ToProperty(this, x => x.CanSave);

        _hasActiveChapter = this.WhenAnyValue(x => x.ActiveNode)
            .Select(node => node != null)
            .ToProperty(this, x => x.HasActiveChapter);

        _showWorkspacePrompt = this.WhenAnyValue(x => x.HasWorkspace)
            .Select(hasWorkspace => !hasWorkspace)
            .ToProperty(this, x => x.ShowWorkspacePrompt);

        _showNoChapterPrompt = this.WhenAnyValue(
                x => x.HasWorkspace, x => x.ActiveNode, x => x.ActiveFilePath, x => x.IsBookSelected,
                x => x.ShowMissingChapterPrompt, x => x.IsResearchSurface,
                (hasWorkspace, node, path, isBook, isMissing, isResearch) =>
                    !isResearch && hasWorkspace && node == null && path == null && !isBook && !isMissing)
            .ToProperty(this, x => x.ShowNoChapterPrompt);

        _showNoResearchDocPrompt = this.WhenAnyValue(
                x => x.HasWorkspace, x => x.ActiveNode, x => x.ActiveFilePath, x => x.IsBookSelected,
                x => x.ShowMissingChapterPrompt, x => x.IsResearchSurface,
                (hasWorkspace, node, path, isBook, isMissing, isResearch) =>
                    isResearch && hasWorkspace && node == null && path == null && !isBook && !isMissing)
            .ToProperty(this, x => x.ShowNoResearchDocPrompt);
        Disposables.Add(_showNoResearchDocPrompt);

        _showEditor = this.WhenAnyValue(x => x.HasActiveChapter, x => x.IsBookSelected, x => x.ActiveFilePath,
                (ch, book, path) => ch || book || path != null)
            .ToProperty(this, x => x.ShowEditor);
        Disposables.Add(_showEditor);

        _showBookTxtWarning = this.WhenAnyValue(x => x.IsBookSelected)
            .ToProperty(this, x => x.ShowBookTxtWarning);
        Disposables.Add(_showBookTxtWarning);

        _missingChapterMessage = this.WhenAnyValue(x => x.ActiveNode)
            .Select(n => n != null
                ? $"\"{n.Title}\" is referenced in Book.txt but the file could not be found on disk."
                : null)
            .ToProperty(this, x => x.MissingChapterMessage);
        Disposables.Add(_missingChapterMessage);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.CanSave));

        AcceptExternalCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (ActiveFilePath == null) return;
            try
            {
                if (_story is { } story)
                {
                    using (await _storyWriter.HoldMetadataChangesAsync(story.FullPath))
                        LoadStoryBuffer(ActiveFilePath, await ReadStoryFileAsync(ActiveFilePath));
                }
                else
                {
                    var content = await File.ReadAllTextAsync(ActiveFilePath);
                    SetBufferContent(content);
                }

                HasConflict = false;
                ConflictMessage = null;
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"Failed to reload file from disk: {ex.Message}");
            }
        });

        KeepLocalCommand = ReactiveCommand.Create(() =>
        {
            HasConflict = false;
            ConflictMessage = null;
        });

        // Register OAPHs for disposal so they unsubscribe cleanly.
        Disposables.Add(_canSave);

        // Live word count: debounced 300 ms on the thread-pool, result marshalled to UI thread.
        _liveWordCount = this.WhenAnyValue(x => x.Text)
            .Throttle(TimeSpan.FromMilliseconds(300), TaskPoolScheduler.Default)
            .Select(t => _wordCountService.CountWords(t))
            .ObserveOn(AvaloniaScheduler.Instance)
            .ToProperty(this, x => x.LiveWordCount, out _liveWordCount);
        Disposables.Add(_liveWordCount);

        // Register watcher teardown with the composite disposable.
        Disposables.Add(Disposable.Create(StopWatcher));
    }

    private void SetBufferContent(string content)
    {
        _bufferGeneration++;
        _suppressDirtyTracking = true;
        try
        {
            Text = content;
            OriginalText = content;
            SetDirtyFlag(false);
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
    }

    private void SetDirtyFlag(bool dirty)
    {
        if (_isDirtyFlag == dirty)
            return;

        IsDirty = dirty;
    }

    // ── Chapter lifecycle ────────────────────────────────────────────────────

    /// <summary>
    /// Opens a chapter: stops any existing watcher, reads and splits the file (Text = prose, metadata block held),
    /// clears dirty, then starts a new FileSystemWatcher. Throws <see cref="IOException"/> when the file cannot be
    /// read or is not valid UTF-8.
    /// </summary>
    public async Task OpenChapterAsync(ChapterNode node, string absolutePath)
    {
        StopWatcher();

        // Holding off metadata changes to the old and new file while the buffer switches means a change
        // pending for the new file is on disk before it is read, and later ones come to this buffer.
        using (await _storyWriter.HoldMetadataChangesAsync(_story?.FullPath, absolutePath))
        {
            var split = await ReadStoryFileAsync(absolutePath);
            _damagedBlockNoticeShown = false;
            LoadStoryBuffer(absolutePath, split);
            ActiveNode = node;
            ActiveFilePath = absolutePath;
        }

        HasConflict = false;
        ConflictMessage = null;
        ShowMissingChapterPrompt = false;
        IsBookSelected = false;

        StartWatcher(absolutePath);
    }

    private async Task<StorySplit> ReadStoryFileAsync(string absolutePath)
    {
        var read = await _storyWriter.ReadFileAsync(absolutePath);
        return read.IsSuccess ? read.Value! : throw new IOException(read.Error);
    }

    /// <summary>Puts a split story file into the buffer: prose (without a byte order mark) and its metadata.</summary>
    private void LoadStoryBuffer(string absolutePath, StorySplit split)
    {
        if (!StoryFileWriter.TryResolvePath(absolutePath, out var full, out var real))
            full = real = absolutePath;

        var hasBom = split.Prose.Length > 0 && split.Prose[0] == ByteOrderMark;
        _story = new StoryBuffer(full, real, split.Metadata, split.LineEnding, hasBom);
        SetBufferContent(hasBom ? split.Prose[1..] : split.Prose);

        if (split.HadMalformedBlock && !_damagedBlockNoticeShown)
        {
            _damagedBlockNoticeShown = true;
            _notificationService.ShowInfo(
                $"'{Path.GetFileName(absolutePath)}' ends with a damaged Hymnal data block, shown as text at the end " +
                "of the story. Fix or delete it; Hymnal writes a new block after it when it next saves this story's data.");
        }
    }

    /// <summary>
    /// Leaves story mode for a non-chapter buffer, holding off metadata changes to the story file while
    /// <paramref name="load"/> runs so none is sent to a buffer that no longer holds it.
    /// </summary>
    private async Task SwitchAwayFromStoryAsync(Func<Task> load)
    {
        if (_story is not { } story)
        {
            await load();
            return;
        }

        using (await _storyWriter.HoldMetadataChangesAsync(story.FullPath))
            await load();
    }

    /// <summary>
    /// Opens an arbitrary workspace file in the single-buffer editor without associating it
    /// with a Book.txt chapter node. Used by supplemental docs and preserves the same save,
    /// dirty-state, and watcher lifecycle as chapter files.
    /// </summary>
    public async Task OpenArbitraryFileAsync(string absolutePath)
    {
        StopWatcher();

        await SwitchAwayFromStoryAsync(async () =>
        {
            var content = await File.ReadAllTextAsync(absolutePath);
            _story = null;
            SetBufferContent(content);
            ActiveNode = null;
            ActiveFilePath = absolutePath;
        });
        HasConflict = false;
        ConflictMessage = null;
        ShowMissingChapterPrompt = false;
        IsBookSelected = false;

        StartWatcher(absolutePath);
    }

    /// <summary>
    /// Shows the missing-chapter warning overlay. The chapter is referenced
    /// in Book.txt but no file was found on disk.
    /// </summary>
    /// <summary>
    /// Updates ActiveNode in-place (e.g. after a title rename on save) so that
    /// downstream observers such as ChapterInfoViewModel see the new title immediately.
    /// </summary>
    public void UpdateActiveNode(ChapterNode updated)
    {
        if (_activeNode?.RelativePath == updated.RelativePath)
            ActiveNode = updated;
    }

    public void OpenMissingChapter(ChapterNode node)
    {
        StopWatcher();
        _story = null;
        HasConflict = false;
        ConflictMessage = null;
        ActiveNode = node;
        ActiveFilePath = null;
        SetBufferContent(string.Empty);
        ShowMissingChapterPrompt = true;
        IsBookSelected = false;
    }

    /// <summary>
    /// <summary>
    /// Opens Book.txt in the editor. Shows a warning banner so the user knows edits affect
    /// the chapter list. Saves go to the same file; WorkspaceViewModel observes Saved to reload.
    /// </summary>
    public async Task SelectBookAsync(string bookTxtPath)
    {
        StopWatcher();
        HasConflict = false;
        ConflictMessage = null;
        ShowMissingChapterPrompt = false;

        await SwitchAwayFromStoryAsync(async () =>
        {
            _story = null;
            ActiveNode = null;

            if (File.Exists(bookTxtPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(bookTxtPath);
                    SetBufferContent(content);
                    ActiveFilePath = bookTxtPath;
                }
                catch (Exception ex)
                {
                    _notificationService.ShowError($"Could not read Book.txt: {ex.Message}");
                    SetBufferContent(string.Empty);
                    ActiveFilePath = null;
                }
            }
            else
            {
                SetBufferContent(string.Empty);
                ActiveFilePath = null;
            }
        });

        IsBookSelected = true;
        if (ActiveFilePath != null)
            StartWatcher(ActiveFilePath);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically writes Text to ActiveFilePath (a chapter file with its metadata block merged back). On
    /// success, sets OriginalText = Text (clears IsDirty). On failure, calls ShowError and re-throws so
    /// save-before-switch callers can abort the chapter switch.
    /// </summary>
    public async Task SaveAsync()
    {
        if (!CanSave) return;

        try
        {
            await SaveCoreAsync(_story, adoptStory: false);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Save failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Writes the buffer to ActiveFilePath, as a story file merged with <paramref name="story"/>'s metadata when
    /// it is not null, else as plain text. With <paramref name="adoptStory"/>, a successful write also makes
    /// <paramref name="story"/> the held story state (a metadata apply); a plain save never replaces it, so a
    /// save that overlaps an apply cannot restore the older metadata. Throws on failure.
    /// </summary>
    private async Task SaveCoreAsync(StoryBuffer? story, bool adoptStory)
    {
        var savePath = ActiveFilePath;
        StopWatcher();

        try
        {
            if (story is not null)
            {
                var prose = story.HasByteOrderMark ? ByteOrderMark + Text : Text;
                var written = await _storyWriter.WriteFileAsync(savePath!, prose, story.Metadata, story.LineEnding);
                if (!written.IsSuccess)
                    throw new IOException(written.Error);

                if (adoptStory && _story is { } current
                    && StoryFileWriter.PathComparer.Equals(current.FullPath, story.FullPath))
                    _story = story;
            }
            else
            {
                await _metadataStore.WriteTextAtomicAsync(savePath!, Text);
            }

            _bufferGeneration++;
            _suppressDirtyTracking = true;
            try
            {
                OriginalText = Text;
                SetDirtyFlag(false);
            }
            finally
            {
                _suppressDirtyTracking = false;
            }

            _savedSubject.OnNext(Unit.Default);
            HasConflict = false;
            ConflictMessage = null;
        }
        finally
        {
            if (savePath != null && ActiveFilePath == savePath)
                StartWatcher(savePath);
        }
    }

    // ── IOpenStoryBuffer ─────────────────────────────────────────────────────

    /// <summary>
    /// True when the open chapter file is <paramref name="absolutePath"/>, compared as a normalized full path
    /// against the path it was opened by and that path's link target, with the writer's path comparison.
    /// Safe on any thread (reads an immutable snapshot).
    /// </summary>
    public bool HoldsFile(string absolutePath)
    {
        if (_story is not { } story || string.IsNullOrWhiteSpace(absolutePath))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(absolutePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        return StoryFileWriter.PathComparer.Equals(full, story.FullPath)
               || StoryFileWriter.PathComparer.Equals(full, story.RealPath);
    }

    /// <summary>The open chapter's metadata block, or null. Safe on any thread (reads an immutable snapshot).</summary>
    public EmbeddedStoryMetadata? CurrentMetadata => _story?.Metadata;

    /// <summary>
    /// On the UI thread: saves the open chapter with <paramref name="metadata"/> (null: no block) through the
    /// normal save path, including unsaved prose. On failure the held metadata is left as it was.
    /// The writer calls this while holding the file's metadata lock; the save takes only its file lock.
    /// </summary>
    public Task<StoryResult> ApplyMetadataAsync(EmbeddedStoryMetadata? metadata) =>
        Dispatcher.UIThread.CheckAccess() || Application.Current is null
            ? ApplyMetadataOnUiThreadAsync(metadata)
            : Dispatcher.UIThread.InvokeAsync(() => ApplyMetadataOnUiThreadAsync(metadata));

    private async Task<StoryResult> ApplyMetadataOnUiThreadAsync(EmbeddedStoryMetadata? metadata)
    {
        if (_story is not { } story || ActiveFilePath is null)
            return StoryResult.Fail("The story file is no longer open in the editor.");

        try
        {
            await SaveCoreAsync(story with { Metadata = metadata }, adoptStory: true);
            return StoryResult.Ok(CoreUnit.Default);
        }
        catch (Exception ex)
        {
            return StoryResult.Fail(ex.Message);
        }
    }

    // ── FileSystemWatcher ────────────────────────────────────────────────────

    private void StartWatcher(string absolutePath)
    {
        StopWatcher();

        var dir = Path.GetDirectoryName(absolutePath);
        var fileName = Path.GetFileName(absolutePath);
        if (dir == null || fileName == null) return;

        var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileChanged;
        _watcher = watcher;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_watcher == null || ActiveFilePath == null)
            return;

        _watcherDebounceTimer?.Dispose();
        _watcherDebounceTimer = new Timer(
            _ => _ = ProcessExternalFileChangeAsync(),
            null,
            150,
            Timeout.Infinite);
    }

    private async Task ProcessExternalFileChangeAsync()
    {
        var path = ActiveFilePath;
        var isStory = _story != null;
        var generation = Volatile.Read(ref _bufferGeneration);
        if (_watcher == null || path == null)
            return;

        string? content = null;
        StorySplit? split = null;
        try
        {
            if (isStory)
            {
                var read = await _storyWriter.ReadFileAsync(path).ConfigureAwait(false);
                split = read.IsSuccess ? read.Value : throw new IOException(read.Error);
            }
            else
            {
                content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                _notificationService.ShowError($"Auto-reload failed: {ex.Message}"));
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyExternalChange(content, split, generation));
    }

    /// <summary>Applies an external change from the file's full text (split when a chapter file is open).</summary>
    private void ApplyExternalFileChange(string content) =>
        ApplyExternalChange(content, _story != null ? StoryFileFormat.Split(content) : null, _bufferGeneration);

    private void ApplyExternalChange(string? content, StorySplit? split, int generation)
    {
        // Guard again after the async read — state may have changed, and a load or save since the
        // read means the buffer is newer than what was read.
        if (_watcher == null || ActiveFilePath == null || generation != _bufferGeneration) return;

        if (IsBookSelected)
        {
            // Book.txt: always silently reload, never prompt, never notify.
            SetBufferContent(content ?? "");
            HasConflict = false;
            ConflictMessage = null;
            return;
        }

        if (!IsDirty)
        {
            if (_story != null && split != null)
                LoadStoryBuffer(ActiveFilePath, split);
            else
                SetBufferContent(content ?? "");
            _notificationService.ShowInfo(
                $"'{Path.GetFileName(ActiveFilePath)}' was changed externally and reloaded.");
        }
        else
        {
            HasConflict = true;
            ConflictMessage =
                $"'{Path.GetFileName(ActiveFilePath)}' was changed externally. " +
                "Choose 'Reload from disk' to accept the external version, or 'Keep my edits' to discard it.";
        }
    }

    private void StopWatcher()
    {
        _watcherDebounceTimer?.Dispose();
        _watcherDebounceTimer = null;

        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Clears the active chapter and buffer, leaving the editor ready for a new workspace.
    /// </summary>
    public void CloseChapter()
    {
        StopWatcher();
        _story = null;
        HasConflict = false;
        ConflictMessage = null;
        ActiveNode = null;
        ActiveFilePath = null;
        SetBufferContent(string.Empty);
        ShowMissingChapterPrompt = false;
        IsBookSelected = false;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _story = null;
        Disposables.Dispose();  // triggers StopWatcher via registered Disposable.Create
    }
}

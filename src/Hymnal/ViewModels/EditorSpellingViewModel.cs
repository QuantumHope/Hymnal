using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Spelling;
using Hymnal.Spelling;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Hymnal.ViewModels;

/// <summary>The misspellings found in one snapshot of the editor's text.</summary>
/// <param name="Text">The exact text that was scanned; the view applies the issues only while the buffer still matches.</param>
public sealed record SpellingScan(string Text, IReadOnlyList<SpellingIssue> Issues)
{
    public static readonly SpellingScan Empty = new("", []);
}

/// <summary>
/// Spell check for the editor buffer. Checking is off until the writer asks for it, so drafting stays
/// undisturbed: <see cref="CheckSpellingCommand"/> (Shift+F7) checks the open file on request, and
/// <see cref="SpellCheckPreferences.CheckAsYouType"/> keeps every file checked. Book.txt is never checked.
/// <para>
/// Scans run off the UI thread when typing pauses, are cancelled by newer text, and produce
/// <see cref="Scan"/>. The view draws the marks, offers fixes on right-click and moves the caret for
/// <see cref="NavigationRequests"/>.
/// </para>
/// </summary>
public sealed class EditorSpellingViewModel : ViewModelBase, IDisposable
{
    internal static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(400);

    private readonly EditorViewModel _editor;
    private readonly INotificationService? _notifications;
    private readonly Subject<int> _navigation = new();
    private readonly Subject<Unit> _recheck = new();
    private readonly ObservableAsPropertyHelper<bool> _isActive;
    private readonly ObservableAsPropertyHelper<bool> _canClear;
    private readonly ObservableAsPropertyHelper<string> _summary;
    private SpellingScan _scan = SpellingScan.Empty;
    private bool _isRequested;
    private bool _isChecking;
    private bool _navigateAfterScan;

    public EditorSpellingViewModel(EditorViewModel editor, SpellingService? service, INotificationService? notifications = null)
    {
        _editor = editor;
        Service = service;
        _notifications = notifications;

        var checkAsYouType = service is null
            ? Observable.Return(false)
            : service.Preferences.WhenAnyValue(p => p.CheckAsYouType);

        // Any file but Book.txt, in the source editor (the same files the preview covers).
        var checkable = editor.WhenAnyValue(x => x.IsPreviewAvailable);

        _isActive = Observable.CombineLatest(checkable, checkAsYouType, this.WhenAnyValue(x => x.IsRequested),
                (file, always, requested) => service is not null && file && (always || requested))
            .DistinctUntilChanged()
            .ToProperty(this, x => x.IsActive);
        Disposables.Add(_isActive);

        _canClear = Observable.CombineLatest(this.WhenAnyValue(x => x.IsRequested), checkAsYouType,
                (requested, always) => requested && !always)
            .ToProperty(this, x => x.CanClear);
        Disposables.Add(_canClear);

        // A check asked for covers the file it was asked in; opening another file ends it.
        Disposables.Add(editor.WhenAnyValue(x => x.ActiveFilePath)
            .Skip(1)
            .DistinctUntilChanged(StringComparer.Ordinal)
            .Subscribe(_ => IsRequested = false));

        if (service is not null)
        {
            Disposables.Add(this.WhenAnyValue(x => x.IsActive)
                .Subscribe(active =>
                {
                    IsChecking = active;
                    if (active)
                        _ = service.EnsureLoadedAsync();
                }));

            Disposables.Add(this.WhenAnyValue(x => x.IsActive)
                .Select(active => active ? Scans(service) : Observable.Return(SpellingScan.Empty))
                .Switch()
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(ApplyScan));
        }

        _summary = Observable.CombineLatest(
                this.WhenAnyValue(x => x.IsActive, x => x.IsChecking, x => x.Scan),
                service is null ? Observable.Return((SpellingStatus.Unavailable, (string?)null))
                    : service.WhenAnyValue(s => s.Status, s => s.StatusMessage),
                (state, status) => Describe(state.Item1, state.Item2, state.Item3, status.Item1))
            .ToProperty(this, x => x.Summary, "");
        Disposables.Add(_summary);

        // Like F6, the keys only act while Write or Research is on screen.
        var surface = editor.WhenAnyValue(x => x.IsEditorSurfaceVisible);
        var canCheck = Observable.CombineLatest(checkable, surface, (file, shown) => service is not null && file && shown);
        var hasIssues = Observable.CombineLatest(this.WhenAnyValue(x => x.Scan), surface,
            (scan, shown) => shown && scan.Issues.Count > 0);

        CheckSpellingCommand = ReactiveCommand.Create(CheckSpelling, canCheck);
        ClearCommand = ReactiveCommand.Create(() => { IsRequested = false; }, this.WhenAnyValue(x => x.CanClear));
        NextIssueCommand = ReactiveCommand.Create(() => Navigate(+1), hasIssues);
        PreviousIssueCommand = ReactiveCommand.Create(() => Navigate(-1), hasIssues);
        IgnoreCommand = ReactiveCommand.Create<string>(word => Service?.Ignore(word));
        AddToDictionaryCommand = ReactiveCommand.CreateFromTask<string>(AddToDictionaryAsync);
        ToggleCheckAsYouTypeCommand = ReactiveCommand.Create(() => Service?.Preferences.ToggleCheckAsYouType());
        Disposables.Add(_navigation);
        Disposables.Add(_recheck);
    }

    /// <summary>The app's spell checker; null when the host has none (some tests).</summary>
    public SpellingService? Service { get; }

    public bool IsAvailable => Service is not null;

    /// <summary>The writer asked for a check of the open file (cleared when another file opens).</summary>
    public bool IsRequested
    {
        get => _isRequested;
        private set => this.RaiseAndSetIfChanged(ref _isRequested, value);
    }

    /// <summary>The open file is being checked: as you type, or because a check was asked for.</summary>
    public bool IsActive => _isActive.Value;

    /// <summary>A requested check can be put away (it can't while checking as you type).</summary>
    public bool CanClear => _canClear.Value;

    /// <summary>A scan is running.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set => this.RaiseAndSetIfChanged(ref _isChecking, value);
    }

    /// <summary>The latest misspellings (empty while not active).</summary>
    public SpellingScan Scan
    {
        get => _scan;
        private set => this.RaiseAndSetIfChanged(ref _scan, value);
    }

    /// <summary>A short status for the editor header: "3 spelling issues", "No spelling issues", …</summary>
    public string Summary => _summary.Value;

    /// <summary>+1 to go to the next issue after the caret, -1 for the previous one.</summary>
    public IObservable<int> NavigationRequests => _navigation;

    /// <summary>Checks the open file now (Shift+F7) and then goes to the first issue after the caret.</summary>
    public ReactiveCommand<Unit, Unit> CheckSpellingCommand { get; }

    /// <summary>Puts away a requested check's marks.</summary>
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public ReactiveCommand<Unit, Unit> NextIssueCommand { get; }

    public ReactiveCommand<Unit, Unit> PreviousIssueCommand { get; }

    /// <summary>Accepts a word for the rest of the session.</summary>
    public ReactiveCommand<string, Unit> IgnoreCommand { get; }

    /// <summary>Adds a word to the project dictionary.</summary>
    public ReactiveCommand<string, Unit> AddToDictionaryCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleCheckAsYouTypeCommand { get; }

    /// <summary>Replacements for <paramref name="word"/>, best first; empty when there is no dictionary.</summary>
    public IReadOnlyList<string> Suggest(string word) =>
        Service?.Checker?.Suggest(word) ?? (IReadOnlyList<string>)[];

    /// <summary>Whether words can be added to a project dictionary (a workspace is open).</summary>
    public bool CanAddToDictionary => Service?.WorkspaceRoot is not null;

    public void Dispose() => Disposables.Dispose();

    internal static string Describe(bool active, bool checking, SpellingScan scan, SpellingStatus status)
    {
        if (!active)
            return "";
        if (status == SpellingStatus.Unavailable)
            return "Spell check unavailable";
        if (status is SpellingStatus.Loading or SpellingStatus.Idle || (checking && scan.Issues.Count == 0))
            return "Checking spelling…";
        return scan.Issues.Count switch
        {
            0 => "No spelling issues",
            1 => "1 spelling issue",
            var n => $"{n:N0} spelling issues",
        };
    }

    private void CheckSpelling()
    {
        if (Service is null)
            return;
        if (Service.Status == SpellingStatus.Unavailable)
        {
            _ = RetryUnavailableAsync(Service);
            return;
        }

        _navigateAfterScan = true;
        if (IsActive)
            _recheck.OnNext(Unit.Default);
        IsRequested = true;
    }

    /// <summary>
    /// Tries the dictionary again (one may have been installed since) and checks if it loads; otherwise says why
    /// spell check can't run.
    /// </summary>
    private async Task RetryUnavailableAsync(SpellingService service)
    {
        var checker = await service.EnsureLoadedAsync();
        if (checker is null)
        {
            _notifications?.ShowInfo(service.StatusMessage ?? service.NoDictionaryMessage);
            return;
        }

        CheckSpelling();
    }

    private void Navigate(int direction)
    {
        if (Scan.Issues.Count > 0)
            _navigation.OnNext(direction);
    }

    private async Task AddToDictionaryAsync(string word)
    {
        if (Service is null)
            return;
        var added = await Service.AddToProjectAsync(word);
        if (!added.IsSuccess)
            _notifications?.ShowError(added.Error ?? "The word could not be added.");
    }

    private void ApplyScan(SpellingScan scan)
    {
        IsChecking = false;
        Scan = scan;
        if (_navigateAfterScan && IsActive && Service?.Checker is not null)
        {
            _navigateAfterScan = false;
            if (scan.Issues.Count > 0)
                _navigation.OnNext(+1);
        }
    }

    /// <summary>
    /// Scans the text at once, then whenever typing pauses, the checker changes (dictionary loaded, a word
    /// added or ignored) or a check is asked for again. A newer scan cancels the one still running.
    /// </summary>
    private IObservable<SpellingScan> Scans(SpellingService service)
    {
        var texts = _editor.WhenAnyValue(x => x.Text)
            .Publish(t => t.Take(1).Merge(t.Skip(1).Throttle(TypingPause, TaskPoolScheduler.Default)));
        var checkers = service.WhenAnyValue(s => s.Checker);
        var rechecks = _recheck.StartWith(Unit.Default);

        return Observable.CombineLatest(texts, checkers, rechecks, (text, checker, _) => (text, checker))
            .Select(input => input.checker is null
                ? Observable.Return(SpellingScan.Empty)
                : Observable.FromAsync(ct => Task.Run(
                        () => new SpellingScan(input.text, input.checker.FindIssues(input.text, ct)), ct))
                    // A cancelled scan was replaced by a newer one. Any other failure (a dictionary quirk) drops this
                    // scan's marks but must not end the pipeline, which would stop spell check until restart.
                    .Catch<SpellingScan, Exception>(ex => ex is OperationCanceledException
                        ? Observable.Empty<SpellingScan>()
                        : Observable.Return(new SpellingScan(input.text, []))))
            .Switch();
    }
}

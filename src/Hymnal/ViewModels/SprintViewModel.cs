using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Models.Sprints;
using Hymnal.Core.Services;
using Hymnal.Core.Services.Sprints;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Avalonia.Threading;

namespace Hymnal.ViewModels;

public enum SprintStage
{
    Idle,
    Choosing,
    Running,
    Recap,
    FutureYou,
}

/// <summary>A sprint length on the start card.</summary>
public sealed class SprintDurationOption : ReactiveObject
{
    public SprintDurationOption(int minutes) => Minutes = minutes;

    public int Minutes { get; }
    public string Label => $"{Minutes} min";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>One chapter's line in the recap.</summary>
public sealed record SprintRecapChapter(string Title, string NetDisplay, string DetailDisplay);

/// <summary>Everything the recap screen shows, already formatted.</summary>
public sealed record SprintRecap
{
    public required string Headline { get; init; }
    public required string Summary { get; init; }
    public required string Added { get; init; }
    public required string Cut { get; init; }
    public required string Net { get; init; }
    public required string Minutes { get; init; }
    public required string WordsPerMinute { get; init; }
    public required string Xp { get; init; }
    public required string ChurnLabel { get; init; }

    /// <summary>Share of touched words that were added, 0–100, for the impact bar.</summary>
    public required double AddedPercent { get; init; }
    public required bool HasChanges { get; init; }
    public required IReadOnlyList<SprintRecapChapter> Chapters { get; init; }
    public required string ProjectWords { get; init; }
    public string? TargetProgress { get; init; }
    public double TargetPercentAfter { get; init; }
    public bool HasTarget => TargetProgress != null;
    public string? Milestone { get; init; }
    public bool HasMilestone => Milestone != null;
    public string? Streak { get; init; }
    public string? BestStreak { get; init; }
    public bool HasStreak => Streak != null;
    public string? ProfileError { get; init; }
    public bool HasProfileError => ProfileError != null;
}

/// <summary>
/// One-click writing sprints: the start card, the running timer (shown as a HUD over focus mode), the recap and the
/// "note to Future You" screen. Edits are attributed to chapters through <see cref="EditorViewModel.ProseEdited"/>;
/// finished sprints go to the global writer profile and notes to the workspace.
/// </summary>
public sealed class SprintViewModel : ViewModelBase, IDisposable
{
    public static readonly IReadOnlyList<int> Presets = [10, 15, 25, 45];
    public const int DefaultMinutes = 15;
    public const string LastMinutesKey = "sprint.lastMinutes";

    private readonly EditorViewModel _editor;
    private readonly WorkspaceViewModel _workspace;
    private readonly IWriterProfileStore _profile;
    private readonly IFutureYouNotesService _notes;
    private readonly WordCountService _counter;
    private readonly IAppSettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly IScheduler _scheduler;

    private readonly SerialDisposable _running = new();
    private SprintTracker? _tracker;
    private DateTimeOffset _startedAt;
    private TimeSpan _planned;
    private int _projectWordsAtStart;
    private int? _projectTarget;
    private string _workspaceRoot = "";
    private string _workspaceName = "";
    private bool _finishing;

    public SprintViewModel(
        EditorViewModel editor,
        WorkspaceViewModel workspace,
        IWriterProfileStore profile,
        IFutureYouNotesService notes,
        WordCountService counter,
        IAppSettingsStore settings,
        INotificationService notifications,
        IScheduler? scheduler = null)
    {
        _editor = editor;
        _workspace = workspace;
        _profile = profile;
        _notes = notes;
        _counter = counter;
        _settings = settings;
        _notifications = notifications;
        _scheduler = scheduler ?? AvaloniaScheduler.Instance;

        DurationOptions = Presets.Select(m => new SprintDurationOption(m)).ToList();
        SelectMinutes(DefaultMinutes);
        Disposables.Add(_running);

        var stage = this.WhenAnyValue(x => x.Stage);
        Disposables.Add(_isRunning = stage.Select(s => s == SprintStage.Running).ToProperty(this, x => x.IsRunning));
        Disposables.Add(_isChoosing = stage.Select(s => s == SprintStage.Choosing).ToProperty(this, x => x.IsChoosing));
        Disposables.Add(_isRecap = stage.Select(s => s == SprintStage.Recap).ToProperty(this, x => x.IsRecap));
        Disposables.Add(_isFutureYou = stage.Select(s => s == SprintStage.FutureYou).ToProperty(this, x => x.IsFutureYou));
        Disposables.Add(_isOverlayOpen = stage
            .Select(s => s is SprintStage.Choosing or SprintStage.Recap or SprintStage.FutureYou)
            .ToProperty(this, x => x.IsOverlayOpen));

        var canOpen = Observable.CombineLatest(
            stage, workspace.WhenAnyValue(x => x.HasWorkspace),
            (s, has) => s == SprintStage.Idle && has);
        // Command results and can-execute changes go to the UI thread (as RxApp.MainThreadScheduler does in the
        // app), so buttons bound to them are never touched from a background thread.
        var ui = AvaloniaScheduler.Instance;
        OpenCommand = ReactiveCommand.CreateFromTask(OpenAsync, canOpen, ui);
        SelectDurationCommand = ReactiveCommand.Create<int>(SelectMinutes, outputScheduler: ui);
        BeginCommand = ReactiveCommand.Create(Begin, stage.Select(s => s == SprintStage.Choosing), ui);
        CancelCommand = ReactiveCommand.Create(() => { Stage = SprintStage.Idle; },
            stage.Select(s => s == SprintStage.Choosing), ui);
        EndCommand = ReactiveCommand.CreateFromTask(() => FinishAsync(completed: false),
            stage.Select(s => s == SprintStage.Running), ui);
        ToFutureYouCommand = ReactiveCommand.Create(ShowFutureYou, stage.Select(s => s == SprintStage.Recap), ui);
        DoneCommand = ReactiveCommand.Create(() => { Stage = SprintStage.Idle; },
            stage.Select(s => s is SprintStage.Recap or SprintStage.FutureYou), ui);
        SaveNoteCommand = ReactiveCommand.CreateFromTask(SaveNoteAsync,
            Observable.CombineLatest(stage, this.WhenAnyValue(x => x.FutureYouText),
                (s, text) => s == SprintStage.FutureYou && !string.IsNullOrWhiteSpace(text)), ui);

        foreach (var command in new IHandleObservableErrors[] { OpenCommand, EndCommand, SaveNoteCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex => _notifications.ShowError($"Sprint: {ex.Message}")));

        // Attribute every edit to the chapter it was made in.
        Disposables.Add(editor.ProseEdited
            .Where(_ => Stage == SprintStage.Running)
            .Subscribe(e =>
            {
                if (e.Node is { Kind: NodeKind.Chapter } node && !e.IsBookTxt)
                    Observe(node, e.Text);
            }));

        // A chapter opened mid-sprint: its loaded text is its baseline (or, after a save, matches its latest).
        Disposables.Add(editor.WhenAnyValue(x => x.ActiveNode)
            .Where(_ => Stage == SprintStage.Running)
            .Subscribe(_ => ObserveActive()));

        // A save can follow an undo back to the saved text, which is not a ProseEdit; catch it here.
        Disposables.Add(editor.Saved
            .Where(_ => Stage == SprintStage.Running)
            .Subscribe(_ => ObserveActive()));

        // Closing the workspace abandons a running sprint and closes the screens.
        Disposables.Add(workspace.WhenAnyValue(x => x.HasWorkspace)
            .Where(has => !has && Stage != SprintStage.Idle)
            .Subscribe(_ =>
            {
                _running.Disposable = null;
                Stage = SprintStage.Idle;
            }));
    }

    // ── State ──────────────────────────────────────────────────────────────

    private SprintStage _stage;
    public SprintStage Stage
    {
        get => _stage;
        private set => this.RaiseAndSetIfChanged(ref _stage, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _isRunning;
    public bool IsRunning => _isRunning.Value;

    private readonly ObservableAsPropertyHelper<bool> _isChoosing;
    public bool IsChoosing => _isChoosing.Value;

    private readonly ObservableAsPropertyHelper<bool> _isRecap;
    public bool IsRecap => _isRecap.Value;

    private readonly ObservableAsPropertyHelper<bool> _isFutureYou;
    public bool IsFutureYou => _isFutureYou.Value;

    private readonly ObservableAsPropertyHelper<bool> _isOverlayOpen;
    public bool IsOverlayOpen => _isOverlayOpen.Value;

    // ── Start card ─────────────────────────────────────────────────────────

    public IReadOnlyList<SprintDurationOption> DurationOptions { get; }

    private int _selectedMinutes;
    public int SelectedMinutes
    {
        get => _selectedMinutes;
        private set => this.RaiseAndSetIfChanged(ref _selectedMinutes, value);
    }

    private FutureYouNote? _pastYouNote;
    /// <summary>The latest note the author left for this session, shown on the start card.</summary>
    public FutureYouNote? PastYouNote
    {
        get => _pastYouNote;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pastYouNote, value);
            this.RaisePropertyChanged(nameof(HasPastYouNote));
            this.RaisePropertyChanged(nameof(PastYouContext));
        }
    }

    public bool HasPastYouNote => PastYouNote != null;

    public string? PastYouContext => PastYouNote is { } n
        ? (n.ChapterTitle is { Length: > 0 } t ? $"{When(n.CreatedAt)} · in {t}" : When(n.CreatedAt))
        : null;

    // ── Running HUD ────────────────────────────────────────────────────────

    private string _remainingDisplay = "";
    public string RemainingDisplay
    {
        get => _remainingDisplay;
        private set => this.RaiseAndSetIfChanged(ref _remainingDisplay, value);
    }

    private double _progress;
    /// <summary>Elapsed share of the sprint, 0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    private string _liveNetDisplay = "";
    public string LiveNetDisplay
    {
        get => _liveNetDisplay;
        private set => this.RaiseAndSetIfChanged(ref _liveNetDisplay, value);
    }

    // ── Recap and Future You ───────────────────────────────────────────────

    private SprintRecap? _recap;
    public SprintRecap? Recap
    {
        get => _recap;
        private set => this.RaiseAndSetIfChanged(ref _recap, value);
    }

    private string _futureYouText = "";
    public string FutureYouText
    {
        get => _futureYouText;
        set => this.RaiseAndSetIfChanged(ref _futureYouText, value);
    }

    private string? _leftOffChapter;
    public string? LeftOffChapter
    {
        get => _leftOffChapter;
        private set
        {
            this.RaiseAndSetIfChanged(ref _leftOffChapter, value);
            this.RaisePropertyChanged(nameof(HasLeftOff));
        }
    }

    private string? _leftOffLine;
    public string? LeftOffLine
    {
        get => _leftOffLine;
        private set => this.RaiseAndSetIfChanged(ref _leftOffLine, value);
    }

    public bool HasLeftOff => LeftOffChapter != null;

    private string? _leftOffPath;

    // ── Commands ───────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<int, Unit> SelectDurationCommand { get; }
    public ReactiveCommand<Unit, Unit> BeginCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> EndCommand { get; }
    public ReactiveCommand<Unit, Unit> ToFutureYouCommand { get; }
    public ReactiveCommand<Unit, Unit> DoneCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveNoteCommand { get; }

    // ── Flow ───────────────────────────────────────────────────────────────

    private async Task OpenAsync()
    {
        var root = _workspace.WorkspaceRoot;
        var last = await _settings.GetAsync<int?>(LastMinutesKey);
        var note = string.IsNullOrEmpty(root) ? null : await _notes.LatestAsync(root);
        await OnUiAsync(() =>
        {
            SelectMinutes(last is > 0 and <= 240 ? last.Value : DefaultMinutes);
            PastYouNote = note;
            Stage = SprintStage.Choosing;
        });
    }

    private void SelectMinutes(int minutes)
    {
        SelectedMinutes = minutes;
        foreach (var option in DurationOptions)
            option.IsSelected = option.Minutes == minutes;
    }

    private void Begin()
    {
        _tracker = new SprintTracker(_counter);
        _startedAt = _scheduler.Now;
        _planned = TimeSpan.FromMinutes(SelectedMinutes);
        _projectWordsAtStart = _workspace.TotalWordCount;
        _projectTarget = ProjectTarget();
        _workspaceRoot = _workspace.WorkspaceRoot;
        _workspaceName = _workspace.WorkspaceName ?? "";
        _finishing = false;
        _ = _settings.SetAsync(LastMinutesKey, SelectedMinutes);

        Stage = SprintStage.Running;
        ObserveActive();
        Tick();
        _running.Disposable = Observable.Interval(TimeSpan.FromSeconds(1), _scheduler)
            .Subscribe(_ => Tick());
    }

    /// <summary>Updates the HUD; when the time is up, finishes the sprint. Runs every second while running.</summary>
    public void Tick()
    {
        if (Stage != SprintStage.Running || _tracker == null)
            return;

        var elapsed = _scheduler.Now - _startedAt;
        var remaining = _planned - elapsed;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        RemainingDisplay = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        Progress = Math.Clamp(elapsed.TotalSeconds / _planned.TotalSeconds * 100, 0, 100);
        LiveNetDisplay = $"{Signed(_tracker.LiveNet)} {Words(_tracker.LiveNet)}";

        if (remaining == TimeSpan.Zero)
            _ = FinishAsync(completed: true);
    }

    private void Observe(ChapterNode node, string text)
    {
        if (_tracker == null)
            return;
        _tracker.Observe(node.RelativePath, node.Title, text);
        LiveNetDisplay = $"{Signed(_tracker.LiveNet)} {Words(_tracker.LiveNet)}";
    }

    private void ObserveActive()
    {
        if (_editor.ActiveNode is { Kind: NodeKind.Chapter } node && !_editor.IsBookSelected)
            Observe(node, _editor.Text);
    }

    private async Task FinishAsync(bool completed)
    {
        if (Stage != SprintStage.Running || _tracker == null || _finishing)
            return;
        _finishing = true;
        _running.Disposable = null;
        ObserveActive();

        var tracker = _tracker;
        var elapsed = _scheduler.Now - _startedAt;
        if (completed || elapsed > _planned)
            elapsed = _planned;
        var seconds = (int)Math.Round(elapsed.TotalSeconds);
        var summary = tracker.Summarize();

        if (summary.Total.Touched == 0 && seconds < 60)
        {
            Stage = SprintStage.Idle;
            _notifications.ShowInfo("Sprint ended with no writing, so it wasn't recorded.");
            return;
        }

        var localDate = DateOnly.FromDateTime(_startedAt.ToLocalTime().DateTime);
        var session = new SprintSessionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = _startedAt,
            LocalDate = localDate.ToString("yyyy-MM-dd"),
            PlannedSeconds = (int)_planned.TotalSeconds,
            ActualSeconds = seconds,
            Completed = completed,
            Workspace = _workspaceName,
            WordsAdded = summary.Total.Added,
            WordsCut = summary.Total.Cut,
            WordsNet = summary.Total.Net,
            Chapters = summary.Chapters.Count,
        };

        StreakOutcome? streak = null;
        string? profileError = null;
        var earlier = await _profile.LoadSessionsAsync();
        if (earlier.IsSuccess)
        {
            var saved = await _profile.AppendSessionAsync(session);
            if (saved.IsSuccess)
                streak = StreakCalculator.Apply(earlier.Value!.Select(s => DateOnly.Parse(s.LocalDate)), localDate);
            else
                profileError = $"This sprint wasn't recorded: {saved.Error}";
        }
        else
        {
            profileError = $"This sprint wasn't recorded: {earlier.Error}";
        }

        await OnUiAsync(() =>
        {
            Recap = BuildRecap(summary, seconds, completed, streak, profileError);
            CaptureLeftOff(tracker);
            Stage = SprintStage.Recap;
        });
    }

    private SprintRecap BuildRecap(SprintSummary summary, int seconds, bool completed, StreakOutcome? streak,
        string? profileError)
    {
        var total = summary.Total;
        var minutes = seconds / 60.0;
        var wpm = minutes >= 0.5 ? (int)Math.Round(Math.Max(0, total.Net) / minutes) : 0;
        var xp = SprintScoring.Xp(total.Added, total.Cut, seconds, completed);

        var before = _projectWordsAtStart;
        var after = Math.Max(0, before + total.Net);
        string? targetProgress = null;
        string? milestone = null;
        double percentAfter = 0;
        if (_projectTarget is > 0 and var target)
        {
            var percentBefore = 100.0 * before / target;
            percentAfter = 100.0 * after / target;
            targetProgress = $"{percentBefore:0}% → {percentAfter:0}% of your {target:N0}-word target";
            if (SprintScoring.MilestoneCrossed(percentBefore, percentAfter) is { } m)
                milestone = m == 100 ? "Target reached! The draft hits 100%." : $"Milestone: you crossed {m}% of your target.";
        }

        var duration = FormatDuration(seconds);
        var summaryLine = total.Touched == 0
            ? $"{duration} at the desk. Showing up counts."
            : $"{Signed(total.Net)} {Words(total.Net)} in {duration}.";

        return new SprintRecap
        {
            Headline = completed ? "SPRINT COMPLETE" : "SPRINT ENDED",
            Summary = summaryLine,
            Added = $"+{total.Added:N0}",
            Cut = $"−{total.Cut:N0}",
            Net = Signed(total.Net),
            Minutes = duration,
            WordsPerMinute = wpm.ToString("N0"),
            Xp = $"+{xp:N0} XP",
            ChurnLabel = SprintScoring.Label(SprintScoring.Churn(total)),
            AddedPercent = total.Touched == 0 ? 0 : 100.0 * total.Added / total.Touched,
            HasChanges = total.Touched > 0,
            Chapters = summary.Chapters
                .Select(c => new SprintRecapChapter(c.Title, Signed(c.Delta.Net),
                    $"+{c.Delta.Added:N0} / −{c.Delta.Cut:N0}"))
                .ToList(),
            ProjectWords = $"{before:N0} → {after:N0} words",
            TargetProgress = targetProgress,
            TargetPercentAfter = Math.Min(100, percentAfter),
            Milestone = milestone,
            Streak = streak?.Describe(),
            BestStreak = streak is { } s ? $"Best streak: {(s.Best == 1 ? "1 day" : $"{s.Best} days")}" : null,
            ProfileError = profileError,
        };
    }

    private void CaptureLeftOff(SprintTracker tracker)
    {
        var key = tracker.LastKey;
        if (key == null && _editor.ActiveNode is { Kind: NodeKind.Chapter } active)
            key = active.RelativePath;

        _leftOffPath = key;
        LeftOffChapter = key == null ? null : tracker.TitleOf(key) ?? _editor.ActiveNode?.Title ?? key;
        LeftOffLine = key == null ? null : SprintTracker.LastLine(tracker.LatestText(key) ?? _editor.Text);
    }

    private void ShowFutureYou()
    {
        FutureYouText = "";
        Stage = SprintStage.FutureYou;
    }

    private async Task SaveNoteAsync()
    {
        var note = new FutureYouNote
        {
            CreatedAt = _scheduler.Now,
            Text = FutureYouText.Trim(),
            ChapterPath = _leftOffPath,
            ChapterTitle = LeftOffChapter,
            LastLine = LeftOffLine,
        };
        var result = await _notes.AddAsync(_workspaceRoot, note);
        await OnUiAsync(() =>
        {
            if (!result.IsSuccess)
            {
                _notifications.ShowError($"Your note wasn't saved: {result.Error}");
                return;
            }
            Stage = SprintStage.Idle;
            _notifications.ShowInfo("Note saved. Future You will see it when the next sprint starts.");
        });
    }

    public void Dispose() => Disposables.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs state changes on the UI thread after awaiting services (their continuations can resume on the thread
    /// pool), inline when already there or when there is no application (view model tests outside a Dispatch).
    /// </summary>
    private static async Task OnUiAsync(Action action)
    {
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
            action();
        else
            await Dispatcher.UIThread.InvokeAsync(action);
    }

    private int? ProjectTarget()
    {
        var sum = _workspace.Nodes
            .Where(n => n.Node.Kind == NodeKind.Chapter)
            .Sum(n => n.Target?.MinWords ?? n.Target?.MaxWords ?? 0);
        return sum > 0 ? sum : null;
    }

    private static string Signed(int n) => n > 0 ? $"+{n:N0}" : n < 0 ? $"−{-n:N0}" : "±0";

    private static string Words(int n) => Math.Abs(n) == 1 ? "word" : "words";

    private static string FormatDuration(int seconds)
    {
        var minutes = seconds / 60;
        var rest = seconds % 60;
        if (minutes == 0)
            return $"{rest} s";
        return rest == 0 ? $"{minutes} min" : $"{minutes} min {rest} s";
    }

    private string When(DateTimeOffset at)
    {
        var days = DateOnly.FromDateTime(_scheduler.Now.ToLocalTime().DateTime).DayNumber
                   - DateOnly.FromDateTime(at.ToLocalTime().DateTime).DayNumber;
        return days switch
        {
            <= 0 => "Earlier today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            _ => at.ToLocalTime().ToString("MMM d"),
        };
    }
}

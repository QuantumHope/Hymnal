using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Audio;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Models.Runs;
using Hymnal.Core.Services;
using Hymnal.Core.Services.Runs;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Avalonia.Threading;

namespace Hymnal.ViewModels;

public enum RunStage
{
    Idle,
    Choosing,
    Running,
    Recap,
}

/// <summary>A run length on the start card.</summary>
public sealed class RunDurationOption : ReactiveObject
{
    public RunDurationOption(int minutes) => Minutes = minutes;

    public int Minutes { get; }
    public string Label => $"{Minutes} min";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>
/// One minute of a run in the recap chart: the words added and cut in that minute, and the running totals at its
/// end. <see cref="Seconds"/> is 60 except, perhaps, for the last minute.
/// </summary>
public sealed record RunRecapMinute(int Minute, int Seconds, int Added, int Cut, int TotalAdded, int TotalCut, int TotalNet)
{
    public int Net => Added - Cut;

    /// <summary>Net words a minute, scaled up for a part minute so it compares with the full ones.</summary>
    public double NetPerMinute => Seconds <= 0 ? 0 : Net * 60.0 / Seconds;
}

/// <summary>One chapter's line in the recap.</summary>
public sealed record RunRecapChapter(string Title, string NetDisplay, string DetailDisplay);

/// <summary>Everything the recap screen shows, already formatted.</summary>
public sealed record RunRecap
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
    public required IReadOnlyList<RunRecapChapter> Chapters { get; init; }

    /// <summary>The run minute by minute, for the recap chart.</summary>
    public IReadOnlyList<RunRecapMinute> Timeline { get; init; } = [];
    public bool HasTimeline => Timeline.Count > 0;

    /// <summary>Any minute added or cut words; otherwise the chart would only be empty axes.</summary>
    public bool HasChartData => Timeline.Any(m => m.Added > 0 || m.Cut > 0);

    /// <summary>The busiest minute, e.g. "Best minute: +48 (minute 7)".</summary>
    public string? BestMinute { get; init; }
    public required string ProjectWords { get; init; }
    public string? TargetProgress { get; init; }
    public double TargetPercentAfter { get; init; }
    public bool HasTarget => TargetProgress != null;
    public string? Milestone { get; init; }
    public bool HasMilestone => Milestone != null;
    public string? Streak { get; init; }
    public string? BestStreak { get; init; }
    public bool HasStreak => Streak != null;

    /// <summary>The last 12 weeks with the current streak marked, for the recap's calendar.</summary>
    public IReadOnlyList<LogbookDay> StreakDays { get; init; } = [];
    public bool HasStreakDays => StreakDays.Count > 0;
    public string? ProfileError { get; init; }
    public bool HasProfileError => ProfileError != null;
}

/// <summary>
/// One-click writing runs: the start card, the running timer (shown as a HUD over focus mode), and the recap, which
/// ends with an optional note to Future You. Edits are attributed to chapters through <see cref="EditorViewModel.ProseEdited"/>;
/// finished runs go to the global writer profile and notes to the workspace.
/// </summary>
public sealed class RunViewModel : ViewModelBase, IDisposable
{
    public static readonly IReadOnlyList<int> Presets = [10, 15, 25, 45, 60];
    public const int DefaultMinutes = 15;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 180;

    /// <summary>How long a combo callout stays on the HUD.</summary>
    private static readonly TimeSpan ComboShown = TimeSpan.FromSeconds(3);
    public const string LastMinutesKey = "run.lastMinutes";

    private readonly EditorViewModel _editor;
    private readonly WorkspaceViewModel _workspace;
    private readonly IWriterProfileStore _profile;
    private readonly IFutureYouNotesService _notes;
    private readonly WordCountService _counter;
    private readonly IAppSettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly IScheduler _scheduler;
    private readonly IRunSoundscape _soundscape;

    private readonly SerialDisposable _running = new();
    private RunTracker? _tracker;
    private DateTimeOffset _startedAt;
    private TimeSpan _planned;
    private int _projectWordsAtStart;
    private int? _projectTarget;
    private string _workspaceRoot = "";
    private string _workspaceName = "";
    private bool _finishing;
    private int _lastNet;
    /// <summary>The highest net count this run, so a combo is called out once, not again after a cut and retype.</summary>
    private int _peakNet;
    private readonly List<RunRecapMinute> _timeline = [];
    private DateTimeOffset _comboUntil;
    private string? _lastRunId;

    public RunViewModel(
        EditorViewModel editor,
        WorkspaceViewModel workspace,
        IWriterProfileStore profile,
        IFutureYouNotesService notes,
        WordCountService counter,
        IAppSettingsStore settings,
        INotificationService notifications,
        IScheduler? scheduler = null,
        RunAtmospherePreferences? atmosphere = null,
        IRunSoundscape? soundscape = null)
    {
        _editor = editor;
        _workspace = workspace;
        _profile = profile;
        _notes = notes;
        _counter = counter;
        _settings = settings;
        _notifications = notifications;
        _scheduler = scheduler ?? AvaloniaScheduler.Instance;
        Atmosphere = atmosphere ?? new RunAtmospherePreferences();
        _soundscape = soundscape ?? new SilentRunSoundscape();
        // Turning Sparks off mid-run also takes down a callout already showing.
        Disposables.Add(Atmosphere.WhenAnyValue(a => a.Sparks).Where(on => !on).Subscribe(_ => ComboText = null));

        DurationOptions = Presets.Select(m => new RunDurationOption(m)).ToList();
        SelectMinutes(DefaultMinutes);
        Disposables.Add(_running);

        var stage = this.WhenAnyValue(x => x.Stage);
        Disposables.Add(_isRunning = stage.Select(s => s == RunStage.Running).ToProperty(this, x => x.IsRunning));
        Disposables.Add(_isChoosing = stage.Select(s => s == RunStage.Choosing).ToProperty(this, x => x.IsChoosing));
        Disposables.Add(_isRecap = stage.Select(s => s == RunStage.Recap).ToProperty(this, x => x.IsRecap));
        Disposables.Add(_isOverlayOpen = stage
            .Select(s => s is RunStage.Choosing or RunStage.Recap)
            .ToProperty(this, x => x.IsOverlayOpen));

        var canOpen = Observable.CombineLatest(
            stage, workspace.WhenAnyValue(x => x.HasWorkspace),
            (s, has) => s == RunStage.Idle && has);
        // Command results and can-execute changes go to the UI thread (as RxApp.MainThreadScheduler does in the
        // app), so buttons bound to them are never touched from a background thread.
        var ui = AvaloniaScheduler.Instance;
        OpenCommand = ReactiveCommand.CreateFromTask(OpenAsync, canOpen, ui);
        SelectDurationCommand = ReactiveCommand.Create<int>(SelectMinutes, outputScheduler: ui);
        BeginCommand = ReactiveCommand.Create(Begin, stage.Select(s => s == RunStage.Choosing), ui);
        CancelCommand = ReactiveCommand.Create(() => { Stage = RunStage.Idle; },
            stage.Select(s => s == RunStage.Choosing), ui);
        EndCommand = ReactiveCommand.CreateFromTask(() => FinishAsync(completed: false),
            stage.Select(s => s == RunStage.Running), ui);
        DoneCommand = ReactiveCommand.CreateFromTask(DoneAsync, stage.Select(s => s == RunStage.Recap), ui);
        NudgeMinutesCommand = ReactiveCommand.Create<int>(delta => SelectedMinutes += delta, outputScheduler: ui);
        ShowLogbookCommand = ReactiveCommand.CreateFromTask(
            () => RunLogbookViewModel.LoadAsync(_profile, _notes, DateOnly.FromDateTime(_scheduler.Now.ToLocalTime().DateTime)),
            outputScheduler: ui);

        foreach (var command in new IHandleObservableErrors[] { OpenCommand, EndCommand, DoneCommand, ShowLogbookCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex => _notifications.ShowError($"Run: {ex.Message}")));

        // Attribute every edit to the chapter it was made in.
        Disposables.Add(editor.ProseEdited
            .Where(_ => Stage == RunStage.Running)
            .Subscribe(e =>
            {
                if (e.Node is { Kind: NodeKind.Chapter } node && !e.IsBookTxt)
                    Observe(node, e.Text);
            }));

        // A chapter opened mid-run: its loaded text is its baseline (or, after a save, matches its latest).
        Disposables.Add(editor.WhenAnyValue(x => x.ActiveNode)
            .Where(_ => Stage == RunStage.Running)
            .Subscribe(_ => ObserveActive()));

        // A save can follow an undo back to the saved text, which is not a ProseEdit; catch it here.
        Disposables.Add(editor.Saved
            .Where(_ => Stage == RunStage.Running)
            .Subscribe(_ => ObserveActive()));

        // Closing the workspace abandons a running run and closes the screens.
        Disposables.Add(workspace.WhenAnyValue(x => x.HasWorkspace)
            .Where(has => !has && Stage != RunStage.Idle)
            .Subscribe(_ => Abandon()));

        // Opening another workspace (HasWorkspace stays true) abandons the run too: chapters are tracked by their
        // workspace-relative path, so the new workspace's files would be diffed against the old one's. The recap
        // stays: its note belongs to the run's workspace, which DoneAsync still writes to.
        Disposables.Add(workspace.WorkspaceChanged
            .Where(_ => Stage is RunStage.Choosing or RunStage.Running
                        && !string.Equals(workspace.WorkspaceRoot, _workspaceRoot, StringComparison.Ordinal))
            .Subscribe(_ => Abandon()));
    }

    private void Abandon()
    {
        if (Stage == RunStage.Running)
            _soundscape.Finish(completed: false);
        _running.Disposable = null;
        Stage = RunStage.Idle;
    }

    // ── State ──────────────────────────────────────────────────────────────

    private RunStage _stage;
    public RunStage Stage
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

    private readonly ObservableAsPropertyHelper<bool> _isOverlayOpen;
    public bool IsOverlayOpen => _isOverlayOpen.Value;

    // ── Start card ─────────────────────────────────────────────────────────

    public IReadOnlyList<RunDurationOption> DurationOptions { get; }

    private int _selectedMinutes;
    /// <summary>The run length on the dial, <see cref="MinMinutes"/> to <see cref="MaxMinutes"/>.</summary>
    public int SelectedMinutes
    {
        get => _selectedMinutes;
        set => SelectMinutes(value);
    }

    /// <summary>The run atmosphere choices (flames, glow, sparks, ambience).</summary>
    public RunAtmospherePreferences Atmosphere { get; }

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
    /// <summary>Elapsed share of the run, 0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            this.RaiseAndSetIfChanged(ref _progress, value);
            this.RaisePropertyChanged(nameof(HeartbeatProgress));
        }
    }

    private string _liveNetDisplay = "";
    public string LiveNetDisplay
    {
        get => _liveNetDisplay;
        private set => this.RaiseAndSetIfChanged(ref _liveNetDisplay, value);
    }

    /// <summary>How far through the run it is, 0–1 (for the heartbeat's pace).</summary>
    public double HeartbeatProgress => Progress / 100;

    private double _intensity;
    /// <summary>How hard the atmosphere burns, <see cref="RunIntensity.Idle"/> to 1 (see <see cref="RunIntensity"/>).</summary>
    public double Intensity
    {
        get => _intensity;
        private set => this.RaiseAndSetIfChanged(ref _intensity, value);
    }

    private bool _isFinalStretch;
    /// <summary>True through the last minute (or last quarter of a short run): the HUD runs hot.</summary>
    public bool IsFinalStretch
    {
        get => _isFinalStretch;
        private set => this.RaiseAndSetIfChanged(ref _isFinalStretch, value);
    }

    private int _sparks;
    /// <summary>Counts words added during runs; each rise throws sparks off the HUD.</summary>
    public int Sparks
    {
        get => _sparks;
        private set => this.RaiseAndSetIfChanged(ref _sparks, value);
    }

    private string? _comboText;
    /// <summary>A combo callout ("COMBO · 100 WORDS") for a few seconds after the net count crosses a threshold.</summary>
    public string? ComboText
    {
        get => _comboText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _comboText, value);
            this.RaisePropertyChanged(nameof(HasCombo));
        }
    }

    public bool HasCombo => ComboText != null;

    private bool _showCumulative;
    /// <summary>The recap chart shows running totals rather than each minute.</summary>
    public bool ShowCumulative
    {
        get => _showCumulative;
        set
        {
            this.RaiseAndSetIfChanged(ref _showCumulative, value);
            this.RaisePropertyChanged(nameof(ShowPerMinute));
        }
    }

    /// <summary>The recap chart shows each minute (the opposite of <see cref="ShowCumulative"/>).</summary>
    public bool ShowPerMinute
    {
        get => !ShowCumulative;
        set => ShowCumulative = !value;
    }

    private int _celebrations;
    /// <summary>Goes up each time a run's timer runs out; the atmosphere layer bursts.</summary>
    public int Celebrations
    {
        get => _celebrations;
        private set => this.RaiseAndSetIfChanged(ref _celebrations, value);
    }

    // ── Recap and Future You ───────────────────────────────────────────────

    private RunRecap? _recap;
    public RunRecap? Recap
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
    /// <summary>Closes the recap, first saving the note to Future You when one was written.</summary>
    public ReactiveCommand<Unit, Unit> DoneCommand { get; }

    /// <summary>Moves the dial by a number of minutes (the − and + buttons).</summary>
    public ReactiveCommand<int, Unit> NudgeMinutesCommand { get; }

    /// <summary>Loads the Logbook; the window shows the result.</summary>
    public ReactiveCommand<Unit, RunLogbookViewModel> ShowLogbookCommand { get; }

    // ── Flow ───────────────────────────────────────────────────────────────

    private async Task OpenAsync()
    {
        var root = _workspace.WorkspaceRoot;
        var last = await _settings.GetAsync<int?>(LastMinutesKey);
        var note = string.IsNullOrEmpty(root) ? null : await _notes.LatestAsync(root);
        await OnUiAsync(() =>
        {
            _workspaceRoot = root;
            SelectMinutes(last is >= MinMinutes and <= MaxMinutes ? last.Value : DefaultMinutes);
            PastYouNote = note;
            Stage = RunStage.Choosing;
        });
    }

    private void SelectMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        this.RaiseAndSetIfChanged(ref _selectedMinutes, minutes, nameof(SelectedMinutes));
        foreach (var option in DurationOptions)
            option.IsSelected = option.Minutes == minutes;
    }

    private void Begin()
    {
        _tracker = new RunTracker(_counter);
        _startedAt = _scheduler.Now;
        _planned = TimeSpan.FromMinutes(SelectedMinutes);
        _projectWordsAtStart = _workspace.TotalWordCount;
        _projectTarget = ProjectTarget();
        _workspaceRoot = _workspace.WorkspaceRoot;
        _workspaceName = _workspace.WorkspaceName ?? "";
        _finishing = false;
        _lastNet = 0;
        _peakNet = 0;
        _timeline.Clear();
        _comboUntil = DateTimeOffset.MinValue;
        ComboText = null;
        Intensity = RunIntensity.Idle;
        IsFinalStretch = false;
        _ = _settings.SetAsync(LastMinutesKey, SelectedMinutes);

        Stage = RunStage.Running;
        _soundscape.Start();
        ObserveActive();
        Tick();
        _running.Disposable = Observable.Interval(TimeSpan.FromSeconds(1), _scheduler)
            .Subscribe(_ => Tick());
    }

    /// <summary>Updates the HUD; when the time is up, finishes the run. Runs every second while running.</summary>
    public void Tick()
    {
        if (Stage != RunStage.Running || _tracker == null)
            return;

        var elapsed = _scheduler.Now - _startedAt;
        var remaining = _planned - elapsed;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        RemainingDisplay = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        Progress = Math.Clamp(elapsed.TotalSeconds / _planned.TotalSeconds * 100, 0, 100);
        LiveNetDisplay = $"{Signed(_tracker.LiveNet)} {Words(_tracker.LiveNet)}";
        Intensity = RunIntensity.At(remaining, _planned);
        IsFinalStretch = RunIntensity.IsFinalStretch(remaining, _planned);
        _soundscape.Update(Progress / 100, Intensity);
        while (elapsed >= TimeSpan.FromMinutes(_timeline.Count + 1) && _timeline.Count * 60 < _planned.TotalSeconds)
            CloseMinute(60);
        if (ComboText != null && _scheduler.Now >= _comboUntil)
            ComboText = null;

        if (remaining == TimeSpan.Zero)
            _ = FinishAsync(completed: true);
    }

    private void Observe(ChapterNode node, string text)
    {
        if (_tracker == null)
            return;
        _tracker.Observe(node.RelativePath, node.Title, text);
        var net = _tracker.LiveNet;
        LiveNetDisplay = $"{Signed(net)} {Words(net)}";

        if (net > _lastNet)
        {
            Sparks += net - _lastNet;
            // Combo callouts belong to the Sparks layer: turned off in Settings → Runs, they don't show.
            if (Atmosphere.Sparks && RunCombos.Crossed(_peakNet, net) is { } combo)
            {
                ComboText = RunCombos.Callout(combo);
                _comboUntil = _scheduler.Now + ComboShown;
            }
            _peakNet = Math.Max(_peakNet, net);
        }
        _lastNet = net;
    }

    private void ObserveActive()
    {
        // A chapter ActiveNode means the buffer holds that chapter: Book.txt clears ActiveNode before it loads.
        // IsBookSelected is not checked, because it is still true when ActiveNode changes on the way from
        // Book.txt to a chapter, and the chapter would then get no baseline until its first edit.
        if (_editor.ActiveNode is { Kind: NodeKind.Chapter } node)
            Observe(node, _editor.Text);
    }

    private async Task FinishAsync(bool completed)
    {
        if (Stage != RunStage.Running || _tracker == null || _finishing)
            return;
        _finishing = true;
        _running.Disposable = null;
        ObserveActive();
        _soundscape.Finish(completed);
        ComboText = null;

        var tracker = _tracker;
        var elapsed = _scheduler.Now - _startedAt;
        if (completed || elapsed > _planned)
            elapsed = _planned;
        var seconds = (int)Math.Round(elapsed.TotalSeconds);
        var summary = tracker.Summarize();
        var tail = seconds - _timeline.Count * 60;
        if (tail > 0 || _timeline.Count == 0)
            CloseMinute(Math.Max(0, tail));
        else
            FoldIntoLastMinute(); // edits in the fraction of a second after the last tick's minute closed

        if (summary.Total.Touched == 0 && seconds < 60)
        {
            _lastRunId = null;
            Stage = RunStage.Idle;
            _notifications.ShowInfo("Run ended with no writing, so it wasn't recorded.");
            return;
        }

        var localDate = DateOnly.FromDateTime(_startedAt.ToLocalTime().DateTime);
        var session = new RunRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = _startedAt,
            LocalDate = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PlannedSeconds = (int)_planned.TotalSeconds,
            ActualSeconds = seconds,
            Completed = completed,
            Workspace = _workspaceName,
            WorkspacePath = string.IsNullOrEmpty(_workspaceRoot) ? null : _workspaceRoot,
            WordsAdded = summary.Total.Added,
            WordsCut = summary.Total.Cut,
            WordsNet = summary.Total.Net,
            Chapters = summary.Chapters.Count,
            MinuteAdded = _timeline.Select(m => m.Added).ToArray(),
            MinuteCut = _timeline.Select(m => m.Cut).ToArray(),
        };

        _lastRunId = session.Id;
        StreakOutcome? streak = null;
        IReadOnlyList<LogbookDay> streakDays = [];
        string? profileError = null;
        // Anything thrown here must still reach the recap: the run is already finishing (_finishing is set and the
        // timer stopped), so an escaped exception would leave it stuck in Running with End doing nothing.
        try
        {
            var earlier = await _profile.LoadRunsAsync();
            if (earlier.IsSuccess)
            {
                var saved = await _profile.AppendRunAsync(session);
                if (saved.IsSuccess)
                {
                    streak = StreakCalculator.Apply(
                        earlier.Value!.Select(s => DateOnly.Parse(s.LocalDate, CultureInfo.InvariantCulture)), localDate);
                    var today = DateOnly.FromDateTime(_scheduler.Now.ToLocalTime().DateTime);
                    streakDays = RunLogbook.Build(earlier.Value!.Append(session), today).Heatmap;
                }
                else
                    profileError = $"This run wasn't recorded: {saved.Error}";
            }
            else
            {
                profileError = $"This run wasn't recorded: {earlier.Error}";
            }
        }
        catch (Exception ex)
        {
            profileError = $"This run wasn't recorded: {ex.Message}";
        }

        await OnUiAsync(() =>
        {
            // The workspace was closed or switched while the profile was being written: the run is abandoned.
            if (Stage != RunStage.Running)
                return;
            Recap = BuildRecap(summary, seconds, completed, streak, profileError) with { StreakDays = streakDays };
            CaptureLeftOff(tracker);
            FutureYouText = "";
            Intensity = completed ? 1 : RunIntensity.Idle;
            IsFinalStretch = false;
            Stage = RunStage.Recap;
            if (completed)
                Celebrations++;
        });
    }

    /// <summary>Ends the current minute of the timeline, <paramref name="seconds"/> long.</summary>
    private void CloseMinute(int seconds)
    {
        if (_tracker == null)
            return;
        var delta = _tracker.TakeInterval();
        var previous = _timeline.Count > 0 ? _timeline[^1] : null;
        var totalAdded = (previous?.TotalAdded ?? 0) + delta.Added;
        var totalCut = (previous?.TotalCut ?? 0) + delta.Cut;
        _timeline.Add(new RunRecapMinute(_timeline.Count + 1, seconds, delta.Added, delta.Cut, totalAdded, totalCut,
            totalAdded - totalCut));
    }

    /// <summary>Adds any words changed since the last minute closed to that minute.</summary>
    private void FoldIntoLastMinute()
    {
        if (_tracker == null || _timeline.Count == 0)
            return;
        var delta = _tracker.TakeInterval();
        if (delta.Touched == 0)
            return;
        var last = _timeline[^1];
        _timeline[^1] = last with
        {
            Added = last.Added + delta.Added,
            Cut = last.Cut + delta.Cut,
            TotalAdded = last.TotalAdded + delta.Added,
            TotalCut = last.TotalCut + delta.Cut,
            TotalNet = last.TotalNet + delta.Net,
        };
    }

    private RunRecap BuildRecap(RunSummary summary, int seconds, bool completed, StreakOutcome? streak,
        string? profileError)
    {
        var total = summary.Total;
        var minutes = seconds / 60.0;
        var wpm = minutes >= 0.5 ? (int)Math.Round(Math.Max(0, total.Net) / minutes) : 0;
        var xp = RunScoring.Xp(total.Added, total.Cut, seconds, completed);

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
            if (RunScoring.MilestoneCrossed(percentBefore, percentAfter) is { } m)
                milestone = m == 100 ? "Target reached! The draft hits 100%." : $"Milestone: you crossed {m}% of your target.";
        }

        var duration = FormatDuration(seconds);
        var summaryLine = total.Touched == 0
            ? $"{duration} at the desk. Showing up counts."
            : $"{Signed(total.Net)} {Words(total.Net)} in {duration}.";

        return new RunRecap
        {
            Headline = completed ? "RUN COMPLETE" : "RUN ENDED",
            Summary = summaryLine,
            Added = $"+{total.Added:N0}",
            Cut = $"−{total.Cut:N0}",
            Net = Signed(total.Net),
            Minutes = duration,
            WordsPerMinute = wpm.ToString("N0"),
            Xp = $"+{xp:N0} XP",
            ChurnLabel = RunScoring.Label(RunScoring.Churn(total)),
            AddedPercent = total.Touched == 0 ? 0 : 100.0 * total.Added / total.Touched,
            HasChanges = total.Touched > 0,
            Chapters = summary.Chapters
                .Select(c => new RunRecapChapter(c.Title, Signed(c.Delta.Net),
                    $"+{c.Delta.Added:N0} / −{c.Delta.Cut:N0}"))
                .ToList(),
            ProjectWords = $"{before:N0} → {after:N0} words",
            Timeline = _timeline.ToList(),
            BestMinute = _timeline.Count > 1 && _timeline.MaxBy(m => m.Net) is { Net: > 0 } best
                ? $"Best minute: {Signed(best.Net)} (minute {best.Minute})"
                : null,
            TargetProgress = targetProgress,
            TargetPercentAfter = Math.Min(100, percentAfter),
            Milestone = milestone,
            Streak = streak?.Describe(),
            BestStreak = streak is { } s ? $"Best streak: {(s.Best == 1 ? "1 day" : $"{s.Best} days")}" : null,
            ProfileError = profileError,
        };
    }

    private void CaptureLeftOff(RunTracker tracker)
    {
        var key = tracker.LastKey;
        if (key == null && _editor.ActiveNode is { Kind: NodeKind.Chapter } active)
            key = active.RelativePath;

        _leftOffPath = key;
        LeftOffChapter = key == null ? null : tracker.TitleOf(key) ?? _editor.ActiveNode?.Title ?? key;
        LeftOffLine = key == null ? null : RunTracker.LastLine(tracker.LatestText(key) ?? _editor.Text);
    }

    /// <summary>A note typed on the recap that Done hasn't saved yet (the window asks before closing over it).</summary>
    public bool HasPendingNote => Stage == RunStage.Recap && !string.IsNullOrWhiteSpace(FutureYouText);

    /// <summary>
    /// Saves a note typed on the recap but not yet saved, as when the window closes or the app restarts. True when
    /// there was nothing to save or it was saved; false (the text kept) when it couldn't be.
    /// </summary>
    public async Task<bool> FlushNoteAsync()
    {
        if (!HasPendingNote)
            return true;
        var result = await SaveNoteAsync();
        if (result.IsSuccess)
            await OnUiAsync(() => FutureYouText = "");
        return result.IsSuccess;
    }

    private async Task DoneAsync()
    {
        if (string.IsNullOrWhiteSpace(FutureYouText))
        {
            Stage = RunStage.Idle;
            return;
        }

        var result = await SaveNoteAsync();
        await OnUiAsync(() =>
        {
            if (!result.IsSuccess)
            {
                _notifications.ShowError($"Your note wasn't saved: {result.Error}");
                return;
            }
            FutureYouText = "";
            Stage = RunStage.Idle;
            _notifications.ShowInfo("Note saved. Future You will see it when the next run starts.");
        });
    }

    private Task<Hymnal.Core.Common.Result<Hymnal.Core.Common.Unit>> SaveNoteAsync()
    {
        var note = new FutureYouNote
        {
            CreatedAt = _scheduler.Now,
            Text = FutureYouText.Trim(),
            RunId = _lastRunId,
            ChapterPath = _leftOffPath,
            ChapterTitle = LeftOffChapter,
            LastLine = LeftOffLine,
        };
        return _notes.AddAsync(_workspaceRoot, note);
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

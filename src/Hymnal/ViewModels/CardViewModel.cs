using System;
using System.Globalization;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// Live projection of a single <see cref="ChapterViewModel"/> into the values the
/// corkboard card surface needs to render.
/// </summary>
public sealed class CardViewModel : ViewModelBase, IDisposable
{
    private readonly ChapterViewModel _chapter;

    public ChapterViewModel Chapter => _chapter;

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    private string _relativePath = string.Empty;
    public string RelativePath
    {
        get => _relativePath;
        private set => this.RaiseAndSetIfChanged(ref _relativePath, value);
    }

    private ChapterStatus _status;
    public ChapterStatus Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string StatusDisplay => _chapter.StatusLabel;

    public string StatusBrushKey => $"corkboard-status-{Status.ToString().ToLowerInvariant()}";

    private int _wordCount;
    public int WordCount
    {
        get => _wordCount;
        private set => this.RaiseAndSetIfChanged(ref _wordCount, value);
    }

    private bool _wordCountKnown;
    public bool WordCountKnown
    {
        get => _wordCountKnown;
        private set => this.RaiseAndSetIfChanged(ref _wordCountKnown, value);
    }

    public string WordCountDisplay => WordCountKnown ? $"{WordCount:N0} w" : "—";

    private WordCountTarget? _target;
    public WordCountTarget? Target
    {
        get => _target;
        private set => this.RaiseAndSetIfChanged(ref _target, value);
    }

    public string TargetDisplay => FormatTargetDisplay(Target, _chapter.Profile);

    public double ProximityFill => CalculateProximityFill(Target, WordCount);

    private PhaseData? _phaseData;
    public PhaseData? PhaseData
    {
        get => _phaseData;
        private set => this.RaiseAndSetIfChanged(ref _phaseData, value);
    }

    public string PhaseStartDateDisplay => FormatDateDisplay(PhaseData?.PhaseStartDate);

    public string PhaseEndDateDisplay => FormatDateDisplay(PhaseData?.PhaseEndDate);

    private bool _isMissing;
    public bool IsMissing
    {
        get => _isMissing;
        private set => this.RaiseAndSetIfChanged(ref _isMissing, value);
    }

    public string MissingStateDisplay => IsMissing ? "Missing file" : string.Empty;

    public bool CanCompletePhase => _chapter.CanCompletePhase;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public CardViewModel(ChapterViewModel chapter)
    {
        _chapter = chapter;

        RefreshFromNode();
        RefreshStatus();
        RefreshWordCount();
        RefreshTarget();
        RefreshPhaseData();

        Disposables.Add(
            chapter.WhenAnyValue(x => x.Node)
                .Subscribe(_ => RefreshFromNode()));

        Disposables.Add(
            chapter.WhenAnyValue(x => x.Status)
                .Subscribe(_ => RefreshStatus()));

        Disposables.Add(
            chapter.WhenAnyValue(x => x.WordCount)
                .Subscribe(_ => RefreshWordCount()));

        Disposables.Add(
            chapter.WhenAnyValue(x => x.WordCountKnown)
                .Subscribe(_ => RefreshWordCount()));

        Disposables.Add(
            chapter.WhenAnyValue(x => x.Target)
                .Subscribe(_ => RefreshTarget()));

        Disposables.Add(
            chapter.WhenAnyValue(x => x.PhaseData)
                .Subscribe(_ => RefreshPhaseData()));

        Disposables.Add(
            chapter.WhenAnyValue(x => x.CanCompletePhase)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(CanCompletePhase))));
    }

    private void RefreshFromNode()
    {
        var node = _chapter.Node;
        Title = node.Title;
        RelativePath = node.RelativePath;
        IsMissing = node.IsMissing;
        this.RaisePropertyChanged(nameof(MissingStateDisplay));
    }

    private void RefreshStatus()
    {
        Status = _chapter.Status;
        this.RaisePropertyChanged(nameof(StatusDisplay));
        this.RaisePropertyChanged(nameof(StatusBrushKey));
    }

    private void RefreshWordCount()
    {
        WordCount = _chapter.WordCount;
        WordCountKnown = _chapter.WordCountKnown;
        this.RaisePropertyChanged(nameof(WordCountDisplay));
        this.RaisePropertyChanged(nameof(ProximityFill));
    }

    private void RefreshTarget()
    {
        Target = _chapter.Target;
        this.RaisePropertyChanged(nameof(TargetDisplay));
        this.RaisePropertyChanged(nameof(ProximityFill));
    }

    private void RefreshPhaseData()
    {
        PhaseData = _chapter.PhaseData;
        this.RaisePropertyChanged(nameof(PhaseStartDateDisplay));
        this.RaisePropertyChanged(nameof(PhaseEndDateDisplay));
    }

    private static string FormatTargetDisplay(WordCountTarget? target, ProjectProfile profile)
    {
        if (target is null)
            return "No target";

        string text;
        if (target.MinWords.HasValue && target.MaxWords.HasValue)
            text = $"{target.MinWords:N0}–{target.MaxWords:N0} w";
        else if (target.MinWords.HasValue)
            text = $"{target.MinWords:N0} w";
        else if (target.MaxWords.HasValue)
            text = $"≤{target.MaxWords:N0} w";
        else
            return "No target";

        var label = profile.PresetLabel(target.Preset);
        return label is not null && target.Preset != "custom" ? $"{label} · {text}" : text;
    }

    private static double CalculateProximityFill(WordCountTarget? target, int wordCount)
    {
        if (target is null)
            return 0.0;

        var effectiveMax = target.MaxWords ?? target.MinWords ?? 1;
        return Math.Min((double)wordCount / effectiveMax, 1.0);
    }

    private static string FormatDateDisplay(string? raw)
    {
        var parsed = GanttProjection.ParseDate(raw);
        return parsed.HasValue
            ? parsed.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "—";
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Disposables.Dispose();
    }
}

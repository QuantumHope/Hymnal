using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Hymnal.Core.Models;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// One beat on a beat board. <see cref="Title"/> and <see cref="Note"/> are edited in place and every
/// edit is reported through <see cref="Edited"/> so the board can debounce a save. The scene fields
/// mirror the linked chapter (word count, display status) and update live while it is attached.
/// </summary>
public sealed class BeatCardViewModel : ViewModelBase, IDisposable
{
    private readonly Subject<Unit> _edited = new();
    private readonly Subject<Unit> _sceneMoved = new();
    private IDisposable? _sceneSubscription;

    public BeatCardViewModel(Beat beat)
    {
        Id = beat.Id;
        _title = beat.Title;
        _note = beat.Note;
        _scenePath = beat.ScenePath;
        SceneId = beat.SceneId;
    }

    /// <summary>Fires after any user edit to <see cref="Title"/> or <see cref="Note"/>.</summary>
    public IObservable<Unit> Edited => _edited.AsObservable();

    /// <summary>Fires when the linked scene (found by <see cref="SceneId"/>) moved, so <see cref="ScenePath"/> changed.</summary>
    public IObservable<Unit> SceneMoved => _sceneMoved.AsObservable();

    /// <summary>Registry UUID of the linked scene; null when unlinked or not known (older board files).</summary>
    public string? SceneId { get; private set; }

    public string Id { get; }

    private int _number;
    /// <summary>Its place on the board, from 1 (kept current by <see cref="BeatBoardViewModel"/>).</summary>
    public int Number
    {
        get => _number;
        set
        {
            if (_number == value)
                return;
            this.RaiseAndSetIfChanged(ref _number, value);
            this.RaisePropertyChanged(nameof(Numeral));
        }
    }

    /// <summary>The number as a Roman numeral, as a book numbers its chapters ("IV").</summary>
    public string Numeral => ToRoman(Number);

    internal static string ToRoman(int number)
    {
        if (number <= 0)
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (var (value, symbol) in new[]
                 {
                     (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
                     (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
                 })
            while (number >= value)
            {
                sb.Append(symbol);
                number -= value;
            }
        return sb.ToString();
    }

    private string _title;
    public string Title
    {
        get => _title;
        set => SetEdited(ref _title, value ?? "", nameof(Title));
    }

    private string _note;
    public string Note
    {
        get => _note;
        set => SetEdited(ref _note, value ?? "", nameof(Note));
    }

    private string? _scenePath;
    /// <summary>RelativePath of the linked scene, or null when unlinked.</summary>
    public string? ScenePath
    {
        get => _scenePath;
        private set
        {
            if (string.Equals(_scenePath, value, StringComparison.Ordinal))
                return;
            _scenePath = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(SelectedScene));
        }
    }

    private IReadOnlyList<SceneOption> _sceneChoices = [];
    private Action<string?>? _linkScene;

    /// <summary>Scene picker items: <see cref="BeatBoardViewModel.NoScene"/>, then the board's scenes.</summary>
    public IReadOnlyList<SceneOption> SceneChoices => _sceneChoices;

    /// <summary>
    /// Scene picker value: <see cref="BeatBoardViewModel.NoScene"/> when unlinked, the linked scene's
    /// option, or null when the linked scene is missing. Choosing an option asks the board to link it;
    /// null (a picker whose items were swapped writes it back) is ignored.
    /// </summary>
    public SceneOption? SelectedScene
    {
        get => ScenePath == null
            ? BeatBoardViewModel.NoScene
            : _sceneChoices.FirstOrDefault(o => o != BeatBoardViewModel.NoScene
                && string.Equals(o.RelativePath, ScenePath, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value == null || _linkScene == null)
                return;
            var path = value == BeatBoardViewModel.NoScene ? null : value.RelativePath;
            if (!string.Equals(path, ScenePath, StringComparison.Ordinal))
                _linkScene(path);
            // A refused link leaves the picker showing the current scene.
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Sets the picker items and how a picked scene is linked (by the owning board).</summary>
    internal void SetSceneChoices(IReadOnlyList<SceneOption> choices, Action<string?> linkScene)
    {
        _linkScene = linkScene;
        _sceneChoices = choices;
        this.RaisePropertyChanged(nameof(SceneChoices));
        this.RaisePropertyChanged(nameof(SelectedScene));
    }

    private string? _sceneTitle;
    /// <summary>Title of the linked scene; its path when the scene is missing; null when unlinked.</summary>
    public string? SceneTitle
    {
        get => _sceneTitle;
        private set => this.RaiseAndSetIfChanged(ref _sceneTitle, value);
    }

    private int? _sceneWordCount;
    /// <summary>The linked scene's word count; null when unlinked, missing, or not counted yet.</summary>
    public int? SceneWordCount
    {
        get => _sceneWordCount;
        private set => this.RaiseAndSetIfChanged(ref _sceneWordCount, value);
    }

    private string? _sceneStatusLabel;
    /// <summary>The linked scene's display status; null when unlinked or missing.</summary>
    public string? SceneStatusLabel
    {
        get => _sceneStatusLabel;
        private set => this.RaiseAndSetIfChanged(ref _sceneStatusLabel, value);
    }

    private bool _isSceneMissing;
    /// <summary>True when a scene is linked but is no longer in the book.</summary>
    public bool IsSceneMissing
    {
        get => _isSceneMissing;
        private set => this.RaiseAndSetIfChanged(ref _isSceneMissing, value);
    }

    private bool _isCurrent;
    /// <summary>
    /// True when this beat is linked to the scene open in the editor (Write mode's Beats panel). Display
    /// only: never saved and never counts as an edit.
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
    }

    public Beat ToBeat() => new(Id, Title, Note, ScenePath, SceneId);

    /// <summary>Changes the link; the owner then attaches the matching scene and saves.</summary>
    internal void SetScene(string? relativePath, string? sceneId)
    {
        ScenePath = relativePath;
        SceneId = relativePath == null ? null : sceneId;
    }

    /// <summary>Records the id of the scene found at <see cref="ScenePath"/> when the board file had none.</summary>
    internal void RememberSceneId(string sceneId) => SceneId ??= sceneId;

    /// <summary>
    /// Points the link at <paramref name="scene"/>, found by <see cref="SceneId"/>. Returns true when
    /// <see cref="ScenePath"/> changed (the scene was renamed), which needs saving.
    /// </summary>
    internal bool FollowScene(ChapterViewModel scene)
    {
        if (string.Equals(ScenePath, scene.Node.RelativePath, StringComparison.Ordinal))
            return false;
        ScenePath = scene.Node.RelativePath;
        return true;
    }

    /// <summary>
    /// Shows <paramref name="scene"/> (the chapter at <see cref="ScenePath"/>, or null when it isn't in the
    /// book) and follows its word count and status until another scene is attached.
    /// </summary>
    internal void AttachScene(ChapterViewModel? scene)
    {
        _sceneSubscription?.Dispose();
        _sceneSubscription = null;

        if (ScenePath is null)
        {
            ShowScene(null, null, null, missing: false);
            return;
        }

        if (scene == null)
        {
            ShowScene(ScenePath, null, null, missing: true);
            return;
        }

        _sceneSubscription = scene
            .WhenAnyValue(s => s.Node, s => s.WordCountKnown, s => s.WordCount, s => s.StatusLabel,
                (node, known, count, label) => (node, known, count, label))
            .Subscribe(s =>
            {
                // The chapter can be renamed or excluded in place. A link by id follows a rename;
                // otherwise the link no longer resolves.
                if (SceneId != null && scene.Uuid == SceneId && !s.node.IsExcluded)
                {
                    if (FollowScene(scene))
                        _sceneMoved.OnNext(Unit.Default);
                }
                else if (ScenePath is not { } path || !IsSameScene(s.node, path))
                {
                    ShowScene(ScenePath, null, null, missing: true);
                    return;
                }
                ShowScene(s.node.Title, s.known ? s.count : null, s.label, missing: false);
            });
    }

    /// <summary>True when <paramref name="node"/> is the book entry at <paramref name="relativePath"/>.</summary>
    internal static bool IsSameScene(ChapterNode node, string relativePath) =>
        !node.IsExcluded && string.Equals(node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase);

    private void ShowScene(string? title, int? wordCount, string? statusLabel, bool missing)
    {
        SceneTitle = title;
        SceneWordCount = wordCount;
        SceneStatusLabel = statusLabel;
        IsSceneMissing = missing;
    }

    private void SetEdited(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;
        field = value;
        this.RaisePropertyChanged(propertyName);
        _edited.OnNext(Unit.Default);
    }

    public void Dispose()
    {
        _sceneSubscription?.Dispose();
        _sceneSubscription = null;
        Disposables.Dispose();
        _edited.OnCompleted();
        _edited.Dispose();
        _sceneMoved.OnCompleted();
        _sceneMoved.Dispose();
    }
}

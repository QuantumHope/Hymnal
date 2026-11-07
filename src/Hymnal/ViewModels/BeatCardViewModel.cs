using System;
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
    private IDisposable? _sceneSubscription;

    public BeatCardViewModel(Beat beat)
    {
        Id = beat.Id;
        _title = beat.Title;
        _note = beat.Note;
        _scenePath = beat.ScenePath;
    }

    /// <summary>Fires after any user edit to <see cref="Title"/> or <see cref="Note"/>.</summary>
    public IObservable<Unit> Edited => _edited.AsObservable();

    public string Id { get; }

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
        private set => this.RaiseAndSetIfChanged(ref _scenePath, value);
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

    public Beat ToBeat() => new(Id, Title, Note, ScenePath);

    /// <summary>Changes the link; the owner then attaches the matching scene and saves.</summary>
    internal void SetScenePath(string? relativePath) => ScenePath = relativePath;

    /// <summary>
    /// Shows <paramref name="scene"/> (the chapter at <see cref="ScenePath"/>, or null when it isn't in the
    /// book) and follows its word count and status until another scene is attached.
    /// </summary>
    internal void AttachScene(ChapterViewModel? scene)
    {
        _sceneSubscription?.Dispose();
        _sceneSubscription = null;

        if (ScenePath is not { } path)
        {
            ShowScene(null, null, null, missing: false);
            return;
        }

        if (scene == null)
        {
            ShowScene(path, null, null, missing: true);
            return;
        }

        _sceneSubscription = scene
            .WhenAnyValue(s => s.Node, s => s.WordCountKnown, s => s.WordCount, s => s.StatusLabel,
                (node, known, count, label) => (node, known, count, label))
            .Subscribe(s =>
            {
                // The chapter can be renamed or excluded in place; the link then no longer resolves.
                if (!IsSameScene(s.node, path))
                    ShowScene(path, null, null, missing: true);
                else
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
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// One story idea as shown on the Ideas card grid and edited in the side editor.
/// Every user edit is reported through <see cref="Edited"/> so the owner can debounce a save.
/// </summary>
public sealed class IdeaCardViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<TargetPreset> _presets;
    private readonly Subject<Unit> _edited = new();

    public IdeaCardViewModel(StoryIdea idea, IReadOnlyList<TargetPreset> presets)
    {
        _presets = presets;
        Id = idea.Id;
        CreatedAt = idea.CreatedAt;
        StoryId = idea.StoryId;
        _title = idea.Title;
        _premise = idea.Premise;
        _notes = idea.Notes;
        _tags = StoryIdeasService.NormalizeTags(idea.Tags);
        _tagsText = string.Join(", ", _tags);
        _targetPreset = idea.TargetPreset;
        _state = idea.State;
    }

    /// <summary>Fires after any user edit to an editable field.</summary>
    public IObservable<Unit> Edited => _edited.AsObservable();

    public string Id { get; }
    public DateOnly CreatedAt { get; }
    public string? StoryId { get; }

    private string _title;
    public string Title
    {
        get => _title;
        set => SetEdited(ref _title, value ?? "", nameof(Title));
    }

    private string _premise;
    public string Premise
    {
        get => _premise;
        set => SetEdited(ref _premise, value ?? "", nameof(Premise));
    }

    private string _notes;
    public string Notes
    {
        get => _notes;
        set => SetEdited(ref _notes, value ?? "", nameof(Notes));
    }

    private string _tagsText;
    /// <summary>Comma-separated tags as typed by the author.</summary>
    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (!SetEdited(ref _tagsText, value ?? "", nameof(TagsText)))
                return;
            var parsed = StoryIdeasService.NormalizeTags(_tagsText.Split(','));
            if (!parsed.SequenceEqual(_tags, StringComparer.Ordinal))
            {
                _tags = parsed;
                this.RaisePropertyChanged(nameof(Tags));
            }
        }
    }

    private IReadOnlyList<string> _tags;
    /// <summary>Tags parsed from <see cref="TagsText"/>.</summary>
    public IReadOnlyList<string> Tags => _tags;

    private string? _targetPreset;
    /// <summary>Key of the idea's target preset, or null for none.</summary>
    public string? TargetPreset
    {
        get => _targetPreset;
        set
        {
            if (SetEdited(ref _targetPreset, string.IsNullOrEmpty(value) ? null : value, nameof(TargetPreset)))
                this.RaisePropertyChanged(nameof(TargetLabel));
        }
    }

    /// <summary>Display label of <see cref="TargetPreset"/>, or empty when none is set.</summary>
    public string TargetLabel =>
        _presets.FirstOrDefault(p => p.Key == _targetPreset)?.Label ?? "";

    private IdeaState _state;
    public IdeaState State
    {
        get => _state;
        set => SetEdited(ref _state, value, nameof(State));
    }

    public StoryIdea ToIdea() =>
        new(Id, Title, Premise, Notes, Tags, TargetPreset, State, CreatedAt, StoryId);

    private bool SetEdited<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        this.RaisePropertyChanged(propertyName);
        _edited.OnNext(Unit.Default);
        return true;
    }

    public void Dispose()
    {
        Disposables.Dispose();
        _edited.OnCompleted();
        _edited.Dispose();
    }
}

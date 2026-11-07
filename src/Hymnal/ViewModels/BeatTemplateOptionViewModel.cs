using System.Collections.Generic;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// One template in the beat board's template picker, with the parameter value chosen for it
/// (the MICE thread) and the requests that apply it.
/// </summary>
public sealed class BeatTemplateOptionViewModel : ViewModelBase
{
    public BeatTemplateOptionViewModel(BeatTemplate template)
    {
        Template = template;
        _selectedParameter = template.Parameter?.Options.Count > 0 ? template.Parameter.Options[0] : null;
    }

    public BeatTemplate Template { get; }
    public string Key => Template.Key;
    public string Name => Template.Name;
    public string Description => Template.Description;
    public bool HasParameter => Template.Parameter != null;
    public IReadOnlyList<string> ParameterOptions => Template.Parameter?.Options ?? [];

    private string? _selectedParameter;
    /// <summary>The chosen parameter value; a picker writing null back (its items swapped) is ignored.</summary>
    public string? SelectedParameter
    {
        get => _selectedParameter;
        set
        {
            if (value == null || !HasParameter || value == _selectedParameter)
                return;
            _selectedParameter = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(AppendRequest));
            this.RaisePropertyChanged(nameof(ReplaceRequest));
        }
    }

    public ApplyTemplateRequest AppendRequest => new(Key, TemplateApplyMode.Append, SelectedParameter);
    public ApplyTemplateRequest ReplaceRequest => new(Key, TemplateApplyMode.Replace, SelectedParameter);
}

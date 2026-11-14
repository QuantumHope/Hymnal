using System.Collections.Generic;
using System.Linq;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>
/// One template in the beat board's template gallery: its thumbnail (tension curve and the beats it
/// adds), the parameter value chosen for it (the MICE thread) and the requests that apply it.
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

    /// <summary>The parameter's name for its picker label ("Thread"), or empty.</summary>
    public string ParameterLabel =>
        Template.Parameter is { Name: { Length: > 0 } name } ? char.ToUpperInvariant(name[0]) + name[1..] : "";

    /// <summary>The thumbnail's tension curve (0 calm … 1 peak); empty for a template without one.</summary>
    public IReadOnlyList<double> Shape => Template.Shape ?? [];

    /// <summary>False for a template without beats (Blank): appending it would add nothing.</summary>
    public bool CanAppend => Template.Beats.Count > 0;

    /// <summary>True for a template without beats; the gallery offers to start an empty board instead.</summary>
    public bool IsBlank => Template.Beats.Count == 0;

    public string BeatCountLabel => Template.Beats.Count switch
    {
        0 => "No beats",
        1 => "1 beat",
        var n => $"{n} beats"
    };

    /// <summary>The beats this template adds, numbered, with the chosen parameter filled in.</summary>
    public IReadOnlyList<string> BeatPreview
    {
        get
        {
            var token = Template.Parameter == null ? null : "{" + Template.Parameter.Name + "}";
            return Template.Beats
                .Select((b, i) => $"{i + 1}. " + (token == null || SelectedParameter == null
                    ? b.Title
                    : b.Title.Replace(token, SelectedParameter)))
                .ToList();
        }
    }

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
            this.RaisePropertyChanged(nameof(BeatPreview));
        }
    }

    public ApplyTemplateRequest AppendRequest => new(Key, TemplateApplyMode.Append, SelectedParameter);
    public ApplyTemplateRequest ReplaceRequest => new(Key, TemplateApplyMode.Replace, SelectedParameter);
}

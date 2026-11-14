namespace Hymnal.Core.Models;

public sealed record BeatTemplateBeat(string Title, string Note);

public sealed record BeatTemplateParameter(string Name, IReadOnlyList<string> Options);

/// <summary>
/// A built-in plot template that seeds a beat board. <paramref name="Shape"/> is the story's tension
/// curve for the template picker's thumbnail: levels from 0 (calm) to 1 (peak), left to right, or
/// null for no curve (Blank).
/// </summary>
public sealed record BeatTemplate(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<BeatTemplateBeat> Beats,
    BeatTemplateParameter? Parameter,
    IReadOnlyList<double>? Shape = null)
{
    /// <summary>
    /// Creates fresh beats from the template, replacing the <c>{Name}</c> placeholder with
    /// <paramref name="parameterValue"/> when the template has a parameter.
    /// </summary>
    /// <exception cref="ArgumentException">A parameter is required and the value is missing or not an option.</exception>
    public IReadOnlyList<Beat> Instantiate(string? parameterValue = null)
    {
        if (Parameter is null)
            return Beats.Select(b => Beat.Create(b.Title, b.Note)).ToList();

        if (parameterValue is null || !Parameter.Options.Contains(parameterValue))
            throw new ArgumentException(
                $"Template '{Key}' requires a '{Parameter.Name}' of: {string.Join(", ", Parameter.Options)}.",
                nameof(parameterValue));

        var token = "{" + Parameter.Name + "}";
        return Beats
            .Select(b => Beat.Create(b.Title.Replace(token, parameterValue), b.Note.Replace(token, parameterValue)))
            .ToList();
    }
}

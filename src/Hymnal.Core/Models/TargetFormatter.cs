namespace Hymnal.Core.Models;

/// <summary>Formats a word-count target for display, prefixing the preset label when one applies.</summary>
public static class TargetFormatter
{
    public static string Format(WordCountTarget? target, ProjectProfile profile, string emptyText)
    {
        if (target is null) return emptyText;

        string text;
        if (target.MinWords.HasValue && target.MaxWords.HasValue)
            text = $"{target.MinWords:N0}–{target.MaxWords:N0} w";
        else if (target.MinWords.HasValue)
            text = $"{target.MinWords:N0} w";
        else if (target.MaxWords.HasValue)
            text = $"≤{target.MaxWords:N0} w";
        else
            return emptyText;

        var label = profile.PresetLabel(target.Preset);
        return label is not null && target.Preset != "custom" ? $"{label} · {text}" : text;
    }
}

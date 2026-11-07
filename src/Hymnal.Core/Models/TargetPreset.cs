namespace Hymnal.Core.Models;

public sealed record TargetPreset(string Key, string Label, int? MinWords, int? MaxWords);

using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

public enum TemplateApplyMode { Append, Replace }

/// <summary>Pure list operations for a beat board. Each returns a new list; the input is never changed.</summary>
public static class BeatBoardOperations
{
    public static IReadOnlyList<Beat> ApplyTemplate(
        IReadOnlyList<Beat> current, IReadOnlyList<Beat> templateBeats, TemplateApplyMode mode) =>
        mode == TemplateApplyMode.Replace
            ? templateBeats.ToList()
            : current.Concat(templateBeats).ToList();

    /// <summary>Moves the beat at <paramref name="from"/> so it ends up at <paramref name="to"/> (clamped). An invalid <paramref name="from"/> leaves the list unchanged.</summary>
    public static IReadOnlyList<Beat> Move(IReadOnlyList<Beat> beats, int from, int to)
    {
        var list = beats.ToList();
        if (from < 0 || from >= list.Count)
            return list;
        var beat = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(to, 0, list.Count), beat);
        return list;
    }

    public static IReadOnlyList<Beat> Remove(IReadOnlyList<Beat> beats, string id) =>
        beats.Where(b => b.Id != id).ToList();

    public static IReadOnlyList<Beat> Update(IReadOnlyList<Beat> beats, Beat updated) =>
        beats.Select(b => b.Id == updated.Id ? updated : b).ToList();

    public static IReadOnlyList<Beat> Insert(IReadOnlyList<Beat> beats, Beat beat, int index)
    {
        var list = beats.ToList();
        list.Insert(Math.Clamp(index, 0, list.Count), beat);
        return list;
    }
}

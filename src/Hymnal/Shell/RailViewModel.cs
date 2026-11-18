using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;

namespace Hymnal.Shell;

/// <summary>
/// One shell rail: its <see cref="ShellPanel"/>s top to bottom, whether any of them shows (a collapsed rail shows
/// only its icon strip, whatever is open), and where its section splitters show. This replaces per-pane flags and
/// string switches: a rail works the same whatever panels are registered in it.
/// </summary>
public sealed class RailViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ObservableAsPropertyHelper<bool> _isAnyOpen;
    private readonly ObservableAsPropertyHelper<int> _openCount;

    public RailViewModel(RailSide side, IReadOnlyList<ShellPanel> panels, IObservable<bool> isCollapsed)
    {
        if (panels.Any(p => p.Side != side))
            throw new ArgumentException("Every panel must belong to this rail's side.", nameof(panels));
        if (panels.Select(p => p.Id).Distinct().Count() != panels.Count)
            throw new ArgumentException("Panel ids must be unique.", nameof(panels));

        Side = side;
        Panels = panels;

        var openStates = OpenStates();
        _openCount = openStates.Select(states => states.Count(o => o)).ToProperty(this, x => x.OpenCount);
        _isAnyOpen = openStates.CombineLatest(isCollapsed, (states, collapsed) => states.Any(o => o) && !collapsed)
            .DistinctUntilChanged()
            .ToProperty(this, x => x.IsAnyOpen);
        _disposables.Add(_openCount);
        _disposables.Add(_isAnyOpen);
    }

    public RailSide Side { get; }

    /// <summary>The panels, top to bottom.</summary>
    public IReadOnlyList<ShellPanel> Panels { get; }

    /// <summary>True when the rail is expanded and at least one panel is open.</summary>
    public bool IsAnyOpen => _isAnyOpen.Value;

    /// <summary>How many panels are open (whether or not the rail is collapsed).</summary>
    public int OpenCount => _openCount.Value;

    public ShellPanel this[string id] =>
        Panels.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"No '{id}' panel in the {Side} rail.");

    public bool Contains(string id) => Panels.Any(p => p.Id == id);

    /// <summary>
    /// Whether the splitter below panel <paramref name="id"/> shows: when the panel below it is open and some
    /// panel at or above it is open, so the splitter always resizes an open section against the space above.
    /// </summary>
    public IObservable<bool> SplitterBelow(string id)
    {
        var index = Panels.ToList().FindIndex(p => p.Id == id);
        if (index < 0 || index >= Panels.Count - 1)
            throw new ArgumentException($"'{id}' has no panel below it in the {Side} rail.", nameof(id));
        return OpenStates()
            .Select(states => states[index + 1] && states.Take(index + 1).Any(o => o))
            .DistinctUntilChanged();
    }

    /// <summary>Each panel's open state, in order, whenever any changes.</summary>
    private IObservable<IList<bool>> OpenStates() =>
        Panels.Select(p => p.WhenAnyValue(x => x.IsOpen)).CombineLatest();

    public void Dispose() => _disposables.Dispose();
}

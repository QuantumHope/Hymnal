using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using ReactiveUI;

namespace Hymnal.Shell;

/// <summary>Which rail a <see cref="ShellPanel"/> lives in.</summary>
public enum RailSide
{
    Left,
    Right,
}

/// <summary>
/// One section of a shell rail (Chapters, Docs, Info, Beats, Notes, AI Chat): its id, icon and tooltip for the
/// collapsed icon strip, whether it is available for this project and open now, and how to open or toggle it.
/// The panel owns no state of its own; it reads and drives the view model behind the section, so every way a
/// section opens (a shortcut, its header, a link elsewhere) stays in step.
/// </summary>
public sealed class ShellPanel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ObservableAsPropertyHelper<bool> _isOpen;
    private readonly ObservableAsPropertyHelper<bool> _isAvailable;
    private readonly ObservableAsPropertyHelper<string> _toolTip;
    private readonly Action _open;
    private readonly Action _toggle;

    /// <param name="id">Stable id, also the parameter of the rail commands ("info", "notes", …).</param>
    /// <param name="side">The rail it belongs to.</param>
    /// <param name="isOpen">Whether its content is open (ignoring whether its rail is collapsed).</param>
    /// <param name="open">Opens it; does nothing when it is already open.</param>
    /// <param name="toggle">Opens or closes it.</param>
    /// <param name="toolTip">The icon strip's tooltip (it may change, e.g. with the project's nouns).</param>
    /// <param name="isAvailable">Whether this project has it at all; always, when null.</param>
    public ShellPanel(string id, RailSide side, IObservable<bool> isOpen, Action open, Action toggle,
        IObservable<string> toolTip, IObservable<bool>? isAvailable = null)
    {
        Id = id;
        Side = side;
        _open = open;
        _toggle = toggle;
        _isOpen = isOpen.DistinctUntilChanged().ToProperty(this, x => x.IsOpen);
        _isAvailable = (isAvailable ?? Observable.Return(true)).DistinctUntilChanged()
            .ToProperty(this, x => x.IsAvailable);
        _toolTip = toolTip.ToProperty(this, x => x.ToolTip, initialValue: "");
        _disposables.Add(_isOpen);
        _disposables.Add(_isAvailable);
        _disposables.Add(_toolTip);
    }

    public string Id { get; }

    public RailSide Side { get; }

    /// <summary>The icon's path data (24×24 view box).</summary>
    public string IconData { get; init; } = "";

    /// <summary>True for an outlined icon (stroked); false for a filled one.</summary>
    public bool IconIsStroked { get; init; } = true;

    private Geometry? _icon;

    /// <summary><see cref="IconData"/> parsed, for the icon strip.</summary>
    public Geometry Icon => _icon ??= Geometry.Parse(IconData);

    public bool IsOpen => _isOpen.Value;

    public bool IsAvailable => _isAvailable.Value;

    public string ToolTip => _toolTip.Value;

    public void Open()
    {
        if (IsAvailable && !IsOpen)
            _open();
    }

    public void Toggle()
    {
        if (IsAvailable)
            _toggle();
    }

    public void Dispose() => _disposables.Dispose();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Search;
using Hymnal.Infrastructure;
using ReactiveUI;

namespace Hymnal.ViewModels;

/// <summary>What a palette entry does when chosen: run a command, or go somewhere in the project.</summary>
public enum PaletteEntryKind
{
    Command,
    Destination,
}

/// <summary>One row of the command palette.</summary>
/// <param name="Id">Stable identity, used to remember recently run commands.</param>
/// <param name="Title">What the row says, and what the query is matched against first.</param>
/// <param name="Category">A short caps label: the command's menu, or what the destination is (Chapter, Scene, Idea).</param>
/// <param name="Detail">Optional second line (where a scene sits, an idea's premise); also matched.</param>
/// <param name="Shortcut">The command's key gesture as shown in menus, if it has one.</param>
public sealed record PaletteEntry(
    string Id,
    PaletteEntryKind Kind,
    string Title,
    string Category,
    string? Detail,
    string? Shortcut,
    Func<Task> Run)
{
    /// <summary>The category as the row shows it: in capitals, as the app's other caps labels are written.</summary>
    public string CategoryLabel => Category.ToUpperInvariant();

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasShortcut => !string.IsNullOrWhiteSpace(Shortcut);
}

/// <summary>
/// The command palette (Ctrl+P to go to a chapter, scene, story or idea; Ctrl+Shift+P, or a query starting
/// with <c>&gt;</c>, to run a command). Entries come from <see cref="_source"/> each time it opens, so they
/// reflect what can be run and what the project holds at that moment.
/// </summary>
public sealed class CommandPaletteViewModel : ViewModelBase, IDisposable
{
    /// <summary>A query starting with this lists commands rather than places, as in VS Code.</summary>
    public const string CommandPrefix = ">";

    private const int MaxRecent = 8;

    private readonly Func<IReadOnlyList<PaletteEntry>> _source;
    private readonly NotificationService? _notifications;
    private readonly List<string> _recentCommandIds = new();
    private IReadOnlyList<PaletteEntry> _entries = [];

    public CommandPaletteViewModel(Func<IReadOnlyList<PaletteEntry>> source, NotificationService? notifications = null)
    {
        _source = source;
        _notifications = notifications;

        _isCommandMode = this.WhenAnyValue(x => x.Query)
            .Select(q => (q ?? "").StartsWith(CommandPrefix, StringComparison.Ordinal))
            .ToProperty(this, x => x.IsCommandMode);
        Disposables.Add(_isCommandMode);

        Disposables.Add(this.WhenAnyValue(x => x.Query).Subscribe(_ => Refresh()));

        // Only while open, so the window's Esc binding for it falls through to the next one otherwise.
        CloseCommand = ReactiveCommand.Create(Close, this.WhenAnyValue(x => x.IsOpen));
        RunSelectedCommand = ReactiveCommand.CreateFromTask(RunSelectedAsync);
        RunEntryCommand = ReactiveCommand.CreateFromTask<PaletteEntry>(RunAsync);
        foreach (var command in new IHandleObservableErrors[] { RunSelectedCommand, RunEntryCommand })
            Disposables.Add(command.ThrownExceptions.Subscribe(ex =>
                _notifications?.ShowError($"Command failed: {ex.Message}")));
    }

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    private string _query = "";
    public string Query
    {
        get => _query;
        set => this.RaiseAndSetIfChanged(ref _query, value ?? "");
    }

    private readonly ObservableAsPropertyHelper<bool> _isCommandMode;
    /// <summary>True while the query starts with <see cref="CommandPrefix"/>: the palette lists commands.</summary>
    public bool IsCommandMode => _isCommandMode.Value;

    private IReadOnlyList<PaletteEntry> _results = [];
    public IReadOnlyList<PaletteEntry> Results
    {
        get => _results;
        private set
        {
            this.RaiseAndSetIfChanged(ref _results, value);
            this.RaisePropertyChanged(nameof(HasNoResults));
        }
    }

    private PaletteEntry? _selected;
    public PaletteEntry? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public bool HasNoResults => Results.Count == 0;

    /// <summary>The query box's hint for the current mode.</summary>
    public string Placeholder => IsCommandMode ? "Run a command…" : "Go to… (type > to run a command)";

    /// <summary>What the list says when nothing matches.</summary>
    public string EmptyText => IsCommandMode ? "No matching commands" : "Nothing matches. Type > to run a command.";

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> RunSelectedCommand { get; }

    /// <summary>Runs one row (a click), closing the palette first.</summary>
    public ReactiveCommand<PaletteEntry, Unit> RunEntryCommand { get; }

    /// <summary>Opens on places to go (Ctrl+P).</summary>
    public void OpenGoTo() => Open("");

    /// <summary>Opens on commands (Ctrl+Shift+P).</summary>
    public void OpenCommands() => Open(CommandPrefix);

    private void Open(string query)
    {
        _entries = _source();
        // Raise even when the query is unchanged, so a reopened palette shows the fresh entries.
        _query = query;
        this.RaisePropertyChanged(nameof(Query));
        Refresh();
        IsOpen = true;
    }

    public void Dispose() => Disposables.Dispose();

    public void Close()
    {
        IsOpen = false;
        _entries = [];
        Results = [];
        Selected = null;
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows, wrapping at either end.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
            return;
        var index = Selected == null ? -1 : IndexOf(Selected);
        if (index < 0)
            index = delta > 0 ? -1 : 0;
        var next = ((index + delta) % Results.Count + Results.Count) % Results.Count;
        Selected = Results[next];
    }

    /// <summary>Closes the palette and runs <paramref name="entry"/> (a click on a row).</summary>
    public async Task RunAsync(PaletteEntry entry)
    {
        Close();
        if (entry.Kind == PaletteEntryKind.Command)
            Remember(entry.Id);
        await entry.Run();
    }

    private Task RunSelectedAsync() => Selected is { } entry ? RunAsync(entry) : Task.CompletedTask;

    private int IndexOf(PaletteEntry entry)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            if (ReferenceEquals(Results[i], entry))
                return i;
        }
        return -1;
    }

    private void Remember(string id)
    {
        _recentCommandIds.Remove(id);
        _recentCommandIds.Insert(0, id);
        if (_recentCommandIds.Count > MaxRecent)
            _recentCommandIds.RemoveAt(_recentCommandIds.Count - 1);
    }

    private void Refresh()
    {
        this.RaisePropertyChanged(nameof(EmptyText));
        this.RaisePropertyChanged(nameof(Placeholder));
        var commands = Query.StartsWith(CommandPrefix, StringComparison.Ordinal);
        var text = commands ? Query[CommandPrefix.Length..] : Query;
        var kind = commands ? PaletteEntryKind.Command : PaletteEntryKind.Destination;

        var candidates = _entries.Where(e => e.Kind == kind).ToList();
        List<PaletteEntry> results;
        if (string.IsNullOrWhiteSpace(text))
        {
            // Nothing typed: places in book order; commands with the recently run ones first.
            results = commands
                ? candidates
                    .OrderBy(e => _recentCommandIds.IndexOf(e.Id) is var r && r >= 0 ? r : int.MaxValue)
                    .ToList()
                : candidates;
        }
        else
        {
            results = candidates
                .Select((entry, order) => (entry, order,
                    score: FuzzyMatcher.BestScore(text, entry.Title, entry.Category, entry.Detail)))
                .Where(x => x.score != null)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.order)
                .Select(x => x.entry)
                .ToList();
        }

        Results = results;
        Selected = results.Count > 0 ? results[0] : null;
    }
}

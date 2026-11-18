using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Shell;
using Xunit;

namespace Hymnal.ViewModels;

/// <summary>The generic shell rail: open state, collapse, availability and where its splitters show.</summary>
public sealed class RailViewModelTests
{
    static RailViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private sealed class Pane
    {
        public BehaviorSubject<bool> Open { get; } = new(false);
        public BehaviorSubject<bool> Available { get; } = new(true);
        public ShellPanel Panel { get; }

        public Pane(string id, RailSide side = RailSide.Right) =>
            Panel = new ShellPanel(id, side, Open, () => Open.OnNext(true), () => Open.OnNext(!Open.Value),
                Observable.Return(id), Available);
    }

    private static (RailViewModel Rail, Dictionary<string, Pane> Panes, BehaviorSubject<bool> Collapsed) Rail(
        params string[] ids)
    {
        var panes = ids.ToDictionary(id => id, id => new Pane(id));
        var collapsed = new BehaviorSubject<bool>(false);
        return (new RailViewModel(RailSide.Right, ids.Select(id => panes[id].Panel).ToList(), collapsed), panes,
            collapsed);
    }

    [Fact]
    public void Any_open_follows_the_panels_and_a_collapsed_rail_shows_none()
    {
        var (rail, panes, collapsed) = Rail("info", "notes");
        Assert.False(rail.IsAnyOpen);

        panes["notes"].Open.OnNext(true);
        Assert.True(rail.IsAnyOpen);
        Assert.Equal(1, rail.OpenCount);

        collapsed.OnNext(true);
        Assert.False(rail.IsAnyOpen);
        Assert.Equal(1, rail.OpenCount); // collapsing keeps what is open

        collapsed.OnNext(false);
        Assert.True(rail.IsAnyOpen);
    }

    [Theory]
    // info, beats, notes, chat open → splitters below info, beats, notes
    [InlineData("1000", "000")]
    [InlineData("1100", "100")]  // info + beats
    [InlineData("1010", "010")]  // info + notes: the splitter above notes
    [InlineData("0011", "001")]  // notes + chat
    [InlineData("1001", "001")]  // info + chat with notes closed: chat can still be resized against info
    [InlineData("0101", "001")]
    [InlineData("1111", "111")]
    [InlineData("0001", "000")]  // nothing above chat
    public void A_splitter_shows_below_a_section_when_the_next_is_open_and_something_above_is(
        string open, string expected)
    {
        var ids = new[] { "info", "beats", "notes", "chat" };
        var (rail, panes, _) = Rail(ids);
        for (var i = 0; i < ids.Length; i++)
            panes[ids[i]].Open.OnNext(open[i] == '1');

        var actual = string.Concat(ids.Take(3).Select(id =>
            rail.SplitterBelow(id).FirstAsync().Wait() ? '1' : '0'));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void An_unavailable_panel_neither_opens_nor_toggles()
    {
        var (rail, panes, _) = Rail("beats", "notes");
        panes["beats"].Available.OnNext(false);

        rail["beats"].Open();
        rail["beats"].Toggle();
        Assert.False(panes["beats"].Open.Value);

        rail["notes"].Open();
        Assert.True(panes["notes"].Open.Value);
        rail["notes"].Open(); // already open: stays open
        Assert.True(panes["notes"].Open.Value);
        rail["notes"].Toggle();
        Assert.False(panes["notes"].Open.Value);
    }

    [Fact]
    public void Rails_refuse_duplicate_ids_and_panels_from_the_other_side()
    {
        var collapsed = Observable.Return(false);
        Assert.Throws<ArgumentException>(() =>
            new RailViewModel(RailSide.Right, [new Pane("a").Panel, new Pane("a").Panel], collapsed));
        Assert.Throws<ArgumentException>(() =>
            new RailViewModel(RailSide.Right, [new Pane("a", RailSide.Left).Panel], collapsed));

        var (rail, _, _) = Rail("info", "notes");
        Assert.Throws<KeyNotFoundException>(() => rail["chat"]);
        Assert.Throws<ArgumentException>(() => rail.SplitterBelow("notes")); // nothing below the last
    }
}

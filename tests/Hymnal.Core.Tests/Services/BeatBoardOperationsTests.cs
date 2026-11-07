using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class BeatBoardOperationsTests
{
    private static Beat B(string id) => new(id, "T" + id, "", null);
    private static IReadOnlyList<Beat> Board(params string[] ids) => ids.Select(B).ToList();
    private static string Ids(IReadOnlyList<Beat> beats) => string.Join(",", beats.Select(b => b.Id));

    [Fact]
    public void Append_keeps_existing_then_adds()
    {
        var result = BeatBoardOperations.ApplyTemplate(Board("a", "b"), Board("x", "y"), TemplateApplyMode.Append);
        Assert.Equal("a,b,x,y", Ids(result));
    }

    [Fact]
    public void Replace_returns_only_template_beats()
    {
        var result = BeatBoardOperations.ApplyTemplate(Board("a", "b"), Board("x", "y"), TemplateApplyMode.Replace);
        Assert.Equal("x,y", Ids(result));
    }

    [Fact]
    public void Move_forward() =>
        Assert.Equal("b,c,a,d", Ids(BeatBoardOperations.Move(Board("a", "b", "c", "d"), 0, 2)));

    [Fact]
    public void Move_backward() =>
        Assert.Equal("a,d,b,c", Ids(BeatBoardOperations.Move(Board("a", "b", "c", "d"), 3, 1)));

    [Fact]
    public void Move_clamps_target() =>
        Assert.Equal("b,c,a", Ids(BeatBoardOperations.Move(Board("a", "b", "c"), 0, 99)));

    [Fact]
    public void Move_clamps_negative_target() =>
        Assert.Equal("c,a,b", Ids(BeatBoardOperations.Move(Board("a", "b", "c"), 2, -5)));

    [Fact]
    public void Move_from_out_of_range_is_unchanged() =>
        Assert.Equal("a,b", Ids(BeatBoardOperations.Move(Board("a", "b"), 5, 0)));

    [Fact]
    public void Remove_by_id() =>
        Assert.Equal("a,c", Ids(BeatBoardOperations.Remove(Board("a", "b", "c"), "b")));

    [Fact]
    public void Remove_unknown_id_is_unchanged() =>
        Assert.Equal("a,b", Ids(BeatBoardOperations.Remove(Board("a", "b"), "zzz")));

    [Fact]
    public void Update_replaces_matching_beat_in_place()
    {
        var updated = new Beat("b", "New", "note", "s.md");
        var result = BeatBoardOperations.Update(Board("a", "b", "c"), updated);
        Assert.Equal("a,b,c", Ids(result));
        Assert.Equal(updated, result[1]);
    }

    [Fact]
    public void Update_unknown_id_is_unchanged() =>
        Assert.Equal("a,b", Ids(BeatBoardOperations.Update(Board("a", "b"), B("zzz"))));

    [Fact]
    public void Insert_at_index() =>
        Assert.Equal("a,x,b", Ids(BeatBoardOperations.Insert(Board("a", "b"), B("x"), 1)));

    [Fact]
    public void Insert_clamps_index()
    {
        Assert.Equal("a,b,x", Ids(BeatBoardOperations.Insert(Board("a", "b"), B("x"), 99)));
        Assert.Equal("x,a,b", Ids(BeatBoardOperations.Insert(Board("a", "b"), B("x"), -3)));
    }

    [Fact]
    public void Operations_do_not_mutate_input()
    {
        var original = Board("a", "b", "c");
        BeatBoardOperations.Move(original, 0, 2);
        BeatBoardOperations.Remove(original, "a");
        BeatBoardOperations.Insert(original, B("x"), 0);
        Assert.Equal("a,b,c", Ids(original));
    }
}

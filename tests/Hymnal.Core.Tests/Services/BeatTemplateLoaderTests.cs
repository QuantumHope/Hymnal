using System;
using System.Linq;
using Hymnal.Core.Services;
using Xunit;

namespace Hymnal.Core.Tests.Services;

public class BeatTemplateLoaderTests
{
    [Fact]
    public void All_has_seven_templates_in_order() =>
        Assert.Equal(
            ["threeAct", "tryFail", "sevenPoint", "kishotenketsu", "freytag", "mice", "blank"],
            BeatTemplateLoader.All.Select(t => t.Key));

    [Theory]
    [InlineData("threeAct", "Three-Act (short)", new[] { "Hook", "Inciting Incident", "Rising Complications", "Climax", "Resolution" })]
    [InlineData("tryFail", "Try/Fail Cycles", new[] { "Goal", "Try 1 (fail)", "Try 2 (fail, worse)", "Try 3 (success or final failure)", "Outcome" })]
    [InlineData("sevenPoint", "Seven-Point", new[] { "Hook", "Plot Turn 1", "Pinch 1", "Midpoint", "Pinch 2", "Plot Turn 2", "Resolution" })]
    [InlineData("kishotenketsu", "Kishōtenketsu", new[] { "Introduction", "Development", "Twist", "Reconciliation" })]
    [InlineData("freytag", "Freytag's Pyramid", new[] { "Exposition", "Rising Action", "Climax", "Falling Action", "Dénouement" })]
    [InlineData("blank", "Blank", new string[0])]
    public void Template_has_expected_name_and_beat_titles(string key, string name, string[] titles)
    {
        var t = BeatTemplateLoader.Find(key);
        Assert.NotNull(t);
        Assert.Equal(name, t.Name);
        Assert.Equal(titles, t.Beats.Select(b => b.Title));
        Assert.Null(t.Parameter);
    }

    [Fact]
    public void Mice_has_thread_parameter_and_name()
    {
        var t = BeatTemplateLoader.Find("mice");
        Assert.NotNull(t);
        Assert.Equal("MICE Quotient", t.Name);
        Assert.Equal("thread", t.Parameter!.Name);
        Assert.Equal(["Milieu", "Inquiry", "Character", "Event"], t.Parameter.Options);
        Assert.Equal(["Open: {thread}", "Close: {thread}"], t.Beats.Select(b => b.Title));
    }

    [Fact]
    public void Find_unknown_key_returns_null() => Assert.Null(BeatTemplateLoader.Find("nope"));

    [Fact]
    public void Every_template_has_description_and_every_beat_a_note()
    {
        foreach (var t in BeatTemplateLoader.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Key);
            Assert.All(t.Beats, b => Assert.False(string.IsNullOrWhiteSpace(b.Note), $"{t.Key}: {b.Title}"));
        }
    }

    [Fact]
    public void Blank_has_no_beats() => Assert.Empty(BeatTemplateLoader.Find("blank")!.Instantiate());

    [Fact]
    public void Mice_instantiate_with_thread()
    {
        var beats = BeatTemplateLoader.Find("mice")!.Instantiate("Character");
        Assert.Equal(["Open: Character", "Close: Character"], beats.Select(b => b.Title));
        Assert.All(beats, b => Assert.DoesNotContain("{", b.Note));
        Assert.All(beats, b => Assert.Null(b.ScenePath));
    }

    [Fact]
    public void Mice_without_parameter_throws() =>
        Assert.Throws<ArgumentException>(() => BeatTemplateLoader.Find("mice")!.Instantiate());

    [Fact]
    public void Mice_with_unknown_parameter_throws() =>
        Assert.Throws<ArgumentException>(() => BeatTemplateLoader.Find("mice")!.Instantiate("Weather"));

    [Fact]
    public void Instantiate_gives_fresh_ids_each_call()
    {
        var t = BeatTemplateLoader.Find("threeAct")!;
        var first = t.Instantiate().Select(b => b.Id).ToList();
        var second = t.Instantiate().Select(b => b.Id).ToList();
        Assert.Equal(5, first.Distinct().Count());
        Assert.Empty(first.Intersect(second));
    }
}

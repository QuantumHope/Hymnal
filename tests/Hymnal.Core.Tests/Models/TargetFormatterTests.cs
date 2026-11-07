using Hymnal.Core.Models;

namespace Hymnal.Core.Tests.Models;

public sealed class TargetFormatterTests
{
    private static readonly ProjectProfile Story = ProjectProfile.For(new ProjectSettings(ProjectKind.Story));

    [Fact]
    public void Null_target_returns_empty_text() =>
        Assert.Equal("—", TargetFormatter.Format(null, ProjectProfile.Novel, "—"));

    [Fact]
    public void Novel_output_is_unprefixed()
    {
        var t = new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" };
        Assert.Equal("1,000–7,500 w", TargetFormatter.Format(t, ProjectProfile.Novel, "—"));
        Assert.Equal("5,000 w", TargetFormatter.Format(new WordCountTarget { MinWords = 5000 }, ProjectProfile.Novel, "—"));
        Assert.Equal("≤900 w", TargetFormatter.Format(new WordCountTarget { MaxWords = 900 }, ProjectProfile.Novel, "—"));
        Assert.Equal("No target", TargetFormatter.Format(new WordCountTarget(), ProjectProfile.Novel, "No target"));
    }

    [Fact]
    public void Story_preset_is_prefixed_but_custom_is_not()
    {
        var t = new WordCountTarget { MinWords = 1000, MaxWords = 7500, Preset = "shortStory" };
        Assert.Equal("Short Story · 1,000–7,500 w", TargetFormatter.Format(t, Story, "—"));
        var c = new WordCountTarget { MinWords = 1, MaxWords = 2, Preset = "custom" };
        Assert.Equal("1–2 w", TargetFormatter.Format(c, Story, "—"));
    }
}

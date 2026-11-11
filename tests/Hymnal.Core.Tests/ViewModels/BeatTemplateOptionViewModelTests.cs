using System.Linq;
using Hymnal.Core.Services;
using Hymnal.ViewModels;
using Xunit;

namespace Hymnal.Core.Tests.ViewModels;

public sealed class BeatTemplateOptionViewModelTests
{
    [Fact]
    public void Every_template_with_beats_has_a_thumbnail_curve_and_blank_has_none()
    {
        foreach (var template in BeatTemplateLoader.All)
        {
            var option = new BeatTemplateOptionViewModel(template);
            if (option.IsBlank)
            {
                Assert.Empty(option.Shape);
                continue;
            }

            Assert.True(option.Shape.Count >= 2, $"{template.Key} needs at least two levels to draw a curve.");
            Assert.All(option.Shape, level => Assert.InRange(level, 0, 1));
        }
    }

    [Fact]
    public void Preview_numbers_the_beats_and_counts_them()
    {
        var option = new BeatTemplateOptionViewModel(BeatTemplateLoader.Find("threeAct")!);

        Assert.Equal("5 beats", option.BeatCountLabel);
        Assert.Equal(
            ["1. Hook", "2. Inciting Incident", "3. Rising Complications", "4. Climax", "5. Resolution"],
            option.BeatPreview);
        Assert.False(option.IsBlank);
    }

    [Fact]
    public void Preview_fills_in_the_chosen_parameter()
    {
        var option = new BeatTemplateOptionViewModel(BeatTemplateLoader.Find("mice")!);
        var raised = new System.Collections.Generic.List<string?>();
        option.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("Thread", option.ParameterLabel);
        Assert.All(option.BeatPreview, line => Assert.DoesNotContain("{thread}", line));
        Assert.Contains(option.ParameterOptions[0], option.BeatPreview[0]);

        option.SelectedParameter = option.ParameterOptions[1];

        Assert.Contains(nameof(BeatTemplateOptionViewModel.BeatPreview), raised);
        Assert.Contains(option.ParameterOptions[1], option.BeatPreview[0]);
    }

    [Fact]
    public void Blank_is_marked_blank_with_no_beats()
    {
        var option = new BeatTemplateOptionViewModel(BeatTemplateLoader.Find("blank")!);

        Assert.True(option.IsBlank);
        Assert.Equal("No beats", option.BeatCountLabel);
        Assert.Empty(option.BeatPreview);
        Assert.Equal("", option.ParameterLabel);
    }
}

using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Models;
using Hymnal.Core.Tests.Infrastructure;
using Xunit;

namespace Hymnal.ViewModels;

public sealed class ProjectTypeDialogViewModelTests
{
    public ProjectTypeDialogViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public void Catalog_has_three_kinds_in_order()
    {
        var all = ProjectTypeCatalog.All;
        Assert.Equal(new[] { ProjectKind.Novel, ProjectKind.Story, ProjectKind.Anthology }, all.Select(i => i.Kind));
        Assert.Equal(new[] { "Novel", "Short Story", "Anthology" }, all.Select(i => i.Title));
        Assert.Equal(new[] { "One long work in chapters", "One standalone story", "A collection of stories" }, all.Select(i => i.Description));
        Assert.Equal(new[] { "Icon.ProjectNovel", "Icon.ProjectStory", "Icon.ProjectAnthology" }, all.Select(i => i.IconKey));
        Assert.Equal("Full toolset: 7-stage chapter lifecycle, phase scheduling, chapter targets and book-wide stats.", all[0].Impact);
        Assert.Equal("Simpler 4-stage status, story-length targets, and a Corkboard of beats from a plot template.", all[1].Impact);
        Assert.Equal("Short story tools for each story, plus collection order, Parts for long stories and total word count.", all[2].Impact);
        Assert.Equal("Statuses and tools will switch to the full novel toolset. No data is lost.", ProjectTypeCatalog.SwitchSummary(ProjectKind.Novel));
        Assert.Equal("Status labels and Corkboard will switch to short story tools. No data is lost.", ProjectTypeCatalog.SwitchSummary(ProjectKind.Story));
        Assert.Equal("Status labels, targets and stats will switch to anthology tools. No data is lost.", ProjectTypeCatalog.SwitchSummary(ProjectKind.Anthology));
    }

    [Fact]
    public async Task Create_disabled_until_selection()
    {
        var vm = new ProjectTypeDialogViewModel();
        Assert.Null(vm.SelectedKind);
        Assert.False(await vm.CreateCommand.CanExecute.FirstAsync());
        await vm.SelectCommand.Execute(vm.Cards[1]).FirstAsync();
        Assert.True(await vm.CreateCommand.CanExecute.FirstAsync());
    }

    [Fact]
    public async Task Select_marks_only_one_card_selected()
    {
        var vm = new ProjectTypeDialogViewModel();
        await vm.SelectCommand.Execute(vm.Cards[0]).FirstAsync();
        await vm.SelectCommand.Execute(vm.Cards[2]).FirstAsync();
        Assert.Equal(new[] { false, false, true }, vm.Cards.Select(c => c.IsSelected));
        Assert.Equal(ProjectKind.Anthology, vm.SelectedKind);
    }

    [Fact]
    public async Task Create_returns_selected_kind()
    {
        var vm = new ProjectTypeDialogViewModel();
        await vm.SelectCommand.Execute(vm.Cards[1]).FirstAsync();
        Assert.Equal(ProjectKind.Story, await vm.CreateCommand.Execute().FirstAsync());
    }
}

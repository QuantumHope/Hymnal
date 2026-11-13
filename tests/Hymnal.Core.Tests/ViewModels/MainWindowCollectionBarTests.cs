using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>The collection bar over Plan and Manage.</summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowCollectionBarTests
{
    static MainWindowCollectionBarTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Collection_bar_shows_over_Plan_and_Manage_but_not_Write()
    {
        var root = CreateWorkspace(AnthologyJson, ("lighthouse.md", "# Lighthouse\n\nThe lamp turns.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());

                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.False(window.ShowsCollectionBar);

                await Within(window.SelectPlanCommand.Execute().ToTask());
                Assert.True(window.ShowsCollectionBar);

                await Within(window.SelectManageCommand.Execute().ToTask());
                Assert.True(window.ShowsCollectionBar);

                await Within(window.SelectWriteCommand.Execute().ToTask());
                Assert.False(window.ShowsCollectionBar);
            });
        }
        finally { DeleteFolder(root); }
    }
}

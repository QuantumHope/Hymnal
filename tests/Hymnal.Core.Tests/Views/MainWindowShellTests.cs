using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ReactiveUI;
using ReactiveUI.Avalonia;
using Hymnal.Commands;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Shell;
using Hymnal.Views;
using Xunit;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.Core.Tests.Views;

/// <summary>
/// The real main window, headless: the menu bar it builds from the command catalog and the rails' icon strips it
/// builds from the panel registry.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowShellTests
{
    static MainWindowShellTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Window_builds_its_menu_bar_and_icon_strips_and_a_strip_button_opens_its_panel()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                // A shown window's buttons listen to their commands' CanExecute; as in the app, ReactiveUI must
                // deliver it on the UI thread (the tests otherwise leave its default scheduler in place).
                var previousScheduler = RxSchedulers.MainThreadScheduler;
                RxSchedulers.MainThreadScheduler = AvaloniaScheduler.Instance;
                try
                {
                    await ShowWindowAndCheckShellAsync(context);
                }
                finally
                {
                    RxSchedulers.MainThreadScheduler = previousScheduler;
                }
            });
        }
        finally { DeleteFolder(root); }
    }

    private static async Task ShowWindowAndCheckShellAsync(PlanTestContext context)
    {
        var vm = context.CreateMainWindow(context.CreateHost(), context.CreateWriteBeats());
        await PumpUntilAsync(() => vm.IsBeatsSectionAvailable);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await PumpUntilAsync(() => window.IsVisible);

        // The menu bar: the catalog's seven menus.
        var menu = window.FindControl<Menu>("PART_MainMenu")!;
        Assert.Equal(ShellMenus.Order,
            menu.Items.OfType<MenuItem>().Select(m => (string?)m.Header));

        // Collapse both rails: the icon strips show one button per available panel.
        vm.SetLeftRailCollapsed(true);
        vm.SetRightRailCollapsed(true);
        await PumpUntilAsync(() => !vm.IsAnyLeftPaneOpen && !vm.IsAnyRightPaneOpen);
        window.UpdateLayout();

        Button[] StripButtons(RailViewModel rail) => window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => ReferenceEquals(i.ItemsSource, rail.Panels))
            .GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();

        var left = StripButtons(vm.LeftRail);
        Assert.Equal(2, left.Length);
        var right = StripButtons(vm.RightRail);
        Assert.Equal(["info", "beats", "notes", "chat"], right.Select(b => (string?)b.CommandParameter));
        Assert.Equal("Notes (F4)", ToolTip.GetTip(right[2]));

        // A strip button expands its rail with that panel open.
        Assert.False(vm.AiChatViewModel.IsVisible);
        right[3].Command!.Execute(right[3].CommandParameter);
        await PumpUntilAsync(() => vm.IsAnyRightPaneOpen);
        Assert.True(vm.AiChatViewModel.IsVisible);
        Assert.False(vm.IsRightRailCollapsed);

        window.Close();
    }
}

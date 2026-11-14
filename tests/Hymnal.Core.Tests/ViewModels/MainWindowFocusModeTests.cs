using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>Focus mode (F11): Write mode with nothing but the text.</summary>
[Collection("AvaloniaUi")]
public sealed class MainWindowFocusModeTests
{
    static MainWindowFocusModeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task F11_enters_and_leaves_focus_and_Esc_only_acts_inside_it()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());

                Assert.False(window.IsFocusMode);
                Assert.False(await window.ExitFocusModeCommand.CanExecute.FirstAsyncTask());

                await Within(window.ToggleFocusModeCommand.Execute().ToTask());
                Assert.True(window.IsFocusMode);
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.True(await window.ExitFocusModeCommand.CanExecute.FirstAsyncTask());

                await Within(window.ExitFocusModeCommand.Execute().ToTask());
                Assert.False(window.IsFocusMode);

                await Within(window.ToggleFocusModeCommand.Execute().ToTask());
                await Within(window.ToggleFocusModeCommand.Execute().ToTask());
                Assert.False(window.IsFocusMode);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Focus_starts_from_any_mode_in_Write_and_ends_when_Write_is_left()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());

                await Within(window.ToggleGanttCommand.Execute().ToTask());
                Assert.Equal(ShellMode.Manage, window.ActiveMode);

                await Within(window.ToggleFocusModeCommand.Execute().ToTask());
                Assert.Equal(ShellMode.Write, window.ActiveMode);
                Assert.True(window.IsFocusMode);

                await Within(window.ToggleGanttCommand.Execute().ToTask());
                Assert.Equal(ShellMode.Manage, window.ActiveMode);
                Assert.False(window.IsFocusMode);
            });
        }
        finally { DeleteFolder(root); }
    }
}

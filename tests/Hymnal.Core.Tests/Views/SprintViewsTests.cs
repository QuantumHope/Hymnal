using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.Core.Tests.Views;

/// <summary>The sprint screens and HUD load, show the right screen for each stage, take focus and close on Esc.</summary>
[Collection("AvaloniaUi")]
public sealed class SprintViewsTests
{
    static SprintViewsTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Each_stage_shows_its_screen_and_Esc_backs_out()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nOne two three\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var sprint = context.CreateSprint();
                context.Workspace.SelectedNode = context.Node("a.md");
                await PumpUntilAsync(() => context.Editor.ActiveNode != null);

                var overlay = new SprintOverlayView { DataContext = sprint };
                var hud = new SprintHudView { DataContext = sprint };
                var window = new Window { Width = 1000, Height = 800, Content = new Grid { Children = { hud, overlay } } };
                window.Show();
                await context.SettleAsync();

                Assert.False(overlay.IsVisible);
                Assert.False(hud.IsVisible);

                // Start card, then Esc cancels it.
                context.Scheduler.AdvanceTo(new DateTimeOffset(2026, 11, 17, 20, 0, 0, TimeSpan.Zero));
                await Within(sprint.OpenCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.True(overlay.IsVisible);
                Assert.True(overlay.FindControl<StackPanel>("StartCard")!.IsVisible);
                Assert.True(overlay.FindControl<Button>("BeginButton")!.IsFocused);
                Press(window, Key.Escape);
                Assert.Equal(SprintStage.Idle, sprint.Stage);

                // Running: only the HUD.
                await Within(sprint.OpenCommand.Execute().ToTask());
                await Within(sprint.BeginCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.False(overlay.IsVisible);
                Assert.True(hud.IsVisible);
                Assert.Equal("15:00", hud.FindControl<TextBlock>("RemainingText")!.Text);

                context.Editor.Text += "four five\n";
                await context.SettleAsync();
                Assert.Equal("+2 words", hud.FindControl<TextBlock>("LiveNetText")!.Text);

                // Recap, then the note screen with its box focused.
                await Within(sprint.EndCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.False(hud.IsVisible);
                Assert.True(overlay.FindControl<StackPanel>("RecapCard")!.IsVisible);
                Assert.True(overlay.FindControl<Button>("FutureYouButton")!.IsFocused);

                await Within(sprint.ToFutureYouCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.True(overlay.FindControl<StackPanel>("FutureYouCard")!.IsVisible);
                Assert.True(overlay.FindControl<TextBox>("FutureYouInput")!.IsFocused);

                Press(window, Key.Escape);
                Assert.Equal(SprintStage.Idle, sprint.Stage);
                await context.SettleAsync();
                Assert.False(overlay.IsVisible);

                window.Close();
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void MainWindow_wires_the_sprint_shortcut_menu_hud_and_overlay()
    {
        var axaml = System.IO.File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("<KeyBinding Gesture=\"Ctrl+Alt+S\" Command=\"{Binding Sprint.OpenCommand}\" />", axaml);
        Assert.Contains("Header=\"Start Writing _Sprint…\"", axaml);
        Assert.Contains("<views:SprintHudView", axaml);
        Assert.Contains("<views:SprintOverlayView", axaml);
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relativePath);
            if (System.IO.File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException(relativePath);
    }
}

using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Hymnal.Views.Controls;
using Xunit;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.Core.Tests.Views;

/// <summary>The run screens and HUD load, show the right screen for each stage, take focus and close on Esc.</summary>
[Collection("AvaloniaUi")]
public sealed class RunViewsTests
{
    static RunViewsTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Each_stage_shows_its_screen_and_Esc_backs_out()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nOne two three\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                context.Workspace.SelectedNode = context.Node("a.md");
                await PumpUntilAsync(() => context.Editor.ActiveNode != null);

                var overlay = new RunOverlayView { DataContext = run };
                var hud = new RunHudView { DataContext = run };
                var window = new Window { Width = 1000, Height = 800, Content = new Grid { Children = { hud, overlay } } };
                window.Show();
                await context.SettleAsync();

                Assert.False(overlay.IsVisible);
                Assert.False(hud.IsVisible);

                // Start card, then Esc cancels it.
                context.Scheduler.AdvanceTo(new DateTimeOffset(2026, 11, 17, 20, 0, 0, TimeSpan.Zero));
                await Within(run.OpenCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.True(overlay.IsVisible);
                Assert.True(overlay.FindControl<StackPanel>("StartCard")!.IsVisible);
                Assert.Equal(600, overlay.FindControl<Border>("Card")!.Bounds.Width); // the start card stays compact
                var dial = overlay.FindControl<MinuteDial>("Dial")!;
                Assert.True(dial.IsFocused);
                Assert.Equal(15, dial.Value);

                // The dial: arrows nudge it, typed digits set it, and the view model follows.
                Press(window, Key.Up);
                Assert.Equal(16, run.SelectedMinutes);
                Press(window, Key.PageDown);
                Assert.Equal(11, run.SelectedMinutes);
                Press(window, Key.D4);
                Press(window, Key.D5);
                Assert.Equal(45, run.SelectedMinutes);
                run.SelectedMinutes = 15;
                Assert.Equal(15, dial.Value);

                Press(window, Key.Escape);
                Assert.Equal(RunStage.Idle, run.Stage);

                // Running: only the HUD.
                await Within(run.OpenCommand.Execute().ToTask());
                await Within(run.BeginCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.False(overlay.IsVisible);
                Assert.True(hud.IsVisible);
                Assert.Equal("15:00", hud.FindControl<TextBlock>("RemainingText")!.Text);

                context.Editor.Text += "four five\n";
                await context.SettleAsync();
                Assert.Equal("+2 words", hud.FindControl<TextBlock>("LiveNetText")!.Text);

                // The recap, with Done focused and the note box and streak calendar on it.
                await Within(run.EndCommand.Execute().ToTask());
                await context.SettleAsync();
                Assert.False(hud.IsVisible);
                Assert.True(overlay.FindControl<Grid>("RecapCard")!.IsVisible);
                Assert.True(overlay.FindControl<Button>("DoneButton")!.IsFocused);

                // The recap is a screen of its own: the card fills the window (up to 1,180 px), the chart takes
                // two of three columns and the spare height, and Done is on screen.
                var card = overlay.FindControl<Border>("Card")!;
                Assert.Equal(1000 - 48, card.Bounds.Width);
                var chartBounds = overlay.FindControl<RunMinuteChart>("MinuteChart")!.Bounds;
                Assert.True(chartBounds.Width > 500 && chartBounds.Height >= 150, $"chart {chartBounds}");
                var done = overlay.FindControl<Button>("DoneButton")!;
                var doneBottom = done.TranslatePoint(new Point(0, done.Bounds.Height), card)!.Value.Y;
                Assert.True(doneBottom <= card.Bounds.Height, $"Done ends at {doneBottom:0} in a {card.Bounds.Height:0} px card");
                Assert.True(overlay.FindControl<TextBox>("FutureYouInput")!.IsEffectivelyVisible);
                Assert.True(overlay.FindControl<StreakHeatmap>("StreakCalendar")!.IsEffectivelyVisible);
                var chart = overlay.FindControl<RunMinuteChart>("MinuteChart")!;
                Assert.True(chart.IsEffectivelyVisible);
                Assert.Single(chart.Items!);
                Assert.False(chart.Cumulative);
                overlay.FindControl<RadioButton>("CumulativeToggle")!.IsChecked = true;
                Assert.True(run.ShowCumulative);
                Assert.True(chart.Cumulative);

                Press(window, Key.Escape);
                Assert.Equal(RunStage.Idle, run.Stage);
                await context.SettleAsync();
                Assert.False(overlay.IsVisible);

                window.Close();
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void MainWindow_wires_the_run_button_shortcuts_menus_hud_overlay_and_atmosphere()
    {
        var axaml = System.IO.File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("<KeyBinding Gesture=\"Ctrl+Alt+R\" Command=\"{Binding WritingRun.OpenCommand}\" />", axaml);
        Assert.Contains("<KeyBinding Gesture=\"Ctrl+Alt+L\" Command=\"{Binding WritingRun.ShowLogbookCommand}\" />", axaml);
        Assert.Contains("x:Name=\"PART_RunButton\"", axaml);
        Assert.Contains("<views:RunHudView", axaml);
        Assert.Contains("<views:RunOverlayView", axaml);
        Assert.Contains("<controls:RunAtmosphereLayer", axaml);

        // The menu bar: File, View, Go, Insert, Run, Tools, Help, and View holds only view toggles.
        string[] order = ["Header=\"_File\"", "Header=\"_View\"", "Header=\"_Go\"", "x:Name=\"PART_InsertMenu\"",
            "Header=\"_Run\"", "Header=\"_Tools\"", "Header=\"_Help\""];
        var positions = order.Select(h => axaml.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);

        var view = axaml[positions[1]..positions[2]];
        foreach (var misplaced in new[] { "ShowGoToCommand", "ShowCommandsCommand", "PART_SpellingMenu", "PART_SoundMenuItem", "WritingRun" })
            Assert.DoesNotContain(misplaced, view);
        var run = axaml[positions[4]..positions[5]];
        Assert.Contains("Header=\"_New Run…\"", run);
        Assert.Contains("Header=\"_Logbook…\"", run);
        var tools = axaml[positions[5]..positions[6]];
        Assert.Contains("PART_SpellingMenu", tools);
        Assert.Contains("PART_SoundMenuItem", tools);
    }

    [Fact]
    public async Task The_atmosphere_layer_burns_while_active_and_settles_when_the_run_ends()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(() =>
        {
            var layer = new RunAtmosphereLayer();
            var window = new Window { Width = 900, Height = 600, Content = layer };
            window.Show();

            for (var i = 0; i < 60; i++)
                layer.Step(1 / 60.0);
            Assert.Equal(0, layer.ParticleCount); // inactive: nothing

            layer.IsActive = true;
            for (var i = 0; i < 60; i++)
                layer.Step(1 / 60.0);
            var idle = layer.ParticleCount;
            Assert.True(idle > 0);

            layer.Intensity = 1;
            for (var i = 0; i < 120; i++)
                layer.Step(1 / 60.0);
            Assert.True(layer.ParticleCount > idle * 2, $"{layer.ParticleCount} embers at full intensity vs {idle} idling");

            var before = layer.ParticleCount;
            layer.Sparks += 5;
            Assert.True(layer.ParticleCount > before);

            layer.IsActive = false;
            layer.Celebrations++;
            Assert.True(layer.ParticleCount > before);
            for (var i = 0; i < 60 * 5; i++)
                layer.Step(1 / 60.0);
            Assert.Equal(0, layer.ParticleCount); // everything burns out

            window.Close();
            return Task.FromResult(0);
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task The_Logbook_window_shows_streak_heatmap_and_runs()
    {
        var root = CreateWorkspace(NovelJson, ("a.md", "# A\n\nOne two three\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var run = context.CreateRun();
                context.Workspace.SelectedNode = context.Node("a.md");
                await PumpUntilAsync(() => context.Editor.ActiveNode != null);
                context.Scheduler.AdvanceTo(new DateTimeOffset(2026, 11, 17, 20, 0, 0, TimeSpan.Zero));
                await Within(run.OpenCommand.Execute().ToTask());
                await Within(run.BeginCommand.Execute().ToTask());
                context.Editor.Text += "four five\n";
                await Within(run.EndCommand.Execute().ToTask());

                var logbook = await Within(run.ShowLogbookCommand.Execute().ToTask());
                var dialog = new RunLogbookDialog { DataContext = logbook };
                dialog.Show();
                await context.SettleAsync();

                Assert.Equal("1", dialog.FindControl<TextBlock>("StreakText")!.Text);
                var heatmap = dialog.FindControl<StreakHeatmap>("Heatmap")!;
                Assert.Equal(84, heatmap.Days!.Count);
                Assert.Equal(12 * 17 + 26, heatmap.DesiredSize.Width); // twelve weeks of 14 px squares and gaps, plus day names
                Assert.Equal(1, dialog.FindControl<ItemsControl>("RunList")!.ItemCount);
                Assert.False(dialog.FindControl<TextBlock>("EmptyText")!.IsVisible);
                dialog.Close();
            });
        }
        finally { DeleteFolder(root); }
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

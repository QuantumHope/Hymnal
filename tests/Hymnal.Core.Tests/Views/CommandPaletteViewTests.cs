using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>The palette overlay's keyboard routing and focus, in a headless window.</summary>
[Collection("AvaloniaUi")]
public sealed class CommandPaletteViewTests
{
    static CommandPaletteViewTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Arrows_move_Enter_runs_and_Esc_closes_without_reaching_the_window()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var ran = new List<string>();
            PaletteEntry Place(string title) => new($"node:{title}", PaletteEntryKind.Destination, title, "Scene",
                null, null, () => { ran.Add(title); return Task.CompletedTask; });

            using var vm = new CommandPaletteViewModel(() => [Place("One"), Place("Two"), Place("Three")]);
            var windowEscapes = 0;
            var before = new Button { Content = "editor" };
            var view = new CommandPaletteView { DataContext = vm };
            var window = new Window
            {
                Width = 900,
                Height = 700,
                Content = new Grid { Children = { before, view } },
            };
            window.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.Escape),
                Command = new CountingCommand(() => windowEscapes++, canExecute: false), // as ExitFocusModeCommand outside focus mode
            });
            window.Show();
            before.Focus();
            await Pump();

            Assert.False(view.IsVisible);
            Assert.True(before.IsFocused);

            vm.OpenGoTo();
            await Pump();
            Assert.True(view.IsVisible);
            var query = view.FindControl<TextBox>("QueryInput")!;
            Assert.True(query.IsFocused);

            Press(window, Key.Down);
            Press(window, Key.Down);
            Assert.Equal("Three", vm.Selected?.Title);
            Press(window, Key.Up);
            Assert.Equal("Two", vm.Selected?.Title);

            Press(window, Key.Escape);
            await Pump();
            Assert.False(vm.IsOpen);
            Assert.Equal(0, windowEscapes);
            Assert.True(before.IsFocused, "focus goes back where it was");

            vm.OpenGoTo();
            await Pump();
            Press(window, Key.Down);
            Press(window, Key.Enter);
            await Pump();
            Assert.False(vm.IsOpen);
            Assert.Equal(["Two"], ran);

            window.Close();
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    private sealed class CountingCommand(Action run, bool canExecute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => canExecute;

        public void Execute(object? parameter) => run();
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
    }

    /// <summary>Runs posted dispatcher work (focus after layout), as <c>PlanTestContext.SettleAsync</c> does.</summary>
    private static async Task Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}

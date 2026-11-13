using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using AvaloniaEdit;
using Hymnal.Audio;
using Hymnal.Core.Audio;
using Hymnal.Core.Tests.Audio;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Views.Editor;
using Xunit;

namespace Hymnal.Core.Tests.Views;

[Collection("AvaloniaUi")]
public sealed class TypingSoundHookTests
{
    static TypingSoundHookTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Theory]
    [InlineData(Key.Back, KeyModifiers.None, TypingSoundKind.Backspace)]
    [InlineData(Key.Back, KeyModifiers.Control, TypingSoundKind.Backspace)]
    [InlineData(Key.Delete, KeyModifiers.None, TypingSoundKind.Backspace)]
    [InlineData(Key.Enter, KeyModifiers.None, TypingSoundKind.Return)]
    [InlineData(Key.Enter, KeyModifiers.Shift, TypingSoundKind.Return)]
    [InlineData(Key.Tab, KeyModifiers.None, TypingSoundKind.Space)]
    public void ForKey_EditingKeysMakeASound(Key key, KeyModifiers modifiers, TypingSoundKind expected) =>
        Assert.Equal(expected, TypingSoundHook.ForKey(key, modifiers));

    [Theory]
    [InlineData(Key.Left, KeyModifiers.None)]
    [InlineData(Key.Right, KeyModifiers.Shift)]
    [InlineData(Key.Up, KeyModifiers.None)]
    [InlineData(Key.Down, KeyModifiers.None)]
    [InlineData(Key.Home, KeyModifiers.None)]
    [InlineData(Key.End, KeyModifiers.Control)]
    [InlineData(Key.PageDown, KeyModifiers.None)]
    [InlineData(Key.Z, KeyModifiers.Control)]
    [InlineData(Key.V, KeyModifiers.Control)]
    [InlineData(Key.Enter, KeyModifiers.Control)]
    [InlineData(Key.Back, KeyModifiers.Alt)]
    [InlineData(Key.A, KeyModifiers.None)]
    public void ForKey_NavigationAndShortcutsAreSilent(Key key, KeyModifiers modifiers) =>
        Assert.Null(TypingSoundHook.ForKey(key, modifiers));

    [Fact]
    public async Task TypingAndDeleting_PlaySounds_ButNavigationUndoAndLoadsDoNot()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            // The test app has the Fluent theme only; the editor's template comes from AvaloniaEdit's theme, which
            // App.axaml includes in the application styles. Each test dispatch gets its own application.
            Avalonia.Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://Hymnal.Core.Tests/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
            });
            var editor = new TextEditor { Text = "", Width = 600, Height = 300 };
            var window = new Window { Content = editor, Width = 640, Height = 360 };
            var played = new List<(TypingSoundKind Kind, float Pan)>();
            using var hook = new TypingSoundHook(editor, (kind, pan) => played.Add((kind, pan)));
            window.Show();
            try
            {
                window.Activate();
                window.UpdateLayout(); // applies the editor's template, so its text area is in the tree
                await Pump();
                Assert.True(editor.TextArea.Focus(),
                    $"the text area should take focus (in tree: {TopLevel.GetTopLevel(editor.TextArea) != null}, bounds {editor.TextArea.Bounds}, template {editor.Template != null})");
                await Pump();
                Assert.True(editor.TextArea.IsKeyboardFocusWithin, "keyboard focus should be in the text area");

                window.KeyTextInput("H");
                await Pump();
                window.KeyTextInput("i");
                await Pump();
                window.KeyTextInput(" ");
                await Pump();
                Press(window, Key.Enter);
                await Pump();
                Press(window, Key.Back);
                await Pump();

                Assert.Equal("Hi ", editor.Text);
                Assert.Equal(
                    [TypingSoundKind.Key, TypingSoundKind.Key, TypingSoundKind.Space, TypingSoundKind.Return, TypingSoundKind.Backspace],
                    played.ConvertAll(p => p.Kind));
                Assert.All(played, p => Assert.InRange(p.Pan, -TypingSoundVoicer.MaxCarriagePan, TypingSoundVoicer.MaxCarriagePan));
                played.Clear();

                // Moving the caret and selecting make no sound.
                Press(window, Key.Left);
                await Pump();
                Press(window, Key.Home);
                await Pump();
                Press(window, Key.Right, RawInputModifiers.Shift);
                await Pump();
                Assert.Empty(played);

                // Backspace with nothing to delete makes no sound.
                editor.TextArea.ClearSelection();
                editor.CaretOffset = 0;
                Press(window, Key.Back);
                await Pump();
                Assert.Empty(played);

                // Undo changes the text but isn't typing.
                Press(window, Key.Z, RawInputModifiers.Control);
                await Pump();
                Assert.NotEqual("Hi ", editor.Text);
                Assert.Empty(played);

                // Nor is loading a chapter.
                editor.Text = "A whole new chapter";
                await Pump();
                Assert.Empty(played);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TypewriterSoundsMenuItem_TogglesThePreference()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var prefs = new SoundPreferences(new SoundSettingsService(new TypingSoundPlayerTests.InMemorySoundStore()));
            await prefs.Loaded;
            // Built as MainWindow.axaml builds it: a toggle command and a one-way IsChecked.
            var item = new MenuItem
            {
                Header = "Typewriter Sounds",
                ToggleType = MenuItemToggleType.CheckBox,
                DataContext = prefs,
                Command = new ToggleCommand(prefs.ToggleTypingSounds),
            };
            item.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(SoundPreferences.TypingSoundsEnabled)) { Mode = BindingMode.OneWay });
            var window = new Window { Content = new Menu { Items = { item } } };
            window.Show();
            try
            {
                // What the menu's interaction handler calls when the item is clicked or chosen with the keyboard.
                // Avalonia doesn't toggle IsChecked itself, so the command does it and the binding shows it.
                Click(item);
                await Pump();
                Assert.True(prefs.TypingSoundsEnabled);
                Assert.True(item.IsChecked);
                Click(item);
                await Pump();
                Assert.False(prefs.TypingSoundsEnabled);
                Assert.False(item.IsChecked);

                prefs.TypingSoundsEnabled = true;
                Assert.True(item.IsChecked);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OnlyTheWriteSurfacePlaysTypingSounds()
    {
        var mainWindow = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        var research = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/ResearchView.axaml"));

        Assert.Contains("PlaysTypingSounds=\"True\"", mainWindow);
        Assert.DoesNotContain("PlaysTypingSounds", research);
        Assert.Contains("Command=\"{Binding ToggleTypingSoundsCommand}\"", mainWindow);
        Assert.Contains("IsChecked=\"{Binding SoundPreferences.TypingSoundsEnabled, Mode=OneWay}\"", mainWindow);
    }

    /// <summary>
    /// Clicks a menu item the way the menu's interaction handler does (Avalonia's internal IMenuItem.RaiseClick),
    /// including any toggling Avalonia does itself, so a double toggle would show.
    /// </summary>
    private static void Click(MenuItem item)
    {
        var raiseClick = typeof(MenuItem).GetInterfaces()
            .Where(i => i.Name == "IMenuItem")
            .Select(i => i.GetMethod("RaiseClick"))
            .Single(m => m is not null)!;
        var map = typeof(MenuItem).GetInterfaceMap(raiseClick.DeclaringType!);
        var index = Array.IndexOf(map.InterfaceMethods, raiseClick);
        map.TargetMethods[index].Invoke(item, null);
    }

    /// <summary>
    /// A plain command: a ReactiveCommand re-enables itself on RxApp's main-thread scheduler, which a headless
    /// test doesn't pump between clicks.
    /// </summary>
    private sealed class ToggleCommand(Action toggle) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => toggle();
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    /// <summary>Lets posted dispatcher work (the hook's disarm, layout) run.</summary>
    private static Task Pump() => Task.Delay(10);

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}

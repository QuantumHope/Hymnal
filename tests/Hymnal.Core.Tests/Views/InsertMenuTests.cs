using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using System.Reactive.Linq;
using AvaloniaEdit;
using ReactiveUI;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Markua.Editing;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>The Insert menu's shape, and its shortcuts in a real editor in a headless window.</summary>
[Collection("AvaloniaUi")]
public sealed class InsertMenuTests
{
    static InsertMenuTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Menu_follows_the_catalog_with_submenus_separators_and_shortcuts()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var root = new MenuItem { Header = "_Insert" };
            InsertMenu.Populate(root);

            var top = root.Items.ToList();
            var sections = MarkuaSnippets.All.Select(s => s.Section).Distinct().Count();
            Assert.Equal(sections - 1, top.OfType<Separator>().Count());

            var items = top.OfType<MenuItem>().ToList();
            var bold = items.Single(i => (string?)i.Header == "Bold");
            Assert.Equal(KeyGesture.Parse("Ctrl+B"), bold.InputGesture);
            Assert.Same(MarkuaSnippets.Get("bold"), bold.CommandParameter);

            var headings = items.Single(i => (string?)i.Header == "Heading").Items.OfType<MenuItem>().ToList();
            Assert.Equal(5, headings.Count);
            var callouts = items.Single(i => (string?)i.Header == "Callout").Items.OfType<MenuItem>().ToList();
            Assert.Contains(callouts, i => (string?)i.Header == "Warning (W>)");
            Assert.Single(items, i => (string?)i.Header == "Book Structure");

            // Every snippet is in the menu once.
            var all = items.Concat(items.SelectMany(i => i.Items.OfType<MenuItem>()))
                .Select(i => i.CommandParameter).OfType<MarkuaSnippet>().ToList();
            Assert.Equal(MarkuaSnippets.All.Count, all.Distinct().Count());
            await Task.CompletedTask;
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Main_window_has_an_Insert_menu_between_View_and_Help()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        var view = axaml.IndexOf("Header=\"_View\"", StringComparison.Ordinal);
        var insert = axaml.IndexOf("x:Name=\"PART_InsertMenu\"", StringComparison.Ordinal);
        var help = axaml.IndexOf("Header=\"_Help\"", StringComparison.Ordinal);
        Assert.True(view < insert && insert < help);

        var code = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml.cs"));
        Assert.Contains("InsertMenu.Populate(PART_InsertMenu)", code);

        // Window key bindings run before the editor sees a key, so Ctrl+B must go through the gated command.
        Assert.Contains("<KeyBinding Gesture=\"Ctrl+B\" Command=\"{Binding ToggleSidebarShortcutCommand}\" />", axaml);
    }

    [Fact]
    public async Task Shortcuts_style_the_selection_ahead_of_the_window_and_AvaloniaEdit_and_undo_in_one_step()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hymnal-insert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "chapter.md");
        await File.WriteAllTextAsync(file, "a word here");
        try
        {
            await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
            {
                var store = new MetadataStore();
                using var vm = new EditorViewModel(store, new NotificationService(), new WordCountService(),
                    new StoryFileWriter(store)) { HasWorkspace = true };
                await vm.OpenArbitraryFileAsync(file);

                // The editor's template comes from AvaloniaEdit's theme, which App.axaml includes in the app styles.
                Avalonia.Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://Hymnal.Core.Tests/"))
                {
                    Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
                });
                var windowCtrlB = 0;
                var view = new EditorView { DataContext = vm };
                var window = new Window { Width = 900, Height = 600, Content = view };
                window.KeyBindings.Add(new KeyBinding
                {
                    // As MainWindow's ToggleSidebarShortcutCommand: it stands down while the editor takes the key.
                    Gesture = new KeyGesture(Key.B, KeyModifiers.Control),
                    Command = ReactiveUI.ReactiveCommand.Create(() => { windowCtrlB++; },
                        vm.WhenAnyValue(x => x.TakesInsertShortcuts).Select(takes => !takes)),
                });
                window.Show();
                await Pump();

                var editor = view.FindControl<TextEditor>("PART_Editor")!;
                Assert.Equal("a word here", editor.Text);
                Assert.True(vm.CanInsertMarkua);
                window.Activate();
                window.UpdateLayout();
                await Pump();
                editor.TextArea.Focus();
                await Pump();
                Assert.True(editor.TextArea.IsFocused, $"focused: {window.FocusManager?.GetFocusedElement()}");
                Assert.True(vm.TakesInsertShortcuts);
                editor.Select(2, 4);

                Press(window, Key.B, RawInputModifiers.Control);
                await Pump();
                Assert.Equal("a **word** here", editor.Text);
                Assert.Equal("a **word** here", vm.Text);
                Assert.Equal("word", editor.SelectedText);
                Assert.Equal(0, windowCtrlB);

                Press(window, Key.I, RawInputModifiers.Control); // AvaloniaEdit would indent
                await Pump();
                Assert.Equal("a ***word*** here", editor.Text);

                editor.Undo();
                await Pump();
                Assert.Equal("a **word** here", editor.Text);

                // Preview only: the shortcut falls through to the window again.
                vm.ViewMode = EditorViewMode.Preview;
                await Pump();
                Assert.False(vm.CanInsertMarkua);
                window.Focus();
                Press(window, Key.B, RawInputModifiers.Control);
                await Pump();
                Assert.Equal(1, windowCtrlB);
                Assert.Equal("a **word** here", vm.Text);

                window.Close();
                return 0;
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    private static async Task Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

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

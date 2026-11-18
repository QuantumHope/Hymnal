using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using AvaloniaEdit;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>The Edit menu's shape, and its commands on a TextBox and on the manuscript editor in a headless window.</summary>
[Collection("AvaloniaUi")]
public sealed class EditMenuTests
{
    static EditMenuTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Menu_lists_undo_clipboard_and_selection_items_with_shortcuts()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var root = new MenuItem { Header = "_Edit" };
            var window = new Window { Content = new Menu { Items = { root } } };
            using var menu = EditMenu.Attach(root, window);

            var items = root.Items.ToList();
            Assert.Equal(
                ["_Undo", "_Redo", "-", "Cu_t", "_Copy", "_Paste", "_Delete", "-", "Select _All"],
                items.Select(i => i is Separator ? "-" : (string)((MenuItem)i!).Header!));
            Assert.All(items.OfType<MenuItem>(), i => Assert.NotNull(i.InputGesture));
            Assert.All(items.OfType<MenuItem>(), i => Assert.NotNull(i.Command));
            Assert.Equal(new KeyGesture(Key.Delete), items.OfType<MenuItem>().Single(i => (string)i.Header! == "_Delete").InputGesture);

            // Nothing has focus yet, so there is nothing to edit.
            Assert.All(items.OfType<MenuItem>(), i => Assert.False(i.Command!.CanExecute(null)));
            await Task.CompletedTask;
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Main_window_has_an_Edit_menu_between_File_and_View()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        var file = axaml.IndexOf("Header=\"_File\"", StringComparison.Ordinal);
        var edit = axaml.IndexOf("x:Name=\"PART_EditMenu\"", StringComparison.Ordinal);
        var view = axaml.IndexOf("Header=\"_View\"", StringComparison.Ordinal);
        Assert.True(file >= 0 && file < edit && edit < view, $"File {file}, Edit {edit}, View {view}");

        var code = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml.cs"));
        Assert.Contains("EditMenu.Attach(PART_EditMenu, this)", code);
        Assert.Contains("_editMenu.Dispose()", code);

        // The text controls handle these keys themselves; a window binding would take them from every text box.
        foreach (var key in new[] { "Ctrl+Z", "Ctrl+Y", "Ctrl+X", "Ctrl+C", "Ctrl+V", "Ctrl+A" })
            Assert.DoesNotContain($"<KeyBinding Gesture=\"{key}\"", axaml);
    }

    [Fact]
    public async Task Commands_act_on_the_text_box_that_had_focus_even_after_the_menu_takes_it()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var (window, root, menu) = CreateWindow(out var notes, out var title, out var button);
            using var attached = menu;
            try
            {
                Focus(notes);
                await Pump();
                window.KeyTextInput("the quick fox");
                await Pump();
                Assert.Equal("the quick fox", notes.Text);
                Assert.Same(notes, menu.Target);

                // Select "quick ", then open the menu the way a click does: focus moves to it, and the text box
                // drops its selection on losing focus.
                notes.SelectionStart = 4;
                notes.SelectionEnd = 10;
                await OpenMenu(root);
                Assert.True(Item(root, "Cu_t").Command!.CanExecute(null));
                Assert.True(Item(root, "_Copy").Command!.CanExecute(null));
                Assert.True(Item(root, "_Undo").Command!.CanExecute(null));
                Assert.False(Item(root, "_Redo").Command!.CanExecute(null));

                Click(Item(root, "Cu_t"));
                await Pump();
                Assert.Equal("the fox", notes.Text);
                Assert.True(notes.IsFocused, "focus goes back to the text box");

                // Paste what was cut at the end.
                notes.CaretIndex = notes.Text!.Length;
                await OpenMenu(root);
                Click(Item(root, "_Paste"));
                await PumpUntil(() => notes.Text == "the foxquick ");
                Assert.Equal("the foxquick ", notes.Text);

                await OpenMenu(root);
                Click(Item(root, "_Undo"));
                await Pump();
                Assert.Equal("the fox", notes.Text);

                await OpenMenu(root);
                Assert.True(Item(root, "_Redo").Command!.CanExecute(null));
                Click(Item(root, "_Redo"));
                await Pump();
                Assert.Equal("the foxquick ", notes.Text);

                await OpenMenu(root);
                Click(Item(root, "Select _All"));
                await Pump();
                Assert.Equal(0, notes.SelectionStart);
                Assert.Equal(notes.Text!.Length, notes.SelectionEnd);

                await OpenMenu(root);
                Click(Item(root, "_Delete"));
                await Pump();
                Assert.Equal(string.Empty, notes.Text);

                // Each text box has its own history: Undo in the title leaves the notes alone.
                Focus(title);
                await Pump();
                window.KeyTextInput("Chapter One");
                await Pump();
                Assert.Same(title, menu.Target);
                await OpenMenu(root);
                Click(Item(root, "_Undo"));
                await Pump();
                Assert.NotEqual("Chapter One", title.Text);
                Assert.Equal(string.Empty, notes.Text);

                // Focus on a button: nothing to edit, so every item greys out.
                Focus(button);
                await Pump();
                Assert.Null(menu.Target);
                await OpenMenu(root);
                Assert.All(root.Items.OfType<MenuItem>(), i => Assert.False(i.Command!.CanExecute(null), (string)i.Header!));
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Read_only_text_can_be_copied_but_not_changed()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var (window, root, menu) = CreateWindow(out var notes, out _, out _);
            using var attached = menu;
            try
            {
                // Edited first, then made read-only: the edit history must not let Undo change the text.
                Focus(notes);
                await Pump();
                window.KeyTextInput("fixed words");
                await Pump();
                notes.IsReadOnly = true;
                Assert.True(notes.CanUndo, "the box still has an edit to undo");
                notes.SelectionStart = 0;
                notes.SelectionEnd = 5;
                await OpenMenu(root);

                Assert.True(Item(root, "_Copy").Command!.CanExecute(null));
                Assert.True(Item(root, "Select _All").Command!.CanExecute(null));
                Assert.False(Item(root, "Cu_t").Command!.CanExecute(null));
                Assert.False(Item(root, "_Paste").Command!.CanExecute(null));
                Assert.False(Item(root, "_Delete").Command!.CanExecute(null));
                Assert.False(Item(root, "_Undo").Command!.CanExecute(null));
                Assert.False(Item(root, "_Redo").Command!.CanExecute(null));
                Item(root, "_Undo").Command!.Execute(null);
                Assert.Equal("fixed words", notes.Text);
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Commands_act_on_the_manuscript_editor_and_undo_stops_at_a_newly_loaded_chapter()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            // The editor's template comes from AvaloniaEdit's theme, which App.axaml includes in the app styles.
            Avalonia.Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://Hymnal.Core.Tests/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
            });
            var root = new MenuItem { Header = "_Edit" };
            var editor = new TextEditor { Text = "", Width = 600, Height = 300 };
            var panel = new DockPanel();
            var bar = new Menu { Items = { root } };
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);
            panel.Children.Add(editor);
            var window = new Window { Content = panel, Width = 640, Height = 400 };
            using var menu = EditMenu.Attach(root, window);
            window.Show();
            try
            {
                window.Activate();
                window.UpdateLayout(); // applies the editor's template, so its text area is in the tree
                await Pump();
                Assert.True(editor.TextArea.Focus());
                await Pump();
                window.KeyTextInput("a bright morning");
                await Pump();
                Assert.Same(editor.TextArea, menu.Target);

                editor.Select(2, 7); // "bright "
                await OpenMenu(root);
                Assert.True(Item(root, "Cu_t").Command!.CanExecute(null));
                Click(Item(root, "Cu_t"));
                await Pump();
                Assert.Equal("a morning", editor.Text);
                Assert.True(editor.TextArea.IsKeyboardFocusWithin, "focus goes back to the editor");

                await OpenMenu(root);
                Click(Item(root, "_Undo"));
                await Pump();
                Assert.Equal("a bright morning", editor.Text);

                await OpenMenu(root);
                Click(Item(root, "_Redo"));
                await Pump();
                Assert.Equal("a morning", editor.Text);

                await OpenMenu(root);
                Click(Item(root, "Select _All"));
                await Pump();
                Assert.Equal(editor.Text.Length, editor.SelectionLength);

                await OpenMenu(root);
                Click(Item(root, "_Delete"));
                await Pump();
                Assert.Equal(string.Empty, editor.Text);

                // Opening another chapter replaces the text (EditorView sets TextEditor.Text): Undo must not reach
                // back into the previous chapter.
                editor.Text = "Chapter two";
                await OpenMenu(root);
                Assert.False(Item(root, "_Undo").Command!.CanExecute(null));
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    private static (Window Window, MenuItem Root, EditMenu Menu) CreateWindow(
        out TextBox notes, out TextBox title, out Button button)
    {
        var root = new MenuItem { Header = "_Edit" };
        notes = new TextBox { Width = 400 };
        title = new TextBox { Width = 400 };
        button = new Button { Content = "Save" };
        var window = new Window
        {
            Width = 640,
            Height = 400,
            Content = new StackPanel { Children = { new Menu { Items = { root } }, notes, title, button } },
        };
        var menu = EditMenu.Attach(root, window);
        window.Show();
        window.Activate();
        window.UpdateLayout();
        return (window, root, menu);
    }

    private static void Focus(Control control) => Assert.True(control.Focus(), $"{control.GetType().Name} should take focus");

    /// <summary>Moves focus to the menu and opens it, as clicking or pressing Alt+E does.</summary>
    private static async Task OpenMenu(MenuItem root)
    {
        root.IsSubMenuOpen = false;
        await Pump();
        root.Focus();
        await Pump();
        root.IsSubMenuOpen = true;
        await Pump();
    }

    private static MenuItem Item(MenuItem root, string header) =>
        root.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);

    /// <summary>
    /// Clicks a menu item the way the menu's interaction handler does (Avalonia's internal IMenuItem.RaiseClick).
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

    /// <summary>Lets posted dispatcher work (focus, layout, the clipboard) run.</summary>
    private static Task Pump() => Task.Delay(10);

    private static async Task PumpUntil(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
            await Pump();
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

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Hymnal.Commands;
using Hymnal.Core.Markua.Editing;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Views;
using Xunit;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.Core.Tests.Views;

/// <summary>
/// The shell's command catalog (<c>MainWindowViewModel.ShellCommands</c>) and the menu bar
/// <see cref="ShellMenu"/> builds from it.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class ShellMenuTests
{
    static ShellMenuTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Catalog_ids_are_unique_and_no_two_bound_commands_share_a_gesture()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost());
                var commands = window.ShellCommands;

                Assert.Equal(commands.Count, commands.Select(c => c.Id).Distinct().Count());
                Assert.All(commands, c => Assert.Contains(c.Menu, ShellMenus.Order));

                var bound = commands.Where(c => c.Gesture != null && c.BindsKey)
                    .Select(c => KeyGesture.Parse(c.Gesture!)).ToList();
                Assert.Equal(bound.Count, bound.Distinct().Count());

                // Every snippet is offered once, with the editor handling its keys.
                var inserts = commands.Where(c => c.Menu == ShellMenus.Insert).ToList();
                Assert.Equal(MarkuaSnippets.All.Count, inserts.Count);
                Assert.All(inserts, c => Assert.False(c.BindsKey));

                // Ctrl+B goes through the command that stands down while the editor takes Markua shortcuts.
                var left = commands.Single(c => c.Id == "toggle-left-rail");
                Assert.Same(window.ToggleSidebarShortcutCommand, left.KeyCommand!());
                Assert.Same(window.ToggleSidebarCommand, left.Command());
                await Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Menu_bar_follows_the_catalog_in_order_with_submenus_separators_and_shortcuts()
    {
        var root = CreateWorkspace(StoryJson, ("story.md", "# Story\n\nText.\n"));
        try
        {
            await Ui(root, 1, async context =>
            {
                var window = context.CreateMainWindow(context.CreateHost(), context.CreateWriteBeats());
                await PumpUntilAsync(() => window.IsBeatsSectionAvailable);

                var menu = new Menu();
                ShellMenu.Populate(menu, window.ShellCommands);
                var tops = menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(ShellMenus.Order, tops.Select(t => (string?)t.Header));
                MenuItem Top(string name) => tops.Single(t => (string?)t.Header == name);
                static string[] Headers(MenuItem m) =>
                    m.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => (string)i.Header!).ToArray();

                // View holds only view toggles.
                var view = Headers(Top(ShellMenus.View));
                Assert.Contains("Toggle _Beats", view); // a story has beats
                Assert.DoesNotContain(view, h => h.Contains("Run") || h.Contains("Go To") || h.Contains("pelling"));

                // Run: New Run, End Run (shown only while a run is going), Logbook. This window has no run view
                // model, so they are hidden here.
                Assert.Equal(["_New Run…", "_End Run", "_Logbook…"],
                    Top(ShellMenus.Run).Items.OfType<MenuItem>().Select(i => (string?)i.Header));
                Assert.Null(window.WritingRun);
                Assert.Empty(Headers(Top(ShellMenus.Run)));
                var tools = Top(ShellMenus.Tools);
                // Spelling and Sound are there, hidden here because this window has no spelling service or sound.
                var toolItems = tools.Items.OfType<MenuItem>().ToList();
                Assert.Contains(toolItems, i => (string?)i.Header == "S_pelling" && !i.IsVisible);
                Assert.Contains(toolItems, i => (string?)i.Header == "S_ound" && !i.IsVisible);
                Assert.Equal(["_Settings…"], Headers(tools));

                // Insert: one separator per section boundary, submenus, and the editor's shortcuts shown.
                var insert = Top(ShellMenus.Insert);
                var sections = MarkuaSnippets.All.Select(s => s.Section).Distinct().Count();
                Assert.Equal(sections - 1, insert.Items.OfType<Separator>().Count());
                var items = insert.Items.OfType<MenuItem>().ToList();
                var bold = items.Single(i => (string?)i.Header == "Bold");
                Assert.Equal(KeyGesture.Parse("Ctrl+B"), bold.InputGesture);
                Assert.Same(MarkuaSnippets.Get("bold"), bold.CommandParameter);
                Assert.Equal(5, items.Single(i => (string?)i.Header == "Heading").Items.OfType<MenuItem>().Count());
                Assert.Contains(items.Single(i => (string?)i.Header == "Callout").Items.OfType<MenuItem>(),
                    i => (string?)i.Header == "Warning (W>)");

                // The Settings gesture shows as the user types it.
                var settings = window.ShellCommands.Single(c => c.Id == "settings");
                Assert.Equal("Ctrl+,", settings.Shortcut);

                // No separator at the edge of a menu or next to another separator.
                foreach (var top in tops)
                {
                    var visible = top.Items.OfType<Control>().Where(c => c.IsVisible).ToList();
                    if (visible.Count == 0)
                        continue;
                    Assert.IsNotType<Separator>(visible[0]);
                    Assert.IsNotType<Separator>(visible[^1]);
                }
                await Task.CompletedTask;
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void Main_window_builds_its_menu_and_key_bindings_from_the_catalog()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("x:Name=\"PART_MainMenu\"", axaml);
        // Only the Escape stack is written by hand; the rest come from the catalog.
        Assert.DoesNotContain("<KeyBinding Gesture=\"F", axaml);
        Assert.DoesNotContain("<KeyBinding Gesture=\"Ctrl", axaml);

        var code = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/MainWindow.axaml.cs"));
        Assert.Contains("ShellMenu.Populate(PART_MainMenu, vm.ShellCommands)", code);
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

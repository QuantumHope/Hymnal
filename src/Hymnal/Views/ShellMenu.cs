using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hymnal.Commands;

namespace Hymnal.Views;

/// <summary>
/// Builds the main window's menu bar from the command catalog (<c>MainWindowViewModel.ShellCommands</c>): one
/// top-level menu per <see cref="ShellMenus.Order"/> entry, submenus by <see cref="ShellCommand.Submenu"/>, and a
/// separator wherever <see cref="ShellCommand.Group"/> changes. Titles, commands, visibility and check marks are
/// functions, so each top-level menu re-reads them when it opens (and once when built).
/// </summary>
public static class ShellMenu
{
    /// <summary>
    /// Replaces <paramref name="menu"/>'s items with the catalog's menus. Each generated item keeps its
    /// <see cref="ShellCommand"/> in <see cref="Control.Tag"/>.
    /// </summary>
    public static void Populate(Menu menu, IReadOnlyList<ShellCommand> commands)
    {
        menu.Items.Clear();
        foreach (var name in ShellMenus.Order)
        {
            var top = new MenuItem { Header = name };
            top.Bind(MenuItem.ForegroundProperty, top.GetResourceObservable("Cora.Text"));
            Fill(top, commands.Where(c => c.InMenu && c.Menu == name));
            top.AddHandler(MenuItem.SubmenuOpenedEvent, (_, e) =>
            {
                if (ReferenceEquals(e.Source, top))
                    Refresh(top);
            }, RoutingStrategies.Bubble);
            Refresh(top);
            menu.Items.Add(top);
        }
    }

    private static void Fill(MenuItem root, IEnumerable<ShellCommand> commands)
    {
        var submenus = new Dictionary<string, MenuItem>();
        var lastGroup = new Dictionary<MenuItem, int>();

        foreach (var command in commands)
        {
            var parent = root;
            if (command.Submenu is { } name)
            {
                if (!submenus.TryGetValue(name, out var submenu))
                {
                    submenu = new MenuItem { Header = name };
                    submenus[name] = submenu;
                    AddWithSeparator(root, submenu, command.Group, lastGroup);
                }
                parent = submenu;
            }
            AddWithSeparator(parent, Create(command), command.Group, lastGroup);
        }
    }

    private static void AddWithSeparator(MenuItem parent, MenuItem item, int group, Dictionary<MenuItem, int> lastGroup)
    {
        if (lastGroup.TryGetValue(parent, out var previous) && previous != group)
            parent.Items.Add(new Separator());
        lastGroup[parent] = group;
        parent.Items.Add(item);
    }

    private static MenuItem Create(ShellCommand command)
    {
        var item = new MenuItem { CommandParameter = command.Parameter, Tag = command };
        if (command.Gesture != null)
            item.InputGesture = KeyGesture.Parse(command.Gesture);
        if (command.IsChecked != null)
            item.ToggleType = MenuItemToggleType.CheckBox;
        if (command.Detail != null)
            ToolTip.SetTip(item, command.Detail);
        return item;
    }

    /// <summary>Re-reads every generated item's title, command, visibility and check mark under <paramref name="menu"/>.</summary>
    public static void Refresh(MenuItem menu)
    {
        foreach (var child in menu.Items.OfType<MenuItem>())
        {
            if (child.Tag is ShellCommand command)
            {
                child.Header = command.MenuTitle();
                var target = command.Command();
                child.Command = target;
                child.IsVisible = target != null && command.IsAvailable();
                if (command.IsChecked != null)
                    child.IsChecked = command.IsChecked();
            }
            else
            {
                Refresh(child);
                child.IsVisible = child.Items.OfType<MenuItem>().Any(i => i.IsVisible);
            }
        }
        HideStraySeparators(menu);
    }

    // A separator shows only between two visible items.
    private static void HideStraySeparators(MenuItem menu)
    {
        var items = menu.Items.OfType<Control>().ToList();
        Separator? pending = null;
        var seenItem = false;
        foreach (var control in items)
        {
            if (control is Separator separator)
            {
                separator.IsVisible = false;
                if (seenItem)
                    pending ??= separator;
                continue;
            }
            if (!control.IsVisible)
                continue;
            if (pending != null)
                pending.IsVisible = true;
            pending = null;
            seenItem = true;
        }
    }
}

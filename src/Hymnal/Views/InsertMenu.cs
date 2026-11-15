using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Hymnal.Core.Markua.Editing;

namespace Hymnal.Views;

/// <summary>
/// Fills the main window's Insert menu from <see cref="MarkuaSnippets"/>: a separator between sections, and the
/// Heading, Callout and Book Structure items in submenus. Each item runs
/// <c>EditorViewModel.InsertSnippetCommand</c> (bound through the window's data context), so items grey out
/// whenever no editor can take an insert.
/// </summary>
public static class InsertMenu
{
    /// <summary>Path from the main window's view model to the command every item runs.</summary>
    public const string CommandPath = "EditorViewModel.InsertSnippetCommand";

    public static void Populate(MenuItem root)
    {
        root.Items.Clear();
        MarkuaSnippetSection? section = null;
        var submenus = new Dictionary<string, MenuItem>();

        foreach (var snippet in MarkuaSnippets.All)
        {
            if (section != null && section != snippet.Section)
                root.Items.Add(new Separator());
            section = snippet.Section;

            var item = new MenuItem
            {
                Header = snippet.Title,
                CommandParameter = snippet,
                [!MenuItem.CommandProperty] = new Binding(CommandPath),
            };
            if (snippet.Shortcut != null)
                item.InputGesture = KeyGesture.Parse(snippet.Shortcut.Gesture);
            ToolTip.SetTip(item, snippet.Syntax);

            if (snippet.Submenu is null)
            {
                root.Items.Add(item);
                continue;
            }

            if (!submenus.TryGetValue(snippet.Submenu, out var submenu))
            {
                submenu = new MenuItem { Header = snippet.Submenu };
                submenus[snippet.Submenu] = submenu;
                root.Items.Add(submenu);
            }
            submenu.Items.Add(item);
        }
    }
}

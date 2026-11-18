using System;
using System.Windows.Input;

namespace Hymnal.Commands;

/// <summary>
/// One entry in the shell's command catalog (<c>MainWindowViewModel.ShellCommands</c>). The catalog is the single
/// source for the menu bar (<c>ShellMenu</c>), the command palette and the window's key bindings, so a command is
/// added in one place and shows up consistently everywhere. Everything that can change while the app runs (the
/// command itself, its title, whether it is offered, a check mark) is a function, read when the menu opens or the
/// palette is built.
/// </summary>
public sealed class ShellCommand
{
    public ShellCommand(string id, string menu, string menuTitle, Func<ICommand?> command)
    {
        Id = id;
        Menu = menu;
        MenuTitle = () => menuTitle;
        Command = command;
    }

    /// <summary>Stable id; the palette entry is <c>command:{Id}</c>.</summary>
    public string Id { get; }

    /// <summary>The top-level menu it lives in (see <see cref="ShellMenus.Order"/>).</summary>
    public string Menu { get; }

    /// <summary>A submenu inside <see cref="Menu"/> (e.g. "S_pelling"), or null for a direct item.</summary>
    public string? Submenu { get; init; }

    /// <summary>Items with different groups in the same (sub)menu are separated by a separator.</summary>
    public int Group { get; init; }

    /// <summary>The menu header, with its access key (<c>_</c>).</summary>
    public Func<string> MenuTitle { get; init; }

    /// <summary>The palette title; defaults to the menu title without its access key.</summary>
    public Func<string>? PaletteTitle { get; init; }

    /// <summary>The palette category; defaults to <see cref="Menu"/> without its access key.</summary>
    public string? PaletteCategory { get; init; }

    /// <summary>Extra palette text under the title (e.g. a snippet's syntax); also the menu item's tooltip.</summary>
    public string? Detail { get; init; }

    /// <summary>The command to run; null while it doesn't exist (the entry is then not offered).</summary>
    public Func<ICommand?> Command { get; }

    public object? Parameter { get; init; }

    /// <summary>The key gesture, in <c>KeyGesture.Parse</c> form (e.g. "Ctrl+OemComma").</summary>
    public string? Gesture { get; init; }

    /// <summary>How the gesture is shown, when it differs from <see cref="Gesture"/> (e.g. "Ctrl+,").</summary>
    public string? ShortcutDisplay { get; init; }

    /// <summary>
    /// Whether the window binds <see cref="Gesture"/>. False when something else handles the keys (the editor
    /// handles Insert shortcuts) and the gesture is only shown.
    /// </summary>
    public bool BindsKey { get; init; } = true;

    /// <summary>The command the key binding runs, when it differs from <see cref="Command"/>.</summary>
    public Func<ICommand?>? KeyCommand { get; init; }

    /// <summary>Whether it is offered right now (menu visibility, palette inclusion). Defaults to always.</summary>
    public Func<bool> IsAvailable { get; init; } = static () => true;

    /// <summary>
    /// An extra condition for the palette only (e.g. Save is listed in the menu always, but offered in the palette
    /// only when there is something to save). The palette also skips any command that can't execute.
    /// </summary>
    public Func<bool>? IsOfferedInPalette { get; init; }

    /// <summary>A check mark for toggles; null for plain commands.</summary>
    public Func<bool>? IsChecked { get; init; }

    /// <summary>False for palette-only entries.</summary>
    public bool InMenu { get; init; } = true;

    /// <summary>False for menu-only entries.</summary>
    public bool InPalette { get; init; } = true;

    /// <summary>The palette title now.</summary>
    public string CurrentPaletteTitle => PaletteTitle?.Invoke() ?? StripAccessKey(MenuTitle());

    /// <summary>The palette category.</summary>
    public string Category => PaletteCategory ?? StripAccessKey(Menu);

    /// <summary>The shortcut as shown in menus and the palette.</summary>
    public string? Shortcut => ShortcutDisplay ?? Gesture;

    /// <summary>Removes the access-key marker (<c>_</c>; a doubled <c>__</c> is a literal underscore).</summary>
    public static string StripAccessKey(string header) =>
        header.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_");
}

/// <summary>The menu bar's top-level menus, in order.</summary>
public static class ShellMenus
{
    public const string File = "_File";
    public const string View = "_View";
    public const string Go = "_Go";
    public const string Insert = "_Insert";
    public const string Run = "_Run";
    public const string Tools = "_Tools";
    public const string Help = "_Help";

    public static readonly string[] Order = [File, View, Go, Insert, Run, Tools, Help];
}

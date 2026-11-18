using System;
using System.Collections.Generic;
using Hymnal.Commands;
using Hymnal.Core.Markua.Editing;
using static Hymnal.Commands.ShellMenus;

namespace Hymnal.ViewModels;

// The shell's command catalog: every command the menu bar, the command palette and the window's key bindings
// offer, in menu order. ShellMenu builds the menu bar from it, MainWindow binds its gestures, and the palette lists
// what can run now. Add a command here and nowhere else.
public partial class MainWindowViewModel
{
    private IReadOnlyList<ShellCommand>? _shellCommands;

    /// <summary>The command catalog, in menu order (built on first use).</summary>
    public IReadOnlyList<ShellCommand> ShellCommands => _shellCommands ??= BuildShellCommands();

    private IReadOnlyList<ShellCommand> BuildShellCommands()
    {
        var spelling = EditorViewModel.Spelling;
        var run = WritingRun;
        var commands = new List<ShellCommand>
        {
            // File
            new("open-workspace", File, "_Open Workspace…", () => WorkspaceViewModel.OpenWorkspaceCommand),
            new("close-workspace", File, "_Close Workspace", () => WorkspaceViewModel.CloseWorkspaceCommand)
            {
                IsOfferedInPalette = () => WorkspaceViewModel.HasWorkspace,
            },
            new("save", File, "_Save", () => EditorViewModel.SaveCommand)
            {
                Group = 1, Gesture = "Ctrl+S", BindsKey = false, // the editor handles Ctrl+S
                IsOfferedInPalette = () => EditorViewModel.IsDirty,
            },
            new("capture-idea", File, "New _Idea…", () => QuickCaptureIdeaCommand)
            {
                Group = 1, Gesture = "Ctrl+Shift+I", PaletteTitle = () => "Capture an Idea…",
                IsAvailable = () => IsIdeasModeAvailable,
            },
            new("open-book-txt", File, "Open Book.txt", () => OpenBookTxtCommand)
            {
                InMenu = false, IsOfferedInPalette = () => WorkspaceViewModel.HasWorkspace,
            },
            new("exit", File, "E_xit", () => ExitCommand) { Group = 2 },

            // View
            new("toggle-left-rail", View, "Toggle _Left Sidebar", () => ToggleSidebarCommand)
            {
                Gesture = "Ctrl+B",
                // The window's Ctrl+B gives way to the editor's Bold while Markua can be inserted.
                KeyCommand = () => ToggleSidebarShortcutCommand,
            },
            new("toggle-right-rail", View, "Toggle _Right Sidebar", () => ToggleRightRailCommand)
            {
                Gesture = "Ctrl+Alt+B",
            },
            new("toggle-info", View, "", () => ToggleRightPaneCommand)
            {
                MenuTitle = () => ChapterInfoViewModel.ToggleMenuHeader, Parameter = "info", Gesture = "F3",
            },
            new("toggle-notes", View, "Toggle _Notes", () => ToggleRightPaneCommand)
            {
                Parameter = "notes", Gesture = "F4",
            },
            new("toggle-manage", View, "Toggle _Manage View", () => ToggleGanttCommand) { Gesture = "F5" },
            new("toggle-beats", View, "Toggle _Beats", () => ToggleRightPaneCommand)
            {
                Parameter = "beats", Gesture = "F7", IsAvailable = () => IsBeatsSectionAvailable,
            },
            new("toggle-chat", View, "Toggle _AI Chat", () => ToggleRightPaneCommand)
            {
                Parameter = "chat", Gesture = "Ctrl+Shift+A",
            },
            new("focus-mode", View, "_Focus Mode", () => ToggleFocusModeCommand)
            {
                Group = 1, Gesture = "F11", PaletteTitle = () => IsFocusMode ? "Leave Focus Mode" : "Focus Mode",
            },
            new("preview-write", View, "_Write Only", () => EditorViewModel.SetViewModeCommand)
            {
                Group = 1, Submenu = "Markua _Preview", Parameter = EditorViewMode.Write,
                PaletteTitle = () => "Markua Preview: Write Only",
                IsOfferedInPalette = () => EditorViewModel.IsPreviewAvailable,
            },
            new("preview-split", View, "_Split: Write and Preview", () => EditorViewModel.SetViewModeCommand)
            {
                Group = 1, Submenu = "Markua _Preview", Parameter = EditorViewMode.Split,
                PaletteTitle = () => "Markua Preview: Split Write and Preview",
                IsOfferedInPalette = () => EditorViewModel.IsPreviewAvailable,
            },
            new("preview-only", View, "_Preview Only", () => EditorViewModel.SetViewModeCommand)
            {
                Group = 1, Submenu = "Markua _Preview", Parameter = EditorViewMode.Preview,
                PaletteTitle = () => "Markua Preview: Preview Only",
                IsOfferedInPalette = () => EditorViewModel.IsPreviewAvailable,
            },
            new("preview-cycle", View, "_Cycle Mode", () => EditorViewModel.CycleViewModeCommand)
            {
                Group = 2, Submenu = "Markua _Preview", Gesture = "F6",
                PaletteTitle = () => "Markua Preview: Cycle Mode",
                IsOfferedInPalette = () => EditorViewModel.IsPreviewAvailable,
            },
            new("show-whole-collection", View, "", () => WorkspaceViewModel.ShowWholeCollectionCommand)
            {
                InMenu = false,
                PaletteTitle = () => $"Show the Whole {WorkspaceViewModel.Profile.CollectionNoun}",
                IsOfferedInPalette = () => WorkspaceViewModel.FocusedStory != null,
            },
            new("reset-write-layout", View, "_Reset Write Layout", () => ResetWriteLayoutCommand) { Group = 2 },

            // Go
            new("go-to", Go, "_Go To…", () => ShowGoToCommand) { Gesture = "Ctrl+P", InPalette = false },
            new("command-palette", Go, "_Command Palette…", () => ShowCommandsCommand)
            {
                Gesture = "Ctrl+Shift+P", InPalette = false,
            },
            new("mode-research", Go, "_Research", () => SelectResearchCommand)
            {
                Group = 1, PaletteTitle = () => "Go to Research",
            },
            new("mode-ideas", Go, "_Ideas", () => SelectIdeasCommand)
            {
                Group = 1, PaletteTitle = () => "Go to Ideas", IsAvailable = () => IsIdeasModeAvailable,
            },
            new("mode-plan", Go, "_Plan", () => SelectPlanCommand) { Group = 1, PaletteTitle = () => "Go to Plan" },
            new("collection-details", Go, "", () => ShowCollectionDetailsCommand)
            {
                Group = 1, InMenu = false,
                PaletteTitle = () => $"{WorkspaceViewModel.Profile.CollectionNoun} Details",
                IsOfferedInPalette = () => WorkspaceViewModel.HasWorkspace && PlanHostViewModel?.Details != null,
            },
            new("mode-write", Go, "_Write", () => SelectWriteCommand)
            {
                Group = 1, PaletteTitle = () => "Go to Write",
            },
            new("mode-manage", Go, "_Manage", () => SelectManageCommand)
            {
                Group = 1, PaletteTitle = () => "Go to Manage",
            },
        };

        // Insert: the Markua snippets. The editor handles their keys; the menu and palette only show them.
        foreach (var snippet in MarkuaSnippets.All)
        {
            commands.Add(new ShellCommand($"insert-{snippet.Id}", Insert, snippet.Title,
                () => EditorViewModel.InsertSnippetCommand)
            {
                Group = (int)snippet.Section, Submenu = snippet.Submenu, Parameter = snippet,
                PaletteTitle = () => snippet.PaletteTitle, Detail = snippet.Syntax,
                Gesture = snippet.Shortcut?.Gesture, ShortcutDisplay = snippet.Shortcut?.Display, BindsKey = false,
            });
        }

        commands.AddRange(
        [
            // Run
            new("run-start", Run, "_New Run…", () => run?.OpenCommand)
            {
                Gesture = "Ctrl+Alt+R", IsAvailable = () => run != null,
            },
            new("run-end", Run, "_End Run", () => run?.EndCommand)
            {
                IsAvailable = () => run?.IsRunning == true,
            },
            new("run-logbook", Run, "_Logbook…", () => run?.ShowLogbookCommand)
            {
                Group = 1, Gesture = "Ctrl+Alt+L", IsAvailable = () => run != null,
            },

            // Tools
            new("spelling-check", Tools, "_Check Spelling", () => spelling.CheckSpellingCommand)
            {
                Submenu = "S_pelling", Gesture = "Shift+F7", IsAvailable = () => spelling.IsAvailable,
            },
            new("spelling-as-you-type", Tools, "Check As You _Type", () => spelling.ToggleCheckAsYouTypeCommand)
            {
                Submenu = "S_pelling", IsAvailable = () => spelling.IsAvailable,
                IsChecked = () => spelling.Service?.Preferences.CheckAsYouType == true,
            },
            new("spelling-next", Tools, "_Next Issue", () => spelling.NextIssueCommand)
            {
                Submenu = "S_pelling", Group = 1, Gesture = "F8", IsAvailable = () => spelling.IsAvailable,
            },
            new("spelling-previous", Tools, "_Previous Issue", () => spelling.PreviousIssueCommand)
            {
                Submenu = "S_pelling", Group = 1, Gesture = "Shift+F8", IsAvailable = () => spelling.IsAvailable,
            },
            new("spelling-clear", Tools, "C_lear Spelling Marks", () => spelling.ClearCommand)
            {
                Submenu = "S_pelling", Group = 1, IsAvailable = () => spelling.IsAvailable,
            },
            new("spelling-settings", Tools, "Spelling _Settings and Project Dictionary…",
                () => OpenSpellingSettingsCommand)
            {
                Submenu = "S_pelling", Group = 2, IsAvailable = () => spelling.IsAvailable,
            },
            new("toggle-sound", Tools, "S_ound", () => ToggleSoundCommand)
            {
                IsAvailable = () => SoundPreferences != null,
                IsChecked = () => SoundPreferences?.SoundEnabled == true,
                PaletteTitle = () => SoundPreferences?.SoundEnabled == true ? "Turn Sound Off" : "Turn Sound On",
            },
            new("settings", Tools, "_Settings…", () => OpenSettingsCommand)
            {
                Group = 1, Gesture = "Ctrl+OemComma", ShortcutDisplay = "Ctrl+,",
            },

            // Help
            new("check-updates", Help, "Check for _Updates", () => Updates?.CheckCommand)
            {
                IsAvailable = () => Updates != null,
            },
            new("about", Help, "_About Hymnal", () => ShowAboutCommand) { Group = 1, Gesture = "F1" },
        ]);

        return commands;
    }
}

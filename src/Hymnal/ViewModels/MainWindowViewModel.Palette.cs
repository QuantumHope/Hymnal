using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows.Input;
using Hymnal.Core.Models;
using ReactiveUI;

namespace Hymnal.ViewModels;

// The command palette's entries: every command the menus, mode tabs and shortcuts offer that can run right
// now, and every place in the open project the palette can go to.
public partial class MainWindowViewModel
{
    /// <summary>The command palette shown over the window (Ctrl+P / Ctrl+Shift+P).</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>Ctrl+P: go to a chapter, scene, story or idea.</summary>
    public ReactiveCommand<Unit, Unit> ShowGoToCommand { get; }

    /// <summary>Ctrl+Shift+P: run a command.</summary>
    public ReactiveCommand<Unit, Unit> ShowCommandsCommand { get; }

    private IReadOnlyList<PaletteEntry> BuildPaletteEntries()
    {
        var entries = new List<PaletteEntry>();
        AddCommandEntries(entries);
        AddDestinationEntries(entries);
        return entries;
    }

    private void AddCommandEntries(List<PaletteEntry> entries)
    {
        // Only what can run now goes in, so the palette never offers a command that would do nothing.
        void Add(string id, string category, string title, ICommand command, object? parameter = null,
            string? shortcut = null)
        {
            if (!command.CanExecute(parameter))
                return;
            entries.Add(new PaletteEntry($"command:{id}", PaletteEntryKind.Command, title, category, null, shortcut,
                () =>
                {
                    command.Execute(parameter);
                    return Task.CompletedTask;
                }));
        }

        var hasWorkspace = WorkspaceViewModel.HasWorkspace;

        Add("open-workspace", "File", "Open Workspace…", WorkspaceViewModel.OpenWorkspaceCommand);
        if (hasWorkspace)
            Add("close-workspace", "File", "Close Workspace", WorkspaceViewModel.CloseWorkspaceCommand);
        if (EditorViewModel.IsDirty)
            Add("save", "File", "Save", EditorViewModel.SaveCommand, shortcut: "Ctrl+S");
        Add("settings", "File", "Settings", OpenSettingsCommand, shortcut: "Ctrl+,");
        Add("exit", "File", "Exit", ExitCommand);

        Add("mode-research", "Mode", "Go to Research", SelectResearchCommand);
        if (IsIdeasModeAvailable)
            Add("mode-ideas", "Mode", "Go to Ideas", SelectIdeasCommand);
        Add("mode-plan", "Mode", "Go to Plan", SelectPlanCommand);
        Add("mode-write", "Mode", "Go to Write", SelectWriteCommand);
        Add("mode-manage", "Mode", "Go to Manage", SelectManageCommand);

        Add("toggle-left-rail", "View", "Toggle Left Sidebar", ToggleSidebarCommand, shortcut: "Ctrl+B");
        Add("toggle-right-rail", "View", "Toggle Right Sidebar", ToggleRightRailCommand, shortcut: "Ctrl+Alt+B");
        Add("toggle-info", "View", ChapterInfoViewModel.ToggleMenuHeader.Replace("_", ""), ToggleRightPaneCommand,
            "info", "F3");
        Add("toggle-notes", "View", "Toggle Notes", ToggleRightPaneCommand, "notes", "F4");
        if (IsBeatsSectionAvailable)
            Add("toggle-beats", "View", "Toggle Beats", ToggleRightPaneCommand, "beats", "F7");
        Add("toggle-chat", "View", "Toggle AI Chat", ToggleRightPaneCommand, "chat", "Ctrl+Shift+A");
        Add("toggle-manage", "View", "Toggle Manage View", ToggleGanttCommand, shortcut: "F5");
        Add("focus-mode", "View", IsFocusMode ? "Leave Focus Mode" : "Focus Mode", ToggleFocusModeCommand,
            shortcut: "F11");
        if (EditorViewModel.IsPreviewAvailable)
        {
            Add("preview-write", "View", "Markua Preview: Write Only", EditorViewModel.SetViewModeCommand,
                EditorViewMode.Write);
            Add("preview-split", "View", "Markua Preview: Split Write and Preview", EditorViewModel.SetViewModeCommand,
                EditorViewMode.Split);
            Add("preview-only", "View", "Markua Preview: Preview Only", EditorViewModel.SetViewModeCommand,
                EditorViewMode.Preview);
            Add("preview-cycle", "View", "Markua Preview: Cycle Mode", EditorViewModel.CycleViewModeCommand,
                shortcut: "F6");
        }
        if (SoundPreferences != null)
            Add("toggle-sound", "View", SoundPreferences.SoundEnabled ? "Turn Sound Off" : "Turn Sound On",
                ToggleSoundCommand);
        Add("reset-write-layout", "View", "Reset Write Layout", ResetWriteLayoutCommand);

        if (WorkspaceViewModel.FocusedStory != null)
            Add("show-whole-collection", "View", $"Show the Whole {WorkspaceViewModel.Profile.CollectionNoun}",
                WorkspaceViewModel.ShowWholeCollectionCommand);

        if (IsIdeasModeAvailable)
            Add("capture-idea", "Ideas", "Capture an Idea…", QuickCaptureIdeaCommand, shortcut: "Ctrl+Shift+I");

        Add("about", "Help", "About Hymnal", ShowAboutCommand, shortcut: "F1");
    }

    private void AddDestinationEntries(List<PaletteEntry> entries)
    {
        if (!WorkspaceViewModel.HasWorkspace)
            return;

        // The sidebar's own list, so story focus narrows the palette as it narrows the sidebar. Dividers
        // (parts, sections, multi-scene stories) can't be opened; the files inside them name them instead.
        string? partTitle = null;
        foreach (var node in WorkspaceViewModel.VisibleNodes)
        {
            if (node.Node.Kind == NodeKind.Part)
            {
                partTitle = node.Node.Title;
                continue;
            }
            if (node.Node.Kind != NodeKind.Chapter || node.Node.IsMissing)
                continue;

            var noun = WorkspaceViewModel.NounFor(node);
            var detail = partTitle == null ? node.Node.RelativePath : $"{partTitle} · {node.Node.RelativePath}";
            var target = node;
            entries.Add(new PaletteEntry($"node:{node.Node.RelativePath}", PaletteEntryKind.Destination,
                node.Node.Title, noun, detail, null,
                () =>
                {
                    LeaveSettings();
                    WorkspaceViewModel.SelectedNode = target;
                    ActiveMode = ShellMode.Write;
                    return Task.CompletedTask;
                }));
        }

        if (IdeasViewModel is { } ideas && IsIdeasModeAvailable)
        {
            foreach (var card in ideas.Cards)
            {
                var target = card;
                entries.Add(new PaletteEntry($"idea:{card.Id}", PaletteEntryKind.Destination,
                    string.IsNullOrWhiteSpace(card.Title) ? "Untitled idea" : card.Title, "Idea",
                    string.IsNullOrWhiteSpace(card.Premise) ? null : card.Premise, null,
                    () =>
                    {
                        LeaveSettings();
                        ActiveMode = ShellMode.Ideas;
                        ideas.Selected = target;
                        return Task.CompletedTask;
                    }));
            }
        }
    }
}

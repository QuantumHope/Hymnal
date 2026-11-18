using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows.Input;
using Hymnal.Core.Markua.Editing;
using Hymnal.Core.Models;
using Hymnal.Theming;
using ReactiveUI;

namespace Hymnal.ViewModels;

// The command palette's entries: every catalog command (MainWindowViewModel.Commands.cs) that can run right now,
// the themes, and every place in the open project the palette can go to.
public partial class MainWindowViewModel
{
    /// <summary>The command palette shown over the window (Ctrl+P / Ctrl+Shift+P).</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>Ctrl+P: go to a chapter, scene, story or idea.</summary>
    public ReactiveCommand<Unit, Unit> ShowGoToCommand { get; }

    /// <summary>Ctrl+Shift+P: run a command.</summary>
    public ReactiveCommand<Unit, Unit> ShowCommandsCommand { get; }

    private readonly IThemeService? _themes;

    /// <summary>Plan → Details: the open collection at a glance (the sidebar's collection row, the palette).</summary>
    public void ShowCollectionDetails()
    {
        if (!WorkspaceViewModel.HasWorkspace)
            return;
        LeaveSettings();
        ActiveMode = ShellMode.Plan;
        PlanHostViewModel?.ShowDetails();
    }

    private ReactiveCommand<Unit, Unit>? _showCollectionDetailsCommand;
    private ReactiveCommand<Unit, Unit>? _openBookTxtCommand;

    /// <summary><see cref="ShowCollectionDetails"/> as a command, for the command catalog.</summary>
    public ReactiveCommand<Unit, Unit> ShowCollectionDetailsCommand =>
        _showCollectionDetailsCommand ??= ReactiveCommand.Create(ShowCollectionDetails);

    /// <summary><see cref="OpenBookTxt"/> as a command, for the command catalog.</summary>
    public ReactiveCommand<Unit, Unit> OpenBookTxtCommand => _openBookTxtCommand ??= ReactiveCommand.Create(OpenBookTxt);

    /// <summary>Opens Book.txt, the raw list, in Write.</summary>
    public void OpenBookTxt()
    {
        if (!WorkspaceViewModel.HasWorkspace)
            return;
        LeaveSettings();
        ActiveMode = ShellMode.Write;
        WorkspaceViewModel.SelectBookCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private IReadOnlyList<PaletteEntry> BuildPaletteEntries()
    {
        var entries = new List<PaletteEntry>();
        AddCommandEntries(entries);
        AddDestinationEntries(entries);
        return entries;
    }

    private void AddCommandEntries(List<PaletteEntry> entries)
    {
        // The catalog's commands that can run now, so the palette never offers a command that would do nothing.
        foreach (var shell in ShellCommands)
        {
            if (!shell.InPalette || !shell.IsAvailable() || shell.IsOfferedInPalette?.Invoke() == false)
                continue;
            var command = shell.Command();
            var parameter = shell.Parameter;
            if (command == null || !command.CanExecute(parameter))
                continue;
            entries.Add(new PaletteEntry($"command:{shell.Id}", PaletteEntryKind.Command, shell.CurrentPaletteTitle,
                shell.Category, shell.Detail, shell.Shortcut,
                () =>
                {
                    command.Execute(parameter);
                    return Task.CompletedTask;
                }));
        }

        // Themes last: there are many, and the query ("theme", "mordor", "dark") finds them.
        if (_themes != null)
        {
            var current = _themes.Current.Id;
            foreach (var theme in _themes.Available)
            {
                var id = theme.Id;
                entries.Add(new PaletteEntry($"command:theme:{id}", PaletteEntryKind.Command,
                    $"Theme: {theme.DisplayName}", "Theme",
                    id == current ? $"Current theme · {theme.Description}" : theme.Description, null,
                    () => _themes.SelectAsync(id)));
            }
        }
    }

    private void AddDestinationEntries(List<PaletteEntry> entries)
    {
        if (!WorkspaceViewModel.HasWorkspace)
            return;

        // Collections: the open one shows its details; another is opened first, then shown.
        var collectionNoun = WorkspaceViewModel.Profile.CollectionNoun;
        var collections = CollectionsViewModel;
        if (collections is { Collections.Count: > 0 })
        {
            foreach (var collection in collections.Collections)
            {
                var slug = collection.Slug;
                var isOpen = collection.IsActive;
                entries.Add(new PaletteEntry($"collection:{slug}", PaletteEntryKind.Destination,
                    collection.Title, collectionNoun,
                    isOpen ? $"Open now · {collection.EntryCount} entries" : $"{collection.EntryCount} entries",
                    null,
                    async () =>
                    {
                        if (!isOpen && !await collections.OpenAsync(slug))
                            return;
                        ShowCollectionDetails();
                    }));
            }
        }
        else
        {
            // One unnamed (or never listed) collection: it is still a place to go.
            entries.Add(new PaletteEntry("collection:open", PaletteEntryKind.Destination,
                WorkspaceViewModel.CollectionDisplayName, collectionNoun, "Open now", null,
                () =>
                {
                    ShowCollectionDetails();
                    return Task.CompletedTask;
                }));
        }

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
            // Missing and excluded files can't be opened (TrySwitchChapterAsync refuses excluded ones).
            if (node.Node.Kind != NodeKind.Chapter || node.Node.IsMissing || node.Node.IsExcluded)
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

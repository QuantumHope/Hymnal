using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Hymnal.Core.Models;
using Hymnal.ViewModels;

namespace Hymnal.Views;

/// <summary>
/// The collection menu, shown from Write's sidebar (the ⋯ button) and from the collection bar over Plan and
/// Manage: name the open collection (book, story), open another collection or start one, add a story that is in
/// no collection, publish to LeanPub, export for submission.
/// </summary>
public static class CollectionMenu
{
    public static async Task ShowAsync(Control anchor, Window owner, MainWindowViewModel mainVm)
    {
        if (mainVm.CollectionsViewModel is not { } collections || !mainVm.WorkspaceViewModel.HasWorkspace)
            return;

        var workspace = mainVm.WorkspaceViewModel;
        var noun = workspace.Profile.CollectionNoun.ToLowerInvariant();
        await collections.RefreshAsync();

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem { Header = workspace.CollectionDisplayName, IsEnabled = false });
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item(
            collections.IsOpenCollectionNamed ? $"Rename this {noun}…" : $"Name this {noun}…",
            () => NameOpenCollectionAsync(owner, collections, workspace)));

        if (collections.SupportsMultiple)
        {
            foreach (var other in collections.Collections.Where(c => !c.IsActive).ToList())
                flyout.Items.Add(Item($"Open “{other.Title}”",
                    () => OpenCollectionAsync(owner, collections, workspace, other.Slug)));
            flyout.Items.Add(Item("New collection…", () => NewCollectionAsync(owner, collections, workspace)));

            var unassigned = await collections.UnassignedAsync();
            if (unassigned.Count > 0)
            {
                var add = new MenuItem { Header = $"Add a story in no collection ({unassigned.Count})" };
                foreach (var path in unassigned)
                    add.Items.Add(Item(path, () => collections.AddToOpenAsync(path)));
                flyout.Items.Add(add);
            }
        }

        if (workspace.IsStoryFocused)
        {
            flyout.Items.Add(new Separator());
            flyout.Items.Add(Item($"Show the whole {noun}", () =>
            {
                workspace.ShowWholeCollectionCommand.Execute().Subscribe();
                return Task.CompletedTask;
            }));
        }

        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("Publish to LeanPub…",
            () => PublishDialog.ShowAsync(owner, collections, workspace.CollectionDisplayName)));
        if (mainVm.SubmissionExportViewModel is { IsAvailable: true } export)
        {
            // Short Story: the story. Anthology: the story being written (or focused); a story's own
            // right-click menu in the sidebar exports any other.
            var label = workspace.Profile.Kind == ProjectKind.Story
                ? "Export for submission…"
                : "Export this story for submission…";
            flyout.Items.Add(Item(label, () => SubmissionExportDialog.ShowAsync(owner, export)));
        }
        flyout.ShowAt(anchor);
    }

    /// <summary>Opens another collection (asking for the open one's name first if it has none).</summary>
    public static Task OpenAsync(Window owner, MainWindowViewModel mainVm, string slug) =>
        mainVm.CollectionsViewModel is { } collections
            ? OpenCollectionAsync(owner, collections, mainVm.WorkspaceViewModel, slug)
            : Task.CompletedTask;

    /// <summary>Starts a new collection and offers to open it.</summary>
    public static Task NewAsync(Window owner, MainWindowViewModel mainVm) =>
        mainVm.CollectionsViewModel is { } collections
            ? NewCollectionAsync(owner, collections, mainVm.WorkspaceViewModel)
            : Task.CompletedTask;

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static async Task<bool> NameOpenCollectionAsync(Window owner, CollectionsViewModel collections,
        WorkspaceViewModel workspace, string? prompt = null)
    {
        var noun = workspace.Profile.CollectionNoun.ToLowerInvariant();
        var title = await FileNameDialog.ShowAsync(owner, $"Name this {noun}",
            prompt ?? $"The {noun}'s title, shown in Hymnal's sidebar, Plan and Schedule.",
            collections.IsOpenCollectionNamed ? workspace.CollectionTitle : workspace.WorkspaceName);
        return title != null && await collections.RenameOpenAsync(title);
    }

    // An unnamed open collection is named after the workspace when another opens (CollectionsViewModel.OpenAsync),
    // so neither opening nor creating asks for a second name.
    private static Task OpenCollectionAsync(Window owner, CollectionsViewModel collections,
        WorkspaceViewModel workspace, string slug) =>
        collections.OpenAsync(slug);

    private static async Task NewCollectionAsync(Window owner, CollectionsViewModel collections,
        WorkspaceViewModel workspace)
    {
        var title = await FileNameDialog.ShowAsync(owner, "New collection",
            "Its title. It opens straight away, empty: add stories in Plan, or from the ⋯ menu.", "");
        if (title != null)
            await collections.CreateAndOpenAsync(title);
    }
}

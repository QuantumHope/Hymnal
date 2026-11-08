using System.Reflection;
using Hymnal.Core.Common;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using NSubstitute;

namespace Hymnal.ViewModels;

/// <summary>
/// Guards against <c>FileSystemWatcher</c> leaks. Each live watcher holds an inotify instance on Linux,
/// and the per-user limit (<c>fs.inotify.max_user_instances</c>, often 128) is shared with every other
/// process, so a suite that leaks watchers fails with "The configured user limit (128) on the number of
/// inotify instances has been reached". This runs the watcher owners through an open/dispose cycle
/// and checks the process's inotify descriptors (<c>/proc/self/fd</c>) return to where they started.
/// </summary>
[Collection(WatcherLeakGuardCollection.Name)]
public sealed class WatcherLeakGuardTests
{
    static WatcherLeakGuardTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [LinuxOnlyFact]
    public async Task Watcher_owners_release_their_inotify_instances_on_dispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "hymnal-watcher-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Book.txt"), "one.md\n");
        File.WriteAllText(Path.Combine(root, "one.md"), "# One\n");
        try
        {
            var baseline = SettledInotifyCount();
            var notifications = Substitute.For<INotificationService>();
            var store = new MetadataStore();
            // Held until the check, so a leaked watcher can't be finalized (closing its handle) and hide the leak.
            var keepAlive = new List<object>();

            using (var owned = new OwnedDisposables())
            {
                var manuscript = owned.Track(new ManuscriptService(notifications));
                var load = await manuscript.LoadWorkspaceAsync(root);
                Assert.True(load.IsSuccess, load.Error);

                var editor = owned.Track(new EditorViewModel(store, notifications, new WordCountService(), new StoryFileWriter(store)));
                await editor.OpenArbitraryFileAsync(Path.Combine(root, "one.md"));

                var workspace = new WorkspaceViewModel(
                    manuscript,
                    Substitute.For<IBookTxtStructureService>(),
                    Substitute.For<IFilePickerService>(),
                    Substitute.For<IAppSettingsStore>(),
                    Substitute.For<IFolderPickerService>(),
                    notifications,
                    editor,
                    new ChapterRegistryService(store),
                    new PhaseDataService(store),
                    new TargetsService(store),
                    new WordCountService(),
                    new WordCountHistoryService(store));
                SetPrivateField(workspace, "_model", load.Value!);
                SetPrivateField(workspace, "_hasWorkspace", true);

                var gitPanel = owned.Track(new GitPanelViewModel(workspace, editor, new RepositoryGitService(), notifications));
                await gitPanel.RefreshAsync();
                keepAlive.AddRange([manuscript, editor, workspace, gitPanel]);

                // One instance each: Book.txt, the open file, and the Git workspace tree.
                Assert.True(CountInotifyInstances() >= baseline + 3,
                    $"Expected at least {baseline + 3} inotify instances while open; saw {CountInotifyInstances()} (baseline {baseline}).");
            }

            var released = await WaitUntilAsync(() => CountInotifyInstances() <= baseline, TimeSpan.FromSeconds(5));
            Assert.True(released,
                $"inotify instances did not return to the baseline of {baseline} after dispose; saw {CountInotifyInstances()}. " +
                "A watcher owner (ManuscriptService / EditorViewModel / GitPanelViewModel) is not releasing its FileSystemWatcher.");
            GC.KeepAlive(keepAlive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Collects unreachable watchers first, so a finalizer can't lower the count mid-test.</summary>
    private static int SettledInotifyCount()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return CountInotifyInstances();
    }

    private static int CountInotifyInstances()
    {
        var count = 0;
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == "anon_inode:inotify")
                    count++;
            }
            catch (IOException)
            {
                // The descriptor closed while enumerating.
            }
        }
        return count;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(50);
        }
        return true;
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = typeof(WorkspaceViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"WorkspaceViewModel field '{name}' was not found.");
        field.SetValue(target, value);
    }

    /// <summary>Reports a visible repository, so the Git panel starts watching the workspace.</summary>
    private sealed class RepositoryGitService : NoOpGitService
    {
        public override Task<Result<GitRepositoryStatus>> GetRepositoryStatusAsync(
            string workspaceRoot, bool includeRemoteState = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
    }
}

/// <summary>
/// Not run in parallel with other collections: the guard counts the whole process's inotify descriptors.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WatcherLeakGuardCollection
{
    public const string Name = "WatcherLeakGuard";
}

/// <summary>A fact that runs only on Linux (it reads <c>/proc</c>).</summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Counts inotify instances through /proc; Linux only.";
    }
}

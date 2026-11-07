using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Common;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using ReactiveUI.Builder;
using Xunit;
using CoreUnit = Hymnal.Core.Common.Unit;

namespace Hymnal.ViewModels;

public sealed class GitPanelViewModelTests : IDisposable
{
    private readonly TestContext _context = new();

    static GitPanelViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task RefreshAsync_WithNoWorkspace_ResetsStateAndStaysHidden()
    {
        _context.ClearWorkspace();
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();

        Assert.False(vm.IsVisible);
        Assert.Null(vm.BranchName);
        Assert.Equal(0, vm.UncommittedChangeCount);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Null(vm.LastError);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, _context.GitService.StatusCalls);
    }

    [Fact]
    public async Task RefreshAsync_WhenRepositoryProbeFails_HidesPanelAndStoresLastError()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitRepositoryStatus.Hidden(
            ProbeFailure("fatal: not a git repository"),
            isGitAvailable: true)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();

        Assert.False(vm.IsVisible);
        Assert.Null(vm.BranchName);
        Assert.Equal(0, vm.UncommittedChangeCount);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal("fatal: not a git repository", vm.LastError);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, _context.GitService.StatusCalls);
    }

    [Fact]
    public async Task RefreshAsync_WithVisibleRepository_ShowsBranchCountAndStatusText()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("feature/git-toolbar", 3)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();

        Assert.True(vm.IsVisible);
        Assert.Equal("feature/git-toolbar", vm.BranchName);
        Assert.Equal(3, vm.UncommittedChangeCount);
        Assert.Equal("3 uncommitted changes", vm.StatusText);
        Assert.Equal("3 uncommitted changes", vm.ChangeSummaryText);
        Assert.True(vm.CanSync);
        Assert.Null(vm.LastError);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, _context.GitService.StatusCalls);
    }

    [Fact]
    public async Task SaveAsync_QueuesSingleRefreshForSaveAndWatcherEvents()
    {
        _context.EnableWorkspace();
        var filePath = _context.CreateWorkspaceFile("draft.md", "before");
        await _context.Editor.OpenArbitraryFileAsync(filePath);
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 2)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 1)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        Assert.Equal(1, _context.GitService.StatusCalls);

        _context.Editor.Text = "after save";
        await _context.Editor.SaveAsync();

        WaitForGitRefreshComplete(
            vm,
            expectedStatusCalls: 2,
            () => vm.UncommittedChangeCount == 1 && vm.StatusText == "1 uncommitted change");
        Assert.Equal("main", vm.BranchName);
    }

    [Fact]
    public async Task ExternalFileChange_RefreshesGitStatusThroughWorkspaceWatcher()
    {
        _context.EnableWorkspace();
        var filePath = _context.CreateWorkspaceFile("watch.md", "before");
        await _context.Editor.OpenArbitraryFileAsync(filePath);
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 4)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 5)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        Assert.Equal(1, _context.GitService.StatusCalls);

        await File.WriteAllTextAsync(filePath, "external change");

        WaitForGitRefreshComplete(
            vm,
            expectedStatusCalls: 2,
            () => vm.UncommittedChangeCount == 5 && vm.StatusText == "5 uncommitted changes");
    }

    [Fact]
    public async Task SyncAsync_UsesDefaultMessageWhenBlank_AndRefreshesAfterSuccess()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 2)));
        _context.GitService.EnqueueSync(Result<GitCommandResult>.Ok(CommitResult("[main abc123] commit\n", string.Empty)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        await vm.SyncAsync("   ");

        Assert.Single(_context.GitService.SyncMessages);
        Assert.StartsWith("Hymnal: save progress ", _context.GitService.SyncMessages[0]);
        WaitForGitRefreshComplete(
            vm,
            expectedStatusCalls: 2,
            () => vm.UncommittedChangeCount == 0 && vm.StatusText == "Up to date");
        Assert.True(vm.IsVisible);
        Assert.Equal("main", vm.BranchName);
        Assert.True(vm.CanSync);
        Assert.Equal("Pull", vm.PrimaryActionText);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public async Task RefreshAsync_WhenUpToDate_ShowsPullAction()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();

        Assert.True(vm.IsFullySynced);
        Assert.Equal("Up to date", vm.ChangeSummaryText);
        Assert.True(vm.CanSync);
        Assert.Equal("Pull", vm.PrimaryActionText);
    }

    [Fact]
    public async Task PullLatestAsync_WhenUpToDate_ShowsAlreadyUpToDateSummary()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        _context.GitService.EnqueuePull(Result<GitCommandResult>.Ok(PullResult("Already up to date.\n", string.Empty)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        await vm.PullLatestAsync();

        WaitForGitRefreshComplete(vm, expectedStatusCalls: 3);
        Assert.Equal("Already up to date", vm.ChangeSummaryText);
    }

    [Fact]
    public void CreateDefaultCommitMessage_UsesIso8601UtcTimestamp()
    {
        var vm = _context.CreateGitPanel();

        var message = vm.CreateDefaultCommitMessage();

        Assert.StartsWith("Hymnal: save progress ", message);
        var timestamp = message["Hymnal: save progress ".Length..];
        var parsed = DateTime.ParseExact(timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public async Task CommitFailure_ShowsRawStderrAndStillRefreshes()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 2)));
        _context.GitService.EnqueueSync(Result<GitCommandResult>.Ok(CommitFailure("fatal: index.lock exists")));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 2)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        await vm.SyncAsync("Save draft");

        Assert.Contains("fatal: index.lock exists", _context.Notifications.Errors);
        WaitForGitRefreshComplete(vm, expectedStatusCalls: 2);
        Assert.Equal("fatal: index.lock exists", vm.LastError);
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public async Task SyncFailure_ShowsRawStderrAndStillRefreshes()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 2)));
        _context.GitService.EnqueueSync(Result<GitCommandResult>.Ok(PushFailure("fatal: unable to access 'https://example.invalid/repo.git': Could not resolve host")));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 2)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        await vm.SyncAsync("Save draft");

        Assert.Contains("fatal: unable to access 'https://example.invalid/repo.git': Could not resolve host", _context.Notifications.Errors);
        WaitForGitRefreshComplete(vm, expectedStatusCalls: 2);
        Assert.Equal("fatal: unable to access 'https://example.invalid/repo.git': Could not resolve host", vm.LastError);
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public async Task WorkspaceChange_DisposesOldWatcherBeforeNewRootRefreshes()
    {
        _context.EnableWorkspace();
        var oldFile = _context.CreateWorkspaceFile("old.md", "before");
        await _context.Editor.OpenArbitraryFileAsync(oldFile);
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 1)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("feature/new-root", 2)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        Assert.Equal(1, _context.GitService.StatusCalls);

        _context.SwitchWorkspace();
        WaitForGitRefreshComplete(
            vm,
            expectedStatusCalls: 2,
            () => vm.BranchName == "feature/new-root" && vm.UncommittedChangeCount == 2);

        var callsBefore = _context.GitService.StatusCalls;
        await File.WriteAllTextAsync(oldFile, "after switch");

        Assert.False(SpinWait.SpinUntil(() => _context.GitService.StatusCalls > callsBefore, TimeSpan.FromMilliseconds(700)));
        Assert.Equal(callsBefore, _context.GitService.StatusCalls);
    }

    [Fact]
    public async Task SyncAsync_UsesDefaultMessageAndRefreshesAfterSync()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 4)));
        _context.GitService.EnqueueSync(Result<GitCommandResult>.Ok(CommitResult("To origin\n", string.Empty)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        await vm.SyncAsync(null);

        Assert.Single(_context.GitService.SyncMessages);
        Assert.StartsWith("Hymnal: save progress ", _context.GitService.SyncMessages[0]);
        WaitForGitRefreshComplete(
            vm,
            expectedStatusCalls: 2,
            () => vm.StatusText == "Up to date" && vm.UncommittedChangeCount == 0);
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public async Task RefreshAsync_WithMergeConflict_DisablesSyncAndNotifiesOnce()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(
            GitTestDoubles.VisibleStatus("main", 0, hasMergeConflict: true, conflictedFiles: new[] { "manuscript/ch01.md" })));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();

        Assert.Equal("Merge conflict", vm.ChangeSummaryText);
        Assert.False(vm.CanSync);
        Assert.Single(_context.Notifications.Errors);
    }

    [Fact]
    public async Task RefreshAsync_AfterUncommittedChanges_IsFullySyncedNotifiesFalse()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 1)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();

        Assert.True(vm.IsFullySynced);

        var notified = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GitPanelViewModel.IsFullySynced))
                notified = true;
        };

        await vm.RefreshAsync();

        Assert.True(notified);
        Assert.False(vm.IsFullySynced);
        Assert.Equal(1, vm.UncommittedChangeCount);
        Assert.Equal("Sync", vm.PrimaryActionText);
    }

    [Fact]
    public async Task WorkspaceChanged_AfterSimulatedReorder_EnablesSyncWithCorrectCount()
    {
        _context.EnableWorkspace();
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 0)));
        _context.GitService.EnqueueStatus(Result<GitRepositoryStatus>.Ok(GitTestDoubles.VisibleStatus("main", 1)));
        var vm = _context.CreateGitPanel();

        await vm.RefreshAsync();
        Assert.True(vm.IsFullySynced);
        Assert.Equal("Pull", vm.PrimaryActionText);

        RaiseWorkspaceChanged(_context.Workspace);

        Assert.True(SpinWait.SpinUntil(
            () => vm.UncommittedChangeCount == 1 && vm.CanSync && !vm.IsFullySynced,
            TimeSpan.FromSeconds(3)));

        Assert.Equal(1, vm.UncommittedChangeCount);
        Assert.Equal("1 uncommitted change", vm.ChangeSummaryText);
        Assert.True(vm.CanSync);
        Assert.False(vm.IsFullySynced);
        Assert.Equal("Sync", vm.PrimaryActionText);
        Assert.Equal(2, _context.GitService.StatusCalls);
    }

    /// <summary>
    /// Waits for a debounced Git refresh to fully complete.
    /// StatusCalls alone is not sufficient — it increments before ApplyStatus runs,
    /// and some mutations set panel text before the post-mutation refresh finishes.
    /// </summary>
    private void WaitForGitRefreshComplete(GitPanelViewModel vm, int expectedStatusCalls, Func<bool>? assertState = null)
    {
        Assert.True(SpinWait.SpinUntil(
            () => !vm.IsBusy
                  && _context.GitService.StatusCalls == expectedStatusCalls
                  && (assertState?.Invoke() ?? true),
            TimeSpan.FromSeconds(3)));
    }

    private static void RaiseWorkspaceChanged(WorkspaceViewModel workspace)
    {
        var field = typeof(WorkspaceViewModel).GetField("_workspaceChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workspace change subject not found.");

        var subject = (IObserver<CoreUnit>)field.GetValue(workspace)!;
        subject.OnNext(CoreUnit.Default);
    }

    private static GitCommandResult ProbeFailure(string stderr)
        => new("git", new[] { "-C", "workspace", "rev-parse", "--is-inside-work-tree" }, "workspace", 128, string.Empty, stderr);

    private static GitCommandResult CommitResult(string stdout, string stderr)
        => new("git", new[] { "-C", "workspace", "commit", "-m", "message" }, "workspace", 0, stdout, stderr);

    private static GitCommandResult CommitFailure(string stderr)
        => new("git", new[] { "-C", "workspace", "commit", "-m", "message" }, "workspace", 1, string.Empty, stderr);

    private static GitCommandResult PushFailure(string stderr)
        => new("git", new[] { "-C", "workspace", "push" }, "workspace", 1, string.Empty, stderr);

    private static GitCommandResult PullResult(string stdout, string stderr)
        => new("git", new[] { "-C", "workspace", "pull", "--no-edit" }, "workspace", 0, stdout, stderr);

    public void Dispose() => _context.Dispose();

    private sealed class TestContext : IDisposable
    {
        private readonly OwnedDisposables _owned = new();
        public RecordingNotificationService Notifications { get; } = new();
        public RecordingGitService GitService { get; } = new();
        public RecordingMetadataStore MetadataStore { get; } = new();
        public FakeAppSettingsStore SettingsStore { get; } = new();
        public FakeFolderPickerService FolderPicker { get; } = new();
        public WordCountService WordCountService { get; } = new();
        public EditorViewModel Editor { get; }
        public WorkspaceViewModel Workspace { get; }
        public string WorkspaceRoot { get; private set; }
        public string ManuscriptRoot { get; private set; }

        public TestContext()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-git-panel-tests", Guid.NewGuid().ToString("N"));
            ManuscriptRoot = Path.Combine(WorkspaceRoot, "manuscript");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(ManuscriptRoot);

            Editor = _owned.Track(new EditorViewModel(MetadataStore, Notifications, WordCountService)
            {
                HasWorkspace = true
            });

            Workspace = new WorkspaceViewModel(
                _owned.Track(new ManuscriptService(Notifications)),
                new FakeBookTxtStructureService(),
                new FakeFilePickerService(),
                SettingsStore,
                FolderPicker,
                Notifications,
                Editor,
                new ChapterRegistryService(MetadataStore),
                new PhaseDataService(MetadataStore),
                new TargetsService(MetadataStore),
                WordCountService,
                new WordCountHistoryService(MetadataStore));

            ApplyWorkspace(WorkspaceRoot, ManuscriptRoot);
        }

        public GitPanelViewModel CreateGitPanel()
            => _owned.Track(new GitPanelViewModel(Workspace, Editor, GitService, Notifications));

        public void EnableWorkspace()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-git-panel-tests", Guid.NewGuid().ToString("N"));
            ManuscriptRoot = Path.Combine(WorkspaceRoot, "manuscript");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(ManuscriptRoot);
            ApplyWorkspace(WorkspaceRoot, ManuscriptRoot);
        }

        public void SwitchWorkspace()
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "hymnal-git-panel-tests", Guid.NewGuid().ToString("N"));
            ManuscriptRoot = Path.Combine(WorkspaceRoot, "manuscript");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(ManuscriptRoot);
            ApplyWorkspace(WorkspaceRoot, ManuscriptRoot);
        }

        public string CreateWorkspaceFile(string relativePath, string content)
        {
            var path = Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void ClearWorkspace()
        {
            var modelField = typeof(WorkspaceViewModel).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Workspace model field not found.");
            modelField.SetValue(Workspace, null);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), false);
            SetPrivateProperty<string?>(Workspace, nameof(WorkspaceViewModel.WorkspaceName), null);
            RaiseWorkspaceChanged(Workspace);
        }

        private void ApplyWorkspace(string workspaceRoot, string manuscriptRoot)
        {
            var model = new ManuscriptModel();
            model.SetRoots(workspaceRoot, manuscriptRoot);
            SetPrivateField(Workspace, "_model", model);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.HasWorkspace), true);
            SetPrivateProperty(Workspace, nameof(WorkspaceViewModel.WorkspaceName), Path.GetFileName(workspaceRoot));
            RaiseWorkspaceChanged(Workspace);
        }

        private static void RaiseWorkspaceChanged(WorkspaceViewModel workspace)
        {
            var field = typeof(WorkspaceViewModel).GetField("_workspaceChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Workspace change subject not found.");

            var subject = (IObserver<CoreUnit>)field.GetValue(workspace)!;
            subject.OnNext(CoreUnit.Default);
        }

        public void Dispose()
        {
            try
            {
                _owned.Dispose();
            }
            finally
            {
                DeleteTempFolders();
            }
        }

        private void DeleteTempFolders()
        {
            try
            {
                if (Directory.Exists(WorkspaceRoot))
                    Directory.Delete(WorkspaceRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class RecordingGitService : NoOpGitService
    {
        private readonly Queue<Result<GitRepositoryStatus>> _statusResults = new();
        private readonly Queue<Result<GitCommandResult>> _syncResults = new();
        private readonly Queue<Result<GitCommandResult>> _pullResults = new();
        private readonly Queue<Result<GitCommandResult>> _pushResults = new();

        public int StatusCalls { get; private set; }
        public List<string> SyncMessages { get; } = new();

        public void EnqueueStatus(Result<GitRepositoryStatus> result) => _statusResults.Enqueue(result);
        public void EnqueueSync(Result<GitCommandResult> result) => _syncResults.Enqueue(result);
        public void EnqueuePull(Result<GitCommandResult> result) => _pullResults.Enqueue(result);
        public void EnqueuePush(Result<GitCommandResult> result) => _pushResults.Enqueue(result);

        public override Task<Result<GitRepositoryStatus>> GetRepositoryStatusAsync(
            string workspaceRoot,
            bool includeRemoteState = false,
            CancellationToken cancellationToken = default)
        {
            StatusCalls++;

            if (_statusResults.Count == 0)
                throw new InvalidOperationException("No Git status result was queued.");

            return Task.FromResult(_statusResults.Dequeue());
        }

        public override Task<Result<GitCommandResult>> StageAllCommitPullAndPushAsync(
            string workspaceRoot,
            string commitMessage,
            CancellationToken cancellationToken = default)
        {
            SyncMessages.Add(commitMessage);
            if (_syncResults.Count == 0)
                throw new InvalidOperationException("No Git sync result was queued.");

            return Task.FromResult(_syncResults.Dequeue());
        }

        public override Task<Result<GitCommandResult>> FetchAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<GitCommandResult>.Ok(new GitCommandResult("git", new[] { "-C", workspaceRoot, "fetch", "--quiet" }, workspaceRoot, 0, string.Empty, string.Empty)));

        public override Task<Result<GitCommandResult>> PullAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            if (_pullResults.Count == 0)
                throw new InvalidOperationException("No Git pull result was queued.");

            return Task.FromResult(_pullResults.Dequeue());
        }

        public override Task<Result<GitCommandResult>> PushAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            if (_pushResults.Count == 0)
                throw new InvalidOperationException("No Git push result was queued.");

            return Task.FromResult(_pushResults.Dequeue());
        }
    }

    private sealed class RecordingMetadataStore : IMetadataStore
    {
        public Task WriteTextAtomicAsync(string absolutePath, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            return File.WriteAllTextAsync(absolutePath, content);
        }
    }

    private sealed class FakeAppSettingsStore : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key)
            => Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFolderPickerService : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public Task<string?> PickFileAsync(string? suggestedStartDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class FakeBookTxtStructureService : IBookTxtStructureService
    {
        public Task<Result<IReadOnlyList<string>>> ReadNormalizedEntriesAsync(string bookTxtPath)
            => Task.FromResult(Result<IReadOnlyList<string>>.Ok(Array.Empty<string>()));

        public Task<Result<CoreUnit>> ReorderEntryAsync(string bookTxtPath, string chapterPath, int newIndex)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> RenameEntryAsync(string bookTxtPath, string existingPath, string replacementPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> AddExistingEntryAsync(string bookTxtPath, string chapterPath, int index)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> AddExistingEntryAfterPartAsync(string bookTxtPath, string chapterPath, string partPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> CreateNewChapterAsync(string bookTxtPath, string chapterPath, string content, int index)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> CreateNewPartAsync(string bookTxtPath, string partPath, string title, int index)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> RemoveEntryAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));

        public Task<Result<CoreUnit>> DeleteChapterFileAsync(string bookTxtPath, string chapterPath)
            => Task.FromResult(Result<CoreUnit>.Ok(CoreUnit.Default));
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public List<string> Successes { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) => Infos.Add(message);
        public void ShowSuccess(string message) => Successes.Add(message);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = typeof(WorkspaceViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found.");
        field.SetValue(target, value);
    }

    private static void SetPrivateProperty<T>(object target, string propertyName, T value)
    {
        var property = typeof(WorkspaceViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found.");
        property.SetValue(target, value);
    }
}

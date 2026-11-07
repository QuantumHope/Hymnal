using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.ViewModels;

/// <summary>
/// The chapter detail dialog's debounced schedule save runs fire-and-forget off the UI thread, so it must
/// never throw: not when the dialog closes during the "Saved" delay, and not when the save fails.
/// </summary>
[Collection("AvaloniaUi")]
public sealed class ScheduleAutoSaveTests
{
    static ScheduleAutoSaveTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Disposing_during_the_saved_delay_ends_the_save_quietly()
    {
        var root = CreateWorkspace(StoryJson, ("scene.md", "# Scene\n"));
        try
        {
            var notes = new RecordingNotificationService();
            await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
            {
                var (chapter, detail) = CreateDetail(root, notes);
                using var owned = chapter;
                var save = AutoSave(detail);
                await PumpUntilAsync(() => detail.ScheduleSavedRecently);

                detail.Dispose();
                await Within(save);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.True(save.IsCompletedSuccessfully);
                // The delay was cancelled, so the hint was never cleared.
                Assert.True(detail.ScheduleSavedRecently);
                Assert.Empty(notes.Errors);
                return 0;
            }, TimeSpan.FromSeconds(40));
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task A_failed_save_is_reported_not_thrown()
    {
        var root = CreateWorkspace(StoryJson, ("scene.md", "# Scene\n"));
        File.WriteAllText(Path.Combine(root, ".hymnal-data", "phases.json"), "{ \"schemaVersion\": 99 }");
        try
        {
            var notes = new RecordingNotificationService();
            await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
            {
                var (chapter, detail) = CreateDetail(root, notes);
                using var owned = chapter;
                using var ownedDetail = detail;
                var save = AutoSave(detail);

                await Within(save);
                await PumpUntilAsync(() => notes.Errors.Count > 0);

                Assert.True(save.IsCompletedSuccessfully);
                Assert.StartsWith("Schedule save failed: ", Assert.Single(notes.Errors));
                Assert.False(detail.ScheduleSavedRecently);
                return 0;
            }, TimeSpan.FromSeconds(40));
        }
        finally { DeleteFolder(root); }
    }

    /// <summary>Runs the debounced save body directly, as the throttle subscriber does.</summary>
    private static Task AutoSave(ChapterDetailViewModel detail)
    {
        var method = typeof(ChapterDetailViewModel).GetMethod(
            "AutoSaveScheduleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(detail, Array.Empty<object>())!;
    }

    private static (ChapterViewModel Chapter, ChapterDetailViewModel Detail) CreateDetail(string root, INotificationService notes)
    {
        var store = new MetadataStore();
        var phases = new PhaseDataService(store);
        var chapter = new ChapterViewModel(
            new ChapterNode("scene.md", "scene.md", "Scene", NodeKind.Chapter, IsMissing: false, Index: 0),
            "uuid-1",
            null,
            phases,
            new TargetsService(store),
            new InMemorySettings(),
            notes,
            root,
            target: null,
            profile: ProjectProfile.For(new ProjectSettings(ProjectKind.Story)));
        return (chapter, new ChapterDetailViewModel(chapter, phases, root, notes));
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<string> Errors { get; } = new();

        public void ShowError(string message) => Errors.Add(message);
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message) { }
    }

    private sealed class InMemorySettings : IAppSettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<T?> GetAsync<T>(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed ? typed : default);

        public Task SetAsync<T>(string key, T value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}

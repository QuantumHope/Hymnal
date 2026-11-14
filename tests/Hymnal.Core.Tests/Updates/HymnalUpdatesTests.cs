using System.Text.Json;
using System.Text.RegularExpressions;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Infrastructure.Updates;
using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates.ReactiveUI;

namespace Hymnal.Core.Tests.Updates;

/// <summary>
/// Hymnal's wiring of the shared update libraries: where it updates from, where it keeps update files, how the shared
/// view model talks to Hymnal's toasts and settings, and what happens after an install. The libraries themselves are
/// tested in MattEland.HopeLab.Shared.
/// </summary>
public sealed class HymnalUpdatesTests
{
    static HymnalUpdatesTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    // ── Options ──────────────────────────────────────────────────────────────

    [Fact]
    public void Options_point_at_Hymnal_releases_and_a_home_relative_data_folder()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var options = HymnalUpdates.CreateOptions(HymnalUpdates.DefaultDataDirectory(home));

        Assert.Equal("Hymnal", options.AppName);
        Assert.Equal("QuantumHope/Hymnal", options.Repository);
        Assert.Equal("Hymnal", options.DesktopExecutable);
        Assert.Equal(Path.Combine(home, ".hymnal"), options.DataDirectory);
        Assert.Equal(Path.Combine(home, ".hymnal", "github-token"), options.TokenFile);
        Assert.Equal(Path.Combine(home, ".hymnal", "updates"), options.UpdatesDirectory);
        Assert.Equal("HYMNAL_GITHUB_TOKEN", options.TokenVariable);
        Assert.Matches("^\\{[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\\}$", options.WindowsSetupAppId);
        Assert.Null(options.StopServices); // Hymnal runs nothing beside itself
    }

    [Fact]
    public void Default_data_folder_is_under_the_home_folder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".hymnal"), HymnalUpdates.CreateOptions().DataDirectory);
    }

    [Fact]
    public void Options_match_hopelab_app_json_which_the_release_workflow_and_installers_read()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(RepositoryFile("hopelab-app.json")));
        var root = config.RootElement;
        var options = HymnalUpdates.CreateOptions();

        Assert.Equal(options.AppName, root.GetProperty("name").GetString());
        Assert.Equal("hymnal", root.GetProperty("id").GetString());
        Assert.Equal(options.DesktopExecutable, root.GetProperty("desktop").GetProperty("executable").GetString());
        Assert.Equal(options.WindowsSetupAppId, root.GetProperty("windows").GetProperty("setupAppId").GetString());
        var install = root.GetProperty("install");
        Assert.Equal(HymnalUpdates.DataFolderName, install.GetProperty("dataDirectory").GetString());
        Assert.Equal(options.TokenVariable, install.GetProperty("tokenVariable").GetString());
        Assert.Contains(options.DesktopExecutable, install.GetProperty("processes").EnumerateArray().Select(p => p.GetString()));

        // The files it names exist, and the project builds an executable of that name.
        var desktop = root.GetProperty("desktop");
        var project = desktop.GetProperty("project").GetString()!;
        var csproj = RepositoryFile(Path.Combine(project, Path.GetFileName(project) + ".csproj"));
        Assert.Equal(options.DesktopExecutable, Path.GetFileNameWithoutExtension(csproj));
        Assert.DoesNotContain("<AssemblyName>", File.ReadAllText(csproj));
        Assert.True(File.Exists(RepositoryFile(desktop.GetProperty("icon").GetString()!)));
        Assert.True(File.Exists(RepositoryFile(desktop.GetProperty("png").GetString()!)));
    }

    [Fact]
    public void Version_is_in_Directory_Build_props_for_the_release_workflow()
    {
        var props = File.ReadAllText(RepositoryFile("Directory.Build.props"));
        Assert.Matches(new Regex(@"<Version>\d+\.\d+\.\d+</Version>"), props);
    }

    // ── The view model Hymnal creates ────────────────────────────────────────

    [Fact]
    public void View_model_warns_about_unsaved_changes_only_while_there_are_some()
    {
        var dirty = false;
        using var vm = HymnalUpdates.CreateViewModel(new FakeUpdateService(), new InMemoryUpdateHost(), () => dirty);

        Assert.Equal("Hymnal", vm.AppName);
        Assert.Null(vm.RestartWarning!());
        dirty = true;
        Assert.Equal(HymnalUpdates.UnsavedChangesWarning, vm.RestartWarning!());
        Assert.Contains("Settings → Updates", vm.InstallHint);
    }

    [Fact]
    public async Task Banner_names_the_new_version_and_says_unsaved_changes_are_saved_first()
    {
        var service = new FakeUpdateService { Offer = FakeUpdateService.SampleOffer() };
        using var vm = HymnalUpdates.CreateViewModel(service, new InMemoryUpdateHost(), () => true);

        await vm.CheckAsync(userAsked: false);

        Assert.True(vm.ShowBanner);
        Assert.Equal("Hymnal 0.1.42 is available", vm.BannerTitle);
        Assert.EndsWith(HymnalUpdates.UnsavedChangesWarning, vm.BannerBody);
    }

    // ── IUpdateHost over Hymnal's settings and toasts ────────────────────────

    [Fact]
    public async Task Host_preferences_round_trip_through_the_settings_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HymnalTests_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            using (var store = new AppSettingsStore(path))
            {
                var host = new HymnalUpdateHost(new RecordingNotificationService(), store, _ => Task.CompletedTask);
                Assert.Null(host.GetPreference<bool?>(UpdatesViewModel.AutoCheckKey));
                Assert.Null(host.GetPreference<string>(UpdatesViewModel.DismissedKey));

                await host.SetPreferenceAsync(UpdatesViewModel.AutoCheckKey, false);
                await host.SetPreferenceAsync(UpdatesViewModel.DismissedKey, "0.1.42");
                await store.FlushPendingAsync();
            }

            using var reopened = new AppSettingsStore(path);
            var again = new HymnalUpdateHost(new RecordingNotificationService(), reopened, _ => Task.CompletedTask);
            Assert.False(again.GetPreference<bool?>(UpdatesViewModel.AutoCheckKey));
            Assert.Equal("0.1.42", again.GetPreference<string>(UpdatesViewModel.DismissedKey));

            // The shared view model reads them through the host.
            using var vm = HymnalUpdates.CreateViewModel(new FakeUpdateService(), again, () => false);
            Assert.False(vm.AutoCheck);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(UpdateToastKind.Information, "info")]
    [InlineData(UpdateToastKind.Success, "success")]
    [InlineData(UpdateToastKind.Warning, "error")]
    [InlineData(UpdateToastKind.Error, "error")]
    public void Host_toasts_become_Hymnal_notifications(UpdateToastKind kind, string expected)
    {
        var notifications = new RecordingNotificationService();
        var host = new HymnalUpdateHost(notifications, new AppSettingsStore(Path.Combine(Path.GetTempPath(), "unused.json")), _ => Task.CompletedTask);

        host.Toast("Hymnal 0.1.42 is available", "Install it from the banner.", kind);
        host.Toast("Token removed", "", kind);

        Assert.Equal([(expected, "Hymnal 0.1.42 is available. Install it from the banner."), (expected, "Token removed")], notifications.Shown);
    }

    [Fact]
    public async Task Host_reports_a_browser_that_would_not_open()
    {
        var notifications = new RecordingNotificationService();
        var host = new HymnalUpdateHost(notifications, new AppSettingsStore(Path.Combine(Path.GetTempPath(), "unused.json")),
            _ => throw new InvalidOperationException("no browser"));

        await host.OpenUrlAsync("https://github.com/QuantumHope/Hymnal/releases");

        var (kind, message) = Assert.Single(notifications.Shown);
        Assert.Equal("error", kind);
        Assert.Contains("no browser", message);
    }

    // ── After an install ─────────────────────────────────────────────────────

    [Fact]
    public async Task Restart_saves_then_starts_the_new_copy_then_exits()
    {
        var steps = new List<string>();
        var service = new FakeUpdateService();
        var restart = new UpdateRestart(service,
            () => { steps.Add("save"); return Task.FromResult(true); },
            () => steps.Add("exit"),
            new RecordingNotificationService());

        Assert.True(await restart.RestartAsync("/apps/Hymnal/Hymnal"));

        Assert.Equal(["/apps/Hymnal/Hymnal"], service.Launched);
        Assert.Equal(["save", "exit"], steps);
    }

    [Fact]
    public async Task Restart_does_not_happen_when_work_could_not_be_saved()
    {
        foreach (Func<Task<bool>> save in new Func<Task<bool>>[]
                 {
                     () => Task.FromResult(false),
                     () => throw new IOException("disk full"),
                 })
        {
            var service = new FakeUpdateService();
            var notifications = new RecordingNotificationService();
            var exited = false;
            var restart = new UpdateRestart(service, save, () => exited = true, notifications);

            Assert.False(await restart.RestartAsync("Hymnal"));

            Assert.Empty(service.Launched);
            Assert.False(exited);
            var (kind, message) = Assert.Single(notifications.Shown);
            Assert.Equal("error", kind);
            Assert.Contains("couldn't save your changes", message);
        }
    }

    [Fact]
    public async Task Restart_keeps_running_when_the_new_copy_would_not_start()
    {
        var service = new FakeUpdateService { LaunchResult = Result<MattEland.HopeLab.Common.Unit>.Fail("Couldn't start Hymnal.") };
        var notifications = new RecordingNotificationService();
        var exited = false;
        var restart = new UpdateRestart(service, () => Task.FromResult(true), () => exited = true, notifications);

        Assert.False(await restart.RestartAsync("Hymnal"));

        Assert.False(exited);
        Assert.Contains("Couldn't start Hymnal.", Assert.Single(notifications.Shown).Message);
    }

    internal static string RepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate) && File.Exists(Path.Combine(current.FullName, "Hymnal.slnx")))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}

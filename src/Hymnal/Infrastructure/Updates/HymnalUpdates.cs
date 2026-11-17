using System;
using System.IO;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates;
using MattEland.HopeLab.Updates.ReactiveUI;

namespace Hymnal.Infrastructure.Updates;

/// <summary>
/// Hymnal's side of the shared release updates (MattEland.HopeLab.Updates): where its releases come from, where it
/// keeps update downloads and a saved GitHub token, and the wording of the update banner and Settings → Updates. The same
/// values are in <c>hopelab-app.json</c>, which the release workflow and the installers read.
/// </summary>
public static class HymnalUpdates
{
    public const string AppName = "Hymnal";
    public const string Repository = "QuantumHope/Hymnal";

    /// <summary>The desktop executable (the app project's assembly name), without <c>.exe</c>.</summary>
    public const string Executable = "Hymnal";

    /// <summary>
    /// The per-user folder for update downloads (<c>updates/</c>), under the home folder. (A saved GitHub token goes in
    /// Hymnal's credential store instead.) Hymnal's settings stay in the OS app-data folder (<c>AppSettingsStore</c>); the installers need a
    /// folder relative to home, so updates get their own.
    /// </summary>
    public const string DataFolderName = ".hymnal";

    /// <summary>The Windows setup's Inno Setup AppId. Never change it: Apps &amp; features and upgrades key on it.</summary>
    public const string WindowsSetupAppId = "{AADCC81E-C17B-4436-A68E-890A41335EB4}";

    public const string TokenVariable = "HYMNAL_GITHUB_TOKEN";

    /// <summary>Warning shown on the banner while the editor has unsaved changes.</summary>
    public const string UnsavedChangesWarning = "You have unsaved changes; they'll be saved first.";

    public static string DefaultDataDirectory(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DataFolderName);

    /// <param name="credentials">
    /// Hymnal's secret storage. A GitHub token saved in Settings → Updates is kept there (<see cref="CredentialTokenStore"/>),
    /// never in the shared library's plain <c>github-token</c> file.
    /// </param>
    public static AppUpdateOptions CreateOptions(ICredentialStore credentials, string? dataDirectory = null) =>
        new(AppName, Repository, dataDirectory ?? DefaultDataDirectory(), Executable)
        {
            WindowsSetupAppId = WindowsSetupAppId,
            TokenVariable = TokenVariable,
            TokenStore = new CredentialTokenStore(credentials),
        };

    /// <summary>The real update service: checks GitHub and installs over the running copy's own folder.</summary>
    public static IUpdateService CreateService(ICredentialStore credentials) =>
        new AppUpdateService(new AppUpdater(CreateOptions(credentials), SystemClock.Instance));

    /// <param name="hasUnsavedChanges">True while restarting would need to save first (the editor is dirty).</param>
    public static UpdatesViewModel CreateViewModel(IUpdateService? service, IUpdateHost host, Func<bool> hasUnsavedChanges) =>
        new(service, host, AppName)
        {
            InstallHint = "Install it from the banner, or in Settings → Updates.",
            AccessHint = "Check your connection, or add a GitHub token in Settings → Updates.",
            RestartWarning = () => hasUnsavedChanges() ? UnsavedChangesWarning : null,
        };
}

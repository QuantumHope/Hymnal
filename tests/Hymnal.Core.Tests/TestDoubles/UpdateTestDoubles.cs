using System.Collections.Generic;
using System.Threading;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates;
using MattEland.HopeLab.Updates.ReactiveUI;

namespace Hymnal.Core.Tests.TestDoubles;

/// <summary>An update service that never touches GitHub or the install folder.</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public string CurrentVersion { get; set; } = "0.1.0";
    public string? NotUpdatableReason { get; set; }
    public bool AsksForPassword { get; set; }
    public string ReleasesUrl => "https://github.com/QuantumHope/Hymnal/releases";
    public string AccessSource { get; set; } = "none";

    /// <summary>What <see cref="CheckAsync"/> returns; null means up to date.</summary>
    public UpdateOffer? Offer { get; set; }

    public Result<Unit> LaunchResult { get; set; } = Result<Unit>.Ok(Unit.Value);
    public List<string> Launched { get; } = [];

    public Task<Result<UpdateCheck>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<UpdateCheck>.Ok(new UpdateCheck(
            Offer?.Version ?? new ReleaseVersion(0, 1, 0), new ReleaseVersion(0, 1, 0), Offer, DateTimeOffset.UtcNow)));

    public Task<Result<UpdateInstallResult>> InstallAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Result<UpdateInstallResult>.Ok(new UpdateInstallResult(offer.Version, 1, "Hymnal.exe", false, null)));

    public Result<Unit> Launch(string executable)
    {
        Launched.Add(executable);
        return LaunchResult;
    }

    public Task<Result<string>> SaveTokenAsync(string token, CancellationToken ct = default) => Task.FromResult(Result<string>.Ok("Saved."));
    public Result<Unit> ClearToken() => Result<Unit>.Ok(Unit.Value);

    /// <summary>A release a little newer than <see cref="CurrentVersion"/>.</summary>
    public static UpdateOffer SampleOffer()
    {
        const string name = "Hymnal-0.1.42-win-x64.zip";
        return new UpdateOffer(new ReleaseVersion(0, 1, 42), new ReleaseVersion(0, 1, 0), "v0.1.42",
            "https://github.com/QuantumHope/Hymnal/releases/tag/v0.1.42", null, DateTimeOffset.UtcNow, "win-x64",
            new ReleaseAsset(name, new string('0', 64), 92_400_000),
            new GitHubReleaseAsset(1, name, 92_400_000, "https://api.github.com/x", "https://github.com/x"));
    }
}

/// <summary>Records what Hymnal's notification toasts were asked to show.</summary>
internal sealed class RecordingNotificationService : INotificationService
{
    public List<(string Kind, string Message)> Shown { get; } = [];
    public void ShowError(string message) => Shown.Add(("error", message));
    public void ShowInfo(string message) => Shown.Add(("info", message));
    public void ShowSuccess(string message) => Shown.Add(("success", message));
}

/// <summary>An update host with preferences in memory.</summary>
internal sealed class InMemoryUpdateHost : IUpdateHost
{
    public Dictionary<string, object?> Preferences { get; } = new();
    public List<string> Toasts { get; } = [];
    public List<string> OpenedUrls { get; } = [];

    public void Toast(string title, string message, UpdateToastKind kind) => Toasts.Add(title);

    public Task OpenUrlAsync(string url)
    {
        OpenedUrls.Add(url);
        return Task.CompletedTask;
    }

    public T? GetPreference<T>(string key) => Preferences.TryGetValue(key, out var v) && v is T t ? t : default;

    public Task SetPreferenceAsync<T>(string key, T value)
    {
        Preferences[key] = value;
        return Task.CompletedTask;
    }
}

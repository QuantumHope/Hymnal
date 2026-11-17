using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Updates.ReactiveUI;

namespace Hymnal.Infrastructure.Updates;

/// <summary>
/// What the shared update view model needs from Hymnal: its notification toasts, a browser, and the app settings
/// store (<c>settings.json</c>) for the two update preferences. The view model reads its preferences synchronously in
/// its constructor, so <see cref="CreateAsync"/> loads them from the async store first; nothing ever waits on the store.
/// A missing or unreadable setting reads as no preference.
/// </summary>
public sealed class HymnalUpdateHost : IUpdateHost
{
    private readonly INotificationService _notifications;
    private readonly IAppSettingsStore _settings;
    private readonly Func<string, Task> _openUrl;
    private readonly Dictionary<string, object?> _preferences;

    private HymnalUpdateHost(INotificationService notifications, IAppSettingsStore settings, Func<string, Task> openUrl,
        Dictionary<string, object?> preferences)
    {
        _notifications = notifications;
        _settings = settings;
        _openUrl = openUrl;
        _preferences = preferences;
    }

    /// <summary>A host with the update preferences loaded from <paramref name="settings"/>, without blocking any thread.</summary>
    public static async Task<HymnalUpdateHost> CreateAsync(INotificationService notifications, IAppSettingsStore settings,
        Func<string, Task> openUrl)
    {
        var preferences = new Dictionary<string, object?>(StringComparer.Ordinal);
        await Load<bool?>(UpdatesViewModel.AutoCheckKey);
        await Load<string>(UpdatesViewModel.DismissedKey);
        return new HymnalUpdateHost(notifications, settings, openUrl, preferences);

        async Task Load<T>(string key)
        {
            try
            {
                if (await settings.GetAsync<T>(key) is { } value)
                    preferences[key] = value;
            }
            catch (Exception)
            {
                // Unreadable: no preference, so the view model's default applies.
            }
        }
    }

    /// <summary>
    /// Hymnal's toasts have a kind and one message: the title leads it. Warnings show as errors, the closest kind
    /// Hymnal has (they say something didn't work).
    /// </summary>
    public void Toast(string title, string message, UpdateToastKind kind)
    {
        var text = string.IsNullOrWhiteSpace(message) ? title : $"{title}. {message}";
        switch (kind)
        {
            case UpdateToastKind.Success:
                _notifications.ShowSuccess(text);
                break;
            case UpdateToastKind.Warning:
            case UpdateToastKind.Error:
                _notifications.ShowError(text);
                break;
            default:
                _notifications.ShowInfo(text);
                break;
        }
    }

    public async Task OpenUrlAsync(string url)
    {
        try
        {
            await _openUrl(url);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Couldn't open {url}: {ex.Message}");
        }
    }

    /// <summary>The value loaded by <see cref="CreateAsync"/> or set since; never reads the store.</summary>
    public T? GetPreference<T>(string key)
    {
        lock (_preferences)
            return _preferences.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    public async Task SetPreferenceAsync<T>(string key, T value)
    {
        lock (_preferences)
            _preferences[key] = value;
        try
        {
            await _settings.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Couldn't save the update preference: {ex.Message}");
        }
    }
}

using System;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Updates.ReactiveUI;

namespace Hymnal.Infrastructure.Updates;

/// <summary>
/// What the shared update view model needs from Hymnal: its notification toasts, a browser, and the app settings
/// store (<c>settings.json</c>) for the two update preferences. A missing or unreadable setting reads as no preference.
/// </summary>
public sealed class HymnalUpdateHost : IUpdateHost
{
    private readonly INotificationService _notifications;
    private readonly IAppSettingsStore _settings;
    private readonly Func<string, Task> _openUrl;

    public HymnalUpdateHost(INotificationService notifications, IAppSettingsStore settings, Func<string, Task> openUrl)
    {
        _notifications = notifications;
        _settings = settings;
        _openUrl = openUrl;
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

    /// <summary>
    /// Read once, when the view model is created. The store is async (and reads with ConfigureAwait(false)), so it is
    /// awaited on the thread pool rather than on the caller's thread.
    /// </summary>
    public T? GetPreference<T>(string key)
    {
        try
        {
            return Task.Run(() => _settings.GetAsync<T>(key)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return default;
        }
    }

    public async Task SetPreferenceAsync<T>(string key, T value)
    {
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

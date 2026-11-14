using System;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Updates;

namespace Hymnal.Infrastructure.Updates;

/// <summary>
/// What Hymnal does once an update is installed (<c>UpdatesViewModel.RestartRequested</c>): save everything, start the
/// new copy, then exit the way File → Exit does. It never restarts over work it couldn't save: if saving fails, the
/// new version is installed but this copy keeps running and says so.
/// </summary>
public sealed class UpdateRestart
{
    private readonly IUpdateService _updates;
    private readonly Func<Task<bool>> _saveAll;
    private readonly Action _shutdown;
    private readonly INotificationService _notifications;

    /// <param name="saveAll">Saves the editor and flushes pending notes, schedule, idea and beat edits; false if anything couldn't be saved.</param>
    /// <param name="shutdown">Exits Hymnal normally.</param>
    public UpdateRestart(IUpdateService updates, Func<Task<bool>> saveAll, Action shutdown, INotificationService notifications)
    {
        _updates = updates;
        _saveAll = saveAll;
        _shutdown = shutdown;
        _notifications = notifications;
    }

    /// <summary>True when the new copy was started and this one is shutting down.</summary>
    public async Task<bool> RestartAsync(string executable)
    {
        bool saved;
        try
        {
            saved = await _saveAll();
        }
        catch (Exception)
        {
            saved = false;
        }

        if (!saved)
        {
            _notifications.ShowError(
                "The update is installed, but Hymnal couldn't save your changes, so it didn't restart. " +
                "Save your work, then quit and open Hymnal again.");
            return false;
        }

        var launched = _updates.Launch(executable);
        if (!launched.IsSuccess)
        {
            _notifications.ShowError($"The update is installed, but Hymnal couldn't restart. {launched.Error} Quit and open Hymnal again.");
            return false;
        }

        _shutdown();
        return true;
    }
}

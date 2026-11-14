using System;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Updates;

namespace Hymnal.Infrastructure.Updates;

/// <summary>
/// What Hymnal does once an update is installed (<c>UpdatesViewModel.RestartRequested</c>): save everything with
/// editing locked, start the new copy, then exit the way File → Exit does. It never restarts over work it couldn't
/// save: if saving fails, the new version is installed but this copy keeps running, editing comes back, and it says why.
/// </summary>
public sealed class UpdateRestart
{
    /// <summary>How many save rounds may run before giving up because the work keeps changing.</summary>
    public const int SaveRounds = 3;

    public const string CouldNotSave = "Hymnal couldn't save your changes";
    public const string KeptChanging = "your work was still changing while Hymnal saved it";

    private readonly IUpdateService _updates;
    private readonly Func<Task<string?>> _saveAll;
    private readonly Action _shutdown;
    private readonly INotificationService _notifications;
    private readonly Action? _cancelRestart;

    /// <param name="saveAll">
    /// Locks editing and saves the editor, notes, schedule, idea and beat edits until nothing is pending. Null when
    /// everything is saved (editing stays locked: the app is about to exit), otherwise why not.
    /// </param>
    /// <param name="shutdown">Exits Hymnal normally.</param>
    /// <param name="cancelRestart">Gives editing back when the restart doesn't happen.</param>
    public UpdateRestart(IUpdateService updates, Func<Task<string?>> saveAll, Action shutdown,
        INotificationService notifications, Action? cancelRestart = null)
    {
        _updates = updates;
        _saveAll = saveAll;
        _shutdown = shutdown;
        _notifications = notifications;
        _cancelRestart = cancelRestart;
    }

    /// <summary>True when the new copy was started and this one is shutting down.</summary>
    public async Task<bool> RestartAsync(string executable)
    {
        string? problem;
        try
        {
            problem = await _saveAll();
        }
        catch (Exception)
        {
            problem = CouldNotSave;
        }

        if (problem is not null)
        {
            _cancelRestart?.Invoke();
            _notifications.ShowError(
                $"The update is installed, but {problem}, so Hymnal didn't restart. " +
                "Save your work, then quit and open Hymnal again.");
            return false;
        }

        var launched = _updates.Launch(executable);
        if (!launched.IsSuccess)
        {
            _cancelRestart?.Invoke();
            _notifications.ShowError($"The update is installed, but Hymnal couldn't restart. {launched.Error} Quit and open Hymnal again.");
            return false;
        }

        _shutdown();
        return true;
    }

    /// <summary>
    /// Runs <paramref name="saveRound"/> until <paramref name="hasUnsavedWork"/> says nothing is dirty or pending, so an
    /// edit made while an earlier save was still being awaited is saved too. Null when everything is saved;
    /// <see cref="CouldNotSave"/> when a round failed; <see cref="KeptChanging"/> after <paramref name="rounds"/> rounds.
    /// </summary>
    public static async Task<string?> SaveUntilCleanAsync(Func<Task<bool>> saveRound, Func<bool> hasUnsavedWork, int rounds = SaveRounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            if (!await saveRound())
                return CouldNotSave;
            if (!hasUnsavedWork())
                return null;
        }

        return KeptChanging;
    }
}

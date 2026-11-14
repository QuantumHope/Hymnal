using System;
using System.Collections.Generic;
using Hymnal.Core.Common;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Pure helper for marking the current authoring phase complete and advancing status.
/// </summary>
public static class PhaseCompletion
{
    public static ChapterStatus? GetNextStatus(ChapterStatus current) =>
        current switch
        {
            ChapterStatus.Planned => ChapterStatus.Outlining,
            ChapterStatus.Outlining => ChapterStatus.Drafting,
            ChapterStatus.Drafting => ChapterStatus.Editing,
            ChapterStatus.Editing => ChapterStatus.Polishing,
            ChapterStatus.Polishing => ChapterStatus.Reviewing,
            ChapterStatus.Reviewing => ChapterStatus.Done,
            ChapterStatus.Done => null,
            _ => null
        };

    public static bool IsTrackablePhase(ChapterStatus status) =>
        status is ChapterStatus.Outlining
            or ChapterStatus.Drafting
            or ChapterStatus.Editing
            or ChapterStatus.Polishing
            or ChapterStatus.Reviewing;

    /// <summary>
    /// Marks the current phase 100% complete (with dates), then advances to the next status.
    /// Does not prefill start dates on the newly active phase.
    /// </summary>
    public static Result<PhaseData> CompleteAndAdvance(PhaseData current, DateOnly today) =>
        CompleteAndAdvance(current, today, ProjectProfile.Novel);

    /// <summary>Profile-aware variant: status sequence and trackable phases come from <paramref name="profile"/>.</summary>
    public static Result<PhaseData> CompleteAndAdvance(PhaseData current, DateOnly today, ProjectProfile profile)
    {
        if (profile.DisplayStatus(current.Status) == ChapterStatus.Done)
            return Result<PhaseData>.Fail("It is already done.");

        var nextStatus = profile.NextStatus(current.Status);
        if (nextStatus is null)
            return Result<PhaseData>.Fail("It is already done.");

        var todayStr = today.ToString("yyyy-MM-dd");
        var schedule = new Dictionary<string, PhaseScheduleEntry>(
            current.Schedule ?? new Dictionary<string, PhaseScheduleEntry>(),
            StringComparer.Ordinal);

        string? phaseStart = current.PhaseStartDate;
        string? phaseEnd = current.PhaseEndDate;

        if (profile.IsTrackablePhase(current.Status))
        {
            var phaseName = current.Status.ToString();
            schedule.TryGetValue(phaseName, out var existing);

            var startDate = existing?.StartDate;
            if (string.IsNullOrWhiteSpace(startDate))
                startDate = current.PhaseStartDate;
            if (string.IsNullOrWhiteSpace(startDate))
                startDate = todayStr;

            schedule[phaseName] = new PhaseScheduleEntry
            {
                StartDate = startDate,
                EndDate = todayStr,
                Progress = 100.0
            };

            phaseStart = startDate;
            phaseEnd = todayStr;
        }

        var newStatus = nextStatus.Value;

        if (newStatus == ChapterStatus.Done)
        {
            phaseEnd = phaseEnd ?? todayStr;
        }
        else if (profile.IsTrackablePhase(newStatus))
        {
            phaseStart = null;
            phaseEnd = null;
        }

        return Result<PhaseData>.Ok(new PhaseData
        {
            Status = newStatus,
            PhaseStartDate = phaseStart,
            PhaseEndDate = phaseEnd,
            Schedule = schedule
        });
    }
}

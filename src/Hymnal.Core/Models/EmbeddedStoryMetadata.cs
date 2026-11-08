using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hymnal.Core.Models;

/// <summary>
/// A single-file story's Hymnal data as stored in the trailing <c>&lt;!-- hymnal:v1 … --&gt;</c> block of
/// the story file. Every field is optional; an absent (null) field means "none".
/// </summary>
/// <param name="Id">The story's identity; replaces the chapter-registry UUID for embedded stories.</param>
/// <param name="Schedule">Per-phase schedule keyed by phase name, as in <see cref="PhaseData.Schedule"/>.</param>
/// <param name="Beats">The story board's beats.</param>
public sealed record EmbeddedStoryMetadata(
    string? Id,
    ChapterStatus? Status,
    DateOnly? PhaseStartDate,
    DateOnly? PhaseEndDate,
    IReadOnlyDictionary<string, PhaseScheduleEntry>? Schedule,
    WordCountTarget? Target,
    string? Notes,
    IReadOnlyList<Beat>? Beats)
{
    private readonly ChapterStatus? _status = Status;

    // The block's status value as raw JSON when this version doesn't know it (Status then reads as none).
    private string? _unknownStatus;

    /// <summary>
    /// The story's status; null for none or for a status this version doesn't know. Changing it drops an unknown
    /// status; setting it to the value it already has (e.g. none again) keeps it, so it is written back.
    /// </summary>
    public ChapterStatus? Status
    {
        get => _status;
        init
        {
            if (value != _status)
                _unknownStatus = null;
            _status = value;
        }
    }

    /// <summary>The unknown status kept from the block read, as raw JSON; null when there is none.</summary>
    internal string? UnknownStatus => _unknownStatus;

    /// <summary>A copy that keeps <paramref name="rawJson"/> as its unknown status (while Status stays none).</summary>
    internal EmbeddedStoryMetadata WithUnknownStatus(string rawJson)
    {
        var copy = this with { };
        copy._unknownStatus = rawJson;
        return copy;
    }

    /// <summary>
    /// Top-level block fields this version doesn't know (e.g. written by a newer Hymnal), kept as they are when
    /// the block is rewritten; null when there are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// The result of splitting a story file into its prose and its trailing metadata block.
/// </summary>
/// <param name="Prose">The file's text without the recognized block and its separator; the whole text when no
/// block was recognized or the block was malformed.</param>
/// <param name="Metadata">The parsed block, or null when there is none or it is malformed.</param>
/// <param name="HadMalformedBlock">True when a block was recognized but its JSON could not be read; the block
/// is then kept in <paramref name="Prose"/>.</param>
/// <param name="LineEnding">The file's line ending ("\n" or "\r\n"), detected from its first line break.</param>
public sealed record StorySplit(string Prose, EmbeddedStoryMetadata? Metadata, bool HadMalformedBlock, string LineEnding);

/// <summary>A story file read: the split file, or a failure with <paramref name="NotUtf8"/> set when the only
/// problem is that the file is not valid UTF-8.</summary>
public sealed record StoryFileRead(Hymnal.Core.Common.Result<StorySplit> Result, bool NotUtf8);

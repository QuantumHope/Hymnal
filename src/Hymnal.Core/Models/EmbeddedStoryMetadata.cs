using System;
using System.Collections.Generic;

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
    IReadOnlyList<Beat>? Beats);

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

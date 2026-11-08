using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Hymnal.Core.Models;

namespace Hymnal.Core.Services;

/// <summary>
/// Reads and writes the trailing Hymnal metadata block of a single-file story:
/// <code>
/// …last paragraph.
///
/// &lt;!-- hymnal:v1
/// { …json… }
/// --&gt;
/// </code>
/// Only the last block whose opening line is exactly <c>&lt;!-- hymnal:v1</c> (ignoring trailing whitespace)
/// and which is followed by nothing but whitespace is recognized. Prose bytes are never changed:
/// <c>Split(Merge(prose, m, le)).Prose == prose</c>.
/// </summary>
public static class StoryFileFormat
{
    private const string OpeningMarker = "<!-- hymnal:v1";
    private const string ClosingMarker = "-->";
    private const char ByteOrderMark = '﻿';

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
        NewLine = "\n",
        // Keeps non-ASCII text readable while still escaping '<' and '>' so a value can never
        // contain "-->" (which would end the HTML comment) or "<!--".
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static StorySplit Split(string text)
    {
        var lineEnding = DetectLineEnding(text);
        var blockStart = FindLastOpeningLine(text, out var bodyStart);
        if (blockStart < 0)
            return new StorySplit(text, null, false, lineEnding);

        var closing = text.IndexOf(ClosingMarker, bodyStart, StringComparison.Ordinal);
        if (closing < 0 || !IsWhitespaceToEnd(text, closing + ClosingMarker.Length))
            return new StorySplit(text, null, false, lineEnding);

        var metadata = TryDeserialize(text[bodyStart..closing]);
        if (metadata is null)
            return new StorySplit(text, null, true, lineEnding);

        return new StorySplit(RemoveSeparator(text[..blockStart], lineEnding), metadata, false, lineEnding);
    }

    public static string Merge(string prose, EmbeddedStoryMetadata? metadata, string lineEnding)
    {
        if (metadata is null)
            return prose;

        var separator = IsEmptyProse(prose) ? "" : lineEnding + lineEnding;
        var json = SerializeMetadata(metadata).Replace("\n", lineEnding, StringComparison.Ordinal);
        return prose + separator + OpeningMarker + lineEnding + json + lineEnding + ClosingMarker + lineEnding;
    }

    /// <summary>Serializes <paramref name="m"/> as indented camelCase JSON with "\n" line breaks.</summary>
    public static string SerializeMetadata(EmbeddedStoryMetadata m) => JsonSerializer.Serialize(m, JsonOptions);

    private static string DetectLineEnding(string text)
    {
        var newline = text.IndexOf('\n');
        return newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>
    /// Returns the index of the last line that is exactly the opening marker (trailing whitespace allowed),
    /// or -1; <paramref name="bodyStart"/> receives the index just past that line's line break.
    /// </summary>
    private static int FindLastOpeningLine(string text, out int bodyStart)
    {
        bodyStart = -1;
        var searchEnd = text.Length - 1;
        while (searchEnd >= 0)
        {
            var index = text.LastIndexOf(OpeningMarker, searchEnd, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            if (IsLineStart(text, index))
            {
                var lineBreak = text.IndexOf('\n', index + OpeningMarker.Length);
                if (lineBreak >= 0 && IsWhitespace(text, index + OpeningMarker.Length, lineBreak))
                {
                    bodyStart = lineBreak + 1;
                    return index;
                }
            }

            searchEnd = index - 1;
        }

        return -1;
    }

    private static bool IsLineStart(string text, int index) =>
        index == 0
        || text[index - 1] == '\n'
        || (index == 1 && text[0] == ByteOrderMark);

    private static bool IsWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }
        return true;
    }

    private static bool IsWhitespaceToEnd(string text, int start) => IsWhitespace(text, start, text.Length);

    private static EmbeddedStoryMetadata? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EmbeddedStoryMetadata>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsEmptyProse(string prose) =>
        prose.Length == 0 || (prose.Length == 1 && prose[0] == ByteOrderMark);

    /// <summary>
    /// Removes the blank-line separator <see cref="Merge"/> writes before the block. Text that was not written
    /// by Merge loses at most two trailing line breaks.
    /// </summary>
    private static string RemoveSeparator(string beforeBlock, string lineEnding)
    {
        if (IsEmptyProse(beforeBlock))
            return beforeBlock;

        var separator = lineEnding + lineEnding;
        if (beforeBlock.EndsWith(separator, StringComparison.Ordinal))
            return beforeBlock[..^separator.Length];

        var prose = beforeBlock;
        for (var i = 0; i < 2; i++)
        {
            if (prose.EndsWith("\r\n", StringComparison.Ordinal))
                prose = prose[..^2];
            else if (prose.EndsWith('\n'))
                prose = prose[..^1];
        }
        return prose;
    }
}

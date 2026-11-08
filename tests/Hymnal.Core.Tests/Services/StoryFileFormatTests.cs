using System.Text.Json;
using Hymnal.Core.Models;
using Hymnal.Core.Services;

namespace Hymnal.Core.Tests.Services;

public class StoryFileFormatTests
{
    private static EmbeddedStoryMetadata FullMetadata() => new(
        Id: "3f2c9a1e-0000-4000-8000-000000000001",
        Status: ChapterStatus.Drafting,
        PhaseStartDate: new DateOnly(2026, 11, 1),
        PhaseEndDate: new DateOnly(2026, 11, 30),
        Schedule: new Dictionary<string, PhaseScheduleEntry>
        {
            ["Drafting"] = new() { StartDate = "2026-11-01", EndDate = "2026-11-15", Progress = 40 }
        },
        Target: new WordCountTarget { Preset = "shortStory", MinWords = 1000, MaxWords = 7500 },
        Notes: "Remember the lighthouse.\nSecond line.",
        Beats: new[]
        {
            new Beat("b1", "Hook", "Open on the storm", "story.md", "scene-uuid"),
            new Beat("b2", "Twist", "", null)
        });

    private static EmbeddedStoryMetadata MinimalMetadata() =>
        new("id-1", ChapterStatus.Editing, null, null, null, null, null, null);

    /// <summary>Compares metadata by its serialized JSON (records holding collections use reference equality).</summary>
    private static void AssertSameMetadata(EmbeddedStoryMetadata expected, EmbeddedStoryMetadata? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(StoryFileFormat.SerializeMetadata(expected), StoryFileFormat.SerializeMetadata(actual!));
    }

    // ── Round-trip ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("# Title\n\nFirst paragraph.\n\nLast paragraph.\n", "\n")]
    [InlineData("# Title\r\n\r\nFirst paragraph.\r\n\r\nLast paragraph.\r\n", "\r\n")]
    [InlineData("# Title\n\nLast paragraph without newline", "\n")]
    [InlineData("# Title\r\n\r\nLast paragraph without newline", "\r\n")]
    [InlineData("Ends with blank lines\n\n\n", "\n")]
    public void SplitOfMerge_RoundTripsProseBytesAndMetadata(string prose, string lineEnding)
    {
        var metadata = FullMetadata();

        var merged = StoryFileFormat.Merge(prose, metadata, lineEnding);
        var split = StoryFileFormat.Split(merged);

        Assert.Equal(prose, split.Prose);
        Assert.False(split.HadMalformedBlock);
        Assert.Equal(lineEnding, split.LineEnding);
        AssertSameMetadata(metadata, split.Metadata);
    }

    [Theory]
    [InlineData("a\r", "\n")]
    [InlineData("a\r", "\r\n")]
    [InlineData("p\r\r", "\n")]
    [InlineData("p\r\r", "\r\n")]
    [InlineData("\r", "\n")]
    [InlineData("line one\rline two\r", "\n")]
    [InlineData("crlf\r\nthen lf\n", "\r\n")]
    [InlineData("lf\nthen crlf\r\n", "\n")]
    [InlineData("mixed\r\n\r", "\n")]
    [InlineData("\uFEFF\r", "\n")]
    public void SplitOfMerge_LoneCrAndMixedLineBreaks_PreserveProseBytesAndLineEnding(string prose, string lineEnding)
    {
        var split = StoryFileFormat.Split(StoryFileFormat.Merge(prose, MinimalMetadata(), lineEnding));

        Assert.Equal(prose, split.Prose);
        Assert.Equal(lineEnding, split.LineEnding);
        AssertSameMetadata(MinimalMetadata(), split.Metadata);
    }

    [Fact]
    public void Merge_ProseWithoutTrailingNewline_SeparatesWithExactlyOneBlankLine()
    {
        var merged = StoryFileFormat.Merge("Last line.", MinimalMetadata(), "\n");

        Assert.StartsWith("Last line.\n\n<!-- hymnal:v1\n", merged);
        Assert.EndsWith("\n-->\n", merged);
    }

    [Fact]
    public void Merge_Crlf_UsesCrlfThroughoutTheBlock()
    {
        var merged = StoryFileFormat.Merge("Line one.\r\nLine two.", FullMetadata(), "\r\n");

        Assert.StartsWith("Line one.\r\nLine two.\r\n\r\n<!-- hymnal:v1\r\n", merged);
        Assert.EndsWith("\r\n-->\r\n", merged);
        Assert.DoesNotContain("\n", merged.Replace("\r\n", ""));
    }

    [Fact]
    public void Merge_RepeatedSplitAndMerge_IsStable()
    {
        var once = StoryFileFormat.Merge("Prose.\r\n", FullMetadata(), "\r\n");
        var split = StoryFileFormat.Split(once);
        var twice = StoryFileFormat.Merge(split.Prose, split.Metadata, split.LineEnding);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Merge_NullMetadata_ReturnsProseUnchanged()
    {
        const string prose = "Some prose\r\nwithout a block";

        Assert.Equal(prose, StoryFileFormat.Merge(prose, null, "\r\n"));
    }

    [Fact]
    public void Merge_EmptyProse_WritesOnlyTheBlock_AndRoundTrips()
    {
        var merged = StoryFileFormat.Merge("", MinimalMetadata(), "\n");

        Assert.StartsWith("<!-- hymnal:v1\n", merged);

        var split = StoryFileFormat.Split(merged);
        Assert.Equal("", split.Prose);
        AssertSameMetadata(MinimalMetadata(), split.Metadata);
    }

    [Fact]
    public void UnicodeProse_RoundTrips()
    {
        const string prose = "Café — naïve 日本語 🚀\n";

        var split = StoryFileFormat.Split(StoryFileFormat.Merge(prose, FullMetadata(), "\n"));

        Assert.Equal(prose, split.Prose);
    }

    [Fact]
    public void ByteOrderMark_StaysInProse()
    {
        const string prose = "﻿# Title\n\nText.\n";

        var split = StoryFileFormat.Split(StoryFileFormat.Merge(prose, MinimalMetadata(), "\n"));

        Assert.Equal(prose, split.Prose);
        Assert.NotNull(split.Metadata);
    }

    [Fact]
    public void ByteOrderMark_OnlyBlockFile_RoundTripsWithoutSeparator()
    {
        var text = "﻿" + StoryFileFormat.Merge("", MinimalMetadata(), "\n");

        var split = StoryFileFormat.Split(text);

        Assert.Equal("﻿", split.Prose);
        Assert.NotNull(split.Metadata);
        Assert.Equal(text, StoryFileFormat.Merge(split.Prose, split.Metadata, split.LineEnding));
    }

    // ── Comment terminator inside JSON values ───────────────────────────────

    [Fact]
    public void SerializeMetadata_NeverContainsCommentTerminator()
    {
        var metadata = MinimalMetadata() with { Notes = "arrow --> here", Beats = new[] { new Beat("b", "x-->", "<!-- -->", null) } };

        var json = StoryFileFormat.SerializeMetadata(metadata);

        Assert.DoesNotContain("-->", json);
        Assert.DoesNotContain("<!--", json);
    }

    [Fact]
    public void NotesContainingCommentTerminator_RoundTrip()
    {
        var metadata = MinimalMetadata() with { Notes = "a --> b\n<!-- hymnal:v1\n-->" };

        var split = StoryFileFormat.Split(StoryFileFormat.Merge("Prose.\n", metadata, "\n"));

        Assert.Equal("Prose.\n", split.Prose);
        Assert.Equal(metadata.Notes, split.Metadata!.Notes);
    }

    // ── JSON shape ──────────────────────────────────────────────────────────

    [Fact]
    public void SerializeMetadata_UsesCamelCaseAndOmitsAbsentFields()
    {
        var json = StoryFileFormat.SerializeMetadata(FullMetadata() with { Notes = null });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("3f2c9a1e-0000-4000-8000-000000000001", root.GetProperty("id").GetString());
        Assert.Equal("drafting", root.GetProperty("status").GetString());
        Assert.Equal("2026-11-01", root.GetProperty("phaseStartDate").GetString());
        Assert.Equal("2026-11-30", root.GetProperty("phaseEndDate").GetString());
        Assert.Equal(40, root.GetProperty("schedule").GetProperty("Drafting").GetProperty("progress").GetDouble());
        Assert.Equal("shortStory", root.GetProperty("target").GetProperty("preset").GetString());
        Assert.Equal(1000, root.GetProperty("target").GetProperty("minWords").GetInt32());
        Assert.Equal(7500, root.GetProperty("target").GetProperty("maxWords").GetInt32());
        Assert.False(root.TryGetProperty("notes", out _));
        var beat = root.GetProperty("beats")[0];
        Assert.Equal("b1", beat.GetProperty("id").GetString());
        Assert.Equal("Hook", beat.GetProperty("title").GetString());
        Assert.Equal("story.md", beat.GetProperty("scenePath").GetString());
        Assert.Equal("scene-uuid", beat.GetProperty("sceneId").GetString());
    }

    [Fact]
    public void Split_ReadsSpecExampleBlock()
    {
        const string text =
            "…last paragraph.\n\n<!-- hymnal:v1\n" +
            "{ \"id\": \"abc\", \"status\": \"drafting\",\n" +
            "  \"target\": { \"preset\": \"shortStory\", \"minWords\": 1000, \"maxWords\": 7500 },\n" +
            "  \"notes\": \"n\",\n" +
            "  \"beats\": [ { \"id\": \"b\", \"title\": \"Hook\", \"note\": \"x\", \"scenePath\": null } ] }\n" +
            "-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal("…last paragraph.", split.Prose);
        var m = split.Metadata!;
        Assert.Equal("abc", m.Id);
        Assert.Equal(ChapterStatus.Drafting, m.Status);
        Assert.Equal("shortStory", m.Target!.Preset);
        Assert.Equal(7500, m.Target.MaxWords);
        Assert.Equal("n", m.Notes);
        Assert.Null(m.Schedule);
        Assert.Null(m.PhaseStartDate);
        Assert.Equal(new Beat("b", "Hook", "x", null), Assert.Single(m.Beats!));
    }

    // ── Recognition rules ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("Just prose.\n")]
    [InlineData("Just prose, no newline")]
    public void Split_NoBlock_ReturnsTextAsProse(string text)
    {
        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
        Assert.False(split.HadMalformedBlock);
    }

    [Fact]
    public void Split_FileThatIsOnlyABlock_HasEmptyProse()
    {
        const string text = "<!-- hymnal:v1\n{ \"id\": \"x\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal("", split.Prose);
        Assert.Equal("x", split.Metadata!.Id);
    }

    [Fact]
    public void Split_TwoBlocks_RecognizesOnlyTheLast()
    {
        const string first = "Prose.\n\n<!-- hymnal:v1\n{ \"id\": \"first\" }\n-->\n";
        var text = first + "\n<!-- hymnal:v1\n{ \"id\": \"second\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal("second", split.Metadata!.Id);
        Assert.Equal(first.TrimEnd('\n'), split.Prose);
    }

    [Fact]
    public void Split_BlockFollowedByText_IsNotRecognized()
    {
        const string text = "Prose.\n\n<!-- hymnal:v1\n{ \"id\": \"x\" }\n-->\n\nMore prose after.\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
        Assert.False(split.HadMalformedBlock);
    }

    [Fact]
    public void Split_BlockFollowedOnlyByWhitespace_IsRecognized()
    {
        const string text = "Prose.\n\n<!-- hymnal:v1\n{ \"id\": \"x\" }\n-->  \n\n\t\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal("Prose.", split.Prose);
        Assert.Equal("x", split.Metadata!.Id);
    }

    [Fact]
    public void Split_OpeningMarkerMidProse_StaysProse()
    {
        const string text = "Before.\n\n<!-- hymnal:v1\nnot really a block\n\nThe story continues.\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
    }

    [Fact]
    public void Split_OpeningMarkerNotAtLineStart_StaysProse()
    {
        const string text = "Inline <!-- hymnal:v1\n{ \"id\": \"x\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
    }

    [Fact]
    public void Split_OpeningLineWithExtraText_StaysProse()
    {
        const string text = "Prose.\n\n<!-- hymnal:v12\n{ \"id\": \"x\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
    }

    [Fact]
    public void Split_OpeningLineWithTrailingSpaces_IsRecognized()
    {
        const string text = "Prose.\n\n<!-- hymnal:v1   \t\n{ \"id\": \"x\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal("Prose.", split.Prose);
        Assert.Equal("x", split.Metadata!.Id);
    }

    [Fact]
    public void Split_UnterminatedBlock_StaysProse()
    {
        const string text = "Prose.\n\n<!-- hymnal:v1\n{ \"id\": \"x\" }\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
    }

    [Fact]
    public void Split_RawTerminatorInsideHandEditedJson_IsKeptAsProseAndFlaggedMalformed()
    {
        // A literal "-->" ends the HTML comment, so the rest would be published; keep it all as prose and warn.
        const string text = "Prose.\n\n<!-- hymnal:v1\n{ \"notes\": \"a --> b\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
        Assert.True(split.HadMalformedBlock);
    }

    [Fact]
    public void Split_SingleLineBreakBeforeBlock_StaysInProse()
    {
        const string text = "Prose.\n<!-- hymnal:v1\n{ \"id\": \"x\" }\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal("Prose.\n", split.Prose);
        Assert.Equal("x", split.Metadata!.Id);
    }

    // ── Line endings ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a\r\nb\nc", "\r\n")]
    [InlineData("a\nb\r\nc", "\n")]
    [InlineData("no line break", "\n")]
    [InlineData("", "\n")]
    public void Split_DetectsLineEndingFromFirstLineBreak(string text, string expected)
    {
        Assert.Equal(expected, StoryFileFormat.Split(text).LineEnding);
    }

    // ── Malformed blocks ────────────────────────────────────────────────────

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"status\": \"notAStatus\" }")]
    public void Split_MalformedJson_KeepsBlockAsProseAndFlagsIt(string json)
    {
        var text = "Prose.\n\n<!-- hymnal:v1\n" + json + "\n-->\n";

        var split = StoryFileFormat.Split(text);

        Assert.Equal(text, split.Prose);
        Assert.Null(split.Metadata);
        Assert.True(split.HadMalformedBlock);
    }

    [Fact]
    public void Merge_AfterMalformedSplit_AppendsNewBlockAfterTheOldOne()
    {
        const string text = "Prose.\n\n<!-- hymnal:v1\n{ broken\n-->\n";
        var split = StoryFileFormat.Split(text);

        var merged = StoryFileFormat.Merge(split.Prose, MinimalMetadata(), split.LineEnding);

        Assert.StartsWith(text, merged);
        var resplit = StoryFileFormat.Split(merged);
        Assert.Equal(text, resplit.Prose);
        Assert.False(resplit.HadMalformedBlock);
        AssertSameMetadata(MinimalMetadata(), resplit.Metadata);
    }
}

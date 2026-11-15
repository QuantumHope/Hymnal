using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Hymnal.Core.Spelling;

namespace Hymnal.Views.Editor;

/// <summary>A misspelled word in the editor document. Its offsets follow edits (AvaloniaEdit moves segments).</summary>
internal sealed class SpellingMark : TextSegment
{
    public SpellingMark(string word)
    {
        Word = word;
    }

    /// <summary>The word as it was when checked; a mark whose text no longer matches is being edited.</summary>
    public string Word { get; }
}

/// <summary>
/// Draws a wavy underline under each misspelled word. Marks live in a <see cref="TextSegmentCollection{T}"/>
/// tied to the document, so they move with edits between scans; a mark whose text has changed since the scan
/// is not drawn, and <see cref="Suppress"/> can hide the word still being typed. Never throws while drawing.
/// </summary>
internal sealed class SpellingRenderer : IBackgroundRenderer
{
    private const double WaveHeight = 2.0;
    private const double WaveLength = 4.0;

    private TextSegmentCollection<SpellingMark>? _marks;
    private TextDocument? _document;

    public SpellingRenderer(IBrush brush)
    {
        Pen = new Pen(brush, 1.1);
    }

    public Pen Pen { get; set; }

    /// <summary>False hides every mark (Focus Mode, spell check off) without forgetting them.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Returns true for a mark that should not be drawn right now (the word at the caret being typed).</summary>
    public Func<SpellingMark, bool>? Suppress { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public int Count => _marks?.Count ?? 0;

    /// <summary>Replaces the marks with <paramref name="issues"/>, found in <paramref name="document"/>'s current text.</summary>
    public void SetIssues(TextDocument document, IReadOnlyList<SpellingIssue> issues)
    {
        Clear();
        _document = document;
        _marks = new TextSegmentCollection<SpellingMark>(document);
        foreach (var issue in issues)
        {
            if (issue.End > document.TextLength)
                continue;
            _marks.Add(new SpellingMark(issue.Word) { StartOffset = issue.Start, Length = issue.Length });
        }
    }

    public void Clear()
    {
        if (_marks is not null && _document is not null)
        {
            try { _marks.Disconnect(_document); } catch { /* the document may already be gone */ }
        }

        _marks = null;
        _document = null;
    }

    /// <summary>The live mark (one whose text still matches) at <paramref name="offset"/>, if any.</summary>
    public SpellingMark? FindAt(int offset)
    {
        if (_marks is null)
            return null;
        return _marks.FindSegmentsContaining(offset).FirstOrDefault(IsLive);
    }

    /// <summary>
    /// The next live mark after <paramref name="offset"/> (or the previous one before it), wrapping around the
    /// document; null when there are none.
    /// </summary>
    public SpellingMark? FindNext(int offset, int direction)
    {
        if (_marks is null || _marks.Count == 0)
            return null;

        var live = _marks.Where(IsLive).ToList();
        if (live.Count == 0)
            return null;

        return direction >= 0
            ? live.FirstOrDefault(m => m.StartOffset > offset) ?? live[0]
            : live.LastOrDefault(m => m.EndOffset < offset) ?? live[^1];
    }

    /// <summary>A mark still covers the word it was made for (the writer hasn't started changing it).</summary>
    public bool IsLive(SpellingMark mark)
    {
        try
        {
            return _document is not null
                   && mark.Length == mark.Word.Length
                   && mark.EndOffset <= _document.TextLength
                   && string.CompareOrdinal(_document.GetText(mark.StartOffset, mark.Length), mark.Word) == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        try
        {
            if (!IsVisible || _marks is null || _marks.Count == 0 || !textView.VisualLinesValid)
                return;
            var lines = textView.VisualLines;
            if (lines.Count == 0)
                return;

            var start = lines[0].FirstDocumentLine.Offset;
            var end = lines[^1].LastDocumentLine.EndOffset;
            foreach (var mark in _marks.FindOverlappingSegments(start, end - start))
            {
                if (!IsLive(mark) || Suppress?.Invoke(mark) == true)
                    continue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, mark))
                    DrawWave(drawingContext, rect);
            }
        }
        catch
        {
            // Marks are advisory; drawing must never break the editor.
        }
    }

    private void DrawWave(DrawingContext context, Rect rect)
    {
        if (rect.Width <= 0)
            return;

        var y = rect.Bottom - WaveHeight - 0.5;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left, y + WaveHeight), isFilled: false);
            var up = true;
            for (var x = rect.Left + WaveLength / 2; x <= rect.Right + 0.01; x += WaveLength / 2)
            {
                g.LineTo(new Point(x, up ? y : y + WaveHeight));
                up = !up;
            }

            g.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, Pen, geometry);
    }
}

// NOTE: AbstractMargin is NOT present in Avalonia.AvaloniaEdit 12.0.0.
// Fallback: IBackgroundRenderer with BackgroundRenderers.Add() registration in EditorView.axaml.cs.
// The advisory gutter dot is drawn in the leftmost pixel region of the TextView itself.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Hymnal.Theming;

namespace Hymnal.Views;

/// <summary>
/// Advisory gutter renderer that draws a small amber dot next to lines that
/// match one of two Markua advisory patterns:
///   1. A blank line immediately before a {sample: true} attribute block heading.
///   2. A line containing an attribute block <c>{…}</c> with an unrecognised Markua key.
///
/// Registered via <c>TextArea.TextView.BackgroundRenderers</c> from EditorView.axaml.cs.
/// All exceptions are swallowed silently so the editor is never blocked or crashed.
/// </summary>
internal sealed class ValidationMargin : IBackgroundRenderer
{
    // ── Markua valid attribute keys ───────────────────────────────────────────
    private static readonly HashSet<string> ValidMarkuaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "id", "class", "width", "height", "alt", "caption", "title",
        "type", "format", "line-numbers", "crop", "start-line", "end-line", "lang",
        "target", "aside", "blurb", "nonum", "pagebreak"
    };

    // Pattern 1: line N+1 is a {…sample: true…} block (so line N blank is advisory).
    private static readonly Regex SampleBlockRegex = new(
        @"^\{[^}]*sample\s*:\s*true[^}]*\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Pattern 2: captures all attribute blocks on a line.
    private static readonly Regex AttrBlockRegex = new(
        @"\{([^}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Regex to split key=value pairs inside an attribute block.
    private static readonly Regex KeyValueRegex = new(
        @"(\w[\w\-]*)(?:\s*[:=]\s*[^\s,}]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Rendering primitives ─────────────────────────────────────────────────
    private const double DotRadius = 4.0;
    private const double DotX = DotRadius + 2.0;   // horizontal centre of dot in viewport

    // The theme's warning yellow, recoloured in place when the theme changes.
    private readonly IBrush _advisoryBrush = ThemeBrushes.Get("Cora.Accent.Create", "#FCD34D");

    // ── Advisory state ───────────────────────────────────────────────────────
    private readonly HashSet<int> _advisoryLines = new();
    private Timer? _refreshTimer;
    private IDocument? _pendingDocument;
    private TextView? _pendingTextView;

    /// <summary>
    /// Schedules a debounced document scan so rapid keystrokes coalesce into one refresh.
    /// </summary>
    public void ScheduleRefresh(IDocument? document, TextView? textView)
    {
        _pendingDocument = document;
        _pendingTextView = textView;

        _refreshTimer?.Dispose();
        _refreshTimer = new Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Refresh(_pendingDocument, _pendingTextView);
            }),
            null,
            150,
            Timeout.Infinite);
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    // ── IBackgroundRenderer ──────────────────────────────────────────────────

    /// <summary>Render behind text so the dot does not obscure characters.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        try
        {
            if (_advisoryLines.Count == 0) return;

            foreach (var visualLine in textView.VisualLines)
            {
                try
                {
                    int lineNumber = visualLine.FirstDocumentLine.LineNumber;
                    if (!_advisoryLines.Contains(lineNumber)) continue;

                    // Vertical centre of this visual line relative to the viewport.
                    double lineTop = visualLine.GetTextLineVisualYPosition(
                        visualLine.TextLines[0], VisualYPosition.LineTop)
                        - textView.ScrollOffset.Y;
                    double lineBottom = lineTop + visualLine.Height;
                    double centerY = (lineTop + lineBottom) / 2.0;

                    drawingContext.DrawEllipse(
                        _advisoryBrush,
                        null,
                        new Point(DotX, centerY),
                        DotRadius,
                        DotRadius);
                }
                catch
                {
                    // Swallow per-line render exceptions silently.
                }
            }
        }
        catch
        {
            // Swallow all rendering exceptions silently — must never crash the editor.
        }
    }

    // ── Document scanning ────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="document"/> for the two Markua advisory patterns,
    /// rebuilds <c>_advisoryLines</c>, and requests a redraw on <paramref name="textView"/>.
    /// All exceptions are swallowed silently.
    /// </summary>
    public void Refresh(IDocument? document, TextView? textView)
    {
        try
        {
            _advisoryLines.Clear();
            if (document == null) return;

            int lineCount = document.LineCount;

            for (int i = 1; i <= lineCount; i++)
            {
                try
                {
                    var docLine = document.GetLineByNumber(i);
                    string text = document.GetText(docLine.Offset, docLine.Length);

                    // Pattern 1: blank line before a {sample: true} heading.
                    if (string.IsNullOrWhiteSpace(text) && i < lineCount)
                    {
                        var nextLine = document.GetLineByNumber(i + 1);
                        string nextText = document.GetText(nextLine.Offset, nextLine.Length).TrimStart();
                        if (SampleBlockRegex.IsMatch(nextText))
                            _advisoryLines.Add(i);
                    }

                    // Pattern 2: unrecognised Markua attribute key on this line.
                    foreach (Match attrMatch in AttrBlockRegex.Matches(text))
                    {
                        string blockContent = attrMatch.Groups[1].Value;
                        foreach (Match kv in KeyValueRegex.Matches(blockContent))
                        {
                            string key = kv.Groups[1].Value.Trim();
                            if (key.Length > 0 && !ValidMarkuaKeys.Contains(key))
                            {
                                _advisoryLines.Add(i);
                                break;
                            }
                        }
                        if (_advisoryLines.Contains(i)) break;
                    }
                }
                catch
                {
                    // Swallow per-line scan exceptions silently.
                }
            }
        }
        catch
        {
            // Swallow document scan exceptions silently.
        }
        finally
        {
            try { textView?.InvalidateVisual(); } catch { /* swallow */ }
        }
    }
}

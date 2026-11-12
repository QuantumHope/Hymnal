using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hymnal.Core.Markua;

namespace Hymnal.Views.Editor;

/// <summary>
/// Renders a <see cref="MarkuaDocument"/> the way a Leanpub book sets it: a paper page with a serif text face,
/// boxed asides and blurbs, centred scene breaks and footnotes at the end. Read-only; the text can be selected
/// and copied. Purely presentational: the document comes from <c>EditorViewModel.PreviewDocument</c>.
/// </summary>
public sealed class MarkuaPreviewView : UserControl
{
    public static readonly StyledProperty<MarkuaDocument?> DocumentProperty =
        AvaloniaProperty.Register<MarkuaPreviewView, MarkuaDocument?>(nameof(Document));

    public static readonly StyledProperty<string?> BaseDirectoryProperty =
        AvaloniaProperty.Register<MarkuaPreviewView, string?>(nameof(BaseDirectory));

    public MarkuaDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Folder of the previewed file, for resolving relative image paths.</summary>
    public string? BaseDirectory
    {
        get => GetValue(BaseDirectoryProperty);
        set => SetValue(BaseDirectoryProperty, value);
    }

    // ── Page palette (a printed page, independent of the app theme) ─────────

    private static readonly IBrush PaperBrush = new SolidColorBrush(Color.Parse("#FBF8F1"));
    private static readonly IBrush InkBrush = new SolidColorBrush(Color.Parse("#2A2433"));
    private static readonly IBrush DimInkBrush = new SolidColorBrush(Color.Parse("#6E6678"));
    private static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#DCD4C6"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#6B35B0"));
    private static readonly IBrush CodeBackgroundBrush = new SolidColorBrush(Color.Parse("#EFE9DD"));
    private static readonly IBrush BlurbBackgroundBrush = new SolidColorBrush(Color.Parse("#F1ECE2"));

    private static readonly FontFamily SerifFont =
        new("Georgia, Cambria, Constantia, Times New Roman, Liberation Serif, DejaVu Serif, Noto Serif");
    private static readonly FontFamily SansFont = new("avares://Hymnal/Assets/Fonts#Inter");
    private static readonly FontFamily MonoFont = new("avares://Hymnal/Assets/Fonts#JetBrains Mono");

    private const double BodySize = 16.5;
    private const double BodyLineHeight = 27;

    private readonly ScrollViewer _scroller;
    private readonly StackPanel _page;
    private readonly Dictionary<string, (DateTime Stamp, Bitmap Bitmap)> _images = new(StringComparer.Ordinal);

    /// <summary>Number of top-level blocks currently laid out (for tests and diagnostics).</summary>
    internal int RenderedBlockCount { get; private set; }

    internal StackPanel PagePanel => _page;

    public MarkuaPreviewView()
    {
        _page = new StackPanel();

        var paper = new Border
        {
            Background = PaperBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(56, 48, 56, 64),
            Margin = new Thickness(24, 20, 24, 32),
            MaxWidth = 780,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BoxShadow = BoxShadows.Parse("0 6 24 0 #66000000"),
            Child = _page,
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = paper,
        };

        Content = _scroller;
        Background = Brushes.Transparent;
    }

    /// <summary>Scrolls so the given fraction (0 = top, 1 = bottom) of the page is in view; used by split view.</summary>
    public void ScrollToRatio(double ratio)
    {
        var max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
        _scroller.Offset = new Vector(_scroller.Offset.X, Math.Clamp(ratio, 0, 1) * max);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty || change.Property == BaseDirectoryProperty)
            Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var (_, bitmap) in _images.Values)
            bitmap.Dispose();
        _images.Clear();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private void Render()
    {
        _page.Children.Clear();
        var document = Document ?? MarkuaDocument.Empty;

        if (document.Blocks.Count == 0)
        {
            _page.Children.Add(new TextBlock
            {
                Text = "Nothing to preview yet.",
                FontFamily = SerifFont,
                FontStyle = FontStyle.Italic,
                FontSize = BodySize,
                Foreground = DimInkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            RenderedBlockCount = 0;
            return;
        }

        var context = new BlockContext(MarkuaAlignment.Default, 1.0, InkBrush);
        foreach (var block in document.Blocks)
            _page.Children.Add(RenderBlock(block, context));
        RenderedBlockCount = document.Blocks.Count;

        if (document.Footnotes.Count > 0)
            _page.Children.Add(RenderFootnotes(document.Footnotes));
    }

    private sealed record BlockContext(MarkuaAlignment Alignment, double Scale, IBrush Foreground);

    private Control RenderBlock(MarkuaBlock block, BlockContext context) => block switch
    {
        MarkuaHeading heading => RenderHeading(heading),
        MarkuaParagraph paragraph => RenderParagraph(paragraph.Runs,
            paragraph.Alignment == MarkuaAlignment.Default ? context.Alignment : paragraph.Alignment, context),
        MarkuaBlockQuote quote => RenderQuote(quote, context),
        MarkuaCallout callout => RenderCallout(callout, context),
        MarkuaCodeBlock code => RenderCode(code),
        MarkuaList list => RenderList(list, context),
        MarkuaSceneBreak => RenderSceneBreak(),
        MarkuaPageBreak => RenderPageBreak(),
        MarkuaResource resource => RenderResource(resource),
        MarkuaTable table => RenderTable(table, context),
        _ => new Control(),
    };

    private Control RenderHeading(MarkuaHeading heading)
    {
        if (heading.IsPart)
        {
            var part = MakeText(heading.Runs, new BlockContext(MarkuaAlignment.Center, 1, InkBrush), 34, 44);
            part.FontWeight = FontWeight.Bold;
            part.TextAlignment = TextAlignment.Center;

            var panel = new StackPanel { Margin = new Thickness(0, 72, 0, 72), Spacing = 18 };
            panel.Children.Add(part);
            panel.Children.Add(new Border
            {
                Height = 1, Width = 120, Background = RuleBrush, HorizontalAlignment = HorizontalAlignment.Center,
            });
            return panel;
        }

        var (size, top, bottom) = heading.Level switch
        {
            1 => (30.0, 8.0, 26.0),
            2 => (23.0, 18.0, 12.0),
            3 => (19.0, 14.0, 8.0),
            _ => (BodySize, 12.0, 6.0),
        };

        var text = MakeText(heading.Runs, new BlockContext(MarkuaAlignment.Default, 1, InkBrush), size, size * 1.3);
        text.FontWeight = heading.Level >= 4 ? FontWeight.SemiBold : FontWeight.Bold;
        text.Margin = new Thickness(0, top, 0, bottom);
        if (heading.Level >= 5)
            text.FontStyle = FontStyle.Italic;
        return text;
    }

    private Control RenderParagraph(IReadOnlyList<MarkuaRun> runs, MarkuaAlignment alignment, BlockContext context)
    {
        var text = MakeText(runs, context, BodySize * context.Scale, BodyLineHeight * context.Scale);
        text.TextAlignment = ToTextAlignment(alignment);
        text.Margin = new Thickness(0, 0, 0, 13 * context.Scale);
        return text;
    }

    private Control RenderQuote(MarkuaBlockQuote quote, BlockContext context)
    {
        var inner = new StackPanel();
        foreach (var child in quote.Blocks)
            inner.Children.Add(RenderBlock(child, context with { Foreground = DimInkBrush }));

        return new Border
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(18, 2, 0, 0),
            Margin = new Thickness(18, 0, 18, 13),
            Child = inner,
        };
    }

    private static (string? Label, Color Accent) CalloutStyle(MarkuaCalloutKind kind) => kind switch
    {
        MarkuaCalloutKind.Warning => ("Warning", Color.Parse("#B26A00")),
        MarkuaCalloutKind.Tip => ("Tip", Color.Parse("#2E7D32")),
        MarkuaCalloutKind.Error => ("Error", Color.Parse("#C62828")),
        MarkuaCalloutKind.Information => ("Information", Color.Parse("#1565C0")),
        MarkuaCalloutKind.Question => ("Question", Color.Parse("#6A1B9A")),
        MarkuaCalloutKind.Discussion => ("Discussion", Color.Parse("#00796B")),
        MarkuaCalloutKind.Exercise => ("Exercise", Color.Parse("#5D4037")),
        _ => (null, Color.Parse("#B9AE9C")),
    };

    private Control RenderCallout(MarkuaCallout callout, BlockContext context)
    {
        if (callout.Kind == MarkuaCalloutKind.Center)
        {
            var centered = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            foreach (var child in callout.Blocks)
                centered.Children.Add(RenderBlock(child, context with { Alignment = MarkuaAlignment.Center }));
            return centered;
        }

        var inner = new StackPanel();
        var (label, accent) = CalloutStyle(callout.Kind);
        if (label is not null)
        {
            inner.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                FontFamily = SansFont,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 1.2,
                Foreground = new SolidColorBrush(accent),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        var innerContext = context with { Scale = 0.94 };
        foreach (var child in callout.Blocks)
            inner.Children.Add(RenderBlock(child, innerContext));

        // Drop the last child's bottom margin so the box padding is even.
        if (inner.Children.Count > 0 && inner.Children[^1] is Control last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);

        var isAside = callout.Kind == MarkuaCalloutKind.Aside;
        return new Border
        {
            Background = label is null
                ? (isAside ? Brushes.Transparent : BlurbBackgroundBrush)
                : new SolidColorBrush(accent, 0.08),
            BorderBrush = new SolidColorBrush(accent, isAside ? 0.9 : 1),
            BorderThickness = isAside ? new Thickness(1) : new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(isAside ? 4 : 2),
            Padding = new Thickness(18, 14),
            Margin = new Thickness(0, 4, 0, 17),
            Child = inner,
        };
    }

    private static Control RenderCode(MarkuaCodeBlock code)
    {
        var text = new SelectableTextBlock
        {
            Text = code.Code,
            FontFamily = MonoFont,
            FontSize = 13,
            LineHeight = 20,
            Foreground = InkBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        return new Border
        {
            Background = CodeBackgroundBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 2, 0, 15),
            Child = text,
        };
    }

    private Control RenderList(MarkuaList list, BlockContext context)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8 * context.Scale) };
        for (var k = 0; k < list.Items.Count; k++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*") };
            row.Children.Add(new TextBlock
            {
                Text = list.Ordered ? $"{list.Start + k}." : "•",
                FontFamily = SerifFont,
                FontSize = BodySize * context.Scale,
                LineHeight = BodyLineHeight * context.Scale,
                Foreground = context.Foreground,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 10, 0),
            });

            var body = new StackPanel();
            Grid.SetColumn(body, 1);
            foreach (var child in list.Items[k])
            {
                var rendered = RenderBlock(child, context);
                if (child is MarkuaParagraph)
                    rendered.Margin = new Thickness(0, 0, 0, 5 * context.Scale);
                body.Children.Add(rendered);
            }

            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control RenderSceneBreak() => new TextBlock
    {
        Text = "*      *      *",
        FontFamily = SerifFont,
        FontSize = BodySize,
        Foreground = DimInkBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 10, 0, 22),
    };

    private static Control RenderPageBreak()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Margin = new Thickness(-56, 18, -56, 30),
        };

        Border Dash(int column)
        {
            var dash = new Border
            {
                Height = 1,
                BorderBrush = RuleBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dash, column);
            return dash;
        }

        var label = new TextBlock
        {
            Text = "PAGE BREAK",
            FontFamily = SansFont,
            FontSize = 10,
            LetterSpacing = 1.5,
            Foreground = DimInkBrush,
            Margin = new Thickness(12, 0),
        };
        Grid.SetColumn(label, 1);

        grid.Children.Add(Dash(0));
        grid.Children.Add(label);
        grid.Children.Add(Dash(2));
        return grid;
    }

    private Control RenderResource(MarkuaResource resource)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 18), Spacing = 8 };

        var bitmap = resource.IsImage ? LoadImage(resource.Source) : null;
        if (bitmap is not null)
        {
            panel.Children.Add(new Image
            {
                Source = bitmap,
                MaxHeight = 460,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        else
        {
            var what = !resource.IsImage ? "Media" : MarkuaResourceResolver.IsRemote(resource.Source) ? "Web image" : "Image not found";
            panel.Children.Add(new Border
            {
                BorderBrush = RuleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(16, 22),
                Background = BlurbBackgroundBrush,
                Child = new TextBlock
                {
                    Text = $"{what}: {resource.Source}",
                    FontFamily = SansFont,
                    FontSize = 12,
                    Foreground = DimInkBrush,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            });
        }

        if (resource.Caption.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = resource.Caption,
                FontFamily = SerifFont,
                FontStyle = FontStyle.Italic,
                FontSize = 14,
                Foreground = DimInkBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        return panel;
    }

    private Bitmap? LoadImage(string source)
    {
        try
        {
            var path = MarkuaResourceResolver.Resolve(BaseDirectory, source, File.Exists);
            if (path is null)
                return null;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (_images.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Bitmap;

            cached.Bitmap?.Dispose();
            var bitmap = new Bitmap(path);
            _images[path] = (stamp, bitmap);
            return bitmap;
        }
        catch
        {
            return null; // Unreadable or unsupported image: show the placeholder instead.
        }
    }

    private Control RenderTable(MarkuaTable table, BlockContext context)
    {
        var columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 16) };
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        void AddRow(IReadOnlyList<IReadOnlyList<MarkuaRun>> cells, int rowIndex, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var runs = c < cells.Count ? cells[c] : [];
                var alignment = c < table.Alignments.Count ? table.Alignments[c] : MarkuaAlignment.Default;
                var text = MakeText(runs, context, 14.5, 22);
                text.TextAlignment = ToTextAlignment(alignment);
                if (header)
                    text.FontWeight = FontWeight.Bold;

                var cell = new Border
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 0, header ? 2 : 1),
                    Padding = new Thickness(0, 6, 22, 6),
                    Child = text,
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        AddRow(table.Header, 0, header: true);
        for (var r = 0; r < table.Rows.Count; r++)
            AddRow(table.Rows[r], r + 1, header: false);
        return grid;
    }

    private Control RenderFootnotes(IReadOnlyList<MarkuaFootnote> footnotes)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 28, 0, 0), Spacing = 6 };
        panel.Children.Add(new Border
        {
            Height = 1, Width = 160, Background = RuleBrush,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
        });

        var context = new BlockContext(MarkuaAlignment.Default, 0.82, InkBrush);
        foreach (var note in footnotes)
        {
            var runs = new List<MarkuaRun> { new($"{note.Number}. ") };
            runs.AddRange(note.Runs);
            panel.Children.Add(MakeText(runs, context, 13.5, 21));
        }

        return panel;
    }

    // ── Inline text ──────────────────────────────────────────────────────────

    private static TextAlignment ToTextAlignment(MarkuaAlignment alignment) => alignment switch
    {
        MarkuaAlignment.Center => TextAlignment.Center,
        MarkuaAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    private static SelectableTextBlock MakeText(IReadOnlyList<MarkuaRun> runs, BlockContext context, double size, double lineHeight)
    {
        var text = new SelectableTextBlock
        {
            FontFamily = SerifFont,
            FontSize = size,
            LineHeight = lineHeight,
            Foreground = context.Foreground,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = ToTextAlignment(context.Alignment),
            SelectionBrush = new SolidColorBrush(Color.Parse("#4D9D4EDD")),
            Inlines = new InlineCollection(),
        };

        foreach (var run in runs)
            text.Inlines!.Add(ToInline(run, size));

        return text;
    }

    internal static Inline ToInline(MarkuaRun source, double size)
    {
        if (source.IsLineBreak)
            return new LineBreak();

        var run = new Run(source.Text);

        if (source.Has(MarkuaTextStyle.Bold))
            run.FontWeight = FontWeight.Bold;
        if (source.Has(MarkuaTextStyle.Italic))
            run.FontStyle = FontStyle.Italic;

        if (source.Has(MarkuaTextStyle.Code))
        {
            run.FontFamily = MonoFont;
            run.FontSize = size * 0.86;
            run.Background = CodeBackgroundBrush;
        }

        var decorations = new TextDecorationCollection();
        if (source.Has(MarkuaTextStyle.Strikethrough))
            decorations.AddRange(TextDecorations.Strikethrough);
        if (source.Has(MarkuaTextStyle.Link))
        {
            decorations.AddRange(TextDecorations.Underline);
            run.Foreground = LinkBrush;
        }

        if (decorations.Count > 0)
            run.TextDecorations = decorations;

        if (source.Has(MarkuaTextStyle.Superscript))
        {
            run.BaselineAlignment = BaselineAlignment.Superscript;
            run.FontSize = size * 0.7;
        }
        else if (source.Has(MarkuaTextStyle.Subscript))
        {
            run.BaselineAlignment = BaselineAlignment.Subscript;
            run.FontSize = size * 0.7;
        }

        if (source.Has(MarkuaTextStyle.FootnoteReference))
            run.Foreground = LinkBrush;

        return run;
    }
}

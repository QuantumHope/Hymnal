using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Hymnal.Views;

/// <summary>
/// Code-behind for EditorView. Handles:
///   1. Markua XSHD syntax highlighting load on first attach.
///   2. Two-way text sync: ViewModel.Text ↔ PART_Editor.Text
///      (AvaloniaEdit.TextEditor does not expose Text as a bindable AvaloniaProperty,
///      so we wire it manually via TextChanged and WhenAnyValue).
/// </summary>
public partial class EditorView : UserControl
{
    private CompositeDisposable _viewDisposables = new();
    private IDisposable? _vmTextSub;
    private IDisposable? _vmLayoutSub;
    private bool _highlightingLoaded;
    private MarkuaColorizingTransformer? _markuaColorizer;
    private ValidationMargin? _validationMargin;

    public static readonly StyledProperty<bool> HasWorkspaceProperty =
        AvaloniaProperty.Register<EditorView, bool>(nameof(HasWorkspace));

    public bool HasWorkspace
    {
        get => GetValue(HasWorkspaceProperty);
        set => SetValue(HasWorkspaceProperty, value);
    }

    /// <summary>
    /// Shown at the left of the editor's header row, beside the Write / Split / Preview switch (the Write
    /// surface puts the chapter status bar here, so the editor has one header row rather than two).
    /// </summary>
    public static readonly StyledProperty<Control?> HeaderContentProperty =
        AvaloniaProperty.Register<EditorView, Control?>(nameof(HeaderContent));

    public Control? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>Below this header width the mode buttons drop their labels and show icons only.</summary>
    internal const double CompactHeaderWidth = 640;

    private IDisposable? _headerContentVisibilitySub;

    public EditorView()
    {
        InitializeComponent();
        PART_HeaderRow.SizeChanged += (_, e) => UpdateHeaderCompact(e.NewSize.Width);
    }

    internal void UpdateHeaderCompact(double width) =>
        PART_HeaderRow.Classes.Set("compact", width > 0 && width < CompactHeaderWidth);

    /// <summary>
    /// The header row shows when it has something to show: the mode switch (any previewable file) or a visible
    /// header control. The divider only separates the two when both are present.
    /// </summary>
    private void UpdateHeaderRow()
    {
        var hasSwitch = DataContext is EditorViewModel { IsPreviewAvailable: true };
        var hasHeader = HeaderContent is { IsVisible: true };
        PART_HeaderRow.IsVisible = hasSwitch || hasHeader;
        PART_HeaderDivider.IsVisible = hasSwitch && hasHeader;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HasWorkspaceProperty && DataContext is EditorViewModel vm)
            vm.HasWorkspace = change.GetNewValue<bool>();

        if (change.Property == HeaderContentProperty)
        {
            _headerContentVisibilitySub?.Dispose();
            _headerContentVisibilitySub = HeaderContent?.GetObservable(IsVisibleProperty)
                .Subscribe(_ => UpdateHeaderRow());
            UpdateHeaderRow();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _viewDisposables = new CompositeDisposable();

        // Load syntax styling once (idempotent via the loaded flag).
        if (!_highlightingLoaded)
        {
            LoadMarkuaHighlighting();
            _highlightingLoaded = true;
        }

        // Advisory gutter: register ValidationMargin as a background renderer.
        // AbstractMargin is absent in Avalonia.AvaloniaEdit 12.0.0; we use IBackgroundRenderer
        // + BackgroundRenderers.Add() as the documented fallback.
        try
        {
            _validationMargin = new ValidationMargin();
            PART_Editor.TextArea.TextView.BackgroundRenderers.Add(_validationMargin);
        }
        catch
        {
            // Swallow silently — ValidationMargin is advisory only; editor must not crash.
            _validationMargin = null;
        }

        // Apply dark-theme colors directly to the AvaloniaEdit rendering surface.
        ApplyEditorTheme();

        // Editor → VM: fire on every text edit in the AvaloniaEdit surface.
        PART_Editor.TextChanged += OnEditorTextChanged;
        _viewDisposables.Add(Disposable.Create(() =>
        {
            PART_Editor.TextChanged -= OnEditorTextChanged;
        }));

        // Split view: keep the preview scrolled to the same place as the source.
        PART_Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolled;
        _viewDisposables.Add(Disposable.Create(() =>
        {
            PART_Editor.TextArea.TextView.ScrollOffsetChanged -= OnEditorScrolled;
        }));

        // VM → Editor: initial sync + react to DataContext changes.
        SubscribeToVmText();
        DataContextChanged += OnDataContextChanged;
        _viewDisposables.Add(Disposable.Create(() =>
        {
            DataContextChanged -= OnDataContextChanged;
            _vmTextSub?.Dispose();
            _vmTextSub = null;
            _vmLayoutSub?.Dispose();
            _vmLayoutSub = null;
        }));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Remove advisory gutter renderer to avoid dangling references.
        if (_validationMargin != null)
        {
            try { PART_Editor.TextArea.TextView.BackgroundRenderers.Remove(_validationMargin); }
            catch { /* swallow */ }
            _validationMargin.Dispose();
            _validationMargin = null;
        }

        _viewDisposables.Dispose();
    }

    // ── VM → Editor ──────────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vmTextSub?.Dispose();
        _vmTextSub = null;
        SubscribeToVmText();
    }

    // ── Write / Split / Preview layout ───────────────────────────────────────

    /// <summary>
    /// Sizes the source and preview columns for the current mode: source only, half and half, or preview only.
    /// Re-entering Split resets a splitter drag to an even split.
    /// </summary>
    private void SubscribeToVmLayout()
    {
        _vmLayoutSub?.Dispose();
        _vmLayoutSub = null;
        if (DataContext is not EditorViewModel vm) return;

        _vmLayoutSub = new CompositeDisposable(
            vm.WhenAnyValue(x => x.ShowSourcePane, x => x.ShowPreviewPane)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(panes => ApplyPaneLayout(panes.Item1, panes.Item2)),
            vm.WhenAnyValue(x => x.IsPreviewAvailable)
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => UpdateHeaderRow()));
    }

    internal void ApplyPaneLayout(bool showSource, bool showPreview)
    {
        var columns = PART_PaneGrid.ColumnDefinitions;
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);

        columns[0].Width = showSource || !showPreview ? star : none;
        columns[2].Width = showPreview ? star : none;

        if (showSource && showPreview)
            PART_Preview.ScrollToRatio(EditorScrollRatio());
    }

    private void OnEditorScrolled(object? sender, EventArgs e)
    {
        if (DataContext is EditorViewModel { IsSplitView: true })
            PART_Preview.ScrollToRatio(EditorScrollRatio());
    }

    private double EditorScrollRatio()
    {
        var view = PART_Editor.TextArea.TextView;
        var scrollable = view.DocumentHeight - view.Bounds.Height;
        return scrollable > 0 ? view.ScrollOffset.Y / scrollable : 0;
    }

    /// <summary>
    /// Observes <c>EditorViewModel.Text</c> and updates <c>PART_Editor.Text</c> when it changes
    /// (handles initial load and external-reload scenarios). The equality guard breaks the
    /// potential notify loop with <see cref="OnEditorTextChanged"/>.
    /// </summary>
    private void SubscribeToVmText()
    {
        SubscribeToVmLayout();
        if (DataContext is not EditorViewModel vm) return;

        _vmTextSub = vm.WhenAnyValue(x => x.Text)
            .ObserveOn(AvaloniaScheduler.Instance)
            .Subscribe(text =>
            {
                if (PART_Editor.Text != text)
                    PART_Editor.Text = text;
            });
    }

    // ── Editor → VM ──────────────────────────────────────────────────────────

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (DataContext is EditorViewModel vm)
            vm.Text = PART_Editor.Text;

        // Refresh advisory gutter on every text change (debounced).
        if (_validationMargin != null)
        {
            try { _validationMargin.ScheduleRefresh(PART_Editor.Document, PART_Editor.TextArea.TextView); }
            catch { /* swallow — advisory only */ }
        }
    }

    // ── Syntax styling ───────────────────────────────────────────────────────

    private void ApplyEditorTheme()
    {
        var theme = Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;

        var surfaceBrush = TryGetResource("SurfaceBaseBrush", theme, out var surface)
            ? surface as IBrush
            : null;
        var textBrush = TryGetResource("OnSurfaceBrush", theme, out var text)
            ? text as IBrush
            : null;
        var accentBrush = TryGetResource("SynthwavePurpleBrush", theme, out var accent)
            ? accent as IBrush
            : null;

        var dimBrush = TryGetResource("OnSurfaceDimBrush", theme, out var dim)
            ? dim as IBrush
            : null;
        var borderBrush = TryGetResource("BorderDefaultBrush", theme, out var border)
            ? border as IBrush
            : null;
        var currentLineBrush = TryGetResource("SurfaceHighBrush", theme, out var currentLine)
            ? currentLine as IBrush
            : null;

        // Keep the editor and TextArea transparent so the synthwave background gradient
        // set on the parent Panel in MainWindow shows through.
        PART_Editor.Background = Brushes.Transparent;
        PART_Editor.TextArea.Background = Brushes.Transparent;

        if (textBrush != null)
        {
            PART_Editor.Foreground = textBrush;
            PART_Editor.TextArea.Foreground = textBrush;
            PART_Editor.TextArea.TextView.LinkTextForegroundBrush = accentBrush ?? textBrush;
        }

        PART_Editor.LineNumbersForeground = dimBrush ?? textBrush ?? Brushes.White;

        if (accentBrush != null)
            PART_Editor.TextArea.CaretBrush = accentBrush;

        PART_Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x9D, 0x4E, 0xDD));
        PART_Editor.TextArea.SelectionForeground = textBrush ?? Brushes.White;
        PART_Editor.TextArea.SelectionBorder = borderBrush != null ? new Pen(borderBrush, 1) : null;
        PART_Editor.TextArea.SelectionCornerRadius = 2;

        if (currentLineBrush != null)
            PART_Editor.TextArea.TextView.CurrentLineBackground = currentLineBrush;
    }

    private void LoadMarkuaHighlighting()
    {
        // The XSHD definition is currently disabled because AvaloniaEdit crashes on a zero-width
        // Markua rule during startup. Keep the editor usable by applying our own safe transformer.
        PART_Editor.SyntaxHighlighting = null;

        _markuaColorizer ??= new MarkuaColorizingTransformer(PART_Editor.FontFamily);
        PART_Editor.TextArea.TextView.LineTransformers.Add(_markuaColorizer);

        if (App.Services?.GetService<NotificationService>() is { } notifications)
            notifications.ShowInfo("Markua syntax styling is active in simplified mode.");
    }

    private static bool TryGetResource(string key, ThemeVariant theme, out object? value)
    {
        value = null;
        if (Application.Current?.Resources == null)
            return false;

        if (!Application.Current.TryGetResource(key, theme, out value))
            return false;

        return value != null;
    }

    private sealed class MarkuaColorizingTransformer : DocumentColorizingTransformer
    {
        private static readonly Regex HeadingRegex = new(@"^\s*#{1,6}\s+.+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex BoldRegex = new(@"\*\*[^*\r\n]+\*\*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ItalicRegex = new(@"(?<!\*)\*[^*\r\n]+\*(?!\*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IBrush _headingBrush = new SolidColorBrush(Color.Parse("#C792EA"));
        private readonly IBrush _boldBrush = new SolidColorBrush(Color.Parse("#EDE8F5"));
        private readonly IBrush _italicBrush = new SolidColorBrush(Color.Parse("#A0A0B8"));
        private readonly Typeface _headingTypeface;
        private readonly Typeface _boldTypeface;
        private readonly Typeface _italicTypeface;

        public MarkuaColorizingTransformer(FontFamily fontFamily)
        {
            _headingTypeface = new Typeface(fontFamily, FontStyle.Normal, FontWeight.Bold);
            _boldTypeface = new Typeface(fontFamily, FontStyle.Normal, FontWeight.Bold);
            _italicTypeface = new Typeface(fontFamily, FontStyle.Italic, FontWeight.Normal);
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (CurrentContext.Document is null)
                return;

            var text = CurrentContext.Document.GetText(line);
            if (HeadingRegex.IsMatch(text))
            {
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    var props = element.TextRunProperties;
                    props.SetForegroundBrush(_headingBrush);
                    props.SetTypeface(_headingTypeface);
                });
                return;
            }

            ApplyMatches(text, line.Offset, BoldRegex, _boldBrush, _boldTypeface);
            ApplyMatches(text, line.Offset, ItalicRegex, _italicBrush, _italicTypeface);
        }

        private void ApplyMatches(string text, int lineOffset, Regex regex, IBrush brush, Typeface typeface)
        {
            foreach (Match match in regex.Matches(text))
            {
                if (!match.Success || match.Length <= 0)
                    continue;

                var start = lineOffset + match.Index;
                var end = start + match.Length;

                ChangeLinePart(start, end, element =>
                {
                    var props = element.TextRunProperties;
                    props.SetForegroundBrush(brush);
                    props.SetTypeface(typeface);
                });
            }
        }
    }
}

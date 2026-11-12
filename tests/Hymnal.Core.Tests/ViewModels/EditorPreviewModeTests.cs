using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Markua;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Views.Editor;

namespace Hymnal.ViewModels;

/// <summary>The Write pane's Write / Split / Preview modes and the rendered Markua preview.</summary>
[Collection("AvaloniaUi")]
public sealed class EditorPreviewModeTests : IDisposable
{
    private readonly string _root;
    private readonly OwnedDisposables _owned = new();

    static EditorPreviewModeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    public EditorPreviewModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hymnal-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            _owned.Dispose();
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private EditorViewModel CreateEditor(OwnedDisposables owner)
    {
        var store = new MetadataStore();
        var writer = new StoryFileWriter(store);
        var editor = owner.Track(new EditorViewModel(store, new SilentNotifications(), new WordCountService(), writer)
        {
            HasWorkspace = true,
        });
        writer.AttachBuffer(editor);
        return editor;
    }

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static ChapterNode Node(string name) => new(name, name, name, NodeKind.Chapter, IsMissing: false, Index: 0);

    [Fact]
    public async Task Defaults_to_write_mode_and_does_not_parse_until_preview_is_shown()
    {
        var path = WriteFile("one.md", "# One\n\nHello.");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));

            Assert.Equal(EditorViewMode.Write, editor.ViewMode);
            Assert.True(editor.IsPreviewAvailable);
            Assert.True(editor.ShowSourcePane);
            Assert.False(editor.ShowPreviewPane);
            Assert.False(editor.IsSplitView);

            await Task.Delay(350);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Same(MarkuaDocument.Empty, editor.PreviewDocument);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Split_and_preview_modes_show_the_parsed_document_and_follow_edits()
    {
        var path = WriteFile("one.md", "# One\n\nHello.");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);
            await PlanTestHarness.Within(editor.OpenChapterAsync(Node("one.md"), path));

            editor.SetViewModeCommand.Execute(EditorViewMode.Split).Subscribe();
            Assert.True(editor.IsSplitView);
            Assert.True(editor.ShowSourcePane && editor.ShowPreviewPane);
            Assert.Equal(_root, editor.PreviewBaseDirectory);

            await PlanTestHarness.PumpUntilAsync(() => editor.PreviewDocument.Blocks.Count == 2);
            Assert.IsType<MarkuaHeading>(editor.PreviewDocument.Blocks[0]);

            editor.Text = "# One\n\nHello.\n\n* * *\n\nLater.";
            await PlanTestHarness.PumpUntilAsync(() => editor.PreviewDocument.Blocks.Count == 4);

            editor.SetViewModeCommand.Execute(EditorViewMode.Preview).Subscribe();
            Assert.False(editor.ShowSourcePane);
            Assert.True(editor.ShowPreviewPane);
            Assert.False(editor.IsSplitView);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Cycle_goes_write_split_preview_write()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(() =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);

            var seen = new List<EditorViewMode>();
            for (var k = 0; k < 3; k++)
            {
                editor.CycleViewModeCommand.Execute().Subscribe();
                seen.Add(editor.ViewMode);
            }

            Assert.Equal([EditorViewMode.Split, EditorViewMode.Preview, EditorViewMode.Write], seen);
            return Task.FromResult(0);
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Book_txt_is_never_previewed_and_keeps_its_source_visible()
    {
        var book = WriteFile("Book.txt", "one.md\n");

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var owner = new OwnedDisposables();
            var editor = CreateEditor(owner);
            editor.ViewMode = EditorViewMode.Preview;
            await PlanTestHarness.Within(editor.SelectBookAsync(book));

            Assert.False(editor.IsPreviewAvailable);
            Assert.False(editor.ShowPreviewPane);
            Assert.True(editor.ShowSourcePane);
            return 0;
        }, TimeSpan.FromSeconds(30));
    }

    // No forced line breaks here: the headless platform's stub text shaper never finishes measuring a multi-line
    // TextBlock under an unbounded height (a StackPanel or ScrollViewer), even a plain one. Real rendering uses
    // Skia and lays them out normally; the parser tests cover how line breaks are produced.
    [Fact]
    public async Task Preview_view_renders_every_block_kind()
    {
        const string source = """
            {class: part}
            # Part One

            # Chapter

            Plain **bold** *italic* `code` ~~gone~~ x^2^ [link](https://x.y) note[^n]

            B> A blurb.

            W> A warning.

            {aside}
            Aside text.
            {/aside}

            > A quote.

            ```
            code block
            ```

            - one
              1. nested

            * * *

            {pagebreak}

            ![Missing](nowhere.png)

            | A | B |
            |---|:-:|
            | 1 | 2 |

            [^n]: The note.
            """;

        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(() =>
        {
            var document = MarkuaParser.Parse(source);
            var view = new MarkuaPreviewView { BaseDirectory = _root, Document = document };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            try
            {
                Assert.Equal(document.Blocks.Count, view.RenderedBlockCount);
                // Every block plus the footnote section.
                Assert.Equal(document.Blocks.Count + 1, view.PagePanel.Children.Count);

                view.ScrollToRatio(0.5);
                view.Document = MarkuaDocument.Empty;
                Assert.Equal(0, view.RenderedBlockCount);
                Assert.Single(view.PagePanel.Children);
            }
            finally
            {
                window.Close();
            }

            return Task.FromResult(0);
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Inline_styles_map_to_text_runs()
    {
        ReactiveUiTestBootstrap.RunOnUiThread(() =>
        {
            var bold = Assert.IsType<Run>(MarkuaPreviewView.ToInline(
                new MarkuaRun("b", MarkuaTextStyle.Bold | MarkuaTextStyle.Italic), 16));
            Assert.Equal(FontWeight.Bold, bold.FontWeight);
            Assert.Equal(FontStyle.Italic, bold.FontStyle);

            var sup = Assert.IsType<Run>(MarkuaPreviewView.ToInline(new MarkuaRun("2", MarkuaTextStyle.Superscript), 16));
            Assert.Equal(BaselineAlignment.Superscript, sup.BaselineAlignment);

            var link = Assert.IsType<Run>(MarkuaPreviewView.ToInline(new MarkuaRun("l", MarkuaTextStyle.Link, "u"), 16));
            Assert.NotNull(link.TextDecorations);

            Assert.IsType<LineBreak>(MarkuaPreviewView.ToInline(MarkuaRun.LineBreak, 16));
        });
    }

    private sealed class SilentNotifications : INotificationService
    {
        public void ShowError(string message) { }
        public void ShowInfo(string message) { }
        public void ShowSuccess(string message) { }
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using AvaloniaEdit;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>Focus mode's writing column: a fixed width, centred, whatever the text.</summary>
[Collection("AvaloniaUi")]
public sealed class EditorFocusColumnTests
{
    static EditorFocusColumnTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task The_focus_column_keeps_its_width_and_place_as_lines_grow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hymnal-focus-column-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "chapter.md");
        await File.WriteAllTextAsync(file, "Short.");
        try
        {
            await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
            {
                var store = new MetadataStore();
                using var vm = new EditorViewModel(store, new NotificationService(), new WordCountService(),
                    new StoryFileWriter(store)) { HasWorkspace = true };
                await vm.OpenArbitraryFileAsync(file);

                // The editor's template comes from AvaloniaEdit's theme, which App.axaml includes in the app styles.
                Avalonia.Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://Hymnal.Core.Tests/"))
                {
                    Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
                });
                var view = new EditorView { DataContext = vm, IsDistractionFree = true };
                var window = new Window { Width = 1400, Height = 700, Content = view };
                window.Show();
                await Pump();

                var editor = view.FindControl<TextEditor>("PART_Editor")!;
                var before = editor.Bounds;
                Assert.Equal(EditorView.FocusColumnWidth, before.Width);
                Assert.Equal((view.Bounds.Width - EditorView.FocusColumnWidth) / 2, before.X, 1);

                editor.Text = "A much longer line of prose that keeps on going, the kind a writer types mid-scene.";
                await Pump();
                Assert.Equal(before, editor.Bounds);

                // A window narrower than the column: the column fills it.
                window.Width = 600;
                await Pump();
                Assert.Equal(view.Bounds.Width, editor.Bounds.Width, 1);

                // Out of focus mode the editor fills its pane again.
                window.Width = 1400;
                view.IsDistractionFree = false;
                await Pump();
                Assert.Equal(HorizontalAlignment.Stretch, editor.HorizontalAlignment);
                Assert.True(editor.Bounds.Width > EditorView.FocusColumnWidth);

                window.Close();
                return true;
            });
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Services;
using Hymnal.Core.Spelling;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views.Editor;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>The editor side of spell check: marks follow scans of the current text, and fixes edit the document.</summary>
[Collection("AvaloniaUi")]
public sealed class SpellingHookTests
{
    static SpellingHookTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            if (waited >= timeoutMs)
                Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Marks_scans_of_the_current_text_goes_to_issues_and_applies_suggestions()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            Avalonia.Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://Hymnal.Core.Tests/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
            });

            using var service = EditorSpellingViewModelTests.CreateService();
            var store = new MetadataStore();
            using var vm = new EditorViewModel(store, new EditorSpellingViewModelTests.RecordingNotifications(),
                new WordCountService(), new StoryFileWriter(store), service);

            var folder = Path.Combine(Path.GetTempPath(), "hymnal-spelling-hook-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "one.md");
            File.WriteAllText(path, "The quikc cat sat.");
            await PlanTestHarness.Within(vm.OpenChapterAsync(
                new Hymnal.Core.Models.ChapterNode("one.md", "one.md", "one.md", Hymnal.Core.Models.NodeKind.Chapter, IsMissing: false, Index: 0), path));

            var textEditor = new TextEditor { Width = 600, Height = 300, Text = vm.Text };
            var window = new Window { Content = textEditor, Width = 640, Height = 360 };
            // Keep the view model's buffer in step with the editor, as EditorView does.
            textEditor.TextChanged += (_, _) => vm.Text = textEditor.Text;
            using var hook = new SpellingHook(textEditor, vm.Spelling, () => vm.Text, Brushes.Red);
            window.Show();
            try
            {
                window.UpdateLayout();

                // A scan of older text is dropped; one of the current text is shown.
                hook.Apply(new SpellingScan("Older text qwzx", [new SpellingIssue(11, 4, "qwzx")]));
                Assert.Equal(0, hook.MarkCount);
                hook.Apply(new SpellingScan(vm.Text, [new SpellingIssue(4, 5, "quikc")]));
                Assert.Equal(1, hook.MarkCount);
                hook.Apply(SpellingScan.Empty);
                Assert.Equal(0, hook.MarkCount);

                // Check Spelling scans, then selects the first issue and opens its fixes.
                await vm.Spelling.CheckSpellingCommand.Execute();
                await Until(() => hook.Menu is not null, "the fixes menu");
                Assert.Equal("quikc", textEditor.SelectedText);
                var items = hook.Menu!.ItemsSource!.OfType<MenuItem>().ToList();
                Assert.Contains(items, i => (i.Header as string)?.StartsWith("Add “quikc”") == true);
                Assert.Contains(items, i => (i.Header as string) == "Ignore “quikc”");

                var quick = items.First(i => (i.Header as string) == "quick");
                quick.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("The quick cat sat.", textEditor.Text);
                Assert.Equal("The quick cat sat.", vm.Text);
                await Until(() => vm.Spelling.Scan.Issues.Count == 0, "the fixed text to be rechecked");

                // Focus Mode can hide the marks without forgetting them.
                hook.MarksVisible = false;
                Assert.False(hook.MarksVisible);
            }
            finally
            {
                hook.Menu?.Close();
                window.Close();
                vm.Dispose();
                try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
            }

            return 0;
        }, TimeSpan.FromSeconds(30));
    }
}

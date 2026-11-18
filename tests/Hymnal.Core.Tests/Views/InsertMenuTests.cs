using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using System.Reactive.Linq;
using AvaloniaEdit;
using ReactiveUI;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Markua.Editing;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

/// <summary>Insert shortcuts in a real editor in a headless window (the menu itself: ShellMenuTests).</summary>
[Collection("AvaloniaUi")]
public sealed class InsertMenuTests
{
    static InsertMenuTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Shortcuts_style_the_selection_ahead_of_the_window_and_AvaloniaEdit_and_undo_in_one_step()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hymnal-insert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "chapter.md");
        await File.WriteAllTextAsync(file, "a word here");
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
                var windowCtrlB = 0;
                var view = new EditorView { DataContext = vm };
                var window = new Window { Width = 900, Height = 600, Content = view };
                window.KeyBindings.Add(new KeyBinding
                {
                    // As MainWindow's ToggleSidebarShortcutCommand: it stands down while the editor takes the key.
                    Gesture = new KeyGesture(Key.B, KeyModifiers.Control),
                    Command = ReactiveUI.ReactiveCommand.Create(() => { windowCtrlB++; },
                        vm.WhenAnyValue(x => x.TakesInsertShortcuts).Select(takes => !takes)),
                });
                window.Show();
                await Pump();

                var editor = view.FindControl<TextEditor>("PART_Editor")!;
                Assert.Equal("a word here", editor.Text);
                Assert.True(vm.CanInsertMarkua);
                window.Activate();
                window.UpdateLayout();
                await Pump();
                editor.TextArea.Focus();
                await Pump();
                Assert.True(editor.TextArea.IsFocused, $"focused: {window.FocusManager?.GetFocusedElement()}");
                Assert.True(vm.TakesInsertShortcuts);
                editor.Select(2, 4);

                Press(window, Key.B, RawInputModifiers.Control);
                await Pump();
                Assert.Equal("a **word** here", editor.Text);
                Assert.Equal("a **word** here", vm.Text);
                Assert.Equal("word", editor.SelectedText);
                Assert.Equal(0, windowCtrlB);

                Press(window, Key.I, RawInputModifiers.Control); // AvaloniaEdit would indent
                await Pump();
                Assert.Equal("a ***word*** here", editor.Text);

                editor.Undo();
                await Pump();
                Assert.Equal("a **word** here", editor.Text);

                // Preview only: the shortcut falls through to the window again.
                vm.ViewMode = EditorViewMode.Preview;
                await Pump();
                Assert.False(vm.CanInsertMarkua);
                window.Focus();
                Press(window, Key.B, RawInputModifiers.Control);
                await Pump();
                Assert.Equal(1, windowCtrlB);
                Assert.Equal("a **word** here", vm.Text);

                window.Close();
                return 0;
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    private static async Task Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}

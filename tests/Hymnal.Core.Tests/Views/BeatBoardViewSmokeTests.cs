using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Services;
using Hymnal.Core.Tests.Infrastructure;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using Xunit;

namespace Hymnal.Core.Tests.Views;

[Collection("AvaloniaUi")]
public sealed class BeatBoardViewSmokeTests
{
    static BeatBoardViewSmokeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task BeatBoardView_shows_template_picker_when_empty_and_cards_after_applying()
    {
        var root = CreateStory();
        try
        {
            await Ui(root, 2, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var view = new BeatBoardView { DataContext = context.Board };
                LayoutView(view);

                var picker = view.FindControl<ScrollViewer>("TemplatePickerPanel")!;
                var beats = view.FindControl<ListBox>("BeatList")!;
                Assert.True(picker.IsVisible);
                Assert.False(beats.IsVisible);
                Assert.Same(context.Board.TemplateOptions, view.FindControl<ItemsControl>("TemplatePicker")!.ItemsSource);
                Assert.Same(context.Board.Beats, beats.ItemsSource);

                await Within(context.Board.ApplyTemplateCommand
                    .Execute(new ApplyTemplateRequest("threeAct", TemplateApplyMode.Append, null)).ToTask());
                LayoutView(view);

                Assert.False(picker.IsVisible);
                Assert.True(beats.IsVisible);
                Assert.Equal(5, beats.ItemCount);
                Assert.False(view.FindControl<Border>("ReadOnlyBanner")!.IsVisible);

                // Apply template shows the same gallery in place of the beats.
                var apply = view.FindControl<Button>("ApplyTemplateButton")!;
                Assert.True(apply.IsVisible);
                await Within(context.Board.ChooseTemplateCommand.Execute().ToTask());
                LayoutView(view);
                Assert.True(picker.IsVisible);
                Assert.False(beats.IsVisible);
                Assert.False(apply.IsVisible);
                Assert.True(view.FindControl<Button>("BackToBeatsButton")!.IsVisible);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task PlanHostView_shows_board_with_files_strip_and_hides_breadcrumb_for_a_story()
    {
        var root = CreateStory();
        try
        {
            await Ui(root, 2, async context =>
            {
                var host = context.CreateHost();
                await PumpUntilAsync(() => context.Board.BoardKey == BeatBoardService.ShortStoryBoardKey);

                var view = new PlanHostView { DataContext = host };
                LayoutView(view);

                Assert.Same(host.Content, view.FindControl<ContentControl>("PlanContent")!.Content);
                Assert.False(view.FindControl<Border>("Breadcrumb")!.IsVisible);
                var strip = view.FindControl<Expander>("FilesStrip")!;
                Assert.True(strip.IsVisible);
                Assert.False(strip.IsExpanded);

                host.IsFilesStripExpanded = true;
                LayoutView(view);
                Assert.True(strip.IsExpanded);
                Assert.Same(host.Corkboard, view.FindControl<ContentControl>("FilesContent")!.Content);
                Assert.Same(host.FilesStripContent, view.FindControl<ContentControl>("FilesContent")!.Content);
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public void BeatBoardView_Axaml_declares_editing_affordances()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/BeatBoardView.axaml"));

        Assert.Contains("x:Class=\"Hymnal.Views.BeatBoardView\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding Selected, Mode=TwoWay}\"", axaml);
        Assert.Contains("ItemsSource=\"{Binding SceneChoices}\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedScene, Mode=TwoWay}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding IsSceneMissing}\"", axaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedParameter, Mode=TwoWay}\"", axaml);
        Assert.Contains("CommandParameter=\"{Binding AppendRequest}\"", axaml);
        Assert.Contains("CommandParameter=\"{Binding ReplaceRequest}\"", axaml);
        Assert.Contains("Command=\"{Binding AddBeatCommand}\"", axaml);
        Assert.Contains("Loaded=\"BeatCard_Loaded\"", axaml);
        Assert.Contains("Text=\"Apply template…\"", axaml);
        Assert.Contains("Command=\"{Binding ChooseTemplateCommand}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding CanAppend}\"", axaml);
        Assert.Contains("<controls:BeatShapeThumbnail Levels=\"{Binding Shape}\" />", axaml);
        Assert.DoesNotContain("<Button.Flyout>", axaml);
    }

    [Fact]
    public void CorkboardView_Axaml_offers_beats_and_open_in_anthology()
    {
        var axaml = File.ReadAllText(FindRepositoryFile("src/Hymnal/Views/CorkboardView.axaml"));

        Assert.Contains("Command=\"{Binding DataContext.ShowBeatsCommand, ElementName=Root}\"", axaml);
        Assert.Contains("Command=\"{Binding DataContext.OpenInEditorCommand, ElementName=Root}\"", axaml);
        Assert.Contains("IsVisible=\"{Binding DataContext.IsAnthology, ElementName=Root}\"", axaml);
        Assert.Contains("DoubleTapped=\"PartHeader_DoubleTapped\"", axaml);
        Assert.Contains("Click=\"PartHeader_Click\"", axaml);
        Assert.Contains("IsVisible=\"{Binding ShowsBeatsAction}\"", axaml);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void LayoutView(Control view)
    {
        view.ApplyTemplate();
        view.Measure(new Size(1280, 800));
        view.Arrange(new Rect(0, 0, 1280, 800));
        view.UpdateLayout();
    }

    private static string CreateStory() =>
        CreateWorkspace(StoryJson,
            ("scene-one.md", "# Scene One\n\nDusk.\n"),
            ("scene-two.md", "# Scene Two\n\nDark.\n"));

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}

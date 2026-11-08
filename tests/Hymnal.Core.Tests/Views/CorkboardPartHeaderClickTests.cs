using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.Views;
using static Hymnal.Core.Tests.Infrastructure.PlanTestHarness;

namespace Hymnal.Core.Tests.Views;

/// <summary>Real pointer input on a Corkboard Part header: click toggles, an anthology double-click opens beats only.</summary>
[Collection("AvaloniaUi")]
public sealed class CorkboardPartHeaderClickTests
{
    static CorkboardPartHeaderClickTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public async Task Novel_single_click_toggles_the_part_at_once()
    {
        var root = CreateWorkspace(NovelJson,
            ("saga/part.md", "{class: part}\n# The Saga\n"),
            ("saga/one.md", "# One\n\nBegin.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var (host, part, header) = await ShowAsync(context);
                var expanded = part.IsExpanded;
                try
                {
                    Click(host, header);
                    Dispatcher.UIThread.RunJobs();

                    Assert.NotEqual(expanded, part.IsExpanded);
                }
                finally { host.Close(); }
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_double_click_opens_beats_without_toggling()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("saga/part.md", "{class: part}\n# The Saga\n"),
            ("saga/one.md", "# One\n\nBegin.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var beats = new List<ChapterViewModel>();
                using var requests = context.Corkboard.BeatsRequested.Subscribe(beats.Add);
                var (host, part, header) = await ShowAsync(context);
                var expanded = part.IsExpanded;
                try
                {
                    Click(host, header);
                    Click(host, header);
                    await PumpUntilAsync(() => beats.Count > 0);
                    // Outlast the double-click time, so a stray pending toggle would have fired.
                    await PumpForAsync(GetDoubleTapTime() * 3);

                    Assert.Equal(expanded, part.IsExpanded);
                    Assert.Equal("saga/part.md", Assert.Single(beats).Node.RelativePath);
                }
                finally { host.Close(); }
            });
        }
        finally { DeleteFolder(root); }
    }

    [Fact]
    public async Task Anthology_single_click_toggles_after_the_double_click_time()
    {
        var root = CreateWorkspace(AnthologyJson,
            ("saga/part.md", "{class: part}\n# The Saga\n"),
            ("saga/one.md", "# One\n\nBegin.\n"));
        try
        {
            await Ui(root, 2, async context =>
            {
                var (host, part, header) = await ShowAsync(context);
                var expanded = part.IsExpanded;
                try
                {
                    Click(host, header);

                    await PumpUntilAsync(() => part.IsExpanded != expanded);
                }
                finally { host.Close(); }
            });
        }
        finally { DeleteFolder(root); }
    }

    private static async Task<(Window Host, PartDividerItemViewModel Part, Button Header)> ShowAsync(PlanTestContext context)
    {
        var view = new CorkboardView { DataContext = context.Corkboard };
        var host = new Window { Content = view, Width = 1280, Height = 800 };
        host.Show();
        // Lets the board finish rebuilding, and waits out the double-click time: the headless mouse is
        // shared across tests, so a previous test's clicks must not count toward this test's first press.
        await PumpForAsync(GetDoubleTapTime() + TimeSpan.FromMilliseconds(100));
        var part = await context.PartDividerAsync("saga/part.md");
        Button? header = null;
        await PumpUntilAsync(() => (header = view.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, part))) is not null);
        return (host, part, header!);
    }

    private static async Task PumpForAsync(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static TimeSpan GetDoubleTapTime() =>
        Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);

    private static void Click(Window host, Button header)
    {
        var point = header.TranslatePoint(new Point(40, header.Bounds.Height / 2), host)!.Value;
        host.MouseDown(point, MouseButton.Left);
        host.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}

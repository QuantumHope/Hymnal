using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Infrastructure;

namespace Hymnal.ViewModels;

/// <summary>The palette's own behaviour, with hand-made entries.</summary>
public sealed class CommandPaletteViewModelTests
{
    static CommandPaletteViewModelTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    private readonly List<string> _ran = new();

    private PaletteEntry Command(string title, string category = "View", string? shortcut = null) =>
        new($"command:{title}", PaletteEntryKind.Command, title, category, null, shortcut,
            () => { _ran.Add(title); return Task.CompletedTask; });

    private PaletteEntry Place(string title, string category = "Chapter", string? detail = null) =>
        new($"node:{title}", PaletteEntryKind.Destination, title, category, detail, null,
            () => { _ran.Add(title); return Task.CompletedTask; });

    private static IEnumerable<string> Titles(CommandPaletteViewModel vm) => vm.Results.Select(r => r.Title);

    [Fact]
    public void Go_to_lists_places_in_order_and_commands_mode_lists_commands()
    {
        using var vm = new CommandPaletteViewModel(() =>
            [Command("Focus Mode"), Place("Arrival"), Place("The Harbour"), Command("Save")]);

        vm.OpenGoTo();
        Assert.True(vm.IsOpen);
        Assert.False(vm.IsCommandMode);
        Assert.Equal(["Arrival", "The Harbour"], Titles(vm));
        Assert.Equal("Arrival", vm.Selected?.Title);

        vm.Query = ">";
        Assert.True(vm.IsCommandMode);
        Assert.Equal(["Focus Mode", "Save"], Titles(vm));

        vm.OpenCommands();
        Assert.Equal(">", vm.Query);
        Assert.Equal(["Focus Mode", "Save"], Titles(vm));
    }

    [Fact]
    public void Typing_filters_and_ranks_the_best_match_first()
    {
        using var vm = new CommandPaletteViewModel(() =>
            [Place("Reset the Harbour"), Place("Harbour"), Place("Lighthouse", detail: "part-two/harbour-night.md")]);

        vm.OpenGoTo();
        vm.Query = "harb";

        Assert.Equal(["Harbour", "Reset the Harbour", "Lighthouse"], Titles(vm));
        Assert.Equal("Harbour", vm.Selected?.Title);

        vm.Query = "zzz";
        Assert.True(vm.HasNoResults);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public void Selection_moves_and_wraps()
    {
        using var vm = new CommandPaletteViewModel(() => [Place("A"), Place("B"), Place("C")]);
        vm.OpenGoTo();

        vm.MoveSelection(1);
        Assert.Equal("B", vm.Selected?.Title);
        vm.MoveSelection(2);
        Assert.Equal("A", vm.Selected?.Title);
        vm.MoveSelection(-1);
        Assert.Equal("C", vm.Selected?.Title);
    }

    [Fact]
    public async Task Running_the_selection_closes_the_palette_first()
    {
        CommandPaletteViewModel? palette = null;
        var openWhileRunning = true;
        using var vm = palette = new CommandPaletteViewModel(() =>
        [
            new PaletteEntry("command:x", PaletteEntryKind.Command, "X", "View", null, null, () =>
            {
                openWhileRunning = palette!.IsOpen;
                return Task.CompletedTask;
            }),
        ]);

        vm.OpenCommands();
        await vm.RunSelectedCommand.Execute().ToTask();

        Assert.False(openWhileRunning);
        Assert.False(vm.IsOpen);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Recently_run_commands_come_first_when_nothing_is_typed()
    {
        using var vm = new CommandPaletteViewModel(() => [Command("Alpha"), Command("Beta"), Command("Gamma")]);

        vm.OpenCommands();
        vm.Query = ">gam";
        await vm.RunSelectedCommand.Execute().ToTask();
        vm.OpenCommands();
        vm.Query = ">bet";
        await vm.RunSelectedCommand.Execute().ToTask();

        vm.OpenCommands();
        Assert.Equal(["Beta", "Gamma", "Alpha"], Titles(vm));
        Assert.Equal(["Gamma", "Beta"], _ran);
    }

    [Fact]
    public void Entries_are_read_again_each_time_it_opens()
    {
        var titles = new List<string> { "First" };
        using var vm = new CommandPaletteViewModel(() => titles.Select(t => Place(t)).ToList());

        vm.OpenGoTo();
        Assert.Equal(["First"], Titles(vm));
        vm.Close();

        titles.Add("Second");
        vm.OpenGoTo();
        Assert.Equal(["First", "Second"], Titles(vm));
    }

    [Fact]
    public async Task A_failing_entry_is_reported_not_thrown()
    {
        var notifications = new NotificationService();
        var messages = new List<Notification>();
        using var log = notifications.Notifications.Subscribe(messages.Add);
        using var vm = new CommandPaletteViewModel(() =>
        [
            new PaletteEntry("command:boom", PaletteEntryKind.Command, "Boom", "View", null, null,
                () => throw new InvalidOperationException("no luck")),
        ], notifications);

        vm.OpenCommands();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunSelectedCommand.Execute().ToTask());

        Assert.Contains(messages, m => m.Kind == NotificationKind.Error && m.Message.Contains("no luck"));
    }
}

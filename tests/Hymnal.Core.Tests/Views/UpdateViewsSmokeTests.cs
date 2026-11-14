using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Hymnal.Core.Tests.Infrastructure;
using Hymnal.Core.Tests.TestDoubles;
using Hymnal.Core.Tests.Updates;
using Hymnal.Infrastructure.Updates;
using Hymnal.Views;

namespace Hymnal.Core.Tests.Views;

[Collection("AvaloniaUi")]
public sealed class UpdateViewsSmokeTests
{
    static UpdateViewsSmokeTests() => ReactiveUiTestBootstrap.EnsureInitialized();

    [Fact]
    public void MainWindow_and_Settings_host_the_update_views()
    {
        var main = File.ReadAllText(HymnalUpdatesTests.RepositoryFile("src/Hymnal/Views/MainWindow.axaml"));
        Assert.Contains("<views:UpdateBannerView DataContext=\"{Binding Updates}\"", main);
        Assert.Contains("DataContext=\"{Binding Updates}\"", main);
        Assert.Contains("Command=\"{Binding Updates.CheckCommand}\"", main);
        // The banner sits above the shell, below the accent stripe.
        Assert.True(main.IndexOf("UpdateBannerView", StringComparison.Ordinal) > main.IndexOf("Accent band (Row 1", StringComparison.Ordinal));

        var settings = File.ReadAllText(HymnalUpdatesTests.RepositoryFile("src/Hymnal/Views/SettingsView.axaml"));
        Assert.Contains("<views:UpdatesSettingsView DataContext=\"{Binding Updates}\"", settings);
        Assert.Contains("CommandParameter=\"{x:Static vm:SettingsSection.Updates}\"", settings);
    }

    [Fact]
    public async Task Banner_shows_an_available_update_and_hides_once_dismissed()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            var host = new InMemoryUpdateHost();
            using var vm = HymnalUpdates.CreateViewModel(new FakeUpdateService(), host, () => false);
            var view = new UpdateBannerView { DataContext = vm };
            LayoutView(view);
            var banner = view.FindControl<Border>("PART_UpdateBanner")!;
            Assert.False(banner.IsVisible);

            using var withUpdate = HymnalUpdates.CreateViewModel(
                new FakeUpdateService { Offer = FakeUpdateService.SampleOffer() }, host, () => false);
            await withUpdate.CheckAsync(userAsked: false);
            view.DataContext = withUpdate;
            LayoutView(view);

            Assert.True(banner.IsVisible);
            Assert.Equal("Hymnal 0.1.42 is available", view.FindControl<TextBlock>("PART_BannerTitle")!.Text);
            Assert.Equal("Install 0.1.42 and restart", view.FindControl<Button>("PART_InstallButton")!.Content);

            await withUpdate.DismissCommand.Execute().ToTask();
            LayoutView(view);
            Assert.False(banner.IsVisible);
            Assert.Equal("0.1.42", host.Preferences[MattEland.HopeLab.Updates.ReactiveUI.UpdatesViewModel.DismissedKey]);
            return 0;
        }, TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task Settings_section_shows_version_status_and_install()
    {
        await ReactiveUiTestBootstrap.RunOnUiThreadAsync(async () =>
        {
            using var vm = HymnalUpdates.CreateViewModel(
                new FakeUpdateService { Offer = FakeUpdateService.SampleOffer() }, new InMemoryUpdateHost(), () => false);
            var view = new UpdatesSettingsView { DataContext = vm };
            LayoutView(view);
            Assert.False(view.FindControl<Button>("PART_SettingsInstallButton")!.IsVisible);

            await vm.CheckAsync(userAsked: false);
            LayoutView(view);

            Assert.Equal("Version 0.1.0", view.FindControl<TextBlock>("PART_CurrentVersion")!.Text);
            Assert.Equal("Hymnal 0.1.42 is available (you have 0.1.0).", view.FindControl<TextBlock>("PART_StatusText")!.Text);
            Assert.True(view.FindControl<Button>("PART_SettingsInstallButton")!.IsVisible);
            Assert.StartsWith("No GitHub access", view.FindControl<TextBlock>("PART_AccessText")!.Text);
            return 0;
        }, TimeSpan.FromSeconds(40));
    }

    private static void LayoutView(Control view)
    {
        view.ApplyTemplate();
        view.Measure(new Size(1280, 800));
        view.Arrange(new Rect(0, 0, 1280, 800));
        view.UpdateLayout();
    }
}

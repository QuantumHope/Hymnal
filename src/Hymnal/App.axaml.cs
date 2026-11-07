using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Infrastructure.Ai;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Services;
using Hymnal.Core.Services.Ai;
using Hymnal.Infrastructure;
using Hymnal.ViewModels;
using Hymnal.ViewModels.Ai;
using Hymnal.Views;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hymnal;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }
    private ServiceProvider? _serviceProvider;
    private bool _servicesDisposed;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

        // Platform credential store stub — real impl deferred to a future milestone
        services.AddSingleton<ICredentialStore, CredentialStoreStub>();

        // S02 services
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<ManuscriptService>();

        // S03 services
        services.AddSingleton<IProcessRunner, GitProcessRunner>();
        services.AddSingleton<IGitService, ProcessGitService>();
        services.AddSingleton<IMetadataStore, MetadataStore>();

        // M002/S01 services — chapter registry and phase data
        services.AddSingleton<ChapterRegistryService>();
        services.AddSingleton<PhaseDataService>();

        // M002/S02 services — word count, targets, history
        services.AddSingleton<WordCountService>();
        services.AddSingleton<TargetsService>();
        services.AddSingleton<WordCountHistoryService>();

        // S04 services
        services.AddSingleton<INotesService, NotesService>();

        // M005/S01 services — persistent exclusion manifest
        services.AddSingleton<IExclusionManifestService, ExclusionManifestService>();

        // S01 services — plan corkboard structural editing
        services.AddSingleton<IBookTxtStructureService, BookTxtStructureService>();
        services.AddSingleton<IOrphanFileDiscoveryService, OrphanFileDiscoveryService>();

        // Story ideas (story/anthology projects)
        services.AddSingleton<IStoryIdeasService, StoryIdeasService>();
        services.AddSingleton<StoryIdeaPromoter>();

        // Beat boards (story/anthology plan mode)
        services.AddSingleton<IBeatBoardService, BeatBoardService>();

        // M004/S02 services — supplemental docs sidebar tree
        services.AddSingleton<ISupplementalDocsService, SupplementalDocsService>();

        // M006 — AI platform services
        // IChatClient factory: builds a provider-specific client from a ProviderProfile + API key
        services.AddSingleton<Func<Hymnal.Core.Models.Ai.ProviderProfile, string, IChatClient>>(sp =>
            (profile, apiKey) => ChatClientFactory.Create(profile, apiKey));

        services.AddSingleton<IRolePromptProvider, RolePromptProvider>();
        services.AddSingleton<IManuscriptContextReader, ManuscriptContextReader>();
        services.AddSingleton<WriteContextBuilder>(sp =>
            new WriteContextBuilder(sp.GetRequiredService<IManuscriptContextReader>()));
        services.AddSingleton<PlanContextBuilder>(sp =>
            new PlanContextBuilder(sp.GetRequiredService<IManuscriptContextReader>()));
        services.AddSingleton<ManageContextBuilder>(sp =>
            new ManageContextBuilder(sp.GetRequiredService<IManuscriptContextReader>()));

        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IProviderProfileService>(sp =>
            new ProviderProfileService(
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<ICredentialStore>(),
                sp.GetRequiredService<Func<Hymnal.Core.Models.Ai.ProviderProfile, string, IChatClient>>()));
        services.AddSingleton<IAiChatService>(sp =>
            new AiChatService(
                sp.GetRequiredService<IProviderProfileService>(),
                sp.GetRequiredService<ICredentialStore>(),
                sp.GetRequiredService<Func<Hymnal.Core.Models.Ai.ProviderProfile, string, IChatClient>>()));

        // S03 view-models (order matters: EditorViewModel before WorkspaceViewModel)
        services.AddSingleton<EditorViewModel>(sp =>
            new EditorViewModel(
                sp.GetRequiredService<IMetadataStore>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<WordCountService>()));

        services.AddSingleton<IFolderPickerService>(sp =>
            new FolderPickerService(() =>
                App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dl
                    ? TopLevel.GetTopLevel(dl.MainWindow)
                    : null));

        services.AddSingleton<IFilePickerService>(sp =>
            new FilePickerService(() =>
                App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dl
                    ? TopLevel.GetTopLevel(dl.MainWindow)
                    : null));

        services.AddSingleton<ProjectSettingsService>();
        services.AddSingleton<ProjectInitializer>();
        services.AddSingleton<IProjectInitPrompt>(sp =>
            new ProjectInitPrompt(() =>
                App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dl
                    ? dl.MainWindow
                    : null));

        services.AddSingleton<WorkspaceViewModel>(sp =>
            new WorkspaceViewModel(
                sp.GetRequiredService<ManuscriptService>(),
                sp.GetRequiredService<IBookTxtStructureService>(),
                sp.GetRequiredService<IFilePickerService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<IFolderPickerService>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<ChapterRegistryService>(),
                sp.GetRequiredService<PhaseDataService>(),
                sp.GetRequiredService<TargetsService>(),
                sp.GetRequiredService<WordCountService>(),
                sp.GetRequiredService<WordCountHistoryService>(),
                sp.GetRequiredService<IExclusionManifestService>(),
                sp.GetRequiredService<IOrphanFileDiscoveryService>(),
                sp.GetRequiredService<ProjectSettingsService>(),
                sp.GetRequiredService<ProjectInitializer>(),
                sp.GetRequiredService<IProjectInitPrompt>()));

        services.AddSingleton<NotesViewModel>(sp =>
            new NotesViewModel(
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<INotesService>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IAppSettingsStore>()));

        services.AddSingleton<ChapterInfoViewModel>(sp =>
            new ChapterInfoViewModel(
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<PhaseDataService>(),
                sp.GetRequiredService<TargetsService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<WordCountHistoryService>()));

        services.AddSingleton<GanttViewModel>(sp =>
            new GanttViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<PhaseDataService>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<CorkboardViewModel>(sp =>
            new CorkboardViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<IBookTxtStructureService>(),
                sp.GetRequiredService<IOrphanFileDiscoveryService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<ManuscriptService>()));

        services.AddSingleton<ResearchViewModel>(sp =>
            new ResearchViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<SupplementalDocsViewModel>(),
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<AiChatViewModel>()));

        services.AddSingleton<SupplementalDocsViewModel>(sp =>
            new SupplementalDocsViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<ISupplementalDocsService>(),
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<IFilePickerService>()));

        services.AddSingleton<GitPanelViewModel>(sp =>
            new GitPanelViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<IGitService>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<BookStatsViewModel>(sp =>
            new BookStatsViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<WordCountHistoryService>(),
                sp.GetRequiredService<TargetsService>()));

        services.AddSingleton<IdeasViewModel>(sp =>
            new IdeasViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<IStoryIdeasService>(),
                sp.GetRequiredService<StoryIdeaPromoter>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<BeatBoardViewModel>(sp =>
            new BeatBoardViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<IBeatBoardService>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<PlanHostViewModel>(sp =>
            new PlanHostViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<CorkboardViewModel>(),
                sp.GetRequiredService<BeatBoardViewModel>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IBeatBoardService>()));

        // M006 — AI chat ViewModels (must be registered after Editor/Workspace VMs)
        services.AddSingleton<ConversationListViewModel>(sp =>
            new ConversationListViewModel(
                sp.GetRequiredService<IConversationStore>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<AiChatViewModel>(sp =>
            new AiChatViewModel(
                sp.GetRequiredService<IAiChatService>(),
                sp.GetRequiredService<IConversationStore>(),
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IRolePromptProvider>(),
                sp.GetRequiredService<WriteContextBuilder>(),
                sp.GetRequiredService<PlanContextBuilder>(),
                sp.GetRequiredService<ManageContextBuilder>(),
                sp.GetRequiredService<ConversationListViewModel>()));

        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<IProviderProfileService>(),
                sp.GetRequiredService<ICredentialStore>(),
                sp.GetRequiredService<IAiChatService>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<ProjectSettingsService>(),
                sp.GetRequiredService<IBeatBoardService>(),
                sp.GetRequiredService<BeatBoardViewModel>()));

        services.AddTransient<MainWindowViewModel>(sp =>
            new MainWindowViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<NotesViewModel>(),
                sp.GetRequiredService<ChapterInfoViewModel>(),
                sp.GetRequiredService<BookStatsViewModel>(),
                sp.GetRequiredService<GanttViewModel>(),
                sp.GetRequiredService<CorkboardViewModel>(),
                sp.GetRequiredService<ResearchViewModel>(),
                sp.GetRequiredService<SupplementalDocsViewModel>(),
                sp.GetRequiredService<GitPanelViewModel>(),
                sp.GetRequiredService<NotificationService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<AiChatViewModel>(),
                () => sp.GetRequiredService<SettingsViewModel>(),
                sp.GetRequiredService<IdeasViewModel>(),
                sp.GetRequiredService<PlanHostViewModel>()));

        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => DisposeServices();
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisposeServices()
    {
        if (_servicesDisposed)
            return;

        _servicesDisposed = true;
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        Services = null;
    }
}

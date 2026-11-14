using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hymnal.Audio;
using Hymnal.Core.Audio;
using Hymnal.Core.Infrastructure;
using Hymnal.Core.Infrastructure.Ai;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Services;
using Hymnal.Core.Submission;
using Hymnal.Core.Services.Ai;
using Hymnal.Infrastructure;
using Hymnal.Infrastructure.Updates;
using Hymnal.Theming;
using Hymnal.ViewModels;
using Hymnal.ViewModels.Ai;
using Hymnal.Views;
using Microsoft.Extensions.AI;
using MattEland.HopeLab.Updates;
using MattEland.HopeLab.Updates.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

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
        // Themes: the picker in Settings swaps them at runtime; the choice is kept in the app settings
        services.AddSingleton(sp => new ThemeManager(new ThemePreference(sp.GetRequiredService<IAppSettingsStore>())));
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeManager>());
        services.AddSingleton<ManuscriptService>();

        // Sound: preferences, the audio output (opened only while a sound is on) and the typewriter
        services.AddSingleton<SoundSettingsService>();
        services.AddSingleton<SoundPreferences>();
        services.AddSingleton<IAudioEngine, OpenAlAudioEngine>();
        services.AddSingleton<TypingSoundPlayer>(sp => new TypingSoundPlayer(
            sp.GetRequiredService<IAudioEngine>(),
            sp.GetRequiredService<SoundPreferences>(),
            ThemeSoundSchemes.SynthwaveThemeId));

        // S03 services
        services.AddSingleton<IProcessRunner, GitProcessRunner>();
        services.AddSingleton<IGitService, ProcessGitService>();
        services.AddSingleton<IMetadataStore, MetadataStore>();
        // The single writer of story .md files with their metadata block (the editor save path uses it)
        services.AddSingleton<StoryFileWriter>();

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
        // Writes story files through the same StoryFileWriter singleton the editor uses.
        services.AddSingleton<StorageMigrator>();

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
        {
            var storyFileWriter = sp.GetRequiredService<StoryFileWriter>();
            var editor = new EditorViewModel(
                sp.GetRequiredService<IMetadataStore>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<WordCountService>(),
                storyFileWriter);
            // Metadata changes for the chapter open in the editor are saved through it, keeping unsaved prose.
            storyFileWriter.AttachBuffer(editor);
            return editor;
        });

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

        services.AddSingleton<ISaveFilePickerService>(sp => (FilePickerService)sp.GetRequiredService<IFilePickerService>());

        services.AddSingleton<SubmissionExportService>();
        services.AddSingleton<SubmissionExportViewModel>(sp =>
            new SubmissionExportViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<SubmissionExportService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<ISaveFilePickerService>(),
                sp.GetRequiredService<INotificationService>(),
                async () =>
                {
                    var notes = await sp.GetRequiredService<NotesViewModel>().FlushAsync();
                    var schedule = await sp.GetRequiredService<ChapterInfoViewModel>().FlushScheduleAsync();
                    var writeBeats = await sp.GetRequiredService<WriteBeatsViewModel>().FlushAsync();
                    return notes && schedule && writeBeats;
                },
                sp.GetRequiredService<EditorViewModel>()));

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
                sp.GetRequiredService<IProjectInitPrompt>(),
                sp.GetRequiredService<INotesService>(),
                sp.GetRequiredService<IBeatBoardService>(),
                sp.GetRequiredService<CollectionService>()));

        services.AddSingleton<NotesViewModel>(sp =>
            new NotesViewModel(
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IAppSettingsStore>()));

        services.AddSingleton<ChapterInfoViewModel>(sp =>
            new ChapterInfoViewModel(
                sp.GetRequiredService<EditorViewModel>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<WordCountHistoryService>()));

        services.AddSingleton<GanttViewModel>(sp =>
            new GanttViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
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
                sp.GetRequiredService<WordCountHistoryService>()));

        services.AddSingleton<IdeasViewModel>(sp =>
            new IdeasViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<IStoryIdeasService>(),
                sp.GetRequiredService<StoryIdeaPromoter>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<BeatBoardViewModel>(sp =>
            new BeatBoardViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<CollectionService>(sp => new CollectionService(sp.GetRequiredService<IMetadataStore>()));
        services.AddSingleton<CollectionsViewModel>(sp =>
            new CollectionsViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<CollectionService>(),
                sp.GetRequiredService<INotificationService>(),
                async () =>
                {
                    var notes = await sp.GetRequiredService<NotesViewModel>().FlushAsync();
                    var schedule = await sp.GetRequiredService<ChapterInfoViewModel>().FlushScheduleAsync();
                    var beats = await sp.GetRequiredService<PlanHostViewModel>().FlushAsync();
                    var writeBeats = await sp.GetRequiredService<WriteBeatsViewModel>().FlushAsync();
                    return notes && schedule && beats && writeBeats;
                },
                sp.GetRequiredService<EditorViewModel>()));

        // Write mode's Beats panel has its own board, so it never changes the one Plan shows.
        services.AddSingleton<WriteBeatsViewModel>(sp =>
            new WriteBeatsViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                new BeatBoardViewModel(
                    sp.GetRequiredService<WorkspaceViewModel>(),
                    sp.GetRequiredService<INotificationService>()),
                sp.GetRequiredService<BeatBoardViewModel>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                sp.GetRequiredService<INotificationService>()));

        services.AddSingleton<PlanHostViewModel>(sp =>
            new PlanHostViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<CorkboardViewModel>(),
                sp.GetRequiredService<BeatBoardViewModel>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IBeatBoardService>(),
                sp.GetRequiredService<CollectionPlannerViewModel>()));

        // An anthology's Plan mode: every collection side by side over a library of stories.
        services.AddSingleton<CollectionPlanService>(sp =>
            new CollectionPlanService(
                sp.GetRequiredService<IMetadataStore>(),
                sp.GetRequiredService<CollectionService>(),
                sp.GetRequiredService<WordCountService>()));
        services.AddSingleton<CollectionPlannerViewModel>(sp =>
            new CollectionPlannerViewModel(
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<CollectionPlanService>(),
                sp.GetRequiredService<ChapterRegistryService>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<CollectionsViewModel>(),
                async () =>
                {
                    var notes = await sp.GetRequiredService<NotesViewModel>().FlushAsync();
                    var schedule = await sp.GetRequiredService<ChapterInfoViewModel>().FlushScheduleAsync();
                    var beats = await sp.GetRequiredService<PlanHostViewModel>().FlushAsync();
                    var writeBeats = await sp.GetRequiredService<WriteBeatsViewModel>().FlushAsync();
                    return notes && schedule && beats && writeBeats;
                },
                sp.GetRequiredService<EditorViewModel>()));

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

        // Releases and in-app updates (hopelab-app.json; the shared MattEland.HopeLab.Updates libraries).
        services.AddSingleton<IUpdateService>(sp => HymnalUpdates.CreateService(sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<UpdatesViewModel>(sp => HymnalUpdates.CreateViewModel(
            sp.GetRequiredService<IUpdateService>(),
            new HymnalUpdateHost(
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IAppSettingsStore>(),
                OpenUrlAsync),
            () => sp.GetRequiredService<EditorViewModel>().IsDirty));

        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<IProviderProfileService>(),
                sp.GetRequiredService<ICredentialStore>(),
                sp.GetRequiredService<IAiChatService>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<WorkspaceViewModel>(),
                sp.GetRequiredService<ProjectSettingsService>(),
                sp.GetRequiredService<IBeatBoardService>(),
                sp.GetRequiredService<BeatBoardViewModel>(),
                sp.GetRequiredService<StorageMigrator>(),
                async () =>
                {
                    var notes = await sp.GetRequiredService<NotesViewModel>().FlushAsync();
                    var schedule = await sp.GetRequiredService<ChapterInfoViewModel>().FlushScheduleAsync();
                    var writeBeats = await sp.GetRequiredService<WriteBeatsViewModel>().FlushAsync();
                    return notes && schedule && writeBeats;
                },
                sp.GetRequiredService<SoundPreferences>(),
                sp.GetRequiredService<TypingSoundPlayer>(),
                sp.GetRequiredService<IThemeService>(),
                sp.GetRequiredService<UpdatesViewModel>()));

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
                sp.GetRequiredService<PlanHostViewModel>(),
                sp.GetRequiredService<SoundPreferences>(),
                sp.GetRequiredService<WriteBeatsViewModel>(),
                sp.GetRequiredService<CollectionsViewModel>(),
                sp.GetRequiredService<SubmissionExportViewModel>(),
                sp.GetRequiredService<UpdatesViewModel>()));

        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // Start the typewriter now, so sounds turned on last session are ready by the first keystroke.
        Services.GetRequiredService<TypingSoundPlayer>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => DisposeServices();
            // The saved theme, before the first window draws
            Services.GetRequiredService<ThemeManager>().ApplySaved();
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            SetUpUpdates(Services, desktop, window, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Checks for updates once the window shows. An installed update saves everything, starts the new copy and exits
    /// the way File → Exit does; if anything can't be saved, Hymnal keeps running and says so.
    /// </summary>
    private static void SetUpUpdates(
        IServiceProvider services,
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel vm)
    {
        var updates = services.GetRequiredService<UpdatesViewModel>();
        var restart = new UpdateRestart(
            services.GetRequiredService<IUpdateService>(),
            () => vm.SaveAllBeforeRestartAsync(RestartSaveTimeout),
            () => desktop.TryShutdown(),
            services.GetRequiredService<INotificationService>(),
            vm.CancelRestart);
        updates.RestartRequested += async (_, executable) => await restart.RestartAsync(executable);

        void StartChecks(object? sender, EventArgs e)
        {
            window.Opened -= StartChecks;
            updates.Start();
        }
        window.Opened += StartChecks;
    }

    private static readonly TimeSpan RestartSaveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Opens a web page (release notes) in the default browser.</summary>
    private static async Task OpenUrlAsync(string url)
    {
        var window = (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Launcher is { } launcher && await launcher.LaunchUriAsync(new Uri(url)))
            return;

        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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

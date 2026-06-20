using Microsoft.Extensions.DependencyInjection;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.ViewModels;
using System.Windows;

namespace MirrorsEdgeTweaks
{
    // Interaction logic for App.xaml. Acts as the composition root: builds the DI
    // container, wires up the service layer and view models, and resolves the main window.
    public partial class App : System.Windows.Application
    {
        private IServiceProvider? _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            _services = services.BuildServiceProvider();

            var window = _services.GetRequiredService<MainWindow>();
            window.Show();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Service layer
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IFileService, FileService>();
            services.AddSingleton<IPackageService, PackageService>();
            services.AddSingleton<IDownloadService, DownloadService>();
            services.AddSingleton<IDecompressionService, DecompressionService>();
            services.AddSingleton<IOffsetFinderService, OffsetFinderService>();
            services.AddSingleton<IUIScalingService, UIScalingService>();
            services.AddSingleton<IGraphicsSettingsService, GraphicsSettingsService>();
            services.AddSingleton<IGameLauncher, GameLauncherService>();
            services.AddSingleton<ISettingsStore, SettingsStore>();
            services.AddSingleton<IAppSettingsService, AppSettingsService>();
            services.AddSingleton<IGameDataService, GameDataService>();
            services.AddSingleton<IFolderPickerService, FolderPickerService>();
            services.AddSingleton<IGameProcessMonitor, GameProcessMonitor>();

            // Shared state + view models
            services.AddSingleton<GameSession>();

            // Per-feature status view models (shared instances bound by the View and used by feature VMs)
            services.AddSingleton<GameStatusViewModel>();
            services.AddSingleton<ConsoleViewModel>();
            services.AddSingleton<TweaksScriptsViewModel>();
            services.AddSingleton<UnlockedConfigsViewModel>();
            services.AddSingleton<DownloadProgressViewModel>();
            services.AddSingleton<TdGameVersionViewModel>();

            services.AddSingleton<ModsViewModel>();
            services.AddSingleton<PatchesViewModel>();
            services.AddSingleton<AudioSettingsViewModel>();
            services.AddSingleton<GraphicsTweaksViewModel>();
            services.AddSingleton<InputSettingsViewModel>();
            services.AddSingleton<KeybindsViewModel>();
            services.AddSingleton<InitialisationSettingsViewModel>();
            services.AddSingleton<CommunityModsViewModel>();
            services.AddSingleton<LaunchArgumentsViewModel>();
            services.AddSingleton<LanguageSettingsViewModel>();
            services.AddSingleton<MainViewModel>();

            // Views
            services.AddSingleton<MainWindow>();
        }
    }
}

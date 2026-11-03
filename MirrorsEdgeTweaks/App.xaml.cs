using Microsoft.Extensions.DependencyInjection;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.ViewModels;
using System.IO;
using System.Windows;

namespace MirrorsEdgeTweaks
{
    public partial class App : System.Windows.Application
    {
        private const string SingleInstanceMutexName = "MirrorsEdgeTweaks_SingleInstance";

        private ServiceProvider? _services;
        private Mutex? _singleInstanceMutex;

        private static string CrashLogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorsEdgeTweaks", "logs");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // A second instance could corrupt the game files or metweaksconfig.ini by writing them alongside the first.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                System.Windows.MessageBox.Show(
                    "Mirror's Edge Tweaks is already running.\n\nSwitch to the existing window to continue.",
                    "Mirror's Edge Tweaks",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            RegisterGlobalExceptionHandlers();

            var services = new ServiceCollection();
            ConfigureServices(services);
            _services = services.BuildServiceProvider();

            var window = _services.GetRequiredService<MainWindow>();
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _services?.Dispose();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        private void RegisterGlobalExceptionHandlers()
        {
            // UI thread exceptions are logged and shown, and the app keeps running,
            // so a failed binding or command does not end a patching session.
            DispatcherUnhandledException += (_, args) =>
            {
                LogCrash("DispatcherUnhandledException", args.Exception);
                System.Windows.MessageBox.Show(
                    "Mirror's Edge Tweaks ran into an unexpected error. " +
                    $"A crash log with the details was written to:\n{CrashLogDirectory}",
                    "Mirror's Edge Tweaks - Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            // The process ends after an exception on another thread, so this can only log it.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogCrash("AppDomainUnhandledException", args.ExceptionObject as Exception);

            // Faulted tasks that nothing awaited are logged and marked as observed, so they cannot crash the process.
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogCrash("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };
        }

        private static void LogCrash(string source, Exception? exception)
        {
            try
            {
                Directory.CreateDirectory(CrashLogDirectory);
                string logPath = Path.Combine(CrashLogDirectory, "crash.log");
                string entry =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // Logging must never introduce its own failure path.
            }
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IFileService, FileService>();
            services.AddSingleton<IDownloadService, DownloadService>();
            services.AddSingleton<IAssetUrlProvider, AssetUrlProvider>();
            services.AddSingleton<IDecompressionService, DecompressionService>();
            services.AddSingleton<IOffsetFinderService, OffsetFinderService>();
            services.AddSingleton<IUIScalingService, UIScalingService>();
            services.AddSingleton<IGraphicsSettingsService, GraphicsSettingsService>();
            services.AddSingleton<ISteamService, SteamService>();
            services.AddSingleton<IGameLauncher, GameLauncherService>();
            services.AddSingleton<ISettingsStore, SettingsStore>();
            services.AddSingleton<IAppSettingsService, AppSettingsService>();
            services.AddSingleton<IGameDataService, GameDataService>();
            services.AddSingleton<ITweakSnapshotService, TweakSnapshotService>();
            services.AddSingleton<PendingReapplyStore>();
            services.AddSingleton<IFolderPickerService, FolderPickerService>();
            services.AddSingleton<IGameProcessMonitor, GameProcessMonitor>();

            services.AddSingleton<GameSession>();

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
            services.AddSingleton<LegacyRestoreViewModel>();
            services.AddSingleton<MainViewModel>();

            services.AddSingleton<MainWindow>();
        }
    }
}

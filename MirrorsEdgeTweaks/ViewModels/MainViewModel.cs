using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Brushes = System.Windows.Media.Brushes;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Root view model and shell orchestrator. Owns the shared GameSession, exposes the per-feature
    // child view models, drives startup / game-directory processing / settings persistence, and runs
    // the post-package-reload refresh fan-out. Bound as the window's DataContext.
    public partial class MainViewModel : ObservableObject
    {
        private readonly IGameDataService _gameData;
        private readonly IAppSettingsService _settings;
        private readonly IGameLauncher _gameLauncher;
        private readonly IDialogService _dialogService;
        private readonly IPackageService _packageService;
        private readonly IDecompressionService _decompressionService;
        private readonly IFileService _fileService;
        private readonly IFolderPickerService _folderPicker;

        public GameSession Session { get; }

        public GameStatusViewModel GameStatus { get; }
        public ConsoleViewModel Console { get; }
        public TweaksScriptsViewModel TweaksScripts { get; }
        public UnlockedConfigsViewModel UnlockedConfigs { get; }
        public DownloadProgressViewModel DownloadProgress { get; }
        public TdGameVersionViewModel TdGameVersion { get; }

        public ModsViewModel Mods { get; }
        public PatchesViewModel Patches { get; }
        public AudioSettingsViewModel Audio { get; }
        public GraphicsTweaksViewModel Graphics { get; }
        public InputSettingsViewModel Input { get; }
        public KeybindsViewModel Keybinds { get; }
        public InitialisationSettingsViewModel Init { get; }
        public CommunityModsViewModel CommunityMods { get; }
        public LaunchArgumentsViewModel LaunchArguments { get; }
        public LanguageSettingsViewModel Language { get; }

        public MainViewModel(
            GameSession session,
            GameStatusViewModel gameStatus,
            ConsoleViewModel console,
            TweaksScriptsViewModel tweaksScripts,
            UnlockedConfigsViewModel unlockedConfigs,
            DownloadProgressViewModel downloadProgress,
            TdGameVersionViewModel tdGameVersion,
            ModsViewModel mods,
            PatchesViewModel patches,
            AudioSettingsViewModel audio,
            GraphicsTweaksViewModel graphics,
            InputSettingsViewModel input,
            KeybindsViewModel keybinds,
            InitialisationSettingsViewModel init,
            CommunityModsViewModel communityMods,
            LaunchArgumentsViewModel launchArguments,
            LanguageSettingsViewModel language,
            IGameDataService gameData,
            IAppSettingsService settings,
            IGameLauncher gameLauncher,
            IDialogService dialogService,
            IPackageService packageService,
            IDecompressionService decompressionService,
            IFileService fileService,
            IFolderPickerService folderPicker)
        {
            Session = session;
            GameStatus = gameStatus;
            Console = console;
            TweaksScripts = tweaksScripts;
            UnlockedConfigs = unlockedConfigs;
            DownloadProgress = downloadProgress;
            TdGameVersion = tdGameVersion;
            Mods = mods;
            Patches = patches;
            Audio = audio;
            Graphics = graphics;
            Input = input;
            Keybinds = keybinds;
            Init = init;
            CommunityMods = communityMods;
            LaunchArguments = launchArguments;
            Language = language;

            _gameData = gameData;
            _settings = settings;
            _gameLauncher = gameLauncher;
            _dialogService = dialogService;
            _packageService = packageService;
            _decompressionService = decompressionService;
            _fileService = fileService;
            _folderPicker = folderPicker;

            _gameData.PackagesReloaded += OnPackagesReloaded;
        }

        private static void Dispatch(Action action)
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        // ---- Startup ----

        // Entry point invoked from the window's Loaded event.
        public async Task InitializeAsync()
        {
            LoadSettings();

            if (!string.IsNullOrEmpty(Session.Config.GameDirectoryPath) && _packageService.IsValidGameDirectory(Session.Config.GameDirectoryPath))
            {
                await ProcessGameDirectoryAsync(Session.Config.GameDirectoryPath);
                return;
            }

            Session.Config.GameDirectoryPath = null;
            GameStatus.GameDirectoryPath = "No valid directory selected.";
            DisplayGameVersion();
            GameStatus.IsMainTabEnabled = false;

            CheckForConfigFiles();
            Graphics.InitializeResolutions();
            Graphics.LoadFromIni();
            Graphics.RefreshFpsLimit();
            Mods.RefreshTweaksScriptsUIStatus();
            Input.RefreshMouseSmoothing();
            Input.RefreshUniformSensitivity();
            Input.RefreshGamepadButtons();
            Keybinds.LoadCustomKeybinds();
            Init.RefreshIntroVideo();
            Init.RefreshMainMenuDelay();
            Init.RefreshTimeTrialCountdown();
            Init.RefreshSkipOnlineCheck();
            Language.Refresh();
            Audio.RefreshAudioBackendSetting();
        }

        public async Task ProcessGameDirectoryAsync(string path)
        {
            Session.IsProcessingGameDirectory = true;
            Session.Config.GameDirectoryPath = path;
            GameStatus.GameDirectoryPath = path;

            SaveSettings();
            SetTopStatusLoadingState();

            GameStatus.IsUiEnabled = false;
            GameStatus.Status = "Checking package compression...";
            DownloadProgress.IsDownloadProgressIndeterminate = true;
            DownloadProgress.IsDownloadProgressVisible = true;
            bool loadSucceeded = false;

            try
            {
                await Task.Run(() =>
                {
                    string enginePath = Path.Combine(Session.Config.GameDirectoryPath!, "TdGame", "CookedPC", "Engine.u");
                    string tdGamePath = Path.Combine(Session.Config.GameDirectoryPath!, "TdGame", "CookedPC", "TdGame.u");

                    try
                    {
                        _decompressionService.RunDecompressor(enginePath);
                        _decompressionService.RunDecompressor(tdGamePath);
                    }
                    catch (Exception ex)
                    {
                        Dispatch(() => GameStatus.Status = $"Decompression error: {ex.Message}");
                    }
                });

                GameStatus.Status = "Loading package data...";
                await Task.Run(() => _gameData.LoadPackages());

                GameStatus.Status = "Refreshing status indicators...";
                CheckForConfigFiles();
                await Graphics.InitializeResolutionsAsync();
                Graphics.LoadFromIni();
                CommunityMods.RefreshCinematicFaith();
                Input.RefreshMouseSmoothing();
                Keybinds.LoadCustomKeybinds();
                Keybinds.LoadMacroKeybinds();
                Init.RefreshIntroVideo();
                Init.RefreshMainMenuDelay();
                Init.RefreshTimeTrialCountdown();
                Language.Refresh();
                Audio.RefreshAudioBackendSetting();
                await Graphics.RefreshPhysXFpsAsync();
                await Input.RefreshUniformSensitivityAsync();
                await Input.RefreshGamepadButtonsAsync();
                await Init.RefreshSkipOnlineCheckAsync();
                loadSucceeded = true;
            }
            catch (Exception ex)
            {
                GameStatus.Status = $"Error loading game data: {ex.Message}";
            }
            finally
            {
                Session.IsProcessingGameDirectory = false;

                try { await RefreshStartupStatusIndicatorsAsync(); }
                catch { }

                DownloadProgress.IsDownloadProgressVisible = false;
                DownloadProgress.IsDownloadProgressIndeterminate = false;

                if (loadSucceeded)
                {
                    GameStatus.Status = "Ready.";
                }

                // Everything is loaded and refreshed - activate the whole UI at once
                GameStatus.IsUiEnabled = true;
                GameStatus.IsMainTabEnabled = true;
            }
        }

        private void SetTopStatusLoadingState()
        {
            GameStatus.GameVersion = "Game Version: Loading...";

            GameStatus.ConfigStatus = "Documents Configs: Checking...";
            GameStatus.ConfigStatusForeground = Brushes.Gray;

            Console.ConsoleStatus = "Checking...";
            Console.ConsoleStatusForeground = Brushes.Gray;

            TweaksScripts.TweaksScriptsStatus = "Checking...";
            TweaksScripts.TweaksScriptsStatusForeground = Brushes.Gray;

            TweaksScripts.TweaksScriptsUIStatus = "Checking...";
            TweaksScripts.TweaksScriptsUIStatusForeground = Brushes.Gray;

            UnlockedConfigs.UnlockedConfigsStatus = "Checking...";
            UnlockedConfigs.UnlockedConfigsStatusForeground = Brushes.Gray;

            LaunchArguments.SetChecking();

            Graphics.HorPlusStatus = "";

            Graphics.HighResFixStatus = "High-Res Fix Checking...";
            Graphics.HighResFixStatusForeground = Brushes.Gray;

            Graphics.FpsLimitStatus = "Checking...";
            Graphics.FpsLimitStatusForeground = Brushes.Gray;
        }

        private void RefreshStartupStatusIndicators()
        {
            DisplayGameVersion();
            CheckForConfigFiles();
            _gameData.UpdateConsoleStatus();
            Mods.RefreshTweaksScriptsStatus();
            Mods.RefreshTweaksScriptsUIStatus();
            Patches.RefreshUnlockedConfigs();
            Graphics.RefreshHighResFix();
            Graphics.RefreshFpsLimit();

            if (Session.Package != null && Session.TdGamePackage != null)
            {
                RefreshStatusDisplays();
            }
        }

        private async Task RefreshStartupStatusIndicatorsAsync()
        {
            await DisplayGameVersionAsync();
            CheckForConfigFiles();
            _gameData.UpdateConsoleStatus();
            Mods.RefreshTweaksScriptsStatus();
            Mods.RefreshTweaksScriptsUIStatus();
            await Patches.RefreshUnlockedConfigsAsync();
            await Graphics.RefreshHighResFixAsync();
            Graphics.RefreshFpsLimit();

            if (Session.Package != null && Session.TdGamePackage != null)
            {
                await RefreshStatusDisplaysAsync();
            }
        }

        // Post-reload fan-out: runs the feature-VM refreshes after a package (re)load. Subscribed to
        // IGameDataService.PackagesReloaded so the service need not depend on the feature view models.
        private void OnPackagesReloaded()
        {
            Dispatch(() =>
            {
                TdGameVersion.DetectVersion();
                Graphics.RefreshFovDisplay();
                Patches.RefreshUnlockedConfigs();
                Mods.RefreshTweaksScriptsStatus();

                if (!Session.IsProcessingGameDirectory)
                {
                    if (GameStatus.IsGameTweaksEnabled)
                    {
                        RefreshStatusDisplays();
                    }
                    else
                    {
                        _dialogService.ShowMessage("Warning", "Could not locate any editable properties in the game files.", DialogMessageType.Warning);
                    }
                    GameStatus.Status = "Ready.";
                }
            });
        }

        private void RefreshStatusDisplays()
        {
            try
            {
                var tdGamePath = Session.Config.TdGamePackagePath;
                if (tdGamePath != null && File.Exists(tdGamePath))
                {
                    var tdState = TdGamePatcher.DetectState(tdGamePath);
                    Graphics.CompensatedClip = tdState.ClipApplied;
                    Graphics.FovAgnosticSens = tdState.SensApplied;
                    Init.SetSkipOnlineFromState(tdState.OnlineSkipApplied);
                }

                Graphics.RefreshEnginePatchState();
            }
            catch
            {
            }
        }

        private async Task RefreshStatusDisplaysAsync()
        {
            try
            {
                var tdGamePath = Session.Config.TdGamePackagePath;
                if (tdGamePath != null && File.Exists(tdGamePath))
                {
                    var tdState = await Task.Run(() => TdGamePatcher.DetectState(tdGamePath));
                    Graphics.CompensatedClip = tdState.ClipApplied;
                    Graphics.FovAgnosticSens = tdState.SensApplied;
                    Init.SetSkipOnlineFromState(tdState.OnlineSkipApplied);
                }

                await Graphics.RefreshEnginePatchStateAsync();
            }
            catch
            {
            }
        }

        private void CheckForConfigFiles()
        {
            var config = Session.Config;
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configDirectory = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config");

                config.TdEngineIniPath = Path.Combine(configDirectory, "TdEngine.ini");
                config.TdInputIniPath = Path.Combine(configDirectory, "TdInput.ini");

                if (_fileService.FileExists(config.TdEngineIniPath) && _fileService.FileExists(config.TdInputIniPath))
                {
                    GameStatus.ConfigStatus = "Documents Configs: Found";
                    GameStatus.ConfigStatusForeground = Brushes.Green;
                }
                else
                {
                    GameStatus.ConfigStatus = "Documents Configs: Not Found";
                    GameStatus.ConfigStatusForeground = Brushes.OrangeRed;
                    config.TdEngineIniPath = null;
                    config.TdInputIniPath = null;
                }
            }
            catch (Exception)
            {
                GameStatus.ConfigStatus = "Documents Configs: Error";
                GameStatus.ConfigStatusForeground = Brushes.Red;
                config.TdEngineIniPath = null;
                config.TdInputIniPath = null;
            }
        }

        private void DisplayGameVersion()
        {
            var gameVersion = GameVersionHelper.GetGameVersion(Session.Config.GameDirectoryPath ?? string.Empty);
            GameStatus.GameVersion = gameVersion.DisplayText;

            LaunchArguments.RefreshPatchStatus();
            Patches.RefreshLoggingStatus();
            Patches.RefreshMultiInstanceStatus();
            Patches.RefreshAmbiguousBypassStatus();
        }

        private async Task DisplayGameVersionAsync()
        {
            var gameDir = Session.Config.GameDirectoryPath ?? string.Empty;
            var gameVersion = await Task.Run(() => GameVersionHelper.GetGameVersion(gameDir));
            GameStatus.GameVersion = gameVersion.DisplayText;

            await LaunchArguments.RefreshPatchStatusAsync();
            await Patches.RefreshLoggingStatusAsync();
            await Patches.RefreshMultiInstanceStatusAsync();
            await Patches.RefreshAmbiguousBypassStatusAsync();
        }

        // ---- Settings persistence ----

        private void LoadSettings()
        {
            _settings.Load();

            if (Session.Config.Fov != null)
            {
                Graphics.NewFovValue = Session.Config.Fov;
            }
            if (Session.Config.Dpi != null)
            {
                Input.Dpi = Session.Config.Dpi;
            }
            if (Session.Config.Cm360 != null)
            {
                Input.Cm360 = Session.Config.Cm360;
            }
            LaunchArguments.LaunchArguments = Session.Config.LaunchArguments;
        }

        private void SaveSettings()
        {
            Session.Config.LaunchArguments = (LaunchArguments.LaunchArguments ?? string.Empty).Trim();
            Session.Config.Fov = Graphics.NewFovValue;
            Session.Config.Dpi = Input.Dpi;
            Session.Config.Cm360 = Input.Cm360;
            _settings.Save();
        }

        // ---- Top-bar commands ----

        [RelayCommand]
        private void LaunchGame()
        {
            try
            {
                var gameDir = Session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                {
                    _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                    return;
                }

                string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    _dialogService.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogMessageType.Error);
                    return;
                }

                ShowSteamLaunchWarningIfNeeded(exePath);
                _gameLauncher.Launch(exePath, string.Empty);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to launch game: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private void LaunchGameWithArgs()
        {
            try
            {
                var gameDir = Session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                {
                    _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                    return;
                }

                string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    _dialogService.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogMessageType.Error);
                    return;
                }

                string launchArguments = (LaunchArguments.LaunchArguments ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(launchArguments))
                {
                    _dialogService.ShowMessage("Error", "Please enter launch arguments first.", DialogMessageType.Error);
                    return;
                }

                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                if (unlockMode == CommandLineUnlockMode.Unsupported)
                {
                    _dialogService.ShowMessage("Error",
                        "This executable version does not support command line unlocking.",
                        DialogMessageType.Error);
                    return;
                }

                if (!CommandLineUnlockHelper.IsUnlocked(exePath))
                {
                    _dialogService.ShowMessage("Error",
                        "The executable has not been patched to unlock command line arguments yet.\n\nClick the 'Patch' button first.",
                        DialogMessageType.Error);
                    return;
                }

                ShowSteamLaunchWarningIfNeeded(exePath);
                _gameLauncher.Launch(exePath, launchArguments);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to launch game with arguments: {ex.Message}", DialogMessageType.Error);
            }
        }

        private void ShowSteamLaunchWarningIfNeeded(string exePath)
        {
            if (!_gameLauncher.IsSteamVersionExecutable(exePath))
            {
                return;
            }

            _dialogService.ShowMessage(
                "Steam Launch Warning",
                "You are playing the Steam version of the game.\n\n" +
                "Launching the game outside of the Steam client may present you with the \"Application load error\" message.\n\n" +
                "Please note that launching the game via Tweaks is not required; you may launch the game in Steam as normal.",
                DialogMessageType.Warning);
        }

        [RelayCommand]
        private async Task SelectGameDirectory()
        {
            string? selectedPath = _folderPicker.PickFolder("Select the main Mirror's Edge game directory", Session.Config.GameDirectoryPath);
            if (selectedPath == null)
            {
                return;
            }

            if (_packageService.IsValidGameDirectory(selectedPath))
            {
                await ProcessGameDirectoryAsync(selectedPath);
            }
            else
            {
                _dialogService.ShowMessage("Invalid Directory", "Invalid game directory.\n\nPlease select the base folder where Mirror's Edge is actually installed.", DialogMessageType.Error);
                GameStatus.IsMainTabEnabled = false;
            }
        }
    }

    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public abstract class BusyViewModel : ObservableObject
    {
        protected readonly GameStatusViewModel _gameStatus;
        protected readonly DownloadProgressViewModel _downloadProgress;
        private bool _isBusy;
        protected bool IsBusy => _isBusy;

        protected BusyViewModel(GameStatusViewModel gameStatus, DownloadProgressViewModel downloadProgress)
        {
            _gameStatus = gameStatus;
            _downloadProgress = downloadProgress;
        }

        protected static void Dispatch(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        protected static void Post(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.InvokeAsync(action);
        }

        protected void ShowProgress(string message, bool isIndeterminate)
        {
            Dispatch(() =>
            {
                _gameStatus.Status = message;
                _downloadProgress.IsDownloadProgressVisible = true;
                _downloadProgress.IsDownloadProgressIndeterminate = isIndeterminate;
                if (!isIndeterminate)
                {
                    _downloadProgress.DownloadProgressValue = 0;
                }
            });
        }

        protected void HideProgress(string readyMessage = "Ready.")
        {
            Dispatch(() =>
            {
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.IsDownloadProgressIndeterminate = false;
                _downloadProgress.DownloadProgressValue = 0;
                _gameStatus.Status = readyMessage;
            });
        }

        protected async Task<bool> RunBusyAsync(string status, Action work, bool indeterminate = true, string completedStatus = "Ready.")
        {
            if (_isBusy) return false;
            _isBusy = true;
            ShowProgress(status, indeterminate);
            try
            {
                await Task.Run(work);
                return true;
            }
            finally
            {
                HideProgress(completedStatus);
                _isBusy = false;
            }
        }

        protected Action<double, string?> CreateThrottledProgressReporter(int minIntervalMs = 75)
        {
            long lastTick = 0;
            return (value, status) =>
            {
                long now = Environment.TickCount64;
                if (now - lastTick < minIntervalMs) return;
                lastTick = now;
                Post(() =>
                {
                    _downloadProgress.DownloadProgressValue = value;
                    if (status != null)
                    {
                        _gameStatus.Status = status;
                    }
                });
            };
        }
    }

    public class GameStatusViewModel : BaseViewModel
    {
        private string _gameDirectoryPath = "No valid directory selected.";
        private string _gameVersion = "Game Version: N/A";
        private string _configStatus = "Documents Configs: Not Found";
        private System.Windows.Media.Brush _configStatusForeground = System.Windows.Media.Brushes.OrangeRed;
        private string _status = "Ready. Please select your Mirror's Edge game directory.";
        private bool _isGameTweaksEnabled = true;
        private bool _isUiEnabled = true;
        private bool _isMainTabEnabled = true;

        public string GameDirectoryPath
        {
            get => _gameDirectoryPath;
            set => SetProperty(ref _gameDirectoryPath, value);
        }

        public string GameVersion
        {
            get => _gameVersion;
            set => SetProperty(ref _gameVersion, value);
        }

        public string ConfigStatus
        {
            get => _configStatus;
            set => SetProperty(ref _configStatus, value);
        }

        public System.Windows.Media.Brush ConfigStatusForeground
        {
            get => _configStatusForeground;
            set => SetProperty(ref _configStatusForeground, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public bool IsGameTweaksEnabled
        {
            get => _isGameTweaksEnabled;
            set => SetProperty(ref _isGameTweaksEnabled, value);
        }

        public bool IsUiEnabled
        {
            get => _isUiEnabled;
            set => SetProperty(ref _isUiEnabled, value);
        }

        public bool IsMainTabEnabled
        {
            get => _isMainTabEnabled;
            set => SetProperty(ref _isMainTabEnabled, value);
        }
    }

    public class ConsoleViewModel : BaseViewModel
    {
        private string _consoleStatus = "Not Installed";
        private System.Windows.Media.Brush _consoleStatusForeground = System.Windows.Media.Brushes.Gray;
        private bool _isInstallConsoleEnabled = false;
        private bool _isUninstallConsoleEnabled = false;

        public string ConsoleStatus
        {
            get => _consoleStatus;
            set => SetProperty(ref _consoleStatus, value);
        }

        public System.Windows.Media.Brush ConsoleStatusForeground
        {
            get => _consoleStatusForeground;
            set => SetProperty(ref _consoleStatusForeground, value);
        }

        public bool IsInstallConsoleEnabled
        {
            get => _isInstallConsoleEnabled;
            set => SetProperty(ref _isInstallConsoleEnabled, value);
        }

        public bool IsUninstallConsoleEnabled
        {
            get => _isUninstallConsoleEnabled;
            set => SetProperty(ref _isUninstallConsoleEnabled, value);
        }
    }

    public class TweaksScriptsViewModel : BaseViewModel
    {
        private string _tweaksScriptsStatus = "Not Installed";
        private System.Windows.Media.Brush _tweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;
        private string _tweaksScriptsUIStatus = "N/A";
        private System.Windows.Media.Brush _tweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Gray;
        private bool _isTweaksScriptsUIInstallEnabled = false;
        private string _tweaksScriptsUIInstallTooltip = "Install Tweaks Scripts first to enable this installer.";
        private bool _isTweaksScriptsUIDependencyTextVisible = false;

        public string TweaksScriptsStatus
        {
            get => _tweaksScriptsStatus;
            set => SetProperty(ref _tweaksScriptsStatus, value);
        }

        public System.Windows.Media.Brush TweaksScriptsStatusForeground
        {
            get => _tweaksScriptsStatusForeground;
            set => SetProperty(ref _tweaksScriptsStatusForeground, value);
        }

        public string TweaksScriptsUIStatus
        {
            get => _tweaksScriptsUIStatus;
            set => SetProperty(ref _tweaksScriptsUIStatus, value);
        }

        public System.Windows.Media.Brush TweaksScriptsUIStatusForeground
        {
            get => _tweaksScriptsUIStatusForeground;
            set => SetProperty(ref _tweaksScriptsUIStatusForeground, value);
        }

        public bool IsTweaksScriptsUIInstallEnabled
        {
            get => _isTweaksScriptsUIInstallEnabled;
            set => SetProperty(ref _isTweaksScriptsUIInstallEnabled, value);
        }

        public string TweaksScriptsUIInstallTooltip
        {
            get => _tweaksScriptsUIInstallTooltip;
            set => SetProperty(ref _tweaksScriptsUIInstallTooltip, value);
        }

        public bool IsTweaksScriptsUIDependencyTextVisible
        {
            get => _isTweaksScriptsUIDependencyTextVisible;
            set => SetProperty(ref _isTweaksScriptsUIDependencyTextVisible, value);
        }
    }

    public class UnlockedConfigsViewModel : BaseViewModel
    {
        private string _unlockedConfigsStatus = "N/A";
        private System.Windows.Media.Brush _unlockedConfigsStatusForeground = System.Windows.Media.Brushes.Gray;
        private bool _isPatchConfigsEnabled = false;
        private bool _isUnpatchConfigsEnabled = false;

        public string UnlockedConfigsStatus
        {
            get => _unlockedConfigsStatus;
            set => SetProperty(ref _unlockedConfigsStatus, value);
        }

        public System.Windows.Media.Brush UnlockedConfigsStatusForeground
        {
            get => _unlockedConfigsStatusForeground;
            set => SetProperty(ref _unlockedConfigsStatusForeground, value);
        }

        public bool IsPatchConfigsEnabled
        {
            get => _isPatchConfigsEnabled;
            set => SetProperty(ref _isPatchConfigsEnabled, value);
        }

        public bool IsUnpatchConfigsEnabled
        {
            get => _isUnpatchConfigsEnabled;
            set => SetProperty(ref _isUnpatchConfigsEnabled, value);
        }
    }

    public class DownloadProgressViewModel : BaseViewModel
    {
        private bool _isDownloadProgressVisible = false;
        private bool _isDownloadProgressIndeterminate = false;
        private double _downloadProgressValue = 0;

        public bool IsDownloadProgressVisible
        {
            get => _isDownloadProgressVisible;
            set => SetProperty(ref _isDownloadProgressVisible, value);
        }

        public bool IsDownloadProgressIndeterminate
        {
            get => _isDownloadProgressIndeterminate;
            set => SetProperty(ref _isDownloadProgressIndeterminate, value);
        }

        public double DownloadProgressValue
        {
            get => _downloadProgressValue;
            set => SetProperty(ref _downloadProgressValue, value);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using Brushes = System.Windows.Media.Brushes;

namespace MirrorsEdgeTweaks.ViewModels
{
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
        private readonly IGameProcessMonitor _processMonitor;
        private readonly ISteamService _steamService;
        private readonly IAssetUrlProvider _assetUrls;

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
            IFolderPickerService folderPicker,
            IGameProcessMonitor processMonitor,
            ISteamService steamService,
            IAssetUrlProvider assetUrls)
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
            _processMonitor = processMonitor;
            _steamService = steamService;
            _assetUrls = assetUrls;

            _gameData.PackagesReloaded += OnPackagesReloaded;
            _processMonitor.RunningStateChanged += OnGameRunningChanged;
        }

        private void OnGameRunningChanged(bool running) => Dispatch(() =>
        {
            GameStatus.IsGameRunning = running;
            if (running)
            {
                GameStatus.Status = "Tweaks locked. Close Mirror's Edge to continue.";
            }
            else if (!string.IsNullOrEmpty(Session.Config.GameDirectoryPath))
            {
                GameStatus.Status = "Ready.";
            }
        });

        private static void Dispatch(Action action)
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        public async Task InitializeAsync()
        {
            _ = _assetUrls.EnsureLoadedAsync();

            LoadSettings();

            GameStatus.IsGameRunning = _processMonitor.IsGameRunning;
            _processMonitor.Start();

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

        public async Task ProcessGameDirectoryAsync(string path) =>
            await Session.ApplyGate.RunAsync(() => ProcessGameDirectoryCoreAsync(path));

        private async Task ProcessGameDirectoryCoreAsync(string path)
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

                if (_steamService.IsSteamGameDirectory(path))
                {
                    GameStatus.Status = "Applying Steam language fix...";
                    SteamInstallScriptFixResult steamFixResult = await Task.Run(() => _steamService.ApplyLanguageFix(path));
                    if (steamFixResult.AnyFailed)
                    {
                        string failedPaths = string.Join(Environment.NewLine, steamFixResult.FailedFiles.Select(f => f.Path));
                        _dialogService.ShowMessage(
                            "Steam Language Fix",
                            "Mirror's Edge Tweaks could not update one or more Steam install scripts. " +
                            "Language changes may revert when launching via Steam.\n\n" +
                            failedPaths,
                            DialogMessageType.Warning);
                    }
                }

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
            Graphics.RefreshToneMapper();

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
            Graphics.RefreshToneMapper();

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
                        _dialogService.ShowMessage("Warning", "Could not locate editable properties in the loaded game packages.", DialogMessageType.Warning);
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
                    _dialogService.ShowMessage("Error", "Enter launch arguments first.", DialogMessageType.Error);
                    return;
                }

                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                if (unlockMode == CommandLineUnlockMode.Unsupported)
                {
                    _dialogService.ShowMessage("Error",
                        "This executable version does not support command-line unlocking.",
                        DialogMessageType.Error);
                    return;
                }

                if (!CommandLineUnlockHelper.IsUnlocked(exePath))
                {
                    _dialogService.ShowMessage("Error",
                        "The executable has not been patched to unlock command-line arguments.\n\nClick 'Patch' first.",
                        DialogMessageType.Error);
                    return;
                }

                _gameLauncher.Launch(exePath, launchArguments);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to launch game with arguments: {ex.Message}", DialogMessageType.Error);
            }
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
                _dialogService.ShowMessage("Invalid Directory",
                    "Invalid game directory.\n\nSelect the base folder where Mirror's Edge is installed.",
                    DialogMessageType.Error);
                GameStatus.IsMainTabEnabled = false;
            }
        }
    }
}

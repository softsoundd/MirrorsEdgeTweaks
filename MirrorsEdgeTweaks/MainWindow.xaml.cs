using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.ViewModels;
using UELib;
using UELib.Core;
using UELib.Flags;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks
{
    public partial class MainWindow : Window
    {
        private readonly GameConfiguration _config = new GameConfiguration();
        private readonly PackageOffsets _offsets = new PackageOffsets();

        private readonly IPackageService _packageService;
        private readonly IFileService _fileService;
        private readonly IDownloadService _downloadService;
        private readonly IDecompressionService _decompressionService;
        private readonly IOffsetFinderService _offsetFinderService;
        private readonly IUIScalingService _uiScalingService;
        private readonly IGraphicsSettingsService _graphicsSettingsService;

        private bool _isInitializingResolutionComboBox = false;
        private bool _isInitializingGraphicsSettings = false;
        private bool _isRenderResolutionDragging = false;
        private bool _isProcessingGameDirectory = false;

        private readonly GameStatusViewModel _gameStatusViewModel;
        private readonly FovViewModel _fovViewModel;
        private readonly ConsoleViewModel _consoleViewModel;
        private readonly TweaksScriptsViewModel _tweaksScriptsViewModel;
        private readonly UnlockedConfigsViewModel _unlockedConfigsViewModel;
        private readonly DownloadProgressViewModel _downloadProgressViewModel;
        private readonly TdGameVersionViewModel _tdGameVersionViewModel;

        private UnrealPackage? _package;
        private UnrealPackage? _tdGamePackage;
        private UnrealFlags<PropertyFlag> _originalZoomFovFlags;

        private const string IniFileName = "metweaksconfig.ini";
        private const long SteamMirrorsEdgeExeSize = 31946072;

        private sealed class TdGameFovTouchpointSnapshot
        {
            public bool DynamicPatchesApplied { get; set; }
            public bool SensEnabled { get; set; }
            public bool ClipEnabled { get; set; }
            public bool OnlineSkipEnabled { get; set; }
            public float BaseFov { get; set; } = 90f;

            public bool HasValues => DynamicPatchesApplied;
        }

        private sealed class TdGameTouchpointSnapshot
        {
            public TdGameFovTouchpointSnapshot FovSnapshot { get; } = new TdGameFovTouchpointSnapshot();
            public bool WasHighResFixActive { get; set; }
            public float? UniformSensitivityTargetValue { get; set; }
            public string? GamepadButtonType { get; set; }
            public bool HasLoadLastCheckpointKeybind { get; set; }
            public bool HasRestartTimeTrialKeybind { get; set; }
        }

        private sealed class TdGameTouchpointReapplyResult
        {
            public List<string> ReappliedSettings { get; } = new List<string>();
            public List<string> FailedSettings { get; } = new List<string>();
        }


        public MainWindow()
        {
            InitializeComponent();

            RenderResolutionSlider.AddHandler(
                System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
                new System.Windows.Controls.Primitives.DragStartedEventHandler(RenderResolutionSlider_DragStarted));
            RenderResolutionSlider.AddHandler(
                System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler(RenderResolutionSlider_DragCompleted));

            _fileService = new FileService();
            _packageService = new PackageService();
            _downloadService = new DownloadService(_fileService);
            _decompressionService = new DecompressionService(_fileService);
            _offsetFinderService = new OffsetFinderService();
            _uiScalingService = new UIScalingService(_packageService, _fileService, _offsetFinderService, _decompressionService);
            _graphicsSettingsService = new GraphicsSettingsService();

            _gameStatusViewModel = new GameStatusViewModel();
            _fovViewModel = new FovViewModel();
            _consoleViewModel = new ConsoleViewModel();
            _tweaksScriptsViewModel = new TweaksScriptsViewModel();
            _unlockedConfigsViewModel = new UnlockedConfigsViewModel();
            _downloadProgressViewModel = new DownloadProgressViewModel();
            _tdGameVersionViewModel = new TdGameVersionViewModel();

            DataContext = new
            {
                GameStatus = _gameStatusViewModel,
                Fov = _fovViewModel,
                Console = _consoleViewModel,
                TweaksScripts = _tweaksScriptsViewModel,
                UnlockedConfigs = _unlockedConfigsViewModel,
                DownloadProgress = _downloadProgressViewModel,
                TdGameVersion = _tdGameVersionViewModel
            };

            EventManager.RegisterClassHandler(typeof(ComboBoxItem), System.Windows.Input.Mouse.PreviewMouseUpEvent, new System.Windows.Input.MouseButtonEventHandler(OnComboBoxItemMouseUp), true);
        }

        private void OnComboBoxItemMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is ComboBoxItem item)
            {
                var comboBox = ItemsControl.ItemsControlFromItemContainer(item) as System.Windows.Controls.ComboBox;
                if (comboBox != null)
                {
                    if (comboBox.ItemContainerGenerator.ItemFromContainer(item) == comboBox.SelectedItem)
                    {
                        comboBox.RaiseEvent(new SelectionChangedEventArgs(System.Windows.Controls.ComboBox.SelectionChangedEvent, new List<object>(), new List<object>()));
                    }
                }
            }
        }


        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            bool isProcessingStoredDirectory = InitializeApp();
            if (isProcessingStoredDirectory)
            {
                return;
            }

            CheckForConfigFiles();

            InitializeResolutionComboBox();

            LoadGraphicsSettingsFromIni();

            UpdateFPSLimitStatus();

            UpdateTweaksScriptsUIStatus();

            LoadMouseSmoothingFromIni();
            LoadUniformSensitivityFromPackage();
            LoadGamepadButtonsFromFiles();
            LoadCustomKeybinds();

            LoadIntroVideoSetting();
            LoadMainMenuDelaySetting();
            LoadTimeTrialCountdownSetting();
            LoadSkipOnlineCheckSetting();

            LoadGameLanguageSetting();
            LoadAudioBackendSetting();
        }

        private void LaunchGame_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                    return;
                }

                string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                    return;
                }

                ShowSteamLaunchWarningIfNeeded(exePath);
                LaunchGameProcess(exePath, string.Empty);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to launch game: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void LaunchGameWithArgs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                    return;
                }

                string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                    return;
                }

                string launchArguments = LaunchArgumentsTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(launchArguments))
                {
                    DialogHelper.ShowMessage("Error", "Please enter launch arguments first.", DialogHelper.MessageType.Error);
                    return;
                }

                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                if (unlockMode == CommandLineUnlockMode.Unsupported)
                {
                    DialogHelper.ShowMessage("Error",
                        "This executable version does not support command line unlocking.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                if (!CommandLineUnlockHelper.IsUnlocked(exePath))
                {
                    DialogHelper.ShowMessage("Error",
                        "The executable has not been patched to unlock command line arguments yet.\n\nClick the 'Patch' button first.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                ShowSteamLaunchWarningIfNeeded(exePath);
                LaunchGameProcess(exePath, launchArguments);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to launch game with arguments: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowSteamLaunchWarningIfNeeded(string exePath)
        {
            if (!IsSteamVersionExecutable(exePath))
            {
                return;
            }

            DialogHelper.ShowMessage(
                "Steam Launch Warning",
                "You are playing the Steam version of the game.\n\n" +
                "Launching the game outside of the Steam client may present you with the \"Application load error\" message.\n\n" +
                "Please note that launching the game via Tweaks is not required; you may launch the game in Steam as normal.",
                DialogHelper.MessageType.Warning);
        }

        private static bool IsSteamVersionExecutable(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            try
            {
                return new FileInfo(exePath).Length == SteamMirrorsEdgeExeSize;
            }
            catch
            {
                return false;
            }
        }

        private void LaunchGameProcess(string exePath, string arguments)
        {
            string? workingDirectory = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                throw new InvalidOperationException("Could not determine a valid game working directory.");
            }

            List<string> launchErrors = new List<string>();

            if (TryStartProcess(exePath, arguments, workingDirectory, useShellExecute: false, launchErrors))
            {
                return;
            }

            if (TryStartProcess(exePath, arguments, workingDirectory, useShellExecute: true, launchErrors))
            {
                return;
            }

            if (TryStartViaCmd(exePath, arguments, workingDirectory, launchErrors))
            {
                return;
            }

            throw new InvalidOperationException($"All launch strategies failed. {string.Join(" | ", launchErrors)}");
        }

        private static bool TryStartProcess(
            string exePath,
            string arguments,
            string workingDirectory,
            bool useShellExecute,
            List<string> launchErrors)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = useShellExecute
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    launchErrors.Add($"UseShellExecute={useShellExecute}: Process.Start returned null.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                launchErrors.Add($"UseShellExecute={useShellExecute}: {ex.Message}");
                return false;
            }
        }

        private static bool TryStartViaCmd(
            string exePath,
            string arguments,
            string workingDirectory,
            List<string> launchErrors)
        {
            try
            {
                string cmdArguments = string.IsNullOrWhiteSpace(arguments)
                    ? $"/c start \"\" \"{exePath}\""
                    : $"/c start \"\" \"{exePath}\" {arguments}";

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    launchErrors.Add("cmd fallback: Process.Start returned null.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                launchErrors.Add($"cmd fallback: {ex.Message}");
                return false;
            }
        }

        private bool InitializeApp()
        {
            LoadSettingsFromIni();

            if (!string.IsNullOrEmpty(_config.GameDirectoryPath) && _packageService.IsValidGameDirectory(_config.GameDirectoryPath))
            {
                ProcessGameDirectory(_config.GameDirectoryPath);
                return true;
            }
            else
            {
                _config.GameDirectoryPath = null;
                _gameStatusViewModel.GameDirectoryPath = "No valid directory selected.";
                DisplayGameVersion();

                DisableMainUI();
                return false;
            }
        }

        private async void ProcessGameDirectory(string path)
        {
            _isProcessingGameDirectory = true;
            _config.GameDirectoryPath = path;
            _gameStatusViewModel.GameDirectoryPath = _config.GameDirectoryPath;

            GameDirectoryPathTextBlock.Text = _config.GameDirectoryPath;

            SaveSettingsToIni();
            SetTopStatusLoadingState();

            this.IsEnabled = false;

            UpdateStatus("Checking package compression...");
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressBar.Visibility = Visibility.Visible;
            bool loadSucceeded = false;

            try
            {
                await Task.Run(() =>
                {
                    string enginePath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC", "Engine.u");
                    string tdGamePath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC", "TdGame.u");

                    try
                    {
                        _decompressionService.RunDecompressor(enginePath);
                        _decompressionService.RunDecompressor(tdGamePath);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => _gameStatusViewModel.Status = $"Decompression error: {ex.Message}");
                    }
                });

                UpdateStatus("Loading package data...");
                await Task.Run(() => LoadPackages());

                UpdateStatus("Refreshing status indicators...");
                CheckForConfigFiles();
                InitializeResolutionComboBox();

                LoadGraphicsSettingsFromIni();

                LoadPhysXFPSSetting();
                LoadCinematicFaithSetting();

                UpdateFPSLimitStatus();
                UpdateTweaksScriptsUIStatus();

                LoadMouseSmoothingFromIni();
                LoadUniformSensitivityFromPackage();
                LoadGamepadButtonsFromFiles();
                LoadCustomKeybinds();
                LoadMacroKeybinds();

                LoadIntroVideoSetting();
                LoadMainMenuDelaySetting();
                LoadTimeTrialCountdownSetting();
                LoadSkipOnlineCheckSetting();

                LoadGameLanguageSetting();
                LoadAudioBackendSetting();
                loadSucceeded = true;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading game data: {ex.Message}");
            }
            finally
            {
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                DownloadProgressBar.IsIndeterminate = false;

                this.IsEnabled = true;
                _isProcessingGameDirectory = false;
                RefreshStartupStatusIndicators();
                if (loadSucceeded)
                {
                    UpdateStatus("Ready.");
                }

                EnableMainUI();
            }
        }

        private void SetTopStatusLoadingState()
        {
            _gameStatusViewModel.GameVersion = "Game Version: Loading...";
            GameVersionTextBlock.Text = _gameStatusViewModel.GameVersion;

            _gameStatusViewModel.ConfigStatus = "Documents Configs: Checking...";
            _gameStatusViewModel.ConfigStatusForeground = System.Windows.Media.Brushes.Gray;
            ConfigStatusTextBlock.Text = _gameStatusViewModel.ConfigStatus;
            ConfigStatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;

            _consoleViewModel.ConsoleStatus = "Checking...";
            _consoleViewModel.ConsoleStatusForeground = System.Windows.Media.Brushes.Gray;
            ConsoleStatus.Text = _consoleViewModel.ConsoleStatus;
            ConsoleStatus.Foreground = _consoleViewModel.ConsoleStatusForeground;

            _tweaksScriptsViewModel.TweaksScriptsStatus = "Checking...";
            _tweaksScriptsViewModel.TweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;
            TweaksScriptsStatus.Text = _tweaksScriptsViewModel.TweaksScriptsStatus;
            TweaksScriptsStatus.Foreground = _tweaksScriptsViewModel.TweaksScriptsStatusForeground;

            TweaksScriptsUIStatus.Text = "Checking...";
            TweaksScriptsUIStatus.Foreground = System.Windows.Media.Brushes.Gray;

            _unlockedConfigsViewModel.UnlockedConfigsStatus = "Checking...";
            _unlockedConfigsViewModel.UnlockedConfigsStatusForeground = System.Windows.Media.Brushes.Gray;
            UnlockedConfigsStatus.Text = _unlockedConfigsViewModel.UnlockedConfigsStatus;
            UnlockedConfigsStatus.Foreground = _unlockedConfigsViewModel.UnlockedConfigsStatusForeground;

            SetLaunchArgumentsPatchStatus("Checking executable...", System.Windows.Media.Brushes.Gray);

            HorPlusStatus.Text = "";

            HighResFixStatus.Text = "High-Res Fix Checking...";
            HighResFixStatus.Foreground = System.Windows.Media.Brushes.Gray;

            FPSLimitStatus.Text = "Checking...";
            FPSLimitStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }

        private void RefreshStartupStatusIndicators()
        {
            DisplayGameVersion();
            CheckForConfigFiles();
            UpdateConsoleStatus();
            UpdateTweaksScriptsStatus();
            UpdateTweaksScriptsUIStatus();
            SetupUnlockedConfigsEditor();
            RefreshHighResFixStatusFromCurrentSelection();
            UpdateFPSLimitStatus();

            if (_package != null && _tdGamePackage != null)
            {
                UpdateStatusDisplays();
            }
        }

        private void RefreshHighResFixStatusFromCurrentSelection()
        {
            if (ResolutionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                selectedItem.Tag is ResolutionHelper.Resolution selectedResolution)
            {
                bool isCurrentlyActive = false;
                if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    try
                    {
                        isCurrentlyActive = _uiScalingService.IsUIScalingActive(_config.GameDirectoryPath);
                    }
                    catch
                    {
                        isCurrentlyActive = false;
                    }
                }

                UpdateHighResFixStatus(selectedResolution.Width, isCurrentlyActive);
                return;
            }

            HighResFixStatus.Text = "High-Res Fix N/A";
            HighResFixStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }

        private void EnableMainUI()
        {
            MainTabControl.IsEnabled = true;
        }

        private void DisableMainUI()
        {
            MainTabControl.IsEnabled = false;
        }

        private void DecompressPackages()
        {
            UpdateStatus("Checking package compression...");
            string enginePath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC", "Engine.u");
            string tdGamePath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC", "TdGame.u");

            try
            {
                _decompressionService.RunDecompressor(enginePath);
                _decompressionService.RunDecompressor(tdGamePath);
            }
            catch (Exception ex)
            {
                _gameStatusViewModel.Status = $"Decompression error: {ex.Message}";
            }
        }


        private void CheckForConfigFiles()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configDirectory = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config");

                _config.TdEngineIniPath = Path.Combine(configDirectory, "TdEngine.ini");
                _config.TdInputIniPath = Path.Combine(configDirectory, "TdInput.ini");

                if (_fileService.FileExists(_config.TdEngineIniPath) && _fileService.FileExists(_config.TdInputIniPath))
                {
                    _gameStatusViewModel.ConfigStatus = "Documents Configs: Found";
                    _gameStatusViewModel.ConfigStatusForeground = System.Windows.Media.Brushes.Green;
                    if (!_isProcessingGameDirectory)
                    {
                        ConfigStatusTextBlock.Text = "Documents Configs: Found";
                        ConfigStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                    }
                }
                else
                {
                    _gameStatusViewModel.ConfigStatus = "Documents Configs: Not Found";
                    _gameStatusViewModel.ConfigStatusForeground = System.Windows.Media.Brushes.OrangeRed;
                    if (!_isProcessingGameDirectory)
                    {
                        ConfigStatusTextBlock.Text = "Documents Configs: Not Found";
                        ConfigStatusTextBlock.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    }
                    _config.TdEngineIniPath = null;
                    _config.TdInputIniPath = null;
                }
            }
            catch (Exception)
            {
                _gameStatusViewModel.ConfigStatus = "Documents Configs: Error";
                _gameStatusViewModel.ConfigStatusForeground = System.Windows.Media.Brushes.Red;
                if (!_isProcessingGameDirectory)
                {
                    ConfigStatusTextBlock.Text = "Documents Configs: Error";
                    ConfigStatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                }
                _config.TdEngineIniPath = null;
                _config.TdInputIniPath = null;
            }
        }

        private void DisplayGameVersion()
        {
            var gameVersion = GameVersionHelper.GetGameVersion(_config.GameDirectoryPath ?? string.Empty);
            _gameStatusViewModel.GameVersion = gameVersion.DisplayText;

            GameVersionTextBlock.Text = gameVersion.DisplayText;
            UpdateLaunchArgumentsPatchStatus();
            UpdateLoggingPatchStatus();
            UpdateMultiInstancePatchStatus();
            UpdateAmbiguousBypassPatchStatus();
        }

        private void SetLaunchArgumentsPatchStatus(string status, System.Windows.Media.Brush foreground)
        {
            LaunchArgumentsPatchStatusTextBlock.Text = status;
            LaunchArgumentsPatchStatusTextBlock.Foreground = foreground;
        }

        private void UpdateLoggingPatchStatus()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                LoggingPatchStatusTextBlock.Text = "N/A";
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                LoggingPatchStatusTextBlock.Text = "N/A";
                return;
            }

            try
            {
                var state = LoggingPatchHelper.GetPatchState(exePath);
                switch (state)
                {
                    case LoggingPatchState.Patched:
                        LoggingPatchStatusTextBlock.Text = "Patched";
                        LoggingPatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                        break;
                    case LoggingPatchState.Unpatched:
                        LoggingPatchStatusTextBlock.Text = "Unpatched";
                        LoggingPatchStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("MaterialDesignBodyLight");
                        break;
                    default:
                        LoggingPatchStatusTextBlock.Text = "";
                        break;
                }
            }
            catch
            {
                LoggingPatchStatusTextBlock.Text = "";
            }
        }

        private void UpdateMultiInstancePatchStatus()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                MultiInstancePatchStatusTextBlock.Text = "N/A";
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                MultiInstancePatchStatusTextBlock.Text = "N/A";
                return;
            }

            try
            {
                var state = MultiInstancePatchHelper.GetPatchState(exePath);
                switch (state)
                {
                    case MultiInstancePatchState.Patched:
                        MultiInstancePatchStatusTextBlock.Text = "Patched";
                        MultiInstancePatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                        break;
                    case MultiInstancePatchState.Unpatched:
                        MultiInstancePatchStatusTextBlock.Text = "Unpatched";
                        MultiInstancePatchStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("MaterialDesignBodyLight");
                        break;
                    default:
                        MultiInstancePatchStatusTextBlock.Text = "";
                        break;
                }
            }
            catch
            {
                MultiInstancePatchStatusTextBlock.Text = "";
            }
        }

        private void UpdateAmbiguousBypassPatchStatus()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                AmbiguousBypassPatchStatusTextBlock.Text = "N/A";
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                AmbiguousBypassPatchStatusTextBlock.Text = "N/A";
                return;
            }

            try
            {
                var state = AmbiguousBypassPatchHelper.GetPatchState(exePath);
                switch (state)
                {
                    case AmbiguousBypassPatchState.Patched:
                        AmbiguousBypassPatchStatusTextBlock.Text = "Patched";
                        AmbiguousBypassPatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                        break;
                    case AmbiguousBypassPatchState.Unpatched:
                        AmbiguousBypassPatchStatusTextBlock.Text = "Unpatched";
                        AmbiguousBypassPatchStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("MaterialDesignBodyLight");
                        break;
                    default:
                        AmbiguousBypassPatchStatusTextBlock.Text = "";
                        break;
                }
            }
            catch
            {
                AmbiguousBypassPatchStatusTextBlock.Text = "";
            }
        }

        private void UpdateLaunchArgumentsPatchStatus()
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                SetLaunchArgumentsPatchStatus("N/A (No game directory selected)", System.Windows.Media.Brushes.Gray);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                SetLaunchArgumentsPatchStatus("N/A (EXE not found)", System.Windows.Media.Brushes.Gray);
                return;
            }

            try
            {
                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                switch (unlockMode)
                {
                    case CommandLineUnlockMode.PersistentFilePatch:
                        bool isUnlocked = CommandLineUnlockHelper.IsUnlocked(exePath);
                        SetLaunchArgumentsPatchStatus(
                            isUnlocked ? "Patched - Command line arguments are unlocked" : "Unpatched - Command line arguments are locked",
                            isUnlocked ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray);
                        break;

                    default:
                        SetLaunchArgumentsPatchStatus("Unsupported executable", System.Windows.Media.Brushes.Red);
                        break;
                }
            }
            catch
            {
                SetLaunchArgumentsPatchStatus("Error reading executable", System.Windows.Media.Brushes.Red);
            }
        }

        private void SelectGameDirectory_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select the main Mirror's Edge game directory";
                if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    dialog.SelectedPath = _config.GameDirectoryPath;
                }

                System.Windows.Forms.DialogResult result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    if (_packageService.IsValidGameDirectory(dialog.SelectedPath))
                    {
                        ProcessGameDirectory(dialog.SelectedPath);
                    }
                    else
                    {
                        DialogHelper.ShowMessage("Invalid Directory", "Invalid game directory.\n\nPlease select the base folder where Mirror's Edge is actually installed.", DialogHelper.MessageType.Error);
                        DisableMainUI();
                    }
                }
            }
        }

        private void LoadSettingsFromIni()
        {
            if (!_fileService.FileExists(IniFileName)) return;

            var settings = File.ReadAllLines(IniFileName)
              .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
              .Select(line => line.Split(new[] { '=' }, 2))
              .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            if (settings.TryGetValue("Path", out var path))
            {
                _config.GameDirectoryPath = path;
            }
            if (settings.TryGetValue("FOV", out var fov))
            {
                NewFovValue.Text = fov;
            }
            if (settings.TryGetValue("DPI", out var dpi))
            {
                DpiTextBox.Text = dpi;
            }
            if (settings.TryGetValue("Cm360", out var cm360))
            {
                Cm360TextBox.Text = cm360;
            }
            if (settings.TryGetValue("LaunchArguments", out var launchArguments))
            {
                _config.LaunchArguments = launchArguments;
                LaunchArgumentsTextBox.Text = launchArguments;
            }
            else
            {
                _config.LaunchArguments = string.Empty;
            }
        }

        private void SaveSettingsToIni()
        {
            string launchArguments = LaunchArgumentsTextBox.Text?.Trim() ?? string.Empty;
            _config.LaunchArguments = launchArguments;

            var lines = new List<string>
            {
                $"Path={_config.GameDirectoryPath}",
                $"FOV={NewFovValue.Text}",
                $"DPI={DpiTextBox.Text}",
                $"Cm360={Cm360TextBox.Text}",
                $"LaunchArguments={launchArguments}"
            };
            File.WriteAllLines(IniFileName, lines);
        }

        private void LoadPackages()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath)) return;

            try
            {
                _packageService.DisposePackage(_package);
                _packageService.DisposePackage(_tdGamePackage);

                Dispatcher.Invoke(() =>
                {
                    _gameStatusViewModel.IsGameTweaksEnabled = false;
                    UpdateStatus("Loading packages...");
                });

                _config.EnginePackagePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "Engine.u");
                _config.TdGamePackagePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");

                _package = _packageService.LoadPackage(_config.EnginePackagePath);
                _tdGamePackage = _packageService.LoadPackage(_config.TdGamePackagePath);

                if (_package == null || _tdGamePackage == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        DialogHelper.ShowMessage("Error", "Failed to load one or more packages (Engine.u, TdGame.u).", DialogHelper.MessageType.Error);
                        UpdateStatus("Failed to load packages.");
                    });
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    DetectTdGameVersion();
                });
                SetupEditors();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    DialogHelper.ShowMessage("Error", $"An error occurred: {ex.Message}", DialogHelper.MessageType.Error);
                    _gameStatusViewModel.Status = "Error loading packages.";
                });
                _package = null;
                _tdGamePackage = null;
            }
        }

        private void SetupEditors()
        {
            if (_package == null || _tdGamePackage == null) return;

            bool fovSuccess = SetupFovEditor();
            bool arSuccess = SetupAspectRatioEditor();
            bool consoleSuccess = SetupConsoleEditor();
            bool unlockedConfigsSuccess = SetupUnlockedConfigsEditor();
            Dispatcher.Invoke(() => UpdateTweaksScriptsStatus());

            Dispatcher.Invoke(() =>
            {
                if (fovSuccess || arSuccess || consoleSuccess)
                {
                    GameTweaksGrid.IsEnabled = true;
                    if (!_isProcessingGameDirectory)
                    {
                        UpdateStatusDisplays();
                        UpdateStatus("Ready.");
                    }
                }
                else
                {
                    GameTweaksGrid.IsEnabled = false;
                    if (!_isProcessingGameDirectory)
                    {
                        DialogHelper.ShowMessage("Warning", "Could not locate any editable properties in the game files.", DialogHelper.MessageType.Warning);
                        UpdateStatus("Ready.");
                    }
                }
            });
        }

        private void UpdateStatus(string status)
        {
            _gameStatusViewModel.Status = status;
            StatusTextBlock.Text = status;
        }

        private void ShowProgress(string message, bool isIndeterminate)
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = message;
                DownloadProgressBar.Visibility = System.Windows.Visibility.Visible;
                DownloadProgressBar.IsIndeterminate = isIndeterminate;
                if (!isIndeterminate)
                {
                    DownloadProgressBar.Value = 0;
                }
            });
        }

        private void HideProgress()
        {
            Dispatcher.Invoke(() =>
            {
                DownloadProgressBar.Visibility = System.Windows.Visibility.Collapsed;
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = 0;
                StatusTextBlock.Text = "Ready.";
            });
        }

        private void UpdateStatusDisplays()
        {
            try
            {
                if (_config.TdGamePackagePath != null && File.Exists(_config.TdGamePackagePath))
                {
                    var tdState = TdGamePatcher.DetectState(_config.TdGamePackagePath);
                    CompensatedClipCheckBox.IsChecked = tdState.ClipApplied;
                    FovAgnosticSensCheckBox.IsChecked = tdState.SensApplied;
                    _isLoadingInitSettings = true;
                    try { SkipOnlineCheckComboBox.SelectedIndex = tdState.OnlineSkipApplied ? 1 : 0; }
                    finally { _isLoadingInitSettings = false; }
                }

                UpdateScalingStatus();
            }
            catch
            {
            }
        }

        private void UpdateScalingStatus()
        {
            bool patchesApplied = false;
            if (_config.EnginePackagePath != null && File.Exists(_config.EnginePackagePath))
            {
                try { patchesApplied = EnginePatcher.DetectState(_config.EnginePackagePath) == EnginePatchState.FullyPatched; }
                catch { }
            }

            if (!patchesApplied)
            {
                HorPlusStatus.Text = "";
                return;
            }

            ResolutionHelper.Resolution? res = null;
            if (ResolutionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem
                && selectedItem.Tag is ResolutionHelper.Resolution r)
            {
                res = r;
            }

            if (res == null || res.Height == 0)
            {
                HorPlusStatus.Text = "";
                return;
            }

            double ar = (double)res.Width / res.Height;
            const double baseline = 16.0 / 9.0;
            const double tolerance = 0.01;

            if (ar > baseline + tolerance)
            {
                if (float.TryParse(NewFovValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float baseFov) && baseFov > 0)
                {
                    double effectiveFov = ComputeHorPlusFov(baseFov, ar);
                    HorPlusStatus.Text = $"(HOR+ \u2192 {effectiveFov:F0}°)";
                }
                else
                {
                    HorPlusStatus.Text = "(HOR+)";
                }
            }
            else if (ar < baseline - tolerance)
            {
                HorPlusStatus.Text = "(VERT+)";
            }
            else
            {
                HorPlusStatus.Text = "";
            }
        }

        private static double ComputeHorPlusFov(double baseFov, double aspectRatio)
        {
            const double baseline = 16.0 / 9.0;
            double halfRad = baseFov * Math.PI / 360.0;
            return Math.Atan(Math.Tan(halfRad) * aspectRatio / baseline) * 360.0 / Math.PI;
        }

        private bool SetupFovEditor()
        {
            if (_package == null || _tdGamePackage == null) return false;

            _offsets.PlayerControllerDefaultFovOffset = -1;
            _offsets.PlayerControllerDesiredFovOffset = -1;
            _offsets.PlayerControllerFovAngleOffset = -1;
            _offsets.CameraFovOffset = -1;
            _offsets.CameraActorFovAngleOffset = -1;
            _offsets.SeqActCameraFovOffset = -1;
            _offsets.UnzoomFovRateOffset = -1;
            _offsets.TdMoveVertigoZoomFovOffset = -1;
            _offsets.TdMoveVertigoZoomFovFlagsOffset = -1;
            _offsets.NearClippingPlaneOffset = -1;
            _offsets.FovScaleMultiplierOffset = -1;

            Dispatcher.Invoke(() =>
            {
                _fovViewModel.CurrentFovValue = "N/A";
                CurrentFovValue.Text = "N/A";
            });

            var playerControllerClass = _package.FindObject<UClass>("PlayerController");
            if (playerControllerClass?.Default is UObject playerControllerCDO)
            {
                playerControllerCDO.Load<UObjectRecordStream>();
                var defaultFovProp = playerControllerCDO.Properties.FirstOrDefault(p => p.Name == "DefaultFOV");
                if (defaultFovProp != null && float.TryParse(defaultFovProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentDefaultFov))
                    _offsets.PlayerControllerDefaultFovOffset = _offsetFinderService.FindPropertyOffsetByName(playerControllerCDO, "DefaultFOV", currentDefaultFov, _package, _config.EnginePackagePath);

                var desiredFovProp = playerControllerCDO.Properties.FirstOrDefault(p => p.Name == "DesiredFOV");
                if (desiredFovProp != null && float.TryParse(desiredFovProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentDesiredFov))
                    _offsets.PlayerControllerDesiredFovOffset = _offsetFinderService.FindPropertyOffsetByName(playerControllerCDO, "DesiredFOV", currentDesiredFov, _package, _config.EnginePackagePath);

                var fovAngleProp = playerControllerCDO.Properties.FirstOrDefault(p => p.Name == "FOVAngle");
                if (fovAngleProp != null && float.TryParse(fovAngleProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentFovAngle))
                    _offsets.PlayerControllerFovAngleOffset = _offsetFinderService.FindPropertyOffsetByName(playerControllerCDO, "FOVAngle", currentFovAngle, _package, _config.EnginePackagePath);
            }

            var cameraClass = _package.FindObject<UClass>("Camera");
            if (cameraClass?.Default is UObject cameraCDO)
            {
                cameraCDO.Load<UObjectRecordStream>();
                var fovProperty = cameraCDO.Properties.FirstOrDefault(p => p.Name == "DefaultFOV");
                if (fovProperty != null && float.TryParse(fovProperty.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentFov))
                {
                    Dispatcher.Invoke(() =>
                    {
                        string fovValue = Math.Round(currentFov).ToString(CultureInfo.InvariantCulture) + "° (horizontal)";
                        _fovViewModel.CurrentFovValue = fovValue;
                        CurrentFovValue.Text = fovValue;

                        if (string.IsNullOrEmpty(NewFovValue.Text) || NewFovValue.Text == "90" || NewFovValue.Text == "N/A")
                        {
                            NewFovValue.Text = Math.Round(currentFov).ToString(CultureInfo.InvariantCulture);
                        }
                    });

                    _offsets.CameraFovOffset = _offsetFinderService.FindPropertyOffsetByName(cameraCDO, "DefaultFOV", currentFov, _package, _config.EnginePackagePath);
                }
            }

            var cameraActorClass = _package.FindObject<UClass>("CameraActor");
            if (cameraActorClass?.Default is UObject cameraActorCDO)
            {
                cameraActorCDO.Load<UObjectRecordStream>();
                var fovAngleProperty = cameraActorCDO.Properties.FirstOrDefault(p => p.Name == "FOVAngle");
                if (fovAngleProperty != null && float.TryParse(fovAngleProperty.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentFovAngle))
                    _offsets.CameraActorFovAngleOffset = _offsetFinderService.FindPropertyOffsetByName(cameraActorCDO, "FOVAngle", currentFovAngle, _package, _config.EnginePackagePath);
            }

            var seqActClass = _tdGamePackage.FindObject<UClass>("SeqAct_TdCameraFOV");
            if (seqActClass?.Default is UObject seqActCDO)
            {
                seqActCDO.Load<UObjectRecordStream>();
                var newFovProp = seqActCDO.Properties.FirstOrDefault(p => p.Name == "NewFOV");
                if (newFovProp != null && float.TryParse(newFovProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentNewFov))
                    _offsets.SeqActCameraFovOffset = _offsetFinderService.FindPropertyOffsetByName(seqActCDO, "NewFOV", currentNewFov, _tdGamePackage, _config.TdGamePackagePath);
            }

            var tdMoveVertigoClass = _tdGamePackage.FindObject<UClass>("TdMove_Vertigo");
            if (tdMoveVertigoClass != null)
            {
                var zoomFovProp = tdMoveVertigoClass.EnumerateFields<UProperty>().FirstOrDefault(p => p.Name == "ZoomFOV");
                if (zoomFovProp != null)
                {
                    if (tdMoveVertigoClass.Default is UObject tdMoveVertigoCDO)
                    {
                        tdMoveVertigoCDO.Load<UObjectRecordStream>();
                        var zoomFovDefaultProp = tdMoveVertigoCDO.Properties.FirstOrDefault(p => p.Name == "ZoomFOV");
                        if (zoomFovDefaultProp != null && float.TryParse(zoomFovDefaultProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentZoomFov))
                            _offsets.TdMoveVertigoZoomFovOffset = _offsetFinderService.FindPropertyOffsetByName(tdMoveVertigoCDO, "ZoomFOV", currentZoomFov, _tdGamePackage, _config.TdGamePackagePath);
                    }
                    _originalZoomFovFlags = zoomFovProp.PropertyFlags;
                    _offsets.TdMoveVertigoZoomFovFlagsOffset = _offsetFinderService.FindPropertyFlagsOffset(zoomFovProp, _tdGamePackage, _config.TdGamePackagePath);
                }
            }

            var tdPlayerControllerClass = _tdGamePackage.FindObject<UClass>("TdPlayerController");
            if (tdPlayerControllerClass != null)
            {
                var unzoomFunc = tdPlayerControllerClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UnZoom");
                var fovZoomRateProp = tdPlayerControllerClass.EnumerateFields<UProperty>().FirstOrDefault(p => p.Name == "FOVZoomRate");
                if (unzoomFunc != null && fovZoomRateProp != null)
                {
                    unzoomFunc.Load<UObjectRecordStream>();
                    _offsets.UnzoomFovRateOffset = _offsetFinderService.FindFloatOffsetInBytecode(unzoomFunc, fovZoomRateProp);
                }
            }

            var tdHudClass = _tdGamePackage.FindObject<UClass>("TdHUD");
            if (tdHudClass != null)
            {
                var toggleZoomStateFunc = tdHudClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "ToggleZoomState");
                if (toggleZoomStateFunc != null)
                {
                    toggleZoomStateFunc.Load<UObjectRecordStream>();
                    _offsets.NearClippingPlaneOffset = _offsetFinderService.FindClippingPlaneOffset(toggleZoomStateFunc);
                }
            }

            var tdPlayerInputClass = _tdGamePackage.FindObject<UClass>("TdPlayerInput");
            if (tdPlayerInputClass != null)
            {
                var playerInputFunc = tdPlayerInputClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "PlayerInput");
                if (playerInputFunc != null)
                {
                    playerInputFunc.Load<UObjectRecordStream>();
                    _offsets.FovScaleMultiplierOffset = _offsetFinderService.FindFovScaleMultiplierOffset(playerInputFunc);
                }
            }

            return _offsets.PlayerControllerDefaultFovOffset != -1 ||
                _offsets.PlayerControllerDesiredFovOffset != -1 ||
                _offsets.PlayerControllerFovAngleOffset != -1 ||
                _offsets.CameraFovOffset != -1 ||
                _offsets.CameraActorFovAngleOffset != -1 ||
                _offsets.SeqActCameraFovOffset != -1 ||
                _offsets.UnzoomFovRateOffset != -1 ||
                _offsets.TdMoveVertigoZoomFovOffset != -1 ||
                _offsets.NearClippingPlaneOffset != -1 ||
                _offsets.FovScaleMultiplierOffset != -1;
        }

        

        private bool SetupAspectRatioEditor()
        {
            if (_package == null) return false;

            _offsets.AspectRatioOffset = -1;

            var cameraClass = _package.FindObject<UClass>("Camera");
            if (cameraClass != null)
            {
                var updateCameraFunc = cameraClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UpdateCamera");
                var aspectRatioProperty = cameraClass.EnumerateFields<UProperty>().FirstOrDefault(p => p.Name == "ConstrainedAspectRatio");

                if (updateCameraFunc != null && aspectRatioProperty != null)
                {
                    updateCameraFunc.Load<UObjectRecordStream>();
                    _offsets.AspectRatioOffset = _offsetFinderService.FindFloatOffsetInBytecode(updateCameraFunc, aspectRatioProperty);
                    return _offsets.AspectRatioOffset != -1;
                }
            }
            return false;
        }

        private bool SetupConsoleEditor()
        {
            if (_package == null) return false;

            _offsets.ConsoleHeightOffset = -1;
            Dispatcher.Invoke(() =>
            {
                _consoleViewModel.IsInstallConsoleEnabled = false;
                _consoleViewModel.IsUninstallConsoleEnabled = false;
            });

            var consoleClass = _package.FindObject<UClass>("Console");
            var openState = consoleClass?.EnumerateFields<UState>().FirstOrDefault(s => s.Name == "Open");
            var postRenderFunc = openState?.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "PostRender_Console");

            if (postRenderFunc != null)
            {
                postRenderFunc.Load<UObjectRecordStream>();
                _offsets.ConsoleHeightOffset = _offsetFinderService.FindConsoleHeightOffset(postRenderFunc);
            }

            UpdateConsoleStatus();

            if (_offsets.ConsoleHeightOffset != -1)
            {
                _consoleViewModel.IsInstallConsoleEnabled = true;
                _consoleViewModel.IsUninstallConsoleEnabled = true;
                return true;
            }

            _consoleViewModel.ConsoleStatus = "(Status: Offset not found)";
            return false;
        }

        private void UpdateConsoleStatus()
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            if (_package == null || string.IsNullOrEmpty(_config.GameDirectoryPath)) return;

            string consoleFilePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "MirrorsEdgeConsole.u");
            bool fileExists = _fileService.FileExists(consoleFilePath);

            bool heightModified = false;
            if (_offsets.ConsoleHeightOffset != -1)
            {
                float currentHeightMultiplier = _offsetFinderService.ReadFloatFromPackage(_package, _offsets.ConsoleHeightOffset);

                if (Math.Abs(currentHeightMultiplier - 0.4f) < 0.001f)
                {
                    heightModified = true;
                }
            }

            bool configModified = false;
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configDirectory = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config");
                string tdEngineIniPath = !string.IsNullOrEmpty(_config.TdEngineIniPath)
                    ? _config.TdEngineIniPath
                    : Path.Combine(configDirectory, "TdEngine.ini");
                string tdInputIniPath = !string.IsNullOrEmpty(_config.TdInputIniPath)
                    ? _config.TdInputIniPath
                    : Path.Combine(configDirectory, "TdInput.ini");

                if (_fileService.FileExists(tdEngineIniPath) && _fileService.FileExists(tdInputIniPath))
                {
                    string? consoleClassName = ConfigFileHelper.ReadIniValue(tdEngineIniPath, "Engine.Engine", "ConsoleClassName");
                    string? typeKey = ConfigFileHelper.ReadIniValue(tdInputIniPath, "Engine.Console", "TypeKey");

                    if (!string.IsNullOrEmpty(consoleClassName) && consoleClassName.Equals("MirrorsEdgeConsole.MirrorsEdgeConsole", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(typeKey) && typeKey.Equals("Tab", StringComparison.OrdinalIgnoreCase))
                    {
                        configModified = true;
                    }
                }
            }
            catch
            {
            }

            if (fileExists && heightModified && configModified)
            {
                Dispatcher.Invoke(() =>
                {
                    _consoleViewModel.ConsoleStatus = "Installed";
                    _consoleViewModel.ConsoleStatusForeground = System.Windows.Media.Brushes.Green;
                    ConsoleStatus.Text = "Installed";
                    ConsoleStatus.Foreground = System.Windows.Media.Brushes.Green;
                });
            }
            else if (fileExists || heightModified || configModified)
            {
                Dispatcher.Invoke(() =>
                {
                    _consoleViewModel.ConsoleStatus = "Partially Installed";
                    _consoleViewModel.ConsoleStatusForeground = System.Windows.Media.Brushes.Orange;
                    ConsoleStatus.Text = "Partially Installed";
                    ConsoleStatus.Foreground = System.Windows.Media.Brushes.Orange;
                });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    _consoleViewModel.ConsoleStatus = "Not Installed";
                    _consoleViewModel.ConsoleStatusForeground = System.Windows.Media.Brushes.Gray;
                    ConsoleStatus.Text = "Not Installed";
                    ConsoleStatus.Foreground = System.Windows.Media.Brushes.Gray;
                });
            }
        }

        private bool IsTweaksScriptsInstalled()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                return false;
            }

            string scriptFilePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "MirrorsEdgeTweaksScripts.u");
            return _fileService.FileExists(scriptFilePath);
        }

        private void UpdateTweaksScriptsStatus()
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                Dispatcher.Invoke(() =>
                {
                    _tweaksScriptsViewModel.TweaksScriptsStatus = "N/A";
                    _tweaksScriptsViewModel.TweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;
                    TweaksScriptsStatus.Text = "N/A";
                    TweaksScriptsStatus.Foreground = System.Windows.Media.Brushes.Gray;
                });
                UpdateTweaksScriptsDependencyUI(isTweaksScriptsInstalled: false);
                return;
            }

            bool tweaksScriptsInstalled = IsTweaksScriptsInstalled();
            if (tweaksScriptsInstalled)
            {
                Dispatcher.Invoke(() =>
                {
                    _tweaksScriptsViewModel.TweaksScriptsStatus = "Installed";
                    _tweaksScriptsViewModel.TweaksScriptsStatusForeground = System.Windows.Media.Brushes.Green;
                    TweaksScriptsStatus.Text = "Installed";
                    TweaksScriptsStatus.Foreground = System.Windows.Media.Brushes.Green;
                });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    _tweaksScriptsViewModel.TweaksScriptsStatus = "Not Installed";
                    _tweaksScriptsViewModel.TweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;
                    TweaksScriptsStatus.Text = "Not Installed";
                    TweaksScriptsStatus.Foreground = System.Windows.Media.Brushes.Gray;
                });
            }

            UpdateTweaksScriptsDependencyUI(tweaksScriptsInstalled);
        }

        private void UpdateTweaksScriptsDependencyUI(bool isTweaksScriptsInstalled)
        {
            void ApplyDependencyState()
            {
                bool hasGameDirectory = !string.IsNullOrEmpty(_config.GameDirectoryPath);
                bool canInstallTweaksScriptsUi = isTweaksScriptsInstalled;

                InstallTweaksScriptsUIButton.IsEnabled = canInstallTweaksScriptsUi;
                InstallTweaksScriptsUIButton.ToolTip = canInstallTweaksScriptsUi
                    ? "Install Tweaks Scripts UI."
                    : "Install Tweaks Scripts first to enable this installer.";

                TweaksScriptsUIRequiresScriptsText.Visibility = hasGameDirectory && !canInstallTweaksScriptsUi
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (Dispatcher.CheckAccess())
            {
                ApplyDependencyState();
                return;
            }

            Dispatcher.Invoke(ApplyDependencyState);
        }

        

        private async void ApplyChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_config.EnginePackagePath == null || _config.TdGamePackagePath == null)
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            if (!float.TryParse(NewFovValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float newFov) || newFov < 80 || newFov > 179)
            {
                DialogHelper.ShowMessage("Invalid Input", "Please enter a valid number for the FOV (must be between 80 - 179).", DialogHelper.MessageType.Warning);
                return;
            }

            bool enableSens = FovAgnosticSensCheckBox.IsChecked == true;
            bool enableClip = CompensatedClipCheckBox.IsChecked == true;
            bool enableOnlineSkip = SkipOnlineCheckComboBox.SelectedIndex == 1;

            this.IsEnabled = false;
            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = true;
            UpdateStatus("Applying FOV settings...");

            try
            {
                await Task.Run(() =>
                {
                    _package?.Dispose();
                    _tdGamePackage?.Dispose();
                    _package = null;
                    _tdGamePackage = null;

                    EnginePatcher.Reconcile(_config.EnginePackagePath);

                    TdGamePatcher.Reconcile(_config.TdGamePackagePath, enableSens, enableClip, enableOnlineSkip);

                    string? exePath = FindGameExePath();
                    if (exePath != null)
                    {
                        try { ExePatcher.Reconcile(exePath); }
                        catch (OoaLicenseNotFoundException ooaEx)
                        {
                            Dispatcher.Invoke(() => DialogHelper.ShowMessage(
                                "EA App License Required",
                                ooaEx.Message,
                                DialogHelper.MessageType.Warning));
                        }
                        catch {}
                    }

                    Dispatcher.Invoke(() => LoadPackages());
                });

                await Task.Run(() =>
                {
                    if (_config.EnginePackagePath == null) return;

                    byte[] fovValueBytes = BitConverter.GetBytes(newFov);
                    using var stream = new FileStream(_config.EnginePackagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

                    if (_offsets.PlayerControllerDefaultFovOffset != -1)
                    {
                        stream.Position = _offsets.PlayerControllerDefaultFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (_offsets.PlayerControllerDesiredFovOffset != -1)
                    {
                        stream.Position = _offsets.PlayerControllerDesiredFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (_offsets.PlayerControllerFovAngleOffset != -1)
                    {
                        stream.Position = _offsets.PlayerControllerFovAngleOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (_offsets.CameraFovOffset != -1)
                    {
                        stream.Position = _offsets.CameraFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (_offsets.CameraActorFovAngleOffset != -1)
                    {
                        stream.Position = _offsets.CameraActorFovAngleOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }

                    Dispatcher.Invoke(() => SaveSettingsToIni());
                });

                UpdateStatus("Ready.");
                await DialogHelper.ShowMessageAsync("Success", "Successfully applied FOV patches.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                await DialogHelper.ShowMessageAsync("Save Error", $"Failed to apply changes: {ex.Message}", DialogHelper.MessageType.Error);
                UpdateStatus("Error applying changes.");
            }
            finally
            {
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                DownloadProgressBar.IsIndeterminate = false;

                await Task.Delay(500);
                await Task.Run(() => LoadPackages());

                this.IsEnabled = true;
            }
        }

        private string? FindGameExePath()
        {
            if (_config.GameDirectoryPath == null) return null;
            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            return File.Exists(exePath) ? exePath : null;
        }

        private async void TdGameVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_tdGameVersionViewModel.IsUpdatingComboBoxProgrammatically)
            {
                return;
            }

            if (TdGameVersionComboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Content is string selectedVersionName)
            {
                var result = await DialogHelper.ShowConfirmationAsync("Confirm Download", $"This will download and replace your current 'TdGame.u' file.\n\nThis action cannot be undone. Do you want to continue?");

                if (result)
                {
                    TdGameTouchpointSnapshot touchpointSnapshot = CaptureTdGameTouchpointSnapshot();

                    _packageService.DisposePackage(_package);
                    _packageService.DisposePackage(_tdGamePackage);
                    _package = null;
                    _tdGamePackage = null;

                    _gameStatusViewModel.IsGameTweaksEnabled = false;

                    await DownloadAndExtractTdGameAsync(selectedVersionName, touchpointSnapshot);
                }
            }
        }

        private async Task DownloadAndExtractTdGameAsync(string selectedVersionName, TdGameTouchpointSnapshot touchpointSnapshot)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Warning);
                return;
            }

            string gameVersion = _gameStatusViewModel.GameVersion;
            string? downloadUrl = GameVersionHelper.GetDownloadUrl(gameVersion, selectedVersionName);

            if (string.IsNullOrEmpty(downloadUrl))
            {
                DialogHelper.ShowMessage("URL Error", "Could not determine the download URL for the selected game version and TdGame variant.", DialogHelper.MessageType.Error);
                return;
            }

            _gameStatusViewModel.IsGameTweaksEnabled = false;
            _downloadProgressViewModel.IsDownloadProgressVisible = true;
            _downloadProgressViewModel.IsDownloadProgressIndeterminate = false;
            _gameStatusViewModel.Status = $"Downloading '{selectedVersionName}'...";

            this.IsEnabled = false;
            GameTweaksGrid.IsEnabled = false;
            DownloadProgressBar.Visibility = System.Windows.Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = false;
            UpdateStatus($"Downloading '{selectedVersionName}'...");

            bool installSucceeded = false;
            bool packagesLoadedAtEnd = false;
            TdGameTouchpointReapplyResult reapplyResult = new TdGameTouchpointReapplyResult();

            try
            {
                using (var client = new HttpClient())
                {
                    string tempZipPath = Path.Combine(Path.GetTempPath(), "temp_tdgame.zip");

                    using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            if (totalBytes.HasValue)
                            {
                                var totalBytesRead = 0L;
                                var buffer = new byte[8192];
                                int bytesRead;
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    var progressPercentage = (int)((double)totalBytesRead / totalBytes.Value * 100);
                                    _downloadProgressViewModel.DownloadProgressValue = progressPercentage;
                                    _gameStatusViewModel.Status = $"Downloading... {progressPercentage}%";
                                    DownloadProgressBar.Value = progressPercentage;
                                    UpdateStatus($"Downloading... {progressPercentage}%");
                                }
                            }
                            else
                            {
                                _gameStatusViewModel.Status = "Downloading... (size unknown)";
                                _downloadProgressViewModel.IsDownloadProgressIndeterminate = true;
                                DownloadProgressBar.IsIndeterminate = true;
                                UpdateStatus("Downloading... (size unknown)");
                                await stream.CopyToAsync(fileStream);
                                _downloadProgressViewModel.IsDownloadProgressIndeterminate = false;
                                DownloadProgressBar.IsIndeterminate = false;
                            }
                        }
                    }

                    _gameStatusViewModel.Status = "Extracting...";
                    _downloadProgressViewModel.DownloadProgressValue = 100;
                    DownloadProgressBar.Value = 100;
                    UpdateStatus("Extracting...");

                    ZipFile.ExtractToDirectory(tempZipPath, _config.GameDirectoryPath, true);

                    _gameStatusViewModel.Status = "Decompressing new package...";
                    UpdateStatus("Decompressing new package...");
                    string tdGamePackagePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");
                    _decompressionService.RunDecompressor(tdGamePackagePath);

                    File.Delete(tempZipPath);
                }

                installSucceeded = true;
            }
            catch (Exception ex)
            {
                _gameStatusViewModel.Status = "An error occurred during the download/extraction.";
                await DialogHelper.ShowMessageAsync("Error", $"An error occurred: {ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                try
                {
                    _downloadProgressViewModel.IsDownloadProgressVisible = false;
                    _downloadProgressViewModel.DownloadProgressValue = 0;

                    DownloadProgressBar.Visibility = System.Windows.Visibility.Collapsed;
                    DownloadProgressBar.Value = 0;
                    DownloadProgressBar.IsIndeterminate = false;

                    if (installSucceeded)
                    {
                        try
                        {
                            _gameStatusViewModel.Status = "Reloading packages...";
                            UpdateStatus("Reloading packages...");
                            await Task.Delay(500);
                            await Task.Run(() => LoadPackages());
                            packagesLoadedAtEnd = true;

                            _gameStatusViewModel.Status = "Reapplying TdGame-linked settings...";
                            UpdateStatus("Reapplying TdGame-linked settings...");
                            reapplyResult = await ReapplyTdGameTouchpointSnapshotAsync(touchpointSnapshot);

                            await Task.Delay(250);
                            await Task.Run(() => LoadPackages());
                            packagesLoadedAtEnd = true;

                            _gameStatusViewModel.Status = "Successfully installed.";
                            UpdateStatus("Successfully installed.");
                            await DialogHelper.ShowMessageAsync("Success", BuildTdGameInstallSuccessMessage(selectedVersionName, reapplyResult), DialogHelper.MessageType.Success);
                        }
                        catch (Exception ex)
                        {
                            _gameStatusViewModel.Status = "TdGame installed, but settings reapply hit an error.";
                            UpdateStatus("TdGame installed with warnings.");
                            await DialogHelper.ShowMessageAsync(
                                "Warning",
                                $"The TdGame version was installed successfully, but some TdGame-linked settings failed to be reapplied:\n\n{ex.Message}.",
                                DialogHelper.MessageType.Warning);
                        }
                    }

                    if (!packagesLoadedAtEnd)
                    {
                        await Task.Delay(500);
                        await Task.Run(() => LoadPackages());
                    }

                    if (installSucceeded && _package != null && _tdGamePackage != null)
                    {
                        _gameStatusViewModel.Status = "Ready.";
                        UpdateStatus("Ready.");
                    }
                }
                finally
                {
                    this.IsEnabled = true;
                }
            }
        }

        private TdGameTouchpointSnapshot CaptureTdGameTouchpointSnapshot()
        {
            TdGameTouchpointSnapshot snapshot = new TdGameTouchpointSnapshot
            {
                UniformSensitivityTargetValue = GetUniformSensitivityTargetValueFromUiSelection(),
                GamepadButtonType = GetGamepadButtonTypeFromUiSelection(),
                HasLoadLastCheckpointKeybind = !string.IsNullOrWhiteSpace(LoadLastCheckpointKeyTextBox.Text),
                HasRestartTimeTrialKeybind = !string.IsNullOrWhiteSpace(RestartTimeTrialKeyTextBox.Text)
            };

            if (_config.TdGamePackagePath != null && File.Exists(_config.TdGamePackagePath))
            {
                try
                {
                    var patchState = TdGamePatcher.DetectState(_config.TdGamePackagePath);
                    snapshot.FovSnapshot.DynamicPatchesApplied = patchState.CoreApplied || patchState.SensApplied
                        || patchState.ClipApplied || patchState.OnlineSkipApplied;
                    snapshot.FovSnapshot.SensEnabled = patchState.SensApplied;
                    snapshot.FovSnapshot.ClipEnabled = patchState.ClipApplied;
                    snapshot.FovSnapshot.OnlineSkipEnabled = patchState.OnlineSkipApplied;
                }
                catch { }
            }
            if (float.TryParse(NewFovValue.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float baseFov))
            {
                snapshot.FovSnapshot.BaseFov = baseFov;
            }

            if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                try
                {
                    snapshot.WasHighResFixActive = _uiScalingService.IsUIScalingActive(_config.GameDirectoryPath);
                }
                catch
                {
                    snapshot.WasHighResFixActive = false;
                }
            }

            return snapshot;
        }

        

        private float? GetUniformSensitivityTargetValueFromUiSelection()
        {
            if (UniformSensitivityComboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Content is string content)
            {
                if (content.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                {
                    return 66536f;
                }

                if (content.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    return 16384f;
                }
            }

            return UniformSensitivityComboBox.SelectedIndex switch
            {
                0 => 66536f,
                1 => 16384f,
                _ => null
            };
        }

        private string? GetGamepadButtonTypeFromUiSelection()
        {
            return GamepadButtonsComboBox.SelectedIndex switch
            {
                0 => "xbox",
                1 => "ps3",
                _ => null
            };
        }

        private async Task<TdGameTouchpointReapplyResult> ReapplyTdGameTouchpointSnapshotAsync(TdGameTouchpointSnapshot snapshot)
        {
            TdGameTouchpointReapplyResult result = new TdGameTouchpointReapplyResult();

            if (string.IsNullOrEmpty(_config.TdGamePackagePath) || !File.Exists(_config.TdGamePackagePath))
            {
                result.FailedSettings.Add("TdGame.u was not found after installation");
                return result;
            }

            if (snapshot.FovSnapshot.HasValues)
            {
                try
                {
                    bool wroteFovValues = ReapplyTdGameFovSnapshot(_config.TdGamePackagePath, snapshot.FovSnapshot);
                    if (wroteFovValues)
                    {
                        result.ReappliedSettings.Add("FOV patches");
                    }
                }
                catch (Exception ex)
                {
                    result.FailedSettings.Add($"FOV-related TdGame offsets ({ex.Message})");
                }
            }

            if (snapshot.UniformSensitivityTargetValue.HasValue)
            {
                try
                {
                    float targetValue = snapshot.UniformSensitivityTargetValue.Value;
                    await Task.Run(() => ApplyUniformSensitivity(_config.TdGamePackagePath, targetValue));
                    result.ReappliedSettings.Add("Uniform sensitivity");
                }
                catch (Exception ex)
                {
                    result.FailedSettings.Add($"Uniform sensitivity ({ex.Message})");
                }
            }

            if (!string.IsNullOrEmpty(snapshot.GamepadButtonType))
            {
                try
                {
                    string buttonType = snapshot.GamepadButtonType;
                    await Task.Run(() => ApplyControllerImagePathSwap(_config.TdGamePackagePath, buttonType));
                    result.ReappliedSettings.Add("Gamepad prompt mode");
                }
                catch (Exception ex)
                {
                    result.FailedSettings.Add($"Gamepad prompt mode ({ex.Message})");
                }
            }

            if (snapshot.HasLoadLastCheckpointKeybind)
            {
                try
                {
                    await AddExecFlagToFunction("TdSPGame", "RestartFromLastCheckpoint");
                    result.ReappliedSettings.Add("Load Last Checkpoint exec patch");
                }
                catch (Exception ex)
                {
                    result.FailedSettings.Add($"Load Last Checkpoint exec patch ({ex.Message})");
                }
            }

            if (snapshot.HasRestartTimeTrialKeybind)
            {
                try
                {
                    await AddExecFlagToFunction("TdTimeTrialHUD", "TriggerRestartRaceblink");
                    result.ReappliedSettings.Add("Restart Time Trial exec patch");
                }
                catch (Exception ex)
                {
                    result.FailedSettings.Add($"Restart Time Trial exec patch ({ex.Message})");
                }
            }

            if (snapshot.WasHighResFixActive)
            {
                try
                {
                    await ReapplyHighResUIFixIfNeeded(snapshot.WasHighResFixActive, showDialogs: false);
                    result.ReappliedSettings.Add("Crosshair and cursor scaling (high-res UI fix)");
                }
                catch (Exception ex)
                {
                    result.FailedSettings.Add($"Crosshair and cursor scaling ({ex.Message})");
                }
            }

            List<string> distinctReapplied = result.ReappliedSettings.Distinct(StringComparer.Ordinal).ToList();
            result.ReappliedSettings.Clear();
            result.ReappliedSettings.AddRange(distinctReapplied);
            return result;
        }

        private bool ReapplyTdGameFovSnapshot(string tdGamePackagePath, TdGameFovTouchpointSnapshot snapshot)
        {
            if (!snapshot.DynamicPatchesApplied) return false;

            FileAttributes attributes = File.GetAttributes(tdGamePackagePath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

            if (wasReadOnly)
                File.SetAttributes(tdGamePackagePath, attributes & ~FileAttributes.ReadOnly);

            try
            {
                TdGamePatcher.Apply(tdGamePackagePath, snapshot.SensEnabled, snapshot.ClipEnabled, snapshot.OnlineSkipEnabled);
                return true;
            }
            finally
            {
                if (wasReadOnly)
                    File.SetAttributes(tdGamePackagePath, attributes);
            }
        }

        

        private static string BuildTdGameInstallSuccessMessage(string selectedVersionName, TdGameTouchpointReapplyResult reapplyResult)
        {
            string message = $"Successfully downloaded and installed '{selectedVersionName}' TdGame version.";

            if (reapplyResult.FailedSettings.Count > 0)
            {
                message += "\n\nSome TdGame-linked settings could not be restored automatically. " +
                           "Please review your settings in the UI and reapply if needed.";
            }

            return message;
        }


        private void DetectTdGameVersion()
        {
            if (string.IsNullOrEmpty(_config.TdGamePackagePath) || !_fileService.FileExists(_config.TdGamePackagePath))
            {
                SetComboBoxSelection("Unknown");
                return;
            }

            try
            {
                string detectedVersion = TdGameVersionDetector.DetectTdGameVersion(_config.TdGamePackagePath);
                SetComboBoxSelection(detectedVersion);
            }
            catch (Exception ex)
            {
                _gameStatusViewModel.Status = $"Error detecting TdGame version: {ex.Message}";
                SetComboBoxSelection("Unknown");
            }
        }


        private void SetComboBoxSelection(string versionString)
        {
            _tdGameVersionViewModel.IsUpdatingComboBoxProgrammatically = true;
            try
            {
                bool found = false;
                foreach (var item in TdGameVersionComboBox.Items)
                {
                    if (item is ComboBoxItem cbi && cbi.Content is string content && content.Equals(versionString))
                    {
                        TdGameVersionComboBox.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    TdGameVersionComboBox.SelectedIndex = -1;
                }
            }
            finally
            {
                _tdGameVersionViewModel.IsUpdatingComboBoxProgrammatically = false;
            }
        }


        private void ModifyIniFile(string filePath, string section, string key, string value)
        {
            ConfigFileHelper.ModifyIniFile(filePath, section, key, value);
        }

        private async void InstallConsole_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath) || string.IsNullOrEmpty(_config.TdEngineIniPath) || string.IsNullOrEmpty(_config.TdInputIniPath) || string.IsNullOrEmpty(_config.EnginePackagePath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                return;
            }
            if (_offsets.ConsoleHeightOffset == -1)
            {
                DialogHelper.ShowMessage("Patch Error", "Could not find the necessary location to patch in Engine.u. Cannot proceed.", DialogHelper.MessageType.Error);
                return;
            }

            _gameStatusViewModel.IsGameTweaksEnabled = false;
            _downloadProgressViewModel.IsDownloadProgressVisible = true;
            _downloadProgressViewModel.DownloadProgressValue = 0;

            GameTweaksGrid.IsEnabled = false;
            DownloadProgressBar.Visibility = System.Windows.Visibility.Visible;
            DownloadProgressBar.Value = 0;

            try
            {
                await Task.Run(async () =>
                {
                    Dispatcher.Invoke(() => UpdateStatus("Installing console..."));
                    ModifyIniFile(_config.TdEngineIniPath, "Engine.Engine", "ConsoleClassName", "MirrorsEdgeConsole.MirrorsEdgeConsole");
                    ModifyIniFile(_config.TdInputIniPath, "Engine.Console", "TypeKey", "Tab");

                    const string consoleUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/MirrorsEdgeConsole.zip";
                    string tempZipPath = Path.Combine(Path.GetTempPath(), "MirrorsEdgeConsole.zip");

                    using (var client = new HttpClient())
                    using (var response = await client.GetAsync(consoleUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            if (!totalBytes.HasValue)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    _downloadProgressViewModel.IsDownloadProgressIndeterminate = true;
                                    DownloadProgressBar.IsIndeterminate = true;
                                });
                                await stream.CopyToAsync(fileStream);
                            }
                            else
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    _downloadProgressViewModel.IsDownloadProgressIndeterminate = false;
                                    DownloadProgressBar.IsIndeterminate = false;
                                });
                                var totalBytesRead = 0L;
                                var buffer = new byte[8192];
                                int bytesRead;
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    var progress = (int)((double)totalBytesRead / totalBytes.Value * 100);

                                    Dispatcher.Invoke(() =>
                                    {
                                        _downloadProgressViewModel.DownloadProgressValue = progress;
                                        DownloadProgressBar.Value = progress;
                                        UpdateStatus($"Downloading console... {progress}%");
                                    });
                                }
                            }
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("Extracting console files...");
                        _downloadProgressViewModel.IsDownloadProgressIndeterminate = true;
                        DownloadProgressBar.IsIndeterminate = true;
                    });

                    ZipFile.ExtractToDirectory(tempZipPath, _config.GameDirectoryPath, true);
                    File.Delete(tempZipPath);

                    Dispatcher.Invoke(() => UpdateStatus("Patching Engine.u..."));

                    _packageService.DisposePackage(_package);
                    _package = null;
                    using (var stream = new FileStream(_config.EnginePackagePath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        stream.Position = _offsets.ConsoleHeightOffset;
                        byte[] newValue = BitConverter.GetBytes(0.4f);
                        stream.Write(newValue, 0, newValue.Length);
                    }

                    string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                    if (SetCommandPatchHelper.IsDlcVersion(exePath))
                    {
                        Dispatcher.Invoke(() => UpdateStatus("Applying DLC set/setnopec fix..."));
                    }

                    SetCommandPatchHelper.EnsurePatchedIfApplicable(exePath);
                });

                UpdateStatus("Ready.");
                await DialogHelper.ShowMessageAsync("Success",
                    "Developer console successfully installed. Use the Tilde (~) key to open the console.\n\n" +
                    "Please note that Unreal Engine 3 supports only the US keyboard layout. If you do not wish to use the US layout, the following layouts will interpret these keys as Tilde:\n\n" +
                    "• UK: @ (At sign)\n\n" +
                    "• German: ö\n\n" +
                    "• French: ù (% key)\n\n" +
                    "• Spanish: ñ\n\n" +
                    "• Italian: \\ (backslash)",
                    DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                await DialogHelper.ShowMessageAsync("Installation Failed", $"An error occurred during installation: {ex.Message}", DialogHelper.MessageType.Error);
                UpdateStatus("Console installation failed.");
            }
            finally
            {
                _downloadProgressViewModel.IsDownloadProgressVisible = false;

                DownloadProgressBar.Visibility = System.Windows.Visibility.Collapsed;
                DownloadProgressBar.Value = 0;
                DownloadProgressBar.IsIndeterminate = false;

                await Task.Delay(500);
                await Task.Run(() => LoadPackages());
            }
        }

        private async void UninstallConsole_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath) || string.IsNullOrEmpty(_config.TdEngineIniPath) || string.IsNullOrEmpty(_config.TdInputIniPath) || string.IsNullOrEmpty(_config.EnginePackagePath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                return;
            }
            if (_offsets.ConsoleHeightOffset == -1)
            {
                DialogHelper.ShowMessage("Patch Error", "Could not find the necessary location to patch in Engine.u. Cannot proceed.", DialogHelper.MessageType.Error);
                return;
            }

            var result = await DialogHelper.ShowConfirmationAsync("Confirm Uninstall", "This will revert all changes made by the console installation. Are you sure you want to continue?");

            if (!result)
            {
                return;
            }

            _gameStatusViewModel.IsGameTweaksEnabled = false;
            UpdateStatus("Uninstalling console...");

            try
            {
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() => UpdateStatus("Reverting config files..."));
                    ModifyIniFile(_config.TdEngineIniPath, "Engine.Engine", "ConsoleClassName", "TdGame.TdConsole");
                    ModifyIniFile(_config.TdInputIniPath, "Engine.Console", "TypeKey", "None");

                    Dispatcher.Invoke(() => UpdateStatus("Deleting console package..."));
                    string consolePackagePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "MirrorsEdgeConsole.u");
                    if (_fileService.FileExists(consolePackagePath))
                    {
                        _fileService.DeleteFile(consolePackagePath);
                    }

                    Dispatcher.Invoke(() => UpdateStatus("Patching Engine.u..."));
                    _packageService.DisposePackage(_package);
                    _package = null;
                    using (var stream = new FileStream(_config.EnginePackagePath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        stream.Position = _offsets.ConsoleHeightOffset;
                        byte[] originalValue = BitConverter.GetBytes(0.75f);
                        stream.Write(originalValue, 0, originalValue.Length);
                    }
                });

                UpdateStatus("Ready.");
                await DialogHelper.ShowMessageAsync("Success", "Developer console uninstalled.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                await DialogHelper.ShowMessageAsync("Uninstallation Failed", $"An error occurred during uninstallation: {ex.Message}", DialogHelper.MessageType.Error);
                UpdateStatus("Console uninstallation failed.");
            }
            finally
            {
                await Task.Delay(500);
                await Task.Run(() => LoadPackages());
            }
        }

        private async void InstallTweaksScripts_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Warning);
                return;
            }

            const string downloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/MirrorsEdgeTweaksScripts.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), "MirrorsEdgeTweaksScripts.zip");

            _gameStatusViewModel.IsGameTweaksEnabled = false;
            _downloadProgressViewModel.IsDownloadProgressVisible = true;
            _downloadProgressViewModel.DownloadProgressValue = 0;
            _downloadProgressViewModel.IsDownloadProgressIndeterminate = true;
            UpdateStatus("Downloading Tweaks Scripts...");

            GameTweaksGrid.IsEnabled = false;
            DownloadProgressBar.Visibility = System.Windows.Visibility.Visible;
            DownloadProgressBar.Value = 0;
            DownloadProgressBar.IsIndeterminate = true;

            try
            {
                await Task.Run(async () =>
                {
                    using (var client = new HttpClient())
                    {
                        using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            var totalBytes = response.Content.Headers.ContentLength;

                            using (var stream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                            {
                                if (totalBytes.HasValue)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        _downloadProgressViewModel.IsDownloadProgressIndeterminate = false;
                                        DownloadProgressBar.IsIndeterminate = false;
                                    });

                                    var totalBytesRead = 0L;
                                    var buffer = new byte[8192];
                                    int bytesRead;
                                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                    {
                                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                                        totalBytesRead += bytesRead;
                                        var progress = (int)((double)totalBytesRead / totalBytes.Value * 100);

                                        Dispatcher.Invoke(() =>
                                        {
                                            _downloadProgressViewModel.DownloadProgressValue = progress;
                                            DownloadProgressBar.Value = progress;
                                            UpdateStatus($"Downloading Tweaks Scripts... {progress}%");
                                        });
                                    }
                                }
                                else
                                {
                                    await stream.CopyToAsync(fileStream);
                                }
                            }
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("Extracting files...");
                        _downloadProgressViewModel.DownloadProgressValue = 100;
                        DownloadProgressBar.Value = 100;
                    });

                    // Backup existing TweaksScriptsSettings if present
                    string binariesPath = Path.Combine(_config.GameDirectoryPath, "Binaries");
                    string settingsPath = Path.Combine(binariesPath, "TweaksScriptsSettings");
                    Dictionary<string, string> existingSettings = new Dictionary<string, string>();

                    if (File.Exists(settingsPath))
                    {
                        var lines = File.ReadAllLines(settingsPath);
                        foreach (var line in lines)
                        {
                            var trimmedLine = line.Trim();
                            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("//")) continue;

                            var parts = trimmedLine.Split(new[] { ' ' }, 2);
                            if (parts.Length > 0)
                            {
                                string key = parts[0];
                                string value = parts.Length > 1 ? parts[1] : string.Empty;
                                existingSettings[key] = value;
                            }
                        }
                    }

                    ZipFile.ExtractToDirectory(tempZipPath, _config.GameDirectoryPath, true);
                    File.Delete(tempZipPath);

                    if (existingSettings.Count > 0 && File.Exists(settingsPath))
                    {
                        var newLines = File.ReadAllLines(settingsPath).ToList();
                        var updatedLines = new List<string>();

                        foreach (var line in newLines)
                        {
                            var trimmedLine = line.Trim();
                            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("//"))
                            {
                                updatedLines.Add(line);
                                continue;
                            }

                            var parts = trimmedLine.Split(new[] { ' ' }, 2);
                            if (parts.Length > 0)
                            {
                                string key = parts[0];
                                if (existingSettings.ContainsKey(key))
                                {
                                    string oldValue = existingSettings[key];
                                    string newLine = string.IsNullOrEmpty(oldValue) ? key : $"{key} {oldValue}";
                                    updatedLines.Add(newLine);
                                }
                                else
                                {
                                    updatedLines.Add(line);
                                }
                            }
                            else
                            {
                                updatedLines.Add(line);
                            }
                        }

                        File.WriteAllLines(settingsPath, updatedLines);
                    }

                    string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                    if (SetCommandPatchHelper.IsDlcVersion(exePath))
                    {
                        Dispatcher.Invoke(() => UpdateStatus("Applying DLC set/setnopec fix..."));
                    }

                    SetCommandPatchHelper.EnsurePatchedIfApplicable(exePath);
                });

                UpdateStatus("Ready.");
                UpdateTweaksScriptsStatus();
                await DialogHelper.ShowMessageAsync("Success",
                    "Tweaks Scripts successfully downloaded and installed.",
                    DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                UpdateStatus("An error occurred during installation.");
                await DialogHelper.ShowMessageAsync("Error", $"An error occurred: {ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                _gameStatusViewModel.IsGameTweaksEnabled = true;
                _downloadProgressViewModel.IsDownloadProgressVisible = false;
                _downloadProgressViewModel.DownloadProgressValue = 0;

                GameTweaksGrid.IsEnabled = true;
                DownloadProgressBar.Visibility = System.Windows.Visibility.Collapsed;
                DownloadProgressBar.Value = 0;
                DownloadProgressBar.IsIndeterminate = false;

                UpdateTweaksScriptsStatus();
            }
        }

        private async void UninstallTweaksScripts_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Warning);
                return;
            }

            var result = await DialogHelper.ShowConfirmationAsync("Confirm Uninstall", "This will delete the Tweaks Scripts files from your game directory. Are you sure?");

            if (!result)
            {
                return;
            }

            try
            {
                int filesDeleted = await Task.Run(() =>
                {
                    string cookedPcPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC");
                    string binariesPath = Path.Combine(_config.GameDirectoryPath, "Binaries");

                    string scriptFile = Path.Combine(cookedPcPath, "MirrorsEdgeTweaksScripts.u");

                    var binaryFiles = new List<string>
                    {
                        "Cheats",
                        "CheatsOff",
                        "Speedrun",
                        "SpeenrunOff",
                        "TimeTrialOrder",
                        "TrainerHUD",
                        "TrainerHUDOff",
                        "TweaksScriptsSettings"
                    };

                    int deletedCount = 0;

                    if (_fileService.FileExists(scriptFile))
                    {
                        _fileService.DeleteFile(scriptFile);
                        deletedCount++;
                    }

                    foreach (var fileName in binaryFiles)
                    {
                        string filePath = Path.Combine(binariesPath, fileName);
                        if (_fileService.FileExists(filePath))
                        {
                            _fileService.DeleteFile(filePath);
                            deletedCount++;
                        }
                    }

                    return deletedCount;
                });

                UpdateStatus("Ready.");
                UpdateTweaksScriptsStatus();

                if (filesDeleted > 0)
                {
                    await DialogHelper.ShowMessageAsync("Success", $"Successfully uninstalled Tweaks Scripts ({filesDeleted} files removed).", DialogHelper.MessageType.Success);
                }
                else
                {
                    await DialogHelper.ShowMessageAsync("Not Found", "No Tweaks Scripts files were found to uninstall.", DialogHelper.MessageType.Information);
                }
            }
            catch (Exception ex)
            {
                await DialogHelper.ShowMessageAsync("Error", $"An error occurred during uninstallation: {ex.Message}", DialogHelper.MessageType.Error);
                _gameStatusViewModel.Status = "Error during uninstallation.";
            }
            finally
            {
                UpdateTweaksScriptsStatus();
            }
        }
        private void SetUnlockedConfigsState(string status, System.Windows.Media.Brush foreground, bool isPatchEnabled, bool isUnpatchEnabled)
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                _unlockedConfigsViewModel.UnlockedConfigsStatus = status;
                _unlockedConfigsViewModel.UnlockedConfigsStatusForeground = foreground;
                _unlockedConfigsViewModel.IsPatchConfigsEnabled = isPatchEnabled;
                _unlockedConfigsViewModel.IsUnpatchConfigsEnabled = isUnpatchEnabled;
                UnlockedConfigsStatus.Text = status;
                UnlockedConfigsStatus.Foreground = foreground;
                PatchConfigsButton.IsEnabled = isPatchEnabled;
                UnpatchConfigsButton.IsEnabled = isUnpatchEnabled;
            });
        }

        private bool SetupUnlockedConfigsEditor()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                return false;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                SetUnlockedConfigsState("N/A (EXE not found)", System.Windows.Media.Brushes.Gray, false, false);
                return false;
            }

            try
            {
                ConfigUnlockState state = ConfigUnlockHelper.GetState(exePath);
                switch (state)
                {
                    case ConfigUnlockState.Patched:
                        SetUnlockedConfigsState("Patched", System.Windows.Media.Brushes.Green, true, true);
                        return true;
                    case ConfigUnlockState.Unpatched:
                        SetUnlockedConfigsState("Unpatched", System.Windows.Media.Brushes.Gray, true, true);
                        return true;
                    case ConfigUnlockState.Mixed:
                        SetUnlockedConfigsState("Partially Patched", System.Windows.Media.Brushes.DarkOrange, true, true);
                        return true;
                    default:
                        SetUnlockedConfigsState("Not Applicable", System.Windows.Media.Brushes.Gray, false, false);
                        return false;
                }
            }
            catch (Exception ex)
            {
                SetUnlockedConfigsState("Error reading EXE", System.Windows.Media.Brushes.Red, false, false);
                Dispatcher.Invoke(() =>
                {
                    _gameStatusViewModel.Status = $"Error checking config patch status: {ex.Message}";
                });
                return false;
            }
        }

        private void PatchConfigs_Click(object sender, RoutedEventArgs e)
        {
            ModifyExeConfigPatch(unlock: true);
        }

        private void UnpatchConfigs_Click(object sender, RoutedEventArgs e)
        {
            ModifyExeConfigPatch(unlock: false);
        }

        private async void ModifyExeConfigPatch(bool unlock)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Game directory not selected.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                DialogHelper.ShowMessage("Error", "MirrorsEdge.exe not found.", DialogHelper.MessageType.Error);
                return;
            }

            ConfigUnlockState currentState;
            try
            {
                currentState = ConfigUnlockHelper.GetState(exePath);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to read the executable state: {ex.Message}", DialogHelper.MessageType.Error);
                return;
            }

            if (currentState == ConfigUnlockState.NotApplicable)
            {
                DialogHelper.ShowMessage("Not Applicable", "Config patching is not applicable for this executable.", DialogHelper.MessageType.Warning);
                return;
            }

            if (unlock && currentState == ConfigUnlockState.Patched)
            {
                DialogHelper.ShowMessage("No Action Needed", "Configs are already patched.", DialogHelper.MessageType.Information);
                return;
            }

            if (!unlock && currentState == ConfigUnlockState.Unpatched)
            {
                DialogHelper.ShowMessage("No Action Needed", "Configs are already unpatched.", DialogHelper.MessageType.Information);
                return;
            }

            this.IsEnabled = false;
            string actionText = unlock ? "Patching" : "Unpatching";
            ShowProgress($"{actionText} configs...", true);

            try
            {
                bool patchChanged = await System.Threading.Tasks.Task.Run(() =>
                {
                    _package?.Dispose();
                    _tdGamePackage?.Dispose();
                    _package = null;
                    _tdGamePackage = null;

                    return unlock
                        ? ConfigUnlockHelper.Unlock(exePath)
                        : ConfigUnlockHelper.RestoreStock(exePath);
                });

                HideProgress();

                if (patchChanged)
                {
                    SetupUnlockedConfigsEditor();
                    string status = unlock ? "patched" : "unpatched";
                    await DialogHelper.ShowMessageAsync("Success", $"Successfully {status} unlocked configs.", DialogHelper.MessageType.Success);
                }
                else
                {
                    await DialogHelper.ShowMessageAsync("No Action Needed", "The executable is already in the desired config-unlock state.", DialogHelper.MessageType.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                await DialogHelper.ShowMessageAsync("Error", $"An error occurred while patching the executable: {ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                HideProgress();

                this.IsEnabled = true;
            }
        }

        private async void ApplyPhysXFPS_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string input = PhysXFPSTextBox.Text.Trim();

            if (string.IsNullOrEmpty(input))
            {
                DialogHelper.ShowMessage("Error", "PhysX FPS value not entered.", DialogHelper.MessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int physxFps))
            {
                DialogHelper.ShowMessage("Error", "Invalid PhysX FPS. Please enter a number between 50 and 300.", DialogHelper.MessageType.Error);
                return;
            }

            if (physxFps < 50)
            {
                DialogHelper.ShowMessage("Error", "PhysX framerate cannot be less than 50.", DialogHelper.MessageType.Error);
                return;
            }

            if (physxFps > 300)
            {
                DialogHelper.ShowMessage("Error", "PhysX framerate cannot be greater than 300.", DialogHelper.MessageType.Error);
                return;
            }

            this.IsEnabled = false;
            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = true;
            UpdateStatus("Applying PhysX FPS settings...");

            try
            {
                await Task.Run(() => ApplyPhysXFPS(physxFps));
                UpdateStatus("Ready.");
                await DialogHelper.ShowMessageAsync("Success", $"PhysX FPS set to {physxFps} successfully.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                UpdateStatus("Failed to apply PhysX FPS.");
                await DialogHelper.ShowMessageAsync("Error", $"Failed to apply PhysX FPS:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                DownloadProgressBar.IsIndeterminate = false;
                this.IsEnabled = true;
            }
        }

        private void ApplyPhysXFPS(int physxFps)
        {
            string enginePackagePath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC", "Engine.u");

            if (!File.Exists(enginePackagePath))
            {
                throw new FileNotFoundException("Engine.u file not found. Please ensure your game directory is correct.");
            }

            float physxTimestep = 1.0f / physxFps;

            // linear equation for skeletal mesh PhysX iterations - keeps cloth sim somewhat consistent
            int physxIterations = (int)(-0.016 * physxFps + 5.8);

            using var package = UnrealLoader.LoadPackage(enginePackagePath, FileAccess.Read);
            package?.InitializePackage();

            if (package == null)
            {
                throw new InvalidOperationException("Failed to load Engine.u package");
            }

            bool timestepModified = false;
            bool iterationsModified = false;

            var worldInfoClass = package.FindObject<UClass>("WorldInfo");
            if (worldInfoClass != null)
            {
                if (worldInfoClass.Default is UObject defaultObject)
                {
                    defaultObject.Load<UObjectRecordStream>();

                    if (defaultObject.ExportTable != null)
                    {
                        timestepModified = ModifyPhysicsTimingsTimeStep(enginePackagePath, defaultObject, physxTimestep);
                    }
                }
                else
                {
                    string defaultObjectName = "Default__WorldInfo";
                    var defaultObjectAlt = package.Objects.FirstOrDefault(o => o.Name == defaultObjectName);

                    if (defaultObjectAlt is UObject uObj && uObj.ExportTable != null)
                    {
                        uObj.Load<UObjectRecordStream>();
                        timestepModified = ModifyPhysicsTimingsTimeStep(enginePackagePath, uObj, physxTimestep);
                    }
                }
            }

            var skeletalMeshClass = package.FindObject<UClass>("SkeletalMesh");
            if (skeletalMeshClass != null)
            {
                if (skeletalMeshClass.Default is UObject defaultObject)
                {
                    defaultObject.Load<UObjectRecordStream>();

                    if (defaultObject.ExportTable != null)
                    {
                        iterationsModified = ModifyClothIterations(enginePackagePath, defaultObject, package, physxIterations);
                    }
                }
                else
                {
                    string defaultObjectName = "Default__SkeletalMesh";
                    var defaultObjectAlt = package.Objects.FirstOrDefault(o => o.Name == defaultObjectName);

                    if (defaultObjectAlt is UObject uObj && uObj.ExportTable != null)
                    {
                        uObj.Load<UObjectRecordStream>();
                        iterationsModified = ModifyClothIterations(enginePackagePath, uObj, package, physxIterations);
                    }
                }
            }

            if (!timestepModified)
            {
                throw new InvalidOperationException("Failed to locate PhysicsTimings CompartmentTimingCloth TimeStep in Engine.u");
            }

            if (!iterationsModified)
            {
                throw new InvalidOperationException("Failed to locate ClothIterations property in Engine.u");
            }
        }

        private bool ModifyPhysicsTimingsTimeStep(string filePath, UObject defaultObject, float timestep)
        {
            using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
            package?.InitializePackage();

            if (package == null)
                return false;

            int timeStepNameIndex = package.Names.FindIndex(n => n.ToString() == "TimeStep");
            if (timeStepNameIndex == -1)
                return false;

            byte[] timeStepNameBytes = BitConverter.GetBytes((long)timeStepNameIndex);
            byte[] timestepBytes = BitConverter.GetBytes(timestep);

            var exportTable = defaultObject.ExportTable;
            if (exportTable == null)
                return false;

            byte[] data = File.ReadAllBytes(filePath);

            long searchStart = exportTable.SerialOffset;
            long searchEnd = searchStart + exportTable.SerialSize;

            // looking for 4th occurrence of the TimeStep property (cloth)
            int occurrences = 0;

            for (long i = searchStart; i < searchEnd - 28; i++)
            {
                bool nameMatch = true;
                for (int j = 0; j < 8; j++)
                {
                    if (data[i + j] != timeStepNameBytes[j])
                    {
                        nameMatch = false;
                        break;
                    }
                }

                if (nameMatch)
                {
                    occurrences++;

                    if (occurrences == 4)
                    {
                        Array.Copy(timestepBytes, 0, data, i + 24, 4);

                        File.WriteAllBytes(filePath, data);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool ModifyClothIterations(string filePath, UObject defaultObject, UnrealPackage package, int iterations)
        {
            int nameIndex = package.Names.FindIndex(n => n.ToString() == "ClothIterations");
            if (nameIndex == -1)
                return false;

            byte[] nameIndexBytes = BitConverter.GetBytes((long)nameIndex);

            var exportTable = defaultObject.ExportTable;
            if (exportTable == null)
                return false;

            byte[] data = File.ReadAllBytes(filePath);

            long searchStart = exportTable.SerialOffset;
            long searchEnd = searchStart + exportTable.SerialSize;

            for (long i = searchStart; i < searchEnd - 28; i++)
            {
                bool nameMatch = true;
                for (int j = 0; j < 8; j++)
                {
                    if (data[i + j] != nameIndexBytes[j])
                    {
                        nameMatch = false;
                        break;
                    }
                }

                if (nameMatch)
                {
                    byte[] iterationsBytes = BitConverter.GetBytes(iterations);
                    Array.Copy(iterationsBytes, 0, data, i + 24, 4);

                    File.WriteAllBytes(filePath, data);
                    return true;
                }
            }

            return false;
        }

        private void LoadPhysXFPSSetting()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            try
            {
                string enginePackagePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "Engine.u");

                if (!File.Exists(enginePackagePath))
                    return;

                using var package = UnrealLoader.LoadPackage(enginePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return;

                var worldInfoClass = package.FindObject<UClass>("WorldInfo");
                if (worldInfoClass?.Default is UObject defaultObject)
                {
                    defaultObject.Load<UObjectRecordStream>();

                    if (defaultObject.ExportTable != null)
                    {
                        float? timestep = ReadPhysicsTimingsTimeStep(enginePackagePath, defaultObject);
                        if (timestep.HasValue && timestep.Value > 0)
                        {
                            int fps = (int)Math.Round(1.0f / timestep.Value);
                            PhysXFPSTextBox.Text = fps.ToString();
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private float? ReadPhysicsTimingsTimeStep(string filePath, UObject defaultObject)
        {
            try
            {
                using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return null;

                int timeStepNameIndex = package.Names.FindIndex(n => n.ToString() == "TimeStep");
                if (timeStepNameIndex == -1)
                    return null;

                byte[] timeStepNameBytes = BitConverter.GetBytes((long)timeStepNameIndex);

                var exportTable = defaultObject.ExportTable;
                if (exportTable == null)
                    return null;

                byte[] data = File.ReadAllBytes(filePath);

                long searchStart = exportTable.SerialOffset;
                long searchEnd = searchStart + exportTable.SerialSize;

                int occurrences = 0;

                for (long i = searchStart; i < searchEnd - 28; i++)
                {
                    bool nameMatch = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != timeStepNameBytes[j])
                        {
                            nameMatch = false;
                            break;
                        }
                    }

                    if (nameMatch)
                    {
                        occurrences++;

                        if (occurrences == 4)
                        {
                            float timestep = BitConverter.ToSingle(data, (int)(i + 24));
                            return timestep;
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private ResolutionHelper.Resolution? GetCurrentResolutionFromConfig()
        {
            try
            {
                string? engineIniPath = _config.TdEngineIniPath;

                if (string.IsNullOrEmpty(engineIniPath) || !File.Exists(engineIniPath))
                {
                    return null;
                }

                var lines = File.ReadAllLines(engineIniPath);
                int resX = -1;
                int resY = -1;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("ResX=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmedLine.Substring(5), out int x))
                        {
                            resX = x;
                        }
                    }
                    else if (trimmedLine.StartsWith("ResY=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmedLine.Substring(5), out int y))
                        {
                            resY = y;
                        }
                    }
                }

                if (resX > 0 && resY > 0)
                {
                    return new ResolutionHelper.Resolution { Width = resX, Height = resY };
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private void InitializeResolutionComboBox()
        {
            _isInitializingResolutionComboBox = true;

            var resolutions = ResolutionHelper.GetAvailableResolutions();

            ResolutionComboBox.Items.Clear();

            foreach (var resolution in resolutions)
            {
                var item = new System.Windows.Controls.ComboBoxItem
                {
                    Content = resolution.DisplayText,
                    Tag = resolution
                };
                ResolutionComboBox.Items.Add(item);
            }

            var currentResolution = GetCurrentResolutionFromConfig();
            if (currentResolution != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in ResolutionComboBox.Items)
                {
                    if (item.Tag is ResolutionHelper.Resolution res &&
                        res.Width == currentResolution.Width &&
                        res.Height == currentResolution.Height)
                    {
                        ResolutionComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            if (ResolutionComboBox.SelectedItem == null && ResolutionComboBox.Items.Count > 0)
            {
                ResolutionComboBox.SelectedItem = ResolutionComboBox.Items[0];
            }

            _isInitializingResolutionComboBox = false;

            if (ResolutionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                selectedItem.Tag is ResolutionHelper.Resolution selectedResolution)
            {
                bool isCurrentlyActive = false;
                if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    isCurrentlyActive = _uiScalingService.IsUIScalingActive(_config.GameDirectoryPath);
                }
                UpdateHighResFixStatus(selectedResolution.Width, isCurrentlyActive);
            }
        }

        private async void ResolutionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingResolutionComboBox)
                return;

            if (ResolutionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                selectedItem.Tag is ResolutionHelper.Resolution selectedResolution)
            {
                this.IsEnabled = false;

                try
                {
                    bool success = await UpdateResolutionInConfigAsync(selectedResolution.Width, selectedResolution.Height);

                    if (!success)
                    {
                        return;
                    }

                    bool userWantsUIScaling = false;

                    if (_uiScalingService.ShouldOfferUIScaling(selectedResolution.Width))
                    {
                        this.IsEnabled = true;

                        userWantsUIScaling = await _uiScalingService.AskUserForUIScalingConfirmationAsync();

                        this.IsEnabled = false;

                        if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
                        {
                            ShowProgress("Applying UI scaling...", true);

                            await System.Threading.Tasks.Task.Run(async () =>
                            {
                                if (userWantsUIScaling)
                                {
                                    await _uiScalingService.ApplyUIScalingAsync(selectedResolution.Width, selectedResolution.Height, _config.GameDirectoryPath, HideProgress);
                                }
                                else
                                {
                                    await _uiScalingService.RollbackUIScalingToDefaultsAsync(selectedResolution.Width, selectedResolution.Height, _config.GameDirectoryPath, HideProgress);
                                }
                            });
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
                        {
                            ShowProgress("Resetting UI scaling...", true);

                            await System.Threading.Tasks.Task.Run(async () =>
                            {
                                await _uiScalingService.RollbackUIScalingToDefaultsAsync(selectedResolution.Width, selectedResolution.Height, _config.GameDirectoryPath, HideProgress);
                            });
                        }
                    }

                    UpdateHighResFixStatus(selectedResolution.Width, userWantsUIScaling);

                    await Task.Run(() => ApplyDynamicPatchesForResolution());

                    UpdateScalingStatus();
                }
                finally
                {
                    this.IsEnabled = true;
                    StatusTextBlock.Text = "Ready.";
                }
            }
        }

        // Ensures the exe render target fix, Engine.u dynamic AR/FOV scaling,
        // and TdGame.u core compensation patches are applied when resolution changes
        private void ApplyDynamicPatchesForResolution()
        {
            try
            {
                string? exePath = FindGameExePath();
                if (exePath != null)
                    ExePatcher.Reconcile(exePath);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                Dispatcher.Invoke(() => DialogHelper.ShowMessage(
                    "EA App License Required",
                    ooaEx.Message,
                    DialogHelper.MessageType.Warning));
            }
            catch { }

            try
            {
                if (_config.EnginePackagePath != null && File.Exists(_config.EnginePackagePath))
                    EnginePatcher.Reconcile(_config.EnginePackagePath);
            }
            catch { }

            try
            {
                if (_config.TdGamePackagePath != null && File.Exists(_config.TdGamePackagePath))
                {
                    bool enableSens = false;
                    bool enableClip = false;
                    bool enableOnlineSkip = false;
                    Dispatcher.Invoke(() =>
                    {
                        enableSens = FovAgnosticSensCheckBox.IsChecked == true;
                        enableClip = CompensatedClipCheckBox.IsChecked == true;
                        enableOnlineSkip = SkipOnlineCheckComboBox.SelectedIndex == 1;
                    });
                    TdGamePatcher.Reconcile(_config.TdGamePackagePath, enableSens, enableClip, enableOnlineSkip);
                }
            }
            catch { }
        }

        private void UpdateHighResFixStatus(int width, bool isActive)
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            if (width <= 1920)
            {
                HighResFixStatus.Text = "High-Res Fix N/A";
                HighResFixStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
            else if (isActive)
            {
                HighResFixStatus.Text = "High-Res Fix Active";
                HighResFixStatus.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                HighResFixStatus.Text = "High-Res Fix Inactive";
                HighResFixStatus.Foreground = System.Windows.Media.Brushes.Orange;
            }
        }

        private async Task<bool> UpdateResolutionInConfigAsync(int width, int height)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath)) return false;

            try
            {
                string? engineIniPath = _config.TdEngineIniPath;

                if (string.IsNullOrEmpty(engineIniPath) || !File.Exists(engineIniPath))
                {
                    await DialogHelper.ShowMessageAsync("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                    return false;
                }

                var fileInfo = new FileInfo(engineIniPath);

                try
                {
                    if (fileInfo.IsReadOnly)
                        fileInfo.IsReadOnly = false;
                }
                catch (UnauthorizedAccessException)
                {
                    await DialogHelper.ShowMessageAsync("Error", "Unable to access TdEngine.ini. The file may be in use by another program.", DialogHelper.MessageType.Error);
                    return false;
                }
                catch (IOException ex)
                {
                    await DialogHelper.ShowMessageAsync("Error", $"Unable to access TdEngine.ini: {ex.Message}", DialogHelper.MessageType.Error);
                    return false;
                }

                try
                {
                    ConfigFileHelper.ModifyIniFile(engineIniPath, "SystemSettings", "ResX", width.ToString());
                    ConfigFileHelper.ModifyIniFile(engineIniPath, "SystemSettings", "ResY", height.ToString());
                }
                finally
                {
                    try
                    {
                        if (File.Exists(engineIniPath))
                            fileInfo.IsReadOnly = true;
                    }
                    catch
                    {
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                await DialogHelper.ShowMessageAsync("Error", $"Failed to update resolution: {ex.Message}", DialogHelper.MessageType.Error);
                return false;
            }
        }

        private void NewFovValue_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateScalingStatus();
        }

        private void ShowFOVInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("FOV Information",
                "Default horizontal FOV = 90°.\n\n" +
                "FOV automatically applies HOR+ scaling at aspect ratios wider than 16:9, and VERT+ scaling at " +
                "narrower aspect ratios. The aspect ratio is detected from the game resolution, no manual entry is needed.\n\n" +
                "The FOV value persists after each level load and game restart, scales correctly during cutscenes " +
                "and camera transitions, and does not break the skybox (unlike the keybind FOV method). " +
                "It also fixes the FOV being reset to 85° when reloading from deaths, compensates the vertigo " +
                "zoom effect, and maintains affected ADS FOV with the sniper rifle.\n\n" +
                "A render target fix is also applied to prevent the white-screen issue at narrower aspect ratios above 720p (e.g. Steam Deck's native resolution).\n\n" +
                "Options:\n\n" +
                "• Compensated near clipping plane: adjusts the near clipping plane based on FOV and aspect ratio " +
                "to reduce viewmodel/geometry clipping at higher FOVs or wider aspect ratios. Please note that Z-fighting will " +
                "become more prevalent at more extreme FOVs or wider aspect ratios with this option enabled.\n\n" +
                "• FOV-agnostic sensitivity: keeps mouse sensitivity consistent across all FOV values, using 90° " +
                "as the baseline. Weapon zoom sensitivity still tracks the zoomed FOV as normal. Also useful for TAS tools.",
                DialogHelper.MessageType.Information);
        }

        #region FPS Limit

        private void ShowFPSLimitInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("FPS Limit Information",
                "Default = FPS limit of 62.\n\n60-62 FPS limit is a requirement for speedruns to be verified, any other setting is banned. " +
                "Speedrunning strategies become increasingly more difficult as FPS increases, therefore it is not advised to deviate from the 60-62 FPS limit.\n\n" +
                "As framerate increases, so does player friction which can alter the speed of certain movement mechanics and make forced slides more difficult to control " +
                "as framerates exceed 150 FPS (i.e. Chapter 1C RP&A building slide). Enemy accuracy is also increased at higher framerates. " +
                "Additionally, as load times are tied to framerate, loading times decrease as framerate increases. These effects are otherwise generally not noticeable to casual players " +
                "and the game can be comfortably played with a higher FPS limit in place.\n\nIf you want to run the game with no FPS limiter at all, click the \'Remove Limit\' button.",
                DialogHelper.MessageType.Information);
        }

        private void ApplyFPSLimit_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini path is not set.", DialogHelper.MessageType.Error);
                return;
            }

            string input = FPSLimitTextBox.Text.Trim();

            if (string.IsNullOrEmpty(input))
            {
                DialogHelper.ShowMessage("Error", "FPS value not entered.", DialogHelper.MessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int value))
            {
                DialogHelper.ShowMessage("Error", "Invalid FPS value.", DialogHelper.MessageType.Error);
                return;
            }

            if (value < 1)
            {
                DialogHelper.ShowMessage("Error", "FPS cannot be less than 1.", DialogHelper.MessageType.Error);
                return;
            }

            if (value > 2000)
            {
                DialogHelper.ShowMessage("Error", "FPS cannot be greater than 2000.", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                _graphicsSettingsService.ApplyFPSLimit(_config.TdEngineIniPath, value);
                UpdateFPSLimitStatus();
                DialogHelper.ShowMessage("Success", $"FPS limit set to {value} FPS.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply FPS limit: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void RemoveFPSLimit_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini path is not set.", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                _graphicsSettingsService.RemoveFPSLimit(_config.TdEngineIniPath);
                UpdateFPSLimitStatus();
                DialogHelper.ShowMessage("Success", "FPS limit removed.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove FPS limit: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void UpdateFPSLimitStatus()
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                FPSLimitStatus.Text = "N/A";
                FPSLimitStatus.Foreground = System.Windows.Media.Brushes.Gray;
                return;
            }

            try
            {
                var (isLimited, fpsValue) = _graphicsSettingsService.ReadFPSLimitStatus(_config.TdEngineIniPath);

                if (isLimited && fpsValue.HasValue)
                {
                    FPSLimitStatus.Text = "Limiter On";
                    FPSLimitStatus.Foreground = System.Windows.Media.Brushes.Gray;
                    FPSLimitTextBox.Text = fpsValue.ToString();
                }
                else
                {
                    FPSLimitStatus.Text = "Limiter Off";
                    FPSLimitStatus.Foreground = System.Windows.Media.Brushes.Gray;
                }
            }
            catch
            {
                FPSLimitStatus.Text = "N/A";
                FPSLimitStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        #endregion

        #region Graphics Settings

        private void LoadGraphicsSettingsFromIni()
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
                return;

            _isInitializingGraphicsSettings = true;

            try
            {
                string? vsync = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "UseVsync");
                if (vsync != null)
                {
                    VSyncComboBox.SelectedIndex = vsync.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Anti aliasing
                string? maxMultiSamples = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "MaxMultisamples");
                if (maxMultiSamples != null)
                {
                    switch (maxMultiSamples)
                    {
                        case "1": AntiAliasingComboBox.SelectedIndex = 0; break; // Off
                        case "2": AntiAliasingComboBox.SelectedIndex = 1; break; // 2x
                        case "4": AntiAliasingComboBox.SelectedIndex = 2; break; // 4x
                        case "8": AntiAliasingComboBox.SelectedIndex = 3; break; // 8x
                        case "10": AntiAliasingComboBox.SelectedIndex = 4; break; // 8xQ
                        case "12": AntiAliasingComboBox.SelectedIndex = 5; break; // 16xQ
                    }
                }

                // Anisotropic filtering
                string? maxAnisotropy = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "MaxAnisotropy");
                if (maxAnisotropy != null)
                {
                    switch (maxAnisotropy)
                    {
                        case "0": AnisotropicFilteringComboBox.SelectedIndex = 0; break; // Off
                        case "2": AnisotropicFilteringComboBox.SelectedIndex = 1; break; // 2x
                        case "4": AnisotropicFilteringComboBox.SelectedIndex = 2; break; // 4x
                        case "8": AnisotropicFilteringComboBox.SelectedIndex = 3; break; // 8x
                        case "16": AnisotropicFilteringComboBox.SelectedIndex = 4; break; // 16x
                    }
                }

                // PhysX
                string? physx = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "PhysXEnhanced");
                if (physx != null)
                {
                    PhysXComboBox.SelectedIndex = physx.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Render resolution
                string? screenPercentage = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "ScreenPercentage");
                if (screenPercentage != null && double.TryParse(screenPercentage, out double percentage))
                {
                    RenderResolutionSlider.Value = Math.Round(percentage);
                }

                // Static decals
                string? staticDecals = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "StaticDecals");
                if (staticDecals != null)
                {
                    StaticDecalsComboBox.SelectedIndex = staticDecals.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Dynamic decals
                string? dynamicDecals = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "DynamicDecals");
                if (dynamicDecals != null)
                {
                    DynamicDecalsComboBox.SelectedIndex = dynamicDecals.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Radial blur
                string? motionBlur = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "TdMotionBlur");
                if (motionBlur != null)
                {
                    RadialBlurComboBox.SelectedIndex = motionBlur.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Streak effect
                if (!string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    string defaultHudEffectsPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "Config", "DefaultHudEffects.ini");
                    string? streakEffect = _graphicsSettingsService.ReadStreakEffectStatus(defaultHudEffectsPath);
                    if (streakEffect != null)
                    {
                        StreakEffectComboBox.SelectedIndex = streakEffect.Equals("true", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    }
                }

                // Bloom + dof
                string? bloom = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "Bloom");
                if (bloom != null)
                {
                    BloomDoFComboBox.SelectedIndex = bloom.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Lens flares
                string? lensFlares = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "LensFlares");
                if (lensFlares != null)
                {
                    LensFlareComboBox.SelectedIndex = lensFlares.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Dynamic lights
                string? dynamicLights = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "DynamicLights");
                if (dynamicLights != null)
                {
                    DynamicLightsComboBox.SelectedIndex = dynamicLights.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Dynamic shadows
                string? dynamicShadows = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "DynamicShadows");
                if (dynamicShadows != null)
                {
                    DynamicShadowsComboBox.SelectedIndex = dynamicShadows.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // HQ dynamic shadows
                string? hqShadows = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "bEnableBranchingPCFShadows");
                string? vsmShadows = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "bEnableVSMShadows");
                if (hqShadows != null && vsmShadows != null)
                {
                    bool isHQEnabled = hqShadows.Equals("True", StringComparison.OrdinalIgnoreCase) &&
                                      vsmShadows.Equals("False", StringComparison.OrdinalIgnoreCase);
                    HQDynamicShadowsComboBox.SelectedIndex = isHQEnabled ? 0 : 1;
                }

                // Lightmaps
                string? lightmaps = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "DirectionalLightmaps");
                if (lightmaps != null)
                {
                    LightmapsComboBox.SelectedIndex = lightmaps.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Sun haze
                string? sunHaze = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "TdSunHaze");
                if (sunHaze != null)
                {
                    SunHazeComboBox.SelectedIndex = sunHaze.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Tonemapping
                string? toneMapping = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "TdTonemapping");
                if (toneMapping != null)
                {
                    ToneMappingComboBox.SelectedIndex = toneMapping.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Texture management
                string? textureStreaming = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "OnlyStreamInTextures");
                if (textureStreaming != null)
                {
                    TextureManagementComboBox.SelectedIndex = textureStreaming.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Minimum LOD
                string? minLOD = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "MinLODSize");
                if (minLOD != null)
                {
                    MinLODTextBox.Text = minLOD;
                }

                // Maximum LOD
                string? maxLOD = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "MaxLODSize");
                if (maxLOD != null)
                {
                    MaxLODTextBox.Text = maxLOD;
                }

                // LOD bias
                string? lodBias = _graphicsSettingsService.ReadIniValue(_config.TdEngineIniPath, "LODBias");
                if (lodBias != null)
                {
                    LODBiasTextBox.Text = lodBias;
                }

                // Texture detail preset
                string texturePreset = _graphicsSettingsService.DetectTextureDetailPreset(_config.TdEngineIniPath);
                if (texturePreset == "Custom")
                {
                    (TextureDetailComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Visible;
                    TextureDetailComboBox.SelectedIndex = 0;
                }
                else
                {
                    (TextureDetailComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Collapsed;
                    switch (texturePreset)
                    {
                        case "Lowest": TextureDetailComboBox.SelectedIndex = 1; break;
                        case "Low": TextureDetailComboBox.SelectedIndex = 2; break;
                        case "Medium": TextureDetailComboBox.SelectedIndex = 3; break;
                        case "High": TextureDetailComboBox.SelectedIndex = 4; break;
                        case "Highest": TextureDetailComboBox.SelectedIndex = 5; break;
                    }
                }

                // Graphics quality preset
                string qualityPreset = _graphicsSettingsService.DetectGraphicsQualityPreset(_config.TdEngineIniPath);
                if (qualityPreset == "Custom")
                {
                    (GraphicsQualityComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Visible;
                    GraphicsQualityComboBox.SelectedIndex = 0;
                }
                else
                {
                    (GraphicsQualityComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Collapsed;
                    switch (qualityPreset)
                    {
                        case "Lowest": GraphicsQualityComboBox.SelectedIndex = 1; break;
                        case "Low": GraphicsQualityComboBox.SelectedIndex = 2; break;
                        case "Medium": GraphicsQualityComboBox.SelectedIndex = 3; break;
                        case "High": GraphicsQualityComboBox.SelectedIndex = 4; break;
                        case "Highest": GraphicsQualityComboBox.SelectedIndex = 5; break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load graphics settings: {ex.Message}");
            }
            finally
            {
                _isInitializingGraphicsSettings = false;
            }
        }

        private void UpdatePresetIndicators()
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
                return;

            try
            {
                _isInitializingGraphicsSettings = true;

                // Texture detail preset
                string texturePreset = _graphicsSettingsService.DetectTextureDetailPreset(_config.TdEngineIniPath);
                if (texturePreset == "Custom")
                {
                    (TextureDetailComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Visible;
                    TextureDetailComboBox.SelectedIndex = 0;
                }
                else
                {
                    (TextureDetailComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Collapsed;
                    switch (texturePreset)
                    {
                        case "Lowest": TextureDetailComboBox.SelectedIndex = 1; break;
                        case "Low": TextureDetailComboBox.SelectedIndex = 2; break;
                        case "Medium": TextureDetailComboBox.SelectedIndex = 3; break;
                        case "High": TextureDetailComboBox.SelectedIndex = 4; break;
                        case "Highest": TextureDetailComboBox.SelectedIndex = 5; break;
                    }
                }

                // Graphics quality preset
                string qualityPreset = _graphicsSettingsService.DetectGraphicsQualityPreset(_config.TdEngineIniPath);
                if (qualityPreset == "Custom")
                {
                    (GraphicsQualityComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Visible;
                    GraphicsQualityComboBox.SelectedIndex = 0;
                }
                else
                {
                    (GraphicsQualityComboBox.Items[0] as System.Windows.Controls.ComboBoxItem)!.Visibility = System.Windows.Visibility.Collapsed;
                    switch (qualityPreset)
                    {
                        case "Lowest": GraphicsQualityComboBox.SelectedIndex = 1; break;
                        case "Low": GraphicsQualityComboBox.SelectedIndex = 2; break;
                        case "Medium": GraphicsQualityComboBox.SelectedIndex = 3; break;
                        case "High": GraphicsQualityComboBox.SelectedIndex = 4; break;
                        case "Highest": GraphicsQualityComboBox.SelectedIndex = 5; break;
                    }
                }
            }
            finally
            {
                _isInitializingGraphicsSettings = false;
            }
        }

        #endregion

        #region Graphics Settings Event Handlers

        private void VSyncComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (VSyncComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                VSyncComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                VSyncComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyVSync(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply VSync: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                VSyncComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private async void AntiAliasingComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (AntiAliasingComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                AntiAliasingComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                AntiAliasingComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            string level = selectedItem.Content.ToString() ?? "";

            if (level == "16xQ")
            {
                bool proceed = await DialogHelper.ShowConfirmationAsync(
                    "Warning for NVIDIA GPU users",
                    "16xQ anti-aliasing (CSAA) is not supported on NVIDIA GPUs newer than the first-generation Maxwell microarchitecture (GTX 960 and up). " +
                    "Mirror's Edge will fail to launch if you choose this setting and have an NVIDIA GPU newer than this.\n\nDo you wish to proceed?");

                if (!proceed)
                {
                    _isInitializingGraphicsSettings = true;
                    AntiAliasingComboBox.SelectedIndex = -1;
                    _isInitializingGraphicsSettings = false;
                    return;
                }
            }

            try
            {
                _graphicsSettingsService.ApplyAntiAliasing(_config.TdEngineIniPath, level);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply anti-aliasing: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                AntiAliasingComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void AnisotropicFilteringComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (AnisotropicFilteringComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                AnisotropicFilteringComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                AnisotropicFilteringComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                string level = selectedItem.Content.ToString() ?? "";
                _graphicsSettingsService.ApplyAnisotropicFiltering(_config.TdEngineIniPath, level);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply anisotropic filtering: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                AnisotropicFilteringComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void PhysXComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (PhysXComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                PhysXComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                PhysXComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyPhysX(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply PhysX: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                PhysXComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void RenderResolutionSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (RenderResolutionValue == null)
                return;

            RenderResolutionValue.Text = $"{(int)e.NewValue}%";

            if (!_isRenderResolutionDragging)
                ApplyRenderResolutionValue();
        }

        private void RenderResolutionSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isRenderResolutionDragging = true;
        }

        private void RenderResolutionSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isRenderResolutionDragging = false;
            ApplyRenderResolutionValue();
        }

        private void ApplyRenderResolutionValue()
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                if (RenderResolutionSlider != null && RenderResolutionSlider.Value != 100)
                {
                    _isInitializingGraphicsSettings = true;
                    RenderResolutionSlider.Value = 100;
                    _isInitializingGraphicsSettings = false;
                }
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);

                if (RenderResolutionSlider != null && RenderResolutionSlider.Value != 100)
                {
                    _isInitializingGraphicsSettings = true;
                    RenderResolutionSlider.Value = 100;
                    _isInitializingGraphicsSettings = false;
                }
                return;
            }

            int percentage = (int)RenderResolutionSlider.Value;

            if (percentage > 100 && !string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (File.Exists(exePath))
                {
                    try
                    {
                        var state = SupersamplePatchHelper.GetPatchState(exePath);
                        if (state == SupersamplePatchState.Unpatched)
                        {
                            SupersamplePatchHelper.ApplyPatch(exePath);
                        }
                        else if (state != SupersamplePatchState.Patched)
                        {
                            DialogHelper.ShowMessage("Error",
                                "Could not verify the supersampling patch state. The executable may be an unsupported version.",
                                DialogHelper.MessageType.Error);

                            _isInitializingGraphicsSettings = true;
                            RenderResolutionSlider.Value = 100;
                            _isInitializingGraphicsSettings = false;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        DialogHelper.ShowMessage("Error",
                            $"Failed to apply supersampling patch: {ex.Message}\n\nRender resolution above 100% requires the executable to be patched.",
                            DialogHelper.MessageType.Error);

                        _isInitializingGraphicsSettings = true;
                        RenderResolutionSlider.Value = 100;
                        _isInitializingGraphicsSettings = false;
                        return;
                    }
                }
            }

            try
            {
                _graphicsSettingsService.ApplyRenderResolution(_config.TdEngineIniPath, percentage);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply render resolution: {ex.Message}", DialogHelper.MessageType.Error);

                if (RenderResolutionSlider != null && RenderResolutionSlider.Value != 100)
                {
                    _isInitializingGraphicsSettings = true;
                    RenderResolutionSlider.Value = 100;
                    _isInitializingGraphicsSettings = false;
                }
            }
        }

        private async void TextureDetailComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (TextureDetailComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                TextureDetailComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                TextureDetailComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            string preset = selectedItem.Content.ToString() ?? "";

            if (preset == "Custom")
                return;

            bool proceed = await DialogHelper.ShowConfirmationAsync(
                "Texture detail preset",
                "Applying a texture detail preset will revert any changes you may have made in the 'Individual Settings' section below.\n\nDo you wish to proceed?");

            if (!proceed)
            {
                _isInitializingGraphicsSettings = true;
                TextureDetailComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                _graphicsSettingsService.ApplyTextureDetailPreset(_config.TdEngineIniPath, preset);
                LoadGraphicsSettingsFromIni();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply texture detail preset: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                TextureDetailComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private async void GraphicsQualityComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (GraphicsQualityComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                GraphicsQualityComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                GraphicsQualityComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            string preset = selectedItem.Content.ToString() ?? "";

            if (preset == "Custom")
                return;

            bool proceed = await DialogHelper.ShowConfirmationAsync(
                "Graphics quality preset",
                "Applying a graphics quality preset will revert any changes you may have made in the 'Individual Settings' section below.\n\nDo you wish to proceed?");

            if (!proceed)
            {
                _isInitializingGraphicsSettings = true;
                GraphicsQualityComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                _graphicsSettingsService.ApplyGraphicsQualityPreset(_config.TdEngineIniPath, preset);
                LoadGraphicsSettingsFromIni();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply graphics quality preset: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                GraphicsQualityComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void StaticDecalsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (StaticDecalsComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                StaticDecalsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                StaticDecalsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyStaticDecals(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply static decals: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                StaticDecalsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void DynamicDecalsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (DynamicDecalsComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicDecalsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicDecalsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyDynamicDecals(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply dynamic decals: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicDecalsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void RadialBlurComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (RadialBlurComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                RadialBlurComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                RadialBlurComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyRadialBlur(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply radial blur: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                RadialBlurComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void StreakEffectComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (StreakEffectComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please specify the correct game install folder path first.", DialogHelper.MessageType.Error);
                return;
            }

            string defaultHudEffectsPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "Config", "DefaultHudEffects.ini");

            if (!File.Exists(defaultHudEffectsPath))
            {
                DialogHelper.ShowMessage("Error", "Cannot toggle streak effect, 'DefaultHudEffects.ini' file not found.", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyStreakEffect(defaultHudEffectsPath, enabled);

                if (!enabled)
                {
                    bool isPatched = _unlockedConfigsViewModel.UnlockedConfigsStatus == "Patched";
                    if (!isPatched)
                    {
                        DialogHelper.ShowMessage("Warning", "The config modification patch in the 'Game Tweaks' section is not applied. " +
                        "Please apply the patch in order for your game to launch with the disabled streak effect.", DialogHelper.MessageType.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply streak effect: {ex.Message}", DialogHelper.MessageType.Error);
                StreakEffectComboBox.SelectedIndex = -1;
            }
        }

        private void BloomDoFComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (BloomDoFComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                BloomDoFComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                BloomDoFComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyBloomAndDoF(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply bloom and DoF: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                BloomDoFComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void LensFlareComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (LensFlareComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                LensFlareComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                LensFlareComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyLensFlare(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply lens flare: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                LensFlareComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void DynamicLightsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (DynamicLightsComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicLightsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicLightsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyDynamicLights(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply dynamic lights: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicLightsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void DynamicShadowsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (DynamicShadowsComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicShadowsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicShadowsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyDynamicShadows(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply dynamic shadows: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                DynamicShadowsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void HQDynamicShadowsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (HQDynamicShadowsComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                HQDynamicShadowsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                HQDynamicShadowsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyHQDynamicShadows(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply HQ dynamic shadows: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                HQDynamicShadowsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void LightmapsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (LightmapsComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                LightmapsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                LightmapsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyLightmaps(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply lightmaps: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                LightmapsComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void SunHazeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (SunHazeComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                SunHazeComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                SunHazeComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplySunHaze(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply sun haze: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                SunHazeComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void ToneMappingComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (ToneMappingComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                ToneMappingComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                ToneMappingComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                _graphicsSettingsService.ApplyToneMapping(_config.TdEngineIniPath, enabled);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply tone mapping: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                ToneMappingComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void TextureManagementComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (TextureManagementComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror\'s Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                TextureManagementComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            if (!File.Exists(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                TextureManagementComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
                return;
            }

            try
            {
                string mode = selectedItem.Content.ToString() ?? "";
                _graphicsSettingsService.ApplyTextureManagement(_config.TdEngineIniPath, mode);
                UpdatePresetIndicators();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply texture management: {ex.Message}", DialogHelper.MessageType.Error);
                _isInitializingGraphicsSettings = true;
                TextureManagementComboBox.SelectedIndex = -1;
                _isInitializingGraphicsSettings = false;
            }
        }

        private void ApplyMinLOD_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini path is not set.", DialogHelper.MessageType.Error);
                return;
            }

            string input = MinLODTextBox.Text.Trim();

            if (string.IsNullOrEmpty(input))
            {
                DialogHelper.ShowMessage("Error", "LOD value not entered.", DialogHelper.MessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int value))
            {
                DialogHelper.ShowMessage("Error", "Invalid LOD value.", DialogHelper.MessageType.Error);
                return;
            }

            if (value < 1)
            {
                DialogHelper.ShowMessage("Error", "LOD cannot be less than 1.", DialogHelper.MessageType.Error);
                return;
            }

            if (value > 4096)
            {
                DialogHelper.ShowMessage("Error", "LOD cannot be higher than 4096.", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                _graphicsSettingsService.ApplyMinLOD(_config.TdEngineIniPath, value);
                UpdatePresetIndicators();
                DialogHelper.ShowMessage("Success", $"Minimum LOD set to {value}.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply minimum LOD: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ApplyMaxLOD_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini path is not set.", DialogHelper.MessageType.Error);
                return;
            }

            string input = MaxLODTextBox.Text.Trim();

            if (string.IsNullOrEmpty(input))
            {
                DialogHelper.ShowMessage("Error", "LOD value not entered.", DialogHelper.MessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int value))
            {
                DialogHelper.ShowMessage("Error", "Invalid LOD value.", DialogHelper.MessageType.Error);
                return;
            }

            if (value < 1)
            {
                DialogHelper.ShowMessage("Error", "LOD cannot be less than 1.", DialogHelper.MessageType.Error);
                return;
            }

            if (value > 4096)
            {
                DialogHelper.ShowMessage("Error", "LOD cannot be higher than 4096.", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                _graphicsSettingsService.ApplyMaxLOD(_config.TdEngineIniPath, value);
                UpdatePresetIndicators();
                DialogHelper.ShowMessage("Success", $"Maximum LOD set to {value}.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply maximum LOD: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ApplyLODBias_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.TdEngineIniPath))
            {
                DialogHelper.ShowMessage("Error", "TdEngine.ini path is not set.", DialogHelper.MessageType.Error);
                return;
            }

            string input = LODBiasTextBox.Text.Trim();

            if (string.IsNullOrEmpty(input))
            {
                DialogHelper.ShowMessage("Error", "LOD value not entered.", DialogHelper.MessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int value))
            {
                DialogHelper.ShowMessage("Error", "Invalid LOD value.", DialogHelper.MessageType.Error);
                return;
            }

            if (value < -1)
            {
                DialogHelper.ShowMessage("Error", "LOD bias cannot be lower than -1.", DialogHelper.MessageType.Error);
                return;
            }

            if (value > 12)
            {
                DialogHelper.ShowMessage("Error", "LOD bias cannot be higher than 12.", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                _graphicsSettingsService.ApplyLODBias(_config.TdEngineIniPath, value);
                UpdatePresetIndicators();
                DialogHelper.ShowMessage("Success", $"LOD bias set to {value}.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply LOD bias: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        #endregion

        #region Game Tweaks Info Dialogs

        private void ShowTdGameVersionInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("TdGame Version Information",
                "Allows the selection of various TdGame versions.\n\n" +
                "• Original — Unmodified TdGame and persistent map files.\n\n" +
                "• TdGame Fix (by Keku) — Modified TdGame and persistent map files that allows loading custom skins, animations, sounds and other miscellaneous mods (Mirror's Edge Tweaks does not require this to be installed with the exception of the Cinematic Faith Model mod).\n\n" +
                "• Time Trials Timer Fix (by Nulaft) — Fixes the precision errors of the time trial timer and uses a realtime timer (timer is prepended with an \"R\" to indicate this). Useful for speedrunners.\n\n" +
                "• TdGame Fix + Time Trials Timer Fix — Both versions combined.",
                DialogHelper.MessageType.Information);
        }

        private void ShowConsoleInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Developer Console Information",
                "Install the native Unreal Engine 3 developer console to access debug commands and features.\n\n" +
                "The function responsible for handling user input to open the console was intentionally stripped by DICE. Mirror's Edge Tweaks can install a custom UnrealScript package " +
                "that extends the existing Console class, overriding the empty input function with the required code to restore full console functionality.\n\n" +
                "Please note that Unreal Engine 3 supports only the US keyboard layout. If you do not wish to use the US layout, the following layouts will interpret these keys as Tilde:\n\n" +
                "• UK: @ (At sign)\n\n" +
                "• German: ö\n\n" +
                "• French: ù (% key)\n\n" +
                "• Spanish: ñ\n\n" +
                "• Italian: \\ (backslash)",
                DialogHelper.MessageType.Information);
        }

        private void ShowTweaksScriptsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Tweaks Scripts Information",
                "A custom UnrealScript package that adds an assortment of additional gameplay features, including Softimer (native in-game timer for speedrunners), cheats and trainer functionality, save file editing, and more.\n\n" +
                "It is highly recommended to install the developer console to access the full range of features of Tweaks Scripts.\n\n" +
                "• Softimer — Activate with the console command \"exec speedrun\" (or deactivate with \"exec speedrunoff\"), or toggle it via the 'Tweaks Scripts UI' mod.\n\n" +
                "• Cheats & Trainer — Activate with the console command \"exec cheats\" (or deactivate with \"exec cheatsoff\"), or toggle it via the 'Tweaks Scripts UI' mod. While activated, enter \"listcheats\" to view all cheats.\n\n" +
                "• Trainer HUD — Activate with the console command \"exec trainerhud\" (or deactivate with \"exec trainerhudoff\"), or toggle it via the 'Tweaks Scripts UI' mod.\n\n" +
                "• Save File Editor — Edit save progress (also requires the 'Tweaks Scripts UI' mod to be installed).",
                DialogHelper.MessageType.Information);
        }

        private void ShowTweaksScriptsUIInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Tweaks Scripts UI Information",
                "Provides an in-game UI for Tweaks Scripts features, accessible from the main menu.\n\n" +
                "• Regular: Standard version.\n\n" +
                "• MEMM-Compatible: Version compatible with the Mirror's Edge Map Manager.",
                DialogHelper.MessageType.Information);
        }

        private async void InstallTweaksScriptsUI_Click(object sender, RoutedEventArgs e)
        {
            if (!IsTweaksScriptsInstalled())
            {
                DialogHelper.ShowMessage(
                    "Dependency Missing",
                    "Install 'Tweaks Scripts' first (MirrorsEdgeTweaksScripts.u), then install Tweaks Scripts UI.",
                    DialogHelper.MessageType.Warning);
                UpdateTweaksScriptsDependencyUI(isTweaksScriptsInstalled: false);
                return;
            }

            var versionChoice = await ShowTweaksScriptsUIVersionDialog();

            if (versionChoice == null)
                return;

            bool isMEMM = versionChoice.Value;
            string downloadUrl = isMEMM
                ? "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/MirrorsEdgeTweaksScriptsUI_MEMM_compatible.zip"
                : "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/MirrorsEdgeTweaksScriptsUI.zip";

            await InstallTweaksScriptsUIAsync(downloadUrl, isMEMM);
        }

        private async Task<bool?> ShowTweaksScriptsUIVersionDialog()
        {
            var dialog = new TweaksScriptsUIVersionDialog();
            var result = await MaterialDesignThemes.Wpf.DialogHost.Show(dialog, "RootDialog");
            return result as bool?;
        }

        private async Task InstallTweaksScriptsUIAsync(string downloadUrl, bool isMEMMVersion)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdGamePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame");
                string publishedPath = Path.Combine(tdGamePath, "Published");

                if (!Directory.Exists(publishedPath))
                {
                    if (Directory.Exists(tdGamePath))
                    {
                        // If there's no Published folder but TdGame exists,
                        // just create the missing published folder
                        Directory.CreateDirectory(publishedPath);
                    }
                    else
                    {
                        DialogHelper.ShowMessage("Error",
                            $"Published folder not found at: {publishedPath}\n\n" +
                            "Please ensure you have launched Mirror's Edge at least once.",
                            DialogHelper.MessageType.Error);
                        return;
                    }
                }

                ShowProgress("Downloading Tweaks Scripts UI...", false);

                try
                {
                    string tempZipPath = Path.Combine(Path.GetTempPath(), "TweaksScriptsUI.zip");

                    using (var client = new System.Net.Http.HttpClient())
                    {
                        using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            long? totalBytes = response.Content.Headers.ContentLength;

                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                            {
                                var buffer = new byte[8192];
                                long totalRead = 0;
                                int bytesRead;

                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalRead += bytesRead;

                                    if (totalBytes.HasValue)
                                    {
                                        double progress = (double)totalRead / totalBytes.Value * 100;
                                        DownloadProgressBar.Value = progress;
                                        UpdateStatus($"Downloading Tweaks Scripts UI... {progress:F0}%");
                                    }
                                }
                            }
                        }
                    }

                    UpdateStatus("Extracting Tweaks Scripts UI...");
                    DownloadProgressBar.IsIndeterminate = true;

                    System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, publishedPath, true);

                    File.Delete(tempZipPath);

                    HideProgress();

                    string versionName = isMEMMVersion ? "MEMM-Compatible" : "Regular";
                    DialogHelper.ShowMessage("Success",
                        $"Tweaks Scripts UI ({versionName}) installed successfully!",
                        DialogHelper.MessageType.Success);

                    UpdateTweaksScriptsUIStatus();
                }
                finally
                {
                    HideProgress();
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                DialogHelper.ShowMessage("Error",
                    $"Failed to install Tweaks Scripts UI: {ex.Message}",
                    DialogHelper.MessageType.Error);
            }
        }

        private void UninstallTweaksScriptsUI_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string publishedPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Published");

                string[] filesToDelete = new[]
                {
                    Path.Combine(publishedPath, "CookedPC", "Maps", "Menu", "TdMainMenu.me1"),
                    Path.Combine(publishedPath, "CookedPC", "UI", "TdUI_FrontEnd.upk"),
                    Path.Combine(publishedPath, "CookedPC", "UI", "TdUI_SofTimer.upk"),
                    Path.Combine(publishedPath, "CookedPC", "UI", "TdUI_Custom_Races.upk")
                };

                int deletedCount = 0;
                foreach (string file in filesToDelete)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }

                if (deletedCount == 0)
                {
                    DialogHelper.ShowMessage("Information",
                        "No Tweaks Scripts UI files found to uninstall.",
                        DialogHelper.MessageType.Information);
                }
                else
                {
                    DialogHelper.ShowMessage("Success",
                        $"Tweaks Scripts UI uninstalled. ({deletedCount} file(s) removed)",
                        DialogHelper.MessageType.Success);
                }

                UpdateTweaksScriptsUIStatus();
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error",
                    $"Failed to uninstall Tweaks Scripts UI: {ex.Message}",
                    DialogHelper.MessageType.Error);
            }
        }

        private void UpdateTweaksScriptsUIStatus()
        {
            if (_isProcessingGameDirectory)
            {
                return;
            }

            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string publishedPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Published");

                string mainMenuFile = Path.Combine(publishedPath, "CookedPC", "Maps", "Menu", "TdMainMenu.me1");
                string frontEndFile = Path.Combine(publishedPath, "CookedPC", "UI", "TdUI_FrontEnd.upk");
                string sofTimerFile = Path.Combine(publishedPath, "CookedPC", "UI", "TdUI_SofTimer.upk");
                string customRacesFile = Path.Combine(publishedPath, "CookedPC", "UI", "TdUI_Custom_Races.upk");

                bool hasMainMenu = File.Exists(mainMenuFile);
                bool hasFrontEnd = File.Exists(frontEndFile);
                bool hasSofTimer = File.Exists(sofTimerFile);
                bool hasCustomRaces = File.Exists(customRacesFile);

                if (hasMainMenu && hasFrontEnd && hasSofTimer && hasCustomRaces)
                {
                    TweaksScriptsUIStatus.Text = "Installed (MEMM)";
                    TweaksScriptsUIStatus.Foreground = System.Windows.Media.Brushes.Green;
                }
                else if (hasMainMenu && hasFrontEnd && hasSofTimer && !hasCustomRaces)
                {
                    TweaksScriptsUIStatus.Text = "Installed (regular)";
                    TweaksScriptsUIStatus.Foreground = System.Windows.Media.Brushes.Green;
                }
                else if (hasMainMenu || hasFrontEnd || hasSofTimer || hasCustomRaces)
                {
                    TweaksScriptsUIStatus.Text = "Partially Installed";
                    TweaksScriptsUIStatus.Foreground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    TweaksScriptsUIStatus.Text = "Not Installed";
                    TweaksScriptsUIStatus.Foreground = System.Windows.Media.Brushes.Gray;
                }
            }
            catch (Exception)
            {
                TweaksScriptsUIStatus.Text = "N/A";
                TweaksScriptsUIStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
            finally
            {
                UpdateTweaksScriptsDependencyUI(IsTweaksScriptsInstalled());
            }
        }

        private void ShowUnlockedConfigsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Unlocked Configs Information",
                "Applying this patch bypasses the \"corrupted config\" error message that prevents the game from launching when the game directory's config files have been modified " +
                "(e.g. when removing the streak effects, adding custom maps, removing startup wait period, etc.).\n\nThis is essentially achieving what the MEMLA tool does, " +
                "except it patches the executable directly.",
                DialogHelper.MessageType.Information);
        }

        private void ShowLaunchArgumentsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Launch Arguments Information",
                "Patches the executable to unlock full command line handling in Mirror's Edge. " +
                "Arguments can be entered here, or they can be added to your game library's launch options/other shortcuts.\n\n" +
                "If entering arguments in Mirror's Edge Tweaks, use the 'Launch Game w/ Args' button at the top of the window to start the game with the entered arguments.\n\n" +
                "The majority of arguments should be prefixed with a '-' character. Only URL-specific arguments do not require the '-' prefix, such as commands to load a specific map upon startup. " +
                "Multiple arguments must be separated by a space.\n\n" +
                "Refer to Unreal Engine 3 documentation for available stock command line arguments: https://docs.unrealengine.com/udk/Three/CommandLineArguments.html",
                DialogHelper.MessageType.Information);
        }

        private void PatchLogging_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                var state = LoggingPatchHelper.GetPatchState(exePath);
                if (state == LoggingPatchState.Patched)
                {
                    LoggingPatchStatusTextBlock.Text = "Patched";
                    LoggingPatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                    return;
                }

                LoggingPatchHelper.ApplyPatch(exePath);
                LoggingPatchStatusTextBlock.Text = "Patched";
                LoggingPatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                DialogHelper.ShowMessage("Success", "Logging patch applied successfully.", DialogHelper.MessageType.Information);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply logging patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void UnpatchLogging_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                var state = LoggingPatchHelper.GetPatchState(exePath);
                if (state == LoggingPatchState.Unpatched)
                {
                    LoggingPatchStatusTextBlock.Text = "Unpatched";
                    return;
                }

                LoggingPatchHelper.RemovePatch(exePath);
                LoggingPatchStatusTextBlock.Text = "Unpatched";
                LoggingPatchStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("MaterialDesignBodyLight");
                DialogHelper.ShowMessage("Success", "Logging patch removed successfully.", DialogHelper.MessageType.Information);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove logging patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowLoggingInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Enable Logging Information",
                "Restores Mirror's Edge's disabled UE3 logging system.\n\n" +
                "When patched, UnrealScript log() calls and native logging are written to a log file " +
                "(and displayed in the log console window if the \"-LOG\" launch argument is used).\n\n" +
                "By default, the log file is created at \"Logs\\Launch.log\" next to the executable. " +
                "You can customise the log location using launch arguments:\n\n" +
                "\"-LOG=mylog.txt\" — Write to a custom filename\n" +
                "\"-ABSLOG=C:\\...\" — Write to an absolute path",
                DialogHelper.MessageType.Information);
        }

        private void PatchMultiInstance_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                var state = MultiInstancePatchHelper.GetPatchState(exePath);
                if (state == MultiInstancePatchState.Patched)
                {
                    MultiInstancePatchStatusTextBlock.Text = "Patched";
                    MultiInstancePatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                    return;
                }

                MultiInstancePatchHelper.ApplyPatch(exePath);
                MultiInstancePatchStatusTextBlock.Text = "Patched";
                MultiInstancePatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                DialogHelper.ShowMessage("Success", "Multi-instance patch applied successfully.", DialogHelper.MessageType.Information);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply multi-instance patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void UnpatchMultiInstance_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                var state = MultiInstancePatchHelper.GetPatchState(exePath);
                if (state == MultiInstancePatchState.Unpatched)
                {
                    MultiInstancePatchStatusTextBlock.Text = "Unpatched";
                    return;
                }

                MultiInstancePatchHelper.RemovePatch(exePath);
                MultiInstancePatchStatusTextBlock.Text = "Unpatched";
                MultiInstancePatchStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("MaterialDesignBodyLight");
                DialogHelper.ShowMessage("Success", "Multi-instance patch removed successfully.", DialogHelper.MessageType.Information);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove multi-instance patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowMultiInstanceInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Multi-instance Information",
                "Bypasses Mirror's Edge's single-instance restriction.\n\n" +
                "This is useful for modders so that the editor can be kept open when launching the regular game instance.",
                DialogHelper.MessageType.Information);
        }

        private void PatchAmbiguousBypass_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                var state = AmbiguousBypassPatchHelper.GetPatchState(exePath);
                if (state == AmbiguousBypassPatchState.Patched)
                {
                    AmbiguousBypassPatchStatusTextBlock.Text = "Patched";
                    AmbiguousBypassPatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                    return;
                }

                AmbiguousBypassPatchHelper.ApplyPatch(exePath);
                AmbiguousBypassPatchStatusTextBlock.Text = "Patched";
                AmbiguousBypassPatchStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                DialogHelper.ShowMessage("Success", "Ambiguous bypass patch applied successfully.", DialogHelper.MessageType.Information);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply ambiguous bypass patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void UnpatchAmbiguousBypass_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
            {
                DialogHelper.ShowMessage("Error", "Please select a game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                var state = AmbiguousBypassPatchHelper.GetPatchState(exePath);
                if (state == AmbiguousBypassPatchState.Unpatched)
                {
                    AmbiguousBypassPatchStatusTextBlock.Text = "Unpatched";
                    return;
                }

                AmbiguousBypassPatchHelper.RemovePatch(exePath);
                AmbiguousBypassPatchStatusTextBlock.Text = "Unpatched";
                AmbiguousBypassPatchStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("MaterialDesignBodyLight");
                DialogHelper.ShowMessage("Success", "Ambiguous bypass patch removed successfully.", DialogHelper.MessageType.Information);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove ambiguous bypass patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowAmbiguousBypassInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Ambiguous Bypass Information",
                "Suppresses the \"Ambiguous package name\" dialogs that block game startup.\n\n" +
                "When multiple game files with the same name are present in the game directory, " +
                "a blocking message box for each conflict is displayed when the game starts. " +
                "With the patch applied, these dialogs are silently skipped.\n\n" +
                "Warning: Bypassing this message is not intended for end users — this message appearing " +
                "in the first place is a sign of a problem with your game's file setup. " +
                "Only use this patch if you know what you're doing.",
                DialogHelper.MessageType.Information);
        }

        private void ResetLaunchArguments_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                    return;
                }

                string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                    return;
                }

                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                bool patchWasRemoved = unlockMode == CommandLineUnlockMode.PersistentFilePatch &&
                    CommandLineUnlockHelper.RestoreStock(exePath);

                LaunchArgumentsTextBox.Text = string.Empty;
                _config.LaunchArguments = string.Empty;
                SaveSettingsToIni();
                UpdateLaunchArgumentsPatchStatus();

                string message = unlockMode switch
                {
                    CommandLineUnlockMode.PersistentFilePatch when patchWasRemoved =>
                        "The command line unlock patch has been removed and the saved launch arguments were cleared.",
                    CommandLineUnlockMode.PersistentFilePatch =>
                        "The executable is already using the stock command line behavior. Saved launch arguments were cleared.",
                    _ =>
                        "Saved launch arguments were cleared."
                };

                DialogHelper.ShowMessage(
                    patchWasRemoved ? "Success" : "Information",
                    message,
                    patchWasRemoved ? DialogHelper.MessageType.Success : DialogHelper.MessageType.Information);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                DialogHelper.ShowMessage("EA App License Required", ooaEx.Message, DialogHelper.MessageType.Warning);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to reset launch arguments: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ApplyLaunchArguments_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                    return;
                }

                string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    DialogHelper.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogHelper.MessageType.Error);
                    return;
                }

                string launchArguments = LaunchArgumentsTextBox.Text?.Trim() ?? string.Empty;
                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                bool patchWasApplied = false;

                string unlockMessage = unlockMode switch
                {
                    CommandLineUnlockMode.PersistentFilePatch => (patchWasApplied = CommandLineUnlockHelper.Unlock(exePath))
                        ? "Command line arguments are now unlocked in the executable."
                        : "Command line arguments are already unlocked in the executable.",
                    _ => throw new InvalidOperationException("This executable version does not support command line unlocking.")
                };

                LaunchArgumentsTextBox.Text = launchArguments;
                _config.LaunchArguments = launchArguments;
                SaveSettingsToIni();
                UpdateLaunchArgumentsPatchStatus();

                string argumentsMessage = string.IsNullOrEmpty(launchArguments)
                    ? "No launch arguments were saved. You can add them later, or add them to your game library's launch options/other shortcuts."
                    : $"Saved launch arguments: {launchArguments}\n\nUse 'Launch Game w/ Args' to start the game with them.";

                DialogHelper.ShowMessage(
                    "Success",
                    unlockMessage +
                    "\n\n" +
                    argumentsMessage,
                    DialogHelper.MessageType.Success);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                DialogHelper.ShowMessage("EA App License Required", ooaEx.Message, DialogHelper.MessageType.Warning);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply launch arguments: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        #endregion

        #region Graphics Settings Info Dialogs

        private void ShowHighResFixInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Resolution Information",
                "Mirror's Edge accepts only the resolutions currently available in your system's display settings. However, it is possible to use other software " +
                "(e.g. Custom Resolution Utility, NVIDIA Control Panel, etc.) to add custom display resolutions. Once these are configured, they will appear here.\n\n" +
                "Selecting a resolution will also apply the following fixes:\n\n" +
                "• Removes the hardcoded 16:9 aspect ratio constraint, allowing the game to render correctly at any aspect ratio without letterboxing/pillarboxing.\n\n" +
                "• Applies a render target fix to prevent the white-screen issue at narrower aspect ratios above 720p (e.g. Steam Deck's native resolution).\n\n" +
                "• Enables dynamic FOV scaling so the game automatically applies HOR+ correction at aspect ratios wider than 16:9, and VERT+ at narrower ratios.\n\n" +
                "• Compensates cutscene zoom rates, vertigo effects, and unzoom timing to work correctly and consistently at any FOV and aspect ratio.\n\n" +
                "Selecting a resolution with a horizontal pixel count greater than 1920 will also prompt you with the option to fix the blurry in-game text and other UI fixes. " +
                "Please be aware that this solution partially works at the moment — while blurriness is fixed, subtitles, lists, timer HUD, and loading screen text will appear smaller as you increase the resolution.",
                DialogHelper.MessageType.Information);
        }

        private void ShowVSyncInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("VSync Information",
                "Vertical Sync synchronises the frame rate with your monitor's refresh rate to prevent screen tearing. Enabling it may increase input latency.",
                DialogHelper.MessageType.Information);
        }

        private void ShowAntiAliasingInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Anti-Aliasing Information",
                "Anti-aliasing smooths jagged edges in the game. Higher values provide better quality but reduce performance.",
                DialogHelper.MessageType.Information);
        }

        private void ShowPhysXInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("PhysX Information",
                "PhysX provides additional physics effects such as detailed debris and cloth simulations, and spawns in extra physics props.\n\n" +
                "Note: PhysX in Mirror's Edge is hardware accelerated only on CUDA-ready NVIDIA GPUs.",
                DialogHelper.MessageType.Information);
        }

        private void ShowPhysXFPSInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("PhysX FPS Information",
                "Applies a PhysX FPS value to cloth simulations (flags, construction tarps, strip curtain doors, etc.). Accepts a minimum of 50 FPS and a maximum of 300 FPS. No effect if PhysX is disabled.\n\n" +
                "Cloth simulations in Mirror's Edge are simulated at a rate independent of the game's framerate, otherwise known as time-steps. By default, Mirror's Edge uses a value of 50 FPS " +
                "for PhysX cloth simulations, which can appear choppy when using reaction time or when running the game above the 62 FPS limit.\n\n" +
                "Suggestions:\n\n• If playing at the default 62 FPS limit, change the PhysX FPS value to 62 FPS to match the simulation rate with the game's framerate. " +
                "This effectively removes the frame pacing appearance of PhysX cloth.\n\n• If playing at uncapped FPS, set this value to whatever you want (max of 300 FPS).",
                DialogHelper.MessageType.Information);
        }

        private void ShowRenderResolutionInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Render Resolution Information",
                "Controls the internal rendering resolution relative to your display output.\n\n" +
                "Below 100%: Renders at a lower resolution and upscales, improving performance on lower-end systems.\n\n" +
                "Above 100%: Renders at a higher resolution and downscales to your display, producing sharper visuals " +
                "with reduced aliasing. Setting 200% renders at 4x pixel density.",
                DialogHelper.MessageType.Information);
        }

        private void ShowTextureDetailInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Texture Detail Information",
                "Texture detail controls the resolution/LODs of textures, as well as the level of anisotropic filtering and bicubic filtering to be applied.\n\nThis setting mirrors the in-game video options.",
                DialogHelper.MessageType.Information);
        }

        private void ShowGraphicsQualityInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Graphics Quality Information",
                "Graphics quality controls mesh/shadow quality, as well as various other post-process effects such as bloom, depth of field, lens flares, etc.\n\nThis setting mirrors the in-game video options.",
                DialogHelper.MessageType.Information);
        }

        private void ShowAnisotropicFilteringInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Anisotropic Filtering Information",
                "Anisotropic filtering improves texture quality when viewed at oblique angles. Higher values provide better quality.",
                DialogHelper.MessageType.Information);
        }

        private void ShowStaticDecalsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Static Decals Information",
                "Static decals are pre-placed decals (runner glyphs, paint/graffiti, etc.).",
                DialogHelper.MessageType.Information);
        }

        private void ShowDynamicDecalsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Dynamic Decals Information",
                "Dynamic decals are decals spawned during gameplay (typically bullet holes and explosion effects).",
                DialogHelper.MessageType.Information);
        }

        private void ShowRadialBlurInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Radial Blur Information",
                "Radial blur is the blurring applied to the edges of the screen when running. It is seperate from the streak effect.",
                DialogHelper.MessageType.Information);
        }

        private void ShowStreakEffectInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Streak Effect Information",
                "When approaching top running speed, streak effects will appear on the edges of the screen which can become more noticeable at higher FOV settings. " +
                "\n\nDisabling requires the 'Unlocked Configs' patch in the 'Game Tweaks' section.",
                DialogHelper.MessageType.Information);
        }

        private void ShowBloomDoFInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Bloom & DoF Information",
                "Bloom creates a glow effect around bright lights. Depth of Field blurs objects that are out of focus." +
                "\n\nThe shaders involved for rendering Bloom and Depth of Field are dependent on each other and cannot be individually toggled on/off.",
                DialogHelper.MessageType.Information);
        }

        private void ShowLensFlareInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Lens Flare Information",
                "Allows enabling/disabling the lens flares emitted from the sun and various light sources. In some maps this will also remove the appearance of the sun altogether.",
                DialogHelper.MessageType.Information);
        }

        private void ShowDynamicLightsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Dynamic Lights Information",
                "Dynamic lights are any light sources that dynamically illuminate the scene and characters. Typical examples include flashlights/cop car lights and ambient character illumination.",
                DialogHelper.MessageType.Information);
        }

        private void ShowDynamicShadowsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Dynamic Shadows Information",
                "Dynamic shadows are the modulated shadows casted onto the environment from characters. This also includes self-shadowing of characters.",
                DialogHelper.MessageType.Information);
        }

        private void ShowHQDynamicShadowsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("HQ Dynamic Shadows Information",
                "High Quality dynamic shadows doubles the resolution of what's available from the \"Highest\" graphics quality preset, " +
                "forces the maximum shadow resolution to always be shown, increases the filtering quality, and disables VSM shadowing in favour of the superior-quality PCF shadowing." +
                "\n\nNote: \"High quality\" dynamic shadows will have no effect if dynamic shadows are disabled.",
                DialogHelper.MessageType.Information);
        }

        private void ShowLightmapsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Lightmaps Information",
                "Light maps are the pre-baked lighting used to globally illuminate the environment. These light maps can be disabled (for most objects), " +
                "showing the original textures without the environment's GI and shadow contributions. Note that disabling can also make some vertex-baked objects appear black.",
                DialogHelper.MessageType.Information);
        }

        private void ShowSunHazeInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Sun Haze Information",
                "Toggles the appearance of atmospheric haze around the sun. This haze can bleed through buildings in some scenarios.",
                DialogHelper.MessageType.Information);
        }

        private void ShowToneMappingInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Tone Mapping Information",
                "Tone mapping adjusts the post-process exposure/colour curves, which are applied on a per-map basis. " +
                "Disabling tone mapping typically makes the image appear brighter and with less contrast.",
                DialogHelper.MessageType.Information);
        }

        private void ShowTextureManagementInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Texture Management Information",
                "The \"Modern\" setting removes the 250MB VRAM allocation limit to textures and forces textures to remain in the texture pool once loaded. " +
                "This can resolve the random blurry texture bug, and assists with large custom maps that don't utilise level streaming.\n\n" +
                "If you have a low-end system, it may be more preferable to keep this setting to \"Default\".",
                DialogHelper.MessageType.Information);
        }

        private void ShowMinLODInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Minimum LOD Information",
                "Minimum LOD size controls the lowest quality texture mipmap that will be loaded. Range: 1-4096 (Unreal Engine 3 has a max limit of 4096).",
                DialogHelper.MessageType.Information);
        }

        private void ShowMaxLODInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Maximum LOD Information",
                "Maximum LOD size controls the highest quality texture mipmap that will be loaded. Range: 1-4096 (Unreal Engine 3 has a max limit of 4096).",
                DialogHelper.MessageType.Information);
        }

        private void ShowLODBiasInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("LOD Bias Information",
                "Adjusts the distance at which different texture mipmaps are loaded. A higher bias value results in lower resolution texture mipmaps being shown sooner " +
                "as the player moves away from the texture surface and vice versa. A minimum bias of 0 (highest quality, shows only the maximum resolution LOD) " +
                "and a maximum bias of 12 (lowest quality) can be entered.",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Other Tweaks Event Handlers

        private void MouseSmoothingComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (MouseSmoothingComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string tdInputIniPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

            if (!File.Exists(tdInputIniPath))
            {
                DialogHelper.ShowMessage("Error",
                    $"Cannot edit mouse smoothing, 'TdInput.ini' file is missing from \"{tdInputIniPath}\".\n\n" +
                    "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                    DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";
                ApplyMouseSmoothing(tdInputIniPath, enabled);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply mouse smoothing: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ApplyMouseSmoothing(string iniPath, bool enabled)
        {
            if (!File.Exists(iniPath))
            {
                throw new FileNotFoundException($"TdInput.ini not found at: {iniPath}");
            }

            FileAttributes attributes = File.GetAttributes(iniPath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(iniPath, attributes & ~FileAttributes.ReadOnly);

            try
            {
                string[] lines = File.ReadAllLines(iniPath);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains('='))
                    {
                        string key = lines[i].Split('=')[0].Trim();
                        if (key == "bEnableMouseSmoothing")
                        {
                            lines[i] = $"bEnableMouseSmoothing={enabled.ToString().ToLower()}";
                        }
                    }
                }

                File.WriteAllLines(iniPath, lines);
            }
            finally
            {
                File.SetAttributes(iniPath, File.GetAttributes(iniPath) | FileAttributes.ReadOnly);
            }
        }

        private void LoadMouseSmoothingFromIni()
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string tdInputIniPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

            if (!File.Exists(tdInputIniPath))
                return;

            try
            {
                string[] lines = File.ReadAllLines(tdInputIniPath);

                foreach (string line in lines)
                {
                    if (line.Contains('='))
                    {
                        string key = line.Split('=')[0].Trim();
                        string value = line.Split('=')[1].Trim();

                        if (key == "bEnableMouseSmoothing")
                        {
                            if (value.ToLower() == "true")
                            {
                                MouseSmoothingComboBox.SelectedIndex = 0;
                            }
                            else
                            {
                                MouseSmoothingComboBox.SelectedIndex = 1;
                            }
                            break;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void ShowMouseSmoothingInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Mouse Smoothing Information",
                "Mouse smoothing will variably adjust your mouse sensitivity, generally making it more inconsistent. It is recommended to disable mouse smoothing for a better experience.",
                DialogHelper.MessageType.Information);
        }

        private async void UniformSensitivityComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
                return;

            if (UniformSensitivityComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            if (string.IsNullOrEmpty(_config.TdGamePackagePath))
            {
                DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                return;
            }

            if (!File.Exists(_config.TdGamePackagePath))
            {
                DialogHelper.ShowMessage("Error", $"TdGame.u package not found at: {_config.TdGamePackagePath}", DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                bool enabled = selectedItem.Content.ToString() == "Enabled";

                if (enabled)
                {
                    var result = await DialogHelper.ShowConfirmationAsync(
                        "Speedrun Warning",
                        "Warning: Enabling uniform sensitivity is banned in official Mirror's Edge speedrun categories. " +
                        "Only enable this if you are playing casually.\n\n" +
                        "Do you want to continue?");

                    if (!result)
                    {
                        _isInitializingGraphicsSettings = true;
                        UniformSensitivityComboBox.SelectedIndex = 1;
                        _isInitializingGraphicsSettings = false;
                        return;
                    }
                }

                float targetValue = enabled ? 66536f : 16384f;

                ShowProgress("Applying uniform sensitivity setting...", true);

                await Task.Run(() =>
                {
                    ApplyUniformSensitivity(_config.TdGamePackagePath, targetValue);
                });

                HideProgress();

                string message = enabled
                    ? "Uniform sensitivity enabled. Mouse sensitivity will now remain consistent regardless of vertical view angle."
                    : "Uniform sensitivity disabled (default behavior restored).";

                DialogHelper.ShowMessage("Success", message, DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                HideProgress();
                DialogHelper.ShowMessage("Error", $"Failed to apply uniform sensitivity: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ApplyUniformSensitivity(string tdGamePackagePath, float targetValue)
        {
            FileAttributes attributes = File.GetAttributes(tdGamePackagePath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

            if (wasReadOnly)
            {
                File.SetAttributes(tdGamePackagePath, attributes & ~FileAttributes.ReadOnly);
            }

            try
            {
                using var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    throw new Exception("Failed to load TdGame.u package.");

                var tdPlayerController = package.FindObject<UClass>("TdPlayerController");
                if (tdPlayerController == null)
                    throw new Exception("TdPlayerController class not found in TdGame.u");

                var updateRotationFunc = tdPlayerController.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UpdateRotation");
                if (updateRotationFunc == null)
                    throw new Exception("UpdateRotation function not found in TdPlayerController class.");

                updateRotationFunc.Load<UObjectRecordStream>();

                long floatOffset = FindRotSpeedModFloatOffset(updateRotationFunc);

                if (floatOffset == -1)
                    throw new Exception("Could not locate the rotation speed modifier float in UpdateRotation function.");

                byte[] data = File.ReadAllBytes(tdGamePackagePath);

                float currentValue = BitConverter.ToSingle(data, (int)floatOffset);
                if (Math.Abs(currentValue - 16384f) > 0.1f && Math.Abs(currentValue - 66536f) > 0.1f)
                {
                    throw new Exception($"Unexpected current value at offset {floatOffset}: {currentValue}. Expected either 16384 or 66536.");
                }

                byte[] newValueBytes = BitConverter.GetBytes(targetValue);
                Array.Copy(newValueBytes, 0, data, floatOffset, 4);

                File.WriteAllBytes(tdGamePackagePath, data);
            }
            finally
            {
                if (wasReadOnly)
                {
                    File.SetAttributes(tdGamePackagePath, attributes);
                }
            }
        }

        private long FindRotSpeedModFloatOffset(UFunction function)
        {
            if (function.ByteCodeManager == null || function.ExportTable == null)
                return -1;

            function.ByteCodeManager.Deserialize();
            var tokens = function.ByteCodeManager.DeserializedTokens;

            // look for the 2nd last float in the expression
            // for reference: RotSpeedMod = FMax(0.4, 1 - float(Min(1, int(Abs(float(Normalize(Rotation - myPawn.Rotation).Pitch) / 16384) + 0.3))));

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] is FloatConstToken floatToken)
                {
                    float value = floatToken.Value;
                    if (Math.Abs(value - 16384f) < 0.1f || Math.Abs(value - 66536f) < 0.1f)
                    {
                        return function.ExportTable.SerialOffset + function.ScriptOffset + floatToken.StoragePosition + 1;
                    }
                }
            }

            return -1;
        }

        private void LoadUniformSensitivityFromPackage()
        {
            if (string.IsNullOrEmpty(_config.TdGamePackagePath) || !File.Exists(_config.TdGamePackagePath))
                return;

            try
            {
                using var package = UnrealLoader.LoadPackage(_config.TdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return;

                var tdPlayerController = package.FindObject<UClass>("TdPlayerController");
                if (tdPlayerController == null)
                    return;

                var updateRotationFunc = tdPlayerController.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UpdateRotation");
                if (updateRotationFunc == null)
                    return;

                updateRotationFunc.Load<UObjectRecordStream>();
                long floatOffset = FindRotSpeedModFloatOffset(updateRotationFunc);

                if (floatOffset == -1)
                    return;

                byte[] data = File.ReadAllBytes(_config.TdGamePackagePath);
                float currentValue = BitConverter.ToSingle(data, (int)floatOffset);

                _isInitializingGraphicsSettings = true;

                if (Math.Abs(currentValue - 66536f) < 0.1f)
                {
                    UniformSensitivityComboBox.SelectedIndex = 0;
                }
                else
                {
                    UniformSensitivityComboBox.SelectedIndex = 1;
                }

                _isInitializingGraphicsSettings = false;
            }
            catch
            {
                _isInitializingGraphicsSettings = false;
            }
        }

        private void ShowUniformSensitivityInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Uniform Sensitivity Information",
                "Warning: Enabling uniform sensitivity is banned in official Mirror's Edge speedrun categories. " +
                "Only enable this if you are playing casually.\n\n" +
                "When pitching the camera up or down greater than a 63° angle from the horizon, horizontal camera sensitivity is reduced by 60%. " +
                "Enabling the uniform sensitivity option ensures the sensitivity is consistent at all vertical angles.",
                DialogHelper.MessageType.Information);
        }

        private void ApplyCm360_Click(object sender, RoutedEventArgs e)
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string tdInputIniPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

            if (!File.Exists(tdInputIniPath))
            {
                DialogHelper.ShowMessage("Error",
                    $"Cannot modify sensitivity, 'TdInput.ini' file is missing from \"{tdInputIniPath}\".\n\n" +
                    "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                    DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                if (!double.TryParse(DpiTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi) || dpi <= 0)
                {
                    DialogHelper.ShowMessage("Invalid Input", "Please enter a valid DPI value (must be greater than 0).", DialogHelper.MessageType.Error);
                    return;
                }

                if (!double.TryParse(Cm360TextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double cm360) || cm360 <= 0)
                {
                    DialogHelper.ShowMessage("Invalid Input", "Please enter a valid cm/360° value (must be greater than 0).", DialogHelper.MessageType.Error);
                    return;
                }

                double calculatedValue = (360 * 2.54) / (cm360 * dpi * 0.1538);

                ApplySensitivityMultiplier(tdInputIniPath, calculatedValue);

                SaveSettingsToIni();

                DialogHelper.ShowMessage("Success",
                    $"Sensitivity multiplier set to {calculatedValue:F6}\n\n" +
                    $"Based on {dpi} DPI and {cm360} cm/360°\n\n" +
                    "Important: Please ensure mouse smoothing is disabled and FOV-agnostic sensitivity is enabled (if applicable) for consistent sensitivity behaviour.",
                    DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply sensitivity settings: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ResetCm360_Click(object sender, RoutedEventArgs e)
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string tdInputIniPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

            if (!File.Exists(tdInputIniPath))
            {
                DialogHelper.ShowMessage("Error",
                    $"Cannot modify sensitivity, 'TdInput.ini' file is missing from \"{tdInputIniPath}\".\n\n" +
                    "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                    DialogHelper.MessageType.Error);
                return;
            }

            try
            {
                ApplySensitivityMultiplier(tdInputIniPath, null);

                DpiTextBox.Text = string.Empty;
                Cm360TextBox.Text = string.Empty;

                SaveSettingsToIni();

                DialogHelper.ShowMessage("Success",
                    "Sensitivity behaviour reset to default.",
                    DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to reset sensitivity settings: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ApplySensitivityMultiplier(string iniPath, double? calculatedValue)
        {
            if (!File.Exists(iniPath))
            {
                throw new FileNotFoundException($"TdInput.ini not found at: {iniPath}");
            }

            FileAttributes attributes = File.GetAttributes(iniPath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(iniPath, attributes & ~FileAttributes.ReadOnly);

            try
            {
                string[] lines = File.ReadAllLines(iniPath);
                bool inCorrectSection = false;
                string formattedMultiplier = calculatedValue.HasValue
                    ? calculatedValue.Value.ToString("F6", CultureInfo.InvariantCulture)
                    : string.Empty;

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmedLine = lines[i].Trim();

                    if (trimmedLine == "[TdGame.TdPlayerInputConsole]")
                    {
                        inCorrectSection = true;
                        continue;
                    }

                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        inCorrectSection = false;
                        continue;
                    }

                    if (inCorrectSection && lines[i].Contains('='))
                    {
                        string key = lines[i].Split('=')[0].Trim();

                        if (key == "MaxSensitivityMultiplier")
                        {
                            if (calculatedValue.HasValue)
                            {
                                lines[i] = $"MaxSensitivityMultiplier={formattedMultiplier}";
                            }
                            else
                            {
                                lines[i] = "MaxSensitivityMultiplier=1.800000";
                            }
                        }
                        else if (key == "MinSensitivityMultiplier")
                        {
                            if (calculatedValue.HasValue)
                            {
                                lines[i] = $"MinSensitivityMultiplier={formattedMultiplier}";
                            }
                            else
                            {
                                lines[i] = "MinSensitivityMultiplier=0.200000";
                            }
                        }
                    }
                }

                File.WriteAllLines(iniPath, lines);
            }
            finally
            {
                File.SetAttributes(iniPath, File.GetAttributes(iniPath) | FileAttributes.ReadOnly);
            }
        }

        private void ShowCm360Info_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("cm/360° Converter Information",
                "Converts your real-world mouse sensitivity (measured in centimeters per 360° turn) into Mirror's Edge sensitivity values.\n\n" +
                "1. Enter your mouse DPI\n" +
                "2. Enter your desired cm/360° (how many centimeters you want to move your mouse for a full 360° turn)\n" +
                "3. Check the 'Apply' box to calculate and apply the sensitivity\n\n" +
                "Please be aware that adjusting your sensitivity in-game will have no effect while this is enabled. Click the 'Reset' button to restore the default sensitivity behaviour.",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Gamepad Buttons

        private void LoadGamepadButtonsFromFiles()
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    return;
                }

                string cookedPcPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC");
                if (!Directory.Exists(cookedPcPath))
                {
                    return;
                }

                string tdGamePackagePath = Path.Combine(cookedPcPath, "TdGame.u");
                if (!File.Exists(tdGamePackagePath))
                {
                    return;
                }

                bool isPs3Setting = false;

                using var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package != null)
                {
                    var controlsSettingsClass = package.FindObject<UClass>("TdUIScene_ControlsSettings");

                    if (controlsSettingsClass == null)
                    {
                        var allClasses = package.Objects.OfType<UClass>().ToList();
                        controlsSettingsClass = allClasses.FirstOrDefault(c => c.Name.ToString().Contains("ControlsSettings", StringComparison.OrdinalIgnoreCase));
                    }

                    if (controlsSettingsClass != null)
                    {
                        string defaultObjectName = $"Default__{controlsSettingsClass.Name}";
                        var defaultObject = package.Objects
                            .FirstOrDefault(o => o.Name == defaultObjectName);

                        if (defaultObject is UObject uObject)
                        {
                            uObject.Load<UObjectRecordStream>();

                            if (uObject.Properties != null)
                            {
                                var pcControllerImagePathProp = uObject.Properties
                                    .OfType<UDefaultProperty>()
                                    .FirstOrDefault(p => p.Name?.ToString() == "PCControllerImagePath");

                                if (pcControllerImagePathProp != null)
                                {
                                    string pcPathValue = pcControllerImagePathProp.Value;
                                    isPs3Setting = pcPathValue.Contains("PS3", StringComparison.OrdinalIgnoreCase);
                                }
                            }
                        }
                    }
                }

                _isInitializingGraphicsSettings = true;
                GamepadButtonsComboBox.SelectedIndex = isPs3Setting ? 1 : 0;
                _isInitializingGraphicsSettings = false;
            }
            catch (Exception)
            {
                _isInitializingGraphicsSettings = true;
                GamepadButtonsComboBox.SelectedIndex = 0;
                _isInitializingGraphicsSettings = false;
            }
        }

        private async void GamepadButtonsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializingGraphicsSettings)
            {
                return;
            }

            if (GamepadButtonsComboBox.SelectedIndex == -1)
            {
                return;
            }

            string buttonType = GamepadButtonsComboBox.SelectedIndex == 0 ? "xbox" : "ps3";
            await ApplyGamepadButtons(buttonType);
        }

        private async Task ApplyGamepadButtons(string buttonType)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                {
                    DialogHelper.ShowMessage("Error", "Please select a valid game directory first.", DialogHelper.MessageType.Error);
                    return;
                }

                if (!Directory.Exists(_config.GameDirectoryPath))
                {
                    DialogHelper.ShowMessage("Error", "The selected game directory does not exist.", DialogHelper.MessageType.Error);
                    return;
                }

                ShowProgress($"Applying {buttonType.ToUpper()} gamepad buttons...", true);
                UpdateStatus($"Applying {buttonType.ToUpper()} gamepad buttons...");

                await Task.Run(() =>
                {
                    string cookedPcPath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC");

                    if (!Directory.Exists(cookedPcPath))
                    {
                        throw new DirectoryNotFoundException($"CookedPC directory not found at: {cookedPcPath}");
                    }

                    string[] tsLocFiles = Directory.GetFiles(cookedPcPath, "Ts_LOC_*.upk");

                    if (tsLocFiles.Length == 0)
                    {
                        throw new FileNotFoundException("No Ts_LOC_*.upk files found in CookedPC directory.");
                    }

                    foreach (string tsLocFilePath in tsLocFiles)
                    {
                        try
                        {
                            _decompressionService.RunDecompressor(tsLocFilePath);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Failed to decompress {Path.GetFileName(tsLocFilePath)}: {ex.Message}");
                        }
                    }

                    // todo: utilise UELib instead of hardcoded byte patterns - works for now though
                    byte[] gamepadPatternHeader = new byte[] { 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00 };

                    foreach (string tsLocFilePath in tsLocFiles)
                    {
                        string fileName = Path.GetFileName(tsLocFilePath);
                        string countryCode = fileName.Split('_').Last().Substring(0, 3).ToUpper();

                        byte[]? replacement = GetGamepadReplacement(countryCode, buttonType);

                        if (replacement == null)
                        {
                            continue;
                        }

                        byte[] data = File.ReadAllBytes(tsLocFilePath);

                        // find 2nd occurrence of gamepad pattern
                        int startIndex = 0;
                        bool patternFound = true;
                        for (int occurrence = 0; occurrence < 2; occurrence++)
                        {
                            startIndex = FindBytePattern(data, gamepadPatternHeader, startIndex);
                            if (startIndex == -1)
                            {
                                patternFound = false;
                                break;
                            }
                            startIndex += 1;
                        }

                        if (!patternFound)
                        {
                            continue;
                        }

                        int replaceIndex = startIndex + 43;
                        int endIndex = replaceIndex + 12;

                        byte[] modifiedData = new byte[data.Length];
                        Array.Copy(data, 0, modifiedData, 0, replaceIndex);
                        Array.Copy(replacement, 0, modifiedData, replaceIndex, replacement.Length);
                        Array.Copy(data, endIndex, modifiedData, endIndex, data.Length - endIndex);

                        File.WriteAllBytes(tsLocFilePath, modifiedData);
                    }

                    string tdGamePackagePath = Path.Combine(cookedPcPath, "TdGame.u");
                    if (!File.Exists(tdGamePackagePath))
                    {
                        throw new FileNotFoundException($"TdGame.u not found at: {tdGamePackagePath}");
                    }

                    ApplyControllerImagePathSwap(tdGamePackagePath, buttonType);
                });

                HideProgress();
                UpdateStatus("Ready.");
            }
            catch (Exception ex)
            {
                HideProgress();
                UpdateStatus("Error occurred");
                DialogHelper.ShowMessage("Error", $"Failed to apply gamepad buttons:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private byte[]? GetGamepadReplacement(string countryCode, string buttonType)
        {
            var mappings = new Dictionary<string, (string ps3, string xbox)>
            {
                { "INT", ("410000004200000043000000", "450000004600000044000000") },
                { "CZE", ("540000005500000056000000", "580000005900000057000000") },
                { "HUN", ("540000005500000056000000", "580000005900000057000000") },
                { "DEU", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "ESN", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "FRA", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "ITA", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "POL", ("8B0000008C0000008D000000", "8F000000900000008E000000") },
                { "RUS", ("7E0000007F00000080000000", "820000008300000081000000") },
                { "KOR", ("2E0100002F01000030010000", "320100003301000031010000") },
                { "JPN", ("390100003A0100003B010000", "3D0100003E0100003C010000") },
                { "CHS", ("810100008201000083010000", "850100008601000084010000") },
                { "CHT", ("7F0100008001000081010000", "830100008401000082010000") }
            };

            if (mappings.TryGetValue(countryCode, out var mapping))
            {
                string hexString = buttonType == "ps3" ? mapping.ps3 : mapping.xbox;
                return HexStringToByteArray(hexString);
            }

            return null;
        }

        private byte[] HexStringToByteArray(string hex)
        {
            int length = hex.Length;
            byte[] bytes = new byte[length / 2];
            for (int i = 0; i < length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }

        private int FindBytePattern(byte[] data, byte[] pattern, int startIndex)
        {
            for (int i = startIndex; i <= data.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }

        private void ApplyControllerImagePathSwap(string tdGamePackagePath, string buttonType)
        {
            FileAttributes attributes = File.GetAttributes(tdGamePackagePath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

            if (wasReadOnly)
            {
                File.SetAttributes(tdGamePackagePath, attributes & ~FileAttributes.ReadOnly);
            }

            try
            {
                using var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    throw new Exception("Failed to load TdGame.u package.");

                var controlsSettingsClass = package.FindObject<UClass>("TdUIScene_ControlsSettings");

                if (controlsSettingsClass == null)
                {
                    throw new Exception("TdUIScene_ControlsSettings class not found in TdGame.u");
                }

                string defaultObjectName = $"Default__{controlsSettingsClass.Name}";

                var defaultObject = package.Objects
                    .FirstOrDefault(o => o.Name == defaultObjectName);

                if (defaultObject == null || !(defaultObject is UObject uObject))
                {
                    throw new Exception($"Default object not found for class {controlsSettingsClass.Name}");
                }

                uObject.Load<UObjectRecordStream>();

                if (uObject.Properties == null)
                {
                    throw new Exception("Failed to load properties for Default__TdUIScene_ControlsSettings");
                }

                var pcControllerImagePathProp = uObject.Properties
                    .OfType<UDefaultProperty>()
                    .FirstOrDefault(p => p.Name?.ToString() == "PCControllerImagePath");

                var ps3ControllerImagePathProp = uObject.Properties
                    .OfType<UDefaultProperty>()
                    .FirstOrDefault(p => p.Name?.ToString() == "PS3ControllerImagePath");

                if (pcControllerImagePathProp == null || ps3ControllerImagePathProp == null)
                {
                    throw new Exception("Required controller image path properties not found");
                }

                string currentPcPath = pcControllerImagePathProp.Value.Trim('"');
                string currentPs3Path = ps3ControllerImagePathProp.Value.Trim('"');

                bool shouldSwap = (buttonType == "ps3" && currentPcPath.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) ||
                                  (buttonType == "xbox" && currentPcPath.Contains("PS3", StringComparison.OrdinalIgnoreCase));

                if (!shouldSwap)
                {
                    return;
                }

                int pcNameIndex = package.Names.FindIndex(n => n.ToString() == "PCControllerImagePath");
                int ps3NameIndex = package.Names.FindIndex(n => n.ToString() == "PS3ControllerImagePath");

                if (pcNameIndex == -1 || ps3NameIndex == -1)
                {
                    throw new Exception("Could not find property name indices");
                }

                byte[] data = File.ReadAllBytes(tdGamePackagePath);

                var exportTable = uObject.ExportTable;
                if (exportTable == null)
                {
                    throw new Exception("Export table not found for Default__TdUIScene_ControlsSettings");
                }

                long searchStart = exportTable.SerialOffset;
                long searchEnd = exportTable.SerialOffset + exportTable.SerialSize;

                byte[] pcNameIndexBytes = BitConverter.GetBytes((long)pcNameIndex);
                byte[] ps3NameIndexBytes = BitConverter.GetBytes((long)ps3NameIndex);

                long pcPropertyOffset = -1;
                long ps3PropertyOffset = -1;

                for (long i = searchStart; i < searchEnd - 8; i++)
                {
                    bool matchesPc = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != pcNameIndexBytes[j])
                        {
                            matchesPc = false;
                            break;
                        }
                    }

                    if (matchesPc && pcPropertyOffset == -1)
                    {
                        pcPropertyOffset = i;
                    }

                    bool matchesPs3 = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != ps3NameIndexBytes[j])
                        {
                            matchesPs3 = false;
                            break;
                        }
                    }

                    if (matchesPs3 && ps3PropertyOffset == -1)
                    {
                        ps3PropertyOffset = i;
                    }

                    if (pcPropertyOffset != -1 && ps3PropertyOffset != -1)
                    {
                        break;
                    }
                }

                if (pcPropertyOffset == -1 || ps3PropertyOffset == -1)
                {
                    throw new Exception("Could not find property name index offsets");
                }

                Array.Copy(ps3NameIndexBytes, 0, data, pcPropertyOffset, 8);
                Array.Copy(pcNameIndexBytes, 0, data, ps3PropertyOffset, 8);

                File.WriteAllBytes(tdGamePackagePath, data);
            }
            finally
            {
                if (wasReadOnly)
                {
                    File.SetAttributes(tdGamePackagePath, File.GetAttributes(tdGamePackagePath) | FileAttributes.ReadOnly);
                }
            }
        }

        private void ShowGamepadButtonsInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Gamepad Buttons Information",
                "By default, Mirror's Edge will show only the Xbox button prompts when using a controller. " +
                "This setting lets you toggle between the PS3 and Xbox button prompts.\n\n" +
                "Note: This only targets the UI, it does not enable Sixaxis support for PS3 controllers. " +
                "An XInput wrapper is required if using a DualShock or DualSense controller (such as Steam Input, DS4Windows, DualSenseX, etc.).",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Custom Keybinds

        private bool _isLoadingKeybinds = false;

        private enum KeybindType { Standard, GodMode, TriggersVolumes, Macro }

        private class KeybindInfo
        {
            public string SetCommand { get; }
            public string RemoveCommand { get; }
            public KeybindType Type { get; }

            public KeybindInfo(string setCommand, string removeCommand = null, KeybindType type = KeybindType.Standard)
            {
                SetCommand = setCommand;
                RemoveCommand = removeCommand ?? setCommand;
                Type = type;
            }
        }

        private static readonly Dictionary<string, KeybindInfo> _keybindMap = new Dictionary<string, KeybindInfo>
        {
            ["RestartLevelKeyTextBox"] = new KeybindInfo("RestartLevel"),
            ["LoadLastCheckpointKeyTextBox"] = new KeybindInfo("RestartFromLastCheckpoint"),
            ["RestartTimeTrialKeyTextBox"] = new KeybindInfo("TriggerRestartRaceblink"),
            ["ResetReactionTimeKeyTextBox"] = new KeybindInfo(
                "set TdPlayerController ReactionTimeEnergy 0 | OnRelease set TdPlayerController ReactionTimeEnergy 100",
                "set TdPlayerController ReactionTimeEnergy 0"),
            ["GodModeKeyTextBox"] = new KeybindInfo(null, null, KeybindType.GodMode),
            ["KillBotsKeyTextBox"] = new KeybindInfo("killbots"),
            ["ThirdPersonKeyTextBox"] = new KeybindInfo("FreeFlightCamera"),
            ["ToggleHUDKeyTextBox"] = new KeybindInfo("Showhud"),
            ["FPSIndicatorKeyTextBox"] = new KeybindInfo("stat xunit"),
            ["LevelStatsKeyTextBox"] = new KeybindInfo("stat levels"),
            ["TriggersVolumesKeyTextBox"] = new KeybindInfo(null, null, KeybindType.TriggersVolumes),
            ["ShowCollisionKeyTextBox"] = new KeybindInfo("nxvis collision"),
            ["NoclipKeyTextBox"] = new KeybindInfo("Noclip"),
            ["SaveStateKeyTextBox"] = new KeybindInfo("SaveLocation"),
            ["LoadSavedStateKeyTextBox"] = new KeybindInfo(
                "TpToSavedLocation | OnRelease TpToSavedLocation_OnRelease",
                "TpToSavedLocation"),
            ["SaveTimerLocationKeyTextBox"] = new KeybindInfo("SaveTimerLocation"),
            ["DeleteViewedActorKeyTextBox"] = new KeybindInfo("DestroyViewedActor"),
            ["ScrollDownMacroKeyTextBox"] = new KeybindInfo("ScrollDownMacroKey", "ScrollDownMacroKey", KeybindType.Macro),
            ["ScrollUpMacroKeyTextBox"] = new KeybindInfo("ScrollUpMacroKey", "ScrollUpMacroKey", KeybindType.Macro),
        };

        private Dictionary<string, string> _ue3KeyMap = new Dictionary<string, string>
        {
            // Function keys
            { "F1", "F1" }, { "F2", "F2" }, { "F3", "F3" }, { "F4", "F4" },
            { "F5", "F5" }, { "F6", "F6" }, { "F7", "F7" }, { "F8", "F8" },
            { "F9", "F9" }, { "F10", "F10" }, { "F11", "F11" }, { "F12", "F12" },
            
            // Special keys
            { "Escape", "Escape" }, { "Tab", "Tab" }, { "OemTilde", "Tilde" },
            { "Scroll", "ScrollLock" }, { "Pause", "Pause" },
            { "D1", "ONE" }, { "D2", "TWO" }, { "D3", "THREE" }, { "D4", "FOUR" },
            { "D5", "FIVE" }, { "D6", "SIX" }, { "D7", "SEVEN" }, { "D8", "EIGHT" },
            { "D9", "NINE" }, { "D0", "ZERO" },
            { "OemMinus", "Underscore" }, { "OemPlus", "Equals" },
            { "OemBackslash", "Backslash" }, { "OemPipe", "Backslash" },
            { "OemOpenBrackets", "LeftBracket" }, { "OemCloseBrackets", "RightBracket" },
            { "Return", "Enter" }, { "Enter", "Enter" }, { "Capital", "CapsLock" },
            { "OemSemicolon", "Semicolon" }, { "OemQuotes", "Quote" },
            { "LeftShift", "LeftShift" }, { "RightShift", "RightShift" },
            { "OemComma", "Comma" }, { "OemPeriod", "Period" }, { "OemQuestion", "Slash" },
            { "LeftCtrl", "LeftControl" }, { "RightCtrl", "RightControl" },
            { "LeftAlt", "LeftAlt" }, { "RightAlt", "RightAlt" },
            { "Space", "SpaceBar" },
            { "Left", "Left" }, { "Up", "Up" }, { "Down", "Down" }, { "Right", "Right" },
            { "Home", "Home" }, { "End", "End" }, { "Insert", "Insert" },
            { "PageUp", "PageUp" }, { "Delete", "Delete" }, { "PageDown", "PageDown" },
            { "NumLock", "NumLock" },
            { "Divide", "Divide" }, { "Multiply", "Multiply" },
            { "Subtract", "Subtract" }, { "Add", "Add" },
            { "NumPad0", "NumPadZero" }, { "NumPad1", "NumPadOne" },
            { "NumPad2", "NumPadTwo" }, { "NumPad3", "NumPadThree" },
            { "NumPad4", "NumPadFour" }, { "NumPad5", "NumPadFive" },
            { "NumPad6", "NumPadSix" }, { "NumPad7", "NumPadSeven" },
            { "NumPad8", "NumPadEight" }, { "NumPad9", "NumPadNine" },
            { "Decimal", "Decimal" }
        };

        private async void KeybindField_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isLoadingKeybinds)
                return;

            var textBox = sender as System.Windows.Controls.TextBox;
            if (textBox == null || !_keybindMap.TryGetValue(textBox.Name, out var info))
                return;

            if (info.Type == KeybindType.Macro)
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                string settingsFilePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");
                if (!File.Exists(settingsFilePath))
                {
                    DialogHelper.ShowMessage("Tweaks Scripts Not Installed",
                        "The TweaksScriptsSettings file was not found.\n\n" +
                        "Please install the Tweaks Scripts package from the Game Tweaks section before configuring macro keybinds.",
                        DialogHelper.MessageType.Warning);
                    return;
                }
            }

            var result = await MaterialDesignThemes.Wpf.DialogHost.Show(
                new Helpers.KeybindCaptureDialog(_ue3KeyMap), "RootDialog");

            await Task.Yield();

            if (result is string ue3Key)
            {
                if (string.IsNullOrEmpty(ue3Key))
                {
                    textBox.Text = string.Empty;
                    switch (info.Type)
                    {
                        case KeybindType.GodMode:
                            await RemoveGodModeKeybind();
                            break;
                        case KeybindType.TriggersVolumes:
                            await RemoveTriggersVolumesKeybind();
                            break;
                        case KeybindType.Macro:
                            await UpdateMacroKeybind(info.RemoveCommand, "");
                            break;
                        default:
                            await RemoveKeybind(info.RemoveCommand);
                            break;
                    }
                }
                else
                {
                    textBox.Text = ue3Key;
                    switch (info.Type)
                    {
                        case KeybindType.GodMode:
                            await UpdateGodModeKeybind(ue3Key);
                            break;
                        case KeybindType.TriggersVolumes:
                            await UpdateTriggersVolumesKeybind(ue3Key);
                            break;
                        case KeybindType.Macro:
                            if (!await UpdateMacroKeybind(info.SetCommand, ue3Key))
                                textBox.Text = string.Empty;
                            break;
                        default:
                            await UpdateKeybind(info.SetCommand, ue3Key);
                            break;
                    }
                }
            }
        }

        private void LoadCustomKeybinds()
        {
            try
            {
                _isLoadingKeybinds = true;

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                    return;

                string[] lines = File.ReadAllLines(tdInputPath);

                bool inPlayerInput = false;
                foreach (string line in lines)
                {
                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                        continue;
                    }

                    if (!inPlayerInput)
                        continue;

                    if (line.Contains("Command=\"RestartLevel\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            RestartLevelKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"RestartFromLastCheckpoint\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            LoadLastCheckpointKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"TriggerRestartRaceblink\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            RestartTimeTrialKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"set TdPlayerController ReactionTimeEnergy 0"))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            ResetReactionTimeKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"EnableGodMode\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            GodModeKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"killbots\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            KillBotsKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"FreeFlightCamera\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            ThirdPersonKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"Showhud\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            ToggleHUDKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"stat xunit\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            FPSIndicatorKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"stat levels\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            LevelStatsKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"ShowTriggersAndVolumes\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            TriggersVolumesKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"nxvis collision\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            ShowCollisionKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"Noclip\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            NoclipKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"SaveLocation\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            SaveStateKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"TpToSavedLocation"))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            LoadSavedStateKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"SaveTimerLocation\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            SaveTimerLocationKeyTextBox.Text = key;
                        }
                    }
                    else if (line.Contains("Command=\"DestroyViewedActor\""))
                    {
                        int nameStart = line.IndexOf("Name=\"") + 6;
                        int nameEnd = line.IndexOf("\"", nameStart);
                        if (nameStart > 5 && nameEnd > nameStart)
                        {
                            string key = line.Substring(nameStart, nameEnd - nameStart);
                            DeleteViewedActorKeyTextBox.Text = key;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load custom keybinds: {ex.Message}");
            }
            finally
            {
                _isLoadingKeybinds = false;
            }
        }

        private async Task UpdateKeybind(string command, string key)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                {
                    DialogHelper.ShowMessage("Error",
                        $"Cannot set keybind, 'TdInput.ini' file is missing from \"{tdInputPath}\".\n\n" +
                        "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                string? conflictingCommand = null;

                await Task.Run(() =>
                {
                    string[] lines = File.ReadAllLines(tdInputPath);
                    List<string> newLines = new List<string>();
                    bool foundSection = false;
                    bool foundBinding = false;
                    int mouseSmoothingIndex = -1;

                    foundSection = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Trim() == "[Engine.PlayerInput]")
                        {
                            foundSection = true;
                            continue;
                        }

                        if (foundSection && lines[i].Trim().StartsWith("[") && lines[i].Trim().EndsWith("]"))
                        {
                            break;
                        }

                        if (foundSection && lines[i].Contains("Bindings=") && lines[i].Contains($"Name=\"{key}\""))
                        {
                            int cmdStart = lines[i].IndexOf("Command=\"") + 9;
                            int cmdEnd = lines[i].IndexOf("\"", cmdStart);
                            if (cmdStart > 8 && cmdEnd > cmdStart)
                            {
                                string existingCommand = lines[i].Substring(cmdStart, cmdEnd - cmdStart);
                                if (existingCommand != command)
                                {
                                    conflictingCommand = existingCommand;
                                    return;
                                }
                            }
                        }
                    }

                    foundSection = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Trim() == "[Engine.PlayerInput]")
                        {
                            foundSection = true;
                        }

                        if (foundSection && lines[i].Contains("bEnableMouseSmoothing"))
                        {
                            mouseSmoothingIndex = i;
                        }

                        if (lines[i].Contains($"Command=\"{command}\""))
                        {
                            int nameStart = lines[i].IndexOf("Name=\"") + 6;
                            int nameEnd = lines[i].IndexOf("\"", nameStart);
                            if (nameStart > 5 && nameEnd > nameStart)
                            {
                                string beforeName = lines[i].Substring(0, nameStart);
                                string afterName = lines[i].Substring(nameEnd);
                                lines[i] = beforeName + key + afterName;
                            }
                            foundBinding = true;
                        }
                    }

                    if (!foundBinding && mouseSmoothingIndex >= 0)
                    {
                        newLines = new List<string>(lines);
                        string newBinding = $"Bindings=(Name=\"{key}\",Command=\"{command}\",Control=False,Shift=False,Alt=False)";
                        newLines.Insert(mouseSmoothingIndex + 1, newBinding);
                        lines = newLines.ToArray();
                    }

                    FileAttributes attributes = File.GetAttributes(tdInputPath);
                    bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

                    if (wasReadOnly)
                    {
                        File.SetAttributes(tdInputPath, attributes & ~FileAttributes.ReadOnly);
                    }

                    File.WriteAllLines(tdInputPath, lines);

                    if (wasReadOnly)
                    {
                        File.SetAttributes(tdInputPath, attributes);
                    }
                });

                if (conflictingCommand != null)
                {
                    await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                        "Please choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogHelper.MessageType.Warning);

                    if (command == "RestartLevel")
                    {
                        await Dispatcher.InvokeAsync(() => RestartLevelKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "RestartFromLastCheckpoint")
                    {
                        await Dispatcher.InvokeAsync(() => LoadLastCheckpointKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "TriggerRestartRaceblink")
                    {
                        await Dispatcher.InvokeAsync(() => RestartTimeTrialKeyTextBox.Text = string.Empty);
                    }
                    else if (command.StartsWith("set TdPlayerController ReactionTimeEnergy"))
                    {
                        await Dispatcher.InvokeAsync(() => ResetReactionTimeKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "killbots")
                    {
                        await Dispatcher.InvokeAsync(() => KillBotsKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "FreeFlightCamera")
                    {
                        await Dispatcher.InvokeAsync(() => ThirdPersonKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "Showhud")
                    {
                        await Dispatcher.InvokeAsync(() => ToggleHUDKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "stat xunit")
                    {
                        await Dispatcher.InvokeAsync(() => FPSIndicatorKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "stat levels")
                    {
                        await Dispatcher.InvokeAsync(() => LevelStatsKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "nxvis collision")
                    {
                        await Dispatcher.InvokeAsync(() => ShowCollisionKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "Noclip")
                    {
                        await Dispatcher.InvokeAsync(() => NoclipKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "SaveLocation")
                    {
                        await Dispatcher.InvokeAsync(() => SaveStateKeyTextBox.Text = string.Empty);
                    }
                    else if (command.StartsWith("TpToSavedLocation"))
                    {
                        await Dispatcher.InvokeAsync(() => LoadSavedStateKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "SaveTimerLocation")
                    {
                        await Dispatcher.InvokeAsync(() => SaveTimerLocationKeyTextBox.Text = string.Empty);
                    }
                    else if (command == "DestroyViewedActor")
                    {
                        await Dispatcher.InvokeAsync(() => DeleteViewedActorKeyTextBox.Text = string.Empty);
                    }
                }

                if (conflictingCommand == null)
                {
                    string? macroConflict = CheckMacroKeybindConflict(key);
                    if (macroConflict != null)
                    {
                        conflictingCommand = macroConflict;
                        await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                            $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                            "Please choose a different key.",
                            DialogHelper.MessageType.Warning);
                    }
                }

                if (conflictingCommand == null)
                {
                    // need to add exec flags to these functions, by default they are not executable via keybinds
                    if (command == "RestartFromLastCheckpoint")
                    {
                        await AddExecFlagToFunction("TdSPGame", "RestartFromLastCheckpoint");
                    }
                    else if (command == "TriggerRestartRaceblink")
                    {
                        await AddExecFlagToFunction("TdTimeTrialHUD", "TriggerRestartRaceblink");
                    }
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to update keybind:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private async Task UpdateGodModeKeybind(string key)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                {
                    DialogHelper.ShowMessage("File Not Found",
                        "TdInput.ini not found. Please launch Mirror's Edge at least once to create the configuration file.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                bool inPlayerInput = false;
                string? conflictingCommand = null;

                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                    }
                    else if (inPlayerInput && line.Contains($"Name=\"{key}\"") && line.Contains("Command="))
                    {
                        int commandStart = line.IndexOf("Command=\"") + 9;
                        int commandEnd = line.IndexOf("\"", commandStart);
                        if (commandStart > 8 && commandEnd > commandStart)
                        {
                            string existingCommand = line.Substring(commandStart, commandEnd - commandStart);

                            if (existingCommand != "EnableGodMode" &&
                                !existingCommand.Contains("bGodMode"))
                            {
                                conflictingCommand = existingCommand;
                                break;
                            }
                        }
                    }
                }

                if (conflictingCommand != null)
                {
                    await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                        "Please choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogHelper.MessageType.Warning);
                    await Dispatcher.InvokeAsync(() => GodModeKeyTextBox.Text = string.Empty);
                    return;
                }

                string? macroConflict = CheckMacroKeybindConflict(key);
                if (macroConflict != null)
                {
                    await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Please choose a different key.",
                        DialogHelper.MessageType.Warning);
                    await Dispatcher.InvokeAsync(() => GodModeKeyTextBox.Text = string.Empty);
                    return;
                }

                var modifiedLines = new List<string>();
                inPlayerInput = false;
                bool foundEnableGodMode = false;
                bool foundDisableGodMode = false;
                bool foundEnableGodModeCommand = false;
                int bEnableMouseSmoothingIndex = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                        modifiedLines.Add(line);
                    }
                    else if (inPlayerInput && line.Contains("Command=\"EnableGodMode\"") && !line.Contains("Name=\"EnableGodMode\""))
                    {
                        if (!foundEnableGodMode)
                        {
                            modifiedLines.Add($"Bindings=(Name=\"{key}\",Command=\"EnableGodMode\",Control=False,Shift=False,Alt=False)");
                            foundEnableGodMode = true;
                        }
                    }
                    else if (inPlayerInput && line.Contains("Name=\"EnableGodMode\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"EnableGodMode\",Command=\"set TdPlayerController bGodMode 1 | set TdKillVolume CollisionType COLLIDE_NoCollision | set TdKillZoneVolume CollisionType COLLIDE_NoCollision | set TdKillZoneKiller CollisionType COLLIDE_NoCollision | set TdFallHeightVolume CollisionType COLLIDE_NoCollision | set TdBarbedWireVolume CollisionType COLLIDE_NoCollision | SetBind {key} DisableGodMode\",Control=False,Shift=False,Alt=False)");
                        foundEnableGodModeCommand = true;
                    }
                    else if (inPlayerInput && line.Contains("Name=\"DisableGodMode\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"DisableGodMode\",Command=\"set TdPlayerController bGodMode 0 | set TdKillVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneKiller CollisionType COLLIDE_CustomDefault | set TdFallHeightVolume CollisionType COLLIDE_CustomDefault | set TdBarbedWireVolume CollisionType COLLIDE_CustomDefault | SetBind {key} EnableGodMode\",Control=False,Shift=False,Alt=False)");
                        foundDisableGodMode = true;
                    }
                    else
                    {
                        if (inPlayerInput && line.Contains("bEnableMouseSmoothing"))
                        {
                            bEnableMouseSmoothingIndex = modifiedLines.Count;
                        }
                        modifiedLines.Add(line);
                    }
                }

                if (!foundEnableGodMode || !foundEnableGodModeCommand || !foundDisableGodMode)
                {
                    if (bEnableMouseSmoothingIndex >= 0)
                    {
                        if (!foundEnableGodMode)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"{key}\",Command=\"EnableGodMode\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundEnableGodModeCommand)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"EnableGodMode\",Command=\"set TdPlayerController bGodMode 1 | set TdKillVolume CollisionType COLLIDE_NoCollision | set TdKillZoneVolume CollisionType COLLIDE_NoCollision | set TdKillZoneKiller CollisionType COLLIDE_NoCollision | set TdFallHeightVolume CollisionType COLLIDE_NoCollision | set TdBarbedWireVolume CollisionType COLLIDE_NoCollision | SetBind {key} DisableGodMode\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundDisableGodMode)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"DisableGodMode\",Command=\"set TdPlayerController bGodMode 0 | set TdKillVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneKiller CollisionType COLLIDE_CustomDefault | set TdFallHeightVolume CollisionType COLLIDE_CustomDefault | set TdBarbedWireVolume CollisionType COLLIDE_CustomDefault | SetBind {key} EnableGodMode\",Control=False,Shift=False,Alt=False)");
                        }
                    }
                }

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to update God Mode keybind:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private async Task RemoveGodModeKeybind()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                    return;

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                var modifiedLines = lines.Where(line =>
                    !line.Contains("Command=\"EnableGodMode\"") &&
                    !line.Contains("Name=\"EnableGodMode\"") &&
                    !line.Contains("Name=\"DisableGodMode\"")).ToList();

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove God Mode keybind:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private async Task UpdateTriggersVolumesKeybind(string key)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                {
                    DialogHelper.ShowMessage("File Not Found",
                        "TdInput.ini not found. Please launch Mirror's Edge at least once to create the configuration file.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                bool inPlayerInput = false;
                string? conflictingCommand = null;

                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                    }
                    else if (inPlayerInput && line.Contains($"Name=\"{key}\"") && line.Contains("Command="))
                    {
                        int commandStart = line.IndexOf("Command=\"") + 9;
                        int commandEnd = line.IndexOf("\"", commandStart);
                        if (commandStart > 8 && commandEnd > commandStart)
                        {
                            string existingCommand = line.Substring(commandStart, commandEnd - commandStart);

                            if (existingCommand != "ShowTriggersAndVolumes" &&
                                !existingCommand.Contains("show collision"))
                            {
                                conflictingCommand = existingCommand;
                                break;
                            }
                        }
                    }
                }

                if (conflictingCommand != null)
                {
                    await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                        "Please choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogHelper.MessageType.Warning);
                    await Dispatcher.InvokeAsync(() => TriggersVolumesKeyTextBox.Text = string.Empty);
                    return;
                }

                string? macroConflict = CheckMacroKeybindConflict(key);
                if (macroConflict != null)
                {
                    await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Please choose a different key.",
                        DialogHelper.MessageType.Warning);
                    await Dispatcher.InvokeAsync(() => TriggersVolumesKeyTextBox.Text = string.Empty);
                    return;
                }

                string showCommand = "show collision | set Trigger bHidden 0 | set TriggerVolume bHidden 0 | set BlockingVolume bHidden 0 | set TdAIBlockingVolume bHidden 0 | set TdAIKeepMovingVolume bHidden 0 | set TdAIPawnBlockingVolume bHidden 0 | set TdBalanceWalkVolume bHidden 0 | set TdBarbedWireVolume bHidden 0 | set TdCheckpointVolume bHidden 0 | set TdConfinedVolumePathNode bHidden 0 | set TdCoverGroupVolume bHidden 0 | set TdFallHeightVolume bHidden 0 | set TdKillVolume bHidden 0 | set TdKillZoneVolume bHidden 0 | set TdLadderVolume bHidden 0 | set TdLedgeWalkVolume bHidden 0 | set TdMovementExclusion bHidden 0 | set TdMovementVolume bHidden 0 | set TdMoveVolumeRenderComponent bHidden 0 | set TdPathLimitsVolume bHidden 0 | set TdSwingVolume bHidden 0 | set TdTriggerVolume bHidden 0 | set TdZiplineVolume bHidden 0 | SetBind " + key + " HideTriggersAndVolumes";
                string hideCommand = "show collision | set Trigger bHidden 1 | set TriggerVolume bHidden 1 | set BlockingVolume bHidden 1 | set TdAIBlockingVolume bHidden 1 | set TdAIKeepMovingVolume bHidden 1 | set TdAIPawnBlockingVolume bHidden 1 | set TdBalanceWalkVolume bHidden 1 | set TdBarbedWireVolume bHidden 1 | set TdCheckpointVolume bHidden 1 | set TdConfinedVolumePathNode bHidden 1 | set TdCoverGroupVolume bHidden 1 | set TdFallHeightVolume bHidden 1 | set TdKillVolume bHidden 1 | set TdKillZoneVolume bHidden 1 | set TdLadderVolume bHidden 1 | set TdLedgeWalkVolume bHidden 1 | set TdMovementExclusion bHidden 1 | set TdMovementVolume bHidden 1 | set TdMoveVolumeRenderComponent bHidden 1 | set TdPathLimitsVolume bHidden 1 | set TdSwingVolume bHidden 1 | set TdTriggerVolume bHidden 1 | set TdZiplineVolume bHidden 1 | SetBind " + key + " ShowTriggersAndVolumes";

                var modifiedLines = new List<string>();
                inPlayerInput = false;
                bool foundShowTriggersAndVolumes = false;
                bool foundShowCommand = false;
                bool foundHideCommand = false;
                int bEnableMouseSmoothingIndex = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                        modifiedLines.Add(line);
                    }
                    else if (inPlayerInput && line.Contains("Command=\"ShowTriggersAndVolumes\"") && !line.Contains("Name=\"ShowTriggersAndVolumes\""))
                    {
                        if (!foundShowTriggersAndVolumes)
                        {
                            modifiedLines.Add($"Bindings=(Name=\"{key}\",Command=\"ShowTriggersAndVolumes\",Control=False,Shift=False,Alt=False)");
                            foundShowTriggersAndVolumes = true;
                        }
                    }
                    else if (inPlayerInput && line.Contains("Name=\"ShowTriggersAndVolumes\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"ShowTriggersAndVolumes\",Command=\"{showCommand}\",Control=False,Shift=False,Alt=False)");
                        foundShowCommand = true;
                    }
                    else if (inPlayerInput && line.Contains("Name=\"HideTriggersAndVolumes\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"HideTriggersAndVolumes\",Command=\"{hideCommand}\",Control=False,Shift=False,Alt=False)");
                        foundHideCommand = true;
                    }
                    else
                    {
                        if (inPlayerInput && line.Contains("bEnableMouseSmoothing"))
                        {
                            bEnableMouseSmoothingIndex = modifiedLines.Count;
                        }
                        modifiedLines.Add(line);
                    }
                }

                if (!foundShowTriggersAndVolumes || !foundShowCommand || !foundHideCommand)
                {
                    if (bEnableMouseSmoothingIndex >= 0)
                    {
                        if (!foundShowTriggersAndVolumes)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"{key}\",Command=\"ShowTriggersAndVolumes\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundShowCommand)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"ShowTriggersAndVolumes\",Command=\"{showCommand}\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundHideCommand)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"HideTriggersAndVolumes\",Command=\"{hideCommand}\",Control=False,Shift=False,Alt=False)");
                        }
                    }
                }

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to update Triggers & Volumes keybind:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private async Task RemoveTriggersVolumesKeybind()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                    return;

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                var modifiedLines = lines.Where(line =>
                    !line.Contains("Command=\"ShowTriggersAndVolumes\"") &&
                    !line.Contains("Name=\"ShowTriggersAndVolumes\"") &&
                    !line.Contains("Name=\"HideTriggersAndVolumes\"")).ToList();

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove Triggers & Volumes keybind:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private async Task AddExecFlagToFunction(string className, string functionName)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                string tdGamePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");

                if (!File.Exists(tdGamePath))
                    return;

                await Task.Run(() =>
                {
                    UnrealPackage? package = null;
                    try
                    {
                        package = UnrealLoader.LoadPackage(tdGamePath, FileAccess.Read);
                        package?.InitializePackage();

                        if (package == null)
                            return;

                        var function = package.Objects
                            .OfType<UFunction>()
                            .FirstOrDefault(f => f.Name == functionName &&
                                                f.Outer != null && f.Outer.Name == className);

                        if (function == null)
                            return;

                        if (function.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Exec))
                            return;

                        var functionExport = function.ExportTable;
                        if (functionExport == null)
                            return;

                        byte[] data = File.ReadAllBytes(tdGamePath);

                        ulong currentFlagsValue = function.FunctionFlags;
                        uint uelibFlags = (uint)currentFlagsValue;
                        byte[] uelibFlagsBytes = BitConverter.GetBytes(uelibFlags);

                        long serialStart = functionExport.SerialOffset;
                        long uelibFlagsOffset = -1;
                        long wideSearchStart = Math.Max(0, serialStart - 1000);
                        long wideSearchEnd = Math.Min(data.Length - 4, serialStart + functionExport.SerialSize + 1000);

                        for (long i = wideSearchStart; i <= wideSearchEnd - 4; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < 4; j++)
                            {
                                if (data[i + j] != uelibFlagsBytes[j])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (match)
                            {
                                long relativeOffset = i - serialStart;

                                if (relativeOffset >= 0 && relativeOffset < functionExport.SerialSize)
                                {
                                    uelibFlagsOffset = i;
                                }
                            }
                        }

                        if (uelibFlagsOffset == -1)
                            return;

                        const uint UE3_EXEC_FLAG = 0x00000200;
                        uint newUelibFlags = uelibFlags | UE3_EXEC_FLAG;
                        byte[] newUelibFlagsBytes = BitConverter.GetBytes(newUelibFlags);
                        Array.Copy(newUelibFlagsBytes, 0, data, uelibFlagsOffset, 4);

                        package.Dispose();
                        package = null;

                        FileAttributes attributes = File.GetAttributes(tdGamePath);
                        bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdGamePath, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.WriteAllBytes(tdGamePath, data);

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdGamePath, attributes);
                        }
                    }
                    finally
                    {
                        package?.Dispose();
                    }
                });
            }
            catch (Exception)
            {
            }
        }

        private async Task RemoveKeybind(string command)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (!File.Exists(tdInputPath))
                {
                    return;
                }

                await Task.Run(() =>
                {
                    string[] lines = File.ReadAllLines(tdInputPath);
                    List<string> newLines = new List<string>();

                    foreach (string line in lines)
                    {
                        if (!line.Contains($"Command=\"{command}\""))
                        {
                            newLines.Add(line);
                        }
                    }

                    if (newLines.Count < lines.Length)
                    {
                        FileAttributes attributes = File.GetAttributes(tdInputPath);
                        bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdInputPath, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.WriteAllLines(tdInputPath, newLines.ToArray());

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdInputPath, attributes);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to remove keybind:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowRestartLevelKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Restart Level Keybind Information",
                "Restarts the level from where you started (this does not respect checkpoints reached, refer to the \"Load last checkpoint\" keybind for this).\n\n" +
                "In time trial and speedrun modes, this will reload the level back to the start.\n\nIn chapter mode, this will reload the level back to the checkpoint that was selected in the main menu.\n\n" +
                "In story mode, this will reload the level back to where you started when you pressed \"Continue Game\" (except when you complete a chapter, you'll instead respawn at checkpoint A of the next chapter).",
                DialogHelper.MessageType.Information);
        }

        private void ShowLoadLastCheckpointKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Load Last Checkpoint Keybind Information",
                "In earlier dev/review builds of Mirror's Edge there used to be a dedicated \"Load last checkpoint\" button in the pause menu that would reload Faith " +
                "to the last hard or soft checkpoint that was reached, however, this never made its way into the game's final release.\n\nAlthough the UI for this was removed, " +
                "the underlying function for this still exists in retail builds, and Mirror's Edge Tweaks can patch it to become executable via keybinds/console commands. " +
                "This is essentially a faster way to reset without having to force a death.",
                DialogHelper.MessageType.Information);
        }

        private void ShowRestartTimeTrialKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Restart Time Trial Keybind Information",
                "Restarts the time trial directly to the count down screen — this bypasses having to access it from the \"Restart Race\" button in the pause menu which can make resetting runs less tedious.\n\n" +
                "By default this command is not accessible, Mirror's Edge Tweaks performs a patch to make this function executable via keybinds/console commands.",
                DialogHelper.MessageType.Information);
        }

        private void ShowResetReactionTimeKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Reset Reaction Time Keybind Information",
                "Restores reaction time without needing to build up the required momentum. Toggling this keybind while reaction time is active will immediately disengage it.",
                DialogHelper.MessageType.Information);
        }

        private void ShowGodModeKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("God Mode Keybind Information",
                "Toggles invincibility, as well as additional commands for disabling kill volumes that god mode by itself misses.",
                DialogHelper.MessageType.Information);
        }

        private void ShowKillBotsKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Kill Bots Keybind Information",
                "Kills (deletes) all current bots and enemy helicopters.",
                DialogHelper.MessageType.Information);
        }

        private void ShowThirdPersonKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Third Person Keybind Information",
                "Cycles through different third person camera perspectives. The 6th press will return you to normal first person view.",
                DialogHelper.MessageType.Information);
        }

        private void ShowToggleHUDKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Toggle HUD Keybind Information",
                "Toggles the visibility of the crosshair and timer/checkpoint elements.",
                DialogHelper.MessageType.Information);
        }

        private void ShowFPSIndicatorKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("FPS Indicator Keybind Information",
                "Toggles an overlay displaying the frames per second and other rendering statistics.",
                DialogHelper.MessageType.Information);
        }

        private void ShowLevelStatsKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Level Stats Keybind Information",
                "Toggles an overlay displaying level streaming statistics, listing the levels for the current map. Red levels indicate the level is loaded and visible, " +
                "with the number of seconds next to the level name representing the time taken from load request to load finish. Green levels indicate unloaded levels.",
                DialogHelper.MessageType.Information);
        }

        private void ShowTriggersVolumesKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Triggers & Volumes Keybind Information",
                "Toggles the display of the bounding boxes of ALL triggers (checkpoints, level loads, other scripted gameplay events) and volumes " +
                "(areas that put Faith in a specific movement state, kill barriers, etc.).\n\nThis command also shows invisible blocking volumes the player can collide with, " +
                "making it a more performant alternative to using \"nxvis collision\" ('Show Collision' keybind).",
                DialogHelper.MessageType.Information);
        }

        private void ShowShowCollisionKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Show Collision Keybind Information",
                "Note: This command is very performance intensive and in some cases can crash the game.\n\n" +
                "Toggles the display of the PhysX collision data for the level, allowing you to see the wireframes and volumes for ALL collision objects with which rigid bodies interact.",
                DialogHelper.MessageType.Information);
        }

        private void ShowNoclipKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Noclip Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Toggles the use of noclip (flying with no collision). Keybinds for noclip movement speed can be set in the TweaksScriptsSettings file in the Binaries folder.",
                DialogHelper.MessageType.Information);
        }

        private void ShowSaveStateKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Save State Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Saves Faith's current position and state. If bots were manually spawned, their states will also be saved.",
                DialogHelper.MessageType.Information);
        }

        private void ShowLoadSavedStateKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Load Saved State Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Restores Faith to the saved state. This will also restore manually spawned bots.",
                DialogHelper.MessageType.Information);
        }

        private void ShowSaveTimerLocationKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Save Timer Location Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Saves the current player location as the checkpoint for the timer in the trainer HUD.",
                DialogHelper.MessageType.Information);
        }

        private void ShowDeleteViewedActorKeybindInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Delete Viewed Actor Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Deletes the bot/object currently looked at (some objects are connected to essential world geometry and are excluded).",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Macro Keybinds

        private string? CheckMacroKeybindConflict(string ue3Key, string? excludeSetting = null)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                return null;

            string settingsFilePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");
            if (!File.Exists(settingsFilePath))
                return null;

            var macroSettings = new Dictionary<string, string>
            {
                ["ScrollDownMacroKey"] = "Scroll Down Macro Key",
                ["ScrollUpMacroKey"] = "Scroll Up Macro Key"
            };

            foreach (var line in File.ReadAllLines(settingsFilePath))
            {
                foreach (var kvp in macroSettings)
                {
                    if (kvp.Key == excludeSetting)
                        continue;

                    if (line.StartsWith(kvp.Key))
                    {
                        var parts = line.Split(new[] { ' ' }, 2);
                        if (parts.Length > 1 && parts[1] == ue3Key)
                            return kvp.Value;
                    }
                }
            }

            return null;
        }

        private async Task<bool> UpdateMacroKeybind(string settingName, string ue3Key)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                return false;

            if (!string.IsNullOrEmpty(ue3Key))
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdInputPath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

                if (File.Exists(tdInputPath))
                {
                    string? conflictingCommand = null;

                    await Task.Run(() =>
                    {
                        string[] iniLines = File.ReadAllLines(tdInputPath);
                        bool inPlayerInput = false;

                        for (int i = 0; i < iniLines.Length; i++)
                        {
                            if (iniLines[i].Trim() == "[Engine.PlayerInput]")
                            {
                                inPlayerInput = true;
                                continue;
                            }

                            if (inPlayerInput && iniLines[i].Trim().StartsWith("[") && iniLines[i].Trim().EndsWith("]"))
                                break;

                            if (inPlayerInput && iniLines[i].Contains("Bindings=") && iniLines[i].Contains($"Name=\"{ue3Key}\""))
                            {
                                int cmdStart = iniLines[i].IndexOf("Command=\"") + 9;
                                int cmdEnd = iniLines[i].IndexOf("\"", cmdStart);
                                if (cmdStart > 8 && cmdEnd > cmdStart)
                                {
                                    conflictingCommand = iniLines[i].Substring(cmdStart, cmdEnd - cmdStart);
                                    return;
                                }
                            }
                        }
                    });

                    if (conflictingCommand != null)
                    {
                        await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                            $"The key '{ue3Key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                            "Please choose a different key or remove the existing binding in TdInput.ini first.",
                            DialogHelper.MessageType.Warning);
                        return false;
                    }
                }

                string? macroConflict = CheckMacroKeybindConflict(ue3Key, excludeSetting: settingName);
                if (macroConflict != null)
                {
                    await DialogHelper.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{ue3Key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Please choose a different key.",
                        DialogHelper.MessageType.Warning);
                    return false;
                }
            }

            try
            {
                string settingsFilePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");

                var lines = File.ReadAllLines(settingsFilePath).ToList();
                bool found = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith(settingName))
                    {
                        lines[i] = string.IsNullOrEmpty(ue3Key) ? settingName : $"{settingName} {ue3Key}";
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    lines.Add(string.IsNullOrEmpty(ue3Key) ? settingName : $"{settingName} {ue3Key}");
                }

                File.WriteAllLines(settingsFilePath, lines);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to update macro keybind: {ex.Message}", DialogHelper.MessageType.Error);
                return false;
            }

            return true;
        }

        private void LoadMacroKeybinds()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            try
            {
                _isLoadingKeybinds = true;

                string settingsFilePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");

                if (!File.Exists(settingsFilePath))
                {
                    ScrollDownMacroKeyTextBox.Text = string.Empty;
                    ScrollUpMacroKeyTextBox.Text = string.Empty;
                    return;
                }

                var lines = File.ReadAllLines(settingsFilePath);

                foreach (var line in lines)
                {
                    if (line.StartsWith("ScrollDownMacroKey"))
                    {
                        var parts = line.Split(new[] { ' ' }, 2);
                        ScrollDownMacroKeyTextBox.Text = parts.Length > 1 ? parts[1] : string.Empty;
                    }
                    else if (line.StartsWith("ScrollUpMacroKey"))
                    {
                        var parts = line.Split(new[] { ' ' }, 2);
                        ScrollUpMacroKeyTextBox.Text = parts.Length > 1 ? parts[1] : string.Empty;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _isLoadingKeybinds = false;
            }
        }

        private void ShowScrollDownMacroKeyInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Scroll Down Macro Key Information",
                "Set the keybind that will macro the action that is assigned to 'Scroll Down' in the game's control settings menu.\n\n" +
                "Note: This setting requires the Tweaks Scripts package to be installed. Macros are available while Softimer is active.",
                DialogHelper.MessageType.Information);
        }

        private void ShowScrollUpMacroKeyInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Scroll Up Macro Key Information",
                "Set the keybind that will macro the action that is assigned to 'Scroll Up' in the game's control settings menu.\n\n" +
                "Note: This setting requires the Tweaks Scripts package to be installed. Macros are available while Softimer is active.",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Initialisation Settings

        private bool _isLoadingInitSettings = false;

        private void LoadIntroVideoSetting()
        {
            try
            {
                _isLoadingInitSettings = true;

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                    return;

                var lines = File.ReadAllLines(tdEnginePath);
                bool inFullScreenMovieSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inFullScreenMovieSection = trimmedLine == "[FullScreenMovie]";
                        continue;
                    }

                    if (inFullScreenMovieSection)
                    {
                        if (trimmedLine.StartsWith(";StartupMovies="))
                        {
                            IntroVideoComboBox.SelectedIndex = 1;
                            return;
                        }
                        else if (trimmedLine.StartsWith("StartupMovies="))
                        {
                            IntroVideoComboBox.SelectedIndex = 0;
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load intro video setting: {ex.Message}");
            }
            finally
            {
                _isLoadingInitSettings = false;
            }
        }

        private async void IntroVideoComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (IntroVideoComboBox.SelectedIndex < 0 || _isLoadingInitSettings)
                return;

            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                {
                    DialogHelper.ShowMessage("Error", "TdEngine.ini not found. Please launch Mirror's Edge at least once to create the configuration file.", DialogHelper.MessageType.Error);
                    return;
                }

                bool enableIntroVideo = IntroVideoComboBox.SelectedIndex == 0;

                await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(tdEnginePath))
                        {
                            throw new FileNotFoundException($"TdEngine.ini not found at: {tdEnginePath}");
                        }

                        var lines = File.ReadAllLines(tdEnginePath);
                        bool inFullScreenMovieSection = false;
                        bool modified = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string trimmedLine = lines[i].Trim();

                            if (trimmedLine.StartsWith("["))
                            {
                                inFullScreenMovieSection = trimmedLine == "[FullScreenMovie]";
                                continue;
                            }

                            if (inFullScreenMovieSection)
                            {
                                if (trimmedLine.StartsWith(";StartupMovies=") || trimmedLine.StartsWith("StartupMovies="))
                                {
                                    if (enableIntroVideo)
                                    {
                                        lines[i] = "StartupMovies=StartupMovie";
                                    }
                                    else
                                    {
                                        if (!lines[i].TrimStart().StartsWith(";"))
                                        {
                                            int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                            string indent = lines[i].Substring(0, indentLength);
                                            lines[i] = indent + ";StartupMovies=StartupMovie";
                                        }
                                    }
                                    modified = true;
                                    break;
                                }
                            }
                        }

                        if (modified)
                        {
                            FileAttributes attributes = File.GetAttributes(tdEnginePath);
                            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                File.SetAttributes(tdEnginePath, attributes & ~FileAttributes.ReadOnly);

                            File.WriteAllLines(tdEnginePath, lines);
                            File.SetAttributes(tdEnginePath, File.GetAttributes(tdEnginePath) | FileAttributes.ReadOnly);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        throw;
                    }
                    catch (IOException ex)
                    {
                        throw new IOException($"Failed to access TdEngine.ini: {ex.Message}", ex);
                    }
                });

                string status = enableIntroVideo ? "enabled" : "disabled";
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to modify intro video setting:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowIntroVideoInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Intro Video Information",
                "Controls whether the intro video plays when launching the game. Disabling this setting saves 14 seconds.",
                DialogHelper.MessageType.Information);
        }

        private void LoadMainMenuDelaySetting()
        {
            try
            {
                _isLoadingInitSettings = true;

                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                string defaultUIPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "Config", "DefaultUI.ini");

                if (!File.Exists(defaultUIPath))
                    return;

                var lines = File.ReadAllLines(defaultUIPath);
                bool inTdUISceneStartSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inTdUISceneStartSection = trimmedLine == "[TdGame.TdUIScene_Start]";
                        continue;
                    }

                    if (inTdUISceneStartSection)
                    {
                        if (trimmedLine.StartsWith("TimeTillStartButton="))
                        {
                            string value = trimmedLine.Substring("TimeTillStartButton=".Length);
                            if (value == "0")
                            {
                                MainMenuDelayComboBox.SelectedIndex = 1;
                            }
                            else if (value == "4")
                            {
                                MainMenuDelayComboBox.SelectedIndex = 0;
                            }
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load main menu delay setting: {ex.Message}");
            }
            finally
            {
                _isLoadingInitSettings = false;
            }
        }

        private async void MainMenuDelayComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (MainMenuDelayComboBox.SelectedIndex < 0 || _isLoadingInitSettings || string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            try
            {
                string defaultUIPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "Config", "DefaultUI.ini");

                if (!File.Exists(defaultUIPath))
                {
                    DialogHelper.ShowMessage("Error", "DefaultUI.ini not found in the game directory.", DialogHelper.MessageType.Error);
                    return;
                }

                bool enableDelay = MainMenuDelayComboBox.SelectedIndex == 0;

                if (!enableDelay)
                {
                    bool isPatched = _unlockedConfigsViewModel.UnlockedConfigsStatus == "Patched";
                    if (!isPatched)
                    {
                        DialogHelper.ShowMessage("Warning", "The config modification patch in the 'Game Tweaks' section is not applied. " +
                            "Please apply the patch in order for your game to launch with the disabled main menu delay.", DialogHelper.MessageType.Warning);
                    }
                }

                await Task.Run(() =>
                {
                    var lines = File.ReadAllLines(defaultUIPath);
                    bool inTdUISceneStartSection = false;
                    bool modified = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmedLine = lines[i].Trim();

                        if (trimmedLine.StartsWith("["))
                        {
                            inTdUISceneStartSection = trimmedLine == "[TdGame.TdUIScene_Start]";
                            continue;
                        }

                        if (inTdUISceneStartSection)
                        {
                            if (trimmedLine.StartsWith("TimeTillStartButton="))
                            {
                                string newValue = enableDelay ? "4" : "0";

                                int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                string indent = lines[i].Substring(0, indentLength);
                                lines[i] = indent + "TimeTillStartButton=" + newValue;

                                modified = true;
                                break;
                            }
                        }
                    }

                    if (modified)
                    {
                        File.WriteAllLines(defaultUIPath, lines);
                    }
                });

                string status = enableDelay ? "enabled" : "disabled";
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to modify main menu delay setting:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowMainMenuDelayInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Main Menu Delay Information",
                "When launching the game, you have to wait 4 seconds at the title screen before you can pass input to proceed to the main menu. " +
                "Disabling this delay allows any input to be pressed immediately, getting you to the main menu faster.",
                DialogHelper.MessageType.Information);
        }

        private void LoadTimeTrialCountdownSetting()
        {
            try
            {
                _isLoadingInitSettings = true;

                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                string defaultGamePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "Config", "DefaultGame.ini");

                if (!File.Exists(defaultGamePath))
                    return;

                var lines = File.ReadAllLines(defaultGamePath);
                bool inTdSPTimeTrialGameSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inTdSPTimeTrialGameSection = trimmedLine == "[TdGame.TdSPTimeTrialGame]";
                        continue;
                    }

                    if (inTdSPTimeTrialGameSection)
                    {
                        if (trimmedLine.StartsWith("RaceCountDownTime="))
                        {
                            string value = trimmedLine.Substring("RaceCountDownTime=".Length);
                            switch (value)
                            {
                                case "3":
                                    TimeTrialCountdownComboBox.SelectedIndex = 0; // 4 seconds (default)
                                    break;
                                case "2":
                                    TimeTrialCountdownComboBox.SelectedIndex = 1; // 3 seconds
                                    break;
                                case "1":
                                    TimeTrialCountdownComboBox.SelectedIndex = 2; // 2 seconds
                                    break;
                                case "0":
                                    TimeTrialCountdownComboBox.SelectedIndex = 3; // 1 second
                                    break;
                            }
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load time trial countdown setting: {ex.Message}");
            }
            finally
            {
                _isLoadingInitSettings = false;
            }
        }

        private async void TimeTrialCountdownComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TimeTrialCountdownComboBox.SelectedIndex < 0 || _isLoadingInitSettings || string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            try
            {
                string defaultGamePath = Path.Combine(_config.GameDirectoryPath, "TdGame", "Config", "DefaultGame.ini");

                if (!File.Exists(defaultGamePath))
                {
                    DialogHelper.ShowMessage("Error", "DefaultGame.ini not found in the game directory.", DialogHelper.MessageType.Error);
                    return;
                }

                string newValue = TimeTrialCountdownComboBox.SelectedIndex switch
                {
                    0 => "3",
                    1 => "2",
                    2 => "1",
                    3 => "0",
                    _ => "3"
                };

                if (TimeTrialCountdownComboBox.SelectedIndex != 0)
                {
                    bool isPatched = _unlockedConfigsViewModel.UnlockedConfigsStatus == "Patched";
                    if (!isPatched)
                    {
                        DialogHelper.ShowMessage("Warning", "The config modification patch in the 'Game Tweaks' section is not applied. " +
                            "Please apply the patch in order for your game to launch with the custom time trial countdown.", DialogHelper.MessageType.Warning);
                    }
                }

                await Task.Run(() =>
                {
                    var lines = File.ReadAllLines(defaultGamePath);
                    bool inTdSPTimeTrialGameSection = false;
                    bool modified = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmedLine = lines[i].Trim();

                        if (trimmedLine.StartsWith("["))
                        {
                            inTdSPTimeTrialGameSection = trimmedLine == "[TdGame.TdSPTimeTrialGame]";
                            continue;
                        }

                        if (inTdSPTimeTrialGameSection)
                        {
                            if (trimmedLine.StartsWith("RaceCountDownTime="))
                            {
                                int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                string indent = lines[i].Substring(0, indentLength);
                                lines[i] = indent + "RaceCountDownTime=" + newValue;

                                modified = true;
                                break;
                            }
                        }
                    }

                    if (modified)
                    {
                        File.WriteAllLines(defaultGamePath, lines);
                    }
                });
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to modify time trial countdown setting:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowTimeTrialCountdownInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Time Trial Countdown Information",
                "Controls the countdown timer duration before the start of a time trial. " +
                "If Softimer is active, the countdown timer will revert to the default value of 4 seconds regardless of the setting selected here.",
                DialogHelper.MessageType.Information);
        }

        private void LoadSkipOnlineCheckSetting()
        {
            try
            {
                _isLoadingInitSettings = true;

                if (_config.TdGamePackagePath == null || !File.Exists(_config.TdGamePackagePath))
                    return;

                var tdState = TdGamePatcher.DetectState(_config.TdGamePackagePath);
                SkipOnlineCheckComboBox.SelectedIndex = tdState.OnlineSkipApplied ? 1 : 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load skip online check setting: {ex.Message}");
            }
            finally
            {
                _isLoadingInitSettings = false;
            }
        }

        private async void SkipOnlineCheckComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SkipOnlineCheckComboBox.SelectedIndex < 0 || _isLoadingInitSettings)
                return;

            if (_config.TdGamePackagePath == null || !File.Exists(_config.TdGamePackagePath))
                return;

            try
            {
                bool enableOnlineSkip = SkipOnlineCheckComboBox.SelectedIndex == 1;
                bool enableSens = FovAgnosticSensCheckBox.IsChecked == true;
                bool enableClip = CompensatedClipCheckBox.IsChecked == true;
                string tdGamePath = _config.TdGamePackagePath;

                await Task.Run(() =>
                {
                    TdGamePatcher.Reconcile(tdGamePath, enableSens, enableClip, enableOnlineSkip);
                });
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply skip online check patch: {ex.Message}", DialogHelper.MessageType.Error);
            }
        }

        private void ShowSkipOnlineCheckInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Skip Online Check Information",
                "Skips the dead EA online login attempt for Time Trials and Speedruns. " +
                "The game normally tries to connect to EA servers that no longer exist, " +
                "causing a delay of three intermediate connection check UI scenes before reaching the offline mode. " +
                "With this enabled, the game goes straight to the offline mode.",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Community Mods

        private bool _isLoadingCinematicFaith = false;

        private async void CinematicFaithComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoadingCinematicFaith)
                return;

            if (CinematicFaithComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            string selection = selectedItem.Content.ToString()!;

            if (selection == "Enabled")
            {
                string tdGamePath = Path.Combine(_config.GameDirectoryPath!, "TdGame", "CookedPC", "TdGame.u");
                if (File.Exists(tdGamePath))
                {
                    string tdGameVersion = TdGameVersionDetector.DetectTdGameVersion(tdGamePath);

                    if (tdGameVersion == "Original" || tdGameVersion == "Time Trials Timer Fix (by Nulaft)")
                    {
                        DialogHelper.ShowMessage("Warning",
                            "The Cinematic Faith Model requires a TdGame Fix variant to be installed.\n\n" +
                            "Your current TdGame version is: '" + tdGameVersion + "'\n\n" +
                            "Please install 'TdGame Fix (by Keku)' or 'TdGame Fix + Time Trials Timer Fix' from the Game Tweaks section. Parts of Faith's model will render incorrectly until the fix is applied.",
                            DialogHelper.MessageType.Warning);
                    }
                }
            }

            try
            {
                this.IsEnabled = false;

                string downloadUrl;
                if (selection == "Disabled")
                {
                    downloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/FaithModelOriginal.zip";
                }
                else
                {
                    downloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/FaithModelCinematic.zip";
                }

                await DownloadAndExtractCinematicFaithFiles(downloadUrl);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to apply Cinematic Faith Model setting: {ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                this.IsEnabled = true;
            }
        }

        private async Task DownloadAndExtractCinematicFaithFiles(string downloadUrl)
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                throw new InvalidOperationException("Game directory path is not set.");

            string tempZipPath = Path.Combine(Path.GetTempPath(), "CinematicFaith_temp.zip");

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);

                    UpdateStatus("Downloading Cinematic Faith Model files...");
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Visibility = Visibility.Visible;

                    using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        long? totalBytes = response.Content.Headers.ContentLength;

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int bytesRead;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (totalBytes.HasValue)
                                {
                                    double progress = (double)totalRead / totalBytes.Value * 100;
                                    DownloadProgressBar.Value = progress;
                                    UpdateStatus($"Downloading Cinematic Faith Model files... {progress:F0}%");
                                }
                            }
                        }
                    }
                }

                UpdateStatus("Extracting Cinematic Faith Model files...");
                DownloadProgressBar.IsIndeterminate = true;

                string extractPath = _config.GameDirectoryPath;
                await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, extractPath, overwriteFiles: true));

                UpdateStatus("Ready.");
            }
            finally
            {
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                DownloadProgressBar.Value = 0;
                DownloadProgressBar.IsIndeterminate = false;

                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
        }

        private void LoadCinematicFaithSetting()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            try
            {
                string playerModelPath = Path.Combine(_config.GameDirectoryPath, "TdGame", "CookedPC", "Characters", "CH_TKY_Crim_Fixer.upk");

                if (!File.Exists(playerModelPath))
                    return;

                _isLoadingCinematicFaith = true;

                long fileSize = new FileInfo(playerModelPath).Length;

                if (fileSize == 15063782)
                {
                    CinematicFaithComboBox.SelectedIndex = 1;
                }
                else if (fileSize == 8155273)
                {
                    CinematicFaithComboBox.SelectedIndex = 0;
                }
            }
            catch
            {
            }
            finally
            {
                _isLoadingCinematicFaith = false;
            }
        }

        private void ShowCinematicFaithInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Cinematic Faith Model Information",
                "Cinematic Faith (by Keku) is a mod that swaps the default third person model to a much higher quality version that is only otherwise seen once in the game's final sequence. " +
                "Additionally, this mod fixes the shader issues on the arms in first person, making the armband render as intended.\n\nNote: Requires a TdGame Fix variant to be installed.",
                DialogHelper.MessageType.Information);
        }

        #endregion

        #region Audio Settings

        private bool _isLoadingLanguage = false;

        private void LoadGameLanguageSetting()
        {
            try
            {
                _isLoadingLanguage = true;

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                    return;

                var lines = File.ReadAllLines(tdEnginePath);

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("Language="))
                    {
                        string value = trimmedLine.Substring("Language=".Length).Trim();

                        int index = value.ToLower() switch
                        {
                            "cze" => 0,  // Čeština (CZE)
                            "deu" => 1,  // Deutsch (DEU)
                            "int" => 2,  // English (INT)
                            "esn" => 3,  // Español (ESN)
                            "fra" => 4,  // Français (FRA)
                            "ita" => 5,  // Italiano (ITA)
                            "hun" => 6,  // Magyar (HUN)
                            "pol" => 7,  // Polski (POL)
                            "por" => 8,  // Português (POR)
                            "rus" => 9,  // Русский (RUS)
                            "kor" => 10, // 한국어 (KOR)
                            "cht" => 11, // 台灣繁體中文 (CHT)
                            "jpn" => 12, // 日本語 (JPN)
                            "chs" => 13, // 简体中文 (CHS)
                            _ => -1
                        };

                        if (index >= 0)
                        {
                            GameLanguageComboBox.SelectedIndex = index;
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load game language setting: {ex.Message}");
            }
            finally
            {
                _isLoadingLanguage = false;
            }
        }

        private async void GameLanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (GameLanguageComboBox.SelectedIndex < 0 || _isLoadingLanguage || string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            if (GameLanguageComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
                return;

            string language = selectedItem.Content.ToString() ?? "";

            var languageConfig = GetLanguageConfig(language);
            if (languageConfig == null)
                return;

            this.IsEnabled = false;

            try
            {
                string exePath = Path.Combine(_config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (File.Exists(exePath))
                {
                    var exeFileInfo = new FileInfo(exePath);
                    if (exeFileInfo.Length == SteamMirrorsEdgeExeSize) // Steam version
                    {
                        DialogHelper.ShowMessage("Warning",
                            $"You're currently using the Steam version of Mirror's Edge, which does not support language changes made outside the Steam client. " +
                            $"Each time the game is launched via Steam, the language will automatically revert to the setting configured in your Steam client. " +
                            $"If you want the language changes made with Mirror's Edge Tweaks to remain, you will need to either:\n\n" +
                            $"1. Launch Mirror's Edge with one of the Launch Game buttons at the top of the window\n\n" +
                            $"2. Launch the Mirror's Edge executable directly (found here: \"{exePath}\"), or\n\n" +
                            $"3. Add the aforementioned executable as a Non-Steam game.",
                            DialogHelper.MessageType.Warning);
                    }
                }

                UpdateRegistryValue(@"SOFTWARE\WOW6432Node\EA Games\Mirror's Edge", "Language", languageConfig.RegistryLanguage);
                UpdateRegistryValue(@"SOFTWARE\WOW6432Node\EA Games\Mirror's Edge", "Locale", languageConfig.Locale);

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                {
                    DialogHelper.ShowMessage("Error",
                        $"Cannot switch language, 'TdEngine.ini' file is missing from \"{tdEnginePath}\".\n\n" +
                        "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(tdEnginePath))
                        {
                            throw new FileNotFoundException($"TdEngine.ini not found at: {tdEnginePath}");
                        }

                        var lines = File.ReadAllLines(tdEnginePath);
                        bool modified = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].TrimStart().StartsWith("Language="))
                            {
                                int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                string indent = lines[i].Substring(0, indentLength);
                                lines[i] = indent + "Language=" + languageConfig.TdEngineLanguage;
                                modified = true;
                                break;
                            }
                        }

                        if (!modified)
                        {
                            DialogHelper.ShowMessage("Error", "TdEngine.ini file is corrupted.", DialogHelper.MessageType.Error);
                            return;
                        }

                        FileAttributes attributes = File.GetAttributes(tdEnginePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            File.SetAttributes(tdEnginePath, attributes & ~FileAttributes.ReadOnly);

                        File.WriteAllLines(tdEnginePath, lines);
                        File.SetAttributes(tdEnginePath, File.GetAttributes(tdEnginePath) | FileAttributes.ReadOnly);
                    }
                    catch (FileNotFoundException)
                    {
                        throw;
                    }
                    catch (IOException ex)
                    {
                        throw new IOException($"Failed to access TdEngine.ini: {ex.Message}", ex);
                    }
                });

                await DownloadAndExtractLanguageFiles(languageConfig.DownloadUrl);

                DialogHelper.ShowMessage("Success", $"Game language has been changed to {language}.", DialogHelper.MessageType.Success);
            }
            catch (System.Security.SecurityException)
            {
                DialogHelper.ShowMessage("Administrator Access Required",
                    "Changing the game language requires administrator privileges to modify the Windows Registry.\n\n" +
                    "To switch languages, please:\n\n" +
                    "1. Close Mirror's Edge Tweaks\n" +
                    "2. Right-click on MirrorsEdgeTweaks.exe\n" +
                    "3. Select 'Run as administrator'\n" +
                    "4. Try changing the language again",
                    DialogHelper.MessageType.Error);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to switch language:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                this.IsEnabled = true;
            }
        }

        private class LanguageConfig
        {
            public string DownloadUrl { get; set; } = "";
            public string RegistryLanguage { get; set; } = "";
            public string Locale { get; set; } = "";
            public string TdEngineLanguage { get; set; } = "";
        }

        private LanguageConfig? GetLanguageConfig(string language)
        {
            return language switch
            {
                "Čeština (CZE)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/CZE.zip",
                    RegistryLanguage = "Czech",
                    Locale = "cs",
                    TdEngineLanguage = "cze"
                },
                "Deutsch (DEU)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/DEU.zip",
                    RegistryLanguage = "German",
                    Locale = "de_DE",
                    TdEngineLanguage = "deu"
                },
                "English (INT)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/INT.zip",
                    RegistryLanguage = "English",
                    Locale = "en_UK",
                    TdEngineLanguage = "int"
                },
                "Español (ESN)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/ESN.zip",
                    RegistryLanguage = "Spanish",
                    Locale = "es_ES",
                    TdEngineLanguage = "esn"
                },
                "Français (FRA)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/FRA.zip",
                    RegistryLanguage = "French",
                    Locale = "fr_FR",
                    TdEngineLanguage = "fra"
                },
                "Italiano (ITA)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/ITA.zip",
                    RegistryLanguage = "Italian",
                    Locale = "it_IT",
                    TdEngineLanguage = "ita"
                },
                "Magyar (HUN)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/HUN.zip",
                    RegistryLanguage = "Hungarian",
                    Locale = "hu_HU",
                    TdEngineLanguage = "hun"
                },
                "Polski (POL)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/POL.zip",
                    RegistryLanguage = "Polish",
                    Locale = "pl_PL",
                    TdEngineLanguage = "pol"
                },
                "Português (POR)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/POR.zip",
                    RegistryLanguage = "Portuguese Brazil",
                    Locale = "pt_PT",
                    TdEngineLanguage = "por"
                },
                "Русский (RUS)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/RUS.zip",
                    RegistryLanguage = "Russian",
                    Locale = "ru_RU",
                    TdEngineLanguage = "rus"
                },
                "한국어 (KOR)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/KOR.zip",
                    RegistryLanguage = "Korean",
                    Locale = "ko_KR",
                    TdEngineLanguage = "kor"
                },
                "台灣繁體中文 (CHT)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/CHT.zip",
                    RegistryLanguage = "Traditional Chinese Taiwan",
                    Locale = "zh-TW",
                    TdEngineLanguage = "cht"
                },
                "日本語 (JPN)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/JPN.zip",
                    RegistryLanguage = "Japanese",
                    Locale = "ja_JP",
                    TdEngineLanguage = "jpn"
                },
                "简体中文 (CHS)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/CHS.zip",
                    RegistryLanguage = "Simplified Chinese",
                    Locale = "zh_CN",
                    TdEngineLanguage = "chs"
                },
                _ => null
            };
        }

        private void UpdateRegistryValue(string keyPath, string valueName, string newValue)
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, true))
            {
                if (key == null)
                {
                    using (var createdKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath))
                    {
                        createdKey?.SetValue(valueName, newValue, Microsoft.Win32.RegistryValueKind.String);
                    }
                }
                else
                {
                    key.SetValue(valueName, newValue, Microsoft.Win32.RegistryValueKind.String);
                }
            }
        }

        private async Task DownloadAndExtractLanguageFiles(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                string tempZipPath = Path.Combine(Path.GetTempPath(), $"MELanguage_{Guid.NewGuid()}.zip");
                string extractPath = _config.GameDirectoryPath;

                using (var client = new System.Net.Http.HttpClient())
                {
                    using (var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes != -1;

                        await using (var contentStream = await response.Content.ReadAsStreamAsync())
                        await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var totalRead = 0L;
                            var buffer = new byte[8192];
                            var isMoreToRead = true;

                            await Dispatcher.InvokeAsync(() =>
                            {
                                DownloadProgressBar.IsIndeterminate = false;
                                DownloadProgressBar.Value = 0;
                                DownloadProgressBar.Visibility = Visibility.Visible;
                                StatusTextBlock.Text = "Downloading language files...";
                            });

                            do
                            {
                                var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                                if (read == 0)
                                {
                                    isMoreToRead = false;
                                }
                                else
                                {
                                    await fileStream.WriteAsync(buffer, 0, read);

                                    totalRead += read;

                                    if (canReportProgress)
                                    {
                                        var progressPercentage = (double)totalRead / totalBytes * 100;
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            DownloadProgressBar.Value = progressPercentage;
                                            StatusTextBlock.Text = $"Downloading language files... {progressPercentage:F0}%";
                                        });
                                    }
                                }
                            }
                            while (isMoreToRead);
                        }
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    StatusTextBlock.Text = "Extracting language files...";
                });

                await Task.Run(() =>
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, extractPath, true);
                });

                File.Delete(tempZipPath);

                await ReapplyHighResUIFixIfNeeded(showDialogs: false);

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Ready.";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Ready.";
                });
                DialogHelper.ShowMessage("Error", $"Failed to download or extract language files:\n\n{ex.Message}", DialogHelper.MessageType.Error);
                throw;
            }
        }

        private async Task ReapplyHighResUIFixIfNeeded(bool? wasUIScalingActiveOverride = null, bool showDialogs = true)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                bool wasUIScalingActive = wasUIScalingActiveOverride ?? _uiScalingService.IsUIScalingActive(_config.GameDirectoryPath);

                ResolutionHelper.Resolution? selectedResolution = null;
                if (ResolutionComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                    selectedItem.Tag is ResolutionHelper.Resolution comboResolution)
                {
                    selectedResolution = comboResolution;
                }
                else
                {
                    selectedResolution = GetCurrentResolutionFromConfig();
                }

                if (selectedResolution == null || !_uiScalingService.ShouldOfferUIScaling(selectedResolution.Width))
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    DownloadProgressBar.Visibility = Visibility.Visible;
                    StatusTextBlock.Text = wasUIScalingActive ? "Reapplying high-res UI fix..." : "Resetting UI scaling...";
                });

                await Task.Run(async () =>
                {
                    if (wasUIScalingActive)
                    {
                        await _uiScalingService.ApplyUIScalingAsync(
                            selectedResolution.Width,
                            selectedResolution.Height,
                            _config.GameDirectoryPath,
                            null,
                            showDialogs);
                    }
                    else
                    {
                        await _uiScalingService.RollbackUIScalingToDefaultsAsync(
                            selectedResolution.Width,
                            selectedResolution.Height,
                            _config.GameDirectoryPath,
                            null,
                            showDialogs);
                    }
                });

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateHighResFixStatus(selectedResolution.Width, wasUIScalingActive);
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadProgressBar.IsIndeterminate = false;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to reapply high res UI fix: {ex.Message}");
            }
        }

        private void ShowGameLanguageInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Game Language Information",
                "Allows you to change to any of the game's 14 supported languages.\n\n" +
                "Note: Requires administrator privileges to modify registry values.\n\n" +
                "The following languages support only UI and subtitles: Czech, Hungarian, Portuguese Brazil, Korean, Traditional Chinese Taiwan, and Simplified Chinese.",
                DialogHelper.MessageType.Information);
        }

        private void LoadAudioBackendSetting()
        {
            try
            {
                _isLoadingLanguage = true;

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                    return;

                var lines = File.ReadAllLines(tdEnginePath);
                bool inALAudioSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inALAudioSection = trimmedLine == "[ALAudio.ALAudioDevice]";
                        continue;
                    }

                    if (inALAudioSection)
                    {
                        if (trimmedLine.StartsWith("DeviceName="))
                        {
                            string value = trimmedLine.Substring("DeviceName=".Length).Trim();

                            if (value == "Generic Hardware")
                            {
                                AudioBackendComboBox.SelectedIndex = 0;
                            }
                            else if (value == "OpenAL Soft")
                            {
                                bool hrtfInstalled = !string.IsNullOrEmpty(_config.GameDirectoryPath)
                                    && File.Exists(Path.Combine(_config.GameDirectoryPath, "Binaries", "soft_oal.dll"));
                                AudioBackendComboBox.SelectedIndex = hrtfInstalled ? 2 : 1;
                            }
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load audio backend setting: {ex.Message}");
            }
            finally
            {
                _isLoadingLanguage = false;
            }
        }

        private async void AudioBackendComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (AudioBackendComboBox.SelectedIndex < 0 || _isLoadingLanguage || string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            this.IsEnabled = false;

            try
            {
                int selectedIndex = AudioBackendComboBox.SelectedIndex;
                string downloadUrl;
                int maxChannels;
                string deviceName;

                switch (selectedIndex)
                {
                    case 0:
                        downloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/OpenAL.zip";
                        maxChannels = 32;
                        deviceName = "Generic Hardware";
                        break;
                    case 1:
                        downloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/OpenALSoft.zip";
                        maxChannels = 256;
                        deviceName = "OpenAL Soft";
                        break;
                    case 2:
                        downloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/OpenALSoftHRTF.zip";
                        maxChannels = 256;
                        deviceName = "OpenAL Soft";
                        break;
                    default:
                        return;
                }

                await DownloadAndExtractAudioBackendFiles(downloadUrl);

                if (selectedIndex != 2)
                    CleanupHrtfFiles();

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                {
                    DialogHelper.ShowMessage("Error",
                        $"Cannot change audio backend, 'TdEngine.ini' file is missing from \"{tdEnginePath}\".\n\n" +
                        "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                        DialogHelper.MessageType.Error);
                    return;
                }

                await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(tdEnginePath))
                        {
                            throw new FileNotFoundException($"TdEngine.ini not found at: {tdEnginePath}");
                        }

                        var lines = File.ReadAllLines(tdEnginePath);
                        bool inALAudioSection = false;
                        bool modifiedMaxChannels = false;
                        bool modifiedDeviceName = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string trimmedLine = lines[i].Trim();

                            if (trimmedLine.StartsWith("["))
                            {
                                inALAudioSection = trimmedLine == "[ALAudio.ALAudioDevice]";
                                continue;
                            }

                            if (inALAudioSection)
                            {
                                if (trimmedLine.StartsWith("MaxChannels="))
                                {
                                    int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                    string indent = lines[i].Substring(0, indentLength);
                                    lines[i] = indent + "MaxChannels=" + maxChannels;
                                    modifiedMaxChannels = true;
                                }
                                else if (trimmedLine.StartsWith("DeviceName="))
                                {
                                    int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                    string indent = lines[i].Substring(0, indentLength);
                                    lines[i] = indent + "DeviceName=" + deviceName;
                                    modifiedDeviceName = true;
                                }

                                if (modifiedMaxChannels && modifiedDeviceName)
                                    break;
                            }
                        }

                        if (!modifiedMaxChannels || !modifiedDeviceName)
                        {
                            throw new Exception("Failed to find MaxChannels or DeviceName in [ALAudio.ALAudioDevice] section of TdEngine.ini");
                        }

                        FileAttributes attributes = File.GetAttributes(tdEnginePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            File.SetAttributes(tdEnginePath, attributes & ~FileAttributes.ReadOnly);

                        File.WriteAllLines(tdEnginePath, lines);
                        File.SetAttributes(tdEnginePath, File.GetAttributes(tdEnginePath) | FileAttributes.ReadOnly);
                    }
                    catch (FileNotFoundException)
                    {
                        throw;
                    }
                    catch (IOException ex)
                    {
                        throw new IOException($"Failed to access TdEngine.ini: {ex.Message}", ex);
                    }
                });

                string[] backendNames = { "OpenAL (default)", "OpenAL Soft (modern)", "OpenAL Soft (HRTF)" };
                DialogHelper.ShowMessage("Success", $"Audio backend has been changed to {backendNames[selectedIndex]}.", DialogHelper.MessageType.Success);
            }
            catch (Exception ex)
            {
                DialogHelper.ShowMessage("Error", $"Failed to change audio backend:\n\n{ex.Message}", DialogHelper.MessageType.Error);
            }
            finally
            {
                this.IsEnabled = true;
            }
        }

        private async Task DownloadAndExtractAudioBackendFiles(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                    return;

                string tempZipPath = Path.Combine(Path.GetTempPath(), $"MEAudioBackend_{Guid.NewGuid()}.zip");
                string extractPath = _config.GameDirectoryPath;

                using (var client = new System.Net.Http.HttpClient())
                {
                    using (var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes != -1;

                        await using (var contentStream = await response.Content.ReadAsStreamAsync())
                        await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var totalRead = 0L;
                            var buffer = new byte[8192];
                            var isMoreToRead = true;

                            await Dispatcher.InvokeAsync(() =>
                            {
                                DownloadProgressBar.IsIndeterminate = false;
                                DownloadProgressBar.Value = 0;
                                DownloadProgressBar.Visibility = Visibility.Visible;
                                StatusTextBlock.Text = "Downloading audio backend files...";
                            });

                            do
                            {
                                var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                                if (read == 0)
                                {
                                    isMoreToRead = false;
                                }
                                else
                                {
                                    await fileStream.WriteAsync(buffer, 0, read);

                                    totalRead += read;

                                    if (canReportProgress)
                                    {
                                        var progressPercentage = (double)totalRead / totalBytes * 100;
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            DownloadProgressBar.Value = progressPercentage;
                                            StatusTextBlock.Text = $"Downloading audio backend files... {progressPercentage:F0}%";
                                        });
                                    }
                                }
                            }
                            while (isMoreToRead);
                        }
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    StatusTextBlock.Text = "Extracting audio backend files...";
                });

                await Task.Run(() =>
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, extractPath, true);
                });

                File.Delete(tempZipPath);

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Ready.";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Ready.";
                });
                DialogHelper.ShowMessage("Error", $"Failed to download or extract audio backend files:\n\n{ex.Message}", DialogHelper.MessageType.Error);
                throw;
            }
        }

        private void CleanupHrtfFiles()
        {
            if (string.IsNullOrEmpty(_config.GameDirectoryPath))
                return;

            string binDir = Path.Combine(_config.GameDirectoryPath, "Binaries");
            string[] hrtfFiles = { "soft_oal.dll", "alsoft.ini", "openal-hrtf-proxy.ini" };

            foreach (string file in hrtfFiles)
            {
                string path = Path.Combine(binDir, file);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private void ShowAudioBackendInfo_Click(object sender, RoutedEventArgs e)
        {
            DialogHelper.ShowMessage("Audio Backend Information",
                "The default OpenAL implementation in Mirror's Edge has sampling issues where the initial attack/transients of footstep sounds, " +
                "hand placements, etc. are lost due to the audio fading in. Upgrading to OpenAL Soft is highly recommended and fixes these issues in Mirror's Edge as well " +
                "as providing a noticeable boost in audio clarity.\n\nBoth OpenAL Soft options also raise the simultaneous " +
                "audio source limit from 32 to 256. The default limit of 32 causes sounds to be abruptly cut during busy scenes " +
                "with lots of gunfire, foley and ambient sources.\n\n" +
                "The HRTF option provides realistic 3D spatial audio through stereo headphones, " +
                "and utilises a special proxy that intercepts the engine's audio stream and splits the signal path so that HRTF is only applied " +
                "to actual 3D world sounds. Without the proxy, standard OpenAL Soft HRTF colours everything — including music, " +
                "dialogue, and UI effects — which can sound unnatural. With the proxy, non-spatial audio bypasses HRTF entirely " +
                "and plays back cleanly, while world-space sounds keep their full HRTF spatialisation.",
                DialogHelper.MessageType.Information);
        }

        #endregion
    }

    public class TweaksScriptsUIVersionDialog : System.Windows.Controls.UserControl
    {
        public TweaksScriptsUIVersionDialog()
        {
            var border = new System.Windows.Controls.Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Background = System.Windows.Media.Brushes.White,
                Padding = new System.Windows.Thickness(20),
                MaxWidth = 500,
                MinWidth = 300
            };

            var stackPanel = new System.Windows.Controls.StackPanel();

            var titleText = new System.Windows.Controls.TextBlock
            {
                Text = "Select Version",
                FontSize = 18,
                FontWeight = System.Windows.FontWeights.Bold,
                Margin = new System.Windows.Thickness(0, 0, 0, 16)
            };

            var messageText = new System.Windows.Controls.TextBlock
            {
                Text = "Which version of Tweaks Scripts UI would you like to install?",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
                MaxWidth = 450
            };

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var regularButton = new System.Windows.Controls.Button
            {
                Content = "Regular",
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            regularButton.Click += (s, e) => MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(false, regularButton);

            var memmButton = new System.Windows.Controls.Button
            {
                Content = "MEMM-Compatible",
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            memmButton.Click += (s, e) => MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(true, memmButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MaterialDesignOutlinedButton")
            };
            cancelButton.Click += (s, e) => MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(null, cancelButton);

            buttonPanel.Children.Add(regularButton);
            buttonPanel.Children.Add(memmButton);
            buttonPanel.Children.Add(cancelButton);

            stackPanel.Children.Add(titleText);
            stackPanel.Children.Add(messageText);
            stackPanel.Children.Add(buttonPanel);

            border.Child = stackPanel;
            Content = border;
        }
    }
}
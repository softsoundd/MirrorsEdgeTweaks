using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace MirrorsEdgeTweaks.ViewModels
{
    // View model for the "Mods" section of the Game Tweaks tab (TdGame version, developer console,
    // Tweaks Scripts and Tweaks Scripts UI). Owns the section's informational dialog commands and the
    // developer-console install/uninstall commands. Per-feature status text lives in the shared
    // status view models.
    public partial class ModsViewModel : BusyViewModel
    {
        private const string ConsoleDownloadUrl = DownloadUrls.AssetBase + "MirrorsEdgeConsole.zip";

        private readonly IDialogService _dialogService;
        private readonly IPackageService _packageService;
        private readonly IFileService _fileService;
        private readonly IGameDataService _gameData;
        private readonly GameSession _session;
        private readonly TweaksScriptsViewModel _tweaksScripts;

        public ModsViewModel(
            IDialogService dialogService,
            IPackageService packageService,
            IFileService fileService,
            IGameDataService gameData,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress,
            TweaksScriptsViewModel tweaksScripts)
            : base(gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _packageService = packageService;
            _fileService = fileService;
            _gameData = gameData;
            _session = session;
            _tweaksScripts = tweaksScripts;
        }

        // ---- Tweaks Scripts status ----

        private bool IsTweaksScriptsInstalled()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                return false;
            }

            string scriptFilePath = Path.Combine(gameDir, "TdGame", "CookedPC", "MirrorsEdgeTweaksScripts.u");
            return _fileService.FileExists(scriptFilePath);
        }

        public void RefreshTweaksScriptsStatus()
        {
            if (_session.IsProcessingGameDirectory)
            {
                return;
            }

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
            {
                dispatcher.Invoke(() =>
                {
                    _tweaksScripts.TweaksScriptsStatus = "N/A";
                    _tweaksScripts.TweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;
                });
                UpdateTweaksScriptsDependencyUI(isTweaksScriptsInstalled: false);
                return;
            }

            bool tweaksScriptsInstalled = IsTweaksScriptsInstalled();
            dispatcher.Invoke(() =>
            {
                _tweaksScripts.TweaksScriptsStatus = tweaksScriptsInstalled ? "Installed" : "Not Installed";
                _tweaksScripts.TweaksScriptsStatusForeground = tweaksScriptsInstalled
                    ? System.Windows.Media.Brushes.Green
                    : System.Windows.Media.Brushes.Gray;
            });

            UpdateTweaksScriptsDependencyUI(tweaksScriptsInstalled);
        }

        private void UpdateTweaksScriptsDependencyUI(bool isTweaksScriptsInstalled)
        {
            void ApplyDependencyState()
            {
                bool hasGameDirectory = !string.IsNullOrEmpty(_session.Config.GameDirectoryPath);
                bool canInstallTweaksScriptsUi = isTweaksScriptsInstalled;

                _tweaksScripts.IsTweaksScriptsUIInstallEnabled = canInstallTweaksScriptsUi;
                _tweaksScripts.TweaksScriptsUIInstallTooltip = canInstallTweaksScriptsUi
                    ? "Install Tweaks Scripts UI."
                    : "Install Tweaks Scripts first to enable this installer.";
                _tweaksScripts.IsTweaksScriptsUIDependencyTextVisible = hasGameDirectory && !canInstallTweaksScriptsUi;
            }

            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                ApplyDependencyState();
            }
            else
            {
                dispatcher.Invoke(ApplyDependencyState);
            }
        }

        public void RefreshTweaksScriptsUIStatus()
        {
            if (_session.IsProcessingGameDirectory)
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
                    _tweaksScripts.TweaksScriptsUIStatus = "Installed (MEMM)";
                    _tweaksScripts.TweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Green;
                }
                else if (hasMainMenu && hasFrontEnd && hasSofTimer && !hasCustomRaces)
                {
                    _tweaksScripts.TweaksScriptsUIStatus = "Installed (regular)";
                    _tweaksScripts.TweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Green;
                }
                else if (hasMainMenu || hasFrontEnd || hasSofTimer || hasCustomRaces)
                {
                    _tweaksScripts.TweaksScriptsUIStatus = "Partially Installed";
                    _tweaksScripts.TweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Orange;
                }
                else
                {
                    _tweaksScripts.TweaksScriptsUIStatus = "Not Installed";
                    _tweaksScripts.TweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Gray;
                }
            }
            catch (Exception)
            {
                _tweaksScripts.TweaksScriptsUIStatus = "N/A";
                _tweaksScripts.TweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Gray;
            }
            finally
            {
                UpdateTweaksScriptsDependencyUI(IsTweaksScriptsInstalled());
            }
        }

        [RelayCommand]
        private async Task InstallTweaksScriptsAsync()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Warning);
                return;
            }

            const string downloadUrl = DownloadUrls.AssetBase + "MirrorsEdgeTweaksScripts.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), "MirrorsEdgeTweaksScripts.zip");

            _gameStatus.IsGameTweaksEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.DownloadProgressValue = 0;
            _downloadProgress.IsDownloadProgressIndeterminate = true;
            _gameStatus.Status = "Downloading Tweaks Scripts...";

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            try
            {
                await Task.Run(async () =>
                {
                    using (var client = new HttpClient())
                    using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            if (totalBytes.HasValue)
                            {
                                dispatcher.Invoke(() => _downloadProgress.IsDownloadProgressIndeterminate = false);

                                var totalBytesRead = 0L;
                                var buffer = new byte[8192];
                                int bytesRead;
                                var report = CreateThrottledProgressReporter();
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    var progress = (int)((double)totalBytesRead / totalBytes.Value * 100);

                                    report(progress, $"Downloading Tweaks Scripts... {progress}%");
                                }
                            }
                            else
                            {
                                await stream.CopyToAsync(fileStream);
                            }
                        }
                    }

                    dispatcher.Invoke(() =>
                    {
                        _gameStatus.Status = "Extracting files...";
                        _downloadProgress.DownloadProgressValue = 100;
                    });

                    string binariesPath = Path.Combine(gameDir, "Binaries");
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

                    ZipFile.ExtractToDirectory(tempZipPath, gameDir, true);
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

                    string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                    if (SetCommandPatchHelper.IsDlcVersion(exePath))
                    {
                        dispatcher.Invoke(() => _gameStatus.Status = "Applying DLC set/setnopec fix...");
                    }

                    SetCommandPatchHelper.EnsurePatchedIfApplicable(exePath);
                });

                _gameStatus.Status = "Ready.";
                RefreshTweaksScriptsStatus();
                await _dialogService.ShowMessageAsync("Success",
                    "Tweaks Scripts successfully downloaded and installed.",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "An error occurred during installation.";
                await _dialogService.ShowMessageAsync("Error", $"An error occurred: {ex.Message}", DialogMessageType.Error);
            }
            finally
            {
                _gameStatus.IsGameTweaksEnabled = true;
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.DownloadProgressValue = 0;
                _downloadProgress.IsDownloadProgressIndeterminate = false;

                RefreshTweaksScriptsStatus();
            }
        }

        [RelayCommand]
        private async Task UninstallTweaksScriptsAsync()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Warning);
                return;
            }

            var result = await _dialogService.ShowConfirmationAsync("Confirm Uninstall", "This will delete the Tweaks Scripts files from your game directory. Are you sure?");
            if (!result)
            {
                return;
            }

            try
            {
                int filesDeleted = await Task.Run(() =>
                {
                    string cookedPcPath = Path.Combine(gameDir, "TdGame", "CookedPC");
                    string binariesPath = Path.Combine(gameDir, "Binaries");

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

                _gameStatus.Status = "Ready.";
                RefreshTweaksScriptsStatus();

                if (filesDeleted > 0)
                {
                    await _dialogService.ShowMessageAsync("Success", $"Successfully uninstalled Tweaks Scripts ({filesDeleted} files removed).", DialogMessageType.Success);
                }
                else
                {
                    await _dialogService.ShowMessageAsync("Not Found", "No Tweaks Scripts files were found to uninstall.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Error", $"An error occurred during uninstallation: {ex.Message}", DialogMessageType.Error);
                _gameStatus.Status = "Error during uninstallation.";
            }
            finally
            {
                RefreshTweaksScriptsStatus();
            }
        }

        [RelayCommand]
        private async Task InstallTweaksScriptsUIAsync()
        {
            if (!IsTweaksScriptsInstalled())
            {
                _dialogService.ShowMessage(
                    "Dependency Missing",
                    "Install 'Tweaks Scripts' first (MirrorsEdgeTweaksScripts.u), then install Tweaks Scripts UI.",
                    DialogMessageType.Warning);
                UpdateTweaksScriptsDependencyUI(isTweaksScriptsInstalled: false);
                return;
            }

            var versionChoice = await ShowTweaksScriptsUIVersionDialogAsync();
            if (versionChoice == null)
            {
                return;
            }

            bool isMEMM = versionChoice.Value;
            string downloadUrl = isMEMM
                ? DownloadUrls.AssetBase + "MirrorsEdgeTweaksScriptsUI_MEMM_compatible.zip"
                : DownloadUrls.AssetBase + "MirrorsEdgeTweaksScriptsUI.zip";

            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdGamePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame");
                string publishedPath = Path.Combine(tdGamePath, "Published");

                if (!Directory.Exists(publishedPath))
                {
                    if (Directory.Exists(tdGamePath))
                    {
                        Directory.CreateDirectory(publishedPath);
                    }
                    else
                    {
                        _dialogService.ShowMessage("Error",
                            $"Published folder not found at: {publishedPath}\n\n" +
                            "Please ensure you have launched Mirror's Edge at least once.",
                            DialogMessageType.Error);
                        return;
                    }
                }

                ShowProgress("Downloading Tweaks Scripts UI...", false);

                try
                {
                    string tempZipPath = Path.Combine(Path.GetTempPath(), "TweaksScriptsUI.zip");

                    using (var client = new HttpClient())
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
                            var report = CreateThrottledProgressReporter();

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (totalBytes.HasValue)
                                {
                                    double progress = (double)totalRead / totalBytes.Value * 100;
                                    report(progress, $"Downloading Tweaks Scripts UI... {progress:F0}%");
                                }
                            }
                        }
                    }

                    _gameStatus.Status = "Extracting Tweaks Scripts UI...";
                    _downloadProgress.IsDownloadProgressIndeterminate = true;

                    await Task.Run(() =>
                    {
                        ZipFile.ExtractToDirectory(tempZipPath, publishedPath, true);
                        File.Delete(tempZipPath);
                    });

                    HideProgress();

                    string versionName = isMEMM ? "MEMM-Compatible" : "Regular";
                    _dialogService.ShowMessage("Success",
                        $"Tweaks Scripts UI ({versionName}) installed successfully!",
                        DialogMessageType.Success);

                    RefreshTweaksScriptsUIStatus();
                }
                finally
                {
                    HideProgress();
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                _dialogService.ShowMessage("Error",
                    $"Failed to install Tweaks Scripts UI: {ex.Message}",
                    DialogMessageType.Error);
            }
        }

        private async Task<bool?> ShowTweaksScriptsUIVersionDialogAsync()
        {
            var dialog = new TweaksScriptsUIVersionDialog();
            var result = await _dialogService.ShowDialogAsync(dialog);
            return result as bool?;
        }

        [RelayCommand]
        private async Task UninstallTweaksScriptsUI()
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

                int deletedCount = await Task.Run(() =>
                {
                    int count = 0;
                    foreach (string file in filesToDelete)
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                            count++;
                        }
                    }
                    return count;
                });

                if (deletedCount == 0)
                {
                    _dialogService.ShowMessage("Information",
                        "No Tweaks Scripts UI files found to uninstall.",
                        DialogMessageType.Information);
                }
                else
                {
                    _dialogService.ShowMessage("Success",
                        $"Tweaks Scripts UI uninstalled. ({deletedCount} file(s) removed)",
                        DialogMessageType.Success);
                }

                RefreshTweaksScriptsUIStatus();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error",
                    $"Failed to uninstall Tweaks Scripts UI: {ex.Message}",
                    DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task InstallConsoleAsync()
        {
            var config = _session.Config;
            var offsets = _session.Offsets;

            if (string.IsNullOrEmpty(config.GameDirectoryPath) || string.IsNullOrEmpty(config.TdEngineIniPath) || string.IsNullOrEmpty(config.TdInputIniPath) || string.IsNullOrEmpty(config.EnginePackagePath))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }
            if (offsets.ConsoleHeightOffset == -1)
            {
                _dialogService.ShowMessage("Patch Error", "Could not find the necessary location to patch in Engine.u. Cannot proceed.", DialogMessageType.Error);
                return;
            }

            string gameDir = config.GameDirectoryPath;
            string tdEngineIni = config.TdEngineIniPath;
            string tdInputIni = config.TdInputIniPath;
            string enginePackagePath = config.EnginePackagePath;
            long consoleHeightOffset = offsets.ConsoleHeightOffset;

            _gameStatus.IsGameTweaksEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.DownloadProgressValue = 0;

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            try
            {
                await Task.Run(async () =>
                {
                    dispatcher.Invoke(() => _gameStatus.Status = "Installing console...");
                    ConfigFileHelper.ModifyIniFile(tdEngineIni, "Engine.Engine", "ConsoleClassName", "MirrorsEdgeConsole.MirrorsEdgeConsole");
                    ConfigFileHelper.ModifyIniFile(tdInputIni, "Engine.Console", "TypeKey", "Tab");

                    string tempZipPath = Path.Combine(Path.GetTempPath(), "MirrorsEdgeConsole.zip");

                    using (var client = new HttpClient())
                    using (var response = await client.GetAsync(ConsoleDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            if (!totalBytes.HasValue)
                            {
                                dispatcher.Invoke(() => _downloadProgress.IsDownloadProgressIndeterminate = true);
                                await stream.CopyToAsync(fileStream);
                            }
                            else
                            {
                                dispatcher.Invoke(() => _downloadProgress.IsDownloadProgressIndeterminate = false);
                                var totalBytesRead = 0L;
                                var buffer = new byte[8192];
                                int bytesRead;
                                var report = CreateThrottledProgressReporter();
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    var progress = (int)((double)totalBytesRead / totalBytes.Value * 100);

                                    report(progress, $"Downloading console... {progress}%");
                                }
                            }
                        }
                    }

                    dispatcher.Invoke(() =>
                    {
                        _gameStatus.Status = "Extracting console files...";
                        _downloadProgress.IsDownloadProgressIndeterminate = true;
                    });

                    ZipFile.ExtractToDirectory(tempZipPath, gameDir, true);
                    File.Delete(tempZipPath);

                    dispatcher.Invoke(() => _gameStatus.Status = "Patching Engine.u...");

                    _packageService.DisposePackage(_session.Package);
                    _session.Package = null;
                    using (var stream = new FileStream(enginePackagePath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        stream.Position = consoleHeightOffset;
                        byte[] newValue = BitConverter.GetBytes(0.4f);
                        stream.Write(newValue, 0, newValue.Length);
                    }

                    string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                    if (SetCommandPatchHelper.IsDlcVersion(exePath))
                    {
                        dispatcher.Invoke(() => _gameStatus.Status = "Applying DLC set/setnopec fix...");
                    }

                    SetCommandPatchHelper.EnsurePatchedIfApplicable(exePath);
                });

                _gameStatus.Status = "Ready.";
                await _dialogService.ShowMessageAsync("Success",
                    "Developer console successfully installed. Use the Tilde (~) key to open the console.\n\n" +
                    "Please note that Unreal Engine 3 supports only the US keyboard layout. If you do not wish to use the US layout, the following layouts will interpret these keys as Tilde:\n\n" +
                    "• UK: @ (At sign)\n\n" +
                    "• German: ö\n\n" +
                    "• French: ù (% key)\n\n" +
                    "• Spanish: ñ\n\n" +
                    "• Italian: \\ (backslash)",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Installation Failed", $"An error occurred during installation: {ex.Message}", DialogMessageType.Error);
                _gameStatus.Status = "Console installation failed.";
            }
            finally
            {
                _downloadProgress.IsDownloadProgressVisible = false;

                await _gameData.ReloadPackagesAsync();
            }
        }

        [RelayCommand]
        private async Task UninstallConsoleAsync()
        {
            var config = _session.Config;
            var offsets = _session.Offsets;

            if (string.IsNullOrEmpty(config.GameDirectoryPath) || string.IsNullOrEmpty(config.TdEngineIniPath) || string.IsNullOrEmpty(config.TdInputIniPath) || string.IsNullOrEmpty(config.EnginePackagePath))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }
            if (offsets.ConsoleHeightOffset == -1)
            {
                _dialogService.ShowMessage("Patch Error", "Could not find the necessary location to patch in Engine.u. Cannot proceed.", DialogMessageType.Error);
                return;
            }

            var result = await _dialogService.ShowConfirmationAsync("Confirm Uninstall", "This will revert all changes made by the console installation. Are you sure you want to continue?");
            if (!result)
            {
                return;
            }

            string gameDir = config.GameDirectoryPath;
            string tdEngineIni = config.TdEngineIniPath;
            string tdInputIni = config.TdInputIniPath;
            string enginePackagePath = config.EnginePackagePath;
            long consoleHeightOffset = offsets.ConsoleHeightOffset;

            _gameStatus.IsGameTweaksEnabled = false;
            _gameStatus.Status = "Uninstalling console...";

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            try
            {
                await Task.Run(() =>
                {
                    dispatcher.Invoke(() => _gameStatus.Status = "Reverting config files...");
                    ConfigFileHelper.ModifyIniFile(tdEngineIni, "Engine.Engine", "ConsoleClassName", "TdGame.TdConsole");
                    ConfigFileHelper.ModifyIniFile(tdInputIni, "Engine.Console", "TypeKey", "None");

                    dispatcher.Invoke(() => _gameStatus.Status = "Deleting console package...");
                    string consolePackagePath = Path.Combine(gameDir, "TdGame", "CookedPC", "MirrorsEdgeConsole.u");
                    if (_fileService.FileExists(consolePackagePath))
                    {
                        _fileService.DeleteFile(consolePackagePath);
                    }

                    dispatcher.Invoke(() => _gameStatus.Status = "Patching Engine.u...");
                    _packageService.DisposePackage(_session.Package);
                    _session.Package = null;
                    using (var stream = new FileStream(enginePackagePath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        stream.Position = consoleHeightOffset;
                        byte[] originalValue = BitConverter.GetBytes(0.75f);
                        stream.Write(originalValue, 0, originalValue.Length);
                    }
                });

                _gameStatus.Status = "Ready.";
                await _dialogService.ShowMessageAsync("Success", "Developer console uninstalled.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Uninstallation Failed", $"An error occurred during uninstallation: {ex.Message}", DialogMessageType.Error);
                _gameStatus.Status = "Console uninstallation failed.";
            }
            finally
            {
                await _gameData.ReloadPackagesAsync();
            }
        }

        [RelayCommand]
        private void ShowTdGameVersionInfo()
        {
            _dialogService.ShowMessage("TdGame Version Information",
                "Allows the selection of various TdGame versions.\n\n" +
                "• Original — Unmodified TdGame and persistent map files.\n\n" +
                "• TdGame Fix (by Keku) — Modified TdGame and persistent map files that allows loading custom skins, animations, sounds and other miscellaneous mods (Mirror's Edge Tweaks does not require this to be installed with the exception of the Cinematic Faith Model mod).\n\n" +
                "• Time Trials Timer Fix (by Nulaft) — Fixes the precision errors of the time trial timer and uses a realtime timer (timer is prepended with an \"R\" to indicate this). Useful for speedrunners.\n\n" +
                "• TdGame Fix + Time Trials Timer Fix — Both versions combined.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowConsoleInfo()
        {
            _dialogService.ShowMessage("Developer Console Information",
                "Install the native Unreal Engine 3 developer console to access debug commands and features.\n\n" +
                "The function responsible for handling user input to open the console was intentionally stripped by DICE. Mirror's Edge Tweaks can install a custom UnrealScript package " +
                "that extends the existing Console class, overriding the empty input function with the required code to restore full console functionality.\n\n" +
                "Please note that Unreal Engine 3 supports only the US keyboard layout. If you do not wish to use the US layout, the following layouts will interpret these keys as Tilde:\n\n" +
                "• UK: @ (At sign)\n\n" +
                "• German: ö\n\n" +
                "• French: ù (% key)\n\n" +
                "• Spanish: ñ\n\n" +
                "• Italian: \\ (backslash)",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowTweaksScriptsInfo()
        {
            _dialogService.ShowMessage("Tweaks Scripts Information",
                "A custom UnrealScript package that adds an assortment of additional gameplay features, including Softimer (native in-game timer for speedrunners), cheats and trainer functionality, save file editing, and more.\n\n" +
                "It is highly recommended to install the developer console to access the full range of features of Tweaks Scripts.\n\n" +
                "• Softimer — Activate with the console command \"exec speedrun\" (or deactivate with \"exec speedrunoff\"), or toggle it via the 'Tweaks Scripts UI' mod.\n\n" +
                "• Cheats & Trainer — Activate with the console command \"exec cheats\" (or deactivate with \"exec cheatsoff\"), or toggle it via the 'Tweaks Scripts UI' mod. While activated, enter \"listcheats\" to view all cheats.\n\n" +
                "• Trainer HUD — Activate with the console command \"exec trainerhud\" (or deactivate with \"exec trainerhudoff\"), or toggle it via the 'Tweaks Scripts UI' mod.\n\n" +
                "• Save File Editor — Edit save progress (also requires the 'Tweaks Scripts UI' mod to be installed).",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowTweaksScriptsUIInfo()
        {
            _dialogService.ShowMessage("Tweaks Scripts UI Information",
                "Provides an in-game UI for Tweaks Scripts features, accessible from the main menu.\n\n" +
                "• Regular: Standard version.\n\n" +
                "• MEMM-Compatible: Version compatible with the Mirror's Edge Map Manager.",
                DialogMessageType.Information);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class TdGameVersionViewModel : BusyViewModel
    {
        private readonly IDialogService _dialogService;
        private readonly IDecompressionService _decompressionService;
        private readonly IDownloadService _download;
        private readonly IFileService _fileService;
        private readonly IAssetUrlProvider _assetUrls;
        private readonly IUIScalingService _uiScaling;
        private readonly IGameDataService _gameData;
        private readonly IPackageService _packageService;
        private readonly InputSettingsViewModel _input;
        private readonly KeybindsViewModel _keybinds;
        private readonly GraphicsTweaksViewModel _graphics;

        private bool _isUpdatingComboBoxProgrammatically;

        protected override bool IsApplySuppressed => base.IsApplySuppressed || _isUpdatingComboBoxProgrammatically;

        [ObservableProperty] private int _selectedVersionIndex = -1;

        public TdGameVersionViewModel(
            IDialogService dialogService,
            IDecompressionService decompressionService,
            IDownloadService download,
            IFileService fileService,
            IAssetUrlProvider assetUrls,
            IUIScalingService uiScaling,
            IGameDataService gameData,
            IPackageService packageService,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress,
            InputSettingsViewModel input,
            KeybindsViewModel keybinds,
            GraphicsTweaksViewModel graphics)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _decompressionService = decompressionService;
            _download = download;
            _fileService = fileService;
            _assetUrls = assetUrls;
            _uiScaling = uiScaling;
            _gameData = gameData;
            _packageService = packageService;
            _input = input;
            _keybinds = keybinds;
            _graphics = graphics;
        }

        private void SetVersionIndexSilently(int index)
        {
            _isUpdatingComboBoxProgrammatically = true;
            try
            {
                SelectedVersionIndex = index;
            }
            finally
            {
                _isUpdatingComboBoxProgrammatically = false;
            }
        }

        public void DetectVersion()
        {
            var path = _session.Config.TdGamePackagePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetVersionIndexSilently(-1);
                return;
            }

            try
            {
                string detectedVersion = TdGameVersionDetector.DetectTdGameVersion(path);
                SetVersionIndexSilently(TdGameVersionCatalog.IndexOf(detectedVersion));
            }
            catch (Exception ex)
            {
                _gameStatus.Status = $"Error detecting TdGame version: {ex.Message}";
                SetVersionIndexSilently(-1);
            }
        }

        partial void OnSelectedVersionIndexChanged(int oldValue, int newValue) => _ = OnSelectedVersionChangedAsync(oldValue, newValue);

        private async Task OnSelectedVersionChangedAsync(int previousIndex, int value)
        {
            string? selectedVersionName = TdGameVersionCatalog.NameAt(value);
            if (_isUpdatingComboBoxProgrammatically || selectedVersionName == null)
            {
                return;
            }

            var result = await _dialogService.ShowConfirmationAsync("Confirm Download", $"This will download and replace your current 'TdGame.u' file.\n\nThis action cannot be undone. Do you want to continue?");

            if (!result)
            {
                SetVersionIndexSilently(previousIndex);
                return;
            }

            TdGameTouchpointSnapshot touchpointSnapshot = CaptureTdGameTouchpointSnapshot();

            _packageService.DisposePackage(_session.Package);
            _packageService.DisposePackage(_session.TdGamePackage);
            _session.Package = null;
            _session.TdGamePackage = null;

            _gameStatus.IsGameTweaksEnabled = false;

            await RunApplyAsync(() => DownloadAndExtractTdGameAsync(selectedVersionName, touchpointSnapshot));
        }

        private async Task DownloadAndExtractTdGameAsync(string selectedVersionName, TdGameTouchpointSnapshot touchpointSnapshot)
        {
            var config = _session.Config;
            if (string.IsNullOrEmpty(config.GameDirectoryPath))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Warning);
                DetectVersion();
                return;
            }

            string gameVersion = _gameStatus.GameVersion;
            await _assetUrls.EnsureLoadedAsync();
            string? downloadUrl = GameVersionHelper.GetDownloadUrl(gameVersion, selectedVersionName, _assetUrls);

            if (string.IsNullOrEmpty(downloadUrl))
            {
                _dialogService.ShowMessage("URL Error", "Could not determine the download URL for the selected game version and TdGame variant.", DialogMessageType.Error);
                DetectVersion();
                return;
            }

            _gameStatus.IsUiEnabled = false;
            _gameStatus.IsGameTweaksEnabled = false;

            bool installSucceeded = false;
            bool packagesLoadedAtEnd = false;
            TdGameTouchpointReapplyResult reapplyResult = new TdGameTouchpointReapplyResult();

            try
            {
                string tdGamePackagePath = Path.Combine(config.GameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");
                string extractDir = config.GameDirectoryPath;

                await RunDownloadAndExtractAsync(
                    _download,
                    _fileService,
                    downloadUrl,
                    extractDir,
                    $"'{selectedVersionName}'",
                    customExtract: (tempZipPath, dest) => Task.Run(() =>
                    {
                        ZipFile.ExtractToDirectory(tempZipPath, dest, true);
                        Post(() => _gameStatus.Status = "Decompressing new package...");
                        _decompressionService.RunDecompressor(tdGamePackagePath);
                    }));

                installSucceeded = true;
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "An error occurred during the download/extraction.";
                await _dialogService.ShowMessageAsync("Error", $"An error occurred: {ex.Message}", DialogMessageType.Error);
            }
            finally
            {
                try
                {
                    if (installSucceeded)
                    {
                        try
                        {
                            _gameStatus.Status = "Reloading packages...";
                            await Task.Delay(500);
                            await Task.Run(() => _gameData.LoadPackages());
                            packagesLoadedAtEnd = true;

                            _gameStatus.Status = "Reapplying TdGame-linked settings...";
                            reapplyResult = await ReapplyTdGameTouchpointSnapshotAsync(touchpointSnapshot);

                            await Task.Delay(250);
                            await Task.Run(() => _gameData.LoadPackages());
                            packagesLoadedAtEnd = true;

                            _gameStatus.Status = "Successfully installed.";
                            await _dialogService.ShowMessageAsync("Success", BuildTdGameInstallSuccessMessage(selectedVersionName, reapplyResult), DialogMessageType.Success);
                        }
                        catch (Exception ex)
                        {
                            _gameStatus.Status = "TdGame installed, but settings reapply hit an error.";
                            await _dialogService.ShowMessageAsync(
                                "Warning",
                                $"The TdGame version was installed successfully, but some TdGame-linked settings failed to be reapplied:\n\n{ex.Message}.",
                                DialogMessageType.Warning);
                        }
                    }

                    if (!packagesLoadedAtEnd)
                    {
                        await Task.Delay(500);
                        await Task.Run(() => _gameData.LoadPackages());
                    }

                    if (!installSucceeded)
                    {
                        DetectVersion();
                    }

                    if (installSucceeded && _session.Package != null && _session.TdGamePackage != null)
                    {
                        _gameStatus.Status = "Ready.";
                    }
                }
                finally
                {
                    _gameStatus.IsUiEnabled = true;
                }
            }
        }

        private TdGameTouchpointSnapshot CaptureTdGameTouchpointSnapshot()
        {
            var config = _session.Config;
            TdGameTouchpointSnapshot snapshot = new TdGameTouchpointSnapshot
            {
                UniformSensitivityTargetValue = _input.GetUniformSensitivityTargetValue(),
                GamepadButtonType = _input.GetGamepadButtonType(),
                HasLoadLastCheckpointKeybind = !string.IsNullOrWhiteSpace(_keybinds.LoadLastCheckpoint.DisplayKey),
                HasRestartTimeTrialKeybind = !string.IsNullOrWhiteSpace(_keybinds.RestartTimeTrial.DisplayKey)
            };

            if (config.TdGamePackagePath != null && File.Exists(config.TdGamePackagePath))
            {
                try
                {
                    var patchState = TdGamePatcher.DetectState(config.TdGamePackagePath);
                    snapshot.FovSnapshot.DynamicPatchesApplied = patchState.CoreApplied || patchState.SensApplied
                        || patchState.ClipApplied || patchState.OnlineSkipApplied;
                    snapshot.FovSnapshot.SensEnabled = patchState.SensApplied;
                    snapshot.FovSnapshot.ClipEnabled = patchState.ClipApplied;
                    snapshot.FovSnapshot.OnlineSkipEnabled = patchState.OnlineSkipApplied;
                }
                catch { }
            }
            if (float.TryParse(_graphics.NewFovValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float baseFov))
            {
                snapshot.FovSnapshot.BaseFov = baseFov;
            }

            if (!string.IsNullOrEmpty(config.GameDirectoryPath))
            {
                try
                {
                    snapshot.WasHighResFixActive = _uiScaling.IsUIScalingActive(config.GameDirectoryPath);
                }
                catch
                {
                    snapshot.WasHighResFixActive = false;
                }
            }

            return snapshot;
        }

        private async Task<TdGameTouchpointReapplyResult> ReapplyTdGameTouchpointSnapshotAsync(TdGameTouchpointSnapshot snapshot)
        {
            var config = _session.Config;
            TdGameTouchpointReapplyResult result = new TdGameTouchpointReapplyResult();

            if (string.IsNullOrEmpty(config.TdGamePackagePath) || !File.Exists(config.TdGamePackagePath))
            {
                result.FailedSettings.Add("TdGame.u was not found after installation");
                return result;
            }

            if (snapshot.FovSnapshot.HasValues)
            {
                try
                {
                    bool wroteFovValues = ReapplyTdGameFovSnapshot(config.TdGamePackagePath, snapshot.FovSnapshot);
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
                    await Task.Run(() => UniformSensitivityPatcher.Apply(config.TdGamePackagePath!, targetValue));
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
                    await Task.Run(() => GamepadButtonPatcher.ApplyControllerImagePathSwap(config.TdGamePackagePath!, buttonType));
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
                    await ExecFlagPatcher.AddExecFlag(config.GameDirectoryPath, "TdSPGame", "RestartFromLastCheckpoint");
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
                    await ExecFlagPatcher.AddExecFlag(config.GameDirectoryPath, "TdTimeTrialHUD", "TriggerRestartRaceblink");
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
                    await _graphics.ReapplyHighResUIFixIfNeededAsync(snapshot.WasHighResFixActive, showDialogs: false);
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
    }
}

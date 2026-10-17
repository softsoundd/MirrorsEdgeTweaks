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
        private readonly IGameDataService _gameData;
        private readonly IPackageService _packageService;
        private readonly ITweakSnapshotService _snapshots;

        private bool _isUpdatingComboBoxProgrammatically;

        protected override bool IsApplySuppressed => base.IsApplySuppressed || _isUpdatingComboBoxProgrammatically;

        [ObservableProperty] private int _selectedVersionIndex = -1;

        public TdGameVersionViewModel(
            IDialogService dialogService,
            IDecompressionService decompressionService,
            IDownloadService download,
            IFileService fileService,
            IAssetUrlProvider assetUrls,
            IGameDataService gameData,
            IPackageService packageService,
            ITweakSnapshotService snapshots,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _decompressionService = decompressionService;
            _download = download;
            _fileService = fileService;
            _assetUrls = assetUrls;
            _gameData = gameData;
            _packageService = packageService;
            _snapshots = snapshots;
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

            var result = await _dialogService.ShowConfirmationAsync(
                "Confirm Download",
                "This will download and replace your current TdGame.u file.\n\nThis cannot be undone. Do you want to continue?");

            if (!result)
            {
                SetVersionIndexSilently(previousIndex);
                return;
            }

            _packageService.DisposePackage(_session.Package);
            _packageService.DisposePackage(_session.TdGamePackage);
            _session.Package = null;
            _session.TdGamePackage = null;

            _gameStatus.IsGameTweaksEnabled = false;

            await RunApplyAsync(() => DownloadAndExtractTdGameAsync(selectedVersionName));
        }

        private async Task DownloadAndExtractTdGameAsync(string selectedVersionName)
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
                _dialogService.ShowMessage("URL Error",
                    "Could not determine the download URL for the selected game version and TdGame variant.",
                    DialogMessageType.Error);
                DetectVersion();
                return;
            }

            _gameStatus.IsUiEnabled = false;
            _gameStatus.IsGameTweaksEnabled = false;

            _gameStatus.Status = "Reading TdGame-linked settings...";
            TweakSnapshot snapshot = await Task.Run(() => _snapshots.Capture());

            bool installSucceeded = false;
            bool packagesLoadedAtEnd = false;
            TweakReapplyResult reapplyResult = new TweakReapplyResult();

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

                BackupRetentionService.PruneBackupForPath(tdGamePackagePath);

                installSucceeded = true;
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "An error occurred during the download/extraction.";
                await _dialogService.ShowMessageAsync("Error", $"Download failed:\n\n{ex.Message}", DialogMessageType.Error);
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
                            reapplyResult = await _snapshots.ReapplyAsync(snapshot, TweakScope.TdGame);

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
                                $"The TdGame version was installed, but some TdGame-linked settings could not be reapplied:\n\n{ex.Message}",
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

        private static string BuildTdGameInstallSuccessMessage(string selectedVersionName, TweakReapplyResult reapplyResult)
        {
            string message = $"Downloaded and installed '{selectedVersionName}' TdGame version.";

            if (reapplyResult.FailedSettings.Count > 0)
            {
                message += "\n\nSome TdGame-linked settings could not be restored automatically. " +
                           "Please review your settings in the UI and reapply if needed.";
            }

            return message;
        }
    }
}

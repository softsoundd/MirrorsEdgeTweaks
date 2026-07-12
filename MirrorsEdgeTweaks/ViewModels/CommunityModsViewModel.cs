using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace MirrorsEdgeTweaks.ViewModels
{
    // View model for the "Community Mods" section of the Other Tweaks tab. Currently hosts the
    // Cinematic Faith Model (by Keku) install/uninstall, which downloads and extracts the relevant
    // model package and detects the installed variant by file size.
    public partial class CommunityModsViewModel : BusyViewModel
    {
        private const string OriginalModelUrl = DownloadUrls.AssetBase + "FaithModelOriginal.zip";
        private const string CinematicModelUrl = DownloadUrls.AssetBase + "FaithModelCinematic.zip";

        private readonly IDialogService _dialogService;
        private readonly IDownloadService _download;
        private readonly GameSession _session;

        private bool _isLoading;

        [ObservableProperty] private int _cinematicFaithIndex = -1; // 0 = Disabled, 1 = Enabled

        public CommunityModsViewModel(
            IDialogService dialogService,
            IDownloadService download,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _download = download;
            _session = session;
        }

        partial void OnCinematicFaithIndexChanged(int oldValue, int newValue) => _ = OnCinematicFaithChangedAsync(oldValue, newValue);

        private async Task OnCinematicFaithChangedAsync(int previousIndex, int value)
        {
            if (_isLoading || value < 0)
                return;

            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
            {
                SetSilently(() => CinematicFaithIndex = previousIndex);
                return;
            }

            bool enabled = value == 1;

            if (enabled)
            {
                string tdGamePath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");
                if (File.Exists(tdGamePath))
                {
                    string tdGameVersion = TdGameVersionDetector.DetectTdGameVersion(tdGamePath);

                    if (tdGameVersion == "Original" || tdGameVersion == "Time Trials Timer Fix (by Nulaft)")
                    {
                        _dialogService.ShowMessage("Warning",
                            "The Cinematic Faith Model requires a TdGame Fix variant to be installed.\n\n" +
                            "Your current TdGame version is: '" + tdGameVersion + "'\n\n" +
                            "Please install 'TdGame Fix (by Keku)' or 'TdGame Fix + Time Trials Timer Fix' from the Game Tweaks section. Parts of Faith's model will render incorrectly until the fix is applied.",
                            DialogMessageType.Warning);
                    }
                }
            }

            try
            {
                _gameStatus.IsUiEnabled = false;

                string downloadUrl = enabled ? CinematicModelUrl : OriginalModelUrl;

                await DownloadAndExtractCinematicFaithFiles(downloadUrl);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply Cinematic Faith Model setting: {ex.Message}", DialogMessageType.Error);

                // The model swap did not complete; re-detect the installed variant from disk so
                // the combo reflects reality (falls back to the prior selection if undetectable).
                SetSilently(() => CinematicFaithIndex = previousIndex);
                RefreshCinematicFaith();
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
            }
        }

        private async Task DownloadAndExtractCinematicFaithFiles(string downloadUrl)
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                throw new InvalidOperationException("Game directory path is not set.");

            string tempZipPath = Path.Combine(Path.GetTempPath(), "CinematicFaith_temp.zip");

            try
            {
                _gameStatus.Status = "Downloading Cinematic Faith Model files...";
                _downloadProgress.IsDownloadProgressIndeterminate = false;
                _downloadProgress.IsDownloadProgressVisible = true;

                var report = CreateThrottledProgressReporter();
                await _download.DownloadToFileAsync(downloadUrl, tempZipPath, p =>
                {
                    if (p >= 0)
                        report(p, $"Downloading Cinematic Faith Model files... {p:F0}%");
                });

                _gameStatus.Status = "Extracting Cinematic Faith Model files...";
                _downloadProgress.IsDownloadProgressIndeterminate = true;

                string extractPath = _session.Config.GameDirectoryPath;
                await Task.Run(() => ZipFile.ExtractToDirectory(tempZipPath, extractPath, overwriteFiles: true));

                _gameStatus.Status = "Ready.";
            }
            finally
            {
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.DownloadProgressValue = 0;
                _downloadProgress.IsDownloadProgressIndeterminate = false;

                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
        }

        public void RefreshCinematicFaith()
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return;

            try
            {
                string playerModelPath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "CookedPC", "Characters", "CH_TKY_Crim_Fixer.upk");

                if (!File.Exists(playerModelPath))
                    return;

                long fileSize = new FileInfo(playerModelPath).Length;

                if (fileSize == 15063782)
                    SetSilently(() => CinematicFaithIndex = 1);
                else if (fileSize == 8155273)
                    SetSilently(() => CinematicFaithIndex = 0);
            }
            catch
            {
            }
        }

        private void SetSilently(Action action)
        {
            bool previous = _isLoading;
            _isLoading = true;
            try { action(); }
            finally { _isLoading = previous; }
        }

        [RelayCommand]
        private void ShowCinematicFaithInfo()
        {
            _dialogService.ShowMessage("Cinematic Faith Model Information",
                "Cinematic Faith (by Keku) is a mod that swaps the default third person model to a much higher quality version that is only otherwise seen once in the game's final sequence. " +
                "Additionally, this mod fixes the shader issues on the arms in first person, making the armband render as intended.\n\nNote: Requires a TdGame Fix variant to be installed.",
                DialogMessageType.Information);
        }
    }
}

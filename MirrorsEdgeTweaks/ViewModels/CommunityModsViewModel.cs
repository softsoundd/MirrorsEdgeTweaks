using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class CommunityModsViewModel : BusyViewModel
    {
        private readonly IDialogService _dialogService;
        private readonly IDownloadService _download;
        private readonly IFileService _fileService;
        private readonly IAssetUrlProvider _assetUrls;
        private bool _isLoading;

        protected override bool IsApplySuppressed => base.IsApplySuppressed || _isLoading;

        [ObservableProperty] private int _cinematicFaithIndex = -1;

        public CommunityModsViewModel(
            IDialogService dialogService,
            IDownloadService download,
            IFileService fileService,
            IAssetUrlProvider assetUrls,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _download = download;
            _fileService = fileService;
            _assetUrls = assetUrls;
        }

        partial void OnCinematicFaithIndexChanged(int oldValue, int newValue) => EnqueueApply(() => OnCinematicFaithChangedAsync(oldValue, newValue));

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
                            "Install 'TdGame Fix (by Keku)' or 'TdGame Fix + Time Trials Timer Fix' from Game Tweaks. Parts of Faith's model will render incorrectly until a fix variant is installed.",
                            DialogMessageType.Warning);
                    }
                }
            }

            try
            {
                _gameStatus.IsUiEnabled = false;

                await _assetUrls.EnsureLoadedAsync();
                string fileName = enabled ? "FaithModelCinematic.zip" : "FaithModelOriginal.zip";
                string downloadUrl = _assetUrls.For(fileName);

                await RunDownloadAndExtractAsync(
                    _download,
                    _fileService,
                    downloadUrl,
                    _session.Config.GameDirectoryPath,
                    "Cinematic Faith Model files");
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply Cinematic Faith Model setting: {ex.Message}", DialogMessageType.Error);

                SetSilently(() => CinematicFaithIndex = previousIndex);
                RefreshCinematicFaith();
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
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
                "Cinematic Faith (by Keku) swaps the default third-person model for a higher-quality version otherwise seen only in the game's final sequence. " +
                "It also fixes first-person arm shader issues so the armband renders as intended.\n\n" +
                "Note: Requires a TdGame Fix variant to be installed.",
                DialogMessageType.Information);
        }
    }
}

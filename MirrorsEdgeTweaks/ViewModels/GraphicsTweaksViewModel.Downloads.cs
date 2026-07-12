using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class GraphicsTweaksViewModel
    {
        partial void OnToneMapperIndexChanged(int value) => EnqueueApply(() => OnToneMapperChangedAsync(value));

        private async Task OnToneMapperChangedAsync(int index)
        {
            if (_isLoading || index < 0)
                return;

            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                SetSilently(() => ToneMapperIndex = -1);
                return;
            }

            string variantName = index == 1 ? "Faithful Luma" : "Original";
            string zipName = index == 1 ? "FaithfulLumaTonemap.zip" : "OriginalTonemap.zip";
            string downloadUrl = DownloadUrls.For(zipName);
            string tempZipPath = Path.Combine(Path.GetTempPath(), zipName);

            _gameStatus.IsUiEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.DownloadProgressValue = 0;
            _downloadProgress.IsDownloadProgressIndeterminate = true;
            _gameStatus.Status = $"Downloading {variantName} tone mapper...";

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            try
            {
                var report = CreateThrottledProgressReporter();
                bool determinateSet = false;
                await _download.DownloadToFileAsync(downloadUrl, tempZipPath, p =>
                {
                    if (p < 0)
                        return;
                    if (!determinateSet)
                    {
                        determinateSet = true;
                        Post(() => _downloadProgress.IsDownloadProgressIndeterminate = false);
                    }
                    report(p, $"Downloading {variantName} tone mapper... {p:F0}%");
                });

                dispatcher.Invoke(() =>
                {
                    _gameStatus.Status = "Extracting shader files...";
                    _downloadProgress.DownloadProgressValue = 100;
                    _downloadProgress.IsDownloadProgressIndeterminate = true;
                });

                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(tempZipPath, gameDir, true);
                    File.Delete(tempZipPath);
                });

                _gameStatus.Status = "Ready.";
                RefreshToneMapper();
                await _dialogService.ShowMessageAsync("Success",
                    $"{variantName} tone mapper successfully downloaded and installed.",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "An error occurred during installation.";
                await _dialogService.ShowMessageAsync("Error", $"An error occurred: {ex.Message}", DialogMessageType.Error);
                SetSilently(RefreshToneMapper);
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.DownloadProgressValue = 0;
                _downloadProgress.IsDownloadProgressIndeterminate = false;
            }
        }

        public void RefreshToneMapper()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            int detected = 0;
            if (!string.IsNullOrEmpty(gameDir))
            {
                string shaderPath = Path.Combine(gameDir, "Engine", "Shaders", "TdToneMappingPixelShader.usf");
                try
                {
                    if (File.Exists(shaderPath) &&
                        File.ReadAllText(shaderPath).Contains("ApplyWhiteNeutralityCorrection"))
                        detected = 1;
                }
                catch { }
            }

            SetSilently(() => ToneMapperIndex = detected);
        }
    }
}

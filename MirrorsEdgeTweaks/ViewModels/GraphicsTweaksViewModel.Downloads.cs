using MirrorsEdgeTweaks.Services;
using System.IO;

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

            _gameStatus.IsUiEnabled = false;

            try
            {
                await _assetUrls.EnsureLoadedAsync();
                string downloadUrl = _assetUrls.For(zipName);

                await RunDownloadAndExtractAsync(
                    _download,
                    _fileService,
                    downloadUrl,
                    gameDir,
                    $"{variantName} tone mapper");

                RefreshToneMapper();
                await _dialogService.ShowMessageAsync("Success",
                    $"{variantName} tone mapper downloaded and installed.",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "Installation failed.";
                await _dialogService.ShowMessageAsync("Error", $"Installation failed:\n\n{ex.Message}", DialogMessageType.Error);
                SetSilently(RefreshToneMapper);
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
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

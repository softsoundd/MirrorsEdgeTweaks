using CommunityToolkit.Mvvm.ComponentModel;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class GraphicsTweaksViewModel
    {
        [ObservableProperty] private int _reversedDepthIndex = -1;

        partial void OnReversedDepthIndexChanged(int oldValue, int newValue) =>
            EnqueueApply(() => OnReversedDepthChangedAsync(oldValue, newValue));

        private async Task OnReversedDepthChangedAsync(int previousIndex, int index)
        {
            if (_isLoading || index < 0)
                return;

            string? exePath = GetGameExePath();
            if (exePath == null)
            {
                _dialogService.ShowMessage("Error", "MirrorsEdge.exe was not found. Please select a valid game directory first.", DialogMessageType.Error);
                SetSilently(() => ReversedDepthIndex = previousIndex);
                return;
            }

            bool enable = index == 0;
            try
            {
                bool ran = await RunBusyAsync(enable ? "Applying reversed depth..." : "Removing reversed depth...", () =>
                {
                    if (enable) ReversedDepthPatcher.Apply(exePath);
                    else ReversedDepthPatcher.Remove(exePath);
                });
                if (!ran)
                {
                    await RefreshReversedDepthAsync();
                    return;
                }
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                await _dialogService.ShowMessageAsync("EA App License Required", ooaEx.Message, DialogMessageType.Warning);
                await RefreshReversedDepthAsync();
                return;
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Error",
                    $"Failed to {(enable ? "apply" : "remove")} reversed depth:\n\n{ex.Message}", DialogMessageType.Error);
                await RefreshReversedDepthAsync();
                return;
            }

            string binariesDirectory = Path.GetDirectoryName(exePath)!;
            try
            {
                if (enable)
                    await SetUpFloatDepthAsync(binariesDirectory);
                else
                    await OfferDxvkRemovalAsync(binariesDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _dialogService.ShowMessageAsync("Error",
                    $"Reversed depth {(enable ? "applied" : "removed")}, but Tweaks could not update DXVK:\n\n{ex.Message}",
                    DialogMessageType.Error);
            }
        }

        // Reversed depth only gains precision when DXVK stores the depth buffer as float.
        private async Task SetUpFloatDepthAsync(string binariesDirectory)
        {
            switch (await Task.Run(() => DxvkHelper.GetState(binariesDirectory)))
            {
                case DxvkState.Missing:
                    await OfferDxvkInstallAsync(binariesDirectory);
                    return;
                case DxvkState.OtherWrapper:
                    await _dialogService.ShowMessageAsync("Reversed Depth",
                        "Reversed depth applied, but it requires DXVK. " +
                        "There is another Direct3D9 wrapper in the game's Binaries folder, so Tweaks cannot install DXVK there.\n\n" +
                        "Please remove the other wrapper and try again.",
                        DialogMessageType.Warning);
                    return;
                case DxvkState.Outdated:
                    await _dialogService.ShowMessageAsync("Reversed Depth",
                        "Reversed depth applied, but it requires DXVK 2.0 or later, and the DXVK in the game's Binaries folder is older.\n\n" +
                        "Please update DXVK, or remove it and try again so Tweaks can install the latest version.",
                        DialogMessageType.Warning);
                    return;
            }

            if (DxvkHelper.IsFloatDepthEnabled(binariesDirectory))
            {
                await _dialogService.ShowMessageAsync("Reversed Depth",
                    "Reversed depth applied.",
                    DialogMessageType.Success);
                return;
            }

            bool turnOn = await _dialogService.ShowConfirmationAsync("Reversed Depth",
                "Reversed depth applied. DXVK is installed, but its d3d9.useD32forD24 option is off, " +
                "so the depth buffer is still 24-bit and Z-fighting will not improve.\n\n" +
                "Turn d3d9.useD32forD24 on in Binaries\\dxvk.conf?");
            if (turnOn)
                await EnableFloatDepthAsync(binariesDirectory, "d3d9.useD32forD24 is now on in dxvk.conf.");
        }

        private async Task OfferDxvkInstallAsync(string binariesDirectory)
        {
            bool install = await _dialogService.ShowConfirmationAsync("Reversed Depth",
                "Reversed depth applied, but it requires DXVK to reduce Z-fighting.\n\n" +
                "Download and install DXVK into Binaries?\n\n" +
                "Note: DXVK runs the game through Vulkan, so it needs a graphics driver with Vulkan 1.3 support.");
            if (!install)
                return;

            _gameStatus.IsUiEnabled = false;
            try
            {
                await _assetUrls.EnsureLoadedAsync();
                string url = _assetUrls.For(DxvkHelper.AssetFileName);
                await RunDownloadAndExtractAsync(_download, _fileService, url, binariesDirectory, "DXVK",
                    customExtract: (zip, dest) => Task.Run(() => DxvkHelper.ExtractAssetDll(zip, dest)));
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "Installation failed.";
                await _dialogService.ShowMessageAsync("Error", $"DXVK installation failed:\n\n{ex.Message}", DialogMessageType.Error);
                return;
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
            }

            await EnableFloatDepthAsync(binariesDirectory, "DXVK installed, with d3d9.useD32forD24 on in Binaries\\dxvk.conf.");
        }

        private async Task EnableFloatDepthAsync(string binariesDirectory, string successMessage)
        {
            DxvkHelper.EnableFloatDepth(binariesDirectory);
            await _dialogService.ShowMessageAsync("Reversed Depth", successMessage, DialogMessageType.Success);
        }

        private async Task OfferDxvkRemovalAsync(string binariesDirectory)
        {
            if (await Task.Run(() => DxvkHelper.GetState(binariesDirectory)) != DxvkState.Supported)
                return;

            bool remove = await _dialogService.ShowConfirmationAsync("Reversed Depth",
                "Reversed depth removed. Also remove DXVK from the game's Binaries folder?");
            if (!remove)
                return;

            DxvkHelper.RemoveDxvk(binariesDirectory);
            await _dialogService.ShowMessageAsync("Reversed Depth", "DXVK removed.", DialogMessageType.Success);
        }

        public void RefreshReversedDepth()
        {
            string? exePath = GetGameExePath();
            ReversedDepthState state = exePath == null ? ReversedDepthState.Unknown : ReversedDepthPatcher.GetState(exePath);
            SetSilently(() => ReversedDepthIndex = ToIndex(state));
        }

        public async Task RefreshReversedDepthAsync()
        {
            string? exePath = GetGameExePath();
            ReversedDepthState state = exePath == null
                ? ReversedDepthState.Unknown
                : await Task.Run(() => ReversedDepthPatcher.GetState(exePath));
            SetSilently(() => ReversedDepthIndex = ToIndex(state));
        }

        private static int ToIndex(ReversedDepthState state) => state switch
        {
            ReversedDepthState.Patched => 0,
            ReversedDepthState.Unpatched => 1,
            _ => -1,
        };
    }
}

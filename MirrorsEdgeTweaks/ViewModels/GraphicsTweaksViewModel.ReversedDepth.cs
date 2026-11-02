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
                await _dialogService.ShowErrorAsync($"Could not {(enable ? "apply" : "remove")} reversed depth.", ex);
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
                await _dialogService.ShowErrorAsync(
                    $"Reversed depth {(enable ? "applied" : "removed")}, but Tweaks could not update DXVK.", ex);
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
                        "Another mod's d3d9.dll (for example ReShade) is in the game's Binaries folder, so Tweaks cannot install DXVK there.\n\n" +
                        "Please remove that d3d9.dll and try again.",
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
                "Reversed depth applied, but a DXVK setting it needs is turned off, so Z-fighting will not improve yet.\n\n" +
                "Turn this setting on?");
            if (turnOn)
                await EnableFloatDepthAsync(binariesDirectory, "DXVK is now set up for reversed depth.");
        }

        private async Task OfferDxvkInstallAsync(string binariesDirectory)
        {
            bool install = await _dialogService.ShowConfirmationAsync("Reversed Depth",
                "Reversed depth applied, but it requires DXVK to reduce Z-fighting.\n\n" +
                "Download and install DXVK into the game's Binaries folder?\n\n" +
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
                await _dialogService.ShowErrorAsync("Could not install DXVK.", ex);
                return;
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
            }

            await EnableFloatDepthAsync(binariesDirectory, "DXVK installed and set up for reversed depth.");
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

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
                await _dialogService.ShowErrorAsync(enable
                    ? "Reversed depth applied, but Tweaks could not set up the renderer for it."
                    : "Reversed depth removed, but Tweaks could not remove DXVK.", ex);
            }
        }

        // Reversed depth only gains precision when the runtime stores the depth buffer as float.
        private async Task SetUpFloatDepthAsync(string binariesDirectory)
        {
            switch (await Task.Run(() => RemixRuntimeHelper.Detect(binariesDirectory)))
            {
                case D3D9Runtime.Stock:
                    await OfferDxvkInstallAsync(binariesDirectory);
                    return;
                case D3D9Runtime.RemixNgx:
                    await EnableRemixFloatDepthAsync(binariesDirectory);
                    return;
                case D3D9Runtime.RemixNgxOutdated:
                    await OfferRemixUpdateAsync(binariesDirectory);
                    return;
                case D3D9Runtime.RemixPathTraced:
                case D3D9Runtime.OtherWrapper:
                    await _dialogService.ShowMessageAsync("Reversed Depth",
                        $"Reversed depth applied, but it needs DXVK or {RemixNgxName}, and another mod's d3d9.dll (for example ReShade) " +
                        "is in the game's Binaries folder.\n\n" +
                        "Please remove that d3d9.dll and try again.",
                        DialogMessageType.Warning);
                    return;
                case D3D9Runtime.DxvkOutdated:
                    await OfferDxvkUpdateAsync(binariesDirectory);
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
                $"Reversed depth applied, but it needs DXVK or {RemixNgxName} to reduce Z-fighting.\n\n" +
                "Download and install DXVK into the game's Binaries folder?\n\n" +
                $"You can also choose {RemixNgxName} in the 'Renderer' setting above instead, which adds upscaling and frame generation as well.\n\n" +
                "Note: DXVK runs the game through Vulkan, so it needs a graphics driver with Vulkan 1.3 support.");
            if (install)
                await InstallDxvkCoreAsync(binariesDirectory, "Reversed Depth");
        }

        // RTX Remix NGX stores depth as float by default, but the dxvk.conf line also covers a later switch to DXVK.
        private async Task EnableRemixFloatDepthAsync(string binariesDirectory)
        {
            DxvkHelper.EnableFloatDepth(binariesDirectory);
            if (RemixRuntimeHelper.IsFloatDepthOffInRtxConf(binariesDirectory))
                await _dialogService.ShowMessageAsync("Reversed Depth", RtxConfFloatDepthOffMessage("Reversed depth applied"), DialogMessageType.Warning);
            else
                await _dialogService.ShowMessageAsync("Reversed Depth", "Reversed depth applied.", DialogMessageType.Success);
        }

        private async Task OfferRemixUpdateAsync(string binariesDirectory)
        {
            if (await _dialogService.ShowConfirmationAsync("Reversed Depth",
                    $"Reversed depth applied, but the installed {RemixNgxName} is too old to support it.\n\nUpdate {RemixNgxName} now?"))
                await InstallRemixCoreAsync(binariesDirectory, "Reversed Depth");
        }

        private async Task OfferDxvkUpdateAsync(string binariesDirectory)
        {
            if (await _dialogService.ShowConfirmationAsync("Reversed Depth",
                    "Reversed depth applied, but the DXVK in the game's Binaries folder is too old for it.\n\nUpdate DXVK now?"))
                await InstallDxvkCoreAsync(binariesDirectory, "Reversed Depth");
        }

        private async Task EnableFloatDepthAsync(string binariesDirectory, string successMessage)
        {
            DxvkHelper.EnableFloatDepth(binariesDirectory);
            await _dialogService.ShowMessageAsync("Reversed Depth", successMessage, DialogMessageType.Success);
        }

        private async Task OfferDxvkRemovalAsync(string binariesDirectory)
        {
            if (await Task.Run(() => RemixRuntimeHelper.Detect(binariesDirectory)) != D3D9Runtime.Dxvk)
                return;

            bool remove = await _dialogService.ShowConfirmationAsync("Reversed Depth",
                "Reversed depth removed. Also remove DXVK from the game's Binaries folder?");
            if (!remove)
                return;

            DxvkHelper.RemoveDxvk(binariesDirectory);
            await RefreshRuntimeAsync();
            await _dialogService.ShowMessageAsync("Reversed Depth", "DXVK removed.", DialogMessageType.Success);
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

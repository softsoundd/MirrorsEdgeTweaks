using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    // The Renderer setting picks which d3d9.dll sits beside MirrorsEdge.exe.
    public partial class GraphicsTweaksViewModel
    {
        private const int StockRuntimeIndex = 0;
        private const int DxvkRuntimeIndex = 1;
        private const int RemixNgxRuntimeIndex = 2;
        private const int OtherRuntimeIndex = 3;

        private const string RuntimeTitle = "Renderer";
        private const string RemixNgxName = "RTX Remix NGX";
        private const string RemixMenuHint =
            "In game, press Alt + X to open the Remix menu. Upscaling and frame generation are under User Graphics Settings > General.";

        [ObservableProperty] private int _runtimeIndex = -1;
        [ObservableProperty] private bool _isOtherRuntimeVisible;
        [ObservableProperty] private string _otherRuntimeName = "";
        [ObservableProperty] private bool _isRemixNgxInstalled;
        [ObservableProperty] private bool _isRuntimeUpdateAvailable;

        private D3D9Runtime? _runtime;
        private string? _installedRemixVersion;
        private string? _latestRemixVersion;

        partial void OnRuntimeIndexChanged(int oldValue, int newValue) =>
            EnqueueApply(() => OnRuntimeChangedAsync(oldValue, newValue));

        private async Task OnRuntimeChangedAsync(int previousIndex, int index)
        {
            if (_isLoading || index is < 0 or OtherRuntimeIndex)
                return;

            string? binaries = GetBinariesDirectory();
            if (binaries == null)
            {
                _dialogService.ShowMessage("Error", "MirrorsEdge.exe was not found. Please select a valid game directory first.", DialogMessageType.Error);
                SetSilently(() => RuntimeIndex = previousIndex);
                return;
            }

            D3D9Runtime current;
            try
            {
                current = await Task.Run(() => RemixRuntimeHelper.Detect(binaries));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _dialogService.ShowErrorAsync("Could not check which renderer the game uses.", ex);
                await RefreshRuntimeAsync();
                return;
            }

            if (ToRuntimeIndex(current) == index || !await ConfirmRuntimeChangeAsync(current, index))
            {
                await RefreshRuntimeAsync();
                return;
            }

            switch (index)
            {
                case StockRuntimeIndex:
                    await RemoveRuntimeCoreAsync(binaries, current);
                    break;
                case DxvkRuntimeIndex:
                    await InstallDxvkCoreAsync(binaries, RuntimeTitle);
                    break;
                case RemixNgxRuntimeIndex:
                    await InstallRemixCoreAsync(binaries, RuntimeTitle);
                    break;
            }
        }

        // Another mod's d3d9.dll is never replaced.
        // A path-traced RTX Remix build is only replaced after a confirmation, because Tweaks did not install it.
        private async Task<bool> ConfirmRuntimeChangeAsync(D3D9Runtime current, int index)
        {
            if (current == D3D9Runtime.OtherWrapper)
            {
                await _dialogService.ShowMessageAsync(RuntimeTitle,
                    "Another mod's d3d9.dll (for example ReShade) is in the game's Binaries folder, so Tweaks cannot change the renderer.\n\n" +
                    "Please remove that d3d9.dll and try again.",
                    DialogMessageType.Warning);
                return false;
            }

            var paragraphs = new List<string>();
            if (current == D3D9Runtime.RemixPathTraced)
            {
                string outcome = index == StockRuntimeIndex ? "removed" : "replaced";
                paragraphs.Add($"The path-traced version of RTX Remix in the game's Binaries folder will be {outcome}. " +
                    "Your RTX Remix settings are kept.");
            }
            if (index == RemixNgxRuntimeIndex)
            {
                paragraphs.Add($"{RemixNgxName} will be downloaded and installed into the game's Binaries folder.");
                paragraphs.Add("Tweaks will also patch MirrorsEdge.exe so that the sun haze and first-person shadows stay in the right place " +
                    "at the lower render resolutions that upscalers use.");
                paragraphs.Add($"Note: {RemixNgxName} needs a graphics driver with Vulkan 1.3 support, and DLSS needs an NVIDIA RTX graphics card.");
            }
            if (paragraphs.Count == 0)
                return true;

            paragraphs.Add("Continue?");
            return await _dialogService.ShowConfirmationAsync(RuntimeTitle, string.Join("\n\n", paragraphs));
        }

        [RelayCommand]
        private Task UpdateRuntime() => RunApplyAsync(UpdateRuntimeCoreAsync);

        private async Task UpdateRuntimeCoreAsync()
        {
            string? binaries = GetBinariesDirectory();
            if (binaries == null)
                return;

            switch (_runtime)
            {
                case D3D9Runtime.RemixNgx:
                case D3D9Runtime.RemixNgxOutdated:
                    if (await _dialogService.ShowConfirmationAsync(RuntimeTitle, $"Download and install the latest version of {RemixNgxName}?"))
                        await InstallRemixCoreAsync(binaries, RuntimeTitle);
                    return;
                case D3D9Runtime.DxvkOutdated:
                    await InstallDxvkCoreAsync(binaries, RuntimeTitle);
                    return;
            }
        }

        // The caller confirms the download first.
        private Task InstallRemixCoreAsync(string binariesDirectory, string dialogTitle) =>
            InstallRuntimeAsync(binariesDirectory, dialogTitle, RemixNgxName, isRemix: true, async () =>
            {
                string url = _assetUrls.For(RemixRuntimeHelper.AssetFileName);
                string? version = await RunDownloadAndExtractAsync(_download, _fileService, url, Path.GetDirectoryName(binariesDirectory)!, RemixNgxName,
                    customExtract: (zip, destination) => Task.Run(() => RemixRuntimeHelper.Install(zip, destination)));
                if (version != null)
                    RemixRuntimeHelper.WriteInstalledVersion(binariesDirectory, version);
            });

        private Task InstallDxvkCoreAsync(string binariesDirectory, string dialogTitle) =>
            InstallRuntimeAsync(binariesDirectory, dialogTitle, "DXVK", isRemix: false, () =>
                RunDownloadAndExtractAsync(_download, _fileService, _assetUrls.For(DxvkHelper.AssetFileName), binariesDirectory, "DXVK",
                    customExtract: (zip, destination) => Task.Run(() =>
                    {
                        RemixRuntimeHelper.RemoveCurrentRuntime(destination);
                        DxvkHelper.ExtractAssetDll(zip, destination);
                    })));

        // Runs while the caller holds the apply gate, which is not reentrant.
        // The download finishes before the current runtime is removed, so a failed download leaves it in place.
        private async Task InstallRuntimeAsync(string binariesDirectory, string dialogTitle, string name, bool isRemix, Func<Task> install)
        {
            if (!await EnsureGameClosedAsync(dialogTitle))
            {
                await RefreshRuntimeAsync();
                return;
            }

            Exception? failure = null;
            string message = "";
            DialogMessageType type = DialogMessageType.Success;
            string? details = null;
            _gameStatus.IsUiEnabled = false;
            try
            {
                await _assetUrls.EnsureLoadedAsync();
                await install();
                (message, type, details) = await FinishInstallAsync(binariesDirectory, name, isRemix);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
            }

            await RefreshRuntimeAsync();
            if (failure != null)
            {
                _gameStatus.Status = "Installation failed.";
                await _dialogService.ShowErrorAsync($"Could not install {name}.", failure);
                return;
            }

            await _dialogService.ShowMessageAsync(dialogTitle, message, type, details);
        }

        // Both runtimes read dxvk.conf, so its float depth line also covers a later switch between them.
        private async Task<(string Message, DialogMessageType Type, string? Details)> FinishInstallAsync(
            string binariesDirectory, string name, bool isRemix)
        {
            string message = $"{name} installed.";
            DialogMessageType type = DialogMessageType.Success;
            string? details = null;

            if (ReversedDepthIndex == 0)
            {
                try
                {
                    DxvkHelper.EnableFloatDepth(binariesDirectory);
                    if (isRemix && RemixRuntimeHelper.IsFloatDepthOffInRtxConf(binariesDirectory))
                    {
                        message = RtxConfFloatDepthOffMessage($"{name} installed");
                        type = DialogMessageType.Warning;
                    }
                    else
                    {
                        message = $"{name} installed and set up for reversed depth.";
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    message = $"{name} installed, but Tweaks could not turn on a setting that reversed depth needs, so Z-fighting will not improve yet.\n\n" +
                        ErrorMessageHelper.Explain(ex);
                    type = DialogMessageType.Warning;
                    details = ErrorMessageHelper.Details(ex);
                }
            }

            if (isRemix)
            {
                if (await ApplyRenderTargetFixAsync(binariesDirectory) is { } failure)
                {
                    message += "\n\nTweaks could not patch MirrorsEdge.exe to keep the sun haze and first-person shadows in the right place, " +
                        $"so they may look out of place in upscaler modes that render below native resolution.\n\n{ErrorMessageHelper.Explain(failure)}";
                    type = DialogMessageType.Warning;
                    details ??= ErrorMessageHelper.Details(failure);
                }
                else
                {
                    message += "\n\nTweaks also patched MirrorsEdge.exe so that the sun haze and first-person shadows " +
                        "stay in the right place in every upscaler mode.";
                }
                message += "\n\n" + RemixMenuHint;
            }

            return (message, type, details);
        }

        // Upscaler modes below DLAA render below native resolution, where the sun haze and first-person self-shadowing need this fix.
        // Returns null when the fix is applied.
        // Returns the error when it could not be applied.
        private async Task<Exception?> ApplyRenderTargetFixAsync(string binariesDirectory)
        {
            string exePath = Path.Combine(binariesDirectory, "MirrorsEdge.exe");
            ShowProgress("Patching MirrorsEdge.exe...", isIndeterminate: true);
            try
            {
                await Task.Run(() => RenderTargetPatcher.Reconcile(exePath));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                HideProgress();
            }
        }

        private static string RtxConfFloatDepthOffMessage(string start) =>
            $"{start}, but a line in {RemixNgxName}'s settings file stops reversed depth from working, so Z-fighting will not improve.\n\n" +
            "To fix this, open rtx.conf in the game's Binaries folder and delete the line \"d3d9.useD32forD24 = False\".";

        private async Task RemoveRuntimeCoreAsync(string binariesDirectory, D3D9Runtime current)
        {
            if (!await EnsureGameClosedAsync(RuntimeTitle))
            {
                await RefreshRuntimeAsync();
                return;
            }

            string name = RuntimeName(current);
            bool removed = false;
            Exception? failure = null;
            try
            {
                removed = await RunBusyAsync($"Removing {name}...", () => RemixRuntimeHelper.RemoveCurrentRuntime(binariesDirectory));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await RefreshRuntimeAsync();
            if (failure != null)
            {
                await _dialogService.ShowErrorAsync($"Could not remove {name}.", failure);
                return;
            }
            if (!removed)
                return;

            string message = $"{name} removed. The game now uses its default renderer, Direct3D 9.";
            if (ReversedDepthIndex == 0)
                message += $"\n\nReversed depth stays applied, but it only reduces Z-fighting with DXVK or {RemixNgxName}.";
            await _dialogService.ShowMessageAsync(RuntimeTitle, message, DialogMessageType.Success);
        }

        private async Task<bool> EnsureGameClosedAsync(string dialogTitle)
        {
            if (!RemixRuntimeHelper.IsGameOrBridgeRunning())
                return true;

            bool exited;
            ShowProgress("Waiting for Mirror's Edge to close...", isIndeterminate: true);
            try
            {
                exited = await RemixRuntimeHelper.WaitForGameExitAsync();
            }
            finally
            {
                HideProgress();
            }

            if (!exited)
            {
                await _dialogService.ShowMessageAsync(dialogTitle,
                    "Mirror's Edge is still running, or RTX Remix is still closing (NvRemixBridge.exe), so the renderer cannot be changed yet.\n\n" +
                    "Close Mirror's Edge, wait a few seconds and try again. If NvRemixBridge.exe keeps running, end it in Task Manager.",
                    DialogMessageType.Warning);
            }
            return exited;
        }

        public async Task RefreshRuntimeAsync()
        {
            string? binaries = GetBinariesDirectory();
            D3D9Runtime? runtime = null;
            string? installedVersion = null;
            if (binaries != null)
            {
                try
                {
                    (runtime, installedVersion) = await Task.Run(() =>
                    {
                        D3D9Runtime detected = RemixRuntimeHelper.Detect(binaries);
                        return (detected, detected == D3D9Runtime.RemixNgx ? RemixRuntimeHelper.ReadInstalledVersion(binaries) : null);
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            _runtime = runtime;
            _installedRemixVersion = installedVersion;
            SetSilently(() =>
            {
                OtherRuntimeName = runtime == D3D9Runtime.RemixPathTraced ? "RTX Remix (path-traced)" : "Another mod (e.g. ReShade)";
                IsOtherRuntimeVisible = runtime is D3D9Runtime.RemixPathTraced or D3D9Runtime.OtherWrapper;
                RuntimeIndex = runtime is { } detected ? ToRuntimeIndex(detected) : -1;
                IsRemixNgxInstalled = runtime is D3D9Runtime.RemixNgx or D3D9Runtime.RemixNgxOutdated;
            });
            UpdateRuntimeUpdateAvailable();
        }

        // Compares the validator recorded at install with the published asset's.
        public async Task CheckForRuntimeUpdateAsync()
        {
            if (_runtime != D3D9Runtime.RemixNgx || _installedRemixVersion == null)
                return;

            try
            {
                await _assetUrls.EnsureLoadedAsync();
                string? latest = await _download.GetValidatorAsync(_assetUrls.For(RemixRuntimeHelper.AssetFileName));
                if (latest == null)
                    return;

                _latestRemixVersion = latest;
                UpdateRuntimeUpdateAvailable();
            }
            catch
            {
            }
        }

        // An install without a recorded version, such as a build deployed by hand, is never reported as out of date.
        private void UpdateRuntimeUpdateAvailable() =>
            IsRuntimeUpdateAvailable = _runtime is D3D9Runtime.DxvkOutdated or D3D9Runtime.RemixNgxOutdated
                || (_runtime == D3D9Runtime.RemixNgx && _installedRemixVersion != null && _latestRemixVersion != null
                    && _installedRemixVersion != _latestRemixVersion);

        private string? GetBinariesDirectory() =>
            GetGameExePath() is { } exePath ? Path.GetDirectoryName(exePath) : null;

        private static int ToRuntimeIndex(D3D9Runtime runtime) => runtime switch
        {
            D3D9Runtime.Stock => StockRuntimeIndex,
            D3D9Runtime.Dxvk or D3D9Runtime.DxvkOutdated => DxvkRuntimeIndex,
            D3D9Runtime.RemixNgx or D3D9Runtime.RemixNgxOutdated => RemixNgxRuntimeIndex,
            _ => OtherRuntimeIndex,
        };

        private static string RuntimeName(D3D9Runtime runtime) => runtime switch
        {
            D3D9Runtime.Dxvk or D3D9Runtime.DxvkOutdated => "DXVK",
            D3D9Runtime.RemixNgx or D3D9Runtime.RemixNgxOutdated => RemixNgxName,
            D3D9Runtime.RemixPathTraced => "RTX Remix",
            _ => "the renderer",
        };
    }
}

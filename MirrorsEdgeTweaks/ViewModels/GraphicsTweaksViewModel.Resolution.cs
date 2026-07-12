using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Resolution selection, dynamic high-res UI fix orchestration and per-resolution patch reconcile.
    public partial class GraphicsTweaksViewModel
    {
        // ---- Resolution + High-Res Fix ----

        partial void OnSelectedResolutionChanged(ResolutionHelper.Resolution? value) => _ = OnResolutionChangedAsync(value);

        private async Task OnResolutionChangedAsync(ResolutionHelper.Resolution? selectedResolution)
        {
            if (_isLoading || selectedResolution == null)
                return;

            _gameStatus.IsUiEnabled = false;

            try
            {
                bool success = await UpdateResolutionInConfigAsync(selectedResolution.Width, selectedResolution.Height);
                if (!success)
                    return;

                bool userWantsUIScaling = false;
                var gameDir = _session.Config.GameDirectoryPath;

                if (_uiScaling.ShouldOfferUIScaling(selectedResolution.Width))
                {
                    _gameStatus.IsUiEnabled = true;
                    userWantsUIScaling = await _uiScaling.AskUserForUIScalingConfirmationAsync();
                    _gameStatus.IsUiEnabled = false;

                    if (!string.IsNullOrEmpty(gameDir))
                    {
                        ShowProgress("Applying UI scaling...", true);

                        await Task.Run(async () =>
                        {
                            if (userWantsUIScaling)
                                await _uiScaling.ApplyUIScalingAsync(selectedResolution.Width, selectedResolution.Height, gameDir, () => HideProgress());
                            else
                                await _uiScaling.RollbackUIScalingToDefaultsAsync(selectedResolution.Width, selectedResolution.Height, gameDir, () => HideProgress());
                        });
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(gameDir))
                    {
                        ShowProgress("Resetting UI scaling...", true);

                        await Task.Run(async () =>
                        {
                            await _uiScaling.RollbackUIScalingToDefaultsAsync(selectedResolution.Width, selectedResolution.Height, gameDir, () => HideProgress());
                        });
                    }
                }

                UpdateHighResFixStatus(selectedResolution.Width, userWantsUIScaling);

                await Task.Run(ApplyDynamicPatchesForResolution);

                RefreshEnginePatchState();
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
                _gameStatus.Status = "Ready.";
            }
        }

        // Ensures the exe render target fix, Engine.u dynamic AR/FOV scaling,
        // and TdGame.u core compensation patches are applied when resolution changes
        private void ApplyDynamicPatchesForResolution()
        {
            try
            {
                string? exePath = GetGameExePath();
                if (exePath != null)
                    ExePatcher.Reconcile(exePath);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                Dispatch(() => _dialogService.ShowMessage("EA App License Required", ooaEx.Message, DialogMessageType.Warning));
            }
            catch { }

            try
            {
                var enginePath = _session.Config.EnginePackagePath;
                if (enginePath != null && File.Exists(enginePath))
                    EnginePatcher.Reconcile(enginePath);
            }
            catch { }

            try
            {
                var tdGamePath = _session.Config.TdGamePackagePath;
                if (tdGamePath != null && File.Exists(tdGamePath))
                {
                    bool enableSens = false;
                    bool enableClip = false;
                    bool enableOnlineSkip = false;
                    Dispatch(() =>
                    {
                        enableSens = FovAgnosticSens;
                        enableClip = CompensatedClip;
                        enableOnlineSkip = _session.OnlineSkipEnabled;
                    });
                    TdGamePatcher.Reconcile(tdGamePath, enableSens, enableClip, enableOnlineSkip);
                }
            }
            catch { }
        }

        public void UpdateHighResFixStatus(int width, bool isActive)
        {
            if (_session.IsProcessingGameDirectory)
                return;

            if (width <= 1920)
            {
                HighResFixStatus = "High-Res Fix N/A";
                HighResFixStatusForeground = Brushes.Gray;
            }
            else if (isActive)
            {
                HighResFixStatus = "High-Res Fix Active";
                HighResFixStatusForeground = Brushes.Green;
            }
            else
            {
                HighResFixStatus = "High-Res Fix Inactive";
                HighResFixStatusForeground = Brushes.Orange;
            }
        }

        private async Task<bool> UpdateResolutionInConfigAsync(int width, int height)
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return false;

            try
            {
                string? engineIniPath = IniPath;

                if (string.IsNullOrEmpty(engineIniPath) || !File.Exists(engineIniPath))
                {
                    await _dialogService.ShowMessageAsync("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogMessageType.Error);
                    return false;
                }

                var fileInfo = new FileInfo(engineIniPath);

                try
                {
                    if (fileInfo.IsReadOnly)
                        fileInfo.IsReadOnly = false;
                }
                catch (UnauthorizedAccessException)
                {
                    await _dialogService.ShowMessageAsync("Error", "Unable to access TdEngine.ini. The file may be in use by another program.", DialogMessageType.Error);
                    return false;
                }
                catch (IOException ex)
                {
                    await _dialogService.ShowMessageAsync("Error", $"Unable to access TdEngine.ini: {ex.Message}", DialogMessageType.Error);
                    return false;
                }

                try
                {
                    ConfigFileHelper.ModifyIniFile(engineIniPath, "SystemSettings", "ResX", width.ToString());
                    ConfigFileHelper.ModifyIniFile(engineIniPath, "SystemSettings", "ResY", height.ToString());
                }
                finally
                {
                    try
                    {
                        if (File.Exists(engineIniPath))
                            fileInfo.IsReadOnly = true;
                    }
                    catch
                    {
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Error", $"Failed to update resolution: {ex.Message}", DialogMessageType.Error);
                return false;
            }
        }

        public void RefreshHighResFix()
        {
            var res = SelectedResolution;
            if (res != null)
            {
                bool isCurrentlyActive = false;
                var gameDir = _session.Config.GameDirectoryPath;
                if (!string.IsNullOrEmpty(gameDir))
                {
                    try
                    {
                        isCurrentlyActive = _uiScaling.IsUIScalingActive(gameDir);
                    }
                    catch
                    {
                        isCurrentlyActive = false;
                    }
                }

                UpdateHighResFixStatus(res.Width, isCurrentlyActive);
                return;
            }

            HighResFixStatus = "High-Res Fix N/A";
            HighResFixStatusForeground = Brushes.Gray;
        }

        public async Task RefreshHighResFixAsync()
        {
            var res = SelectedResolution;
            if (res == null)
            {
                HighResFixStatus = "High-Res Fix N/A";
                HighResFixStatusForeground = Brushes.Gray;
                return;
            }

            bool isCurrentlyActive = false;
            var gameDir = _session.Config.GameDirectoryPath;
            if (!string.IsNullOrEmpty(gameDir))
            {
                isCurrentlyActive = await Task.Run(() =>
                {
                    try { return _uiScaling.IsUIScalingActive(gameDir); }
                    catch { return false; }
                });
            }

            UpdateHighResFixStatus(res.Width, isCurrentlyActive);
        }

        // Reapplies (or rolls back to defaults) the high-res UI scaling fix for the active
        // resolution. Called by flows that overwrite game files on disk (language pack and TdGame
        // version installs) so the fix is not lost.
        public async Task ReapplyHighResUIFixIfNeededAsync(bool? wasUIScalingActiveOverride = null, bool showDialogs = true)
        {
            try
            {
                var gameDir = _session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                    return;

                bool wasUIScalingActive = wasUIScalingActiveOverride ?? _uiScaling.IsUIScalingActive(gameDir);

                ResolutionHelper.Resolution? selectedResolution = SelectedResolution ?? GetCurrentResolutionFromConfig();

                if (selectedResolution == null || !_uiScaling.ShouldOfferUIScaling(selectedResolution.Width))
                {
                    return;
                }

                var dispatcher = System.Windows.Application.Current.Dispatcher;

                await dispatcher.InvokeAsync(() =>
                {
                    _downloadProgress.IsDownloadProgressIndeterminate = true;
                    _downloadProgress.IsDownloadProgressVisible = true;
                    _gameStatus.Status = wasUIScalingActive ? "Reapplying high-res UI fix..." : "Resetting UI scaling...";
                });

                await Task.Run(async () =>
                {
                    if (wasUIScalingActive)
                    {
                        await _uiScaling.ApplyUIScalingAsync(
                            selectedResolution.Width,
                            selectedResolution.Height,
                            gameDir,
                            null,
                            showDialogs);
                    }
                    else
                    {
                        await _uiScaling.RollbackUIScalingToDefaultsAsync(
                            selectedResolution.Width,
                            selectedResolution.Height,
                            gameDir,
                            null,
                            showDialogs);
                    }
                });

                await dispatcher.InvokeAsync(() =>
                {
                    UpdateHighResFixStatus(selectedResolution.Width, wasUIScalingActive);
                    _downloadProgress.IsDownloadProgressVisible = false;
                    _downloadProgress.IsDownloadProgressIndeterminate = false;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to reapply high res UI fix: {ex.Message}");
            }
        }

        public void InitializeResolutions()
        {
            SetSilently(() =>
            {
                Resolutions.Clear();
                foreach (var resolution in ResolutionHelper.GetAvailableResolutions())
                    Resolutions.Add(resolution);

                ResolutionHelper.Resolution? match = null;
                var current = GetCurrentResolutionFromConfig();
                if (current != null)
                    match = Resolutions.FirstOrDefault(r => r.Width == current.Width && r.Height == current.Height);

                if (match == null && Resolutions.Count > 0)
                    match = Resolutions[0];

                SelectedResolution = match;
            });

            if (SelectedResolution != null)
            {
                bool isCurrentlyActive = false;
                var gameDir = _session.Config.GameDirectoryPath;
                if (!string.IsNullOrEmpty(gameDir))
                    isCurrentlyActive = _uiScaling.IsUIScalingActive(gameDir);

                UpdateHighResFixStatus(SelectedResolution.Width, isCurrentlyActive);
            }
        }

        public async Task InitializeResolutionsAsync()
        {
            ResolutionHelper.Resolution? selected = null;
            SetSilently(() =>
            {
                Resolutions.Clear();
                foreach (var resolution in ResolutionHelper.GetAvailableResolutions())
                    Resolutions.Add(resolution);

                ResolutionHelper.Resolution? match = null;
                var current = GetCurrentResolutionFromConfig();
                if (current != null)
                    match = Resolutions.FirstOrDefault(r => r.Width == current.Width && r.Height == current.Height);

                if (match == null && Resolutions.Count > 0)
                    match = Resolutions[0];

                SelectedResolution = match;
                selected = match;
            });

            if (selected != null)
            {
                bool isCurrentlyActive = false;
                var gameDir = _session.Config.GameDirectoryPath;
                if (!string.IsNullOrEmpty(gameDir))
                {
                    isCurrentlyActive = await Task.Run(() =>
                    {
                        try { return _uiScaling.IsUIScalingActive(gameDir); }
                        catch { return false; }
                    });
                }

                UpdateHighResFixStatus(selected.Width, isCurrentlyActive);
            }
        }

        private ResolutionHelper.Resolution? GetCurrentResolutionFromConfig()
        {
            try
            {
                string? engineIniPath = IniPath;

                if (string.IsNullOrEmpty(engineIniPath) || !File.Exists(engineIniPath))
                    return null;

                var lines = File.ReadAllLines(engineIniPath);
                int resX = -1;
                int resY = -1;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("ResX=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmedLine.Substring(5), out int x))
                            resX = x;
                    }
                    else if (trimmedLine.StartsWith("ResY=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmedLine.Substring(5), out int y))
                            resY = y;
                    }
                }

                if (resX > 0 && resY > 0)
                    return new ResolutionHelper.Resolution { Width = resX, Height = resY };
            }
            catch (Exception)
            {
            }

            return null;
        }
    }
}

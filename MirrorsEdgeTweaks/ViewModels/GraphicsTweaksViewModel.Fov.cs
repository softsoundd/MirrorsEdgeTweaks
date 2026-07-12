using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Services;
using System.Globalization;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class GraphicsTweaksViewModel
    {
        public void RefreshFovDisplay()
        {
            float? detected = _session.DetectedCameraFov;
            if (detected == null)
            {
                CurrentFovValue = "N/A";
                return;
            }

            CurrentFovValue = Math.Round(detected.Value).ToString(CultureInfo.InvariantCulture) + "\u00b0 (horizontal)";

            if (string.IsNullOrEmpty(NewFovValue) || NewFovValue == "90" || NewFovValue == "N/A")
            {
                NewFovValue = Math.Round(detected.Value).ToString(CultureInfo.InvariantCulture);
            }
        }

        partial void OnNewFovValueChanged(string value) => RefreshScalingStatus();

        [RelayCommand]
        private Task ApplyFov() => RunApplyAsync(ApplyFovCore);

        private async Task ApplyFovCore()
        {
            var config = _session.Config;

            if (config.EnginePackagePath == null || config.TdGamePackagePath == null)
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }

            if (!float.TryParse(NewFovValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float newFov) || newFov < 80 || newFov > 179)
            {
                _dialogService.ShowMessage("Invalid Input", "Please enter a valid number for the FOV (must be between 80 - 179).", DialogMessageType.Warning);
                return;
            }

            bool enableSens = FovAgnosticSens;
            bool enableClip = CompensatedClip;
            bool enableOnlineSkip = _session.OnlineSkipEnabled;

            _gameStatus.IsUiEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.IsDownloadProgressIndeterminate = true;
            _gameStatus.Status = "Applying FOV settings...";

            try
            {
                await Task.Run(() =>
                {
                    _session.Package?.Dispose();
                    _session.TdGamePackage?.Dispose();
                    _session.Package = null;
                    _session.TdGamePackage = null;

                    EnginePatcher.Reconcile(config.EnginePackagePath);

                    TdGamePatcher.Reconcile(config.TdGamePackagePath, enableSens, enableClip, enableOnlineSkip);

                    string? exePath = GetGameExePath();
                    if (exePath != null)
                    {
                        try { ExePatcher.Reconcile(exePath); }
                        catch (OoaLicenseNotFoundException ooaEx)
                        {
                            Dispatch(() => _dialogService.ShowMessage("EA App License Required", ooaEx.Message, DialogMessageType.Warning));
                        }
                        catch { }
                    }
                });

                // Intermediate reload purely to re-find the FOV offsets after the reconcile above;
                // notify: false suppresses the feature-VM refresh fan-out, which would otherwise run
                // twice (the finally block below does the single, final notifying reload).
                await _gameData.ReloadPackagesAsync(notify: false);

                await Task.Run(() =>
                {
                    if (config.EnginePackagePath == null)
                        return;

                    var offsets = _session.Offsets;
                    byte[] fovValueBytes = BitConverter.GetBytes(newFov);
                    using var stream = new FileStream(config.EnginePackagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

                    if (offsets.PlayerControllerDefaultFovOffset != -1)
                    {
                        stream.Position = offsets.PlayerControllerDefaultFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.PlayerControllerDesiredFovOffset != -1)
                    {
                        stream.Position = offsets.PlayerControllerDesiredFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.PlayerControllerFovAngleOffset != -1)
                    {
                        stream.Position = offsets.PlayerControllerFovAngleOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.CameraFovOffset != -1)
                    {
                        stream.Position = offsets.CameraFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.CameraActorFovAngleOffset != -1)
                    {
                        stream.Position = offsets.CameraActorFovAngleOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                });

                _session.Config.Fov = NewFovValue;
                _settings.Save();

                _gameStatus.Status = "Ready.";
                await _dialogService.ShowMessageAsync("Success", "Successfully applied FOV patches.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Save Error", $"Failed to apply changes: {ex.Message}", DialogMessageType.Error);
                _gameStatus.Status = "Error applying changes.";
            }
            finally
            {
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.IsDownloadProgressIndeterminate = false;

                await _gameData.ReloadPackagesAsync();

                _gameStatus.IsUiEnabled = true;
            }
        }

        public void RefreshEnginePatchState()
        {
            _enginePatchesApplied = false;
            var enginePath = _session.Config.EnginePackagePath;
            if (enginePath != null && File.Exists(enginePath))
            {
                try { _enginePatchesApplied = EnginePatcher.DetectState(enginePath) == EnginePatchState.FullyPatched; }
                catch { }
            }

            RefreshScalingStatus();
        }

        // Reads the (expensive, Engine.u-loading) dynamic-FOV-scaling patch state off the UI thread,
        // then applies it on the UI thread so the status-bar progress bar keeps animating.
        public async Task RefreshEnginePatchStateAsync()
        {
            bool applied = false;
            var enginePath = _session.Config.EnginePackagePath;
            if (enginePath != null && File.Exists(enginePath))
            {
                applied = await Task.Run(() =>
                {
                    try { return EnginePatcher.DetectState(enginePath) == EnginePatchState.FullyPatched; }
                    catch { return false; }
                });
            }

            _enginePatchesApplied = applied;
            RefreshScalingStatus();
        }

        public void RefreshScalingStatus()
        {
            if (!_enginePatchesApplied)
            {
                HorPlusStatus = "";
                return;
            }

            var res = SelectedResolution;
            if (res == null || res.Height == 0)
            {
                HorPlusStatus = "";
                return;
            }

            double ar = (double)res.Width / res.Height;
            const double baseline = 16.0 / 9.0;
            const double tolerance = 0.01;

            if (ar > baseline + tolerance)
            {
                if (float.TryParse(NewFovValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float baseFov) && baseFov > 0)
                {
                    double effectiveFov = ComputeHorPlusFov(baseFov, ar);
                    HorPlusStatus = $"(HOR+ \u2192 {effectiveFov:F0}°)";
                }
                else
                {
                    HorPlusStatus = "(HOR+)";
                }
            }
            else if (ar < baseline - tolerance)
            {
                HorPlusStatus = "(VERT+)";
            }
            else
            {
                HorPlusStatus = "";
            }
        }

        private static double ComputeHorPlusFov(double baseFov, double aspectRatio)
        {
            const double baseline = 16.0 / 9.0;
            double halfRad = baseFov * Math.PI / 360.0;
            return Math.Atan(Math.Tan(halfRad) * aspectRatio / baseline) * 360.0 / Math.PI;
        }
    }
}

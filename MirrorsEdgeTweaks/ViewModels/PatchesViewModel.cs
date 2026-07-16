using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class PatchesViewModel : BusyViewModel
    {
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        private readonly UnlockedConfigsViewModel _unlockedConfigs;

        [ObservableProperty] private string _loggingPatchStatus = "N/A";
        [ObservableProperty] private System.Windows.Media.Brush _loggingPatchStatusForeground = System.Windows.Media.Brushes.Gray;
        [ObservableProperty] private string _multiInstancePatchStatus = "N/A";
        [ObservableProperty] private System.Windows.Media.Brush _multiInstancePatchStatusForeground = System.Windows.Media.Brushes.Gray;
        [ObservableProperty] private string _ambiguousBypassPatchStatus = "N/A";
        [ObservableProperty] private System.Windows.Media.Brush _ambiguousBypassPatchStatusForeground = System.Windows.Media.Brushes.Gray;

        public PatchesViewModel(
            IDialogService dialogService,
            IFileService fileService,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress,
            UnlockedConfigsViewModel unlockedConfigs)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _fileService = fileService;
            _unlockedConfigs = unlockedConfigs;

            LoggingPatchStatusForeground = BodyLightBrush();
            MultiInstancePatchStatusForeground = BodyLightBrush();
            AmbiguousBypassPatchStatusForeground = BodyLightBrush();
        }

        private static System.Windows.Media.Brush PatchedBrush() =>
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));

        private static System.Windows.Media.Brush BodyLightBrush() =>
            System.Windows.Application.Current.TryFindResource("MaterialDesignBodyLight") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Gray;

        private string? GetExePathIfPresent()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                return null;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            return File.Exists(exePath) ? exePath : null;
        }


        public void RefreshLoggingStatus()
        {
            string? exePath = GetExePathIfPresent();
            if (exePath == null)
            {
                LoggingPatchStatus = "N/A";
                return;
            }

            try { ApplyLoggingStatus(LoggingPatchHelper.GetPatchState(exePath)); }
            catch { LoggingPatchStatus = ""; }
        }

        public async Task RefreshLoggingStatusAsync()
        {
            string? exePath = GetExePathIfPresent();
            if (exePath == null)
            {
                LoggingPatchStatus = "N/A";
                return;
            }

            try { ApplyLoggingStatus(await Task.Run(() => LoggingPatchHelper.GetPatchState(exePath))); }
            catch { LoggingPatchStatus = ""; }
        }

        private void ApplyLoggingStatus(LoggingPatchState state)
        {
            switch (state)
            {
                case LoggingPatchState.Patched:
                    LoggingPatchStatus = "Patched";
                    LoggingPatchStatusForeground = PatchedBrush();
                    break;
                case LoggingPatchState.Unpatched:
                    LoggingPatchStatus = "Unpatched";
                    LoggingPatchStatusForeground = BodyLightBrush();
                    break;
                default:
                    LoggingPatchStatus = "";
                    break;
            }
        }

        public void RefreshMultiInstanceStatus()
        {
            string? exePath = GetExePathIfPresent();
            if (exePath == null)
            {
                MultiInstancePatchStatus = "N/A";
                return;
            }

            try { ApplyMultiInstanceStatus(InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.MultiInstanceKey)); }
            catch { MultiInstancePatchStatus = ""; }
        }

        public async Task RefreshMultiInstanceStatusAsync()
        {
            string? exePath = GetExePathIfPresent();
            if (exePath == null)
            {
                MultiInstancePatchStatus = "N/A";
                return;
            }

            try { ApplyMultiInstanceStatus(await Task.Run(() => InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.MultiInstanceKey))); }
            catch { MultiInstancePatchStatus = ""; }
        }

        private void ApplyMultiInstanceStatus(InlinePatchState state)
        {
            switch (state)
            {
                case InlinePatchState.Patched:
                    MultiInstancePatchStatus = "Patched";
                    MultiInstancePatchStatusForeground = PatchedBrush();
                    break;
                case InlinePatchState.Unpatched:
                    MultiInstancePatchStatus = "Unpatched";
                    MultiInstancePatchStatusForeground = BodyLightBrush();
                    break;
                default:
                    MultiInstancePatchStatus = "";
                    break;
            }
        }

        public void RefreshAmbiguousBypassStatus()
        {
            string? exePath = GetExePathIfPresent();
            if (exePath == null)
            {
                AmbiguousBypassPatchStatus = "N/A";
                return;
            }

            try { ApplyAmbiguousBypassStatus(InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.AmbiguousPackageKey)); }
            catch { AmbiguousBypassPatchStatus = ""; }
        }

        public async Task RefreshAmbiguousBypassStatusAsync()
        {
            string? exePath = GetExePathIfPresent();
            if (exePath == null)
            {
                AmbiguousBypassPatchStatus = "N/A";
                return;
            }

            try { ApplyAmbiguousBypassStatus(await Task.Run(() => InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.AmbiguousPackageKey))); }
            catch { AmbiguousBypassPatchStatus = ""; }
        }

        private void ApplyAmbiguousBypassStatus(InlinePatchState state)
        {
            switch (state)
            {
                case InlinePatchState.Patched:
                    AmbiguousBypassPatchStatus = "Patched";
                    AmbiguousBypassPatchStatusForeground = PatchedBrush();
                    break;
                case InlinePatchState.Unpatched:
                    AmbiguousBypassPatchStatus = "Unpatched";
                    AmbiguousBypassPatchStatusForeground = BodyLightBrush();
                    break;
                default:
                    AmbiguousBypassPatchStatus = "";
                    break;
            }
        }


        [RelayCommand]
        private Task PatchLogging() => RunApplyAsync(PatchLoggingCore);

        private async Task PatchLoggingCore()
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool alreadyPatched = false;
                bool ran = await RunBusyAsync("Applying logging patch...", () =>
                {
                    if (LoggingPatchHelper.GetPatchState(exePath) == LoggingPatchState.Patched)
                    {
                        alreadyPatched = true;
                        return;
                    }

                    LoggingPatchHelper.ApplyPatch(exePath);
                });
                if (!ran) return;

                LoggingPatchStatus = "Patched";
                LoggingPatchStatusForeground = PatchedBrush();
                if (!alreadyPatched)
                {
                    _dialogService.ShowMessage("Success", "Logging patch applied.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply logging patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private Task UnpatchLogging() => RunApplyAsync(UnpatchLoggingCore);

        private async Task UnpatchLoggingCore()
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool alreadyUnpatched = false;
                bool ran = await RunBusyAsync("Removing logging patch...", () =>
                {
                    if (LoggingPatchHelper.GetPatchState(exePath) == LoggingPatchState.Unpatched)
                    {
                        alreadyUnpatched = true;
                        return;
                    }

                    LoggingPatchHelper.RemovePatch(exePath);
                });
                if (!ran) return;

                LoggingPatchStatus = "Unpatched";
                if (!alreadyUnpatched)
                {
                    LoggingPatchStatusForeground = BodyLightBrush();
                    _dialogService.ShowMessage("Success", "Logging patch removed.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove logging patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private void ShowLoggingInfo()
        {
            _dialogService.ShowMessage("Enable Logging Information",
                "Restores Mirror's Edge's disabled UE3 logging system.\n\n" +
                "When patched, UnrealScript log() calls and native logging are written to a log file " +
                "(and displayed in the log console window if the \"-LOG\" launch argument is used).\n\n" +
                "By default, the log file is created at \"Logs\\Launch.log\" next to the executable. " +
                "You can customise the log location using launch arguments:\n\n" +
                "\"-LOG=mylog.txt\" — write to a custom filename\n" +
                "\"-ABSLOG=C:\\...\" — write to an absolute path",
                DialogMessageType.Information);
        }


        [RelayCommand]
        private Task PatchMultiInstance() => RunApplyAsync(PatchMultiInstanceCore);

        private async Task PatchMultiInstanceCore()
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool alreadyPatched = false;
                bool ran = await RunBusyAsync("Applying multi-instance patch...", () =>
                {
                    if (InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.MultiInstanceKey) == InlinePatchState.Patched)
                    {
                        alreadyPatched = true;
                        return;
                    }

                    InlineVaPatchHelper.ApplyPatch(exePath, InlineVaPatchHelper.MultiInstanceKey);
                });
                if (!ran) return;

                MultiInstancePatchStatus = "Patched";
                MultiInstancePatchStatusForeground = PatchedBrush();
                if (!alreadyPatched)
                {
                    _dialogService.ShowMessage("Success", "Multi-instance patch applied.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply multi-instance patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private Task UnpatchMultiInstance() => RunApplyAsync(UnpatchMultiInstanceCore);

        private async Task UnpatchMultiInstanceCore()
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool alreadyUnpatched = false;
                bool ran = await RunBusyAsync("Removing multi-instance patch...", () =>
                {
                    if (InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.MultiInstanceKey) == InlinePatchState.Unpatched)
                    {
                        alreadyUnpatched = true;
                        return;
                    }

                    InlineVaPatchHelper.RemovePatch(exePath, InlineVaPatchHelper.MultiInstanceKey);
                });
                if (!ran) return;

                MultiInstancePatchStatus = "Unpatched";
                if (!alreadyUnpatched)
                {
                    MultiInstancePatchStatusForeground = BodyLightBrush();
                    _dialogService.ShowMessage("Success", "Multi-instance patch removed.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove multi-instance patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private void ShowMultiInstanceInfo()
        {
            _dialogService.ShowMessage("Multi-instance Information",
                "Bypasses Mirror's Edge's single-instance restriction.\n\n" +
                "Useful for modders who need the editor open while launching the regular game instance.",
                DialogMessageType.Information);
        }


        [RelayCommand]
        private Task PatchAmbiguousBypass() => RunApplyAsync(PatchAmbiguousBypassCore);

        private async Task PatchAmbiguousBypassCore()
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool alreadyPatched = false;
                bool ran = await RunBusyAsync("Applying ambiguous bypass patch...", () =>
                {
                    if (InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.AmbiguousPackageKey) == InlinePatchState.Patched)
                    {
                        alreadyPatched = true;
                        return;
                    }

                    InlineVaPatchHelper.ApplyPatch(exePath, InlineVaPatchHelper.AmbiguousPackageKey);
                });
                if (!ran) return;

                AmbiguousBypassPatchStatus = "Patched";
                AmbiguousBypassPatchStatusForeground = PatchedBrush();
                if (!alreadyPatched)
                {
                    _dialogService.ShowMessage("Success", "Ambiguous bypass patch applied.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply ambiguous bypass patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private Task UnpatchAmbiguousBypass() => RunApplyAsync(UnpatchAmbiguousBypassCore);

        private async Task UnpatchAmbiguousBypassCore()
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool alreadyUnpatched = false;
                bool ran = await RunBusyAsync("Removing ambiguous bypass patch...", () =>
                {
                    if (InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.AmbiguousPackageKey) == InlinePatchState.Unpatched)
                    {
                        alreadyUnpatched = true;
                        return;
                    }

                    InlineVaPatchHelper.RemovePatch(exePath, InlineVaPatchHelper.AmbiguousPackageKey);
                });
                if (!ran) return;

                AmbiguousBypassPatchStatus = "Unpatched";
                if (!alreadyUnpatched)
                {
                    AmbiguousBypassPatchStatusForeground = BodyLightBrush();
                    _dialogService.ShowMessage("Success", "Ambiguous bypass patch removed.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove ambiguous bypass patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private void ShowAmbiguousBypassInfo()
        {
            _dialogService.ShowMessage("Ambiguous Bypass Information",
                "Suppresses the \"Ambiguous package name\" dialogs that block game startup.\n\n" +
                "When multiple game files with the same name are present in the game directory, " +
                "a blocking message box is displayed for each conflict at startup. With this patch applied, those dialogs are skipped silently.\n\n" +
                "Warning: Bypassing this message is not intended for end users — it appearing " +
                "in the first place indicates a problem with your game's file setup. " +
                "Only use this patch if you know what you are doing.",
                DialogMessageType.Information);
        }

        private string? RequireExePath()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return null;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            if (!File.Exists(exePath))
            {
                _dialogService.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogMessageType.Error);
                return null;
            }

            return exePath;
        }

        private void SetUnlockedConfigsState(string status, System.Windows.Media.Brush foreground, bool isPatchEnabled, bool isUnpatchEnabled)
        {
            if (_session.IsProcessingGameDirectory)
            {
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _unlockedConfigs.UnlockedConfigsStatus = status;
                _unlockedConfigs.UnlockedConfigsStatusForeground = foreground;
                _unlockedConfigs.IsPatchConfigsEnabled = isPatchEnabled;
                _unlockedConfigs.IsUnpatchConfigsEnabled = isUnpatchEnabled;
            });
        }

        public void RefreshUnlockedConfigs()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                return;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                SetUnlockedConfigsState("N/A (EXE not found)", System.Windows.Media.Brushes.Gray, false, false);
                return;
            }

            try { ApplyUnlockedConfigsState(ConfigUnlockHelper.GetState(exePath)); }
            catch (Exception ex)
            {
                SetUnlockedConfigsState("Error reading EXE", System.Windows.Media.Brushes.Red, false, false);
                _gameStatus.Status = $"Error checking config patch status: {ex.Message}";
            }
        }

        public async Task RefreshUnlockedConfigsAsync()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                return;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                SetUnlockedConfigsState("N/A (EXE not found)", System.Windows.Media.Brushes.Gray, false, false);
                return;
            }

            try { ApplyUnlockedConfigsState(await Task.Run(() => ConfigUnlockHelper.GetState(exePath))); }
            catch (Exception ex)
            {
                SetUnlockedConfigsState("Error reading EXE", System.Windows.Media.Brushes.Red, false, false);
                _gameStatus.Status = $"Error checking config patch status: {ex.Message}";
            }
        }

        private void ApplyUnlockedConfigsState(ConfigUnlockState state)
        {
            switch (state)
            {
                case ConfigUnlockState.Patched:
                    SetUnlockedConfigsState("Patched", System.Windows.Media.Brushes.Green, true, true);
                    break;
                case ConfigUnlockState.Unpatched:
                    SetUnlockedConfigsState("Unpatched", System.Windows.Media.Brushes.Gray, true, true);
                    break;
                case ConfigUnlockState.Mixed:
                    SetUnlockedConfigsState("Partially Patched", System.Windows.Media.Brushes.DarkOrange, true, true);
                    break;
                default:
                    SetUnlockedConfigsState("Not Applicable", System.Windows.Media.Brushes.Gray, false, false);
                    break;
            }
        }

        [RelayCommand]
        private Task PatchConfigsAsync() => ModifyExeConfigPatchAsync(unlock: true);

        [RelayCommand]
        private Task UnpatchConfigsAsync() => ModifyExeConfigPatchAsync(unlock: false);

        private Task ModifyExeConfigPatchAsync(bool unlock) => RunApplyAsync(() => ModifyExeConfigPatchCoreAsync(unlock));

        private async Task ModifyExeConfigPatchCoreAsync(bool unlock)
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                _dialogService.ShowMessage("Error", "MirrorsEdge.exe not found.", DialogMessageType.Error);
                return;
            }

            ConfigUnlockState currentState;
            try
            {
                currentState = ConfigUnlockHelper.GetState(exePath);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to read the executable state: {ex.Message}", DialogMessageType.Error);
                return;
            }

            if (currentState == ConfigUnlockState.NotApplicable)
            {
                _dialogService.ShowMessage("Not Applicable", "Config patching is not applicable for this executable.", DialogMessageType.Warning);
                return;
            }

            if (unlock && currentState == ConfigUnlockState.Patched)
            {
                _dialogService.ShowMessage("No Action Needed", "Configs are already patched.", DialogMessageType.Information);
                return;
            }

            if (!unlock && currentState == ConfigUnlockState.Unpatched)
            {
                _dialogService.ShowMessage("No Action Needed", "Configs are already unpatched.", DialogMessageType.Information);
                return;
            }

            _gameStatus.IsUiEnabled = false;
            string actionText = unlock ? "Patching" : "Unpatching";
            ShowProgress($"{actionText} configs...", true);

            try
            {
                bool patchChanged = await Task.Run(() =>
                {
                    _session.Package?.Dispose();
                    _session.TdGamePackage?.Dispose();
                    _session.Package = null;
                    _session.TdGamePackage = null;

                    return unlock
                        ? ConfigUnlockHelper.Unlock(exePath)
                        : ConfigUnlockHelper.RestoreStock(exePath);
                });

                HideProgress();

                if (patchChanged)
                {
                    RefreshUnlockedConfigs();
                    string status = unlock ? "patched" : "unpatched";
                    await _dialogService.ShowMessageAsync("Success", $"Unlocked configs {status}.", DialogMessageType.Success);
                }
                else
                {
                    await _dialogService.ShowMessageAsync("No Action Needed", "The executable is already in the desired config-unlock state.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                await _dialogService.ShowMessageAsync("Error", $"Failed to patch the executable:\n\n{ex.Message}", DialogMessageType.Error);
            }
            finally
            {
                HideProgress();
                _gameStatus.IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private void ShowUnlockedConfigsInfo()
        {
            _dialogService.ShowMessage("Unlocked Configs Information",
                "Bypasses the \"corrupted config\" error that prevents the game from launching when config files in the game directory have been modified " +
                "(e.g. removing streak effects, adding custom maps, removing the startup wait period).\n\n" +
                "Achieves the same result as the MEMLA tool, except the executable is patched directly.",
                DialogMessageType.Information);
        }
    }
}

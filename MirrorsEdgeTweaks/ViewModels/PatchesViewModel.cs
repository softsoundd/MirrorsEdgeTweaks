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


        // Name completes sentences such as "Could not apply the {Name}."
        // IsApplied returns null when the patch's state cannot be read, such as in an exe of an unknown version.
        private sealed record ExePatch(string Name, Func<string, bool?> IsApplied, Action<string> Apply, Action<string> Remove);

        private static readonly ExePatch LoggingPatch = new("logging patch",
            exe => LoggingPatchHelper.GetPatchState(exe) switch
            {
                LoggingPatchState.Patched => true,
                LoggingPatchState.Unpatched => false,
                _ => null,
            },
            LoggingPatchHelper.ApplyPatch,
            LoggingPatchHelper.RemovePatch);

        private static readonly ExePatch MultiInstancePatch = InlinePatch("multi-instance patch", InlineVaPatchHelper.MultiInstanceKey);

        private static readonly ExePatch AmbiguousBypassPatch = InlinePatch("ambiguous bypass patch", InlineVaPatchHelper.AmbiguousPackageKey);

        private static ExePatch InlinePatch(string name, string patchKey) => new(name,
            exe => InlineVaPatchHelper.GetPatchState(exe, patchKey) switch
            {
                InlinePatchState.Patched => true,
                InlinePatchState.Unpatched => false,
                _ => null,
            },
            exe => InlineVaPatchHelper.ApplyPatch(exe, patchKey),
            exe => InlineVaPatchHelper.RemovePatch(exe, patchKey));

        // The status is read back from MirrorsEdge.exe afterwards, so it shows what the file holds even when the change fails.
        private async Task ChangeExePatchAsync(ExePatch patch, bool apply, Func<Task> refreshStatusAsync)
        {
            string? exePath = RequireExePath();
            if (exePath == null) return;

            try
            {
                bool changed = false;
                bool ran = await RunBusyAsync(apply ? $"Applying {patch.Name}..." : $"Removing {patch.Name}...", () =>
                {
                    bool? applied = patch.IsApplied(exePath);
                    if (applied == apply)
                        return;

                    // Removing a patch that cannot be recognised would overwrite bytes that something else put there.
                    if (applied == null && !apply)
                        throw new UserFacingException(
                            "MirrorsEdge.exe is not a version that Mirror's Edge Tweaks recognises, or another tool has changed it, " +
                            $"so Tweaks cannot tell whether the {patch.Name} is applied and has left the file unchanged. " +
                            "Verifying your game files restores a stock MirrorsEdge.exe.");

                    if (apply)
                        patch.Apply(exePath);
                    else
                        patch.Remove(exePath);

                    if (patch.IsApplied(exePath) != apply)
                        throw new InvalidOperationException(apply
                            ? $"MirrorsEdge.exe did not hold the {patch.Name} after it was applied."
                            : $"MirrorsEdge.exe still held the {patch.Name} after it was removed.");
                    changed = true;
                });

                if (ran && changed)
                {
                    string name = char.ToUpperInvariant(patch.Name[0]) + patch.Name[1..];
                    _dialogService.ShowMessage("Success", $"{name} {(apply ? "applied" : "removed")}.", DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Could not {(apply ? "apply" : "remove")} the {patch.Name}.", ex);
            }
            finally
            {
                await refreshStatusAsync();
            }
        }

        [RelayCommand]
        private Task PatchLogging() => RunApplyAsync(() => ChangeExePatchAsync(LoggingPatch, apply: true, RefreshLoggingStatusAsync));

        [RelayCommand]
        private Task UnpatchLogging() => RunApplyAsync(() => ChangeExePatchAsync(LoggingPatch, apply: false, RefreshLoggingStatusAsync));

        [RelayCommand]
        private void ShowLoggingInfo()
        {
            _dialogService.ShowMessage("Enable Logging Information",
                "Restores Mirror's Edge's disabled logging system.\n\n" +
                "When patched, the game's log messages, including UnrealScript log() calls, are written to a log file " +
                "(and displayed in the log console window if the \"-LOG\" launch argument is used).\n\n" +
                "By default, the log file is created at \"Logs\\Launch.log\" next to the executable. " +
                "You can customise the log location using launch arguments:\n\n" +
                "\"-LOG=mylog.txt\" — write to a custom filename\n" +
                "\"-ABSLOG=C:\\...\" — write to an absolute path",
                DialogMessageType.Information);
        }


        [RelayCommand]
        private Task PatchMultiInstance() =>
            RunApplyAsync(() => ChangeExePatchAsync(MultiInstancePatch, apply: true, RefreshMultiInstanceStatusAsync));

        [RelayCommand]
        private Task UnpatchMultiInstance() =>
            RunApplyAsync(() => ChangeExePatchAsync(MultiInstancePatch, apply: false, RefreshMultiInstanceStatusAsync));

        [RelayCommand]
        private void ShowMultiInstanceInfo()
        {
            _dialogService.ShowMessage("Multi-instance Information",
                "Bypasses Mirror's Edge's single-instance restriction.\n\n" +
                "Useful for modders who need the editor open while launching the regular game instance.",
                DialogMessageType.Information);
        }


        [RelayCommand]
        private Task PatchAmbiguousBypass() =>
            RunApplyAsync(() => ChangeExePatchAsync(AmbiguousBypassPatch, apply: true, RefreshAmbiguousBypassStatusAsync));

        [RelayCommand]
        private Task UnpatchAmbiguousBypass() =>
            RunApplyAsync(() => ChangeExePatchAsync(AmbiguousBypassPatch, apply: false, RefreshAmbiguousBypassStatusAsync));

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
                _dialogService.ShowError("Could not check whether the configs are unlocked.", ex);
                return;
            }

            if (currentState == ConfigUnlockState.NotApplicable)
            {
                _dialogService.ShowMessage("Not Applicable", "Unlocked Configs is not available for this version of MirrorsEdge.exe.", DialogMessageType.Warning);
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
                bool patchChanged = await Task.Run(() => unlock
                    ? ConfigUnlockHelper.Unlock(exePath)
                    : ConfigUnlockHelper.RestoreStock(exePath));

                HideProgress();

                if (patchChanged)
                {
                    RefreshUnlockedConfigs();
                    string status = unlock ? "patched" : "unpatched";
                    await _dialogService.ShowMessageAsync("Success", $"Unlocked configs {status}.", DialogMessageType.Success);
                }
                else
                {
                    await _dialogService.ShowMessageAsync("No Action Needed",
                        unlock ? "Configs are already patched." : "Configs are already unpatched.",
                        DialogMessageType.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                await _dialogService.ShowErrorAsync(
                    unlock ? "Could not apply the Unlocked Configs patch." : "Could not remove the Unlocked Configs patch.", ex);
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

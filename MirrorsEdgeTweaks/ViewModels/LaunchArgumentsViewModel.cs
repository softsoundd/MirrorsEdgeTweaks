using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MirrorsEdgeTweaks.ViewModels
{
    // View model for the Launch Arguments section of the Game Tweaks tab: the entered arguments,
    // the executable command-line-unlock patch status, and the apply / reset / info commands. The
    // entered text is mirrored into GameSession.Config so it is persisted alongside the other
    // settings via IAppSettingsService.
    public partial class LaunchArgumentsViewModel : BusyViewModel
    {
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        private readonly IAppSettingsService _settings;
        private readonly GameSession _session;

        [ObservableProperty] private string _launchArguments = string.Empty;
        [ObservableProperty] private string _patchStatus = "N/A";
        [ObservableProperty] private Brush _patchStatusForeground = Brushes.Gray;

        public LaunchArgumentsViewModel(
            IDialogService dialogService,
            IFileService fileService,
            IAppSettingsService settings,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _fileService = fileService;
            _settings = settings;
            _session = session;
        }

        // Keep the shared config (the persistence source of truth) in sync with the entered text.
        partial void OnLaunchArgumentsChanged(string value) => _session.Config.LaunchArguments = value ?? string.Empty;

        private void SetPatchStatus(string status, Brush foreground)
        {
            PatchStatus = status;
            PatchStatusForeground = foreground;
        }

        public void RefreshPatchStatus()
        {
            if (_session.IsProcessingGameDirectory)
            {
                return;
            }

            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                SetPatchStatus("N/A (No game directory selected)", Brushes.Gray);
                return;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                SetPatchStatus("N/A (EXE not found)", Brushes.Gray);
                return;
            }

            try
            {
                CommandLineUnlockMode unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                switch (unlockMode)
                {
                    case CommandLineUnlockMode.PersistentFilePatch:
                        bool isUnlocked = CommandLineUnlockHelper.IsUnlocked(exePath);
                        SetPatchStatus(
                            isUnlocked ? "Patched - Command line arguments are unlocked" : "Unpatched - Command line arguments are locked",
                            isUnlocked ? Brushes.Green : Brushes.Gray);
                        break;

                    default:
                        SetPatchStatus("Unsupported executable", Brushes.Red);
                        break;
                }
            }
            catch
            {
                SetPatchStatus("Error reading executable", Brushes.Red);
            }
        }

        public async Task RefreshPatchStatusAsync()
        {
            if (_session.IsProcessingGameDirectory)
            {
                return;
            }

            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                SetPatchStatus("N/A (No game directory selected)", Brushes.Gray);
                return;
            }

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            if (!_fileService.FileExists(exePath))
            {
                SetPatchStatus("N/A (EXE not found)", Brushes.Gray);
                return;
            }

            try
            {
                (CommandLineUnlockMode unlockMode, bool isUnlocked) = await Task.Run(() =>
                {
                    CommandLineUnlockMode mode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                    bool unlocked = mode == CommandLineUnlockMode.PersistentFilePatch && CommandLineUnlockHelper.IsUnlocked(exePath);
                    return (mode, unlocked);
                });

                switch (unlockMode)
                {
                    case CommandLineUnlockMode.PersistentFilePatch:
                        SetPatchStatus(
                            isUnlocked ? "Patched - Command line arguments are unlocked" : "Unpatched - Command line arguments are locked",
                            isUnlocked ? Brushes.Green : Brushes.Gray);
                        break;

                    default:
                        SetPatchStatus("Unsupported executable", Brushes.Red);
                        break;
                }
            }
            catch
            {
                SetPatchStatus("Error reading executable", Brushes.Red);
            }
        }

        // Sets a transient "checking" patch status while the game directory is loading.
        public void SetChecking() => SetPatchStatus("Checking executable...", Brushes.Gray);

        [RelayCommand]
        private void ShowLaunchArgumentsInfo()
        {
            _dialogService.ShowMessage("Launch Arguments Information",
                "Patches the executable to unlock full command line handling in Mirror's Edge. " +
                "Arguments can be entered here, or they can be added to your game library's launch options/other shortcuts.\n\n" +
                "If entering arguments in Mirror's Edge Tweaks, use the 'Launch Game w/ Args' button at the top of the window to start the game with the entered arguments.\n\n" +
                "The majority of arguments should be prefixed with a '-' character. Only URL-specific arguments do not require the '-' prefix, such as commands to load a specific map upon startup. " +
                "Multiple arguments must be separated by a space.\n\n" +
                "Refer to Unreal Engine 3 documentation for available stock command line arguments: https://docs.unrealengine.com/udk/Three/CommandLineArguments.html",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private async Task ResetLaunchArguments()
        {
            try
            {
                var gameDir = _session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                {
                    _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                    return;
                }

                string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    _dialogService.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogMessageType.Error);
                    return;
                }

                CommandLineUnlockMode unlockMode = CommandLineUnlockMode.Unsupported;
                bool patchWasRemoved = false;

                await RunBusyAsync("Resetting launch arguments...", () =>
                {
                    unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                    patchWasRemoved = unlockMode == CommandLineUnlockMode.PersistentFilePatch &&
                        CommandLineUnlockHelper.RestoreStock(exePath);
                });

                LaunchArguments = string.Empty;
                _settings.Save();
                await RefreshPatchStatusAsync();

                string message = unlockMode switch
                {
                    CommandLineUnlockMode.PersistentFilePatch when patchWasRemoved =>
                        "The command line unlock patch has been removed and the saved launch arguments were cleared.",
                    CommandLineUnlockMode.PersistentFilePatch =>
                        "The executable is already using the stock command line behavior. Saved launch arguments were cleared.",
                    _ =>
                        "Saved launch arguments were cleared."
                };

                _dialogService.ShowMessage(
                    patchWasRemoved ? "Success" : "Information",
                    message,
                    patchWasRemoved ? DialogMessageType.Success : DialogMessageType.Information);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                _dialogService.ShowMessage("EA App License Required", ooaEx.Message, DialogMessageType.Warning);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to reset launch arguments: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task ApplyLaunchArguments()
        {
            try
            {
                var gameDir = _session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                {
                    _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                    return;
                }

                string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                if (!File.Exists(exePath))
                {
                    _dialogService.ShowMessage("Error", $"Game executable not found at: {exePath}", DialogMessageType.Error);
                    return;
                }

                string launchArguments = (LaunchArguments ?? string.Empty).Trim();

                CommandLineUnlockMode unlockMode = CommandLineUnlockMode.Unsupported;
                bool patchWasApplied = false;

                // The unlock patch reads and rewrites the (large) executable; run it off the UI thread.
                await RunBusyAsync("Applying launch arguments...", () =>
                {
                    unlockMode = CommandLineUnlockHelper.GetUnlockMode(exePath);
                    if (unlockMode == CommandLineUnlockMode.PersistentFilePatch)
                    {
                        patchWasApplied = CommandLineUnlockHelper.Unlock(exePath);
                    }
                });

                if (unlockMode != CommandLineUnlockMode.PersistentFilePatch)
                {
                    throw new InvalidOperationException("This executable version does not support command line unlocking.");
                }

                string unlockMessage = patchWasApplied
                    ? "Command line arguments are now unlocked in the executable."
                    : "Command line arguments are already unlocked in the executable.";

                LaunchArguments = launchArguments;
                _settings.Save();
                await RefreshPatchStatusAsync();

                string argumentsMessage = string.IsNullOrEmpty(launchArguments)
                    ? "No launch arguments were saved. You can add them later, or add them to your game library's launch options/other shortcuts."
                    : $"Saved launch arguments: {launchArguments}\n\nUse 'Launch Game w/ Args' to start the game with them.";

                _dialogService.ShowMessage(
                    "Success",
                    unlockMessage +
                    "\n\n" +
                    argumentsMessage,
                    DialogMessageType.Success);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                _dialogService.ShowMessage("EA App License Required", ooaEx.Message, DialogMessageType.Warning);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply launch arguments: {ex.Message}", DialogMessageType.Error);
            }
        }
    }
}

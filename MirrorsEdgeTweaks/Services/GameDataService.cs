using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.ViewModels;
using System.Globalization;
using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Services
{
    public interface IGameDataService
    {
        event Action? PackagesReloaded;

        // Intermediate reloads pass notify: false, which skips PackagesReloaded until the final reload.
        void LoadPackages(bool notify = true);

        Task ReloadPackagesAsync(bool notify = true);
        void UpdateConsoleStatus();
    }

    // Loads the Unreal packages and finds the offsets that FOV, aspect ratio and developer console editing use.
    // It raises PackagesReloaded instead of refreshing the feature view models, so it does not depend on them.
    public class GameDataService : IGameDataService
    {
        private readonly IOffsetFinderService _offsetFinder;
        private readonly IFileService _fileService;
        private readonly IDialogService _dialogService;
        private readonly GameSession _session;
        private readonly GameStatusViewModel _gameStatus;
        private readonly ConsoleViewModel _console;

        public event Action? PackagesReloaded;

        public GameDataService(
            IOffsetFinderService offsetFinder,
            IFileService fileService,
            IDialogService dialogService,
            GameSession session,
            GameStatusViewModel gameStatus,
            ConsoleViewModel console)
        {
            _offsetFinder = offsetFinder;
            _fileService = fileService;
            _dialogService = dialogService;
            _session = session;
            _gameStatus = gameStatus;
            _console = console;
        }

        private static void Dispatch(Action action)
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        public async Task ReloadPackagesAsync(bool notify = true)
        {
            await Task.Delay(500);
            await Task.Run(() => LoadPackages(notify));
        }

        // The PackagesReloaded handlers run inside this method's read scope, so their status checks reuse these loads.
        public void LoadPackages(bool notify = true)
        {
            var config = _session.Config;
            if (string.IsNullOrEmpty(config.GameDirectoryPath)) return;

            using var readScope = GameFileReadScope.Begin();
            _session.PackagesLoaded = false;
            PackageLease? engineLease = null;
            PackageLease? tdGameLease = null;
            try
            {
                Dispatch(() =>
                {
                    _gameStatus.IsGameTweaksEnabled = false;
                    _gameStatus.Status = "Loading packages...";
                });

                config.EnginePackagePath = Path.Combine(config.GameDirectoryPath, "TdGame", "CookedPC", "Engine.u");
                config.TdGamePackagePath = Path.Combine(config.GameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");

                engineLease = TryOpenPackage(config.EnginePackagePath);
                tdGameLease = TryOpenPackage(config.TdGamePackagePath);

                if (engineLease == null || tdGameLease == null)
                {
                    Dispatch(() =>
                    {
                        _dialogService.ShowMessage("Error", "Engine.u or TdGame.u could not be loaded. Verify your game files and try again.", DialogMessageType.Error);
                        _gameStatus.Status = "Failed to load packages.";
                    });
                    return;
                }

                _session.PackagesLoaded = true;
                SetupEditors(engineLease.Package, tdGameLease.Package);
                if (notify)
                    PackagesReloaded?.Invoke();
            }
            catch (Exception ex)
            {
                Dispatch(() =>
                {
                    _dialogService.ShowError("Could not load the game data.", ex);
                    _gameStatus.Status = "Error loading packages.";
                });
                _session.PackagesLoaded = false;
            }
            finally
            {
                engineLease?.Dispose();
                tdGameLease?.Dispose();
            }
        }

        private static PackageLease? TryOpenPackage(string path)
        {
            try
            {
                return GameFileReadScope.OpenPackage(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void SetupEditors(UnrealPackage enginePackage, UnrealPackage tdGamePackage)
        {
            bool fovSuccess = SetupFovEditor(enginePackage, tdGamePackage);
            bool arSuccess = SetupAspectRatioEditor(enginePackage);
            bool consoleSuccess = SetupConsoleEditor(enginePackage);

            Dispatch(() => _gameStatus.IsGameTweaksEnabled = fovSuccess || arSuccess || consoleSuccess);
        }

        private bool SetupFovEditor(UnrealPackage package, UnrealPackage tdGamePackage)
        {
            var offsets = _session.Offsets;
            var config = _session.Config;

            offsets.PlayerControllerDefaultFovOffset = -1;
            offsets.PlayerControllerDesiredFovOffset = -1;
            offsets.PlayerControllerFovAngleOffset = -1;
            offsets.CameraFovOffset = -1;
            offsets.CameraActorFovAngleOffset = -1;
            offsets.SeqActCameraFovOffset = -1;
            offsets.UnzoomFovRateOffset = -1;
            offsets.TdMoveVertigoZoomFovOffset = -1;
            offsets.TdMoveVertigoZoomFovFlagsOffset = -1;
            offsets.NearClippingPlaneOffset = -1;
            offsets.FovScaleMultiplierOffset = -1;

            _session.DetectedCameraFov = null;

            var playerControllerClass = package.FindObject<UClass>("PlayerController");
            if (playerControllerClass?.Default is UObject playerControllerCDO)
            {
                playerControllerCDO.Load<UObjectRecordStream>();
                var defaultFovProp = playerControllerCDO.Properties.FirstOrDefault(p => p.Name == "DefaultFOV");
                if (defaultFovProp != null && float.TryParse(defaultFovProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentDefaultFov))
                    offsets.PlayerControllerDefaultFovOffset = _offsetFinder.FindPropertyOffsetByName(playerControllerCDO, "DefaultFOV", currentDefaultFov, package, config.EnginePackagePath);

                var desiredFovProp = playerControllerCDO.Properties.FirstOrDefault(p => p.Name == "DesiredFOV");
                if (desiredFovProp != null && float.TryParse(desiredFovProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentDesiredFov))
                    offsets.PlayerControllerDesiredFovOffset = _offsetFinder.FindPropertyOffsetByName(playerControllerCDO, "DesiredFOV", currentDesiredFov, package, config.EnginePackagePath);

                var fovAngleProp = playerControllerCDO.Properties.FirstOrDefault(p => p.Name == "FOVAngle");
                if (fovAngleProp != null && float.TryParse(fovAngleProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentFovAngle))
                    offsets.PlayerControllerFovAngleOffset = _offsetFinder.FindPropertyOffsetByName(playerControllerCDO, "FOVAngle", currentFovAngle, package, config.EnginePackagePath);
            }

            var cameraClass = package.FindObject<UClass>("Camera");
            if (cameraClass?.Default is UObject cameraCDO)
            {
                cameraCDO.Load<UObjectRecordStream>();
                var fovProperty = cameraCDO.Properties.FirstOrDefault(p => p.Name == "DefaultFOV");
                if (fovProperty != null && float.TryParse(fovProperty.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentFov))
                {
                    _session.DetectedCameraFov = currentFov;
                    offsets.CameraFovOffset = _offsetFinder.FindPropertyOffsetByName(cameraCDO, "DefaultFOV", currentFov, package, config.EnginePackagePath);
                }
            }

            var cameraActorClass = package.FindObject<UClass>("CameraActor");
            if (cameraActorClass?.Default is UObject cameraActorCDO)
            {
                cameraActorCDO.Load<UObjectRecordStream>();
                var fovAngleProperty = cameraActorCDO.Properties.FirstOrDefault(p => p.Name == "FOVAngle");
                if (fovAngleProperty != null && float.TryParse(fovAngleProperty.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentFovAngle))
                    offsets.CameraActorFovAngleOffset = _offsetFinder.FindPropertyOffsetByName(cameraActorCDO, "FOVAngle", currentFovAngle, package, config.EnginePackagePath);
            }

            var seqActClass = tdGamePackage.FindObject<UClass>("SeqAct_TdCameraFOV");
            if (seqActClass?.Default is UObject seqActCDO)
            {
                seqActCDO.Load<UObjectRecordStream>();
                var newFovProp = seqActCDO.Properties.FirstOrDefault(p => p.Name == "NewFOV");
                if (newFovProp != null && float.TryParse(newFovProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentNewFov))
                    offsets.SeqActCameraFovOffset = _offsetFinder.FindPropertyOffsetByName(seqActCDO, "NewFOV", currentNewFov, tdGamePackage, config.TdGamePackagePath);
            }

            var tdMoveVertigoClass = tdGamePackage.FindObject<UClass>("TdMove_Vertigo");
            if (tdMoveVertigoClass != null)
            {
                var zoomFovProp = tdMoveVertigoClass.EnumerateFields<UProperty>().FirstOrDefault(p => p.Name == "ZoomFOV");
                if (zoomFovProp != null)
                {
                    if (tdMoveVertigoClass.Default is UObject tdMoveVertigoCDO)
                    {
                        tdMoveVertigoCDO.Load<UObjectRecordStream>();
                        var zoomFovDefaultProp = tdMoveVertigoCDO.Properties.FirstOrDefault(p => p.Name == "ZoomFOV");
                        if (zoomFovDefaultProp != null && float.TryParse(zoomFovDefaultProp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float currentZoomFov))
                            offsets.TdMoveVertigoZoomFovOffset = _offsetFinder.FindPropertyOffsetByName(tdMoveVertigoCDO, "ZoomFOV", currentZoomFov, tdGamePackage, config.TdGamePackagePath);
                    }
                    offsets.TdMoveVertigoZoomFovFlagsOffset = _offsetFinder.FindPropertyFlagsOffset(zoomFovProp, tdGamePackage, config.TdGamePackagePath);
                }
            }

            var tdPlayerControllerClass = tdGamePackage.FindObject<UClass>("TdPlayerController");
            if (tdPlayerControllerClass != null)
            {
                var unzoomFunc = tdPlayerControllerClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UnZoom");
                var fovZoomRateProp = tdPlayerControllerClass.EnumerateFields<UProperty>().FirstOrDefault(p => p.Name == "FOVZoomRate");
                if (unzoomFunc != null && fovZoomRateProp != null)
                {
                    unzoomFunc.Load<UObjectRecordStream>();
                    offsets.UnzoomFovRateOffset = _offsetFinder.FindFloatOffsetInBytecode(unzoomFunc, fovZoomRateProp);
                }
            }

            var tdHudClass = tdGamePackage.FindObject<UClass>("TdHUD");
            if (tdHudClass != null)
            {
                var toggleZoomStateFunc = tdHudClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "ToggleZoomState");
                if (toggleZoomStateFunc != null)
                {
                    toggleZoomStateFunc.Load<UObjectRecordStream>();
                    offsets.NearClippingPlaneOffset = _offsetFinder.FindClippingPlaneOffset(toggleZoomStateFunc);
                }
            }

            var tdPlayerInputClass = tdGamePackage.FindObject<UClass>("TdPlayerInput");
            if (tdPlayerInputClass != null)
            {
                var playerInputFunc = tdPlayerInputClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "PlayerInput");
                if (playerInputFunc != null)
                {
                    playerInputFunc.Load<UObjectRecordStream>();
                    offsets.FovScaleMultiplierOffset = _offsetFinder.FindFovScaleMultiplierOffset(playerInputFunc);
                }
            }

            return offsets.PlayerControllerDefaultFovOffset != -1 ||
                offsets.PlayerControllerDesiredFovOffset != -1 ||
                offsets.PlayerControllerFovAngleOffset != -1 ||
                offsets.CameraFovOffset != -1 ||
                offsets.CameraActorFovAngleOffset != -1 ||
                offsets.SeqActCameraFovOffset != -1 ||
                offsets.UnzoomFovRateOffset != -1 ||
                offsets.TdMoveVertigoZoomFovOffset != -1 ||
                offsets.NearClippingPlaneOffset != -1 ||
                offsets.FovScaleMultiplierOffset != -1;
        }

        private bool SetupAspectRatioEditor(UnrealPackage package)
        {
            var offsets = _session.Offsets;

            offsets.AspectRatioOffset = -1;

            var cameraClass = package.FindObject<UClass>("Camera");
            if (cameraClass != null)
            {
                var updateCameraFunc = cameraClass.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UpdateCamera");
                var aspectRatioProperty = cameraClass.EnumerateFields<UProperty>().FirstOrDefault(p => p.Name == "ConstrainedAspectRatio");

                if (updateCameraFunc != null && aspectRatioProperty != null)
                {
                    updateCameraFunc.Load<UObjectRecordStream>();
                    offsets.AspectRatioOffset = _offsetFinder.FindFloatOffsetInBytecode(updateCameraFunc, aspectRatioProperty);
                    return offsets.AspectRatioOffset != -1;
                }
            }
            return false;
        }

        private bool SetupConsoleEditor(UnrealPackage package)
        {
            var offsets = _session.Offsets;

            offsets.ConsoleHeightOffset = -1;
            Dispatch(() =>
            {
                _console.IsInstallConsoleEnabled = false;
                _console.IsUninstallConsoleEnabled = false;
            });

            var consoleClass = package.FindObject<UClass>("Console");
            var openState = consoleClass?.EnumerateFields<UState>().FirstOrDefault(s => s.Name == "Open");
            var postRenderFunc = openState?.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "PostRender_Console");

            if (postRenderFunc != null)
            {
                postRenderFunc.Load<UObjectRecordStream>();
                offsets.ConsoleHeightOffset = _offsetFinder.FindConsoleHeightOffset(postRenderFunc);
            }

            UpdateConsoleStatus();

            if (offsets.ConsoleHeightOffset != -1)
            {
                Dispatch(() =>
                {
                    _console.IsInstallConsoleEnabled = true;
                    _console.IsUninstallConsoleEnabled = true;
                });
                return true;
            }

            Dispatch(() => _console.ConsoleStatus = "(Status: Offset not found)");
            return false;
        }

        public void UpdateConsoleStatus()
        {
            if (_session.IsProcessingGameDirectory)
            {
                return;
            }

            var offsets = _session.Offsets;
            var config = _session.Config;
            if (!_session.PackagesLoaded || string.IsNullOrEmpty(config.GameDirectoryPath)) return;

            string consoleFilePath = Path.Combine(config.GameDirectoryPath, "TdGame", "CookedPC", "MirrorsEdgeConsole.u");
            bool fileExists = _fileService.FileExists(consoleFilePath);

            bool heightModified = false;
            if (offsets.ConsoleHeightOffset != -1 && config.EnginePackagePath != null)
            {
                float currentHeightMultiplier = _offsetFinder.ReadFloatFromFile(config.EnginePackagePath, offsets.ConsoleHeightOffset);

                if (Math.Abs(currentHeightMultiplier - 0.4f) < 0.001f)
                {
                    heightModified = true;
                }
            }

            bool configModified = false;
            try
            {
                string tdEngineIniPath = UserTdGamePathHelper.GetTdEngineIniPath(config);
                string tdInputIniPath = UserTdGamePathHelper.GetTdInputIniPath(config);

                if (_fileService.FileExists(tdEngineIniPath) && _fileService.FileExists(tdInputIniPath))
                {
                    string? consoleClassName = ConfigFileHelper.ReadIniValue(tdEngineIniPath, "Engine.Engine", "ConsoleClassName");
                    string? typeKey = ConfigFileHelper.ReadIniValue(tdInputIniPath, "Engine.Console", "TypeKey");

                    if (!string.IsNullOrEmpty(consoleClassName) && consoleClassName.Equals("MirrorsEdgeConsole.MirrorsEdgeConsole", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(typeKey) && typeKey.Equals("Tab", StringComparison.OrdinalIgnoreCase))
                    {
                        configModified = true;
                    }
                }
            }
            catch
            {
            }

            if (fileExists && heightModified && configModified)
            {
                Dispatch(() =>
                {
                    _console.ConsoleStatus = "Installed";
                    _console.ConsoleStatusForeground = System.Windows.Media.Brushes.Green;
                });
            }
            else if (fileExists || heightModified || configModified)
            {
                Dispatch(() =>
                {
                    _console.ConsoleStatus = "Partially Installed";
                    _console.ConsoleStatusForeground = System.Windows.Media.Brushes.Orange;
                });
            }
            else
            {
                Dispatch(() =>
                {
                    _console.ConsoleStatus = "Not Installed";
                    _console.ConsoleStatusForeground = System.Windows.Media.Brushes.Gray;
                });
            }
        }
    }
}

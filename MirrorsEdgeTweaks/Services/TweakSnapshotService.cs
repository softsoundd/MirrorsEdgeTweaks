using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.ViewModels;
using System.Globalization;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface ITweakSnapshotService
    {
        TweakSnapshot Capture(LegacyChangeReport? legacy = null);
        Task<TweakReapplyResult> ReapplyAsync(TweakSnapshot snapshot, TweakScope scope);
    }

    // Carries the user's tweaks across a replacement of game files.
    public class TweakSnapshotService : ITweakSnapshotService
    {
        private const float InstalledConsoleHeight = 0.4f;

        private readonly GameSession _session;
        private readonly IGameDataService _gameData;
        private readonly IPackageService _packageService;
        private readonly IDecompressionService _decompressionService;
        private readonly IUIScalingService _uiScaling;
        private readonly GraphicsTweaksViewModel _graphics;

        public TweakSnapshotService(
            GameSession session,
            IGameDataService gameData,
            IPackageService packageService,
            IDecompressionService decompressionService,
            IUIScalingService uiScaling,
            GraphicsTweaksViewModel graphics)
        {
            _session = session;
            _gameData = gameData;
            _packageService = packageService;
            _decompressionService = decompressionService;
            _uiScaling = uiScaling;
            _graphics = graphics;
        }

        // Reads the game files, because the view models are not loaded yet when the startup restore runs.
        // Findings from older releases add the tweaks that the current state detection cannot see.
        public TweakSnapshot Capture(LegacyChangeReport? legacy = null)
        {
            var config = _session.Config;
            string? gameDir = config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
                return new TweakSnapshot();

            string cookedPc = Path.Combine(gameDir, "TdGame", "CookedPC");
            string enginePath = Path.Combine(cookedPc, "Engine.u");
            string tdGamePath = Path.Combine(cookedPc, "TdGame.u");
            var intent = legacy?.Intent ?? LegacyIntent.None;

            var snapshot = new TweakSnapshot
            {
                UniformSensitivityTarget = UniformSensitivityPatcher.ReadIsEnabled(tdGamePath) switch
                {
                    true => UniformSensitivityPatcher.EnabledValue,
                    false => UniformSensitivityPatcher.DisabledValue,
                    null => null,
                },
                GamepadButtonType = GamepadButtonPatcher.ReadIsPs3(gameDir) == true ? "ps3" : "xbox",
                PhysXFps = PhysXTimingPatcher.Read(gameDir),
                ConsoleInstalled = File.Exists(Path.Combine(cookedPc, "MirrorsEdgeConsole.u")),
                HighResFix = _uiScaling.IsUIScalingActive(gameDir) || intent.HasFlag(LegacyIntent.HighResFix),
            };
            (snapshot.HasLoadLastCheckpointKeybind, snapshot.HasRestartTimeTrialKeybind) = ReadExecKeybinds(config);

            try
            {
                var state = TdGamePatcher.DetectState(tdGamePath);
                snapshot.SensEnabled = state.SensApplied || intent.HasFlag(LegacyIntent.FovAgnosticSens);
                snapshot.ClipEnabled = state.ClipApplied || intent.HasFlag(LegacyIntent.CompensatedClip);
                snapshot.OnlineSkipEnabled = state.OnlineSkipApplied;
                snapshot.TdGamePatched = state.CoreApplied || snapshot.SensEnabled || snapshot.ClipEnabled
                    || snapshot.OnlineSkipEnabled || intent.HasFlag(LegacyIntent.Fov);
            }
            catch { }

            try
            {
                if (intent.HasFlag(LegacyIntent.Fov) || EnginePatcher.DetectState(enginePath) != EnginePatchState.Unpatched)
                    snapshot.Fov = ParseFov(config.Fov) ?? ReadCameraFov(enginePath);
            }
            catch { }

            return snapshot;
        }

        // Disposes the loaded packages, so the caller must reload them afterwards.
        public async Task<TweakReapplyResult> ReapplyAsync(TweakSnapshot snapshot, TweakScope scope)
        {
            var result = new TweakReapplyResult();
            var config = _session.Config;
            string gameDir = config.GameDirectoryPath
                ?? throw new InvalidOperationException("No game directory is selected.");
            string cookedPc = Path.Combine(gameDir, "TdGame", "CookedPC");
            string enginePath = Path.Combine(cookedPc, "Engine.u");
            string tdGamePath = Path.Combine(cookedPc, "TdGame.u");
            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");

            using var backupOperation = PatchUtility.BeginBackupOperation();
            DisposePackages();

            // The full prompt patch also swaps the controller image paths in TdGame.u.
            bool fullPrompts = scope.HasFlag(TweakScope.Localisation) && snapshot.GamepadButtonType == "ps3";

            if (scope.HasFlag(TweakScope.TdGame))
            {
                if (snapshot.TdGamePatched)
                    await Step(result, "FOV patches", () => TdGamePatcher.Reconcile(
                        tdGamePath, snapshot.SensEnabled, snapshot.ClipEnabled, snapshot.OnlineSkipEnabled));
                if (snapshot.UniformSensitivityTarget is { } target)
                    await Step(result, "Uniform sensitivity", () => UniformSensitivityPatcher.Apply(tdGamePath, target));
                if (snapshot.GamepadButtonType is { } buttonType && !fullPrompts)
                    await Step(result, "Gamepad prompt mode",
                        () => GamepadButtonPatcher.ApplyControllerImagePathSwap(tdGamePath, buttonType));
                if (snapshot.HasLoadLastCheckpointKeybind)
                    await StepAsync(result, "Load Last Checkpoint exec patch",
                        () => ExecFlagPatcher.AddExecFlag(gameDir, "TdSPGame", "RestartFromLastCheckpoint"));
                if (snapshot.HasRestartTimeTrialKeybind)
                    await StepAsync(result, "Restart Time Trial exec patch",
                        () => ExecFlagPatcher.AddExecFlag(gameDir, "TdTimeTrialHUD", "TriggerRestartRaceblink"));
            }

            if (fullPrompts)
                await Step(result, "Gamepad prompt mode", () => ApplyPs3Prompts(cookedPc));

            if (scope.HasFlag(TweakScope.Engine))
            {
                if (snapshot.Fov != null)
                    await Step(result, "FOV patches", () => EnginePatcher.Reconcile(enginePath));
                if (snapshot.PhysXFps is { } fps && PhysXTimingPatcher.Read(gameDir) != fps)
                    await Step(result, "PhysX simulation rate", () => PhysXTimingPatcher.Apply(gameDir, fps));
            }

            if (scope.HasFlag(TweakScope.Exe))
            {
                if (snapshot.Fov != null)
                    await Step(result, "FOV patches", () => RenderTargetPatcher.Reconcile(exePath));
                if (NeedsSupersampling(config, exePath))
                    await Step(result, "Supersampling", () => SupersamplePatchHelper.ApplyPatch(exePath));

                // The 1.1.0.0 exe disables the console's set commands.
                if (snapshot.ConsoleInstalled && SetCommandPatchHelper.IsDlcVersion(exePath))
                    await Step(result, "Developer console set commands",
                        () => SetCommandPatchHelper.EnsurePatchedIfApplicable(exePath));
            }

            if (snapshot.HighResFix)
            {
                await _graphics.ReapplyHighResUIFixIfNeededAsync(wasUIScalingActiveOverride: true, showDialogs: false);
                result.ReappliedSettings.Add("High-res UI fix");
            }

            // The splices above move the Engine.u defaults, so these writes use offsets from a fresh load.
            if (scope.HasFlag(TweakScope.Engine) && (snapshot.Fov != null || snapshot.ConsoleInstalled))
            {
                await Task.Run(() => _gameData.LoadPackages(notify: false));
                PackageOffsets offsets = _session.Offsets;
                DisposePackages();

                if (snapshot.Fov is { } fov)
                    await Step(result, "FOV patches", () => EngineFovWriter.Write(enginePath, offsets, fov));
                if (snapshot.ConsoleInstalled)
                    await Step(result, "Developer console height",
                        () => WriteConsoleHeight(enginePath, offsets.ConsoleHeightOffset));
            }

            List<string> distinctReapplied = result.ReappliedSettings.Distinct(StringComparer.Ordinal).ToList();
            result.ReappliedSettings.Clear();
            result.ReappliedSettings.AddRange(distinctReapplied);

            if (result.FailedSettings.Count == 0)
                backupOperation.Complete();

            return result;
        }

        private void DisposePackages()
        {
            _packageService.DisposePackage(_session.Package);
            _packageService.DisposePackage(_session.TdGamePackage);
            _session.Package = null;
            _session.TdGamePackage = null;
        }

        private static async Task Step(TweakReapplyResult result, string setting, Action apply) =>
            await StepAsync(result, setting, () => Task.Run(apply));

        private static async Task StepAsync(TweakReapplyResult result, string setting, Func<Task> apply)
        {
            try
            {
                await apply();
                result.ReappliedSettings.Add(setting);
            }
            catch (Exception ex)
            {
                result.FailedSettings.Add($"{setting} ({ex.Message})");
            }
        }

        // The prompt patch edits every installed language, and it can only edit decompressed packages.
        private void ApplyPs3Prompts(string cookedPc)
        {
            foreach (string tsLoc in Directory.GetFiles(cookedPc, "Ts_LOC_*.upk"))
                _decompressionService.RunDecompressor(tsLoc);
            GamepadButtonPatcher.ApplyButtonPatches(cookedPc, "ps3");
        }

        // Mirrors KeybindsViewModel.LoadCustomKeybinds, which reads the same bindings for the UI.
        private static (bool LoadLastCheckpoint, bool RestartTimeTrial) ReadExecKeybinds(GameConfiguration config)
        {
            bool inPlayerInput = false, loadLastCheckpoint = false, restartTimeTrial = false;
            try
            {
                string tdInputPath = UserTdGamePathHelper.GetTdInputIniPath(config);
                if (!File.Exists(tdInputPath))
                    return (false, false);

                foreach (string line in File.ReadLines(tdInputPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("["))
                    {
                        inPlayerInput = trimmed == "[Engine.PlayerInput]";
                        continue;
                    }
                    if (!inPlayerInput)
                        continue;

                    loadLastCheckpoint |= line.Contains("Command=\"RestartFromLastCheckpoint\"");
                    restartTimeTrial |= line.Contains("Command=\"TriggerRestartRaceblink\"");
                }
            }
            catch { }
            return (loadLastCheckpoint, restartTimeTrial);
        }

        // Accepts the range that the FOV tab accepts.
        private static float? ParseFov(string? fov) =>
            float.TryParse(fov, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && value >= 80 && value <= 179
                ? value
                : null;

        private static float? ReadCameraFov(string enginePath)
        {
            using var pkg = UePackageLocator.Load(enginePath);
            return UePackageLocator.ReadFloatDefault(pkg, "Camera", "DefaultFOV");
        }

        // Render resolutions above 100% need the supersampling patch.
        private static bool NeedsSupersampling(GameConfiguration config, string exePath)
        {
            string? percentage = ConfigFileHelper.ReadIniValue(
                UserTdGamePathHelper.GetTdEngineIniPath(config), "SystemSettings", "ScreenPercentage");
            return double.TryParse(percentage, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && value > 100
                && SupersamplePatchHelper.GetPatchState(exePath) == SupersamplePatchState.Unpatched;
        }

        private static void WriteConsoleHeight(string enginePath, long offset)
        {
            if (offset == -1)
                throw new InvalidOperationException("The console height was not found in Engine.u.");

            using var stream = new FileStream(enginePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.Position = offset;
            stream.Write(BitConverter.GetBytes(InstalledConsoleHeight));
        }
    }
}

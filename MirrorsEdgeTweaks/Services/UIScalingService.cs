using MirrorsEdgeTweaks.Helpers;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IUIScalingService
    {
        bool ShouldOfferUIScaling(int width);
        Task<bool> AskUserForUIScalingConfirmationAsync();
        Task ApplyUIScalingAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null, bool showDialogs = true);
        Task RollbackUIScalingToDefaultsAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null, bool showDialogs = true);
        bool IsUIScalingActive(string gameDirectoryPath);
    }

    public class UIScalingService : IUIScalingService
    {
        private readonly IDecompressionService _decompressionService;
        private readonly IDialogService _dialogService;

        public UIScalingService(IDecompressionService decompressionService, IDialogService dialogService)
        {
            _decompressionService = decompressionService;
            _dialogService = dialogService;
        }

        // Blurry UI only occurs above 1920 width
        public bool ShouldOfferUIScaling(int width) => width > 1920;

        public async Task<bool> AskUserForUIScalingConfirmationAsync()
        {
            return await _dialogService.ShowConfirmationAsync(
                "Fix UI and blurry text?",
                "Apply the dynamic high-resolution UI fix? This adjusts UI scaling to your resolution automatically, " +
                "including when you change resolution in-game, without needing to reapply it for each resolution.");
        }

        public async Task ApplyUIScalingAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null, bool showDialogs = true)
        {
            try
            {
                using var backupOperation = PatchUtility.BeginBackupOperation();

                string enginePath = EnginePath(gameDirectoryPath);
                string tdGamePath = TdGamePath(gameDirectoryPath);
                string exePath = ExePath(gameDirectoryPath);
                if (!File.Exists(enginePath))
                    throw new FileNotFoundException("Engine.u not found", enginePath);
                if (!File.Exists(exePath))
                    throw new FileNotFoundException("MirrorsEdge.exe not found", exePath);

                // The Engine.u subtitle blob issues "set" commands, which the 1.1.0.0 DLC exe disables.
                EnsureSetCommandEnabled(gameDirectoryPath);

                // The exe patch is the required core, so its failure fails the whole apply.
                MultiFontScalePatcher.Apply(exePath);

                // The remaining layers are best-effort, and the result dialog lists any that failed.
                var failedLayers = new List<string>();

                // The Engine.u and TdGame.u blobs size the subtitle region and the crosshair.
                if (!TryApplyScriptLayer(enginePath, tdGamePath))
                    failedLayers.Add("subtitle region");

                // SubtitleRegionPatcher stops subtitles vanishing above 16:9,
                // and the Engine.u subtitle region formula assumes that fix.
                if (!TryApplySubtitleFix(gameDirectoryPath))
                    failedLayers.Add("subtitle centring");

                // TODO: find a better approach - the cursor size is currently edited in every
                // Startup_* localisation package rather than in one shared place.
                if (!TryApplyCursorFix(gameDirectoryPath, height))
                    failedLayers.Add("cursor size");

                backupOperation.Complete();

                if (showDialogs)
                {
                    beforeShowingDialog?.Invoke();
                    string message = $"Resolution set to {width} × {height}.\nDynamic UI fix applied.";
                    if (failedLayers.Count > 0)
                        message += $"\n\nNote: the following optional layers could not be applied: {string.Join(", ", failedLayers)}.";
                    await _dialogService.ShowMessageAsync(
                        "Resolution Updated",
                        message,
                        failedLayers.Count == 0 ? DialogMessageType.Success : DialogMessageType.Warning);
                }
            }
            catch (Exception ex)
            {
                beforeShowingDialog?.Invoke();
                await _dialogService.ShowMessageAsync(
                    "UI Fix Failed",
                    $"The dynamic UI fix could not be applied:\n\n{ex.Message}",
                    DialogMessageType.Error);
            }
        }

        public async Task RollbackUIScalingToDefaultsAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null, bool showDialogs = true)
        {
            try
            {
                using var backupOperation = PatchUtility.BeginBackupOperation();

                string enginePath = EnginePath(gameDirectoryPath);
                string tdGamePath = TdGamePath(gameDirectoryPath);

                HighResUIDynamicPatcher.RemoveAll(enginePath, tdGamePath);
                TryRemoveExeFontHook(gameDirectoryPath);
                TryRemoveSubtitleFix(gameDirectoryPath);
                TryRemoveCursorFix(gameDirectoryPath);

                backupOperation.Complete();

                if (showDialogs)
                {
                    beforeShowingDialog?.Invoke();
                    await _dialogService.ShowMessageAsync(
                        "Resolution Updated",
                        $"Resolution set to {width} × {height}.\nDynamic UI fix removed (stock UI scaling restored).",
                        DialogMessageType.Success);
                }
            }
            catch (Exception ex)
            {
                beforeShowingDialog?.Invoke();
                await _dialogService.ShowMessageAsync(
                    "UI Fix Removal Failed",
                    $"The dynamic UI fix could not be fully removed:\n\n{ex.Message}",
                    DialogMessageType.Error);
            }
        }

        public bool IsUIScalingActive(string gameDirectoryPath)
        {
            try
            {
                return MultiFontScalePatcher.IsApplied(ExePath(gameDirectoryPath));
            }
            catch
            {
                return false;
            }
        }

        private static string EnginePath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "Engine.u");

        private static string TdGamePath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");

        private static string ExePath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "Binaries", "MirrorsEdge.exe");

        private static string CookedPcPath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");

        private static void EnsureSetCommandEnabled(string gameDirectoryPath)
        {
            try
            {
                string exePath = ExePath(gameDirectoryPath);
                if (File.Exists(exePath))
                    SetCommandPatchHelper.EnsurePatchedIfApplicable(exePath);
            }
            catch
            {
            }
        }

        // Returns false only when the subtitle blob fails, because ApplyAll swallows crosshair failures.
        private static bool TryApplyScriptLayer(string enginePath, string tdGamePath)
        {
            try
            {
                HighResUIDynamicPatcher.ApplyAll(enginePath, tdGamePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryRemoveExeFontHook(string gameDirectoryPath)
        {
            try
            {
                string exePath = ExePath(gameDirectoryPath);
                if (File.Exists(exePath))
                    MultiFontScalePatcher.Remove(exePath);
            }
            catch
            {
            }
        }

        private static bool TryApplySubtitleFix(string gameDirectoryPath)
        {
            try
            {
                string exePath = ExePath(gameDirectoryPath);
                if (File.Exists(exePath))
                    SubtitleRegionPatcher.Apply(exePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryRemoveSubtitleFix(string gameDirectoryPath)
        {
            try
            {
                string exePath = ExePath(gameDirectoryPath);
                if (File.Exists(exePath))
                    SubtitleRegionPatcher.Remove(exePath);
            }
            catch
            {
            }
        }

        private bool TryApplyCursorFix(string gameDirectoryPath, int height)
        {
            try
            {
                string cookedPc = CookedPcPath(gameDirectoryPath);
                if (!Directory.Exists(cookedPc)) return true;
                foreach (string startup in Directory.GetFiles(cookedPc, "Startup_*"))
                {
                    try { _decompressionService.RunDecompressor(startup); } catch { }
                }
                CursorScalePatcher.Apply(cookedPc, height);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryRemoveCursorFix(string gameDirectoryPath)
        {
            try { CursorScalePatcher.Remove(CookedPcPath(gameDirectoryPath)); }
            catch { }
        }
    }
}

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
                "Do you wish to fix the game's UI and blurry text at higher resolutions?\n\n" +
                "This installs a dynamic fix that adjusts the UI to your resolution automatically, " +
                "including when you change resolution in-game - no need to re-apply it per resolution.");
        }

        public async Task ApplyUIScalingAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null, bool showDialogs = true)
        {
            try
            {
                string enginePath = EnginePath(gameDirectoryPath);
                string tdGamePath = TdGamePath(gameDirectoryPath);
                if (!File.Exists(enginePath))
                    throw new FileNotFoundException("Engine.u not found", enginePath);

                // The runtime "set" commands the hooks issue require console set/setnopec, which EA
                // disabled in the 1.1.0.0 DLC exe - restore them so the fix works there too.
                EnsureSetCommandEnabled(gameDirectoryPath);

                // Script-package (Engine.u/TdGame.u) fix: lies the font ResolutionTestTable + sets
                // UIStyle_Text scale at runtime per resolution. This is the working high-res fix
                // (crisp/correct UI; soft-but-fine above 1080p).
                HighResUIDynamicPatcher.ApplyAll(enginePath, tdGamePath);

                // Layer the canvas/HUD text size fix on top: the UI fix doesn't cover canvas-drawn
                // HUD text (Canvas.DrawText), which stays small. This exe hook scales it to correct
                // size; console/debug text (plain UFont) is excluded.
                TryApplyCanvasTextFix(gameDirectoryPath);

                // Todo - better solution needed later: Mouse cursor is size currently edited across every Startup_* localisation
                TryApplyCursorFix(gameDirectoryPath, height);

                if (showDialogs)
                {
                    beforeShowingDialog?.Invoke();
                    await _dialogService.ShowMessageAsync(
                        "Resolution Updated",
                        $"Resolution set to {width} x {height}\nDynamic UI fix applied.",
                        DialogMessageType.Success);
                }
            }
            catch (Exception)
            {
            }
        }

        public async Task RollbackUIScalingToDefaultsAsync(int width, int height, string gameDirectoryPath, Action? beforeShowingDialog = null, bool showDialogs = true)
        {
            try
            {
                string enginePath = EnginePath(gameDirectoryPath);
                string tdGamePath = TdGamePath(gameDirectoryPath);

                HighResUIDynamicPatcher.RemoveAll(enginePath, tdGamePath);
                TryRemoveExeFontHook(gameDirectoryPath);
                TryRemoveCursorFix(gameDirectoryPath);

                if (showDialogs)
                {
                    beforeShowingDialog?.Invoke();
                    await _dialogService.ShowMessageAsync(
                        "Resolution Updated",
                        $"Resolution set to {width} x {height}\nDynamic UI fix removed (stock UI scaling restored).",
                        DialogMessageType.Success);
                }
            }
            catch (Exception)
            {
            }
        }

        public bool IsUIScalingActive(string gameDirectoryPath)
        {
            try
            {
                return HighResUIDynamicPatcher.IsActive(EnginePath(gameDirectoryPath));
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

        // canvas/HUD text size fix (exe DrawString hook)
        private static void TryApplyCanvasTextFix(string gameDirectoryPath)
        {
            try
            {
                string exePath = ExePath(gameDirectoryPath);
                if (File.Exists(exePath))
                    MultiFontScalePatcher.Apply(exePath);
            }
            catch
            {
            }
        }

        // Removal of the canvas/HUD text exe hook.
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

        // Cursor (Arrow texture) scaling across all Startup_* localisations
        private void TryApplyCursorFix(string gameDirectoryPath, int height)
        {
            try
            {
                string cookedPc = CookedPcPath(gameDirectoryPath);
                if (!Directory.Exists(cookedPc)) return;
                foreach (string startup in Directory.GetFiles(cookedPc, "Startup_*"))
                {
                    try { _decompressionService.RunDecompressor(startup); } catch { }
                }
                CursorScalePatcher.Apply(cookedPc, height);
            }
            catch { }
        }

        // Restore of the stock cursor size.
        private static void TryRemoveCursorFix(string gameDirectoryPath)
        {
            try { CursorScalePatcher.Remove(CookedPcPath(gameDirectoryPath)); }
            catch { }
        }
    }
}

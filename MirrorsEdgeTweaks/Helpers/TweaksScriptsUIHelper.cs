using System.IO;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class TweaksScriptsUIHelper
    {
        public const string StockRestoreBaseZip = "MirrorsEdgeTweaksScriptsUI_StockRestore_Base.zip";
        public const string StockRestoreDlcZip = "MirrorsEdgeTweaksScriptsUI_StockRestore_DLC.zip";

        public static string? GetStockRestoreZipFileName(GameConfiguration config) =>
            string.IsNullOrEmpty(config.GameDirectoryPath)
                ? null
                : GetStockRestoreZipFileName(config.GameDirectoryPath);

        public static string? GetStockRestoreZipFileName(string gameDirectoryPath) =>
            GetStockRestoreZipFileName(GameVersionHelper.GetGameVersion(gameDirectoryPath));

        public static string? GetStockRestoreZipFileName(GameVersion gameVersion)
        {
            if (!gameVersion.IsValid || string.IsNullOrEmpty(gameVersion.Version))
            {
                return null;
            }

            if (gameVersion.Version.StartsWith("1.1.0.0", StringComparison.OrdinalIgnoreCase))
            {
                return StockRestoreDlcZip;
            }

            if (gameVersion.Version.StartsWith("1.0.", StringComparison.OrdinalIgnoreCase))
            {
                return StockRestoreBaseZip;
            }

            return null;
        }

        public static string GetStockRestoreExtractDirectory(GameConfiguration config) =>
            UserTdGamePathHelper.GetUserContentExtractDirectory(config);

        public static bool AnyFilesPresent(GameConfiguration config, TweaksScriptsUIPaths paths) =>
            UserTdGamePathHelper.UsesPublishedLayout(config)
                ? paths.All.Any(File.Exists)
                : paths.ModOnly.Any(File.Exists);

        public static TweaksScriptsUIInstallState GetInstallState(
            GameConfiguration config,
            TweaksScriptsUIFilePresence presence) =>
            UserTdGamePathHelper.UsesPublishedLayout(config)
                ? GetPublishedLayoutInstallState(presence)
                : GetNonPublishedLayoutInstallState(presence);

        public static TweaksScriptsUIInstallState GetInstallState(
            GameConfiguration config,
            TweaksScriptsUIPaths paths) =>
            GetInstallState(config, TweaksScriptsUIFilePresence.FromPaths(paths));

        private static TweaksScriptsUIInstallState GetPublishedLayoutInstallState(TweaksScriptsUIFilePresence presence)
        {
            if (presence.HasMainMenu && presence.HasFrontEnd && presence.HasSofTimer && presence.HasCustomRaces)
            {
                return TweaksScriptsUIInstallState.InstalledMemm;
            }

            if (presence.HasMainMenu && presence.HasFrontEnd && presence.HasSofTimer)
            {
                return TweaksScriptsUIInstallState.InstalledRegular;
            }

            if (presence.HasAny)
            {
                return TweaksScriptsUIInstallState.PartiallyInstalled;
            }

            return TweaksScriptsUIInstallState.NotInstalled;
        }

        private static TweaksScriptsUIInstallState GetNonPublishedLayoutInstallState(TweaksScriptsUIFilePresence presence)
        {
            if (presence.HasSofTimer && presence.HasCustomRaces)
            {
                return TweaksScriptsUIInstallState.InstalledMemm;
            }

            if (presence.HasSofTimer)
            {
                return TweaksScriptsUIInstallState.InstalledRegular;
            }

            if (presence.HasCustomRaces)
            {
                return TweaksScriptsUIInstallState.PartiallyInstalled;
            }

            return TweaksScriptsUIInstallState.NotInstalled;
        }

        public static int DeleteModOnlyFiles(TweaksScriptsUIPaths paths, IFileService fileService) =>
            DeleteExistingFiles(paths.ModOnly, fileService);

        public static int DeleteAllFiles(TweaksScriptsUIPaths paths, IFileService fileService) =>
            DeleteExistingFiles(paths.All, fileService);

        private static int DeleteExistingFiles(IEnumerable<string> files, IFileService fileService)
        {
            int count = 0;
            foreach (string file in files)
            {
                if (!fileService.FileExists(file))
                {
                    continue;
                }

                fileService.DeleteFile(file);
                count++;
            }

            return count;
        }
    }

    public enum TweaksScriptsUIInstallState
    {
        NotInstalled,
        PartiallyInstalled,
        InstalledRegular,
        InstalledMemm
    }

    public readonly record struct TweaksScriptsUIFilePresence(
        bool HasMainMenu,
        bool HasFrontEnd,
        bool HasSofTimer,
        bool HasCustomRaces)
    {
        public bool HasAny => HasMainMenu || HasFrontEnd || HasSofTimer || HasCustomRaces;

        public static TweaksScriptsUIFilePresence FromPaths(TweaksScriptsUIPaths paths) =>
            new(
                File.Exists(paths.MainMenu),
                File.Exists(paths.FrontEnd),
                File.Exists(paths.SofTimer),
                File.Exists(paths.CustomRaces));
    }
}

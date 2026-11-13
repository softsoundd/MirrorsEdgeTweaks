using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class TweaksScriptsInstallHelper
    {
        public const string AssetFileName = "MirrorsEdgeTweaksScripts.zip";

        // Holds the HTTP validator of the download that installed the package.
        private const string InstalledVersionFileName = "MirrorsEdgeTweaksScripts-asset.txt";

        // Besides the package, MirrorsEdgeTweaksScripts.zip puts these exec scripts and the Softimer settings file in Binaries.
        private static readonly string[] BinariesFileNames =
        {
            "Cheats",
            "CheatsOff",
            "Macro",
            "Speedrun",
            "SpeedrunOff",
            "TimeTrialOrder",
            "TrainerHud",
            "TrainerHudOff",
            "TweaksScriptsSettings",
        };

        public static string GetPackagePath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "MirrorsEdgeTweaksScripts.u");

        // Carries the user's TweaksScriptsSettings values over to the zip's copy of the file.
        // The caller records the new version afterwards.
        public static void Install(string zipPath, string gameDirectoryPath)
        {
            // A failed extraction must not leave the previous package's version behind.
            DeleteInstalledVersion(gameDirectoryPath);

            string settingsPath = TweaksScriptsSettingsHelper.GetPath(gameDirectoryPath);
            string[]? previousSettings = File.Exists(settingsPath) ? File.ReadAllLines(settingsPath) : null;

            ZipFile.ExtractToDirectory(zipPath, gameDirectoryPath, overwriteFiles: true);

            if (previousSettings != null && File.Exists(settingsPath))
                File.WriteAllLines(settingsPath, TweaksScriptsSettingsHelper.MergeValues(File.ReadAllLines(settingsPath), previousSettings));
        }

        // Returns the number of files deleted.
        public static int DeleteFiles(string gameDirectoryPath, IFileService fileService)
        {
            IEnumerable<string> files = BinariesFileNames
                .Select(name => Path.Combine(gameDirectoryPath, "Binaries", name))
                .Prepend(GetPackagePath(gameDirectoryPath))
                .Append(GetInstalledVersionPath(gameDirectoryPath));

            int deleted = 0;
            foreach (string file in files)
            {
                if (!fileService.FileExists(file))
                    continue;

                fileService.DeleteFile(file);
                deleted++;
            }
            return deleted;
        }

        public static string? ReadInstalledVersion(string gameDirectoryPath)
        {
            string path = GetInstalledVersionPath(gameDirectoryPath);
            if (!File.Exists(path))
                return null;

            string version = File.ReadAllText(path).Trim();
            return version.Length > 0 ? version : null;
        }

        public static void WriteInstalledVersion(string gameDirectoryPath, string version) =>
            File.WriteAllText(GetInstalledVersionPath(gameDirectoryPath), version);

        // A package without a recorded version, such as one installed by an older Tweaks, counts as outdated.
        internal static bool IsUpdateAvailable(string? installedVersion, string? latestVersion) =>
            latestVersion != null && installedVersion != latestVersion;

        private static void DeleteInstalledVersion(string gameDirectoryPath)
        {
            string path = GetInstalledVersionPath(gameDirectoryPath);
            if (File.Exists(path))
                File.Delete(path);
        }

        private static string GetInstalledVersionPath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "Binaries", InstalledVersionFileName);
    }
}

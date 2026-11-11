using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class TweaksScriptsInstallHelper
    {
        public const string AssetFileName = "MirrorsEdgeTweaksScripts.zip";

        // MirrorsEdgeTweaksScripts.zip puts these exec scripts and the Softimer settings file in Binaries, beside the package.
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

        // Returns the number of files deleted.
        public static int DeleteFiles(string gameDirectoryPath, IFileService fileService)
        {
            IEnumerable<string> files = BinariesFileNames
                .Select(name => Path.Combine(gameDirectoryPath, "Binaries", name))
                .Prepend(GetPackagePath(gameDirectoryPath));

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
    }
}

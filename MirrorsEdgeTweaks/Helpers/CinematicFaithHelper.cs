using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    // The Cinematic Faith Model mod replaces Faith's third-person model package, and the two versions differ in size.
    public static class CinematicFaithHelper
    {
        public const string CinematicAssetFileName = "FaithModelCinematic.zip";
        public const string OriginalAssetFileName = "FaithModelOriginal.zip";

        private const long CinematicModelSize = 15063782;
        private const long OriginalModelSize = 8155273;

        public static string GetModelPath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "Characters", "CH_TKY_Crim_Fixer.upk");

        // Returns null when the model package is missing or matches neither version.
        public static bool? IsCinematicInstalled(string gameDirectoryPath)
        {
            var model = new FileInfo(GetModelPath(gameDirectoryPath));
            if (!model.Exists)
                return null;

            return model.Length switch
            {
                CinematicModelSize => true,
                OriginalModelSize => false,
                _ => null,
            };
        }
    }
}

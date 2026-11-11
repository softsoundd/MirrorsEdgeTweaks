using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class VerifyGameFilesHelper
    {
        // Returns how to verify the game files in the store that the executable comes from.
        public static string GetInstructions(string gameDirectoryPath) => ReadExeVersion(gameDirectoryPath) switch
        {
            "steam" => "In Steam, right-click Mirror's Edge in your library, select Properties, open Installed Files, " +
                "and select \"Verify integrity of game files\".",
            "ea" => "In the EA app, open Mirror's Edge from your library, select the three dots next to Play, " +
                "and select \"Repair\".",
            "gog" => "In GOG Galaxy, open Mirror's Edge, select the settings icon next to Play, " +
                "and select Manage installation > Verify / Repair.",
            _ => "Reinstall Mirror's Edge, along with any official patches you had installed.",
        };

        private static string ReadExeVersion(string gameDirectoryPath)
        {
            string exePath = Path.Combine(gameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            try { return ExeVersionDetector.DetectVersion(File.ReadAllBytes(exePath), exePath) ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}

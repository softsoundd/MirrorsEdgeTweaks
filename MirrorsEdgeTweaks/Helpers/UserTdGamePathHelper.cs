using System.IO;
using MirrorsEdgeTweaks.Models;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class UserTdGamePathHelper
    {
        private const string ConfigFolderName = "Config";
        private const string PublishedFolderName = "Published";
        private const string CookedPcFolderName = "CookedPC";

        public static string GetDefaultTdGamePath() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "EA Games",
                "Mirror's Edge",
                "TdGame");

        public static string ResolveTdGamePath(GameConfiguration config) =>
            ResolveTdGamePath(config.UserFolderPath, config.GameDirectoryPath, config.LaunchArguments);

        public static string ResolveTdGamePath(string? userFolderPath, string? gameDirectoryPath = null, string? launchArguments = null)
        {
            if (!string.IsNullOrWhiteSpace(userFolderPath))
            {
                return userFolderPath.Trim();
            }

            if (!string.IsNullOrEmpty(gameDirectoryPath) && UsesNoHomeDir(launchArguments))
            {
                return Path.Combine(gameDirectoryPath, "TdGame");
            }

            return GetDefaultTdGamePath();
        }

        public static bool UsesPublishedLayout(GameConfiguration config)
        {
            if (!string.IsNullOrEmpty(config.GameDirectoryPath))
            {
                string installTdGamePath = Path.Combine(config.GameDirectoryPath, "TdGame");
                if (PathsEqual(ResolveTdGamePath(config), installTdGamePath))
                {
                    return false;
                }
            }

            return true;
        }

        public static string GetConfigDirectory(GameConfiguration config) =>
            Path.Combine(ResolveTdGamePath(config), ConfigFolderName);

        public static string GetPublishedDirectory(GameConfiguration config) =>
            Path.Combine(ResolveTdGamePath(config), PublishedFolderName);

        public static string GetUserCookedPcDirectory(GameConfiguration config) =>
            UsesPublishedLayout(config)
                ? Path.Combine(GetPublishedDirectory(config), CookedPcFolderName)
                : Path.Combine(ResolveTdGamePath(config), CookedPcFolderName);

        public static string GetUserContentExtractDirectory(GameConfiguration config) =>
            UsesPublishedLayout(config)
                ? GetPublishedDirectory(config)
                : ResolveTdGamePath(config);

        public static string GetTdEngineIniPath(GameConfiguration config) =>
            Path.Combine(GetConfigDirectory(config), "TdEngine.ini");

        public static string GetTdInputIniPath(GameConfiguration config) =>
            Path.Combine(GetConfigDirectory(config), "TdInput.ini");

        public static TweaksScriptsUIPaths GetTweaksScriptsUIPaths(GameConfiguration config)
        {
            string cookedPcPath = GetUserCookedPcDirectory(config);
            return new TweaksScriptsUIPaths(
                Path.Combine(cookedPcPath, "Maps", "Menu", "TdMainMenu.me1"),
                Path.Combine(cookedPcPath, "UI", "TdUI_FrontEnd.upk"),
                Path.Combine(cookedPcPath, "UI", "TdUI_SofTimer.upk"),
                Path.Combine(cookedPcPath, "UI", "TdUI_Custom_Races.upk"));
        }

        public static bool IsUsingCustomPath(string? userFolderPath) =>
            !string.IsNullOrWhiteSpace(userFolderPath);

        public static void EnsureUserFolderLayout(GameConfiguration config)
        {
            string tdGamePath = ResolveTdGamePath(config);
            Directory.CreateDirectory(Path.Combine(tdGamePath, ConfigFolderName));

            if (UsesPublishedLayout(config))
            {
                Directory.CreateDirectory(Path.Combine(tdGamePath, PublishedFolderName));
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(tdGamePath, CookedPcFolderName));
            }
        }

        public static bool TryNormalizeSelectedPath(string selected, out string normalizedPath, out string? errorMessage)
        {
            normalizedPath = string.Empty;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(selected))
            {
                errorMessage = "No folder was selected.";
                return false;
            }

            string trimmed = selected.Trim();
            normalizedPath = trimmed;
            return ValidateTdGamePath(normalizedPath, out errorMessage);
        }

        public static bool IsDefaultPath(string normalizedPath)
        {
            string defaultPath = GetDefaultTdGamePath();
            return PathsEqual(normalizedPath, defaultPath);
        }

        private static bool ValidateTdGamePath(string tdGamePath, out string? errorMessage)
        {
            errorMessage = null;

            if (!Directory.Exists(tdGamePath))
            {
                errorMessage =
                    "The selected folder does not exist.\n\n" +
                    "Select the user folder that contains (or will contain) a Config subfolder.";
                return false;
            }

            string configDirectory = Path.Combine(tdGamePath, ConfigFolderName);
            if (!Directory.Exists(configDirectory))
            {
                errorMessage =
                    "The selected folder does not contain a Config subfolder.\n\n" +
                    "Select the user folder Mirror's Edge uses for user settings " +
                    "(for example, the TdGame folder under Documents or your game install when using -NOHOMEDIR).";
                return false;
            }

            return true;
        }

        private static bool UsesNoHomeDir(string? launchArguments) =>
            !string.IsNullOrWhiteSpace(launchArguments) &&
            launchArguments.Contains("nohomedir", StringComparison.OrdinalIgnoreCase);

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public readonly record struct TweaksScriptsUIPaths(
        string MainMenu,
        string FrontEnd,
        string SofTimer,
        string CustomRaces)
    {
        public IEnumerable<string> ModOnly => [SofTimer, CustomRaces];

        public IEnumerable<string> StockReplacements => [MainMenu, FrontEnd];

        public IEnumerable<string> All => [MainMenu, FrontEnd, SofTimer, CustomRaces];
    };
}

using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class UserTdGamePathHelperTests
    {
        [Fact]
        public void ResolveTdGamePath_WithoutOverride_ReturnsDefaultDocumentsPath()
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "EA Games",
                "Mirror's Edge",
                "TdGame");

            string resolved = UserTdGamePathHelper.ResolveTdGamePath(userFolderPath: null);

            Assert.Equal(expected, resolved);
        }

        [Fact]
        public void ResolveTdGamePath_WithOverride_ReturnsTrimmedOverride()
        {
            string resolved = UserTdGamePathHelper.ResolveTdGamePath(@"  D:\Custom\TdGame  ", null, null);

            Assert.Equal(@"D:\Custom\TdGame", resolved);
        }

        [Fact]
        public void GetConfigAndPublishedDirectories_DeriveFromResolvedTdGamePath()
        {
            var config = new GameConfiguration
            {
                UserFolderPath = @"C:\Custom\TdGame"
            };
            string tdGamePath = @"C:\Custom\TdGame";

            Assert.Equal(
                Path.Combine(tdGamePath, "Config"),
                UserTdGamePathHelper.GetConfigDirectory(config));
            Assert.Equal(
                Path.Combine(tdGamePath, "Published"),
                UserTdGamePathHelper.GetPublishedDirectory(config));
            Assert.Equal(
                Path.Combine(tdGamePath, "Config", "TdEngine.ini"),
                UserTdGamePathHelper.GetTdEngineIniPath(config));
            Assert.Equal(
                Path.Combine(tdGamePath, "Config", "TdInput.ini"),
                UserTdGamePathHelper.GetTdInputIniPath(config));
        }

        [Fact]
        public void IsUsingCustomPath_ReturnsTrueOnlyForNonEmptyOverride()
        {
            Assert.False(UserTdGamePathHelper.IsUsingCustomPath(null));
            Assert.False(UserTdGamePathHelper.IsUsingCustomPath(""));
            Assert.False(UserTdGamePathHelper.IsUsingCustomPath("   "));
            Assert.True(UserTdGamePathHelper.IsUsingCustomPath(@"C:\Custom\TdGame"));
        }

        [Fact]
        public void TryNormalizeSelectedPath_AcceptsTdGameFolder()
        {
            using var temp = new TempDirectory();
            string tdGamePath = CreateTdGameLayout(temp.Path);

            bool success = UserTdGamePathHelper.TryNormalizeSelectedPath(tdGamePath, out string normalizedPath, out string? errorMessage);

            Assert.True(success);
            Assert.Equal(tdGamePath, normalizedPath);
            Assert.Null(errorMessage);
        }

        [Fact]
        public void TryNormalizeSelectedPath_RejectsConfigFolder()
        {
            using var temp = new TempDirectory();
            string tdGamePath = CreateTdGameLayout(temp.Path);
            string configPath = Path.Combine(tdGamePath, "Config");

            bool success = UserTdGamePathHelper.TryNormalizeSelectedPath(configPath, out _, out string? errorMessage);

            Assert.False(success);
            Assert.NotNull(errorMessage);
        }

        [Fact]
        public void TryNormalizeSelectedPath_RejectsFolderWithoutConfigSubdirectory()
        {
            using var temp = new TempDirectory();

            bool success = UserTdGamePathHelper.TryNormalizeSelectedPath(temp.Path, out _, out string? errorMessage);

            Assert.False(success);
            Assert.NotNull(errorMessage);
        }

        [Fact]
        public void ResolveTdGamePath_WithNoHomeDirLaunchArg_UsesGameInstallTdGame()
        {
            string resolved = UserTdGamePathHelper.ResolveTdGamePath(
                userFolderPath: null,
                gameDirectoryPath: @"C:\Games\Mirror's Edge",
                launchArguments: "-NOHOMEDIR");

            Assert.Equal(@"C:\Games\Mirror's Edge\TdGame", resolved);
        }

        [Fact]
        public void ResolveTdGamePath_WithUserConfigInGameInstall_StillUsesDocumentsWhenNoHomeDirNotSet()
        {
            using var temp = new TempDirectory();
            string tdGamePath = CreateTdGameLayout(temp.Path);
            File.WriteAllText(Path.Combine(tdGamePath, "Config", "TdEngine.ini"), "[Engine.Engine]\n");

            string resolved = UserTdGamePathHelper.ResolveTdGamePath(
                userFolderPath: null,
                gameDirectoryPath: temp.Path);

            Assert.Equal(UserTdGamePathHelper.GetDefaultTdGamePath(), resolved);
        }

        [Fact]
        public void ResolveTdGamePath_ExplicitOverrideTakesPrecedenceOverNoHomeDir()
        {
            var config = new GameConfiguration
            {
                UserFolderPath = @"D:\Override\TdGame",
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = "-NOHOMEDIR"
            };

            Assert.Equal(@"D:\Override\TdGame", UserTdGamePathHelper.ResolveTdGamePath(config));
        }

        [Fact]
        public void UsesPublishedLayout_IsTrueForDefaultDocumentsHome()
        {
            var config = new GameConfiguration
            {
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = string.Empty
            };

            Assert.True(UserTdGamePathHelper.UsesPublishedLayout(config));
            Assert.Equal(
                Path.Combine(UserTdGamePathHelper.GetDefaultTdGamePath(), "Published", "CookedPC"),
                UserTdGamePathHelper.GetUserCookedPcDirectory(config));
        }

        [Fact]
        public void UsesPublishedLayout_IsFalseForNoHomeDirGameInstall()
        {
            var config = new GameConfiguration
            {
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = "-NOHOMEDIR"
            };

            Assert.False(UserTdGamePathHelper.UsesPublishedLayout(config));
            Assert.Equal(
                @"C:\Games\Mirror's Edge\TdGame\CookedPC",
                UserTdGamePathHelper.GetUserCookedPcDirectory(config));
            Assert.Equal(
                @"C:\Games\Mirror's Edge\TdGame",
                UserTdGamePathHelper.GetUserContentExtractDirectory(config));
        }

        [Fact]
        public void UsesPublishedLayout_IsFalseWhenOverridePointsAtGameInstallTdGame()
        {
            var config = new GameConfiguration
            {
                UserFolderPath = @"D:\Games\Mirror's Edge\TdGame",
                GameDirectoryPath = @"D:\Games\Mirror's Edge"
            };

            Assert.False(UserTdGamePathHelper.UsesPublishedLayout(config));
            Assert.Equal(
                @"D:\Games\Mirror's Edge\TdGame\CookedPC",
                UserTdGamePathHelper.GetUserCookedPcDirectory(config));
        }

        [Fact]
        public void UsesPublishedLayout_IsTrueWhenExplicitOverrideDespiteStaleNoHomeDir()
        {
            var config = new GameConfiguration
            {
                UserFolderPath = @"D:\Wine\prefix\drive_c\users\player\Documents\EA Games\Mirror's Edge\TdGame",
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = "-NOHOMEDIR"
            };

            Assert.True(UserTdGamePathHelper.UsesPublishedLayout(config));
            Assert.Equal(
                Path.Combine(config.UserFolderPath!, "Published", "CookedPC"),
                UserTdGamePathHelper.GetUserCookedPcDirectory(config));
        }

        [Fact]
        public void EnsureUserFolderLayout_CreatesPublishedForDocumentsHome()
        {
            using var temp = new TempDirectory();
            string tdGamePath = CreateTdGameLayout(temp.Path);

            var config = new GameConfiguration
            {
                UserFolderPath = tdGamePath
            };

            UserTdGamePathHelper.EnsureUserFolderLayout(config);

            Assert.True(Directory.Exists(Path.Combine(tdGamePath, "Config")));
            Assert.True(Directory.Exists(Path.Combine(tdGamePath, "Published")));
            Assert.False(Directory.Exists(Path.Combine(tdGamePath, "CookedPC")));
        }

        [Fact]
        public void EnsureUserFolderLayout_CreatesCookedPcForNoHomeDirInstall()
        {
            using var temp = new TempDirectory();
            string gameDirectory = Path.Combine(temp.Path, "Mirror's Edge");
            string tdGamePath = CreateTdGameLayout(gameDirectory);

            var config = new GameConfiguration
            {
                GameDirectoryPath = gameDirectory,
                LaunchArguments = "-NOHOMEDIR"
            };

            UserTdGamePathHelper.EnsureUserFolderLayout(config);

            Assert.True(Directory.Exists(Path.Combine(tdGamePath, "Config")));
            Assert.True(Directory.Exists(Path.Combine(tdGamePath, "CookedPC")));
            Assert.False(Directory.Exists(Path.Combine(tdGamePath, "Published")));
        }

        [Fact]
        public void IsDefaultPath_MatchesResolvedDefaultPath()
        {
            string defaultPath = UserTdGamePathHelper.GetDefaultTdGamePath();

            Assert.True(UserTdGamePathHelper.IsDefaultPath(defaultPath));
            Assert.False(UserTdGamePathHelper.IsDefaultPath(@"C:\Other\TdGame"));
        }

        private static string CreateTdGameLayout(string root)
        {
            string tdGamePath = Path.Combine(root, "TdGame");
            Directory.CreateDirectory(Path.Combine(tdGamePath, "Config"));
            return tdGamePath;
        }
    }
}

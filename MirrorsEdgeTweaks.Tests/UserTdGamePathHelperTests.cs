using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;

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
            string tdGamePath = CreateTdGameLayout();

            bool success = UserTdGamePathHelper.TryNormalizeSelectedPath(tdGamePath, out string normalizedPath, out string? errorMessage);

            Assert.True(success);
            Assert.Equal(tdGamePath, normalizedPath);
            Assert.Null(errorMessage);
        }

        [Fact]
        public void TryNormalizeSelectedPath_RejectsConfigFolder()
        {
            string tdGamePath = CreateTdGameLayout();
            string configPath = Path.Combine(tdGamePath, "Config");

            bool success = UserTdGamePathHelper.TryNormalizeSelectedPath(configPath, out _, out string? errorMessage);

            Assert.False(success);
            Assert.NotNull(errorMessage);
        }

        [Fact]
        public void TryNormalizeSelectedPath_RejectsFolderWithoutConfigSubdirectory()
        {
            string invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(invalidPath);

            try
            {
                bool success = UserTdGamePathHelper.TryNormalizeSelectedPath(invalidPath, out _, out string? errorMessage);

                Assert.False(success);
                Assert.NotNull(errorMessage);
            }
            finally
            {
                Directory.Delete(invalidPath, recursive: true);
            }
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
            string tdGamePath = CreateTdGameLayout();
            string gameDirectory = Directory.GetParent(tdGamePath)!.FullName;
            File.WriteAllText(Path.Combine(tdGamePath, "Config", "TdEngine.ini"), "[Engine.Engine]\n");

            string resolved = UserTdGamePathHelper.ResolveTdGamePath(
                userFolderPath: null,
                gameDirectoryPath: gameDirectory);

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
            string root = Path.Combine(Path.GetTempPath(), "metweaks-test-" + Guid.NewGuid().ToString("N"));
            string tdGamePath = Path.Combine(root, "TdGame");
            Directory.CreateDirectory(Path.Combine(tdGamePath, "Config"));

            var config = new GameConfiguration
            {
                UserFolderPath = tdGamePath
            };

            try
            {
                UserTdGamePathHelper.EnsureUserFolderLayout(config);

                Assert.True(Directory.Exists(Path.Combine(tdGamePath, "Config")));
                Assert.True(Directory.Exists(Path.Combine(tdGamePath, "Published")));
                Assert.False(Directory.Exists(Path.Combine(tdGamePath, "CookedPC")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void EnsureUserFolderLayout_CreatesCookedPcForNoHomeDirInstall()
        {
            string root = Path.Combine(Path.GetTempPath(), "metweaks-test-" + Guid.NewGuid().ToString("N"));
            string gameDirectory = Path.Combine(root, "Mirror's Edge");
            string tdGamePath = Path.Combine(gameDirectory, "TdGame");
            Directory.CreateDirectory(Path.Combine(tdGamePath, "Config"));

            var config = new GameConfiguration
            {
                GameDirectoryPath = gameDirectory,
                LaunchArguments = "-NOHOMEDIR"
            };

            try
            {
                UserTdGamePathHelper.EnsureUserFolderLayout(config);

                Assert.True(Directory.Exists(Path.Combine(tdGamePath, "Config")));
                Assert.True(Directory.Exists(Path.Combine(tdGamePath, "CookedPC")));
                Assert.False(Directory.Exists(Path.Combine(tdGamePath, "Published")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void IsDefaultPath_MatchesResolvedDefaultPath()
        {
            string defaultPath = UserTdGamePathHelper.GetDefaultTdGamePath();

            Assert.True(UserTdGamePathHelper.IsDefaultPath(defaultPath));
            Assert.False(UserTdGamePathHelper.IsDefaultPath(@"C:\Other\TdGame"));
        }

        private static string CreateTdGameLayout()
        {
            string tdGamePath = Path.Combine(Path.GetTempPath(), "metweaks-test-" + Guid.NewGuid().ToString("N"), "TdGame");
            Directory.CreateDirectory(Path.Combine(tdGamePath, "Config"));
            return tdGamePath;
        }
    }
}

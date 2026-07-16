using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class TweaksScriptsUIHelperTests
    {
        [Theory]
        [InlineData("1.0.0.0", TweaksScriptsUIHelper.StockRestoreBaseZip)]
        [InlineData("1.0.1.0", TweaksScriptsUIHelper.StockRestoreBaseZip)]
        [InlineData("1.1.0.0", TweaksScriptsUIHelper.StockRestoreDlcZip)]
        public void GetStockRestoreZipFileName_MapsKnownGameVersions(string fileVersion, string expectedZip)
        {
            var gameVersion = new GameVersion
            {
                Version = fileVersion,
                IsValid = true
            };

            Assert.Equal(expectedZip, TweaksScriptsUIHelper.GetStockRestoreZipFileName(gameVersion));
        }

        [Fact]
        public void GetStockRestoreZipFileName_ReturnsNullForUnknownVersion()
        {
            var gameVersion = new GameVersion
            {
                Version = "2.0.0.0",
                IsValid = true
            };

            Assert.Null(TweaksScriptsUIHelper.GetStockRestoreZipFileName(gameVersion));
        }

        [Fact]
        public void GetStockRestoreZipFileName_ReturnsNullWhenGameDirectoryMissing()
        {
            var config = new GameConfiguration { GameDirectoryPath = null };

            Assert.Null(TweaksScriptsUIHelper.GetStockRestoreZipFileName(config));
        }

        [Fact]
        public void DeleteModOnlyFiles_RemovesOnlyModAdditions()
        {
            string root = Path.Combine(Path.GetTempPath(), "metweaks-test-" + Guid.NewGuid().ToString("N"));
            var paths = new TweaksScriptsUIPaths(
                Path.Combine(root, "TdMainMenu.me1"),
                Path.Combine(root, "TdUI_FrontEnd.upk"),
                Path.Combine(root, "TdUI_SofTimer.upk"),
                Path.Combine(root, "TdUI_Custom_Races.upk"));

            Directory.CreateDirectory(root);
            File.WriteAllText(paths.MainMenu, "menu");
            File.WriteAllText(paths.FrontEnd, "front");
            File.WriteAllText(paths.SofTimer, "sof");
            File.WriteAllText(paths.CustomRaces, "races");

            var fileService = new FileService();

            try
            {
                int deleted = TweaksScriptsUIHelper.DeleteModOnlyFiles(paths, fileService);

                Assert.Equal(2, deleted);
                Assert.True(File.Exists(paths.MainMenu));
                Assert.True(File.Exists(paths.FrontEnd));
                Assert.False(File.Exists(paths.SofTimer));
                Assert.False(File.Exists(paths.CustomRaces));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void GetStockRestoreExtractDirectory_MatchesUserContentExtractDirectory()
        {
            var config = new GameConfiguration
            {
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = "-NOHOMEDIR"
            };

            Assert.Equal(
                UserTdGamePathHelper.GetUserContentExtractDirectory(config),
                TweaksScriptsUIHelper.GetStockRestoreExtractDirectory(config));
        }

        [Fact]
        public void GetInstallState_NonPublishedLayout_IgnoresStockReplacementFiles()
        {
            var config = new GameConfiguration
            {
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = "-NOHOMEDIR"
            };

            var presence = new TweaksScriptsUIFilePresence(
                HasMainMenu: true,
                HasFrontEnd: true,
                HasSofTimer: false,
                HasCustomRaces: false);

            Assert.Equal(
                TweaksScriptsUIInstallState.NotInstalled,
                TweaksScriptsUIHelper.GetInstallState(config, presence));
        }

        [Theory]
        [InlineData(false, false, TweaksScriptsUIInstallState.NotInstalled)]
        [InlineData(true, false, TweaksScriptsUIInstallState.InstalledRegular)]
        [InlineData(true, true, TweaksScriptsUIInstallState.InstalledMemm)]
        [InlineData(false, true, TweaksScriptsUIInstallState.PartiallyInstalled)]
        public void GetInstallState_NonPublishedLayout_UsesModOnlyFiles(
            bool hasSofTimer,
            bool hasCustomRaces,
            TweaksScriptsUIInstallState expected)
        {
            var config = new GameConfiguration
            {
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = "-NOHOMEDIR"
            };

            var presence = new TweaksScriptsUIFilePresence(
                HasMainMenu: true,
                HasFrontEnd: true,
                HasSofTimer: hasSofTimer,
                HasCustomRaces: hasCustomRaces);

            Assert.Equal(expected, TweaksScriptsUIHelper.GetInstallState(config, presence));
        }

        [Fact]
        public void GetInstallState_PublishedLayout_RequiresStockReplacementFiles()
        {
            var config = new GameConfiguration
            {
                GameDirectoryPath = @"C:\Games\Mirror's Edge",
                LaunchArguments = string.Empty
            };

            var presence = new TweaksScriptsUIFilePresence(
                HasMainMenu: false,
                HasFrontEnd: false,
                HasSofTimer: true,
                HasCustomRaces: false);

            Assert.Equal(
                TweaksScriptsUIInstallState.PartiallyInstalled,
                TweaksScriptsUIHelper.GetInstallState(config, presence));
        }
    }
}

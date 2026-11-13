using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.Tests
{
    public class TweaksScriptsInstallHelperTests
    {
        [Fact]
        public void Install_KeepsTheUsersSettingsValues()
        {
            using var temp = new TempDirectory();
            string zip = WriteAssetZip(temp.Path);
            string game = Path.Combine(temp.Path, "game");
            Directory.CreateDirectory(Path.Combine(game, "Binaries"));
            string settings = TweaksScriptsSettingsHelper.GetPath(game);
            File.WriteAllLines(settings, new[] { "TimerVisibility false", "Speedometer true" });

            TweaksScriptsInstallHelper.Install(zip, game);

            Assert.True(File.Exists(TweaksScriptsInstallHelper.GetPackagePath(game)));
            Assert.Equal(
                new[] { "// SOFTIMER SETTINGS", "TimerVisibility false", "Speedometer true", "InputOverlay false" },
                File.ReadAllLines(settings));
        }

        [Fact]
        public void Install_DropsTheRecordedVersion_EvenWhenExtractionFails()
        {
            using var temp = new TempDirectory();
            string zip = Path.Combine(temp.Path, TweaksScriptsInstallHelper.AssetFileName);
            File.WriteAllText(zip, "not a zip");
            string game = Path.Combine(temp.Path, "game");
            Directory.CreateDirectory(Path.Combine(game, "Binaries"));
            TweaksScriptsInstallHelper.WriteInstalledVersion(game, "\"0x8DF2C6942E1B2AE\"");

            Assert.Throws<InvalidDataException>(() => TweaksScriptsInstallHelper.Install(zip, game));
            Assert.Null(TweaksScriptsInstallHelper.ReadInstalledVersion(game));
        }

        [Fact]
        public void DeleteFiles_RemovesThePackageAndItsBinariesFiles_ButNotOtherExecScripts()
        {
            using var temp = new TempDirectory();
            string binaries = Path.Combine(temp.Path, "Binaries");
            Directory.CreateDirectory(binaries);
            Directory.CreateDirectory(Path.Combine(temp.Path, "TdGame", "CookedPC"));

            string[] installed = { "Cheats", "CheatsOff", "Macro", "Speedrun", "SpeedrunOff", "TrainerHud", "TweaksScriptsSettings" };
            foreach (string name in installed)
                File.WriteAllText(Path.Combine(binaries, name), "exec");
            File.WriteAllText(Path.Combine(binaries, "fastloads"), "exec");
            File.WriteAllText(TweaksScriptsInstallHelper.GetPackagePath(temp.Path), "package");

            int deleted = TweaksScriptsInstallHelper.DeleteFiles(temp.Path, new FileService());

            Assert.Equal(installed.Length + 1, deleted);
            Assert.False(File.Exists(TweaksScriptsInstallHelper.GetPackagePath(temp.Path)));
            Assert.All(installed, name => Assert.False(File.Exists(Path.Combine(binaries, name))));
            Assert.True(File.Exists(Path.Combine(binaries, "fastloads")));
        }

        [Fact]
        public void InstalledVersion_IsRemovedWithThePackage()
        {
            using var temp = new TempDirectory();
            Directory.CreateDirectory(Path.Combine(temp.Path, "Binaries"));
            Directory.CreateDirectory(Path.Combine(temp.Path, "TdGame", "CookedPC"));
            File.WriteAllText(TweaksScriptsInstallHelper.GetPackagePath(temp.Path), "package");
            Assert.Null(TweaksScriptsInstallHelper.ReadInstalledVersion(temp.Path));

            TweaksScriptsInstallHelper.WriteInstalledVersion(temp.Path, "\"0x8DF2C6942E1B2AE\"");
            Assert.Equal("\"0x8DF2C6942E1B2AE\"", TweaksScriptsInstallHelper.ReadInstalledVersion(temp.Path));

            TweaksScriptsInstallHelper.DeleteFiles(temp.Path, new FileService());
            Assert.Null(TweaksScriptsInstallHelper.ReadInstalledVersion(temp.Path));
        }

        [Theory]
        [InlineData(null, "\"0x8DF2C6942E1B2AE\"", true)]
        [InlineData("\"0x8DF2C6942E1B2AE\"", "\"0x8DF2C6942E1B2AE\"", false)]
        [InlineData("\"0x8DF2C6942E1B2AE\"", "\"0x8DF41C31BD7C9B8\"", true)]
        [InlineData("\"0x8DF2C6942E1B2AE\"", null, false)]
        [InlineData(null, null, false)]
        public void IsUpdateAvailable_OffersADifferentLatestVersion_EvenWithoutARecordedOne(string? installed, string? latest, bool expected)
        {
            Assert.Equal(expected, TweaksScriptsInstallHelper.IsUpdateAvailable(installed, latest));
        }

        // Matches the layout of the published asset.
        private static string WriteAssetZip(string directory)
        {
            string zip = Path.Combine(directory, TweaksScriptsInstallHelper.AssetFileName);
            using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            WriteEntry(archive, "Binaries/TweaksScriptsSettings", "// SOFTIMER SETTINGS\nTimerVisibility true\nSpeedometer false\nInputOverlay false\n");
            WriteEntry(archive, "TdGame/CookedPC/MirrorsEdgeTweaksScripts.u", "package");
            return zip;
        }

        private static void WriteEntry(ZipArchive archive, string name, string contents)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(contents);
        }
    }
}

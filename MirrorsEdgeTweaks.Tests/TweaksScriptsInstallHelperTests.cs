using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class TweaksScriptsInstallHelperTests
    {
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
    }
}

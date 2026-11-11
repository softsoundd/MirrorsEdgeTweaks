using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Security.Cryptography;
using System.Text;

namespace MirrorsEdgeTweaks.Tests
{
    public class StockConfigFilesTests
    {
        // A checkout that converts line endings or adds a final newline changes these bytes, and the game rejects the result.
        [Fact]
        public void EmbeddedGameConfigs_MatchTheStockHashes()
        {
            foreach (string name in StockConfigFiles.EditedGameConfigNames)
            {
                byte[] content = StockConfigFiles.ReadResource(StockConfigFiles.GameResourcePrefix + name);
                Assert.Equal(StockConfigFiles.StockGameConfigHashes[name], Convert.ToHexString(SHA256.HashData(content)));
            }
        }

        [Theory]
        [InlineData("TdEngine.ini")]
        [InlineData("TdInput.ini")]
        public void EmbeddedUserTemplates_UseCrlfLineEndings(string name)
        {
            byte[] content = StockConfigFiles.ReadResource(StockConfigFiles.UserResourcePrefix + name);

            Assert.NotEmpty(content);
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == (byte)'\n')
                    Assert.True(i > 0 && content[i - 1] == (byte)'\r', $"{name} has a bare line feed at byte {i}.");
            }
        }

        [Fact]
        public void RestoreGameConfigs_WritesTheStockBytes_AndClearsReadOnly()
        {
            using var temp = new TempDirectory();
            string configDirectory = Path.Combine(temp.Path, "TdGame", "Config");
            Directory.CreateDirectory(configDirectory);
            string hudEffects = Path.Combine(configDirectory, "DefaultHudEffects.ini");
            byte[] stock = StockConfigFiles.ReadResource(StockConfigFiles.GameResourcePrefix + "DefaultHudEffects.ini");

            // The streak writer leaves the file read-only, and ends it with a line break that the stock file lacks.
            File.WriteAllBytes(hudEffects, [.. stock, (byte)'\r', (byte)'\n']);
            File.SetAttributes(hudEffects, FileAttributes.ReadOnly);

            List<string> restored = StockConfigFiles.RestoreGameConfigs(temp.Path);

            Assert.Equal(StockConfigFiles.EditedGameConfigNames, restored);
            Assert.Equal(stock, File.ReadAllBytes(hudEffects));
            Assert.False(File.GetAttributes(hudEffects).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(StockConfigFiles.RestoreGameConfigs(temp.Path));
        }

        [Fact]
        public void FindNonStockGameConfigs_ListsChangedAndMissingFiles_ButNotStockOnes()
        {
            using var temp = new TempDirectory();
            Directory.CreateDirectory(Path.Combine(temp.Path, "TdGame", "Config"));
            StockConfigFiles.RestoreGameConfigs(temp.Path);
            File.AppendAllText(Path.Combine(temp.Path, "TdGame", "Config", "DefaultUI.ini"), "; custom map\r\n");

            List<string> nonStock = StockConfigFiles.FindNonStockGameConfigs(temp.Path);

            Assert.Contains("DefaultUI.ini", nonStock);
            Assert.Contains("DefaultEngine.ini", nonStock);
            Assert.DoesNotContain("DefaultGame.ini", nonStock);
            Assert.DoesNotContain("DefaultHudEffects.ini", nonStock);
            Assert.Equal(StockConfigFiles.StockGameConfigHashes.Count - 2, nonStock.Count);
        }

        [Fact]
        public void BuildTdEngineIni_KeepsTheLanguage_AndChangesNothingElse()
        {
            string template = StockConfigFiles.BuildTdEngineIni(null, null);
            string german = StockConfigFiles.BuildTdEngineIni("deu", null);

            Assert.Equal(ReadUserTemplate("TdEngine.ini"), template);
            Assert.Contains("\r\nLanguage=int\r\n", template);
            Assert.Equal(template.Replace("\r\nLanguage=int\r\n", "\r\nLanguage=deu\r\n"), german);
        }

        [Theory]
        [InlineData("")]
        [InlineData("deu\n")]
        [InlineData("int\r\nConsoleClassName=MirrorsEdgeConsole.MirrorsEdgeConsole")]
        public void BuildTdEngineIni_IgnoresAnInvalidLanguage(string languageCode)
        {
            Assert.Equal(StockConfigFiles.BuildTdEngineIni(null, null), StockConfigFiles.BuildTdEngineIni(languageCode, null));
        }

        [Fact]
        public void BuildTdEngineIni_SetsTheResolution_AndChangesNothingElse()
        {
            string template = StockConfigFiles.BuildTdEngineIni(null, null);
            string native = StockConfigFiles.BuildTdEngineIni(null, new ResolutionHelper.Resolution { Width = 2560, Height = 1440 });

            Assert.Contains("\r\nResX=1600\r\nResY=1200\r\n", template);
            Assert.Equal(template.Replace("\r\nResX=1600\r\nResY=1200\r\n", "\r\nResX=2560\r\nResY=1440\r\n"), native);
        }

        [Theory]
        [InlineData(0, 1440)]
        [InlineData(2560, -1)]
        public void BuildTdEngineIni_IgnoresAnInvalidResolution(int width, int height)
        {
            Assert.Equal(
                StockConfigFiles.BuildTdEngineIni(null, null),
                StockConfigFiles.BuildTdEngineIni(null, new ResolutionHelper.Resolution { Width = width, Height = height }));
        }

        [Fact]
        public void BuildTdEngineIni_AppliesTheCompatBucketInTheGamesFormat_ThenTheResolution()
        {
            var bucket = new Dictionary<string, string>
            {
                ["MaxAnisotropy"] = "16",
                ["Fullscreen"] = "true",
                ["ShadowTexelsPerPixel"] = "2.5",
                ["ResX"] = "1280",
                ["ResY"] = "1024",
            };

            string template = StockConfigFiles.BuildTdEngineIni(null, null);
            string applied = StockConfigFiles.BuildTdEngineIni(null, new ResolutionHelper.Resolution { Width = 2560, Height = 1440 }, bucket);

            Assert.Contains("\r\nFullscreen=True\r\n", template);
            Assert.Equal(
                template
                    .Replace("\r\nMaxAnisotropy=4\r\n", "\r\nMaxAnisotropy=16\r\n")
                    .Replace("\r\nShadowTexelsPerPixel=2.000000\r\n", "\r\nShadowTexelsPerPixel=2.500000\r\n")
                    .Replace("\r\nResX=1600\r\nResY=1200\r\n", "\r\nResX=2560\r\nResY=1440\r\n"),
                applied);
        }

        [Fact]
        public void RestoreUserConfigs_RewritesOnlyTheFilesThatExist_AndLeavesThemWritable()
        {
            using var temp = new TempDirectory();
            string tdEngine = Path.Combine(temp.Path, "TdEngine.ini");
            string tdInput = Path.Combine(temp.Path, "TdInput.ini");
            File.WriteAllText(tdInput, "[Engine.PlayerInput]\r\nBindings=(Name=\"F1\",Command=\"Noclip\")\r\n");
            File.SetAttributes(tdInput, FileAttributes.ReadOnly);

            List<string> restored = StockConfigFiles.RestoreUserConfigs(temp.Path, tdEngine, tdInput, "int", null);

            Assert.Equal(new[] { "TdInput.ini" }, restored);
            Assert.False(File.Exists(tdEngine));
            Assert.Equal(StockConfigFiles.ReadResource(StockConfigFiles.UserResourcePrefix + "TdInput.ini"), File.ReadAllBytes(tdInput));
            Assert.False(File.GetAttributes(tdInput).HasFlag(FileAttributes.ReadOnly));
        }

        [Fact]
        public void RestoreUserConfigs_KeepsTheGamesSections_AndAppliesTheirCompatBucket()
        {
            using var temp = new TempDirectory();
            string configDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "TdGame", "Config")).FullName;
            File.WriteAllText(Path.Combine(configDirectory, "DefaultCompat.ini"),
                "[AppCompatBucket4]\r\nMaxAnisotropy=4\r\n\r\n[AppCompatBucket5]\r\n; Shader model 3.0\r\nMaxAnisotropy=16\r\nResX=1600\r\nResY=1200\r\n");

            const string iniVersion = "[IniVersion]\r\n0=1780402791.000000\r\n1=1464609183.000000\r\n\r\n";
            const string appCompat = "[AppCompat]\r\nMeasuredCPUScore=108.389999\r\nCompatLevelComposite=5\r\n\r\n";
            string tdEngine = Path.Combine(temp.Path, "TdEngine.ini");
            string tdInput = Path.Combine(temp.Path, "TdInput.ini");
            File.WriteAllText(tdEngine, "[Engine.Engine]\r\nLanguage=deu\r\n\r\n" + iniVersion + appCompat + "[WinDrv.Accessibility]\r\nStickyKeysHotkey=False\r\n");
            File.WriteAllText(tdInput, "[Engine.PlayerInput]\r\nBindings=(Name=\"F1\",Command=\"Noclip\")\r\n\r\n[IniVersion]\r\n0=1464609175.000000\r\n1=1464609189.000000");
            var resolution = new ResolutionHelper.Resolution { Width = 3840, Height = 2160 };

            StockConfigFiles.RestoreUserConfigs(temp.Path, tdEngine, tdInput, "deu", resolution);

            var bucket = new Dictionary<string, string> { ["MaxAnisotropy"] = "16", ["ResX"] = "1600", ["ResY"] = "1200" };
            Assert.Equal(
                StockConfigFiles.BuildTdEngineIni("deu", resolution, bucket) + iniVersion + appCompat,
                File.ReadAllText(tdEngine, Encoding.Latin1));
            Assert.Equal(
                ReadUserTemplate("TdInput.ini") + "[IniVersion]\r\n0=1464609175.000000\r\n1=1464609189.000000\r\n\r\n",
                File.ReadAllText(tdInput, Encoding.Latin1));
        }

        private static string ReadUserTemplate(string name) =>
            Encoding.Latin1.GetString(StockConfigFiles.ReadResource(StockConfigFiles.UserResourcePrefix + name));
    }
}

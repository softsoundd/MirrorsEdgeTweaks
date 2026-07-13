using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class SteamInstallScriptPatcherTests
    {
        private static string FixturePath =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "17410_install.vdf");

        private static string ReadFixture() => File.ReadAllText(FixturePath);

        [Fact]
        public void NeedsPatch_ReturnsTrue_ForMirrorEdgeInstallScript()
        {
            Assert.True(SteamInstallScriptPatcher.NeedsPatch(ReadFixture()));
        }

        [Fact]
        public void Patch_RemovesLanguageBlocks_FromRegistryStringSections()
        {
            string patched = SteamInstallScriptPatcher.Patch(ReadFixture());

            Assert.DoesNotContain("\"Language\"\t\t\"English (UK)\"", patched);
            Assert.DoesNotContain("\"Language\"\t\t\"French\"", patched);
            Assert.DoesNotContain("\"Locale\"\t\t\"fr_FR\"", patched);
            Assert.DoesNotContain("\"Language\"\t\t\"German\"", patched);
        }

        [Fact]
        public void Patch_PreservesInstallKeysRunProcessAndSignatures()
        {
            string patched = SteamInstallScriptPatcher.Patch(ReadFixture());

            Assert.Contains("\"Install Dir\"", patched);
            Assert.Contains("\"run process\"", patched);
            Assert.Contains("\"DirectX\"", patched);
            Assert.Contains("\"PhysX Version\"", patched);
            Assert.Contains("\"kvsignatures\"", patched);
            Assert.Contains("\"EA OREG\"", patched);
        }

        [Fact]
        public void Patch_IsIdempotent()
        {
            string once = SteamInstallScriptPatcher.Patch(ReadFixture());
            string twice = SteamInstallScriptPatcher.Patch(once);

            Assert.Equal(once, twice);
            Assert.False(SteamInstallScriptPatcher.NeedsPatch(once));
        }

        [Fact]
        public void TryPatchFile_PatchesWritableCopy()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string scriptPath = Path.Combine(tempDir, "17410_install.vdf");
            File.Copy(FixturePath, scriptPath);

            try
            {
                SteamInstallScriptPatchFileResult result = SteamInstallScriptPatcher.TryPatchFile(scriptPath);

                Assert.Equal(SteamInstallScriptPatchStatus.Patched, result.Status);
                Assert.False(SteamInstallScriptPatcher.NeedsPatch(File.ReadAllText(scriptPath)));

                SteamInstallScriptPatchFileResult second = SteamInstallScriptPatcher.TryPatchFile(scriptPath);
                Assert.Equal(SteamInstallScriptPatchStatus.AlreadyClean, second.Status);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void FindInstallScriptPaths_IncludesGameDirectoryScripts()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string scriptPath = Path.Combine(tempDir, "installscript.vdf");
            File.Copy(FixturePath, scriptPath);

            try
            {
                IReadOnlyList<string> paths = SteamInstallScriptPatcher.FindInstallScriptPaths(tempDir, null);

                Assert.Contains(scriptPath, paths);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Patch_PreservesCrLfLineEndings()
        {
            string fixture = ReadFixture().Replace("\r\n", "\n");
            string crlfFixture = fixture.Replace("\n", "\r\n");

            string patched = SteamInstallScriptPatcher.Patch(crlfFixture);

            Assert.Contains("\r\n", patched);
            Assert.DoesNotContain("\"Language\"\t\t\"English (UK)\"", patched);
        }

        [Fact]
        public void SteamService_ApplyLanguageFix_SkipsNonSteamDirectory()
        {
            var service = new SteamService();
            SteamInstallScriptFixResult result = service.ApplyLanguageFix(@"C:\NotARealGame");

            Assert.Empty(result.PatchedFiles);
            Assert.Empty(result.AlreadyCleanFiles);
            Assert.Empty(result.FailedFiles);
        }
    }
}

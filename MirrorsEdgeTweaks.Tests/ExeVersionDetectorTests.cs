using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class ExeVersionDetectorTests
    {
        private const long VanillaSteamExeSize = 31946072;

        [Fact]
        public void IsSteamExecutable_DetectsSteam_FromCaveTag_WhenFileSizeDiffers()
        {
            byte[] pe = SyntheticPe.Build();
            var cave = CaveSection.Open(pe, versionTag: "steam");
            cave.Alloc(16);
            byte[] patched = cave.Finalize();

            Assert.NotEqual(VanillaSteamExeSize, patched.Length);

            string tempExe = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(tempExe, patched);

                Assert.True(ExeVersionDetector.IsSteamExecutable(tempExe));
            }
            finally
            {
                if (File.Exists(tempExe))
                    File.Delete(tempExe);
            }
        }

        [Fact]
        public void IsSteamExecutable_ReturnsFalse_ForGogCaveTag()
        {
            byte[] pe = SyntheticPe.Build();
            var cave = CaveSection.Open(pe, versionTag: "gog");
            cave.Alloc(16);
            byte[] patched = cave.Finalize();

            string tempExe = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(tempExe, patched);
                Assert.False(ExeVersionDetector.IsSteamExecutable(tempExe));
            }
            finally
            {
                if (File.Exists(tempExe))
                    File.Delete(tempExe);
            }
        }
    }
}

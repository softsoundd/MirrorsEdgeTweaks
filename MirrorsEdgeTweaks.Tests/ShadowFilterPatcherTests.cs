using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class ShadowFilterPatcherTests : IDisposable
    {
        private const string StockLoads = "F30F1047F8 F30F104E58 F30F106660";
        private const string FixedLoads = "F30F1047F8 F30F104FF4 F30F1067FC";
        private const int SiteCount = 5;

        private readonly TempDirectory _temp = new();

        public ShadowFilterPatcherTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void PatchImage_FixesEverySite_AndRemovingRestoresTheStockImage()
        {
            byte[] stock = BuildImage(SiteCount);
            Assert.False(ShadowFilterPatcher.IsAppliedToImage(stock));

            byte[] patched = ShadowFilterPatcher.PatchImage((byte[])stock.Clone(), patch: true)!;

            Assert.True(ShadowFilterPatcher.IsAppliedToImage(patched));
            Assert.Equal(BuildImage(SiteCount, fixedSites: SiteCount), patched);
            Assert.Equal(stock, ShadowFilterPatcher.PatchImage(patched, patch: false));
        }

        [Fact]
        public void PatchImage_ReturnsNull_WhenEverySiteAlreadyHasTheWantedLoads()
        {
            Assert.Null(ShadowFilterPatcher.PatchImage(BuildImage(SiteCount), patch: false));
            Assert.Null(ShadowFilterPatcher.PatchImage(BuildImage(SiteCount, fixedSites: SiteCount), patch: true));
        }

        [Fact]
        public void PatchImage_FinishesAPartialFix()
        {
            byte[] partial = BuildImage(SiteCount, fixedSites: 2);
            Assert.True(ShadowFilterPatcher.IsAppliedToImage(partial));

            Assert.Equal(BuildImage(SiteCount, fixedSites: SiteCount), ShadowFilterPatcher.PatchImage(partial, patch: true));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(6)]
        public void PatchImage_Throws_WhenTheExecutableDoesNotHoldFiveSites(int siteCount)
        {
            byte[] image = BuildImage(siteCount);

            Assert.Null(ShadowFilterPatcher.IsAppliedToImage(image));
            Assert.Throws<InvalidOperationException>(() => ShadowFilterPatcher.PatchImage(image, patch: true));
        }

        [Fact]
        public void ApplyAndRemove_RewriteTheExecutable()
        {
            byte[] stock = BuildImage(SiteCount);
            string exe = Path.Combine(_temp.Path, "MirrorsEdge.exe");
            File.WriteAllBytes(exe, stock);

            AssertAppliesAndRemoves(exe);
            Assert.Equal(stock, File.ReadAllBytes(exe));
        }

        [Theory(Explicit = true)]
        [InlineData("steam")]
        [InlineData("gog")]
        [InlineData("retail")]
        [InlineData("retold")]
        [InlineData("dlc")]
        public void ApplyAndRemove_OnTheStockExecutable(string version)
        {
            string exe = StockExecutables.CopyTo(version, _temp.Path);
            byte[] stock = File.ReadAllBytes(exe);

            AssertAppliesAndRemoves(exe);
            Assert.Equal(stock, File.ReadAllBytes(exe));
        }

        // The EA executable's code is encrypted, so this also needs the game's EA App licence on this PC.
        [Fact(Explicit = true)]
        public void ApplyAndRemove_OnTheEaExecutable()
        {
            string exe = StockExecutables.CopyTo("ea", _temp.Path);

            if (OoaService.FindLicensePath(File.ReadAllBytes(exe)) == null)
            {
                Assert.Skip("The EA App licence for Mirror's Edge is not on this PC.");
                return;
            }

            AssertAppliesAndRemoves(exe);
        }

        internal static byte[] BuildImage(int siteCount, int fixedSites = 0)
        {
            byte[] image = SyntheticPe.Build();
            for (int i = 0; i < siteCount; i++)
                SyntheticPe.Put(image, SyntheticPe.TextRaw + 0x10 + i * 0x40, i < fixedSites ? FixedLoads : StockLoads);
            return image;
        }

        private static void AssertAppliesAndRemoves(string exe)
        {
            Assert.False(ShadowFilterPatcher.IsApplied(exe));

            ShadowFilterPatcher.Apply(exe);
            Assert.True(ShadowFilterPatcher.IsApplied(exe));

            ShadowFilterPatcher.Remove(exe);
            Assert.False(ShadowFilterPatcher.IsApplied(exe));
        }
    }
}

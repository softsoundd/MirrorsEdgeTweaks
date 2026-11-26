using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class LanguageRegistryPatchTests : IDisposable
    {
        private const string Key = InlineVaPatchHelper.LanguageRegistryBypassKey;

        private readonly TempDirectory _temp = new();

        public LanguageRegistryPatchTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        // The patch replaces the jne after RegOpenKeyExW with a nop and a jmp, so the jmp must keep the jne's target.
        [Theory]
        [InlineData("steam")]
        [InlineData("gog")]
        [InlineData("ea")]
        [InlineData("retail")]
        [InlineData("retold")]
        [InlineData("dlc")]
        public void AddressTable_JumpsToTheStockTarget(string version)
        {
            InlinePatchDef patch = VersionAddressTable.Load(version).InlinePatches[Key];

            Assert.Equal(6, patch.OldBytes.Length);
            Assert.Equal(new byte[] { 0x0F, 0x85 }, patch.OldBytes[..2]);
            Assert.Equal(new byte[] { 0x90, 0xE9 }, patch.NewBytes[..2]);
            Assert.Equal(patch.OldBytes[2..], patch.NewBytes[2..]);
        }

        [Theory(Explicit = true)]
        [InlineData("steam")]
        [InlineData("gog")]
        [InlineData("retail")]
        [InlineData("retold")]
        [InlineData("dlc")]
        public void Patch_AppliesAndRemoves(string version)
        {
            string exe = StockExecutables.CopyTo(version, _temp.Path);
            byte[] stock = File.ReadAllBytes(exe);

            AssertAppliesAndRemoves(exe);
            Assert.Equal(stock, File.ReadAllBytes(exe));
        }

        // The EA executable's code is encrypted, so this also needs the game's EA App licence on this PC.
        [Fact(Explicit = true)]
        public void Patch_AppliesAndRemoves_OnTheEaExecutable()
        {
            string exe = StockExecutables.CopyTo("ea", _temp.Path);

            if (OoaService.FindLicensePath(File.ReadAllBytes(exe)) == null)
            {
                Assert.Skip("The EA App licence for Mirror's Edge is not on this PC.");
                return;
            }

            AssertAppliesAndRemoves(exe);
        }

        private static void AssertAppliesAndRemoves(string exe)
        {
            Assert.Equal(InlinePatchState.Unpatched, InlineVaPatchHelper.GetPatchState(exe, Key));

            InlineVaPatchHelper.ApplyPatch(exe, Key);
            Assert.Equal(InlinePatchState.Patched, InlineVaPatchHelper.GetPatchState(exe, Key));

            InlineVaPatchHelper.RemovePatch(exe, Key);
            Assert.Equal(InlinePatchState.Unpatched, InlineVaPatchHelper.GetPatchState(exe, Key));
        }
    }
}

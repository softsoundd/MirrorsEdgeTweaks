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

        // These tests patch copies of the stock executables from the gitignored ref folder,
        // so they only run when requested, and they are skipped when the folder is missing.
        [Theory(Explicit = true)]
        [InlineData(@"MirrorsEdgeSteam\MirrorsEdgeSteam.exe")]
        [InlineData(@"MirrorsEdgeGOG\MirrorsEdgeGOG.exe")]
        [InlineData(@"MirrorsEdgeRetail\MirrorsEdgeRetail.exe")]
        [InlineData(@"MirrorsEdgeRetailOld\MirrorsEdgeRetailOld.exe")]
        [InlineData(@"MirrorsEdgeDLC\MirrorsEdgeDLC.exe")]
        public void Patch_AppliesAndRemoves(string referenceExe)
        {
            if (CopyReferenceExe(referenceExe) is not { } exe)
            {
                Assert.Skip($"{referenceExe} is not in ref\\Mirrors-Edge-Version-KB.");
                return;
            }

            byte[] stock = File.ReadAllBytes(exe);

            AssertAppliesAndRemoves(exe);
            Assert.Equal(stock, File.ReadAllBytes(exe));
        }

        // The EA executable's code is encrypted, so this also needs the game's EA App licence on this PC.
        [Fact(Explicit = true)]
        public void Patch_AppliesAndRemoves_OnTheEaExecutable()
        {
            if (CopyReferenceExe(@"MirrorsEdgeEA\MirrorsEdgeEA.exe") is not { } exe)
            {
                Assert.Skip(@"MirrorsEdgeEA\MirrorsEdgeEA.exe is not in ref\Mirrors-Edge-Version-KB.");
                return;
            }

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

        // Returns null when the ref folder or the executable is missing.
        private string? CopyReferenceExe(string relativePath)
        {
            if (ReferenceExecutables.Find(relativePath) is not { } source)
                return null;

            string binaries = Path.Combine(_temp.Path, "Binaries");
            Directory.CreateDirectory(binaries);
            string exe = Path.Combine(binaries, "MirrorsEdge.exe");
            File.Copy(source, exe);
            return exe;
        }
    }
}

using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class InlineVaPatchHelperTests : IDisposable
    {
        private const string Key = InlineVaPatchHelper.MultiInstanceKey;

        private readonly TempDirectory _temp = new();

        public InlineVaPatchHelperTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void RemovePatch_RestoresTheOriginalBytes()
        {
            string exe = WriteSteamExe(SteamPatch.OldBytes);
            InlineVaPatchHelper.ApplyPatch(exe, Key);
            Assert.Equal(InlinePatchState.Patched, InlineVaPatchHelper.GetPatchState(exe, Key));

            InlineVaPatchHelper.RemovePatch(exe, Key);

            Assert.Equal(InlinePatchState.Unpatched, InlineVaPatchHelper.GetPatchState(exe, Key));
        }

        [Fact]
        public void RemovePatch_ThrowsWithoutWriting_WhenTheSiteHoldsOtherBytes()
        {
            string exe = WriteSteamExe([0x12, 0x34]);
            byte[] before = File.ReadAllBytes(exe);

            Assert.Throws<InvalidOperationException>(() => InlineVaPatchHelper.RemovePatch(exe, Key));

            Assert.Equal(before, File.ReadAllBytes(exe));
        }

        // The logging and supersampling removals report this case the same way.
        [Fact]
        public void RemovePatch_ThrowsWithoutWriting_ForAnExeOfAnUnknownVersion()
        {
            string exe = Path.Combine(_temp.Path, "MirrorsEdge.exe");
            File.WriteAllBytes(exe, SyntheticPe.BuildFlat());
            byte[] before = File.ReadAllBytes(exe);

            Assert.Throws<UserFacingException>(() => InlineVaPatchHelper.RemovePatch(exe, Key));
            Assert.Throws<UserFacingException>(() => LoggingPatchHelper.RemovePatch(exe));
            Assert.Throws<UserFacingException>(() => SupersamplePatchHelper.RemovePatch(exe));

            Assert.Equal(before, File.ReadAllBytes(exe));
        }

        private static InlinePatchDef SteamPatch => VersionAddressTable.Load("steam").InlinePatches[Key];

        // The Steam table's multi-instance site lies in BuildFlat's .text, and the cave's version tag identifies the exe as Steam.
        private string WriteSteamExe(byte[] siteBytes)
        {
            var cave = CaveSection.Open(SyntheticPe.BuildFlat(), versionTag: "steam");
            cave.Alloc(16);
            byte[] image = cave.Finalize();
            siteBytes.CopyTo(image, (int)(SteamPatch.Va - SyntheticPe.ImageBase));

            string exe = Path.Combine(_temp.Path, "MirrorsEdge.exe");
            File.WriteAllBytes(exe, image);
            return exe;
        }
    }
}

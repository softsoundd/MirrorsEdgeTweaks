using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class RenderTargetPatcherReconcileTests : IDisposable
    {
        // Every write replaces the file, so an old write time that survives a run shows that the run did not write.
        private static readonly DateTime OldWriteTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _temp = new();
        private readonly string _exePath;

        public RenderTargetPatcherReconcileTests()
        {
            PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");
            _exePath = Path.Combine(_temp.Path, "MirrorsEdge.exe");
        }

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void Reconcile_WritesEveryFix_ThenLeavesTheFileAlone()
        {
            // The cave's version tag identifies the build, as it does on an executable that Tweaks has patched before.
            byte[] original = CaveSection.Open(
                RenderTargetPatcherTests.BuildViewportImage(SyntheticPe.BuildFlat()), "steam").Finalize();
            byte[]? expected = RenderTargetPatcher.PatchImage((byte[])original.Clone(), "steam", out _);
            File.WriteAllBytes(_exePath, original);

            RenderTargetPatcher.Reconcile(_exePath);
            Assert.Equal(expected, File.ReadAllBytes(_exePath));

            File.SetLastWriteTimeUtc(_exePath, OldWriteTime);
            RenderTargetPatcher.Reconcile(_exePath);
            Assert.Equal(OldWriteTime, File.GetLastWriteTimeUtc(_exePath));
        }

        [Fact]
        public void Reconcile_SavesTheAllocateFix_BeforeReportingAnUnknownVersion()
        {
            // A synthetic executable has no version resource, so its version is unknown.
            byte[] original = RenderTargetPatcherTests.BuildViewportImage(SyntheticPe.BuildFlat());
            byte[]? expected = RenderTargetPatcher.PatchImage((byte[])original.Clone(), null, out _);
            File.WriteAllBytes(_exePath, original);

            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.Reconcile(_exePath));
            Assert.Equal(expected, File.ReadAllBytes(_exePath));

            File.SetLastWriteTimeUtc(_exePath, OldWriteTime);
            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.Reconcile(_exePath));
            Assert.Equal(OldWriteTime, File.GetLastWriteTimeUtc(_exePath));
        }
    }
}

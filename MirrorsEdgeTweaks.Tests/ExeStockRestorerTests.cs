using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    // These tests patch copies of the stock executables from the gitignored ref folder.
    // They take about a minute, so they only run when requested, and they are skipped when the folder is missing.
    [Collection("BackupRoot")]
    public sealed class ExeStockRestorerTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public ExeStockRestorerTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Theory(Explicit = true)]
        [InlineData(@"MirrorsEdgeSteam\MirrorsEdgeSteam.exe")]
        [InlineData(@"MirrorsEdgeGOG\MirrorsEdgeGOG.exe")]
        [InlineData(@"MirrorsEdgeRetail\MirrorsEdgeRetail.exe")]
        [InlineData(@"MirrorsEdgeRetailOld\MirrorsEdgeRetailOld.exe")]
        [InlineData(@"MirrorsEdgeDLC\MirrorsEdgeDLC.exe")]
        public void Restore_LeavesStockCode_AfterEveryPatchIsApplied(string referenceExe)
        {
            if (FindReferenceExe(referenceExe) is not { } source)
            {
                Assert.Skip($"{referenceExe} is not in ref\\Mirrors-Edge-Version-KB.");
                return;
            }

            string binaries = Path.Combine(_temp.Path, "Binaries");
            Directory.CreateDirectory(binaries);
            string exe = Path.Combine(binaries, "MirrorsEdge.exe");
            File.Copy(source, exe);
            byte[] stock = File.ReadAllBytes(exe);

            // The patches go on in a different order from their removal, so the cave reset has blocks to empty.
            RenderTargetPatcher.Reconcile(exe);
            MultiFontScalePatcher.Apply(exe);
            SubtitleRegionPatcher.Apply(exe);
            SupersamplePatchHelper.ApplyPatch(exe);
            SetCommandPatchHelper.EnsurePatchedIfApplicable(exe);
            LoggingPatchHelper.ApplyPatch(exe);
            ReversedDepthPatcher.Apply(exe);
            InlineVaPatchHelper.ApplyPatch(exe, InlineVaPatchHelper.MultiInstanceKey);
            InlineVaPatchHelper.ApplyPatch(exe, InlineVaPatchHelper.AmbiguousPackageKey);
            CommandLineUnlockHelper.Unlock(exe);

            ExeRestoreResult result = ExeStockRestorer.Restore(exe, resetCaves: true);

            Assert.Empty(result.Failures);
            Assert.Empty(result.Undetermined);
            Assert.Equal(SetCommandPatchHelper.IsDlcVersion(exe) ? 10 : 9, result.Removed.Count);
            AssertStockApartFromCaveHeaders(stock, File.ReadAllBytes(exe));
        }

        // A reset padding cave keeps its header, so the version tag stays readable.
        private static void AssertStockApartFromCaveHeaders(byte[] stock, byte[] restored)
        {
            var restoredPe = PeImageLayout.Parse(restored);
            int[] headers =
            {
                restoredPe.VaToOffset(CaveSection.Open((byte[])restored.Clone()).SectionVa),
                restoredPe.VaToOffset(CaveSection.OpenSecondary((byte[])restored.Clone()).SectionVa),
            };

            foreach (var section in PeImageLayout.Parse(stock).Sections)
            {
                int start = (int)section.PointerToRawData;
                for (int i = start; i < start + (int)section.SizeOfRawData; i++)
                {
                    if (restored[i] != stock[i] && !headers.Any(header => i >= header && i < header + CaveSection.HeaderSize))
                        Assert.Fail($"{section.Name}+0x{i - start:X} differs from the stock executable.");
                }
            }
        }

        // Returns null when the ref folder or the executable is missing.
        private static string? FindReferenceExe(string relativePath)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "ref", "Mirrors-Edge-Version-KB", relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}

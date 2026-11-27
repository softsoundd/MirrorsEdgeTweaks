using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    // These tests take about four minutes with all eight TdGame versions.
    [Collection("BackupRoot")]
    public sealed class TdGamePatcherPackageTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public TdGamePatcherPackageTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        // Together, the combinations build every variant of the blocks that Tweaks inserts.
        [Theory(Explicit = true)]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, true, true)]
        public void Remove_RestoresEveryStockPackage(bool sens, bool clip, bool skip)
        {
            var failures = new List<string>();
            foreach (string source in StockTdGamePackages.RequireAll())
            {
                string package = StockTdGamePackages.CopyTo(source, Path.Combine(_temp.Path, "package"));
                Assert.True(Flags(TdGamePatcher.DetectState(package)) == (false, false, false, false, false),
                    $"{source} already holds Tweaks patches, so it cannot stand in for the stock package.");
                byte[] stock = File.ReadAllBytes(package);

                TdGamePatcher.Reconcile(package, sens, clip, skip);

                var flags = Flags(TdGamePatcher.DetectState(package));
                var expected = (true, sens, clip, skip, true);
                if (flags != expected)
                    failures.Add($"{source}: Reconcile left (core, sens, clip, skip, fallback) at {flags}, not {expected}.");

                var changes = LegacyChangeDetector.DetectTdGame(package);
                if (changes.Count > 0)
                    failures.Add($"{source}: the startup check reports {string.Join(", ", changes.Select(c => c.Description))}.");

                TdGamePatcher.Remove(package);

                byte[] removed = File.ReadAllBytes(package);
                int difference = stock.AsSpan().CommonPrefixLength(removed);
                if (difference < stock.Length || removed.Length != stock.Length)
                    failures.Add($"{source}: Remove left the package different from stock at 0x{difference:X}.");
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private static (bool core, bool sens, bool clip, bool skip, bool fallback) Flags(TdGamePatchState state) =>
            (state.CoreApplied, state.SensApplied, state.ClipApplied, state.OnlineSkipApplied, state.OfflineFallbackApplied);
    }
}

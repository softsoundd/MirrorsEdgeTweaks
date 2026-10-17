using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class PendingReapplyStoreTests
    {
        private const string GameDir = @"C:\Games\Mirror's Edge";
        private const string OtherGameDir = @"D:\Mirror's Edge";

        [Fact]
        public void Load_RoundTripsEverySnapshotField()
        {
            using var dir = new TempDirectory();
            string path = Path.Combine(dir.Path, "pending.json");
            var snapshot = new TweakSnapshot
            {
                TdGamePatched = true,
                SensEnabled = true,
                ClipEnabled = true,
                OnlineSkipEnabled = true,
                UniformSensitivityTarget = UniformSensitivityPatcher.EnabledValue,
                GamepadButtonType = "ps3",
                HasLoadLastCheckpointKeybind = true,
                HasRestartTimeTrialKeybind = true,
                Fov = 105.5f,
                PhysXFps = 120,
                ConsoleInstalled = true,
                HighResFix = true,
            };

            new PendingReapplyStore(path).Save(GameDir, snapshot);

            Assert.Equivalent(snapshot, new PendingReapplyStore(path).Load(GameDir), strict: true);
        }

        [Fact]
        public void Load_MatchesTheDirectoryIgnoringCaseAndTrailingSeparator()
        {
            using var dir = new TempDirectory();
            var store = new PendingReapplyStore(Path.Combine(dir.Path, "pending.json"));

            store.Save(GameDir, new TweakSnapshot { Fov = 100f });

            Assert.Equal(100f, store.Load(GameDir.ToUpperInvariant() + @"\")?.Fov);
            Assert.Null(store.Load(OtherGameDir));
        }

        [Fact]
        public void Clear_RemovesOnlyThatDirectory_AndDeletesTheEmptyFile()
        {
            using var dir = new TempDirectory();
            string path = Path.Combine(dir.Path, "pending.json");
            var store = new PendingReapplyStore(path);
            store.Save(GameDir, new TweakSnapshot());
            store.Save(OtherGameDir, new TweakSnapshot());

            store.Clear(GameDir);
            Assert.Null(store.Load(GameDir));
            Assert.NotNull(store.Load(OtherGameDir));

            store.Clear(OtherGameDir);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Load_TreatsADamagedFileAsEmpty()
        {
            using var dir = new TempDirectory();
            string path = Path.Combine(dir.Path, "pending.json");
            File.WriteAllText(path, "{ not json");
            var store = new PendingReapplyStore(path);

            Assert.Null(store.Load(GameDir));

            store.Save(GameDir, new TweakSnapshot());
            Assert.NotNull(store.Load(GameDir));
        }
    }
}

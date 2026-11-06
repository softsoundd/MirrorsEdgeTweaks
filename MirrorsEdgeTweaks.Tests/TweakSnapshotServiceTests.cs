using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;
using MirrorsEdgeTweaks.Tests.TestSupport;
using MirrorsEdgeTweaks.ViewModels;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class TweakSnapshotServiceTests : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly GameSession _session = new();
        private readonly string _enginePath;

        public TweakSnapshotServiceTests()
        {
            PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");
            _session.Config.GameDirectoryPath = _temp.Path;

            string cookedPc = Directory.CreateDirectory(Path.Combine(_temp.Path, "TdGame", "CookedPC")).FullName;
            _enginePath = Path.Combine(cookedPc, "Engine.u");
            File.WriteAllBytes(_enginePath, new byte[32]);
        }

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            File.SetAttributes(_enginePath, FileAttributes.Normal);
            _temp.Dispose();
        }

        [Fact]
        public async Task ReapplyAsync_WritesTheFovAndConsoleHeight_IntoAReadOnlyEngineU_AtTheOffsetsTheLookupFinds()
        {
            File.SetAttributes(_enginePath, FileAttributes.ReadOnly);
            var gameData = new FakeGameDataService(_ =>
            {
                _session.Offsets.CameraFovOffset = 4;
                _session.Offsets.ConsoleHeightOffset = 20;
            });

            TweakReapplyResult result = await Reapply(gameData);

            byte[] engine = File.ReadAllBytes(_enginePath);
            Assert.Equal(100f, BitConverter.ToSingle(engine, 4));
            Assert.Equal(0.4f, BitConverter.ToSingle(engine, 20));
            Assert.Contains("Developer console height", result.ReappliedSettings);
            Assert.True(File.GetAttributes(_enginePath).HasFlag(FileAttributes.ReadOnly));
        }

        [Fact]
        public async Task ReapplyAsync_FailsBothWrites_WhenTheEngineOffsetLookupFails()
        {
            var gameData = new FakeGameDataService(_ => throw new InvalidOperationException("Engine.u could not be loaded."));

            TweakReapplyResult result = await Reapply(gameData);

            Assert.Contains("FOV patches: Engine.u could not be loaded.", result.FailureDetails);
            Assert.Contains("Developer console height: Engine.u could not be loaded.", result.FailureDetails);
            Assert.Equal(new byte[32], File.ReadAllBytes(_enginePath));
        }

        // The other dependencies only serve tweaks and scopes that these tests leave out, so they are null.
        private Task<TweakReapplyResult> Reapply(IGameDataService gameData) =>
            new TweakSnapshotService(_session, gameData, null!, null!, null!)
                .ReapplyAsync(new TweakSnapshot { Fov = 100f, ConsoleInstalled = true }, TweakScope.Engine);
    }
}

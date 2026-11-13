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

        [Fact]
        public async Task ReapplyAsync_AppliesTheRenderTargetFix_WhileRtxRemixNgxIsInstalled()
        {
            byte[] original = WriteExe(out string exePath);
            byte[]? expected = RenderTargetPatcher.PatchImage((byte[])original.Clone(), "steam", out _);

            TweakReapplyResult result = await ReapplyExe(new TweakSnapshot { RemixNgxInstalled = true });

            Assert.Equal(expected, File.ReadAllBytes(exePath));
            Assert.Contains("Render resolution", result.ReappliedSettings);
        }

        [Fact]
        public async Task ReapplyAsync_LeavesTheExeAlone_WithoutRtxRemixNgxOrALowerRenderResolution()
        {
            byte[] original = WriteExe(out string exePath);

            TweakReapplyResult result = await ReapplyExe(new TweakSnapshot());

            Assert.Equal(original, File.ReadAllBytes(exePath));
            Assert.Empty(result.ReappliedSettings);
        }

        // The other dependencies only serve tweaks and scopes that these tests leave out, so they are null.
        private Task<TweakReapplyResult> Reapply(IGameDataService gameData) =>
            new TweakSnapshotService(_session, gameData, null!, null!, null!)
                .ReapplyAsync(new TweakSnapshot { Fov = 100f, ConsoleInstalled = true }, TweakScope.Engine);

        // The empty user folder has no TdEngine.ini, so no render resolution is read.
        private Task<TweakReapplyResult> ReapplyExe(TweakSnapshot snapshot)
        {
            _session.Config.UserFolderPath = Path.Combine(_temp.Path, "UserFolder");
            return new TweakSnapshotService(_session, null!, null!, null!, null!).ReapplyAsync(snapshot, TweakScope.Exe);
        }

        // The cave's version tag identifies the build, as it does on an executable that Tweaks has patched before.
        private byte[] WriteExe(out string exePath)
        {
            byte[] image = CaveSection.Open(RenderTargetPatcherTests.BuildViewportImage(SyntheticPe.BuildFlat()), "steam").Finalize();
            exePath = Path.Combine(Directory.CreateDirectory(Path.Combine(_temp.Path, "Binaries")).FullName, "MirrorsEdge.exe");
            File.WriteAllBytes(exePath, image);
            return image;
        }
    }
}

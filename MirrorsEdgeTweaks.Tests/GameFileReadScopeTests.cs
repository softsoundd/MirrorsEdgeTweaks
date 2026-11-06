using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Windows.Threading;

namespace MirrorsEdgeTweaks.Tests
{
    public sealed class GameFileReadScopeTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void ReadAllBytes_ReturnsPrivateCopies_OutsideAScope()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);

            Assert.NotSame(GameFileReadScope.ReadAllBytes(path), GameFileReadScope.ReadAllBytes(path));
        }

        [Fact]
        public void ReadAllBytes_SharesOneRead_InsideAScope()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                byte[] first = GameFileReadScope.ReadAllBytes(path);

                Assert.Equal([1, 2, 3], first);
                Assert.Same(first, GameFileReadScope.ReadAllBytes(path));
            }
        }

        [Fact]
        public void ReadAllBytes_ReadsAgain_WhenTheFileChangesInsideAScope()
        {
            string path = CreateFile("TdGame.u", [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                byte[] before = GameFileReadScope.ReadAllBytes(path);
                File.WriteAllBytes(path, [4, 5, 6, 7]);

                Assert.Equal([1, 2, 3], before);
                Assert.Equal([4, 5, 6, 7], GameFileReadScope.ReadAllBytes(path));
            }
        }

        [Fact]
        public void ReadAllBytes_ThrowsFileNotFound_ForAMissingFileInsideAScope()
        {
            string missing = Path.Combine(_temp.Path, "Missing.u");

            using (GameFileReadScope.Begin())
            {
                Assert.Throws<FileNotFoundException>(() => GameFileReadScope.ReadAllBytes(missing));
            }
        }

        [Fact]
        public void Invalidate_DropsTheSharedRead()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                byte[] before = GameFileReadScope.ReadAllBytes(path);
                GameFileReadScope.Invalidate(path);

                Assert.NotSame(before, GameFileReadScope.ReadAllBytes(path));
            }
        }

        [Fact]
        public void Begin_JoinsTheActiveScope_SoOnlyTheOutermostScopeEndsSharing()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                byte[] outer = GameFileReadScope.ReadAllBytes(path);

                using (GameFileReadScope.Begin())
                {
                    Assert.Same(outer, GameFileReadScope.ReadAllBytes(path));
                }

                Assert.Same(outer, GameFileReadScope.ReadAllBytes(path));
            }
        }

        [Fact]
        public void Dispose_EndsSharing()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);

            byte[] scoped;
            using (GameFileReadScope.Begin())
            {
                scoped = GameFileReadScope.ReadAllBytes(path);
            }

            Assert.NotSame(scoped, GameFileReadScope.ReadAllBytes(path));
        }

        [Fact]
        public async Task Scope_FlowsIntoTaskRun()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                byte[] outer = GameFileReadScope.ReadAllBytes(path);
                byte[] inner = await Task.Run(() => GameFileReadScope.ReadAllBytes(path));

                Assert.Same(outer, inner);
            }
        }

        // GameDataService raises PackagesReloaded on a worker thread, and its handlers run on the UI thread through Dispatcher.Invoke.
        [Fact]
        public void Scope_FlowsIntoDispatcherInvoke_FromAWorkerThread()
        {
            string path = CreateFile("Engine.u", [1, 2, 3]);
            byte[]? onWorker = null;
            byte[]? onDispatcher = null;

            StaWpfTestRunner.Run(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();

                Task worker = Task.Run(() =>
                {
                    try
                    {
                        using (GameFileReadScope.Begin())
                        {
                            onWorker = GameFileReadScope.ReadAllBytes(path);
                            dispatcher.Invoke(() => onDispatcher = GameFileReadScope.ReadAllBytes(path));
                        }
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                });

                Dispatcher.PushFrame(frame);
                worker.GetAwaiter().GetResult();
                dispatcher.InvokeShutdown();
            });

            Assert.NotNull(onWorker);
            Assert.Same(onWorker, onDispatcher);
        }

        [Fact]
        public void ReadExe_SharesTheImageAndItsRawBytes_InsideAScope()
        {
            string path = CreateFile("MirrorsEdge.exe", SyntheticPe.Build());

            using (GameFileReadScope.Begin())
            {
                ExeImage image = GameFileReadScope.ReadExe(path);

                Assert.Same(image, GameFileReadScope.ReadExe(path));
                Assert.Same(image.Raw, GameFileReadScope.ReadAllBytes(path));
            }
        }

        [Fact]
        public void ExeImage_Version_MatchesTheDetectedVersion()
        {
            byte[] pe = SyntheticPe.Build();
            var cave = CaveSection.Open(pe, versionTag: "steam");
            cave.Alloc(16);
            string path = CreateFile("MirrorsEdge.exe", cave.Finalize());

            Assert.Equal("steam", GameFileReadScope.ReadExe(path).Version);
        }

        [Fact]
        public void ExeImage_GetDecrypted_ThrowsOnEveryCall_WhenTheLicenceIsMissing()
        {
            string path = CreateFile("MirrorsEdge.exe", SyntheticPe.Build());
            ExeImage image = GameFileReadScope.ReadExe(path);

            Assert.Throws<InvalidOperationException>(() => image.GetDecrypted());
            Assert.Throws<InvalidOperationException>(() => image.GetDecrypted());
        }

        private string CreateFile(string name, byte[] content)
        {
            string path = Path.Combine(_temp.Path, name);
            File.WriteAllBytes(path, content);
            return path;
        }
    }

    [Collection("BackupRoot")]
    public sealed class GameFileReadScopeWriteTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public GameFileReadScopeWriteTests() =>
            PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void WritePreservingAttributes_DropsTheSharedRead()
        {
            string path = Path.Combine(_temp.Path, "Engine.u");
            File.WriteAllBytes(path, [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                Assert.Equal([1, 2, 3], GameFileReadScope.ReadAllBytes(path));

                PatchUtility.WritePreservingAttributes(path, [7, 8, 9]);

                Assert.Equal([7, 8, 9], GameFileReadScope.ReadAllBytes(path));
            }
        }

        [Fact]
        public void PatchPreservingAttributes_DropsTheSharedRead()
        {
            string path = Path.Combine(_temp.Path, "Engine.u");
            File.WriteAllBytes(path, [1, 2, 3]);

            using (GameFileReadScope.Begin())
            {
                Assert.Equal([1, 2, 3], GameFileReadScope.ReadAllBytes(path));

                PatchUtility.PatchPreservingAttributes(path, [(1, new byte[] { 8 })]);

                Assert.Equal([1, 8, 3], GameFileReadScope.ReadAllBytes(path));
            }
        }
    }
}

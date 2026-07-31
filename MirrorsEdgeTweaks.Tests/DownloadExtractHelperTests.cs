using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;
using MirrorsEdgeTweaks.ViewModels;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.Tests
{
    public class DownloadExtractHelperTests
    {
        private sealed class TestBusyViewModel : BusyViewModel
        {
            public TestBusyViewModel(GameSession session, GameStatusViewModel gameStatus, DownloadProgressViewModel downloadProgress)
                : base(session, gameStatus, downloadProgress)
            {
            }

            public Task RunExtractAsync(
                IDownloadService download,
                IFileService fileService,
                string url,
                string extractPath,
                string statusPrefix,
                Func<string, string, Task>? customExtract = null,
                Func<Task>? afterExtract = null) =>
                RunDownloadAndExtractAsync(download, fileService, url, extractPath, statusPrefix, customExtract, afterExtract);
        }

        [Fact]
        public async Task RunDownloadAndExtractAsync_DownloadsExtractsAndDeletesTempZip()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"metweaks_dl_test_{Guid.NewGuid():N}");
            string extractPath = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractPath);

            var fileService = new TempDirectoryFileService(Path.Combine(tempRoot, "tmp"));
            var download = new FakeDownloadService();
            var session = new GameSession();
            var gameStatus = new GameStatusViewModel();
            var downloadProgress = new DownloadProgressViewModel();
            var vm = new TestBusyViewModel(session, gameStatus, downloadProgress);

            await vm.RunExtractAsync(download, fileService, "https://example.test/asset.zip", extractPath, "test files");

            Assert.True(File.Exists(Path.Combine(extractPath, "payload.txt")));
            Assert.Empty(Directory.GetFiles(fileService.GetTempPath(), "metweaks_*.zip"));
        }

        [Fact]
        public async Task RunDownloadAndExtractAsync_InvokesAfterExtractHook()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"metweaks_dl_test_{Guid.NewGuid():N}");
            string extractPath = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractPath);

            var fileService = new TempDirectoryFileService(Path.Combine(tempRoot, "tmp"));
            var download = new FakeDownloadService();
            var session = new GameSession();
            var gameStatus = new GameStatusViewModel();
            var downloadProgress = new DownloadProgressViewModel();
            var vm = new TestBusyViewModel(session, gameStatus, downloadProgress);
            bool afterExtractCalled = false;

            await vm.RunExtractAsync(
                download,
                fileService,
                "https://example.test/asset.zip",
                extractPath,
                "test files",
                afterExtract: () =>
                {
                    afterExtractCalled = true;
                    return Task.CompletedTask;
                });

            Assert.True(afterExtractCalled);
        }

        [Fact]
        public async Task RunDownloadAndExtractAsync_ReportsMonotonicProgressAndFlushesTo100()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"metweaks_dl_test_{Guid.NewGuid():N}");
            string extractPath = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractPath);

            var fileService = new TempDirectoryFileService(Path.Combine(tempRoot, "tmp"));
            var download = new RapidProgressFakeDownloadService();
            var downloadProgress = new DownloadProgressViewModel();
            var vm = new TestBusyViewModel(new GameSession(), new GameStatusViewModel(), downloadProgress);

            var progressValues = new List<double>();
            double maxProgress = 0;
            downloadProgress.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DownloadProgressViewModel.DownloadProgressValue))
                    return;

                progressValues.Add(downloadProgress.DownloadProgressValue);
                maxProgress = Math.Max(maxProgress, downloadProgress.DownloadProgressValue);
            };

            await vm.RunExtractAsync(download, fileService, "https://example.test/asset.zip", extractPath, "test files");

            Assert.Equal(100, maxProgress);
            Assert.NotEmpty(progressValues);
            for (int i = 1; i < progressValues.Count; i++)
            {
                if (progressValues[i] < progressValues[i - 1])
                {
                    Assert.Equal(0, progressValues[i]);
                    break;
                }
            }
        }

        [Fact]
        public async Task RunDownloadAndExtractAsync_DoesNotRewriteStatusWithPercentDuringDownload()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"metweaks_dl_test_{Guid.NewGuid():N}");
            string extractPath = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractPath);

            var fileService = new TempDirectoryFileService(Path.Combine(tempRoot, "tmp"));
            var download = new RapidProgressFakeDownloadService();
            var gameStatus = new GameStatusViewModel();
            var downloadProgress = new DownloadProgressViewModel();
            var vm = new TestBusyViewModel(new GameSession(), gameStatus, downloadProgress);

            var statusSnapshots = new List<string>();
            gameStatus.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GameStatusViewModel.Status))
                    statusSnapshots.Add(gameStatus.Status);
            };

            await vm.RunExtractAsync(download, fileService, "https://example.test/asset.zip", extractPath, "test files");

            Assert.Contains(statusSnapshots, s => s == "Downloading test files...");
            Assert.DoesNotContain(statusSnapshots, s => s.Contains('%'));
        }

        [Fact]
        public void DownloadProgressViewModel_IsDownloadProgressPercentVisible_OnlyWhenDeterminateAndVisible()
        {
            var progress = new DownloadProgressViewModel();

            Assert.False(progress.IsDownloadProgressPercentVisible);

            progress.IsDownloadProgressVisible = true;
            Assert.True(progress.IsDownloadProgressPercentVisible);

            progress.IsDownloadProgressIndeterminate = true;
            Assert.False(progress.IsDownloadProgressPercentVisible);

            progress.IsDownloadProgressIndeterminate = false;
            Assert.True(progress.IsDownloadProgressPercentVisible);
        }

        private sealed class FakeDownloadService : IDownloadService
        {
            public Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
            {
                onProgress?.Invoke(50);
                WritePayloadZip(destinationPath);
                onProgress?.Invoke(100);
                return Task.CompletedTask;
            }
        }

        private sealed class RapidProgressFakeDownloadService : IDownloadService
        {
            public Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
            {
                for (int i = 0; i <= 100; i += 5)
                    onProgress?.Invoke(i);

                WritePayloadZip(destinationPath);
                return Task.CompletedTask;
            }
        }

        private static void WritePayloadZip(string destinationPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var zipStream = File.Create(destinationPath);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true);
            var entry = archive.CreateEntry("payload.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write("ok");
        }
    }
}

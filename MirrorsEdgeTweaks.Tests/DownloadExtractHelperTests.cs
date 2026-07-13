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

        private sealed class FakeDownloadService : IDownloadService
        {
            public async Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
            {
                onProgress?.Invoke(50);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                await using var zipStream = File.Create(destinationPath);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true);
                var entry = archive.CreateEntry("payload.txt");
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream);
                await writer.WriteAsync("ok");
                onProgress?.Invoke(100);
            }
        }
    }
}

using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;
using MirrorsEdgeTweaks.Tests.TestSupport;
using MirrorsEdgeTweaks.ViewModels;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Tests
{
    public class ModsViewModelConsoleTests
    {
        [Theory]
        [InlineData(true, true, false, "Please select a valid user folder first.")]
        [InlineData(false, true, false, "Please select a valid user folder first.")]
        [InlineData(true, false, true, "Please select a valid game directory first.")]
        [InlineData(false, false, true, "Please select a valid game directory first.")]
        [InlineData(true, false, false, "Please select a valid game directory first.")]
        [InlineData(false, false, false, "Please select a valid game directory first.")]
        public async Task ConsoleCommand_MissingPath_NamesTheFolderThatIsMissing(
            bool install, bool hasGameDirectory, bool hasUserFolder, string expectedMessage)
        {
            var (viewModel, dialogs, _) = CreateViewModel(hasGameDirectory, hasUserFolder);

            await RunConsoleCommandAsync(viewModel, install);

            var message = Assert.Single(dialogs.Messages);
            Assert.Equal("Error", message.Title);
            Assert.Equal(expectedMessage, message.Message);
            Assert.Equal(DialogMessageType.Error, message.Type);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConsoleCommand_MissingEnginePackage_AsksForGameDirectory(bool install)
        {
            var (viewModel, dialogs, session) = CreateViewModel(hasGameDirectory: true, hasUserFolder: true);
            session.Config.EnginePackagePath = null;

            await RunConsoleCommandAsync(viewModel, install);

            Assert.Equal("Please select a valid game directory first.", Assert.Single(dialogs.Messages).Message);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task ConsoleCommand_OneIniMissing_AsksForUserFolder(bool install, bool clearTdEngineIni)
        {
            var (viewModel, dialogs, session) = CreateViewModel(hasGameDirectory: true, hasUserFolder: true);
            if (clearTdEngineIni)
            {
                session.Config.TdEngineIniPath = null;
            }
            else
            {
                session.Config.TdInputIniPath = null;
            }

            await RunConsoleCommandAsync(viewModel, install);

            Assert.Equal("Please select a valid user folder first.", Assert.Single(dialogs.Messages).Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConsoleCommand_BothFoldersPresent_ReportsMissingConsoleOffset(bool install)
        {
            var (viewModel, dialogs, _) = CreateViewModel(hasGameDirectory: true, hasUserFolder: true);

            await RunConsoleCommandAsync(viewModel, install);

            Assert.Equal("Patch Error", Assert.Single(dialogs.Messages).Title);
        }

        [Fact]
        public async Task InstallConsole_FailedDownload_LeavesTheIniFilesUnchanged()
        {
            using var temp = new TempDirectory();
            string tdEngineIni = Path.Combine(temp.Path, "TdEngine.ini");
            string tdInputIni = Path.Combine(temp.Path, "TdInput.ini");
            string[] engineLines = ["[Engine.Engine]", "ConsoleClassName=TdGame.TdConsole"];
            string[] inputLines = ["[Engine.Console]", "TypeKey=None"];
            File.WriteAllLines(tdEngineIni, engineLines);
            File.WriteAllLines(tdInputIni, inputLines);

            var session = new GameSession();
            session.Config.GameDirectoryPath = temp.Path;
            session.Config.EnginePackagePath = Path.Combine(temp.Path, "Engine.u");
            session.Config.TdEngineIniPath = tdEngineIni;
            session.Config.TdInputIniPath = tdInputIni;
            session.Offsets.ConsoleHeightOffset = 100;

            var dialogs = new RecordingDialogService();
            var viewModel = new ModsViewModel(
                dialogs,
                new TempDirectoryFileService(Path.Combine(temp.Path, "tmp")),
                new FakeGameDataService(_ => { }),
                new FailingDownloadService(),
                new FixedAssetUrlProvider(),
                session,
                new GameStatusViewModel(),
                new DownloadProgressViewModel(),
                new TweaksScriptsViewModel());

            await viewModel.InstallConsoleCommand.ExecuteAsync(null);

            Assert.Equal("Installation Failed", Assert.Single(dialogs.Messages).Title);
            Assert.Equal(engineLines, File.ReadAllLines(tdEngineIni));
            Assert.Equal(inputLines, File.ReadAllLines(tdInputIni));
        }

        private static async Task RunConsoleCommandAsync(ModsViewModel viewModel, bool install)
        {
            if (install)
            {
                await viewModel.InstallConsoleCommand.ExecuteAsync(null);
            }
            else
            {
                await viewModel.UninstallConsoleCommand.ExecuteAsync(null);
            }
        }

        private static (ModsViewModel ViewModel, RecordingDialogService Dialogs, GameSession Session) CreateViewModel(
            bool hasGameDirectory, bool hasUserFolder)
        {
            var session = new GameSession();
            if (hasGameDirectory)
            {
                session.Config.GameDirectoryPath = @"C:\Games\Mirrors Edge";
                session.Config.EnginePackagePath = @"C:\Games\Mirrors Edge\TdGame\CookedPC\Engine.u";
            }

            if (hasUserFolder)
            {
                session.Config.TdEngineIniPath = @"C:\Users\Player\Documents\EA Games\Mirror's Edge\TdGame\Config\TdEngine.ini";
                session.Config.TdInputIniPath = @"C:\Users\Player\Documents\EA Games\Mirror's Edge\TdGame\Config\TdInput.ini";
            }

            var dialogs = new RecordingDialogService();
            var viewModel = new ModsViewModel(
                dialogs,
                new InMemoryFileService(),
                new FakeGameDataService(_ => { }),
                new UnusedDownloadService(),
                new UnusedAssetUrlProvider(),
                session,
                new GameStatusViewModel(),
                new DownloadProgressViewModel(),
                new TweaksScriptsViewModel());
            return (viewModel, dialogs, session);
        }

        private sealed class UnusedDownloadService : IDownloadService
        {
            public Task<string?> DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<string?> GetValidatorAsync(string url, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class FailingDownloadService : IDownloadService
        {
            public Task<string?> DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default) =>
                throw new HttpRequestException("No internet connection.");

            public Task<string?> GetValidatorAsync(string url, CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(null);
        }

        private sealed class FixedAssetUrlProvider : IAssetUrlProvider
        {
            public string AssetBase => "https://example.test/";

            public string For(string fileName) => AssetBase + fileName;

            public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class UnusedAssetUrlProvider : IAssetUrlProvider
        {
            public string AssetBase => throw new NotSupportedException();

            public string For(string fileName) => throw new NotSupportedException();

            public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}

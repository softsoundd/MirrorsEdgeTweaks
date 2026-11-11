using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MirrorsEdgeTweaks.Tests
{
    public sealed class GameLauncherServiceTests : IDisposable
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorAccessDenied = 5;
        private const int ErrorBadExeFormat = 193;
        private const int ErrorVirusInfected = 225;
        private const int ErrorVirusDeleted = 226;
        private const int ErrorElevationRequired = 740;
        private const int ErrorCancelled = 1223;
        private const int ErrorAccessDisabledByPolicy = 1260;

        private readonly TempDirectory _temp = new();
        private readonly FakeProcessStarter _starter = new();
        private readonly string _binaries;
        private readonly string _gameExe;

        public GameLauncherServiceTests()
        {
            _binaries = Path.Combine(_temp.Path, "Binaries");
            Directory.CreateDirectory(_binaries);
            _gameExe = Path.Combine(_binaries, "MirrorsEdge.exe");
            File.WriteAllBytes(_gameExe, SyntheticPe.Build());
        }

        public void Dispose() => _temp.Dispose();

        [Theory]
        [InlineData("", "-applaunch 17410")]
        [InlineData("   ", "-applaunch 17410")]
        [InlineData("-nosound", "-applaunch 17410 -nosound")]
        [InlineData("  -nosound  ", "-applaunch 17410 -nosound")]
        [InlineData("-windowed -nosound", "-applaunch 17410 -windowed -nosound")]
        public void BuildSteamApplaunchArguments_FormatsExpectedCommandLine(string gameArguments, string expected)
        {
            Assert.Equal(expected, GameLauncherService.BuildSteamApplaunchArguments(gameArguments));
        }

        [Fact]
        public void Launch_StartsTheGameExeDirectlyInItsOwnFolder()
        {
            CreateLauncher().Launch(_gameExe, "-nosound");

            Assert.Equal(new[] { new StartCall(Route.Direct, _gameExe, "-nosound", _binaries) }, _starter.Calls);
        }

        [Fact]
        public void Launch_PassesTheArgumentsToTheGameUnchanged()
        {
            const string arguments = "-exec=remix & calc.exe | more > out.txt";

            CreateLauncher().Launch(_gameExe, arguments);

            StartCall call = Assert.Single(_starter.Calls);
            Assert.Equal(_gameExe, call.FileName);
            Assert.Equal(arguments, call.Arguments);
        }

        [Fact]
        public void Launch_UsesTheShellOnlyWhenTheGameNeedsAnAdministratorPrompt()
        {
            _starter.DirectFailure = new Win32Exception(ErrorElevationRequired);

            CreateLauncher().Launch(_gameExe, "-nosound");

            Assert.Equal(
                new[]
                {
                    new StartCall(Route.Direct, _gameExe, "-nosound", _binaries),
                    new StartCall(Route.Shell, _gameExe, "-nosound", _binaries),
                },
                _starter.Calls);
        }

        [Fact]
        public void Launch_DoesNotReportADeclinedAdministratorPrompt()
        {
            _starter.DirectFailure = new Win32Exception(ErrorElevationRequired);
            _starter.ShellFailure = new Win32Exception(ErrorCancelled);

            CreateLauncher().Launch(_gameExe, string.Empty);

            Assert.Equal(new[] { Route.Direct, Route.Shell }, _starter.Calls.Select(call => call.Route));
        }

        [Fact]
        public void Launch_ReportsAFailureAfterTheAdministratorPrompt()
        {
            _starter.DirectFailure = new Win32Exception(ErrorElevationRequired);
            _starter.ShellFailure = new Win32Exception(ErrorAccessDenied);

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.Equal("Windows could not start Mirror's Edge.", ex.Message);
            Assert.Same(_starter.ShellFailure, ex.InnerException);
            Assert.Equal(new[] { Route.Direct, Route.Shell }, _starter.Calls.Select(call => call.Route));
        }

        [Theory]
        [InlineData(ErrorVirusInfected)]
        [InlineData(ErrorVirusDeleted)]
        public void Launch_ReportsAnAntivirusBlockWithoutTryingAnotherRoute(int errorCode)
        {
            _starter.DirectFailure = new Win32Exception(errorCode);

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.StartsWith("Antivirus software blocked MirrorsEdge.exe from starting", ex.Message);
            Assert.Same(_starter.DirectFailure, ex.InnerException);
            Assert.Equal(new[] { Route.Direct }, _starter.Calls.Select(call => call.Route));
        }

        [Fact]
        public void Launch_ReportsAnAntivirusBlockAfterTheAdministratorPrompt()
        {
            _starter.DirectFailure = new Win32Exception(ErrorElevationRequired);
            _starter.ShellFailure = new Win32Exception(ErrorVirusInfected);

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.StartsWith("Antivirus software blocked MirrorsEdge.exe from starting", ex.Message);
        }

        [Fact]
        public void Launch_ReportsAPolicyBlockWithoutTryingAnotherRoute()
        {
            _starter.DirectFailure = new Win32Exception(ErrorAccessDisabledByPolicy);

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.Equal("A security policy on this PC blocked MirrorsEdge.exe from starting.", ex.Message);
            Assert.Same(_starter.DirectFailure, ex.InnerException);
            Assert.Equal(new[] { Route.Direct }, _starter.Calls.Select(call => call.Route));
        }

        [Theory]
        [InlineData(ErrorFileNotFound)]
        [InlineData(ErrorAccessDenied)]
        [InlineData(ErrorBadExeFormat)]
        [InlineData(ErrorCancelled)]
        public void Launch_ReportsAnyOtherFailureWithoutTryingAnotherRoute(int errorCode)
        {
            _starter.DirectFailure = new Win32Exception(errorCode);

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.Equal("Windows could not start Mirror's Edge.", ex.Message);
            Assert.Same(_starter.DirectFailure, ex.InnerException);
            Assert.Equal(new[] { Route.Direct }, _starter.Calls.Select(call => call.Route));
        }

        [Fact]
        public void Launch_ReportsAFailureThatIsNotAWin32Error()
        {
            _starter.DirectFailure = new InvalidOperationException("Cannot start process because a file name has not been provided.");

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.Equal("Windows could not start Mirror's Edge.", ex.Message);
            Assert.Same(_starter.DirectFailure, ex.InnerException);
        }

        [Fact]
        public void Launch_AsksTheDesktopShellToStartTheGame_WhenTweaksIsElevated()
        {
            _starter.IsElevated = true;

            CreateLauncher().Launch(_gameExe, "-nosound");

            Assert.Equal(new[] { new StartCall(Route.Unelevated, _gameExe, "-nosound", _binaries) }, _starter.Calls);
        }

        [Fact]
        public void Launch_StartsTheGameItself_WhenTweaksIsElevatedAndThereIsNoDesktopShell()
        {
            _starter.IsElevated = true;
            _starter.HasDesktopShell = false;

            CreateLauncher().Launch(_gameExe, "-nosound");

            Assert.Equal(
                new[]
                {
                    new StartCall(Route.Unelevated, _gameExe, "-nosound", _binaries),
                    new StartCall(Route.Direct, _gameExe, "-nosound", _binaries),
                },
                _starter.Calls);
        }

        // The desktop shell may have started the game before its request failed.
        [Fact]
        public void Launch_ReportsAFailedDesktopShellRequestWithoutStartingTheGameItself()
        {
            _starter.IsElevated = true;
            _starter.UnelevatedFailure = new COMException("The RPC server is unavailable.");

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.Equal("Windows could not start Mirror's Edge.", ex.Message);
            Assert.Same(_starter.UnelevatedFailure, ex.InnerException);
            Assert.Equal(new[] { Route.Unelevated }, _starter.Calls.Select(call => call.Route));
        }

        [Fact]
        public void Launch_AsksTheDesktopShellToStartSteam_WhenTweaksIsElevated()
        {
            string steamExe = CreateSteamInstall();
            MakeGameExeTheSteamBuild();
            _starter.IsElevated = true;

            CreateLauncher(steamExe).Launch(_gameExe, "-nosound");

            Assert.Equal(
                new[] { new StartCall(Route.Unelevated, steamExe, "-applaunch 17410 -nosound", Path.GetDirectoryName(steamExe)!) },
                _starter.Calls);
        }

        [Fact]
        public void Launch_StartsTheSteamBuildThroughSteam()
        {
            string steamExe = CreateSteamInstall();
            MakeGameExeTheSteamBuild();

            CreateLauncher(steamExe).Launch(_gameExe, " -nosound ");

            Assert.Equal(
                new[] { new StartCall(Route.Direct, steamExe, "-applaunch 17410 -nosound", Path.GetDirectoryName(steamExe)!) },
                _starter.Calls);
        }

        [Fact]
        public void Launch_NamesSteamWhenSteamFailsToStart()
        {
            string steamExe = CreateSteamInstall();
            MakeGameExeTheSteamBuild();
            _starter.DirectFailure = new Win32Exception(ErrorAccessDenied);

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher(steamExe).Launch(_gameExe, string.Empty));

            Assert.Equal("Steam could not start Mirror's Edge.", ex.Message);
            Assert.Equal(new[] { Route.Direct }, _starter.Calls.Select(call => call.Route));
        }

        [Fact]
        public void Launch_ReportsMissingSteamWithoutStartingAnything()
        {
            MakeGameExeTheSteamBuild();

            var ex = Assert.Throws<UserFacingException>(() => CreateLauncher().Launch(_gameExe, string.Empty));

            Assert.Equal("Could not find Steam. Make sure Steam is installed.", ex.Message);
            Assert.Empty(_starter.Calls);
        }

        private GameLauncherService CreateLauncher(string? steamExe = null) =>
            new(new FakeSteamService(steamExe), _starter);

        private string CreateSteamInstall()
        {
            string steamDirectory = Path.Combine(_temp.Path, "Steam");
            Directory.CreateDirectory(steamDirectory);
            string steamExe = Path.Combine(steamDirectory, "steam.exe");
            File.WriteAllBytes(steamExe, Array.Empty<byte>());
            return steamExe;
        }

        private void MakeGameExeTheSteamBuild()
        {
            var cave = CaveSection.Open(SyntheticPe.Build(), versionTag: "steam");
            cave.Alloc(16);
            File.WriteAllBytes(_gameExe, cave.Finalize());
        }

        private enum Route
        {
            Direct,
            Shell,
            Unelevated
        }

        private sealed record StartCall(Route Route, string FileName, string Arguments, string WorkingDirectory);

        private sealed class FakeProcessStarter : IProcessStarter
        {
            public List<StartCall> Calls { get; } = new();
            public bool IsElevated { get; set; }
            public bool HasDesktopShell { get; set; } = true;
            public Exception? DirectFailure { get; set; }
            public Exception? ShellFailure { get; set; }
            public Exception? UnelevatedFailure { get; set; }

            public void StartDirect(string fileName, string arguments, string workingDirectory)
            {
                Calls.Add(new StartCall(Route.Direct, fileName, arguments, workingDirectory));
                if (DirectFailure != null)
                    throw DirectFailure;
            }

            public void StartViaShell(string fileName, string arguments, string workingDirectory)
            {
                Calls.Add(new StartCall(Route.Shell, fileName, arguments, workingDirectory));
                if (ShellFailure != null)
                    throw ShellFailure;
            }

            public bool TryStartUnelevated(string fileName, string arguments, string workingDirectory)
            {
                Calls.Add(new StartCall(Route.Unelevated, fileName, arguments, workingDirectory));
                if (UnelevatedFailure != null)
                    throw UnelevatedFailure;
                return HasDesktopShell;
            }
        }

        private sealed class FakeSteamService : ISteamService
        {
            private readonly string? _steamExe;

            public FakeSteamService(string? steamExe) => _steamExe = steamExe;

            public string? GetSteamInstallPath() => _steamExe == null ? null : Path.GetDirectoryName(_steamExe);
            public string? GetSteamExecutablePath() => _steamExe;
            public bool IsSteamGameDirectory(string gameDirectory) => false;
            public SteamInstallScriptFixResult ApplyLanguageFix(string gameDirectory) => new();
        }
    }
}

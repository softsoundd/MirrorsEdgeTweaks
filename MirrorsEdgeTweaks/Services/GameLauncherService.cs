using MirrorsEdgeTweaks.Helpers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IGameLauncher
    {
        void Launch(string exePath, string arguments);
    }

    internal interface IProcessStarter
    {
        bool IsElevated { get; }

        void StartDirect(string fileName, string arguments, string workingDirectory);
        void StartViaShell(string fileName, string arguments, string workingDirectory);
        bool TryStartUnelevated(string fileName, string arguments, string workingDirectory);
    }

    public class GameLauncherService : IGameLauncher
    {
        private const int ErrorVirusInfected = 225;
        private const int ErrorVirusDeleted = 226;
        private const int ErrorElevationRequired = 740;
        private const int ErrorCancelled = 1223;
        private const int ErrorAccessDisabledByPolicy = 1260;

        private readonly ISteamService _steamService;
        private readonly IProcessStarter _processStarter;

        public GameLauncherService(ISteamService steamService)
            : this(steamService, new ProcessStarter())
        {
        }

        internal GameLauncherService(ISteamService steamService, IProcessStarter processStarter)
        {
            _steamService = steamService;
            _processStarter = processStarter;
        }

        public void Launch(string exePath, string arguments)
        {
            if (ExeVersionDetector.IsSteamExecutable(exePath))
            {
                LaunchViaSteam(arguments);
                return;
            }

            string? workingDirectory = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                throw new InvalidOperationException("Could not determine a valid game working directory.");
            }

            Start(exePath, arguments, workingDirectory, "Windows could not start Mirror's Edge.");
        }

        internal static string BuildSteamApplaunchArguments(string gameArguments)
        {
            string args = $"-applaunch {SteamInstallScriptPatcher.MirrorsEdgeAppId}";
            string trimmed = gameArguments.Trim();
            if (trimmed.Length > 0)
                args += " " + trimmed;
            return args;
        }

        private void LaunchViaSteam(string gameArguments)
        {
            string? steamExe = _steamService.GetSteamExecutablePath();
            if (string.IsNullOrWhiteSpace(steamExe))
            {
                throw new UserFacingException("Could not find Steam. Make sure Steam is installed.");
            }

            string? steamDirectory = Path.GetDirectoryName(steamExe);
            if (string.IsNullOrWhiteSpace(steamDirectory) || !Directory.Exists(steamDirectory))
            {
                throw new InvalidOperationException("Could not determine a valid Steam working directory.");
            }

            Start(
                steamExe,
                BuildSteamApplaunchArguments(gameArguments),
                steamDirectory,
                "Steam could not start Mirror's Edge.");
        }

        // A failed start is reported and not retried by another route.
        // A second route cannot start what Windows or antivirus software refused, and it could start the program twice.
        private void Start(string fileName, string arguments, string workingDirectory, string failureMessage)
        {
            try
            {
                if (_processStarter.IsElevated && _processStarter.TryStartUnelevated(fileName, arguments, workingDirectory))
                    return;

                try
                {
                    _processStarter.StartDirect(fileName, arguments, workingDirectory);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
                {
                    StartWithAdministratorPrompt(fileName, arguments, workingDirectory);
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorVirusInfected or ErrorVirusDeleted)
            {
                throw new UserFacingException(
                    $"Antivirus software blocked {Path.GetFileName(fileName)} from starting, and may have quarantined it. " +
                    "Check your antivirus software's protection history for details.", ex);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDisabledByPolicy)
            {
                throw new UserFacingException(
                    $"A security policy on this PC blocked {Path.GetFileName(fileName)} from starting.", ex);
            }
            catch (Exception ex)
            {
                throw new UserFacingException(failureMessage, ex);
            }
        }

        private void StartWithAdministratorPrompt(string fileName, string arguments, string workingDirectory)
        {
            try
            {
                _processStarter.StartViaShell(fileName, arguments, workingDirectory);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                // The user declined the prompt, which is not a failure to report.
            }
        }
    }

    internal sealed class ProcessStarter : IProcessStarter
    {
        public bool IsElevated => Environment.IsPrivilegedProcess;

        public void StartDirect(string fileName, string arguments, string workingDirectory) =>
            Start(fileName, arguments, workingDirectory, useShellExecute: false);

        public void StartViaShell(string fileName, string arguments, string workingDirectory) =>
            Start(fileName, arguments, workingDirectory, useShellExecute: true);

        public bool TryStartUnelevated(string fileName, string arguments, string workingDirectory) =>
            UnelevatedLauncher.TryStart(fileName, arguments, workingDirectory);

        private static void Start(string fileName, string arguments, string workingDirectory, bool useShellExecute)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = useShellExecute
            };

            // Process.Start returns null when ShellExecute starts no new process, which is not a failure.
            // Disposing the Process releases its handle and leaves the program running.
            Process.Start(startInfo)?.Dispose();
        }
    }
}

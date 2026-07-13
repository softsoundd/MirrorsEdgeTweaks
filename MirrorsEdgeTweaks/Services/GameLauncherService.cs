using MirrorsEdgeTweaks.Helpers;
using System.Diagnostics;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IGameLauncher
    {
        void Launch(string exePath, string arguments);
    }

    public class GameLauncherService : IGameLauncher
    {
        private readonly ISteamService _steamService;

        public GameLauncherService(ISteamService steamService)
        {
            _steamService = steamService;
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

            StartProcessWithFallbacks(
                exePath,
                arguments,
                workingDirectory,
                includeCmdFallback: true,
                "All launch strategies failed.");
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
                throw new InvalidOperationException(
                    "Could not locate Steam. Ensure Steam is installed and its install path is registered in the Windows Registry.");
            }

            string? steamDirectory = Path.GetDirectoryName(steamExe);
            if (string.IsNullOrWhiteSpace(steamDirectory) || !Directory.Exists(steamDirectory))
            {
                throw new InvalidOperationException("Could not determine a valid Steam working directory.");
            }

            StartProcessWithFallbacks(
                steamExe,
                BuildSteamApplaunchArguments(gameArguments),
                steamDirectory,
                includeCmdFallback: false,
                "Failed to launch Mirror's Edge via Steam.");
        }

        private static void StartProcessWithFallbacks(
            string exePath,
            string arguments,
            string workingDirectory,
            bool includeCmdFallback,
            string failureMessage)
        {
            List<string> launchErrors = new List<string>();

            if (TryStartProcess(exePath, arguments, workingDirectory, useShellExecute: false, launchErrors))
                return;

            if (TryStartProcess(exePath, arguments, workingDirectory, useShellExecute: true, launchErrors))
                return;

            if (includeCmdFallback && TryStartViaCmd(exePath, arguments, workingDirectory, launchErrors))
                return;

            throw new InvalidOperationException($"{failureMessage} {string.Join(" | ", launchErrors)}");
        }

        private static bool TryStartProcess(
            string exePath,
            string arguments,
            string workingDirectory,
            bool useShellExecute,
            List<string> launchErrors)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = useShellExecute
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    launchErrors.Add($"UseShellExecute={useShellExecute}: Process.Start returned null.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                launchErrors.Add($"UseShellExecute={useShellExecute}: {ex.Message}");
                return false;
            }
        }

        private static bool TryStartViaCmd(
            string exePath,
            string arguments,
            string workingDirectory,
            List<string> launchErrors)
        {
            try
            {
                string cmdArguments = string.IsNullOrWhiteSpace(arguments)
                    ? $"/c start \"\" \"{exePath}\""
                    : $"/c start \"\" \"{exePath}\" {arguments}";

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    launchErrors.Add("cmd fallback: Process.Start returned null.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                launchErrors.Add($"cmd fallback: {ex.Message}");
                return false;
            }
        }
    }
}

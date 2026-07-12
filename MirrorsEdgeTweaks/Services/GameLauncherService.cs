using System.Diagnostics;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IGameLauncher
    {
        void Launch(string exePath, string arguments);
        bool IsSteamVersionExecutable(string exePath);
    }

    public class GameLauncherService : IGameLauncher
    {
        private const long SteamMirrorsEdgeExeSize = 31946072;

        public void Launch(string exePath, string arguments)
        {
            string? workingDirectory = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                throw new InvalidOperationException("Could not determine a valid game working directory.");
            }

            List<string> launchErrors = new List<string>();

            if (TryStartProcess(exePath, arguments, workingDirectory, useShellExecute: false, launchErrors))
            {
                return;
            }

            if (TryStartProcess(exePath, arguments, workingDirectory, useShellExecute: true, launchErrors))
            {
                return;
            }

            if (TryStartViaCmd(exePath, arguments, workingDirectory, launchErrors))
            {
                return;
            }

            throw new InvalidOperationException($"All launch strategies failed. {string.Join(" | ", launchErrors)}");
        }

        public bool IsSteamVersionExecutable(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            try
            {
                return new FileInfo(exePath).Length == SteamMirrorsEdgeExeSize;
            }
            catch
            {
                return false;
            }
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

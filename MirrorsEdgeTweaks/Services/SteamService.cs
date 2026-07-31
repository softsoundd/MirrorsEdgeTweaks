using Microsoft.Win32;
using MirrorsEdgeTweaks.Helpers;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public sealed class SteamInstallScriptFixResult
    {
        public IReadOnlyList<string> PatchedFiles { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AlreadyCleanFiles { get; init; } = Array.Empty<string>();
        public IReadOnlyList<(string Path, string Error)> FailedFiles { get; init; } = Array.Empty<(string, string)>();

        public bool AnyFailed => FailedFiles.Count > 0;
    }

    public interface ISteamService
    {
        string? GetSteamInstallPath();
        string? GetSteamExecutablePath();
        bool IsSteamGameDirectory(string gameDirectory);
        SteamInstallScriptFixResult ApplyLanguageFix(string gameDirectory);
    }

    public class SteamService : ISteamService
    {
        public string? GetSteamInstallPath()
        {
            try
            {
                using var hkcuKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? steamPath = hkcuKey?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                    return steamPath.Replace('/', '\\');

                using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                string? installPath = hklmKey?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(installPath))
                    return installPath.Replace('/', '\\');
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read Steam install path: {ex.Message}");
            }

            return null;
        }

        public string? GetSteamExecutablePath()
        {
            string? installPath = GetSteamInstallPath();
            if (string.IsNullOrWhiteSpace(installPath))
                return null;

            string steamExe = Path.Combine(installPath, "steam.exe");
            return File.Exists(steamExe) ? steamExe : null;
        }

        public bool IsSteamGameDirectory(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
                return false;

            string exePath = Path.Combine(gameDirectory, "Binaries", "MirrorsEdge.exe");
            return ExeVersionDetector.IsSteamExecutable(exePath);
        }

        public SteamInstallScriptFixResult ApplyLanguageFix(string gameDirectory)
        {
            if (!IsSteamGameDirectory(gameDirectory))
                return new SteamInstallScriptFixResult();

            var patched = new List<string>();
            var alreadyClean = new List<string>();
            var failed = new List<(string Path, string Error)>();

            using var backupOperation = PatchUtility.BeginBackupOperation();

            foreach (string scriptPath in GetInstallScriptPaths(gameDirectory))
            {
                SteamInstallScriptPatchFileResult result = SteamInstallScriptPatcher.TryPatchFile(scriptPath);
                switch (result.Status)
                {
                    case SteamInstallScriptPatchStatus.Patched:
                        patched.Add(result.Path);
                        break;
                    case SteamInstallScriptPatchStatus.AlreadyClean:
                        alreadyClean.Add(result.Path);
                        break;
                    case SteamInstallScriptPatchStatus.Failed:
                        failed.Add((result.Path, result.Error ?? "Unknown error."));
                        break;
                }
            }

            if (failed.Count == 0)
                backupOperation.Complete();

            return new SteamInstallScriptFixResult
            {
                PatchedFiles = patched,
                AlreadyCleanFiles = alreadyClean,
                FailedFiles = failed,
            };
        }

        private IReadOnlyList<string> GetInstallScriptPaths(string gameDirectory) =>
            SteamInstallScriptPatcher.FindInstallScriptPaths(gameDirectory, GetSteamInstallPath());
    }
}

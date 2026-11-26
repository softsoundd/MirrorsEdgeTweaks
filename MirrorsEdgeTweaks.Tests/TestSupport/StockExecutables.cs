using MirrorsEdgeTweaks.Helpers;
using System.Security.Cryptography;

namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Finds stock game executables by their SHA-256, anywhere under the folder that METWEAKS_GAME_EXES names.
    // Tests that use it are explicit, so they only run when requested.
    internal static class StockExecutables
    {
        private const string FolderVariable = "METWEAKS_GAME_EXES";

        private static readonly string? Folder = Environment.GetEnvironmentVariable(FolderVariable);
        private static readonly Lazy<Dictionary<string, string>> PathsByVersion = new(Scan);

        public static string Require(string version)
        {
            if (string.IsNullOrWhiteSpace(Folder))
                Assert.Skip($"Set {FolderVariable} to a folder of stock Mirror's Edge executables to run this test.");

            if (!PathsByVersion.Value.TryGetValue(version, out string? path))
                Assert.Skip($"{FolderVariable} ({Folder}) holds no stock {ExeVersionDetector.VersionLabels[version]} executable.");

            return path;
        }

        public static string CopyTo(string version, string gameDirectory)
        {
            string source = Require(version);
            string binaries = Path.Combine(gameDirectory, "Binaries");
            Directory.CreateDirectory(binaries);
            string exe = Path.Combine(binaries, "MirrorsEdge.exe");
            File.Copy(source, exe);
            return exe;
        }

        // Throws when the folder does not exist, so a mistyped path fails the tests.
        private static Dictionary<string, string> Scan()
        {
            var paths = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(Folder))
                return paths;

            var options = new EnumerationOptions { RecurseSubdirectories = true };
            foreach (string path in Directory.EnumerateFiles(Folder, "*.exe", options))
            {
                using FileStream stream = File.OpenRead(path);
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (ExeVersionDetector.Sha256ToVersion.TryGetValue(hash, out string? version))
                    paths.TryAdd(version, path);
            }
            return paths;
        }
    }
}

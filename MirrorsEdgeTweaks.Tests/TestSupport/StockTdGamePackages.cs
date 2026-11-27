using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Finds every TdGame.u anywhere under the folder that METWEAKS_TDGAME_PACKAGES names.
    // Tests that use it are explicit, so they only run when requested.
    internal static class StockTdGamePackages
    {
        private const string FolderVariable = "METWEAKS_TDGAME_PACKAGES";

        private static readonly string? Folder = Environment.GetEnvironmentVariable(FolderVariable);
        private static readonly Lazy<List<string>> Paths = new(Scan);

        public static IReadOnlyList<string> RequireAll()
        {
            if (string.IsNullOrWhiteSpace(Folder))
                Assert.Skip($"Set {FolderVariable} to a folder of stock TdGame.u files to run this test.");

            if (Paths.Value.Count == 0)
                Assert.Skip($"{FolderVariable} ({Folder}) holds no TdGame.u.");

            return Paths.Value;
        }

        // Tweaks only patches decompressed packages, so the copy is decompressed.
        public static string CopyTo(string source, string directory)
        {
            Directory.CreateDirectory(directory);
            string package = Path.Combine(directory, "TdGame.u");
            File.Copy(source, package, overwrite: true);
            new DecompressionService(new FileService()).RunDecompressor(package);
            return package;
        }

        // Throws when the folder does not exist, so a mistyped path fails the tests.
        private static List<string> Scan()
        {
            if (string.IsNullOrWhiteSpace(Folder))
                return new List<string>();

            var options = new EnumerationOptions { RecurseSubdirectories = true };
            return Directory.EnumerateFiles(Folder, "TdGame.u", options)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

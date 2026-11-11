using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.Tests
{
    public class ZipExtractHelperTests
    {
        [Fact]
        public void ExtractOverwritingReadOnly_ReplacesAReadOnlyFile()
        {
            using var temp = new TempDirectory();
            string zipPath = Path.Combine(temp.Path, "asset.zip");
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("Binaries/OpenAL32.dll").Open()))
                writer.Write("stock");

            string gameDirectory = Path.Combine(temp.Path, "game");
            string target = Path.Combine(gameDirectory, "Binaries", "OpenAL32.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "modded");
            File.SetAttributes(target, FileAttributes.ReadOnly);

            ZipExtractHelper.ExtractOverwritingReadOnly(zipPath, gameDirectory);

            Assert.Equal("stock", File.ReadAllText(target));
        }
    }
}

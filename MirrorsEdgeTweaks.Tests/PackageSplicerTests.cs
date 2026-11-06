using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class PackageSplicerTests : IDisposable
    {
        private const int HeaderSize = 12_000;
        private const int ExportTableOffset = 1_000;
        private static readonly int[] ExportSizes = [100, 200, 50];

        private readonly TempDirectory _temp = new();

        public PackageSplicerTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void ParseHeader_ReadsTheSameHeader_FromTheStartOfThePackage()
        {
            byte[] package = BuildPackage();

            var fromStart = PackageSplicer.ParseHeader(package[..4096], package.Length);

            Assert.Equal(PackageSplicer.ParseHeader(package), fromStart);
            Assert.Equal(HeaderSize, fromStart.TotalHeaderSize);
        }

        [Fact]
        public void SpliceExport_MatchesTheInMemorySplice_WithTheStructuralFixUp()
        {
            byte[] package = BuildPackage();
            int exportStart = HeaderSize + ExportSizes[0];
            byte[] blob = [0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7];
            var modifications = new List<(int, int, int)> { (exportStart, blob.Length, 2) };

            byte[] expected = PackageSplicer.InsertBytes(package, exportStart + 60, blob);
            PackageSplicer.UpdateExportsStructural(expected, PackageSplicer.ParseHeader(expected), modifications);

            string path = WritePackage(package);
            byte[] export = PackageSplicer.InsertBytes(package[exportStart..(exportStart + ExportSizes[1])], 60, blob);
            PackageSplicer.SpliceExport(path, exportStart, ExportSizes[1], export,
                (header, hdr) => PackageSplicer.UpdateExportsStructural(header, hdr, modifications));

            Assert.NotEqual(package[..HeaderSize], expected[..HeaderSize]);
            Assert.Equal(expected, File.ReadAllBytes(path));
        }

        [Fact]
        public void SpliceExport_MatchesTheInMemorySplice_WithTheHeuristicFixUp()
        {
            byte[] package = BuildPackage();
            int exportStart = HeaderSize + ExportSizes[0];
            int removeAt = exportStart + 60;

            byte[] expected = PackageSplicer.RemoveBytes(package, removeAt, 7);
            PackageSplicer.UpdateExportsHeuristic(expected, PackageSplicer.ParseHeader(expected), exportStart, removeAt, package.Length, -7);

            string path = WritePackage(package);
            byte[] export = PackageSplicer.RemoveBytes(package[exportStart..(exportStart + ExportSizes[1])], 60, 7);
            PackageSplicer.SpliceExport(path, exportStart, ExportSizes[1], export,
                (header, hdr) => PackageSplicer.UpdateExportsHeuristic(header, hdr, exportStart, removeAt, package.Length, -7));

            Assert.NotEqual(package[..HeaderSize], expected[..HeaderSize]);
            Assert.Equal(expected, File.ReadAllBytes(path));
        }

        // Lays out a version 536 package whose export table entries have no component map or net objects.
        private static byte[] BuildPackage()
        {
            byte[] package = new byte[HeaderSize + ExportSizes.Sum()];
            using var writer = new BinaryWriter(new MemoryStream(package));
            writer.Write(PackageSplicer.UE3_TAG);
            writer.Write(536 | (43 << 16));
            writer.Write(HeaderSize);
            writer.Write(5);
            writer.Write("None\0"u8);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(ExportSizes.Length);
            writer.Write(ExportTableOffset);
            writer.Write(0);
            writer.Write(0);

            writer.Seek(ExportTableOffset, SeekOrigin.Begin);
            int serialOffset = HeaderSize;
            foreach (int size in ExportSizes)
            {
                writer.Write(new byte[32]);
                writer.Write(size);
                writer.Write(serialOffset);
                writer.Write(new byte[12]);
                writer.Write(new byte[20]);
                serialOffset += size;
            }

            for (int i = HeaderSize; i < package.Length; i++)
                package[i] = (byte)(i * 7);
            return package;
        }

        private string WritePackage(byte[] package)
        {
            string path = Path.Combine(_temp.Path, "TdGame.u");
            File.WriteAllBytes(path, package);
            return path;
        }
    }
}

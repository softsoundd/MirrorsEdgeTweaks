using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class DecompressionServiceTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public DecompressionServiceTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void RunDecompressor_ReplacesACompressedPackage_WithItsDecompressedLayout()
        {
            var (compressed, decompressed) = SyntheticPackage.Build(Body());
            string path = WritePackage(compressed);
            Assert.True(DecompressionService.IsCompressed(path));

            new DecompressionService(new FileService()).RunDecompressor(path);

            Assert.Equal(decompressed, File.ReadAllBytes(path));
            Assert.False(DecompressionService.IsCompressed(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)));
            Assert.False(Directory.Exists(PatchUtility.GetBackupDirectoryForPath(path)));
        }

        [Fact]
        public void RunDecompressor_KeepsTheReadOnlyAttribute()
        {
            var (compressed, decompressed) = SyntheticPackage.Build(Body());
            string path = WritePackage(compressed);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            new DecompressionService(new FileService()).RunDecompressor(path);

            Assert.Equal(decompressed, File.ReadAllBytes(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            File.SetAttributes(path, FileAttributes.Normal);
        }

        [Fact]
        public void RunDecompressor_LeavesADecompressedPackageUntouched()
        {
            var (_, decompressed) = SyntheticPackage.Build(Body());
            string path = WritePackage(decompressed);
            var lastWrite = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, lastWrite);

            new DecompressionService(new FileService()).RunDecompressor(path);

            Assert.Equal(decompressed, File.ReadAllBytes(path));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        }

        private static byte[] Body() => Enumerable.Range(0, 4096).Select(i => (byte)(i % 7)).ToArray();

        private string WritePackage(byte[] content)
        {
            string directory = Path.Combine(_temp.Path, "CookedPC");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Engine.u");
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}

using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class PatchUtilityTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public PatchUtilityTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void PatchPreservingAttributes_WritesEachPatch_AndKeepsTheOtherBytes()
        {
            string path = CreateFile([0, 1, 2, 3, 4, 5, 6, 7]);

            PatchUtility.PatchPreservingAttributes(path, [(1, new byte[] { 0xA1 }), (5, new byte[] { 0xB5, 0xB6 })]);

            Assert.Equal([0, 0xA1, 2, 3, 4, 0xB5, 0xB6, 7], File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".metweaks.writetmp"));
        }

        [Fact]
        public void PatchPreservingAttributes_WritesInPlace_WhenAnOpenReaderBlocksTheRename()
        {
            string path = CreateFile([1, 2, 3]);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                PatchUtility.PatchPreservingAttributes(path, [(1, new byte[] { 9 })]);
            }

            Assert.Equal([1, 9, 3], File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".metweaks.writetmp"));
        }

        [Fact]
        public void PatchPreservingAttributes_KeepsTheOriginalAsBackup_WhenTheOperationIsNotCompleted()
        {
            string path = CreateFile([1, 2, 3]);

            using (PatchUtility.BeginBackupOperation())
            {
                PatchUtility.PatchPreservingAttributes(path, [(0, new byte[] { 9 })]);
            }

            string backup = Path.Combine(PatchUtility.GetBackupDirectoryForPath(path), "Engine.u.last.bak");
            Assert.Equal([1, 2, 3], File.ReadAllBytes(backup));
        }

        [Fact]
        public void PatchPreservingAttributes_RemovesTheBackup_WhenASingleWriteSucceeds()
        {
            string path = CreateFile([1, 2, 3]);

            PatchUtility.PatchPreservingAttributes(path, [(2, new byte[] { 9 })]);

            Assert.Equal([1, 2, 9], File.ReadAllBytes(path));
            Assert.False(Directory.Exists(PatchUtility.GetBackupDirectoryForPath(path)));
        }

        [Fact]
        public void PatchPreservingAttributes_RestoresTheReadOnlyAttribute()
        {
            string path = CreateFile([1, 2, 3]);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            PatchUtility.PatchPreservingAttributes(path, [(0, new byte[] { 9 })]);

            Assert.Equal([9, 2, 3], File.ReadAllBytes(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            File.SetAttributes(path, FileAttributes.Normal);
        }

        [Fact]
        public void PatchPreservingAttributes_ThrowsWithoutWriting_WhenAPatchRunsPastTheEnd()
        {
            string path = CreateFile([1, 2, 3]);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PatchUtility.PatchPreservingAttributes(path, [(0, new byte[] { 9 }), (2, new byte[] { 8, 8 })]));

            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
            Assert.False(Directory.Exists(PatchUtility.GetBackupDirectoryForPath(path)));
        }

        [Fact]
        public void SplicePreservingAttributes_ReplacesEachRange_AndKeepsTheOtherBytes()
        {
            string path = CreateFile([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);

            PatchUtility.SplicePreservingAttributes(path,
                [(1, 2, new byte[] { 0xA1, 0xA2, 0xA3 }), (6, 1, Array.Empty<byte>()), (9, 0, new byte[] { 0xD9 })]);

            Assert.Equal([0, 0xA1, 0xA2, 0xA3, 3, 4, 5, 7, 8, 0xD9, 9], File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".metweaks.writetmp"));
        }

        [Fact]
        public void SplicePreservingAttributes_CopiesInPlace_WhenAnOpenReaderBlocksTheRename()
        {
            string path = CreateFile([1, 2, 3]);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                PatchUtility.SplicePreservingAttributes(path, [(1, 1, new byte[] { 8, 9 })]);
            }

            Assert.Equal([1, 8, 9, 3], File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".metweaks.writetmp"));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        public void SplicePreservingAttributes_ThrowsWithoutWriting_WhenASpliceOverlapsOrRunsPastTheEnd(int offset, int length)
        {
            string path = CreateFile([1, 2, 3]);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PatchUtility.SplicePreservingAttributes(path, [(0, 2, new byte[] { 9 }), (offset, length, new byte[] { 8 })]));

            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
            Assert.False(Directory.Exists(PatchUtility.GetBackupDirectoryForPath(path)));
        }

        private string CreateFile(byte[] content)
        {
            string path = Path.Combine(_temp.Path, "Engine.u");
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}

using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public sealed class BackupRetentionServiceTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _backupRoot;

        public BackupRetentionServiceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "metweaks-backup-test-" + Guid.NewGuid().ToString("N"));
            _backupRoot = Path.Combine(_tempRoot, "backups");
            Directory.CreateDirectory(_backupRoot);
            PatchUtility.BackupRootOverrideForTests = _backupRoot;
        }

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        [Fact]
        public void PruneOrphanedBackups_DeletesFolder_WhenSourceFileMissing()
        {
            string missingSource = Path.Combine(_tempRoot, "gone", "TdGame.u");
            string backupDir = CreateBackupFolder(missingSource, content: [1, 2, 3]);

            int pruned = BackupRetentionService.PruneOrphanedBackups();

            Assert.Equal(1, pruned);
            Assert.False(Directory.Exists(backupDir));
        }

        [Fact]
        public void PruneOrphanedBackups_KeepsInFlightBackup_WhenSourceFileExists()
        {
            string liveSource = Path.Combine(_tempRoot, "live");
            Directory.CreateDirectory(liveSource);
            string sourceFile = Path.Combine(liveSource, "Engine.u");
            File.WriteAllBytes(sourceFile, [9, 9, 9]);

            string backupDir = CreateBackupFolder(sourceFile, content: [1, 2, 3]);

            int pruned = BackupRetentionService.PruneOrphanedBackups();

            Assert.Equal(0, pruned);
            Assert.True(Directory.Exists(backupDir));
        }

        [Fact]
        public void WritePreservingAttributes_RemovesBackup_WhenSingleWriteSucceeds()
        {
            string liveSource = Path.Combine(_tempRoot, "write");
            Directory.CreateDirectory(liveSource);
            string sourceFile = Path.Combine(liveSource, "MirrorsEdge.exe");
            byte[] original = [10, 11, 12];
            byte[] patched = [20, 21, 22];
            File.WriteAllBytes(sourceFile, original);

            string backupDir = PatchUtility.GetBackupDirectoryForPath(sourceFile);
            PatchUtility.WritePreservingAttributes(sourceFile, patched);

            Assert.Equal(patched, File.ReadAllBytes(sourceFile));
            Assert.False(Directory.Exists(backupDir));
        }

        [Fact]
        public void BackupOperationScope_RemovesBackups_WhenCompleted()
        {
            string liveSource = Path.Combine(_tempRoot, "batch");
            Directory.CreateDirectory(liveSource);
            string fileA = Path.Combine(liveSource, "A.u");
            string fileB = Path.Combine(liveSource, "B.u");
            File.WriteAllBytes(fileA, [1]);
            File.WriteAllBytes(fileB, [2]);

            using (var operation = PatchUtility.BeginBackupOperation())
            {
                PatchUtility.WritePreservingAttributes(fileA, [11]);
                PatchUtility.WritePreservingAttributes(fileB, [22]);
                operation.Complete();
            }

            Assert.False(Directory.Exists(PatchUtility.GetBackupDirectoryForPath(fileA)));
            Assert.False(Directory.Exists(PatchUtility.GetBackupDirectoryForPath(fileB)));
        }

        [Fact]
        public void BackupOperationScope_KeepsBackups_WhenNotCompleted()
        {
            string liveSource = Path.Combine(_tempRoot, "abandon");
            Directory.CreateDirectory(liveSource);
            string sourceFile = Path.Combine(liveSource, "TdGame.u");
            File.WriteAllBytes(sourceFile, [5]);

            using (PatchUtility.BeginBackupOperation())
            {
                PatchUtility.WritePreservingAttributes(sourceFile, [6]);
            }

            string backupDir = PatchUtility.GetBackupDirectoryForPath(sourceFile);
            Assert.True(Directory.Exists(backupDir));
            Assert.Equal([5], File.ReadAllBytes(Path.Combine(backupDir, "TdGame.u.last.bak")));
        }

        private static string CreateBackupFolder(string sourcePath, byte[] content)
        {
            string backupDir = PatchUtility.GetBackupDirectoryForPath(sourcePath);
            Directory.CreateDirectory(backupDir);

            string fileName = Path.GetFileName(sourcePath);
            File.WriteAllBytes(Path.Combine(backupDir, fileName + ".last.bak"), content);
            File.WriteAllText(Path.Combine(backupDir, "source.txt"), sourcePath);
            return backupDir;
        }
    }
}

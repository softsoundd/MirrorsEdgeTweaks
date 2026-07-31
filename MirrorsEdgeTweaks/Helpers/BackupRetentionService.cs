using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    internal static class BackupRetentionService
    {
        public static int PruneOrphanedBackups()
        {
            string backupRoot = PatchUtility.BackupRoot;
            if (!Directory.Exists(backupRoot))
                return 0;

            int pruned = 0;
            foreach (string dir in Directory.GetDirectories(backupRoot))
            {
                if (TryPruneOrphanedDirectory(dir))
                    pruned++;
            }

            return pruned;
        }

        public static bool PruneBackupForPath(string gameFilePath)
        {
            try
            {
                string backupDir = PatchUtility.GetBackupDirectoryForPath(gameFilePath);
                if (!Directory.Exists(backupDir))
                    return false;

                Directory.Delete(backupDir, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Backup prune for '{gameFilePath}' failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryPruneOrphanedDirectory(string backupDir)
        {
            try
            {
                string sourceFile = Path.Combine(backupDir, "source.txt");
                if (!File.Exists(sourceFile))
                {
                    Directory.Delete(backupDir, recursive: true);
                    return true;
                }

                string sourcePath = File.ReadAllText(sourceFile).Trim();
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    Directory.Delete(backupDir, recursive: true);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Orphaned-backup prune for '{backupDir}' failed: {ex.Message}");
                return false;
            }
        }
    }
}

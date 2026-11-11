using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    internal static class PatchUtility
    {
        private static readonly AsyncLocal<Stack<BackupOperationScope>> BackupOperationStack = new();

        private static BackupOperationScope? CurrentBackupOperation =>
            BackupOperationStack.Value is { Count: > 0 } stack ? stack.Peek() : null;

        internal static string? BackupRootOverrideForTests { get; set; }

        internal static string BackupRoot =>
            BackupRootOverrideForTests ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MirrorsEdgeTweaks", "backups");

        internal static string GetBackupDirectoryForPath(string path) =>
            Path.Combine(BackupRoot, ComputePathHash(path));

        // Wraps a multi-file patch flow.
        // Its staged backups stay for manual recovery unless Complete() is called after the whole operation succeeds.
        public static BackupOperationScope BeginBackupOperation()
        {
            var scope = new BackupOperationScope();
            Stack<BackupOperationScope> stack = BackupOperationStack.Value ??= new Stack<BackupOperationScope>();
            stack.Push(scope);
            return scope;
        }

        internal static void ReleaseBackupOperation(BackupOperationScope scope)
        {
            Stack<BackupOperationScope>? stack = BackupOperationStack.Value;
            if (stack != null && stack.Count > 0 && ReferenceEquals(stack.Peek(), scope))
                stack.Pop();
        }

        // Every patch to a binary game file, such as the exe, .u and .upk files, is written through this,
        // PatchPreservingAttributes or SplicePreservingAttributes.
        // A read-only attribute is cleared for the write and restored afterwards.
        // The pre-write backup is deleted on success, unless a BackupOperationScope is active.
        // The content goes to a temporary file that is renamed over the target, so a crash cannot leave a half-written file.
        public static void WritePreservingAttributes(string path, byte[] content) =>
            WriteWithBackup(path, () => WriteAtomically(path,
                tempPath => File.WriteAllBytes(tempPath, content),
                _ => File.WriteAllBytes(path, content)));

        // Writes each patch at its offset in the same way as WritePreservingAttributes, without holding the file in memory.
        // Throws without writing anything when a patch runs past the end of the file.
        public static void PatchPreservingAttributes(string path, IReadOnlyList<(long Offset, byte[] Bytes)> patches)
        {
            long length = new FileInfo(path).Length;
            foreach (var (offset, bytes) in patches)
            {
                if (offset < 0 || offset + bytes.Length > length)
                    throw new ArgumentOutOfRangeException(nameof(patches),
                        $"A patch at offset {offset} runs past the end of {Path.GetFileName(path)}.");
            }

            WriteWithBackup(path, () => WriteAtomically(path,
                tempPath =>
                {
                    File.Copy(path, tempPath, overwrite: true);
                    WritePatches(tempPath, patches);
                },
                _ => WritePatches(path, patches)));
        }

        // Replaces each range of Length bytes at Offset with Bytes, which may be longer or shorter,
        // in the same way as WritePreservingAttributes, without holding the file in memory.
        // Throws without writing anything when the ranges are out of file order, overlap or run past the end of the file.
        public static void SplicePreservingAttributes(string path, IReadOnlyList<(long Offset, int Length, byte[] Bytes)> splices)
        {
            long fileLength = new FileInfo(path).Length;
            long previousEnd = 0;
            foreach (var (offset, length, _) in splices)
            {
                if (offset < previousEnd || length < 0 || offset + length > fileLength)
                    throw new ArgumentOutOfRangeException(nameof(splices),
                        $"A splice at offset {offset} overlaps the one before it or runs past the end of {Path.GetFileName(path)}.");
                previousEnd = offset + length;
            }

            WriteWithBackup(path, () => WriteAtomically(path,
                tempPath => WriteSpliced(path, tempPath, splices),
                tempPath => CopyContent(tempPath, path)));
        }

        private static void WriteWithBackup(string path, Action write)
        {
            GameFileReadScope.Invalidate(path);

            FileAttributes attributes = File.GetAttributes(path);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            if (wasReadOnly)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

            StageBackup(path);
            try
            {
                write();
                OnWriteSucceeded(path);
            }
            finally
            {
                if (wasReadOnly)
                    File.SetAttributes(path, attributes);
            }
        }

        private static void StageBackup(string path)
        {
            try
            {
                string fileName = Path.GetFileName(path);
                string backupDir = GetBackupDirectoryForPath(path);
                Directory.CreateDirectory(backupDir);

                string lastBackup = Path.Combine(backupDir, fileName + ".last.bak");
                File.Copy(path, lastBackup, overwrite: true);
                File.WriteAllText(Path.Combine(backupDir, "source.txt"), path);
                File.SetAttributes(lastBackup, FileAttributes.Normal);
            }
            catch (Exception ex)
            {
                // A broken backup location must not block patching.
                System.Diagnostics.Debug.WriteLine($"Backup of '{path}' failed: {ex.Message}");
            }
        }

        private static void OnWriteSucceeded(string path)
        {
            BackupOperationScope? operation = CurrentBackupOperation;
            if (operation != null)
            {
                operation.Register(path);
                return;
            }

            BackupRetentionService.PruneBackupForPath(path);
        }

        // Hashes the full path, so files with the same name from different game installs get separate backup folders.
        private static string ComputePathHash(string path)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        // writeInPlace receives the path of the finished temporary file.
        private static void WriteAtomically(string path, Action<string> writeTemp, Action<string> writeInPlace)
        {
            string tempPath = path + ".metweaks.writetmp";
            try
            {
                writeTemp(tempPath);
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Renaming over the destination needs delete access, which Windows refuses
                    // while any handle without FileShare.Delete is open on it,
                    // such as an undisposed UELib package reader or an antivirus scan.
                    // The in-place write is not atomic, but it is allowed whenever the file is writable.
                    writeInPlace(tempPath);
                    File.Delete(tempPath);
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        private static void WritePatches(string path, IReadOnlyList<(long Offset, byte[] Bytes)> patches)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            foreach (var (offset, bytes) in patches)
            {
                stream.Position = offset;
                stream.Write(bytes);
            }
        }

        private static void WriteSpliced(string sourcePath, string destinationPath, IReadOnlyList<(long Offset, int Length, byte[] Bytes)> splices)
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0, FileOptions.SequentialScan);
            using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
            byte[] buffer = new byte[81920];
            long position = 0;
            foreach (var (offset, length, bytes) in splices)
            {
                CopyRange(source, destination, position, offset - position, buffer);
                destination.Write(bytes);
                position = offset + length;
            }
            CopyRange(source, destination, position, source.Length - position, buffer);
        }

        private static void CopyRange(FileStream source, FileStream destination, long offset, long count, byte[] buffer)
        {
            source.Position = offset;
            while (count > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read == 0)
                    throw new EndOfStreamException();
                destination.Write(buffer, 0, read);
                count -= read;
            }
        }

        // Opens the destination the way File.WriteAllBytes does, so the copy works wherever that write would.
        private static void CopyContent(string sourcePath, string destinationPath)
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 0, FileOptions.SequentialScan);
            using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
            source.CopyTo(destination);
        }

        public static void DecryptOoaInPlace(byte[] data)
        {
            string? dlfPath = OoaService.FindLicensePath(data);
            if (dlfPath == null)
                throw new InvalidOperationException("OOA license file not found.");
            byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
            OoaService.DecryptSections(data, key);
        }

        public sealed class OoaSession
        {
            internal byte[] Key { get; init; } = Array.Empty<byte>();
            internal OoaContext Ctx { get; init; } = new();
        }

        public static OoaSession BeginOoa(byte[] data)
        {
            string? dlfPath = OoaService.FindLicensePath(data);
            if (dlfPath == null)
                throw new OoaLicenseNotFoundException(OoaService.GetExpectedLicensePath(data));

            byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
            PeImageLayout.ClearCertificateEntry(data);
            OoaContext ctx = OoaService.DecryptSections(data, key);
            return new OoaSession { Key = key, Ctx = ctx };
        }

        public static byte[] FinishOoa(byte[] data, OoaSession session)
        {
            OoaService.UpdateEncBlockCrcs(data, session.Ctx);
            OoaService.ReencryptSections(data, session.Key, session.Ctx);
            return OoaService.TruncateToSections(data);
        }

        public static byte[] ApplyUnderOoa(byte[] data, Func<byte[], byte[]> patch)
        {
            OoaSession session = BeginOoa(data);
            data = patch(data);
            return FinishOoa(data, session);
        }

        // The transform receives EA images decrypted, and returns null when it has nothing to change.
        // The exe is only rewritten when the transform changed it, because re-encrypting an EA image strips its signature.
        public static void RewriteExe(string exePath, Func<byte[], byte[]?> transform)
        {
            byte[] data = File.ReadAllBytes(exePath);
            byte[]? result;
            if (OoaService.HasOoaSection(data))
            {
                bool changed = false;
                byte[] reencrypted = ApplyUnderOoa(data, image =>
                {
                    byte[]? transformed = transform(image);
                    changed = transformed != null;
                    return transformed ?? image;
                });
                result = changed ? reencrypted : null;
            }
            else
            {
                result = transform(data);
            }

            if (result != null)
                WritePreservingAttributes(exePath, result);
        }
    }
}

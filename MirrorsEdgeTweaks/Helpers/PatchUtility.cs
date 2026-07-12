using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    internal static class PatchUtility
    {
        // Root folder for pre-patch copies of game files, e.g.
        // %LocalAppData%\MirrorsEdgeTweaks\backups\<pathHash>\TdGame.u.first.bak
        internal static string BackupRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorsEdgeTweaks", "backups");

        // Single safe writer for every binary patch target (exe, .u, .upk). Provides:
        //  1. Read-only attribute preservation (cleared for the write, restored afterwards).
        //  2. A best-effort backup of the current on-disk content before it is overwritten.
        //  3. An atomic temp-file + rename write, so a crash mid-write can never leave a
        //     half-written game file behind.
        public static void WritePreservingAttributes(string path, byte[] content)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            if (wasReadOnly)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            try
            {
                TryBackup(path);
                WriteAtomically(path, content);
            }
            finally
            {
                if (wasReadOnly)
                    File.SetAttributes(path, attributes);
            }
        }

        // Keeps two bounded copies per target file:
        //  - "<name>.first.bak": the content seen the first time this tool ever wrote the file
        //    (closest available state to pristine). Never overwritten.
        //  - "<name>.last.bak": the content immediately before the most recent write (an undo
        //    point for the last operation). Overwritten on every write.
        // Backup failures are swallowed: a broken backup location must not block patching.
        private static void TryBackup(string path)
        {
            try
            {
                string fileName = Path.GetFileName(path);
                string pathHash = ComputePathHash(path);
                string backupDir = Path.Combine(BackupRoot, pathHash);
                Directory.CreateDirectory(backupDir);

                string firstBackup = Path.Combine(backupDir, fileName + ".first.bak");
                string lastBackup = Path.Combine(backupDir, fileName + ".last.bak");

                if (!File.Exists(firstBackup))
                {
                    File.Copy(path, firstBackup);
                    // Record where the backup came from, so users restoring by hand know the target.
                    File.WriteAllText(Path.Combine(backupDir, "source.txt"), path);
                }

                File.Copy(path, lastBackup, overwrite: true);

                // Backups of read-only game files inherit the attribute; clear it so the backup
                // folder stays trivially manageable.
                File.SetAttributes(firstBackup, FileAttributes.Normal);
                File.SetAttributes(lastBackup, FileAttributes.Normal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Backup of '{path}' failed: {ex.Message}");
            }
        }

        // Short stable hash of the full path so identically named files from different game
        // installs get separate backup folders.
        private static string ComputePathHash(string path)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        private static void WriteAtomically(string path, byte[] content)
        {
            string tempPath = path + ".metweaks.writetmp";
            try
            {
                File.WriteAllBytes(tempPath, content);
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Replacing via rename needs delete access on the destination, which Windows
                    // refuses while any handle is open on it without FileShare.Delete (e.g. a
                    // UELib package reader that has not been disposed yet, or an antivirus scan).
                    // Fall back to an in-place rewrite - not atomic, but always permitted when the
                    // file itself is writable, and identical to the pre-pipeline behaviour.
                    File.WriteAllBytes(path, content);
                    File.Delete(tempPath);
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
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
            OoaService.StripAuthenticode(data);
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

        // Centralised patch pipeline for executables that may carry EA's OOA encryption:
        // reads the file, transparently decrypts if needed, applies the in-memory transform,
        // re-encrypts, and writes back with backup + atomic replace. Returns false when the
        // transform produced no change (nothing written).
        public static bool UpdateExe(string exePath, Func<byte[], byte[]> transform)
        {
            byte[] original = File.ReadAllBytes(exePath);
            byte[] result;

            if (OoaService.HasOoaSection(original))
            {
                result = ApplyUnderOoa((byte[])original.Clone(), transform);
            }
            else
            {
                result = transform((byte[])original.Clone());
            }

            if (result.AsSpan().SequenceEqual(original))
                return false;

            WritePreservingAttributes(exePath, result);
            return true;
        }
    }
}

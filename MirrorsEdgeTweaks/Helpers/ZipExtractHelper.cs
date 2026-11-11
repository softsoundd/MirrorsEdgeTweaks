using System.IO;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class ZipExtractHelper
    {
        // ZipFile.ExtractToDirectory cannot overwrite a read-only file, so the attribute is cleared on each file the archive replaces.
        public static void ExtractOverwritingReadOnly(string zipPath, string destinationDirectory)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                        continue;

                    FileAttributes attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                }
            }

            ZipFile.ExtractToDirectory(zipPath, destinationDirectory, overwriteFiles: true);
        }
    }
}

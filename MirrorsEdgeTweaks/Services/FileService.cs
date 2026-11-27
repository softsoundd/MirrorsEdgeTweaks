using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IFileService
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        void DeleteFile(string path);
        byte[] ReadAllBytes(string path);
        void WriteAllBytes(string path, byte[] bytes);
        void DeleteDirectory(string path, bool recursive = false);
        string GetTempPath();
        string CombinePaths(params string[] paths);
        string[] ReadAllLines(string path);
        string ReadAllText(string path);
        void WriteAllLines(string path, IEnumerable<string> lines);
        bool IsReadOnly(string path);
        void SetReadOnly(string path, bool readOnly);

        // Clears the read-only attribute if needed, writes, then always re-locks the file.
        // Game config files are kept read-only so the game does not overwrite user tweaks on launch.
        void WriteAllTextAndLock(string path, string content);
        void WriteAllLinesAndLock(string path, IEnumerable<string> lines);
    }

    public class FileService : IFileService
    {
        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void DeleteFile(string path)
        {
            File.Delete(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
        }

        public void DeleteDirectory(string path, bool recursive = false)
        {
            Directory.Delete(path, recursive);
        }

        public string GetTempPath()
        {
            return Path.GetTempPath();
        }

        public string CombinePaths(params string[] paths)
        {
            return Path.Combine(paths);
        }

        public string[] ReadAllLines(string path)
        {
            return File.ReadAllLines(path);
        }

        public string ReadAllText(string path)
        {
            return File.ReadAllText(path);
        }

        // Writes a temporary file and renames it over the target, so a crash cannot leave a half-written file.
        // A target that refuses the rename, such as one that another program holds open, is written in place.
        public void WriteAllLines(string path, IEnumerable<string> lines)
        {
            string[] content = lines.ToArray();
            string tempPath = path + ".tmp";
            try
            {
                File.WriteAllLines(tempPath, content);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tempPath); } catch { }
                File.WriteAllLines(path, content);
            }
        }

        public void WriteAllTextAndLock(string path, string content) =>
            WriteAndLock(path, () => File.WriteAllText(path, content));

        public bool IsReadOnly(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
        }

        public void SetReadOnly(string path, bool readOnly)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (readOnly)
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            }
            else
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }

        public void WriteAllLinesAndLock(string path, IEnumerable<string> lines) =>
            WriteAndLock(path, () => File.WriteAllLines(path, lines));

        private void WriteAndLock(string path, Action write)
        {
            if (IsReadOnly(path))
            {
                SetReadOnly(path, false);
            }

            try
            {
                write();
            }
            finally
            {
                SetReadOnly(path, true);
            }
        }
    }
}

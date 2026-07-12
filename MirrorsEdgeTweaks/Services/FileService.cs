using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IFileService
    {
        Task<string> ReadAllTextAsync(string path);
        Task WriteAllTextAsync(string path, string content);
        Task<byte[]> ReadAllBytesAsync(string path);
        Task WriteAllBytesAsync(string path, byte[] bytes);
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
        public async Task<string> ReadAllTextAsync(string path)
        {
            return await File.ReadAllTextAsync(path);
        }

        public async Task WriteAllTextAsync(string path, string content)
        {
            await File.WriteAllTextAsync(path, content);
        }

        public async Task<byte[]> ReadAllBytesAsync(string path)
        {
            return await File.ReadAllBytesAsync(path);
        }

        public async Task WriteAllBytesAsync(string path, byte[] bytes)
        {
            await File.WriteAllBytesAsync(path, bytes);
        }

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

        public void WriteAllLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines);
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

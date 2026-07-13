using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    public sealed class TempDirectoryFileService : IFileService
    {
        private readonly string _tempRoot;
        private readonly FileService _inner = new();

        public TempDirectoryFileService(string tempRoot)
        {
            _tempRoot = tempRoot;
            Directory.CreateDirectory(_tempRoot);
        }

        public string GetTempPath() => _tempRoot;

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        public Task<string> ReadAllTextAsync(string path) => _inner.ReadAllTextAsync(path);
        public Task WriteAllTextAsync(string path, string content) => _inner.WriteAllTextAsync(path, content);
        public Task<byte[]> ReadAllBytesAsync(string path) => _inner.ReadAllBytesAsync(path);
        public Task WriteAllBytesAsync(string path, byte[] bytes) => _inner.WriteAllBytesAsync(path, bytes);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);
        public void DeleteDirectory(string path, bool recursive = false) => _inner.DeleteDirectory(path, recursive);
        public string CombinePaths(params string[] paths) => _inner.CombinePaths(paths);
        public string[] ReadAllLines(string path) => _inner.ReadAllLines(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public void WriteAllLines(string path, IEnumerable<string> lines) => _inner.WriteAllLines(path, lines);
        public bool IsReadOnly(string path) => _inner.IsReadOnly(path);
        public void SetReadOnly(string path, bool readOnly) => _inner.SetReadOnly(path, readOnly);
        public void WriteAllTextAndLock(string path, string content) => _inner.WriteAllTextAndLock(path, content);
        public void WriteAllLinesAndLock(string path, IEnumerable<string> lines) => _inner.WriteAllLinesAndLock(path, lines);
    }
}

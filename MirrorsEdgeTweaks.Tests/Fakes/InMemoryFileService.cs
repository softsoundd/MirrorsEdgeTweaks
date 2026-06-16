using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    // In-memory IFileService for tests. Backs the line-based members (FileExists/ReadAllLines/
    // WriteAllLines) with a dictionary; the byte/async/directory members throw because no current
    // test exercises them.
    public sealed class InMemoryFileService : IFileService
    {
        private readonly Dictionary<string, string[]> _files = new(StringComparer.Ordinal);

        public void Seed(string path, params string[] lines) => _files[path] = lines;

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string[] ReadAllLines(string path) =>
            _files.TryGetValue(path, out var lines) ? lines : throw new FileNotFoundException(path);

        public void WriteAllLines(string path, IEnumerable<string> lines) => _files[path] = lines.ToArray();

        public void DeleteFile(string path) => _files.Remove(path);

        // ---- Unused by the current test suite ----
        public Task<string> ReadAllTextAsync(string path) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string content) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path) => throw new NotSupportedException();
        public Task WriteAllBytesAsync(string path, byte[] bytes) => throw new NotSupportedException();
        public bool DirectoryExists(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();
        public void DeleteDirectory(string path, bool recursive = false) => throw new NotSupportedException();
        public string GetTempPath() => throw new NotSupportedException();
        public string CombinePaths(params string[] paths) => Path.Combine(paths);
        public bool IsReadOnly(string path) => false;
        public void SetReadOnly(string path, bool readOnly) { }
        public void WriteAllLinesPreservingReadOnly(string path, IEnumerable<string> lines) => WriteAllLines(path, lines);
    }
}

namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Creates a temporary ini file for the file-based graphics settings tests.
    // Dispose clears the read-only attribute that the service sets after a write, then deletes the file.
    public sealed class TempIniFile : IDisposable
    {
        public string Path { get; }

        public TempIniFile(params string[] lines)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"metweaks_test_{Guid.NewGuid():N}.ini");
            File.WriteAllLines(Path, lines);
        }

        public string[] ReadLines() => File.ReadAllLines(Path);

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.SetAttributes(Path, FileAttributes.Normal);
                    File.Delete(Path);
                }
            }
            catch
            {
                // A failed cleanup must not fail the test.
            }
        }
    }
}

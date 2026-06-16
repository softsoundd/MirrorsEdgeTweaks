namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Creates a real temporary ini file for exercising the file-based graphics settings logic, and
    // cleans it up on dispose (clearing the read-only attribute that the service applies after a write).
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
                // Best-effort cleanup; never fail a test on teardown.
            }
        }
    }
}

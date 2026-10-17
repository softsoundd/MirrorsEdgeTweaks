namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Creates a unique directory under the system temp path and deletes it recursively on dispose.
    public sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"metweaks_test_{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Cleanup is best effort, so a file that is still in use never fails the test.
            }
        }
    }
}

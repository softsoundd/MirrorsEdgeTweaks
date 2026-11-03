using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public sealed class OffsetFinderServiceTests : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly OffsetFinderService _offsetFinder = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void ReadFloatFromFile_ReadsTheFloatAtTheOffset()
        {
            string path = CreateFile([0xFF, .. BitConverter.GetBytes(0.4f), 0xFF]);

            Assert.Equal(0.4f, _offsetFinder.ReadFloatFromFile(path, 1));
        }

        [Fact]
        public void ReadFloatFromFile_ReadsAFileThatAnotherHandleIsWriting()
        {
            string path = CreateFile(BitConverter.GetBytes(0.75f));
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            Assert.Equal(0.75f, _offsetFinder.ReadFloatFromFile(path, 0));
        }

        [Fact]
        public void ReadFloatFromFile_ReturnsZero_WhenTheFloatRunsPastTheEnd()
        {
            string path = CreateFile([1, 2, 3]);

            Assert.Equal(0f, _offsetFinder.ReadFloatFromFile(path, 1));
        }

        [Fact]
        public void ReadFloatFromFile_ReturnsZero_ForAMissingFile()
        {
            Assert.Equal(0f, _offsetFinder.ReadFloatFromFile(Path.Combine(_temp.Path, "Missing.u"), 0));
        }

        [Fact]
        public void ReadFloatFromFile_ReturnsZero_ForANegativeOffset()
        {
            string path = CreateFile(BitConverter.GetBytes(0.4f));

            Assert.Equal(0f, _offsetFinder.ReadFloatFromFile(path, -1));
        }

        private string CreateFile(byte[] content)
        {
            string path = Path.Combine(_temp.Path, "Engine.u");
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}

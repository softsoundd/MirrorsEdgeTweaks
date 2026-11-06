using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class PatternHelperTests
    {
        private static readonly byte[] Data = { 0x00, 0xAA, 0xBB, 0xCC, 0xAA, 0xBB, 0xCC, 0xFF };

        [Fact]
        public void FindPattern_FindsFirstOccurrence()
        {
            Assert.Equal(1, PatternHelper.FindPattern(Data, new byte[] { 0xAA, 0xBB, 0xCC }));
        }

        [Fact]
        public void FindPattern_ReturnsMinusOneWhenAbsent()
        {
            Assert.Equal(-1, PatternHelper.FindPattern(Data, new byte[] { 0xDE, 0xAD }));
        }

        [Fact]
        public void FindPattern_RespectsStartOffset()
        {
            Assert.Equal(4, PatternHelper.FindPattern(Data, new byte[] { 0xAA, 0xBB, 0xCC }, start: 2));
        }

        [Fact]
        public void FindPattern_RespectsEndExclusive()
        {
            // A window ending before the pattern completes must not match.
            Assert.Equal(-1, PatternHelper.FindPattern(Data, new byte[] { 0xAA, 0xBB, 0xCC }, start: 2, endExclusive: 6));
            Assert.Equal(4, PatternHelper.FindPattern(Data, new byte[] { 0xAA, 0xBB, 0xCC }, start: 2, endExclusive: 7));
        }

        [Fact]
        public void FindPattern_ClampsOutOfRangeBounds()
        {
            Assert.Equal(1, PatternHelper.FindPattern(Data, new byte[] { 0xAA }, start: -5, endExclusive: 1000));
        }

        [Fact]
        public void FindPattern_EmptyPatternReturnsMinusOne()
        {
            Assert.Equal(-1, PatternHelper.FindPattern(Data, Array.Empty<byte>()));
        }

        [Fact]
        public void FindPattern_PatternLongerThanDataReturnsMinusOne()
        {
            Assert.Equal(-1, PatternHelper.FindPattern(new byte[] { 0x01 }, new byte[] { 0x01, 0x02 }));
        }

        [Fact]
        public void FindAll_ReturnsAllOccurrencesInOrder()
        {
            var hits = PatternHelper.FindAll(Data, new byte[] { 0xAA, 0xBB }).ToArray();

            Assert.Equal(new[] { 1, 4 }, hits);
        }

        [Fact]
        public void FindAll_FindsOverlappingOccurrences()
        {
            byte[] data = { 0x01, 0x01, 0x01 };

            var hits = PatternHelper.FindAll(data, new byte[] { 0x01, 0x01 }).ToArray();

            Assert.Equal(new[] { 0, 1 }, hits);
        }

        [Fact]
        public void FindUnique_ReturnsIndexForSingleMatch()
        {
            Assert.Equal(7, PatternHelper.FindUnique(Data, new byte[] { 0xFF }));
        }

        [Fact]
        public void FindUnique_ReturnsMinusOneWhenAbsent()
        {
            Assert.Equal(-1, PatternHelper.FindUnique(Data, new byte[] { 0xDE }));
        }

        [Fact]
        public void FindUnique_ThrowsWhenAmbiguous()
        {
            Assert.Throws<InvalidOperationException>(() => PatternHelper.FindUnique(Data, new byte[] { 0xAA, 0xBB }));
        }

        [Theory]
        [InlineData(20, 0)]
        [InlineData(20, 5)]
        [InlineData(20, 6)]
        [InlineData(20, 7)]
        [InlineData(20, 16)]
        [InlineData(16, 12)]
        public void FileContainsAny_FindsAMatch_WhereverItSitsAgainstTheChunks(int length, int position)
        {
            using var temp = new TempDirectory();
            byte[] pattern = { 0xAA, 0xBB, 0xCC, 0xDD };
            byte[] content = new byte[length];
            pattern.CopyTo(content, position);

            Assert.True(PatternHelper.FileContainsAny(CreateFile(temp, content), [pattern], chunkSize: 8));
        }

        [Fact]
        public void FileContainsAny_FindsTheSecondPattern()
        {
            using var temp = new TempDirectory();
            byte[] content = new byte[20];
            content[9] = 0xEE;

            Assert.True(PatternHelper.FileContainsAny(CreateFile(temp, content), [[0xAA, 0xBB], [0x00, 0xEE, 0x00]], chunkSize: 8));
        }

        [Fact]
        public void FileContainsAny_ReturnsFalse_WhenNoPatternOccurs()
        {
            using var temp = new TempDirectory();

            Assert.False(PatternHelper.FileContainsAny(CreateFile(temp, new byte[20]), [[0xAA, 0xBB], [0xCC]], chunkSize: 8));
        }

        [Fact]
        public void FileContainsAny_ReturnsFalse_WhenEveryPatternIsEmpty()
        {
            using var temp = new TempDirectory();
            string path = CreateFile(temp, new byte[20]);

            Assert.False(PatternHelper.FileContainsAny(path, [[]], chunkSize: 8));
            Assert.False(PatternHelper.FileContainsAny(path, [], chunkSize: 8));
        }

        [Fact]
        public void FileContainsAny_ReturnsFalse_ForAnEmptyFile()
        {
            using var temp = new TempDirectory();

            Assert.False(PatternHelper.FileContainsAny(CreateFile(temp, []), [[0xAA]], chunkSize: 8));
        }

        private static string CreateFile(TempDirectory temp, byte[] content)
        {
            string path = Path.Combine(temp.Path, "TdGame.u");
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}

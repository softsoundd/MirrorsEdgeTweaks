using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class BytePatternTests
    {
        [Fact]
        public void IndexOf_SkipsPrefixMatches_WhoseLaterBytesDiffer()
        {
            byte[] data = { 0x8B, 0x06, 0x00, 0xFF, 0x8B, 0x06, 0x11, 0xC3 };

            Assert.Equal(4, BytePattern.Parse("8B 06 ?? C3").IndexOf(data, 0, data.Length));
        }

        [Fact]
        public void IndexOf_FindsAMatchEndingAtTheWindowEnd()
        {
            byte[] data = { 0x90, 0x90, 0x8B, 0x06 };
            BytePattern pattern = new byte[] { 0x8B, 0x06 };

            Assert.Equal(2, pattern.IndexOf(data, 0, data.Length));
            Assert.Equal(-1, pattern.IndexOf(data, 0, data.Length - 1));
        }

        [Fact]
        public void IndexOf_MatchesAPatternThatStartsWithAWildcard()
        {
            byte[] data = { 0x8B, 0x06, 0x7F, 0xC3 };

            Assert.Equal(1, BytePattern.Parse("?? 7F C3").IndexOf(data, 0, data.Length));
        }

        [Fact]
        public void IndexOf_AnchorsOnTheLongestFixedRun_AndChecksTheBytesAroundIt()
        {
            // The run at index 0 has no room for the bytes before the anchor, and the run at index 6 follows the wrong byte.
            byte[] data = { 0x51, 0x52, 0x53, 0x00, 0xEE, 0x90, 0x51, 0x52, 0x53, 0x01, 0xA0, 0x90, 0x51, 0x52, 0x53, 0x02 };

            Assert.Equal(10, BytePattern.Parse("A0 ?? 51 52 53").IndexOf(data, 0, data.Length));
            Assert.Equal(-1, BytePattern.Parse("A0 ?? 51 52 53").IndexOf(data, 11, data.Length));
        }

        [Fact]
        public void IndexOf_MatchesAPatternOfOnlyWildcards()
        {
            byte[] data = { 0x01, 0x02, 0x03 };

            Assert.Equal(1, BytePattern.Parse("?? ??").IndexOf(data, 1, data.Length));
        }

        [Fact]
        public void IndexOf_StartsAtTheGivenOffset()
        {
            byte[] data = { 0xC3, 0x90, 0xC3 };
            BytePattern pattern = new byte[] { 0xC3 };

            Assert.Equal(2, pattern.IndexOf(data, 1, data.Length));
            Assert.Equal(-1, pattern.IndexOf(data, 3, data.Length));
        }
    }
}

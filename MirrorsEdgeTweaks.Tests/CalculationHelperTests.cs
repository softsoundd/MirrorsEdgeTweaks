using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class ByteArrayHelperTests
    {
        [Fact]
        public void StringToByteArray_ParsesHexPairs()
        {
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ByteArrayHelper.StringToByteArray("DEADBEEF"));
        }

        [Fact]
        public void StringToByteArray_ParsesLowercaseHex()
        {
            Assert.Equal(new byte[] { 0x0F, 0xA0 }, ByteArrayHelper.StringToByteArray("0fa0"));
        }

        [Fact]
        public void StringToByteArray_EmptyStringGivesEmptyArray()
        {
            Assert.Empty(ByteArrayHelper.StringToByteArray(""));
        }

        [Fact]
        public void StringToByteArray_ThrowsOnOddLength()
        {
            Assert.Throws<ArgumentException>(() => ByteArrayHelper.StringToByteArray("ABC"));
        }

        [Fact]
        public void FloatRoundTrip_PreservesValue()
        {
            byte[] buffer = new byte[16];

            ByteArrayHelper.WriteFloatToBytes(buffer, 4, 123.456f);

            Assert.Equal(123.456f, ByteArrayHelper.ReadFloatFromBytes(buffer, 4));
        }

        [Fact]
        public void ReadFloatFromBytes_OutOfRangeReturnsZero()
        {
            byte[] buffer = { 0x01, 0x02 };

            Assert.Equal(0f, ByteArrayHelper.ReadFloatFromBytes(buffer, 0));
            Assert.Equal(0f, ByteArrayHelper.ReadFloatFromBytes(buffer, -1));
        }

        [Fact]
        public void WriteFloatToBytes_OutOfRangeIsIgnored()
        {
            byte[] buffer = { 0x01, 0x02 };

            ByteArrayHelper.WriteFloatToBytes(buffer, 0, 1f);
            ByteArrayHelper.WriteFloatToBytes(buffer, -1, 1f);

            Assert.Equal(new byte[] { 0x01, 0x02 }, buffer);
        }
    }

    public class GameVersionHelperTests
    {
        [Theory]
        [InlineData("Game Version: 1.0.0.0", "Original", "Base_TdGame.zip")]
        [InlineData("Game Version: 1.0.1.0", "TdGame Fix (by Keku)", "Base_TdGameFix.zip")]
        [InlineData("Game Version: 1.1.0.0 (DLC)", "Time Trials Timer Fix (by Nulaft)", "DLC_TimerFix.zip")]
        [InlineData("Game Version: 1.1.0.0 (DLC)", "TdGame Fix + Time Trials Timer Fix", "DLC_TdGameFix+TimerFix.zip")]
        public void GetDownloadUrl_MapsVersionAndFixToAsset(string versionInfo, string fix, string expectedFile)
        {
            string? url = GameVersionHelper.GetDownloadUrl(versionInfo, fix);

            Assert.NotNull(url);
            Assert.EndsWith(expectedFile, url);
        }

        [Fact]
        public void GetDownloadUrl_UnknownGameVersionReturnsNull()
        {
            Assert.Null(GameVersionHelper.GetDownloadUrl("Game Version: 2.0.0.0", "Original"));
        }

        [Fact]
        public void GetDownloadUrl_UnknownFixReturnsNull()
        {
            Assert.Null(GameVersionHelper.GetDownloadUrl("Game Version: 1.0.0.0", "Nonexistent Fix"));
        }

        [Fact]
        public void GetGameVersion_MissingDirectoryIsInvalid()
        {
            var version = GameVersionHelper.GetGameVersion("");

            Assert.False(version.IsValid);
        }
    }
}

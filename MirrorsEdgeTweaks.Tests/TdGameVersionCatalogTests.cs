using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class TdGameVersionCatalogTests
    {
        [Fact]
        public void Names_AreTheFourSupportedVariantsInOrder()
        {
            Assert.Equal(
                new[]
                {
                    "Original",
                    "TdGame Fix (by Keku)",
                    "Time Trials Timer Fix (by Nulaft)",
                    "TdGame Fix + Time Trials Timer Fix"
                },
                TdGameVersionCatalog.Names);
        }

        [Theory]
        [InlineData("Original", 0)]
        [InlineData("TdGame Fix (by Keku)", 1)]
        [InlineData("Time Trials Timer Fix (by Nulaft)", 2)]
        [InlineData("TdGame Fix + Time Trials Timer Fix", 3)]
        public void IndexOf_ReturnsExpectedIndex(string name, int expectedIndex)
        {
            Assert.Equal(expectedIndex, TdGameVersionCatalog.IndexOf(name));
        }

        [Theory]
        [InlineData("Unknown")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("original")] // wrong case -> not matched (ordinal)
        public void IndexOf_ReturnsMinusOneForUnknown(string? name)
        {
            Assert.Equal(-1, TdGameVersionCatalog.IndexOf(name));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(99)]
        public void NameAt_ReturnsNullForOutOfRange(int index)
        {
            Assert.Null(TdGameVersionCatalog.NameAt(index));
        }

        [Fact]
        public void IndexOf_And_NameAt_RoundTrip()
        {
            for (int i = 0; i < TdGameVersionCatalog.Names.Count; i++)
            {
                string? name = TdGameVersionCatalog.NameAt(i);
                Assert.NotNull(name);
                Assert.Equal(i, TdGameVersionCatalog.IndexOf(name));
            }
        }
    }
}

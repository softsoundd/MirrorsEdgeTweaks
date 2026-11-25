using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class GameLanguageRegistryTests
    {
        // WriteLocaleAsync checks the value before it touches the registry.
        [Theory]
        [InlineData("en_UK /f")]
        [InlineData("en_UK\" /d x")]
        [InlineData("zh-TW")]
        [InlineData("")]
        public async Task WriteLocaleAsync_Throws_ForAValueThatIsNotALocale(string locale)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new GameLanguageRegistry().WriteLocaleAsync(locale));
        }
    }
}

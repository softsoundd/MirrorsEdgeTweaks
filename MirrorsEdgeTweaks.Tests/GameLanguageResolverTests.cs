using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class GameLanguageResolverTests
    {
        // These cases follow the Locale checks in MirrorsEdge.exe, including the quirks of its case-sensitive prefixes.
        [Theory]
        [InlineData("en_UK", "int")]
        [InlineData("en_US", "int")]
        [InlineData("fr_FR", "fra")]
        [InlineData("es_ES", "esn")]
        [InlineData("de_DE", "deu")]
        [InlineData("it_IT", "ita")]
        [InlineData("ru_RU", "rus")]
        [InlineData("pl_PL", "pol")]
        [InlineData("pt_PT", "por")]
        [InlineData("cs", "cze")]
        [InlineData("hu_HU", "hun")]
        [InlineData("jp_JP", "jpn")]
        [InlineData("ko_KR", "kor")]
        [InlineData("zh_CN", "chs")]
        [InlineData("zh_TW", "cht")]
        [InlineData("ZH_tw", "cht")]
        [InlineData("zh-TW", "chs")]
        [InlineData("zh_HK", "chs")]
        [InlineData("ja_JP", null)]
        [InlineData("EN_us", null)]
        [InlineData("e", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void MapRegistryLocale_FollowsTheGame(string? locale, string? expected)
        {
            Assert.Equal(expected, GameLanguageResolver.MapRegistryLocale(locale));
        }

        [Theory]
        [InlineData("en_UK", "fra", false, "int")]
        [InlineData("en_UK", "fra", true, "fra")]
        [InlineData("ja_JP", "fra", false, "fra")]
        [InlineData(null, "FRA", false, "fra")]
        [InlineData(null, "", false, "int")]
        [InlineData("xx", null, false, "int")]
        public void Resolve_PrefersARecognisedRegistryLocale_UnlessThePatchSkipsIt(
            string? registryLocale, string? iniLanguageCode, bool registryBypassed, string expected)
        {
            Assert.Equal(expected, GameLanguageResolver.Resolve(registryLocale, iniLanguageCode, registryBypassed));
        }

        [Theory]
        [InlineData("en_UK", "fra", "int")]
        [InlineData("en_UK", "int", null)]
        [InlineData("en_UK", "INT", null)]
        [InlineData("ja_JP", "fra", null)]
        [InlineData(null, "fra", null)]
        public void FindOverride_ReturnsTheRegistryLanguage_OnlyWhenItDiffers(string? registryLocale, string languageCode, string? expected)
        {
            Assert.Equal(expected, GameLanguageResolver.FindOverride(registryLocale, languageCode));
        }

        [Theory]
        [InlineData("cze")]
        [InlineData("deu")]
        [InlineData("int")]
        [InlineData("esn")]
        [InlineData("fra")]
        [InlineData("ita")]
        [InlineData("hun")]
        [InlineData("pol")]
        [InlineData("por")]
        [InlineData("rus")]
        [InlineData("kor")]
        [InlineData("cht")]
        [InlineData("jpn")]
        [InlineData("chs")]
        public void RegistryLocaleFor_GivesAWritableValueThatTheGameMapsBackToTheLanguage(string languageCode)
        {
            string? locale = GameLanguageResolver.RegistryLocaleFor(languageCode);

            Assert.NotNull(locale);
            Assert.Equal(languageCode, GameLanguageResolver.MapRegistryLocale(locale));
            Assert.True(GameLanguageRegistry.IsValidLocale(locale));
        }

        [Fact]
        public void RegistryLocaleFor_ReturnsNull_ForAnUnknownLanguage()
        {
            Assert.Null(GameLanguageResolver.RegistryLocaleFor("xyz"));
        }
    }
}

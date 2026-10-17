using MirrorsEdgeTweaks.ViewModels;

namespace MirrorsEdgeTweaks.Tests
{
    public class LanguageSettingsViewModelTests
    {
        [Theory]
        [InlineData("int", "INT.zip")]
        [InlineData("CZE", "CZE.zip")]
        [InlineData("por", "POR.zip")]
        [InlineData("xyz", null)]
        public void LanguagePackFor_MapsTheIniCodeToItsPack(string code, string? pack)
        {
            Assert.Equal(pack, LanguageSettingsViewModel.LanguagePackFor(code));
        }
    }
}

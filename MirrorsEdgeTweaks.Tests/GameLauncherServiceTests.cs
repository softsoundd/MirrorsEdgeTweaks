using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class GameLauncherServiceTests
    {
        [Theory]
        [InlineData("", "-applaunch 17410")]
        [InlineData("   ", "-applaunch 17410")]
        [InlineData("-nosound", "-applaunch 17410 -nosound")]
        [InlineData("  -nosound  ", "-applaunch 17410 -nosound")]
        [InlineData("-windowed -nosound", "-applaunch 17410 -windowed -nosound")]
        public void BuildSteamApplaunchArguments_FormatsExpectedCommandLine(string gameArguments, string expected)
        {
            Assert.Equal(expected, GameLauncherService.BuildSteamApplaunchArguments(gameArguments));
        }
    }
}

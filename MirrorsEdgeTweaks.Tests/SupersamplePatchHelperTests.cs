using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class SupersamplePatchHelperTests
    {
        // RemovePatch writes each hook's pattern back over the bytes that its jump replaced.
        [Theory]
        [InlineData("gog")]
        [InlineData("steam")]
        [InlineData("retail")]
        [InlineData("retold")]
        [InlineData("dlc")]
        [InlineData("ea")]
        public void EveryHookPattern_CoversAllTheBytesItsHookReplaces(string version)
        {
            var addrs = VersionAddressTable.Load(version);

            foreach (string hookName in SupersamplePatchHelper.HookNames)
                Assert.Equal(addrs.Hooks[hookName].Size, addrs.Hooks[hookName].Pattern.Length);
        }
    }
}

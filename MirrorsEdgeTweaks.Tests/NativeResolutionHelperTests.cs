using MirrorsEdgeTweaks.Helpers;
using System.Runtime.InteropServices;

namespace MirrorsEdgeTweaks.Tests
{
    public class NativeResolutionHelperTests
    {
        [Fact]
        public void InteropStructures_MatchTheWindowsSdkSizes()
        {
            Assert.Equal(72, Marshal.SizeOf<NativeResolutionHelper.PathInfo>());
            Assert.Equal(64, Marshal.SizeOf<NativeResolutionHelper.ModeInfo>());
            Assert.Equal(20, Marshal.SizeOf<NativeResolutionHelper.DeviceInfoHeader>());
            Assert.Equal(80, Marshal.SizeOf<NativeResolutionHelper.TargetPreferredMode>());
        }
    }
}

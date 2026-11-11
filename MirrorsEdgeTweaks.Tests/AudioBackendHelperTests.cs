using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class AudioBackendHelperTests
    {
        [Fact]
        public void ReadDeviceName_ReadsOnlyTheALAudioDeviceSection()
        {
            using var temp = new TempDirectory();
            string tdEngine = Path.Combine(temp.Path, "TdEngine.ini");
            File.WriteAllText(tdEngine,
                "[Engine.Engine]\r\nDeviceName=Wrong Section\r\n\r\n" +
                "[ALAudio.ALAudioDevice]\r\nMaxChannels=256\r\nDeviceName=OpenAL Soft\r\n");

            Assert.Equal(AudioBackendHelper.OpenAlSoftDeviceName, AudioBackendHelper.ReadDeviceName(tdEngine));
        }

        [Fact]
        public void ReadDeviceName_ReturnsNull_WhenTheIniIsMissing()
        {
            using var temp = new TempDirectory();

            Assert.Null(AudioBackendHelper.ReadDeviceName(Path.Combine(temp.Path, "TdEngine.ini")));
        }

        [Fact]
        public void DeleteHrtfFiles_LeavesTheOtherBinariesInPlace()
        {
            using var temp = new TempDirectory();
            string binaries = Path.Combine(temp.Path, "Binaries");
            Directory.CreateDirectory(binaries);
            foreach (string name in new[] { "soft_oal.dll", "alsoft.ini", "openal-hrtf-proxy.ini", "OpenAL32.dll" })
                File.WriteAllText(Path.Combine(binaries, name), "x");

            Assert.True(AudioBackendHelper.IsHrtfInstalled(temp.Path));
            AudioBackendHelper.DeleteHrtfFiles(temp.Path);

            Assert.False(AudioBackendHelper.IsHrtfInstalled(temp.Path));
            Assert.Equal(new[] { "OpenAL32.dll" }, Directory.GetFiles(binaries).Select(path => Path.GetFileName(path)));
        }
    }
}

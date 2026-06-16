using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class GraphicsSettingsServiceTests
    {
        private readonly GraphicsSettingsService _service = new();

        [Fact]
        public void ReadIniValue_ReturnsValueForExistingKey()
        {
            using var ini = new TempIniFile("[SystemSettings]", "UseVsync=True");

            Assert.Equal("True", _service.ReadIniValue(ini.Path, "UseVsync"));
        }

        [Fact]
        public void ReadIniValue_ReturnsNullForMissingKey()
        {
            using var ini = new TempIniFile("[SystemSettings]", "UseVsync=True");

            Assert.Null(_service.ReadIniValue(ini.Path, "DoesNotExist"));
        }

        [Fact]
        public void ReadIniValue_ReturnsNullForMissingFile()
        {
            string missingPath = Path.Combine(Path.GetTempPath(), $"metweaks_missing_{Guid.NewGuid():N}.ini");

            Assert.Null(_service.ReadIniValue(missingPath, "UseVsync"));
        }

        [Fact]
        public void ReadIniValue_SkipsCommentedLines()
        {
            using var ini = new TempIniFile("[SystemSettings]", ";Foo=Commented", "Bar=Real");

            Assert.Null(_service.ReadIniValue(ini.Path, "Foo"));
            Assert.Equal("Real", _service.ReadIniValue(ini.Path, "Bar"));
        }

        [Fact]
        public void ApplyVSync_ModifiesExistingKey()
        {
            using var ini = new TempIniFile("[SystemSettings]", "UseVsync=True");

            _service.ApplyVSync(ini.Path, enabled: false);

            Assert.Equal("False", _service.ReadIniValue(ini.Path, "UseVsync"));
        }

        [Fact]
        public void ApplyVSync_InsertsKeyUnderSystemSettingsWhenMissing()
        {
            using var ini = new TempIniFile(
                "[SystemSettings]",
                "SomeOtherKey=1",
                "[OtherSection]",
                "X=2");

            _service.ApplyVSync(ini.Path, enabled: true);

            Assert.Equal("True", _service.ReadIniValue(ini.Path, "UseVsync"));

            // The inserted line must live inside [SystemSettings], i.e. before [OtherSection].
            string[] lines = ini.ReadLines();
            int vsyncIndex = Array.FindIndex(lines, l => l.Trim().StartsWith("UseVsync=", StringComparison.OrdinalIgnoreCase));
            int otherSectionIndex = Array.FindIndex(lines, l => l.Trim().Equals("[OtherSection]", StringComparison.OrdinalIgnoreCase));
            Assert.InRange(vsyncIndex, 1, otherSectionIndex - 1);
        }

        [Fact]
        public void ApplyVSync_ThrowsWhenFileMissing()
        {
            string missingPath = Path.Combine(Path.GetTempPath(), $"metweaks_missing_{Guid.NewGuid():N}.ini");

            Assert.Throws<FileNotFoundException>(() => _service.ApplyVSync(missingPath, enabled: true));
        }
    }
}

using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class GraphicsSettingsServiceTests
    {
        private readonly GraphicsSettingsService _service = new(new FileService());

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

        public static TheoryData<string, Action<GraphicsSettingsService, string, bool>> BooleanToggles => new()
        {
            { "PhysXEnhanced", (s, p, v) => s.ApplyPhysX(p, v) },
            { "StaticDecals", (s, p, v) => s.ApplyStaticDecals(p, v) },
            { "DynamicDecals", (s, p, v) => s.ApplyDynamicDecals(p, v) },
            { "TdMotionBlur", (s, p, v) => s.ApplyRadialBlur(p, v) },
            { "LensFlares", (s, p, v) => s.ApplyLensFlare(p, v) },
            { "DynamicLights", (s, p, v) => s.ApplyDynamicLights(p, v) },
            { "DynamicShadows", (s, p, v) => s.ApplyDynamicShadows(p, v) },
            { "DirectionalLightmaps", (s, p, v) => s.ApplyLightmaps(p, v) },
            { "TdBicubicFiltering", (s, p, v) => s.ApplyHqLightmaps(p, v) },
            { "TdSunHaze", (s, p, v) => s.ApplySunHaze(p, v) },
            { "TdTonemapping", (s, p, v) => s.ApplyToneMapping(p, v) },
        };

        [Theory]
        [MemberData(nameof(BooleanToggles))]
        public void BooleanToggles_WriteInvariantTrueFalse(string key, Action<GraphicsSettingsService, string, bool> apply)
        {
            using var ini = new TempIniFile("[SystemSettings]", $"{key}=True");

            apply(_service, ini.Path, false);
            Assert.Equal("False", _service.ReadIniValue(ini.Path, key));

            apply(_service, ini.Path, true);
            Assert.Equal("True", _service.ReadIniValue(ini.Path, key));
        }

        [Fact]
        public void ApplyBloomAndDoF_WritesBothLinkedKeys()
        {
            using var ini = new TempIniFile("[SystemSettings]", "Bloom=True", "DepthOfField=True");

            _service.ApplyBloomAndDoF(ini.Path, enabled: false);

            Assert.Equal("False", _service.ReadIniValue(ini.Path, "Bloom"));
            Assert.Equal("False", _service.ReadIniValue(ini.Path, "DepthOfField"));
        }

        [Theory]
        [InlineData(50, "50.0", "True")]
        [InlineData(100, "100.0", "False")]
        [InlineData(200, "200.0", "True")]
        public void ApplyRenderResolution_WritesPercentageAndUpscaleFlag(int percentage, string expectedValue, string expectedUpscale)
        {
            using var ini = new TempIniFile("[SystemSettings]", "ScreenPercentage=100.000000", "UpscaleScreenPercentage=False");

            _service.ApplyRenderResolution(ini.Path, percentage);

            Assert.Equal(expectedValue, _service.ReadIniValue(ini.Path, "ScreenPercentage"));
            Assert.Equal(expectedUpscale, _service.ReadIniValue(ini.Path, "UpscaleScreenPercentage"));
        }

        [Theory]
        [InlineData("Off", "1")]
        [InlineData("2x", "2")]
        [InlineData("4x", "4")]
        public void ApplyAntiAliasing_WritesMultisampleCount(string level, string expected)
        {
            using var ini = new TempIniFile("[SystemSettings]", "MaxMultisamples=1");

            _service.ApplyAntiAliasing(ini.Path, level);

            Assert.Equal(expected, _service.ReadIniValue(ini.Path, "MaxMultisamples"));
        }

        [Fact]
        public void ApplyAnisotropicFiltering_WritesLevel()
        {
            using var ini = new TempIniFile("[SystemSettings]", "MaxAnisotropy=0");

            _service.ApplyAnisotropicFiltering(ini.Path, "Off");

            Assert.Equal("0", _service.ReadIniValue(ini.Path, "MaxAnisotropy"));
        }

        [Fact]
        public void ApplyMinAndMaxLod_ReplaceExistingNumbers()
        {
            using var ini = new TempIniFile("[TextureLODSettings]", "TEXTUREGROUP_World=(MinLODSize=1,MaxLODSize=4096,LODBias=0)");

            _service.ApplyMinLOD(ini.Path, 256);
            _service.ApplyMaxLOD(ini.Path, 1024);
            _service.ApplyLODBias(ini.Path, 2);

            string content = string.Join("\n", ini.ReadLines());
            Assert.Contains("MinLODSize=256", content);
            Assert.Contains("MaxLODSize=1024", content);
            Assert.Contains("LODBias=2", content);
        }

        [Fact]
        public void ApplyFPSLimit_SetsSmoothingAndValue()
        {
            using var ini = new TempIniFile("[Engine.GameEngine]", "bSmoothFrameRate=FALSE", "MaxSmoothedFrameRate=62");

            _service.ApplyFPSLimit(ini.Path, 144);

            var (isLimited, fpsValue) = _service.ReadFPSLimitStatus(ini.Path);
            Assert.True(isLimited);
            Assert.Equal(144, fpsValue);
        }

        [Fact]
        public void RemoveFPSLimit_DisablesSmoothing()
        {
            using var ini = new TempIniFile("[Engine.GameEngine]", "bSmoothFrameRate=TRUE", "MaxSmoothedFrameRate=62");

            _service.RemoveFPSLimit(ini.Path);

            var (isLimited, _) = _service.ReadFPSLimitStatus(ini.Path);
            Assert.False(isLimited);
        }

        [Fact]
        public void ApplyFPSLimit_ThrowsWhenKeysMissing()
        {
            using var ini = new TempIniFile("[Engine.GameEngine]", "SomethingElse=1");

            Assert.Throws<Exception>(() => _service.ApplyFPSLimit(ini.Path, 100));
        }

        [Fact]
        public void ReadFPSLimitStatus_MissingFileReportsUnlimited()
        {
            string missingPath = Path.Combine(Path.GetTempPath(), $"metweaks_missing_{Guid.NewGuid():N}.ini");

            var (isLimited, fpsValue) = _service.ReadFPSLimitStatus(missingPath);

            Assert.False(isLimited);
            Assert.Null(fpsValue);
        }

        [Fact]
        public void ApplyStreakEffect_TogglesAndReadsBack()
        {
            using var ini = new TempIniFile(
                "bEnableStreakEffect=true",
                "StreakDistanceInMovementDirection=120",
                "StreakDistanceInCameraDirection=120",
                "StreakEffectFadeTime=0.34f");

            _service.ApplyStreakEffect(ini.Path, enabled: false);
            Assert.Equal("false", _service.ReadStreakEffectStatus(ini.Path));

            _service.ApplyStreakEffect(ini.Path, enabled: true);
            Assert.Equal("true", _service.ReadStreakEffectStatus(ini.Path));
        }

        [Fact]
        public void ModifyIniFile_LeavesFileReadOnly()
        {
            using var ini = new TempIniFile("[SystemSettings]", "UseVsync=True");

            _service.ApplyVSync(ini.Path, enabled: false);

            Assert.True((File.GetAttributes(ini.Path) & FileAttributes.ReadOnly) != 0);
        }

        [Fact]
        public void ModifyIniFile_RewritesReadOnlyFiles()
        {
            using var ini = new TempIniFile("[SystemSettings]", "UseVsync=True");
            File.SetAttributes(ini.Path, FileAttributes.ReadOnly);

            _service.ApplyVSync(ini.Path, enabled: false);

            Assert.Equal("False", _service.ReadIniValue(ini.Path, "UseVsync"));
        }
    }
}

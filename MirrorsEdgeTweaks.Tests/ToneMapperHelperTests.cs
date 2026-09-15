using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.Fakes;

namespace MirrorsEdgeTweaks.Tests
{
    public class ToneMapperHelperTests
    {
        [Fact]
        public void HasMarkerAtTop_AcceptsMarkerAsFirstNonEmptyLine()
        {
            Assert.True(ToneMapperHelper.HasMarkerAtTop([ToneMapperHelper.Marker, "void main() {}"]));
        }

        [Fact]
        public void HasMarkerAtTop_AllowsLeadingBlankLines()
        {
            Assert.True(ToneMapperHelper.HasMarkerAtTop(["", "  ", ToneMapperHelper.Marker, "float4 Color;"]));
        }

        [Fact]
        public void HasMarkerAtTop_RejectsMarkerBelowOtherContent()
        {
            Assert.False(ToneMapperHelper.HasMarkerAtTop(["void main() {}", ToneMapperHelper.Marker]));
        }

        [Fact]
        public void HasMarkerAtTop_RejectsNearMissMarker()
        {
            Assert.False(ToneMapperHelper.HasMarkerAtTop([ToneMapperHelper.Marker + "2"]));
        }

        [Fact]
        public void HasMarkerAtTop_RejectsLegacyFunctionNameWithoutMarker()
        {
            Assert.False(ToneMapperHelper.HasMarkerAtTop(["float ApplyWhiteNeutralityCorrection(float luma) { return luma; }"]));
        }

        [Fact]
        public void IsFaithfulLumaInstalled_RequiresMarkerOnEveryShaderFile()
        {
            var files = new InMemoryFileService();
            string gameDir = @"C:\ME";
            SeedAllShaders(files, gameDir, ToneMapperHelper.Marker, "shader body");

            Assert.True(ToneMapperHelper.IsFaithfulLumaInstalled(gameDir, files));
        }

        [Fact]
        public void IsFaithfulLumaInstalled_ReturnsFalseWhenAnyShaderIsMissing()
        {
            var files = new InMemoryFileService();
            string gameDir = @"C:\ME";
            SeedAllShaders(files, gameDir, ToneMapperHelper.Marker, "shader body");
            files.DeleteFile(ShaderPath(gameDir, "TdToneMappingPixelShader.usf"));

            Assert.False(ToneMapperHelper.IsFaithfulLumaInstalled(gameDir, files));
        }

        [Fact]
        public void IsFaithfulLumaInstalled_ReturnsFalseWhenAnyShaderLacksTheMarker()
        {
            var files = new InMemoryFileService();
            string gameDir = @"C:\ME";
            SeedAllShaders(files, gameDir, ToneMapperHelper.Marker, "shader body");
            files.Seed(
                ShaderPath(gameDir, "GammaCorrectionPixelShader.usf"),
                "float4 Color;");

            Assert.False(ToneMapperHelper.IsFaithfulLumaInstalled(gameDir, files));
        }

        [Fact]
        public void IsFaithfulLumaInstalled_ReturnsFalseForEmptyGameDirectory()
        {
            Assert.False(ToneMapperHelper.IsFaithfulLumaInstalled(null, new InMemoryFileService()));
            Assert.False(ToneMapperHelper.IsFaithfulLumaInstalled("", new InMemoryFileService()));
        }

        private static void SeedAllShaders(InMemoryFileService files, string gameDir, params string[] lines)
        {
            foreach (string fileName in ToneMapperHelper.ShaderFileNames)
                files.Seed(ShaderPath(gameDir, fileName), lines);
        }

        private static string ShaderPath(string gameDir, string fileName) =>
            Path.Combine(gameDir, "Engine", "Shaders", fileName);
    }
}

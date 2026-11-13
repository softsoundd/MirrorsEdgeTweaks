using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.Tests
{
    public class DxvkHelperTests
    {
        [Theory]
        [InlineData("DXVK", true)]
        [InlineData(" dxvk ", true)]
        [InlineData("dxvk-fork (1.0)", false)]
        [InlineData("ReShade", false)]
        [InlineData(null, false)]
        public void IsDxvkProduct_ReadsTheProductName(string? product, bool expected)
        {
            Assert.Equal(expected, DxvkHelper.IsDxvkProduct(product));
        }

        [Fact]
        public void HasFloatDepthOption_LooksForTheOptionName()
        {
            Assert.True(DxvkHelper.HasFloatDepthOption("\0d3d9.shaderModel\0d3d9.useD32forD24\0"u8));
            Assert.False(DxvkHelper.HasFloatDepthOption("\0d3d9.shaderModel\0d3d9.useD24\0"u8));
        }

        [Fact]
        public void ExtractAssetDll_InstallsOnlyTheDll()
        {
            using var temp = new TempDirectory();
            string zip = WriteAssetZip(temp.Path);
            string binaries = Directory.CreateDirectory(Path.Combine(temp.Path, "Binaries")).FullName;

            DxvkHelper.ExtractAssetDll(zip, binaries);

            Assert.Equal(new[] { Path.Combine(binaries, "d3d9.dll") }, Directory.GetFiles(binaries));
            Assert.Equal(new byte[] { 0x4D, 0x5A }, File.ReadAllBytes(Path.Combine(binaries, "d3d9.dll")));
        }

        [Fact]
        public void ExtractAssetDll_KeepsAnExistingD3D9Dll()
        {
            using var temp = new TempDirectory();
            string zip = WriteAssetZip(temp.Path);
            string binaries = Directory.CreateDirectory(Path.Combine(temp.Path, "Binaries")).FullName;
            string dll = Path.Combine(binaries, "d3d9.dll");
            File.WriteAllBytes(dll, new byte[] { 0x01 });

            Assert.Throws<IOException>(() => DxvkHelper.ExtractAssetDll(zip, binaries));
            Assert.Equal(new byte[] { 0x01 }, File.ReadAllBytes(dll));
        }

        [Fact]
        public void RemoveDxvk_DeletesTheDllAndLog_AndAConfHoldingOnlyFloatDepth()
        {
            using var temp = new TempDirectory();
            File.WriteAllBytes(Path.Combine(temp.Path, "d3d9.dll"), new byte[] { 0x4D, 0x5A });
            File.WriteAllText(Path.Combine(temp.Path, "MirrorsEdge_d3d9.log"), "info:  DXVK: v3.1.1");
            DxvkHelper.EnableFloatDepth(temp.Path);

            DxvkHelper.RemoveDxvk(temp.Path);

            Assert.Empty(Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void RemoveDxvk_KeepsAConfWithOtherSettings()
        {
            using var temp = new TempDirectory();
            File.WriteAllBytes(Path.Combine(temp.Path, "d3d9.dll"), new byte[] { 0x4D, 0x5A });
            string conf = Path.Combine(temp.Path, "dxvk.conf");
            File.WriteAllLines(conf, new[] { "d3d9.samplerLodBias = -1.0", "d3d9.useD32forD24 = True" });

            DxvkHelper.RemoveDxvk(temp.Path);

            Assert.False(File.Exists(Path.Combine(temp.Path, "d3d9.dll")));
            Assert.True(File.Exists(conf));
        }

        [Fact]
        public void RemoveDll_KeepsAConfHoldingOnlyFloatDepth()
        {
            using var temp = new TempDirectory();
            File.WriteAllBytes(Path.Combine(temp.Path, "d3d9.dll"), new byte[] { 0x4D, 0x5A });
            File.WriteAllText(Path.Combine(temp.Path, "MirrorsEdge_d3d9.log"), "info:  DXVK: v3.1.1");
            DxvkHelper.EnableFloatDepth(temp.Path);

            DxvkHelper.RemoveDll(temp.Path);

            Assert.Equal(new[] { Path.Combine(temp.Path, "dxvk.conf") }, Directory.GetFiles(temp.Path));
        }

        [Fact]
        public void ReadFloatDepth_IsNull_WithoutALineThatSetsIt()
        {
            Assert.Null(DxvkHelper.ReadFloatDepth(new[] { "# d3d9.useD32forD24 = False", "[Other.exe]", "d3d9.useD32forD24 = False" }));
            Assert.False(DxvkHelper.ReadFloatDepth(new[] { "d3d9.useD32forD24 = False" }));
            Assert.True(DxvkHelper.ReadFloatDepth(new[] { "d3d9.useD32forD24 = False", "[MirrorsEdge.exe]", "d3d9.useD32forD24 = True" }));
        }

        [Fact]
        public void IsFloatDepthEnabled_ReadsGlobalAndGameSettings()
        {
            Assert.False(DxvkHelper.IsFloatDepthEnabled(new[] { "# d3d9.useD32forD24 = True" }));
            Assert.True(DxvkHelper.IsFloatDepthEnabled(new[] { "d3d9.useD32forD24 = True" }));
            Assert.False(DxvkHelper.IsFloatDepthEnabled(new[] { "d3d9.useD32forD24 = True", "d3d9.useD32forD24 = False" }));
            Assert.False(DxvkHelper.IsFloatDepthEnabled(new[] { "d3d9.useD32forD24 = True", "[MirrorsEdge.exe]", "d3d9.useD32forD24 = False" }));
            Assert.True(DxvkHelper.IsFloatDepthEnabled(new[] { "[MirrorsEdge.exe]", "d3d9.useD32forD24 = true" }));
            Assert.False(DxvkHelper.IsFloatDepthEnabled(new[] { "[Other.exe]", "d3d9.useD32forD24 = True" }));
        }

        [Fact]
        public void WithFloatDepth_UncommentsTheTemplateLine()
        {
            var lines = new[] { "# Use D32f for D24", "#", "# d3d9.useD32forD24 = False", "", "d3d9.samplerLodBias = -1.0" };

            var result = DxvkHelper.WithFloatDepth(lines);

            Assert.Equal(new[] { "# Use D32f for D24", "#", "d3d9.useD32forD24 = True", "", "d3d9.samplerLodBias = -1.0" }, result);
        }

        [Fact]
        public void WithFloatDepth_SetsExistingSettings_IncludingTheGameSection()
        {
            var lines = new[] { "  d3d9.useD32forD24 = False", "[MirrorsEdge.exe]", "d3d9.useD32forD24 = False", "[Other.exe]", "d3d9.useD32forD24 = False" };

            var result = DxvkHelper.WithFloatDepth(lines);

            Assert.Equal(new[] { "  d3d9.useD32forD24 = True", "[MirrorsEdge.exe]", "d3d9.useD32forD24 = True", "[Other.exe]", "d3d9.useD32forD24 = False" }, result);
            Assert.True(DxvkHelper.IsFloatDepthEnabled(result));
        }

        [Fact]
        public void WithFloatDepth_AddsTheSettingAboveTheFirstSection()
        {
            var lines = new[] { "dxgi.maxFrameLatency = 1", "[Other.exe]", "d3d9.shaderModel = 2" };

            var result = DxvkHelper.WithFloatDepth(lines);

            Assert.Equal(new[] { "dxgi.maxFrameLatency = 1", "d3d9.useD32forD24 = True", "[Other.exe]", "d3d9.shaderModel = 2" }, result);
        }

        [Fact]
        public void EnableFloatDepth_CreatesTheFile_AndKeepsOtherSettings()
        {
            using var temp = new TempDirectory();

            DxvkHelper.EnableFloatDepth(temp.Path);
            Assert.True(DxvkHelper.IsFloatDepthEnabled(temp.Path));

            string conf = Path.Combine(temp.Path, "dxvk.conf");
            File.WriteAllLines(conf, new[] { "d3d9.samplerLodBias = -1.0", "d3d9.useD32forD24 = False" });
            DxvkHelper.EnableFloatDepth(temp.Path);
            Assert.Equal(new[] { "d3d9.samplerLodBias = -1.0", "d3d9.useD32forD24 = True" }, File.ReadAllLines(conf));
        }

        // Matches the layout of the published asset.
        private static string WriteAssetZip(string directory)
        {
            string zip = Path.Combine(directory, DxvkHelper.AssetFileName);
            using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            archive.CreateEntry("Binaries/");
            using (Stream dll = archive.CreateEntry("Binaries/d3d9.dll").Open())
                dll.Write(new byte[] { 0x4D, 0x5A });
            using (var license = new StreamWriter(archive.CreateEntry("Binaries/DXVK-LICENSE.txt").Open()))
                license.Write("zlib/libpng license");
            return zip;
        }
    }
}

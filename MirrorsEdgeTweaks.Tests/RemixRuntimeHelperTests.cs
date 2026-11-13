using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.IO.Compression;
using System.Text;

namespace MirrorsEdgeTweaks.Tests
{
    public class RemixRuntimeHelperTests
    {
        private static readonly byte[] BridgeBytes = { 0x4D, 0x5A, 0x01 };
        private static readonly byte[] NgxRuntimeBytes = "MZ\0rtx.ngxPassthrough\0d3d9.useD32forD24\0"u8.ToArray();

        [Theory]
        [InlineData("bridge-remix (remix-main+d0b1a59f)", true)]
        [InlineData("bridge-remix (remix-main)", true)]
        [InlineData(" Bridge-Remix ", true)]
        [InlineData("dxvk-remix (remix-main+d0b1a59f)", false)]
        [InlineData("DXVK", false)]
        [InlineData(null, false)]
        public void IsBridgeProduct_ComparesTheStartOfTheProductName(string? product, bool expected)
        {
            Assert.Equal(expected, RemixRuntimeHelper.IsBridgeProduct(product));
        }

        [Theory]
        [InlineData("\0rtx.ngxPassthrough\0d3d9.useD32forD24\0", D3D9Runtime.RemixNgx)]
        [InlineData("\0rtx.ngxPassthrough\0d3d9.shaderModel\0", D3D9Runtime.RemixNgxOutdated)]
        [InlineData("\0rtx.pathtracing\0d3d9.useD32forD24\0", D3D9Runtime.RemixPathTraced)]
        [InlineData("", D3D9Runtime.RemixPathTraced)]
        public void ClassifyRemix_ReadsTheRuntimesOptionNames(string runtimeStrings, D3D9Runtime expected)
        {
            Assert.Equal(expected, RemixRuntimeHelper.ClassifyRemix(Encoding.ASCII.GetBytes(runtimeStrings)));
        }

        [Fact]
        public void Detect_IsStock_WithoutAD3D9Dll()
        {
            using var temp = new TempDirectory();

            Assert.Equal(D3D9Runtime.Stock, RemixRuntimeHelper.Detect(temp.Path));
        }

        [Fact]
        public void Detect_IsOtherWrapper_WithoutTheDxvkProductName_EvenWithTheOptionName()
        {
            using var temp = new TempDirectory();
            File.WriteAllBytes(Path.Combine(temp.Path, "d3d9.dll"), "MZ\0d3d9.useD32forD24\0"u8.ToArray());

            Assert.Equal(D3D9Runtime.OtherWrapper, RemixRuntimeHelper.Detect(temp.Path));
        }

        [Fact]
        public void Detect_IsOtherWrapper_WithoutABridgeProductName_EvenBesideAnNgxRuntime()
        {
            using var temp = new TempDirectory();
            File.WriteAllBytes(Path.Combine(temp.Path, "d3d9.dll"), "MZ\0bridge-remix\0"u8.ToArray());
            Directory.CreateDirectory(Path.Combine(temp.Path, ".trex"));
            File.WriteAllBytes(Path.Combine(temp.Path, ".trex", "d3d9.dll"), NgxRuntimeBytes);

            Assert.Equal(D3D9Runtime.OtherWrapper, RemixRuntimeHelper.Detect(temp.Path));
        }

        [Fact]
        public void Install_ReplacesDxvkAndAStaleRuntime_AndKeepsTheConfigs()
        {
            using var temp = new TempDirectory();
            string zip = WriteAssetZip(temp.Path);
            string game = Path.Combine(temp.Path, "game");
            string binaries = Directory.CreateDirectory(Path.Combine(game, "Binaries")).FullName;
            File.WriteAllBytes(Path.Combine(binaries, "d3d9.dll"), new byte[] { 0x01 });
            File.WriteAllText(Path.Combine(binaries, "MirrorsEdge_d3d9.log"), "info:  DXVK: v3.1.1");
            DxvkHelper.EnableFloatDepth(binaries);
            File.WriteAllText(Path.Combine(binaries, "rtx.conf"), "rtx.ngxPassthrough.disableGameMsaa = True");
            string stale = Path.Combine(Directory.CreateDirectory(Path.Combine(binaries, ".trex")).FullName, "stale.dll");
            File.WriteAllBytes(stale, new byte[] { 0x02 });
            File.SetAttributes(stale, FileAttributes.ReadOnly);

            RemixRuntimeHelper.Install(zip, game, D3D9Runtime.Dxvk);

            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(Path.Combine(binaries, "MirrorsEdge_d3d9.log")));
            Assert.True(DxvkHelper.IsFloatDepthEnabled(binaries));
            Assert.Equal("rtx.ngxPassthrough.disableGameMsaa = True", File.ReadAllText(Path.Combine(binaries, "rtx.conf")));
            Assert.Equal(BridgeBytes, File.ReadAllBytes(Path.Combine(binaries, "d3d9.dll")));
            Assert.True(File.Exists(Path.Combine(binaries, "NvRemixLauncher32.exe")));
            Assert.Equal(NgxRuntimeBytes, File.ReadAllBytes(Path.Combine(binaries, ".trex", "d3d9.dll")));
        }

        [Fact]
        public void Install_RejectsAnIncompleteDownload_WithoutChangingTheGameFiles()
        {
            using var temp = new TempDirectory();
            string zip = Path.Combine(temp.Path, "incomplete.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                WriteEntry(archive, "Binaries/d3d9.dll", BridgeBytes);

            string game = Path.Combine(temp.Path, "game");
            string binaries = Directory.CreateDirectory(Path.Combine(game, "Binaries")).FullName;
            string client = Path.Combine(binaries, "d3d9.dll");
            string runtime = Path.Combine(Directory.CreateDirectory(Path.Combine(binaries, ".trex")).FullName, "d3d9.dll");
            File.WriteAllBytes(client, new byte[] { 0x01 });
            File.WriteAllBytes(runtime, new byte[] { 0x02 });

            Assert.Throws<UserFacingException>(() => RemixRuntimeHelper.Install(zip, game, D3D9Runtime.RemixNgxOutdated));
            Assert.Equal(new byte[] { 0x01 }, File.ReadAllBytes(client));
            Assert.Equal(new byte[] { 0x02 }, File.ReadAllBytes(runtime));
        }

        [Fact]
        public void Install_KeepsAnotherModsD3D9Dll()
        {
            using var temp = new TempDirectory();
            string zip = WriteAssetZip(temp.Path);
            string game = Path.Combine(temp.Path, "game");
            string binaries = Directory.CreateDirectory(Path.Combine(game, "Binaries")).FullName;
            string dll = Path.Combine(binaries, "d3d9.dll");
            File.WriteAllBytes(dll, new byte[] { 0x4D, 0x5A });

            Assert.Throws<UserFacingException>(() => RemixRuntimeHelper.Install(zip, game));
            Assert.Equal(new byte[] { 0x4D, 0x5A }, File.ReadAllBytes(dll));
            Assert.False(Directory.Exists(Path.Combine(binaries, ".trex")));
        }

        [Fact]
        public void Remove_DeletesTheBridgeAndRuntime_AndKeepsTheSettingsAndLogs()
        {
            using var temp = new TempDirectory();
            File.WriteAllBytes(Path.Combine(temp.Path, "d3d9.dll"), BridgeBytes);
            File.WriteAllBytes(Path.Combine(temp.Path, "NvRemixLauncher32.exe"), new byte[] { 0x4D, 0x5A });
            string trex = Directory.CreateDirectory(Path.Combine(temp.Path, ".trex")).FullName;
            string runtime = Path.Combine(trex, "d3d9.dll");
            File.WriteAllBytes(runtime, NgxRuntimeBytes);
            File.SetAttributes(runtime, FileAttributes.ReadOnly);
            DxvkHelper.EnableFloatDepth(temp.Path);
            File.WriteAllText(Path.Combine(temp.Path, "rtx.conf"), "rtx.ngxPassthrough.disableGameMsaa = True");
            File.WriteAllText(Path.Combine(temp.Path, "user.conf"), "rtx.dlfg.enable = True");
            string log = Path.Combine(Directory.CreateDirectory(Path.Combine(temp.Path, "rtx-remix", "logs")).FullName, "remix-dxvk.log");
            File.WriteAllText(log, "Scene depth is reversed");

            RemixRuntimeHelper.Remove(temp.Path);

            Assert.False(Directory.Exists(trex));
            Assert.Equal(new[] { "dxvk.conf", "rtx.conf", "user.conf" },
                Directory.GetFiles(temp.Path).Select(file => Path.GetFileName(file)!).Order());
            Assert.True(File.Exists(log));
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("d3d9.useD32forD24 = False", true)]
        [InlineData("d3d9.useD32forD24 = True", false)]
        [InlineData("# d3d9.useD32forD24 = False", false)]
        [InlineData("[MirrorsEdge.exe]\nd3d9.useD32forD24 = False", true)]
        public void IsFloatDepthOffInRtxConf_OnlyFindsALineThatTurnsItOff(string? rtxConf, bool expected)
        {
            using var temp = new TempDirectory();
            if (rtxConf != null)
                File.WriteAllText(Path.Combine(temp.Path, "rtx.conf"), rtxConf);

            Assert.Equal(expected, RemixRuntimeHelper.IsFloatDepthOffInRtxConf(temp.Path));
        }

        [Fact]
        public void InstalledVersion_IsRemovedWithTheRuntime()
        {
            using var temp = new TempDirectory();
            Directory.CreateDirectory(Path.Combine(temp.Path, ".trex"));
            Assert.Null(RemixRuntimeHelper.ReadInstalledVersion(temp.Path));

            RemixRuntimeHelper.WriteInstalledVersion(temp.Path, "\"0x8DF41C31BD7C9B8\"");
            Assert.Equal("\"0x8DF41C31BD7C9B8\"", RemixRuntimeHelper.ReadInstalledVersion(temp.Path));

            RemixRuntimeHelper.Remove(temp.Path);
            Assert.Null(RemixRuntimeHelper.ReadInstalledVersion(temp.Path));
        }

        // Matches the layout of the published asset.
        private static string WriteAssetZip(string directory)
        {
            string zip = Path.Combine(directory, RemixRuntimeHelper.AssetFileName);
            using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            archive.CreateEntry("Binaries/");
            archive.CreateEntry("Binaries/.trex/");
            WriteEntry(archive, "Binaries/.trex/d3d9.dll", NgxRuntimeBytes);
            WriteEntry(archive, "Binaries/.trex/NvRemixBridge.exe", new byte[] { 0x4D, 0x5A });
            WriteEntry(archive, "Binaries/d3d9.dll", BridgeBytes);
            WriteEntry(archive, "Binaries/NvRemixLauncher32.exe", new byte[] { 0x4D, 0x5A });
            return zip;
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            using Stream stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }
}

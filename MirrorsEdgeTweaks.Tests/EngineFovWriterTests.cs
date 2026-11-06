using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("BackupRoot")]
    public sealed class EngineFovWriterTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public EngineFovWriterTests() => PatchUtility.BackupRootOverrideForTests = Path.Combine(_temp.Path, "backups");

        public void Dispose()
        {
            PatchUtility.BackupRootOverrideForTests = null;
            _temp.Dispose();
        }

        [Fact]
        public void Write_WritesTheFovAtEachFoundOffset_IntoAReadOnlyPackage()
        {
            string path = Path.Combine(_temp.Path, "Engine.u");
            File.WriteAllBytes(path, new byte[16]);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var offsets = new PackageOffsets { PlayerControllerDefaultFovOffset = 0, CameraActorFovAngleOffset = 12 };

            EngineFovWriter.Write(path, offsets, 100f);

            byte[] engine = File.ReadAllBytes(path);
            Assert.Equal(100f, BitConverter.ToSingle(engine, 0));
            Assert.Equal(new byte[8], engine[4..12]);
            Assert.Equal(100f, BitConverter.ToSingle(engine, 12));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}

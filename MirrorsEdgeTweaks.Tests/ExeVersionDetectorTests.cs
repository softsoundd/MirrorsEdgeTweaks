using MirrorsEdgeTweaks.Helpers;
using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Tests
{
    public class ExeVersionDetectorTests
    {
        private const uint ImageBase = 0x00400000;
        private const long VanillaSteamExeSize = 31946072;

        [Fact]
        public void IsSteamExecutable_DetectsSteam_FromCaveTag_WhenFileSizeDiffers()
        {
            byte[] pe = BuildMinimalPe32();
            var cave = CaveSection.Open(pe, versionTag: "steam");
            cave.Alloc(16);
            byte[] patched = cave.Finalize();

            Assert.NotEqual(VanillaSteamExeSize, patched.Length);

            string tempExe = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(tempExe, patched);

                Assert.True(ExeVersionDetector.IsSteamExecutable(tempExe));
            }
            finally
            {
                if (File.Exists(tempExe))
                    File.Delete(tempExe);
            }
        }

        [Fact]
        public void IsSteamExecutable_ReturnsFalse_ForGogCaveTag()
        {
            byte[] pe = BuildMinimalPe32();
            var cave = CaveSection.Open(pe, versionTag: "gog");
            cave.Alloc(16);
            byte[] patched = cave.Finalize();

            string tempExe = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(tempExe, patched);
                Assert.False(ExeVersionDetector.IsSteamExecutable(tempExe));
            }
            finally
            {
                if (File.Exists(tempExe))
                    File.Delete(tempExe);
            }
        }

        private static byte[] BuildMinimalPe32()
        {
            byte[] buffer = new byte[0x600];

            buffer[0] = (byte)'M';
            buffer[1] = (byte)'Z';
            const int peOffset = 0x80;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x3C, 4), peOffset);

            buffer[peOffset] = (byte)'P';
            buffer[peOffset + 1] = (byte)'E';

            int coff = peOffset + 4;
            const ushort optHeaderSize = 0xE0;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(coff + 2, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(coff + 16, 2), optHeaderSize);

            int opt = coff + 20;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(opt, 2), 0x10B);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 28, 4), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 32, 4), 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 36, 4), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 60, 4), 0x200);

            int resourceEntry = opt + 96 + 2 * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(resourceEntry, 4), 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(resourceEntry + 4, 4), 0x100);

            int sectionTable = opt + optHeaderSize;
            WriteSection(buffer, sectionTable, ".text", 0x200, 0x1000, 0x200, 0x200, 0x60000020);
            WriteSection(buffer, sectionTable + 40, ".rsrc", 0x100, 0x2000, 0x100, 0x400, 0x40000040);

            return buffer;
        }

        private static void WriteSection(byte[] buffer, int offset, string name, uint virtualSize,
            uint virtualAddress, uint rawSize, uint rawOffset, uint characteristics)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(buffer, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8, 4), virtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 12, 4), virtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 16, 4), rawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 20, 4), rawOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 36, 4), characteristics);
        }
    }
}

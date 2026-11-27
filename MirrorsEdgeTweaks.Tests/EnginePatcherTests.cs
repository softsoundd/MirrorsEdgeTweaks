using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class EnginePatcherTests
    {
        // Blob A goes in at PointA (CheckViewTarget), and blob B at PointB (the FillCameraCache call).
        // None of these values or their shifted values is an opcode byte, so the byte scan only finds the jump under test.
        private const int PointA = 0x20, PointB = 0x40, Bss = 0x60, BlobALength = 0x10, BlobBLength = 0x08;
        private const int JumpOffset = 0x04;

        private static readonly byte[] JumpOpcodes = [BytecodeBuilder.OP_JUMP, BytecodeBuilder.OP_JUMP_IF_NOT, BytecodeBuilder.OP_CASE];

        [Theory]
        [InlineData(0x10, 0x10)]     // A target before blob A stays.
        [InlineData(0x20, 0x30)]     // CheckViewTarget moves past blob A.
        [InlineData(0x30, 0x40)]     // A target between the blobs moves past blob A.
        [InlineData(0x40, 0x50)]     // The FillCameraCache call moves to the start of blob B.
        [InlineData(0x50, 0x68)]     // A target after the FillCameraCache call moves past both blobs.
        [InlineData(0x60, 0x60)]     // A target outside the script stays.
        [InlineData(0xFFFF, 0xFFFF)] // A default case stays.
        public void ShiftPhase2JumpTargets_MovesEachTargetPastTheBlobsInsertedBeforeIt(int target, int expected)
        {
            foreach (byte opcode in JumpOpcodes)
            {
                byte[] data = BuildScript(opcode, target);

                EnginePatcher.ShiftPhase2JumpTargets(data, 0, Bss - 3, PointA, PointB, Bss, BlobALength, BlobBLength);

                Assert.Equal(expected, BitConverter.ToUInt16(data, JumpOffset + 1));
            }
        }

        // Remove shifts the targets back with these two passes, so they must undo the apply's shifts.
        [Theory]
        [InlineData(0x10)]
        [InlineData(0x20)]
        [InlineData(0x30)]
        [InlineData(0x40)]
        [InlineData(0x41)]
        [InlineData(0x5F)]
        public void ShiftPhase2JumpTargets_IsUndoneByRemovesShifts(int target)
        {
            byte[] data = BuildScript(BytecodeBuilder.OP_JUMP, target);

            EnginePatcher.ShiftPhase2JumpTargets(data, 0, Bss - 3, PointA, PointB, Bss, BlobALength, BlobBLength);

            int blobBStart = PointB + BlobALength;
            PackageSplicer.ShiftJumpTargets(data, 0, [JumpOffset], blobBStart + BlobBLength, -BlobBLength);
            PackageSplicer.ShiftJumpTargets(data, 0, [JumpOffset], PointA + BlobALength, -BlobALength);

            Assert.Equal(target, BitConverter.ToUInt16(data, JumpOffset + 1));
        }

        private static byte[] BuildScript(byte opcode, int target)
        {
            byte[] data = new byte[Bss];
            data[JumpOffset] = opcode;
            BitConverter.GetBytes((ushort)target).CopyTo(data, JumpOffset + 1);
            return data;
        }
    }
}

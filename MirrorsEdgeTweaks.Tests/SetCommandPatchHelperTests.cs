using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Buffers.Binary;

namespace MirrorsEdgeTweaks.Tests
{
    public class SetCommandPatchHelperTests
    {
        // The DLC exe's SET and SETNOPEC jumps and their stock target all lie in this page of .text.
        private const uint TextPageVa = 0x0110B000;
        private const uint JneSetVa = 0x0110BC9F, JneSetNoPecVa = 0x0110BCB9, OriginalTargetVa = 0x0110B671;
        private const int CaveHeaderSize = 12;

        [Fact]
        public void RemoveFromImage_PointsBothJumpsBack_AndFreesThePayload()
        {
            byte[] stock = BuildStockImage();
            byte[] patched = SetCommandPatchHelper.ApplyToImage((byte[])stock.Clone())!;
            Assert.NotEqual(OriginalTargetVa, JumpTarget(patched, JneSetVa));
            Assert.NotEqual(OriginalTargetVa, JumpTarget(patched, JneSetNoPecVa));

            byte[] restored = SetCommandPatchHelper.RemoveFromImage(patched)!;

            Assert.Equal(ReadAtVa(stock, JneSetVa), ReadAtVa(restored, JneSetVa));
            Assert.Equal(ReadAtVa(stock, JneSetNoPecVa), ReadAtVa(restored, JneSetNoPecVa));
            Assert.Equal(CaveHeaderSize, CaveSection.Open(restored).CurrentWatermark);
            Assert.NotNull(SetCommandPatchHelper.ApplyToImage(restored));
        }

        [Fact]
        public void RemoveFromImage_ReturnsNull_ForAStockImage()
        {
            Assert.Null(SetCommandPatchHelper.RemoveFromImage(BuildStockImage()));
        }

        [Fact]
        public void RemoveFromImage_Throws_WhenOnlyOneJumpIsRedirected()
        {
            byte[] image = BuildStockImage();
            WriteJne(image, JneSetVa, OriginalTargetVa + 0x10);

            Assert.Throws<InvalidOperationException>(() => SetCommandPatchHelper.RemoveFromImage(image));
        }

        private static byte[] BuildStockImage()
        {
            byte[] image = SyntheticPe.BuildWithText(TextPageVa - SyntheticPe.ImageBase, 0x1000);
            WriteJne(image, JneSetVa, OriginalTargetVa);
            WriteJne(image, JneSetNoPecVa, OriginalTargetVa);
            return image;
        }

        private static void WriteJne(byte[] image, uint va, uint target)
        {
            byte[] jne = new byte[6];
            jne[0] = 0x0F;
            jne[1] = 0x85;
            BinaryPrimitives.WriteInt32LittleEndian(jne.AsSpan(2), (int)(target - (va + 6)));
            PeImageLayout.Parse(image).WriteAtVa(va, jne);
        }

        private static byte[] ReadAtVa(byte[] image, uint va) => PeImageLayout.Parse(image).ReadAtVa(va, 6);

        private static uint JumpTarget(byte[] image, uint va) =>
            va + 6 + (uint)BinaryPrimitives.ReadInt32LittleEndian(ReadAtVa(image, va).AsSpan(2));
    }
}

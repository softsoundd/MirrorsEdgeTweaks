using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using static MirrorsEdgeTweaks.Tests.TestSupport.SyntheticPe;

namespace MirrorsEdgeTweaks.Tests
{
    public class RenderTargetPatcherTests
    {
        private const int DecoyJne = TextRaw + 0x20;
        private const int DecoyConstant = TextRaw + 0x40;
        private const int Site = TextRaw + 0x200;

        [Fact]
        public void PatchImage_FlipsOnlyTheAnchoredJne_AndIsIdempotent()
        {
            byte[] image = BuildImage();
            byte[] expected = (byte[])image.Clone();
            expected[Site] = 0xEB;

            Assert.True(RenderTargetPatcher.PatchImage(image));
            Assert.Equal(expected, image);

            Assert.False(RenderTargetPatcher.PatchImage(image));
            Assert.Equal(expected, image);
        }

        [Fact]
        public void PatchImage_Throws_WhenTheSiteIsMissing()
        {
            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.PatchImage(SyntheticPe.Build()));
        }

        // Before the site, .text holds a 0.5625 constant whose reference follows a stray jne byte.
        // The high-res UI fix's cave has the same shape in Retail and DLC .text padding.
        private static byte[] BuildImage()
        {
            byte[] image = SyntheticPe.Build();
            uint decoyConstantVa = ImageBase + TextRva + (DecoyConstant - TextRaw);
            Put(image, DecoyJne, $"75 02 D8 0D {Le(decoyConstantVa)}");
            BitConverter.GetBytes(0.5625f).CopyTo(image, DecoyConstant);
            Put(image, Site, "75 3E 8B C7 85 C0 89 44 24 14 DB 44 24 14 7D 06 D8 05 20 93 A9 01 D8 0D D0 4E C5 01");
            return image;
        }
    }
}

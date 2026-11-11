using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Buffers.Binary;
using static MirrorsEdgeTweaks.Tests.TestSupport.SyntheticPe;

namespace MirrorsEdgeTweaks.Tests
{
    public class CaveSectionTests
    {
        // Every cave starts with its watermark and version tag.
        private const int CaveHeaderSize = 12;

        [Fact]
        public void NewCave_ReplacesTrailingCertificate()
        {
            // Steam's certificate is this size, so it extends past the cave's first page.
            byte[] signed = SyntheticPe.Build(certificateSize: 0x1558);

            var cave = CaveSection.Open(signed, versionTag: "steam");
            cave.Alloc(16);
            byte[] patched = cave.Finalize();

            var section = PeImageLayout.Parse(patched).FindSectionByName(".cave");
            Assert.NotNull(section);
            Assert.Equal((uint)SectionsEnd, section.PointerToRawData);
            Assert.Equal(section.PointerToRawData + section.SizeOfRawData, (uint)patched.Length);
            Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(patched.AsSpan(CertificateEntryOffset, 8)));
            Assert.Equal(-1, patched.AsSpan(SectionsEnd + CaveHeaderSize).IndexOfAnyExcept((byte)0));
        }

        [Fact]
        public void OpenSecondary_UsesTheCaveSection_WhenTheImageCanAddOne()
        {
            var cave = CaveSection.OpenSecondary(BuildFlat(), versionTag: "gog");
            uint va = cave.Alloc(16);
            byte[] patched = cave.Finalize();

            var section = PeImageLayout.Parse(patched).FindSectionByName(".cave")!;
            Assert.InRange(va, ImageBase + section.VirtualAddress, ImageBase + section.VirtualAddress + section.VirtualSize - 1);
        }

        [Fact]
        public void OpenSecondary_TakesTheRdataTail_AndMakesRdataExecutableAndWritable()
        {
            const int Tail = 0x400;
            byte[] stock = BuildFixedCave(256, rdataTailBytes: Tail);

            var cave = CaveSection.OpenSecondary((byte[])stock.Clone(), versionTag: "retail");
            uint va = cave.Alloc(16);
            byte[] patched = cave.Finalize();

            var pe = PeImageLayout.Parse(patched);
            var rdata = pe.FindSectionByName(".rdata")!;
            Assert.Equal(ImageBase + FlatRdataRva + FlatSectionSize - Tail + CaveHeaderSize, va);
            Assert.Equal(0xE0000040u, rdata.Characteristics);
            Assert.Equal(0x60000020u, pe.FindSectionByName(".text")!.Characteristics);
            Assert.Equal(CaveHeaderSize + 16, CaveSection.OpenSecondary(patched).CurrentWatermark);
        }

        [Fact]
        public void OpenSecondary_LeavesZeroBytesInsideTheVirtualSize_ToTheSection()
        {
            // The last 0x24 bytes inside the virtual size are zero, as real data can be.
            byte[] image = BuildFixedCave(256, rdataTailBytes: 0x400);
            int virtualEnd = (int)FlatRdataRva + FlatSectionSize - 0x400;
            image[virtualEnd - 0x25] = 0x7F;

            uint va = CaveSection.OpenSecondary(image, versionTag: "retail").Alloc(16);

            Assert.Equal(ImageBase + (uint)virtualEnd + CaveHeaderSize, va);
        }

        [Fact]
        public void Open_LeavesTheSectionFlags_UntilFinalize()
        {
            byte[] image = BuildFixedCave(256, rdataTailBytes: 0x400);
            byte[] stock = (byte[])image.Clone();

            _ = CaveSection.Open(image, versionTag: "retail").Capacity;
            _ = CaveSection.OpenSecondary(image, versionTag: "retail").Capacity;

            Assert.Equal(stock, image);
        }

        [Fact]
        public void ResetExistingCaves_EmptiesTheCaveSection_AndKeepsItsVersionTag()
        {
            var cave = CaveSection.Open(BuildFlat(), versionTag: "gog");
            uint lower = cave.Alloc(32);
            uint upper = cave.Alloc(32);
            cave.Write(lower, Enumerable.Repeat((byte)0xAB, 32).ToArray());
            cave.Write(upper, Enumerable.Repeat((byte)0xCD, 32).ToArray());
            byte[] patched = cave.Finalize();

            byte[] reset = CaveSection.ResetExistingCaves(patched)!;

            Assert.Equal(CaveHeaderSize, CaveSection.Open(reset).CurrentWatermark);
            Assert.Equal(-1, reset.AsSpan((int)(lower - ImageBase), 64).IndexOfAnyExcept((byte)0));
            Assert.Equal("gog", ExeVersionDetector.DetectVersion(reset));
        }

        [Fact]
        public void ResetExistingCaves_EmptiesBothPaddingCaves()
        {
            var primary = CaveSection.Open(BuildFixedCave(256, rdataTailBytes: 0x400), versionTag: "retail");
            primary.Write(primary.Alloc(32), Enumerable.Repeat((byte)0xAB, 32).ToArray());
            var secondary = CaveSection.OpenSecondary(primary.Finalize(), versionTag: "retail");
            secondary.Write(secondary.Alloc(32), Enumerable.Repeat((byte)0xCD, 32).ToArray());
            byte[] patched = secondary.Finalize();

            byte[] reset = CaveSection.ResetExistingCaves(patched)!;

            Assert.Equal(CaveHeaderSize, CaveSection.Open(reset).CurrentWatermark);
            Assert.Equal(CaveHeaderSize, CaveSection.OpenSecondary(reset).CurrentWatermark);
            Assert.Equal("retail", ExeVersionDetector.DetectVersion(reset));
        }

        [Fact]
        public void ResetExistingCaves_NeverAddsACave()
        {
            byte[] flat = BuildFlat();
            byte[] fixedCave = BuildFixedCave(256, rdataTailBytes: 0x400);
            byte[] stockFlat = (byte[])flat.Clone();
            byte[] stockFixedCave = (byte[])fixedCave.Clone();

            Assert.Null(CaveSection.ResetExistingCaves(flat));
            Assert.Null(CaveSection.ResetExistingCaves(fixedCave));
            Assert.Equal(stockFlat, flat);
            Assert.Equal(stockFixedCave, fixedCave);
        }

        [Fact]
        public void ResetExistingCaves_ReturnsNull_WhenTheCaveIsAlreadyEmpty()
        {
            var cave = CaveSection.Open(BuildFlat(), versionTag: "gog");
            uint va = cave.Alloc(16);
            byte[] reclaimed = CaveSection.ReclaimIfTopmost(cave.Finalize(), va, 16);

            Assert.Null(CaveSection.ResetExistingCaves(reclaimed));
        }

        [Fact]
        public void ReclaimIfTopmost_FreesABlockInTheRdataTail()
        {
            var cave = CaveSection.OpenSecondary(BuildFixedCave(256, rdataTailBytes: 0x400), versionTag: "retail");
            uint va = cave.Alloc(32);
            cave.Write(va, Enumerable.Repeat((byte)0xAB, 32).ToArray());
            byte[] patched = cave.Finalize();

            byte[] reclaimed = CaveSection.ReclaimIfTopmost(patched, va, 32);

            Assert.Equal(CaveHeaderSize, CaveSection.OpenSecondary(reclaimed).CurrentWatermark);
            Assert.Equal(-1, reclaimed.AsSpan((int)(va - ImageBase), 32).IndexOfAnyExcept((byte)0));
        }
    }
}

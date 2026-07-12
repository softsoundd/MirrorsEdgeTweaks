using MirrorsEdgeTweaks.Helpers;
using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Tests
{
    public class PeImageLayoutTests
    {
        private const uint ImageBase = 0x00400000;

        // Builds a minimal but structurally valid PE32 image:
        //   .text  VA 0x1000, raw 0x200 @ 0x200, executable
        //   .rsrc  VA 0x2000, raw 0x100 @ 0x400, read-only data (resource directory target)
        private static byte[] BuildMinimalPe32()
        {
            byte[] buffer = new byte[0x600];

            // DOS header
            buffer[0] = (byte)'M';
            buffer[1] = (byte)'Z';
            const int peOffset = 0x80;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x3C, 4), peOffset);

            // PE signature
            buffer[peOffset] = (byte)'P';
            buffer[peOffset + 1] = (byte)'E';

            // COFF header
            int coff = peOffset + 4;
            const ushort optHeaderSize = 0xE0; // standard PE32 optional header incl. 16 data dirs
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(coff + 2, 2), 2);   // NumberOfSections
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(coff + 16, 2), optHeaderSize);

            // Optional header (PE32)
            int opt = coff + 20;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(opt, 2), 0x10B);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 28, 4), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 32, 4), 0x1000); // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 36, 4), 0x200);  // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(opt + 60, 4), 0x200);  // SizeOfHeaders

            // Data directory entry 2 = resources
            int resourceEntry = opt + 96 + 2 * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(resourceEntry, 4), 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(resourceEntry + 4, 4), 0x100);

            // Section table
            int sectionTable = opt + optHeaderSize;
            WriteSection(buffer, sectionTable, ".text", virtualSize: 0x200, virtualAddress: 0x1000,
                rawSize: 0x200, rawOffset: 0x200, characteristics: 0x60000020); // code | execute | read
            WriteSection(buffer, sectionTable + 40, ".rsrc", virtualSize: 0x100, virtualAddress: 0x2000,
                rawSize: 0x100, rawOffset: 0x400, characteristics: 0x40000040); // initialized data | read

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

        [Fact]
        public void Parse_ReadsHeaderAndSections()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.Equal(ImageBase, pe.ImageBase);
            Assert.Equal(0x1000u, pe.SectionAlignment);
            Assert.Equal(0x200u, pe.FileAlignment);
            Assert.Equal(2, pe.Sections.Count);
            Assert.Equal(".text", pe.Sections[0].Name);
            Assert.Equal(".rsrc", pe.Sections[1].Name);
        }

        [Fact]
        public void Parse_ReadsResourceDataDirectory()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.Equal(0x2000u, pe.ResourceDirectoryRva);
            Assert.Equal(0x100u, pe.ResourceDirectorySize);
        }

        [Fact]
        public void Parse_RejectsNonPeFile()
        {
            Assert.Throws<InvalidOperationException>(() => PeImageLayout.Parse(new byte[] { 0x00, 0x01, 0x02 }));
        }

        [Fact]
        public void Parse_RejectsPe32Plus()
        {
            byte[] buffer = BuildMinimalPe32();
            int opt = 0x80 + 4 + 20;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(opt, 2), 0x20B);

            Assert.Throws<InvalidOperationException>(() => PeImageLayout.Parse(buffer));
        }

        [Fact]
        public void VaToOffset_MapsIntoSectionRawData()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.Equal(0x200, pe.VaToOffset(ImageBase + 0x1000));
            Assert.Equal(0x210, pe.VaToOffset(ImageBase + 0x1010));
            Assert.Equal(0x400, pe.VaToOffset(ImageBase + 0x2000));
        }

        [Fact]
        public void VaToOffset_ThrowsOutsideAnySection()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.Throws<InvalidOperationException>(() => pe.VaToOffset(ImageBase + 0x9000));
        }

        [Fact]
        public void RvaToOffset_MatchesVaToOffset()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.Equal(pe.VaToOffset(ImageBase + 0x2010), pe.RvaToOffset(0x2010));
        }

        [Fact]
        public void OffsetToVa_RoundTripsWithVaToOffset()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            uint va = ImageBase + 0x1042;
            Assert.Equal(va, pe.OffsetToVa(pe.VaToOffset(va)));
        }

        [Fact]
        public void TryOffsetToVa_ReturnsNullForHeaderArea()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.Null(pe.TryOffsetToVa(0x50));
            Assert.Equal(ImageBase + 0x1000, pe.TryOffsetToVa(0x200));
        }

        [Fact]
        public void IsExecutable_ReflectsSectionCharacteristics()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.True(pe.IsExecutableVa(ImageBase + 0x1000));
            Assert.False(pe.IsExecutableVa(ImageBase + 0x2000));
            Assert.True(pe.IsExecutableOffset(0x210));
            Assert.False(pe.IsExecutableOffset(0x410));
        }

        [Fact]
        public void FindSectionByName_FindsAndMisses()
        {
            var pe = PeImageLayout.Parse(BuildMinimalPe32());

            Assert.NotNull(pe.FindSectionByName(".text"));
            Assert.Null(pe.FindSectionByName(".cave"));
        }

        [Fact]
        public void ReadAndWriteAtVa_RoundTrip()
        {
            byte[] buffer = BuildMinimalPe32();
            var pe = PeImageLayout.Parse(buffer);

            pe.WriteAtVa(ImageBase + 0x1000, new byte[] { 0xDE, 0xAD });

            Assert.Equal(new byte[] { 0xDE, 0xAD }, pe.ReadAtVa(ImageBase + 0x1000, 2));
            Assert.Equal(0xDE, buffer[0x200]);
        }
    }
}

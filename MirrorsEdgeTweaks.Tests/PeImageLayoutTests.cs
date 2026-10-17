using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Buffers.Binary;
using static MirrorsEdgeTweaks.Tests.TestSupport.SyntheticPe;

namespace MirrorsEdgeTweaks.Tests
{
    public class PeImageLayoutTests
    {
        [Fact]
        public void Parse_ReadsHeaderAndSections()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.Equal(ImageBase, pe.ImageBase);
            Assert.Equal(0x1000u, pe.SectionAlignment);
            Assert.Equal(0x200u, pe.FileAlignment);
            Assert.Equal(2, pe.Sections.Count);
            Assert.Equal(".text", pe.Sections[0].Name);
            Assert.Equal(".rdata", pe.Sections[1].Name);
        }

        [Fact]
        public void Parse_ReadsResourceDataDirectory()
        {
            byte[] buffer = SyntheticPe.Build();
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ResourceEntryOffset, 4), RdataRva);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(ResourceEntryOffset + 4, 4), 0x100);

            var pe = PeImageLayout.Parse(buffer);

            Assert.Equal(RdataRva, pe.ResourceDirectoryRva);
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
            byte[] buffer = SyntheticPe.Build();
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(OptionalHeaderOffset, 2), 0x20B);

            Assert.Throws<InvalidOperationException>(() => PeImageLayout.Parse(buffer));
        }

        [Fact]
        public void VaToOffset_MapsIntoSectionRawData()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.Equal(TextRaw, pe.VaToOffset(ImageBase + TextRva));
            Assert.Equal(TextRaw + 0x10, pe.VaToOffset(ImageBase + TextRva + 0x10));
            Assert.Equal(RdataRaw, pe.VaToOffset(ImageBase + RdataRva));
        }

        [Fact]
        public void VaToOffset_ThrowsOutsideAnySection()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.Throws<InvalidOperationException>(() => pe.VaToOffset(ImageBase + 0x9000));
        }

        [Fact]
        public void RvaToOffset_MatchesVaToOffset()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.Equal(pe.VaToOffset(ImageBase + RdataRva + 0x10), pe.RvaToOffset(RdataRva + 0x10));
        }

        [Fact]
        public void OffsetToVa_RoundTripsWithVaToOffset()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            uint va = ImageBase + TextRva + 0x42;
            Assert.Equal(va, pe.OffsetToVa(pe.VaToOffset(va)));
        }

        [Fact]
        public void TryOffsetToVa_ReturnsNullForHeaderArea()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.Null(pe.TryOffsetToVa(0x50));
            Assert.Equal(ImageBase + TextRva, pe.TryOffsetToVa(TextRaw));
        }

        [Fact]
        public void IsExecutable_ReflectsSectionCharacteristics()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.True(pe.IsExecutableVa(ImageBase + TextRva));
            Assert.False(pe.IsExecutableVa(ImageBase + RdataRva));
            Assert.True(pe.IsExecutableOffset(TextRaw + 0x10));
            Assert.False(pe.IsExecutableOffset(RdataRaw + 0x10));
        }

        [Fact]
        public void FindSectionByName_FindsAndMisses()
        {
            var pe = PeImageLayout.Parse(SyntheticPe.Build());

            Assert.NotNull(pe.FindSectionByName(".text"));
            Assert.Null(pe.FindSectionByName(".cave"));
        }

        [Fact]
        public void ReadAndWriteAtVa_RoundTrip()
        {
            byte[] buffer = SyntheticPe.Build();
            var pe = PeImageLayout.Parse(buffer);

            pe.WriteAtVa(ImageBase + TextRva, new byte[] { 0xDE, 0xAD });

            Assert.Equal(new byte[] { 0xDE, 0xAD }, pe.ReadAtVa(ImageBase + TextRva, 2));
            Assert.Equal(0xDE, buffer[TextRaw]);
        }
    }
}

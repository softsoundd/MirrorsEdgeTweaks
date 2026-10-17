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
    }
}

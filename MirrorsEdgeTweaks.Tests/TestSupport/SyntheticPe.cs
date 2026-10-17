using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Builds minimal PE32 images with zero-filled .text and .rdata sections and header room for one more section.
    internal static class SyntheticPe
    {
        public const uint ImageBase = 0x00400000;
        public const uint TextRva = 0x1000;
        public const int TextRaw = 0x200;
        public const int TextSize = 0x400;
        public const uint RdataRva = 0x2000;
        public const int RdataRaw = TextRaw + TextSize;
        public const int RdataSize = 0x200;
        public const int SectionsEnd = RdataRaw + RdataSize;

        private const int PeOffset = 0x80;
        public const int OptionalHeaderOffset = PeOffset + 4 + 20;
        private const ushort OptionalHeaderSize = 0xE0;

        // The PE32 data directories start 96 bytes into the optional header, and each entry is 8 bytes.
        public const int ResourceEntryOffset = OptionalHeaderOffset + 96 + 2 * 8;
        public const int CertificateEntryOffset = OptionalHeaderOffset + 96 + 4 * 8;

        // A requested certificate follows .rdata, as signed builds carry theirs after the last section.
        public static byte[] Build(int certificateSize = 0)
        {
            byte[] image = new byte[SectionsEnd + certificateSize];

            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C, 4), PeOffset);
            image[PeOffset] = (byte)'P';
            image[PeOffset + 1] = (byte)'E';

            int coff = PeOffset + 4;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2, 2), 2); // NumberOfSections
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16, 2), OptionalHeaderSize);

            int opt = OptionalHeaderOffset;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt, 2), 0x10B);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 28, 4), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32, 4), 0x1000);  // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36, 4), 0x200);   // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56, 4), 0x3000);  // SizeOfImage
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60, 4), TextRaw); // SizeOfHeaders
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 92, 4), 16);      // NumberOfRvaAndSizes

            int sectionTable = opt + OptionalHeaderSize;
            WriteSection(image, sectionTable, ".text", TextRva, TextRaw, TextSize, 0x60000020);          // code | execute | read
            WriteSection(image, sectionTable + 40, ".rdata", RdataRva, RdataRaw, RdataSize, 0x40000040); // initialized data | read

            if (certificateSize > 0)
            {
                image.AsSpan(SectionsEnd).Fill(0xA5);
                BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(CertificateEntryOffset, 4), SectionsEnd);
                BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(CertificateEntryOffset + 4, 4), certificateSize);
            }

            return image;
        }

        public const uint FlatTextRva = 0x1000, FlatRdataRva = 0x2000, FlatDataRva = 0x3000;
        public const int FlatSectionSize = 0x1000;

        // Builds a PE32 image whose .text, .rdata and .data sit at file offsets equal to their RVAs,
        // as the game executables do, so a VA maps to its file offset as va - ImageBase.
        // It has header room for one more section.
        public static byte[] BuildFlat()
        {
            const int alignment = 0x1000;
            byte[] image = new byte[FlatDataRva + FlatSectionSize];

            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C, 4), PeOffset);
            image[PeOffset] = (byte)'P';
            image[PeOffset + 1] = (byte)'E';

            int coff = PeOffset + 4;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2, 2), 3); // NumberOfSections
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16, 2), OptionalHeaderSize);

            int opt = OptionalHeaderOffset;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt, 2), 0x10B);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 28, 4), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32, 4), alignment); // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36, 4), alignment); // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56, 4), FlatDataRva + FlatSectionSize); // SizeOfImage
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60, 4), FlatTextRva); // SizeOfHeaders
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 92, 4), 16);          // NumberOfRvaAndSizes

            int sectionTable = opt + OptionalHeaderSize;
            WriteSection(image, sectionTable, ".text", FlatTextRva, (int)FlatTextRva, FlatSectionSize, 0x60000020);
            WriteSection(image, sectionTable + 40, ".rdata", FlatRdataRva, (int)FlatRdataRva, FlatSectionSize, 0x40000040);
            WriteSection(image, sectionTable + 80, ".data", FlatDataRva, (int)FlatDataRva, FlatSectionSize, 0xC0000040);
            return image;
        }

        // The cave starts here when CaveSection falls back to .text padding, which is the 16-byte boundary
        // after the logging hook sites, whose last bytes sit just below it.
        public const int FixedCaveStart = 0x1420;

        // Builds a flat PE32 image like BuildFlat, but with no header room for another section, so
        // CaveSection falls back to .text padding as it does on the SecuROM game executables.
        // The .text section is sized so its trailing NUL run forms a cave of about caveBytes, rounded up to
        // keep the section end 16-aligned, as real section sizes are, so the cave header stays findable.
        // The caller must keep the hook sites below FixedCaveStart, where the cave then begins.
        public static byte[] BuildFixedCave(int caveBytes)
        {
            const int alignment = 0x1000;
            int textRawSize = (FixedCaveStart - (int)FlatTextRva) + caveBytes;
            textRawSize = (textRawSize + 15) & ~15;
            byte[] image = new byte[FlatDataRva + FlatSectionSize];

            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C, 4), PeOffset);
            image[PeOffset] = (byte)'P';
            image[PeOffset + 1] = (byte)'E';

            int coff = PeOffset + 4;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2, 2), 3); // NumberOfSections
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16, 2), OptionalHeaderSize);

            int opt = OptionalHeaderOffset;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt, 2), 0x10B);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 28, 4), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32, 4), alignment); // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36, 4), alignment); // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56, 4), FlatDataRva + FlatSectionSize); // SizeOfImage
            // Too small to hold another 40-byte section header, so CaveSection uses .text padding.
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60, 4), 0x200); // SizeOfHeaders
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 92, 4), 16);     // NumberOfRvaAndSizes

            int sectionTable = opt + OptionalHeaderSize;
            WriteSection(image, sectionTable, ".text", FlatTextRva, (int)FlatTextRva, textRawSize, 0x60000020);
            WriteSection(image, sectionTable + 40, ".rdata", FlatRdataRva, (int)FlatRdataRva, FlatSectionSize, 0x40000040);
            WriteSection(image, sectionTable + 80, ".data", FlatDataRva, (int)FlatDataRva, FlatSectionSize, 0xC0000040);
            return image;
        }

        // Writes hex bytes such as "8B 06 FF" at the offset.
        public static void Put(byte[] image, int offset, string hex) =>
            Convert.FromHexString(hex.Replace(" ", "")).CopyTo(image, offset);

        // Returns the value's little-endian bytes as hex, for splicing addresses into Put patterns.
        public static string Le(uint value) => Convert.ToHexString(BitConverter.GetBytes(value));

        private static void WriteSection(byte[] image, int header, string name, uint rva, int raw, int size,
            uint characteristics)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(image, header);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 8, 4), size);  // VirtualSize
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 12, 4), rva);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 16, 4), size); // SizeOfRawData
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 20, 4), raw);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 36, 4), characteristics);
        }
    }
}

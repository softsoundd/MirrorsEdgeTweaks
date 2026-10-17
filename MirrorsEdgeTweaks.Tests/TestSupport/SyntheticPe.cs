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

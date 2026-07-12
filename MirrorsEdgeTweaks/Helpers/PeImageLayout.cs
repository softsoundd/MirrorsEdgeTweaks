using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class PeImageLayout
    {
        private const uint ExecuteSectionFlag = 0x20000000;
        private const int ResourceDirectoryIndex = 2;

        private readonly byte[] _buffer;

        public uint ImageBase { get; }
        public uint SectionAlignment { get; }
        public uint FileAlignment { get; }
        public uint SizeOfHeaders { get; }
        public IReadOnlyList<SectionEntry> Sections { get; }

        // Resource data-directory entry (RVA + size); both zero when the image has no resources.
        public uint ResourceDirectoryRva { get; }
        public uint ResourceDirectorySize { get; }

        internal int PeOffset { get; }
        internal int CoffOffset { get; }
        internal int OptionalHeaderOffset { get; }
        internal int SectionTableOffset { get; }

        private PeImageLayout(byte[] buffer, uint imageBase, uint sectionAlignment,
            uint fileAlignment, uint sizeOfHeaders,
            List<SectionEntry> sections, int peOffset, int coffOffset,
            int optionalHeaderOffset, int sectionTableOffset,
            uint resourceDirectoryRva, uint resourceDirectorySize)
        {
            _buffer = buffer;
            ImageBase = imageBase;
            SectionAlignment = sectionAlignment;
            FileAlignment = fileAlignment;
            SizeOfHeaders = sizeOfHeaders;
            Sections = sections;
            PeOffset = peOffset;
            CoffOffset = coffOffset;
            OptionalHeaderOffset = optionalHeaderOffset;
            SectionTableOffset = sectionTableOffset;
            ResourceDirectoryRva = resourceDirectoryRva;
            ResourceDirectorySize = resourceDirectorySize;
        }

        public static PeImageLayout Parse(byte[] buffer)
        {
            if (buffer.Length < 0x40 || buffer[0] != (byte)'M' || buffer[1] != (byte)'Z')
                throw new InvalidOperationException("Not a valid PE file.");

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0x3C, 4));
            if (peOffset < 0 || peOffset + 24 > buffer.Length)
                throw new InvalidOperationException("Invalid PE header offset.");

            if (buffer[peOffset] != 0x50 || buffer[peOffset + 1] != 0x45 ||
                buffer[peOffset + 2] != 0x00 || buffer[peOffset + 3] != 0x00)
                throw new InvalidOperationException("Invalid PE signature.");

            int coffOffset = peOffset + 4;
            ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(coffOffset + 2, 2));
            ushort optHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(coffOffset + 16, 2));
            int optOffset = coffOffset + 20;

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(optOffset, 2));
            if (magic != 0x10B)
                throw new InvalidOperationException($"Only PE32 (0x10B) supported, got 0x{magic:X4}.");

            uint imageBase = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(optOffset + 28, 4));
            uint sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(optOffset + 32, 4));
            uint fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(optOffset + 36, 4));
            uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(optOffset + 60, 4));

            // PE32 data directories start at optional-header offset 96; entry 2 is the resources.
            uint resourceDirectoryRva = 0;
            uint resourceDirectorySize = 0;
            int resourceEntryOffset = optOffset + 96 + ResourceDirectoryIndex * 8;
            if (resourceEntryOffset + 8 <= optOffset + optHeaderSize && resourceEntryOffset + 8 <= buffer.Length)
            {
                resourceDirectoryRva = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(resourceEntryOffset, 4));
                resourceDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(resourceEntryOffset + 4, 4));
            }

            int sectionTableOffset = optOffset + optHeaderSize;
            var sections = new List<SectionEntry>(sectionCount);
            for (int i = 0; i < sectionCount; i++)
            {
                int hdr = sectionTableOffset + i * 40;
                string name = Encoding.ASCII.GetString(buffer, hdr, 8).TrimEnd('\0');
                sections.Add(new SectionEntry(
                    name,
                    hdr,
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(hdr + 8, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(hdr + 12, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(hdr + 16, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(hdr + 20, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(hdr + 36, 4))));
            }

            return new PeImageLayout(buffer, imageBase, sectionAlignment, fileAlignment,
                sizeOfHeaders, sections, peOffset, coffOffset, optOffset, sectionTableOffset,
                resourceDirectoryRva, resourceDirectorySize);
        }

        public int VaToOffset(uint va)
        {
            return RvaToOffset(checked(va - ImageBase));
        }

        public int RvaToOffset(uint rva)
        {
            foreach (var section in Sections)
            {
                uint size = Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                    return checked((int)(section.PointerToRawData + (rva - section.VirtualAddress)));
            }
            throw new InvalidOperationException($"RVA 0x{rva:X} does not fall in any PE section.");
        }

        public uint OffsetToVa(int offset)
        {
            uint uoff = (uint)offset;
            foreach (var section in Sections)
            {
                if (uoff >= section.PointerToRawData &&
                    uoff < section.PointerToRawData + section.SizeOfRawData)
                {
                    uint rva = section.VirtualAddress + (uoff - section.PointerToRawData);
                    return ImageBase + rva;
                }
            }
            throw new InvalidOperationException($"File offset 0x{offset:X} does not fall in any PE section.");
        }

        public SectionEntry? FindSectionByName(string name)
        {
            foreach (var s in Sections)
            {
                if (s.Name.Equals(name, StringComparison.Ordinal))
                    return s;
            }
            return null;
        }

        // True when the VA lies inside a section marked executable (IMAGE_SCN_MEM_EXECUTE).
        public bool IsExecutableVa(uint va)
        {
            uint rva = checked(va - ImageBase);
            foreach (var section in Sections)
            {
                uint size = Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                    return (section.Characteristics & ExecuteSectionFlag) != 0;
            }
            return false;
        }

        public bool IsExecutableOffset(int offset)
        {
            uint uoff = (uint)offset;
            foreach (var section in Sections)
            {
                if (section.SizeOfRawData == 0)
                    continue;
                if (uoff >= section.PointerToRawData &&
                    uoff < section.PointerToRawData + section.SizeOfRawData)
                    return (section.Characteristics & ExecuteSectionFlag) != 0;
            }
            return false;
        }

        // Maps a raw file offset back to a VA, or null when the offset is not inside any
        // section's raw data (e.g. headers or appended data).
        public uint? TryOffsetToVa(int offset)
        {
            uint uoff = (uint)offset;
            foreach (var section in Sections)
            {
                if (section.SizeOfRawData == 0)
                    continue;
                if (uoff >= section.PointerToRawData &&
                    uoff < section.PointerToRawData + section.SizeOfRawData)
                {
                    uint rva = section.VirtualAddress + (uoff - section.PointerToRawData);
                    return ImageBase + rva;
                }
            }
            return null;
        }

        public byte[] ReadAtVa(uint va, int length)
        {
            int offset = VaToOffset(va);
            if (offset < 0 || offset + length > _buffer.Length)
                throw new InvalidOperationException($"VA 0x{va:X8} read of {length} bytes is out of bounds.");
            byte[] data = new byte[length];
            Buffer.BlockCopy(_buffer, offset, data, 0, length);
            return data;
        }

        public void WriteAtVa(uint va, byte[] payload)
        {
            int offset = VaToOffset(va);
            if (offset < 0 || offset + payload.Length > _buffer.Length)
                throw new InvalidOperationException($"VA 0x{va:X8} write of {payload.Length} bytes is out of bounds.");
            Buffer.BlockCopy(payload, 0, _buffer, offset, payload.Length);
        }

        public sealed class SectionEntry
        {
            public string Name { get; }
            public int HeaderOffset { get; }
            public uint VirtualSize { get; }
            public uint VirtualAddress { get; }
            public uint SizeOfRawData { get; }
            public uint PointerToRawData { get; }
            public uint Characteristics { get; }

            public SectionEntry(string name, int headerOffset, uint virtualSize,
                uint virtualAddress, uint sizeOfRawData, uint pointerToRawData,
                uint characteristics)
            {
                Name = name;
                HeaderOffset = headerOffset;
                VirtualSize = virtualSize;
                VirtualAddress = virtualAddress;
                SizeOfRawData = sizeOfRawData;
                PointerToRawData = pointerToRawData;
                Characteristics = characteristics;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum ConfigUnlockState
    {
        NotApplicable,
        Unpatched,
        Patched,
        Mixed
    }

    public static class ConfigUnlockHelper
    {
        private const int ResourceDirectoryIndex = 2;
        private const int ResourceTypeRcData = 10;
        private const int HashResourceId = 1010;
        private const int Sha1HashSize = 20;
        private const byte PatchedNameMask = 0x80;

        public static ConfigUnlockState GetState(string exePath)
        {
            byte[] buffer = File.ReadAllBytes(exePath);
            if (!TryReadHashResource(buffer, out HashResourceLayout layout))
            {
                return ConfigUnlockState.NotApplicable;
            }

            return GetState(buffer, layout);
        }

        public static bool Unlock(string exePath)
        {
            return PatchExecutable(exePath, unlock: true);
        }

        public static bool RestoreStock(string exePath)
        {
            return PatchExecutable(exePath, unlock: false);
        }

        private static bool PatchExecutable(string exePath, bool unlock)
        {
            byte[] originalBuffer = File.ReadAllBytes(exePath);
            byte[] buffer = (byte[])originalBuffer.Clone();

            if (!TryReadHashResource(buffer, out HashResourceLayout layout))
            {
                throw new InvalidOperationException("The selected executable does not contain the signed config hash table.");
            }

            int changedCount = ApplyConfigHashPatch(buffer, layout, unlock);
            if (changedCount == 0)
            {
                return false;
            }

            WriteAllBytesPreservingAttributes(exePath, buffer);
            return true;
        }

        private static ConfigUnlockState GetState(byte[] buffer, HashResourceLayout layout)
        {
            int configEntryCount = 0;
            int patchedEntryCount = 0;

            foreach (HashEntry entry in EnumerateHashEntries(buffer, layout))
            {
                if (!entry.IsConfigEntry)
                {
                    continue;
                }

                configEntryCount++;
                if (entry.IsPatched)
                {
                    patchedEntryCount++;
                }
            }

            if (configEntryCount == 0)
            {
                return ConfigUnlockState.NotApplicable;
            }

            if (patchedEntryCount == 0)
            {
                return ConfigUnlockState.Unpatched;
            }

            if (patchedEntryCount == configEntryCount)
            {
                return ConfigUnlockState.Patched;
            }

            return ConfigUnlockState.Mixed;
        }

        private static int ApplyConfigHashPatch(byte[] buffer, HashResourceLayout layout, bool unlock)
        {
            int configEntryCount = 0;
            int changedCount = 0;

            foreach (HashEntry entry in EnumerateHashEntries(buffer, layout))
            {
                if (!entry.IsConfigEntry)
                {
                    continue;
                }

                configEntryCount++;
                if (entry.IsPatched == unlock)
                {
                    continue;
                }

                // Signed config lookups key off the filename table, so hiding the
                // first byte is enough to disable the hash entry without reshaping
                // the resource data.
                buffer[entry.NameOffset] ^= PatchedNameMask;
                changedCount++;
            }

            if (configEntryCount == 0)
            {
                throw new InvalidOperationException("The executable's hash table does not contain any signed config entries.");
            }

            return changedCount;
        }

        private static IEnumerable<HashEntry> EnumerateHashEntries(byte[] buffer, HashResourceLayout layout)
        {
            int offset = layout.DataOffset;
            int endOffset = checked(layout.DataOffset + layout.DataSize);

            while (offset < endOffset)
            {
                int nameOffset = offset;
                while (offset < endOffset && buffer[offset] != 0)
                {
                    offset++;
                }

                if (offset >= endOffset)
                {
                    throw new InvalidDataException("The executable's hash table is truncated.");
                }

                int nameLength = offset - nameOffset;
                if (nameLength == 0)
                {
                    throw new InvalidDataException("The executable's hash table contains an empty filename entry.");
                }

                offset++;
                int hashOffsetEnd = checked(offset + Sha1HashSize);
                if (hashOffsetEnd > endOffset)
                {
                    throw new InvalidDataException("The executable's hash table is truncated.");
                }

                bool isConfigEntry = HasIniExtension(buffer, nameOffset, nameLength);
                bool isPatched = isConfigEntry && (buffer[nameOffset] & PatchedNameMask) != 0;

                yield return new HashEntry(nameOffset, isConfigEntry, isPatched);
                offset = hashOffsetEnd;
            }

            if (offset != endOffset)
            {
                throw new InvalidDataException("The executable's hash table has an invalid size.");
            }
        }

        private static bool HasIniExtension(byte[] buffer, int offset, int length)
        {
            return
                length >= 4 &&
                buffer[offset + length - 4] == (byte)'.' &&
                ToLowerAscii(buffer[offset + length - 3]) == (byte)'i' &&
                ToLowerAscii(buffer[offset + length - 2]) == (byte)'n' &&
                ToLowerAscii(buffer[offset + length - 1]) == (byte)'i';
        }

        private static byte ToLowerAscii(byte value)
        {
            return value is >= (byte)'A' and <= (byte)'Z'
                ? (byte)(value + 0x20)
                : value;
        }

        private static bool TryReadHashResource(byte[] buffer, out HashResourceLayout layout)
        {
            ExecutableImageLayout image = ExecutableImageLayout.Parse(buffer);
            if (!image.TryGetResourceDirectory(out ResourceDirectoryLocation resourceDirectory))
            {
                layout = default;
                return false;
            }

            if (!TryFindResourceData(buffer, resourceDirectory, HashResourceId, out ResourceDataEntry dataEntry))
            {
                layout = default;
                return false;
            }

            if (dataEntry.Size <= 1)
            {
                layout = default;
                return false;
            }

            int dataOffset = image.RvaToOffset(dataEntry.DataRva);
            int dataSize = checked((int)dataEntry.Size);
            if (dataOffset < 0 || checked((long)dataOffset + dataSize) > buffer.Length)
            {
                throw new InvalidDataException("The executable's hash resource points outside the file.");
            }

            layout = new HashResourceLayout(dataOffset, dataSize);
            return true;
        }

        private static bool TryFindResourceData(byte[] buffer, ResourceDirectoryLocation resourceDirectory, int resourceId, out ResourceDataEntry dataEntry)
        {
            if (!TryFindResourceSubdirectory(buffer, resourceDirectory, resourceDirectory.DirectoryOffset, ResourceTypeRcData, out int typeDirectoryOffset))
            {
                dataEntry = default;
                return false;
            }

            if (!TryFindResourceSubdirectory(buffer, resourceDirectory, typeDirectoryOffset, resourceId, out int nameDirectoryOffset))
            {
                dataEntry = default;
                return false;
            }

            if (!TryFindFirstResourceDataEntry(buffer, resourceDirectory, nameDirectoryOffset, out dataEntry))
            {
                dataEntry = default;
                return false;
            }

            return true;
        }

        private static bool TryFindResourceSubdirectory(byte[] buffer, ResourceDirectoryLocation resourceDirectory, int directoryOffset, int targetId, out int childDirectoryOffset)
        {
            foreach (ResourceDirectoryEntry entry in EnumerateResourceDirectoryEntries(buffer, directoryOffset))
            {
                if (entry.IsNamed || entry.Id != targetId || !entry.IsDirectory)
                {
                    continue;
                }

                childDirectoryOffset = resourceDirectory.DirectoryOffset + entry.RelativeOffset;
                return true;
            }

            childDirectoryOffset = 0;
            return false;
        }

        private static bool TryFindFirstResourceDataEntry(byte[] buffer, ResourceDirectoryLocation resourceDirectory, int directoryOffset, out ResourceDataEntry dataEntry)
        {
            foreach (ResourceDirectoryEntry entry in EnumerateResourceDirectoryEntries(buffer, directoryOffset))
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                int dataEntryOffset = resourceDirectory.DirectoryOffset + entry.RelativeOffset;
                dataEntry = new ResourceDataEntry(
                    ReadUInt32(buffer, dataEntryOffset),
                    ReadUInt32(buffer, checked(dataEntryOffset + 4)));
                return true;
            }

            dataEntry = default;
            return false;
        }

        private static IEnumerable<ResourceDirectoryEntry> EnumerateResourceDirectoryEntries(byte[] buffer, int directoryOffset)
        {
            const int resourceDirectoryHeaderSize = 16;
            const int resourceDirectoryEntrySize = 8;

            ushort namedEntryCount = ReadUInt16(buffer, checked(directoryOffset + 12));
            ushort idEntryCount = ReadUInt16(buffer, checked(directoryOffset + 14));
            int entryCount = checked(namedEntryCount + idEntryCount);
            int entriesOffset = checked(directoryOffset + resourceDirectoryHeaderSize);

            for (int index = 0; index < entryCount; index++)
            {
                int entryOffset = checked(entriesOffset + (index * resourceDirectoryEntrySize));
                uint nameOrId = ReadUInt32(buffer, entryOffset);
                uint offsetToData = ReadUInt32(buffer, checked(entryOffset + 4));

                yield return new ResourceDirectoryEntry(
                    checked((int)(nameOrId & 0xFFFF)),
                    (nameOrId & 0x80000000) != 0,
                    checked((int)(offsetToData & 0x7FFFFFFF)),
                    (offsetToData & 0x80000000) != 0);
            }
        }

        private static void WriteAllBytesPreservingAttributes(string path, byte[] content)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            if (wasReadOnly)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            try
            {
                File.WriteAllBytes(path, content);
            }
            finally
            {
                if (wasReadOnly)
                {
                    File.SetAttributes(path, attributes);
                }
            }
        }

        private static ReadOnlySpan<byte> ReadSpan(byte[] buffer, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > buffer.Length)
            {
                throw new InvalidDataException("The executable appears to be truncated or invalid.");
            }

            return buffer.AsSpan(offset, length);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            ReadOnlySpan<byte> span = ReadSpan(buffer, offset, sizeof(ushort));
            return (ushort)(span[0] | (span[1] << 8));
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            ReadOnlySpan<byte> span = ReadSpan(buffer, offset, sizeof(uint));
            return (uint)(span[0] | (span[1] << 8) | (span[2] << 16) | (span[3] << 24));
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ReadOnlySpan<byte> span = ReadSpan(buffer, offset, sizeof(ulong));
            return
                span[0] |
                ((ulong)span[1] << 8) |
                ((ulong)span[2] << 16) |
                ((ulong)span[3] << 24) |
                ((ulong)span[4] << 32) |
                ((ulong)span[5] << 40) |
                ((ulong)span[6] << 48) |
                ((ulong)span[7] << 56);
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return unchecked((int)ReadUInt32(buffer, offset));
        }

        private readonly struct HashResourceLayout
        {
            public HashResourceLayout(int dataOffset, int dataSize)
            {
                DataOffset = dataOffset;
                DataSize = dataSize;
            }

            public int DataOffset { get; }
            public int DataSize { get; }
        }

        private readonly struct HashEntry
        {
            public HashEntry(int nameOffset, bool isConfigEntry, bool isPatched)
            {
                NameOffset = nameOffset;
                IsConfigEntry = isConfigEntry;
                IsPatched = isPatched;
            }

            public int NameOffset { get; }
            public bool IsConfigEntry { get; }
            public bool IsPatched { get; }
        }

        private readonly struct ResourceDirectoryLocation
        {
            public ResourceDirectoryLocation(int directoryOffset)
            {
                DirectoryOffset = directoryOffset;
            }

            public int DirectoryOffset { get; }
        }

        private readonly struct ResourceDirectoryEntry
        {
            public ResourceDirectoryEntry(int id, bool isNamed, int relativeOffset, bool isDirectory)
            {
                Id = id;
                IsNamed = isNamed;
                RelativeOffset = relativeOffset;
                IsDirectory = isDirectory;
            }

            public int Id { get; }
            public bool IsNamed { get; }
            public int RelativeOffset { get; }
            public bool IsDirectory { get; }
        }

        private readonly struct ResourceDataEntry
        {
            public ResourceDataEntry(uint dataRva, uint size)
            {
                DataRva = dataRva;
                Size = size;
            }

            public uint DataRva { get; }
            public uint Size { get; }
        }

        private sealed class ExecutableImageLayout
        {
            private readonly List<SectionInfo> _sections;

            private ExecutableImageLayout(List<SectionInfo> sections, uint resourceDirectoryRva, uint resourceDirectorySize)
            {
                _sections = sections;
                ResourceDirectoryRva = resourceDirectoryRva;
                ResourceDirectorySize = resourceDirectorySize;
            }

            public uint ResourceDirectoryRva { get; }
            public uint ResourceDirectorySize { get; }

            public static ExecutableImageLayout Parse(byte[] buffer)
            {
                const int peSignatureOffset = 0x3C;
                const int peHeaderSize = 24;
                const int pe32DataDirectoryOffset = 96;
                const int pe64DataDirectoryOffset = 112;
                const int imageDirectoryEntrySize = 8;

                if (buffer.Length < 0x40 || buffer[0] != 'M' || buffer[1] != 'Z')
                {
                    throw new InvalidDataException("The selected file is not a valid PE executable.");
                }

                int peHeaderOffset = ReadInt32(buffer, peSignatureOffset);
                if (peHeaderOffset < 0 || checked(peHeaderOffset + peHeaderSize) > buffer.Length)
                {
                    throw new InvalidDataException("The selected executable has an invalid PE header.");
                }

                if (!ReadSpan(buffer, peHeaderOffset, 4).SequenceEqual(new byte[] { 0x50, 0x45, 0x00, 0x00 }))
                {
                    throw new InvalidDataException("The selected executable has an invalid PE signature.");
                }

                ushort sectionCount = ReadUInt16(buffer, checked(peHeaderOffset + 6));
                ushort optionalHeaderSize = ReadUInt16(buffer, checked(peHeaderOffset + 20));
                int optionalHeaderOffset = checked(peHeaderOffset + peHeaderSize);
                int optionalHeaderEnd = checked(optionalHeaderOffset + optionalHeaderSize);
                if (optionalHeaderEnd > buffer.Length)
                {
                    throw new InvalidDataException("The selected executable has an incomplete optional header.");
                }

                ushort optionalHeaderMagic = ReadUInt16(buffer, optionalHeaderOffset);
                int dataDirectoryOffset = optionalHeaderMagic switch
                {
                    0x10B => checked(optionalHeaderOffset + pe32DataDirectoryOffset),
                    0x20B => checked(optionalHeaderOffset + pe64DataDirectoryOffset),
                    _ => throw new InvalidDataException("Unsupported PE optional header format.")
                };

                int requiredDataDirectoryBytes = checked((ResourceDirectoryIndex + 1) * imageDirectoryEntrySize);
                if (checked(dataDirectoryOffset + requiredDataDirectoryBytes) > optionalHeaderEnd)
                {
                    throw new InvalidDataException("The selected executable does not expose the resource directory entry.");
                }

                uint resourceDirectoryRva = ReadUInt32(buffer, checked(dataDirectoryOffset + (ResourceDirectoryIndex * imageDirectoryEntrySize)));
                uint resourceDirectorySize = ReadUInt32(buffer, checked(dataDirectoryOffset + (ResourceDirectoryIndex * imageDirectoryEntrySize) + 4));

                int sectionTableOffset = checked(optionalHeaderOffset + optionalHeaderSize);
                int requiredSectionBytes = checked(sectionCount * 40);
                if (checked(sectionTableOffset + requiredSectionBytes) > buffer.Length)
                {
                    throw new InvalidDataException("The executable section table is incomplete.");
                }

                List<SectionInfo> sections = new List<SectionInfo>(sectionCount);
                for (int index = 0; index < sectionCount; index++)
                {
                    int sectionOffset = checked(sectionTableOffset + (index * 40));
                    sections.Add(new SectionInfo(
                        ReadUInt32(buffer, checked(sectionOffset + 12)),
                        ReadUInt32(buffer, checked(sectionOffset + 8)),
                        ReadUInt32(buffer, checked(sectionOffset + 20)),
                        ReadUInt32(buffer, checked(sectionOffset + 16))));
                }

                return new ExecutableImageLayout(sections, resourceDirectoryRva, resourceDirectorySize);
            }

            public bool TryGetResourceDirectory(out ResourceDirectoryLocation directory)
            {
                if (ResourceDirectoryRva == 0 || ResourceDirectorySize == 0)
                {
                    directory = default;
                    return false;
                }

                directory = new ResourceDirectoryLocation(RvaToOffset(ResourceDirectoryRva));
                return true;
            }

            public int RvaToOffset(uint rva)
            {
                SectionInfo section = FindSectionByRva(rva);
                return checked((int)(section.PointerToRawData + (rva - section.VirtualAddress)));
            }

            private SectionInfo FindSectionByRva(uint rva)
            {
                foreach (SectionInfo section in _sections)
                {
                    uint size = Math.Max(section.VirtualSize, section.SizeOfRawData);
                    uint start = section.VirtualAddress;
                    uint end = start + size;
                    if (rva >= start && rva < end)
                    {
                        return section;
                    }
                }

                throw new InvalidDataException($"Could not map RVA 0x{rva:X} into a PE section.");
            }
        }

        private readonly struct SectionInfo
        {
            public SectionInfo(uint virtualAddress, uint virtualSize, uint pointerToRawData, uint sizeOfRawData)
            {
                VirtualAddress = virtualAddress;
                VirtualSize = virtualSize;
                PointerToRawData = pointerToRawData;
                SizeOfRawData = sizeOfRawData;
            }

            public uint VirtualAddress { get; }
            public uint VirtualSize { get; }
            public uint PointerToRawData { get; }
            public uint SizeOfRawData { get; }
        }
    }
}

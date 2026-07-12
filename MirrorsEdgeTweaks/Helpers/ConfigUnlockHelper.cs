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
            PeImageLayout image = PeImageLayout.Parse(buffer);
            if (image.ResourceDirectoryRva == 0 || image.ResourceDirectorySize == 0)
            {
                layout = default;
                return false;
            }

            var resourceDirectory = new ResourceDirectoryLocation(image.RvaToOffset(image.ResourceDirectoryRva));

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
            => PatchUtility.WritePreservingAttributes(path, content);

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

    }
}

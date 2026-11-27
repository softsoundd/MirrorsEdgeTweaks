using System.IO.Compression;

namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Builds packages with Mirror's Edge's UE3 version (536, licensee 43) whose body is a single zlib-compressed chunk.
    internal static class SyntheticPackage
    {
        private const uint PackageTag = 0x9E2A83C1;
        private const int PackageFlags = 0x00080009;
        private const int StoreCompressedFlag = 0x02000000;
        private const int ZlibCompression = 0x01;
        private const int ChunkEntrySize = 16;

        // Returns the compressed package, and the file that decompressing it produces.
        public static (byte[] Compressed, byte[] Decompressed) Build(byte[] body)
        {
            byte[] summary = BuildSummary(PackageFlags, compressionFlags: 0, chunk: null);
            byte[] chunk = BuildChunk(body);
            byte[] compressedSummary = BuildSummary(PackageFlags | StoreCompressedFlag, ZlibCompression,
                (summary.Length, body.Length, summary.Length + ChunkEntrySize, chunk.Length));
            return ([.. compressedSummary, .. chunk], [.. summary, .. body]);
        }

        private static byte[] BuildSummary(int packageFlags, int compressionFlags,
            (int UncompressedOffset, int UncompressedSize, int CompressedOffset, int CompressedSize)? chunk)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(PackageTag);
            writer.Write((ushort)536);
            writer.Write((ushort)43);
            writer.Write(0);                // HeadersSize
            writer.Write(5);                // FolderName
            writer.Write("None\0"u8);
            writer.Write(packageFlags);
            writer.Write(0);                // NameCount
            writer.Write(0);                // NameOffset
            writer.Write(0);                // ExportCount
            writer.Write(0);                // ExportOffset
            writer.Write(0);                // ImportCount
            writer.Write(0);                // ImportOffset
            writer.Write(0);                // DependsOffset
            writer.Write(new byte[16]);     // Guid
            writer.Write(1);                // GenerationCount
            writer.Write(0);                // The generation's ExportCount
            writer.Write(0);                // The generation's NameCount
            writer.Write(0);                // The generation's NetObjectCount
            writer.Write(0);                // EngineVersion
            writer.Write(0);                // CookerVersion
            writer.Write(compressionFlags);
            writer.Write(chunk == null ? 0 : 1);
            if (chunk is { } entry)
            {
                writer.Write(entry.UncompressedOffset);
                writer.Write(entry.UncompressedSize);
                writer.Write(entry.CompressedOffset);
                writer.Write(entry.CompressedSize);
            }
            writer.Write(0);                // PackageSource
            writer.Write(0);                // AdditionalPackagesToCook
            writer.Flush();
            return stream.ToArray();
        }

        // A chunk starts with the package tag, the block size and the chunk's two totals, followed by each block's two sizes.
        private static byte[] BuildChunk(byte[] body)
        {
            byte[] block = Compress(body);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(PackageTag);
            writer.Write(0x20000);
            writer.Write(block.Length);
            writer.Write(body.Length);
            writer.Write(block.Length);
            writer.Write(body.Length);
            writer.Write(block);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            {
                zlib.Write(data);
            }
            return output.ToArray();
        }
    }
}

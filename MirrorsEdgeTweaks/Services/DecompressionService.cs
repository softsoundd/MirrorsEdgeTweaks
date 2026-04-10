using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SharpLzo;

namespace MirrorsEdgeTweaks.Services
{
    public interface IDecompressionService
    {
        void RunDecompressor(string packagePath);
    }

    public class DecompressionService : IDecompressionService
    {
        private const uint PackageFileTag = 0x9E2A83C1;
        private const uint PackageFileTagReverse = 0xC1832A9E;
        private const uint FullyCompressedMarkerA = PackageFileTag;
        private const uint FullyCompressedMarkerB = 0x00020000;
        private const uint FullyCompressedMarkerC = 0x00010000;

        private const int PkgStoreCompressed = 0x02000000;

        private const int CompressZlib = 0x01;
        private const int CompressLzo = 0x02;
        private const int CompressLzx = 0x04;

        private const int CompressedChunkSizeBytes = 16;
        private const int DefaultCopyBufferSize = 64 * 1024;

        private readonly IFileService _fileService;

        public DecompressionService(IFileService fileService)
        {
            _fileService = fileService;
        }

        public void RunDecompressor(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                throw new ArgumentException("Invalid package path for decompression.", nameof(packagePath));
            }

            if (!_fileService.FileExists(packagePath))
            {
                throw new FileNotFoundException("Package file was not found.", packagePath);
            }

            DecompressPackageInPlace(packagePath);
        }

        private void DecompressPackageInPlace(string packagePath)
        {
            string temporaryPath = $"{packagePath}.metweaks.tmp";
            var fileInfo = new FileInfo(packagePath);
            bool wasReadOnly = fileInfo.IsReadOnly;
            bool fileReplaced = false;

            try
            {
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                bool shouldReplaceFile = false;
                using (var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (source.Length < sizeof(uint) * 2)
                    {
                        return;
                    }

                    using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
                    uint packageTag = reader.ReadUInt32();
                    if (packageTag == PackageFileTagReverse)
                    {
                        throw new NotSupportedException("Big-endian Unreal packages are not supported.");
                    }

                    if (packageTag != PackageFileTag)
                    {
                        return;
                    }

                    uint secondDword = reader.ReadUInt32();
                    source.Position = 0;

                    if (IsFullyCompressedPackage(secondDword))
                    {
                        using var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        DecompressFullyCompressedPackage(source, destination);
                        destination.Flush(true);
                        shouldReplaceFile = true;
                    }
                    else
                    {
                        if (!TryReadUe3Summary(source, out var summary))
                        {
                            return;
                        }

                        if (summary.CompressionFlags == 0 || summary.CompressedChunks.Count == 0)
                        {
                            return;
                        }

                        source.Position = 0;
                        using var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        DecompressPartiallyCompressedPackage(source, destination, summary);
                        destination.Flush(true);
                        shouldReplaceFile = true;
                    }
                }

                if (!shouldReplaceFile)
                {
                    return;
                }

                File.Copy(temporaryPath, packagePath, overwrite: true);
                fileReplaced = true;
            }
            finally
            {
                if (_fileService.FileExists(temporaryPath))
                {
                    _fileService.DeleteFile(temporaryPath);
                }

                if (wasReadOnly && (_fileService.FileExists(packagePath) || fileReplaced))
                {
                    new FileInfo(packagePath).IsReadOnly = true;
                }
            }
        }

        private static bool IsFullyCompressedPackage(uint secondDword)
        {
            return secondDword is FullyCompressedMarkerA or FullyCompressedMarkerB or FullyCompressedMarkerC;
        }

        private static bool TryReadUe3Summary(Stream source, out Ue3Summary summary)
        {
            source.Position = 0;
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadUInt32() != PackageFileTag)
            {
                summary = default;
                return false;
            }

            ushort version = reader.ReadUInt16();
            ushort licenseeVersion = reader.ReadUInt16();
            // Mirror's Edge uses UE3 version 536/43
            if (version != 536 || licenseeVersion != 43)
            {
                summary = default;
                return false;
            }

            if (version >= 249)
            {
                _ = reader.ReadInt32(); // HeadersSize
            }

            if (version >= 269)
            {
                ReadUnrealString(reader);
            }

            long packageFlagsOffset = source.Position;
            int packageFlags = reader.ReadInt32();

            _ = reader.ReadInt32(); // NameCount
            _ = reader.ReadInt32(); // NameOffset
            _ = reader.ReadInt32(); // ExportCount
            _ = reader.ReadInt32(); // ExportOffset
            _ = reader.ReadInt32(); // ImportCount
            _ = reader.ReadInt32(); // ImportOffset

            if (version >= 415)
            {
                _ = reader.ReadInt32(); // DependsOffset
            }

            if (version >= 584)
            {
                _ = reader.ReadInt32(); // unknown int32
            }

            ReadGuid(reader);

            int generationCount = reader.ReadInt32();
            if (generationCount < 0 || generationCount > 8192)
            {
                throw new InvalidDataException($"Invalid generation count: {generationCount}.");
            }

            for (int i = 0; i < generationCount; i++)
            {
                _ = reader.ReadInt32(); // ExportCount
                _ = reader.ReadInt32(); // NameCount
                if (version >= 322)
                {
                    _ = reader.ReadInt32(); // NetObjectCount
                }
            }

            if (version >= 245)
            {
                _ = reader.ReadInt32(); // EngineVersion
            }

            if (version >= 277)
            {
                _ = reader.ReadInt32(); // CookerVersion
            }

            long compressionFlagsOffset = source.Position;
            int compressionFlags = reader.ReadInt32();
            int chunkCount = reader.ReadInt32();
            if (chunkCount < 0 || chunkCount > 131072)
            {
                throw new InvalidDataException($"Invalid compressed chunk count: {chunkCount}.");
            }

            var chunks = new List<Ue3CompressedChunk>(chunkCount);
            for (int i = 0; i < chunkCount; i++)
            {
                int uncompressedOffset = reader.ReadInt32();
                int uncompressedSize = reader.ReadInt32();
                int compressedOffset = reader.ReadInt32();
                int compressedSize = reader.ReadInt32();
                chunks.Add(new Ue3CompressedChunk(uncompressedOffset, uncompressedSize, compressedOffset, compressedSize));
            }

            summary = new Ue3Summary(packageFlagsOffset, packageFlags, compressionFlagsOffset, compressionFlags, chunks);
            return true;
        }

        private static void DecompressPartiallyCompressedPackage(Stream source, Stream destination, Ue3Summary summary)
        {
            Ue3CompressedChunk firstChunk = summary.CompressedChunks.OrderBy(chunk => chunk.UncompressedOffset).First();
            if (firstChunk.CompressedOffset <= 0)
            {
                throw new InvalidDataException("Invalid compressed chunk offset.");
            }

            byte[] headerBytes = new byte[firstChunk.CompressedOffset];
            source.Position = 0;
            ReadExactly(source, headerBytes, 0, headerBytes.Length);

            PatchPackageFlags(headerBytes, summary.PackageFlagsOffset, summary.PackageFlags);
            RemoveCompressionTable(headerBytes, summary.CompressionInfoOffset, summary.CompressedChunks.Count);

            if (firstChunk.UncompressedOffset < 0 || firstChunk.UncompressedOffset > headerBytes.Length)
            {
                throw new InvalidDataException("Invalid uncompressed header size.");
            }

            destination.Write(headerBytes, 0, firstChunk.UncompressedOffset);

            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            foreach (Ue3CompressedChunk chunk in summary.CompressedChunks.OrderBy(chunk => chunk.UncompressedOffset))
            {
                if (chunk.UncompressedOffset < 0 || chunk.UncompressedSize < 0 || chunk.CompressedOffset < 0 || chunk.CompressedSize < 0)
                {
                    throw new InvalidDataException("Encountered invalid chunk boundaries while decompressing.");
                }

                destination.Position = chunk.UncompressedOffset;
                DecompressChunkData(reader, source, destination, chunk, summary.CompressionFlags);
            }
        }

        private static void DecompressFullyCompressedPackage(Stream source, Stream destination)
        {
            source.Position = 0;
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            CompressedChunkHeader chunkHeader = ReadCompressedChunkHeader(reader);

            int compressionMethod = 0;
            foreach (CompressedChunkBlock block in chunkHeader.Blocks)
            {
                byte[] compressedBlock = reader.ReadBytes(block.CompressedSize);
                if (compressedBlock.Length != block.CompressedSize)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading fully compressed package data.");
                }

                compressionMethod = compressionMethod == 0
                    ? DetectCompressionMethod(compressedBlock)
                    : compressionMethod;

                byte[] decompressed = DecompressBlock(compressedBlock, block.UncompressedSize, compressionMethod);
                destination.Write(decompressed, 0, decompressed.Length);
            }
        }

        private static void DecompressChunkData(BinaryReader reader, Stream source, Stream destination, Ue3CompressedChunk chunk, int compressionFlags)
        {
            source.Position = chunk.CompressedOffset;
            if (chunk.CompressedSize == chunk.UncompressedSize)
            {
                CopyBytes(source, destination, chunk.UncompressedSize);
                return;
            }

            CompressedChunkHeader chunkHeader = ReadCompressedChunkHeader(reader);
            int totalUncompressed = 0;
            foreach (CompressedChunkBlock block in chunkHeader.Blocks)
            {
                byte[] compressedBlock = reader.ReadBytes(block.CompressedSize);
                if (compressedBlock.Length != block.CompressedSize)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading compressed block.");
                }

                byte[] decompressed = DecompressBlock(compressedBlock, block.UncompressedSize, compressionFlags);
                destination.Write(decompressed, 0, decompressed.Length);
                totalUncompressed += decompressed.Length;
            }

            if (totalUncompressed != chunk.UncompressedSize)
            {
                throw new InvalidDataException($"Chunk size mismatch: expected {chunk.UncompressedSize}, got {totalUncompressed}.");
            }
        }

        private static CompressedChunkHeader ReadCompressedChunkHeader(BinaryReader reader)
        {
            uint tag = reader.ReadUInt32();
            if (tag == PackageFileTagReverse)
            {
                throw new NotSupportedException("Big-endian compressed chunk headers are not supported.");
            }

            if (tag != PackageFileTag)
            {
                throw new InvalidDataException($"Invalid compressed chunk header tag: 0x{tag:X8}.");
            }

            _ = reader.ReadInt32(); // BlockSize (not needed here)
            int totalCompressedSize = reader.ReadInt32();
            int totalUncompressedSize = reader.ReadInt32();
            if (totalCompressedSize < 0 || totalUncompressedSize < 0)
            {
                throw new InvalidDataException("Invalid compressed chunk summary sizes.");
            }

            var blocks = new List<CompressedChunkBlock>();
            int compressedSum = 0;
            int uncompressedSum = 0;
            while (compressedSum < totalCompressedSize && uncompressedSum < totalUncompressedSize)
            {
                int blockCompressed = reader.ReadInt32();
                int blockUncompressed = reader.ReadInt32();
                if (blockCompressed <= 0 || blockUncompressed <= 0)
                {
                    throw new InvalidDataException("Invalid block size encountered in compressed chunk.");
                }

                blocks.Add(new CompressedChunkBlock(blockCompressed, blockUncompressed));
                compressedSum += blockCompressed;
                uncompressedSum += blockUncompressed;
            }

            if (uncompressedSum != totalUncompressedSize)
            {
                throw new InvalidDataException("Compressed chunk block table did not match expected uncompressed size.");
            }

            return new CompressedChunkHeader(blocks);
        }

        private static byte[] DecompressBlock(byte[] compressedData, int expectedUncompressedSize, int compressionFlags)
        {
            if (expectedUncompressedSize == 0)
            {
                return Array.Empty<byte>();
            }

            try
            {
                int method = ResolveCompressionMethod(compressedData, compressionFlags);
                return method switch
                {
                    CompressZlib => DecompressWithZlib(compressedData, expectedUncompressedSize),
                    CompressLzo => DecompressWithLzo(compressedData, expectedUncompressedSize),
                    CompressLzx => throw new NotSupportedException("LZX compressed packages are not supported by MirrorsEdgeTweaks."),
                    _ => throw new InvalidDataException($"Unsupported compression method: {method}.")
                };
            }
            catch (Exception) when (compressedData.Length == expectedUncompressedSize)
            {
                // UE3 occasionally stores uncompressed blocks inside a compressed container
                byte[] uncompressedCopy = new byte[expectedUncompressedSize];
                Buffer.BlockCopy(compressedData, 0, uncompressedCopy, 0, expectedUncompressedSize);
                return uncompressedCopy;
            }
        }

        private static int ResolveCompressionMethod(byte[] compressedData, int compressionFlags)
        {
            if ((compressionFlags & CompressLzo) != 0)
            {
                return CompressLzo;
            }

            if ((compressionFlags & CompressZlib) != 0)
            {
                return CompressZlib;
            }

            if ((compressionFlags & CompressLzx) != 0)
            {
                return CompressLzx;
            }

            return DetectCompressionMethod(compressedData);
        }

        private static int DetectCompressionMethod(ReadOnlySpan<byte> compressedData)
        {
            if (compressedData.Length >= 2)
            {
                int cmf = compressedData[0];
                int flg = compressedData[1];
                if ((cmf & 0x0F) == 8 && ((cmf << 8) + flg) % 31 == 0)
                {
                    return CompressZlib;
                }
            }

            return CompressLzo;
        }

        private static byte[] DecompressWithZlib(byte[] compressedData, int expectedUncompressedSize)
        {
            using var compressedStream = new MemoryStream(compressedData, writable: false);
            using var zlibStream = new ZLibStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
            return ReadExactly(zlibStream, expectedUncompressedSize);
        }

        private static byte[] DecompressWithLzo(byte[] compressedData, int expectedUncompressedSize)
        {
            try
            {
                return Lzo.Decompress(compressedData, expectedUncompressedSize);
            }
            catch (LzoException ex)
            {
                throw new InvalidDataException(
                    $"SharpLzo failed to decompress LZO block (result: {ex.Result}).",
                    ex);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "SharpLzo failed to decompress LZO block.",
                    ex);
            }
        }

        private static byte[] ReadExactly(Stream stream, int expectedLength)
        {
            if (expectedLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedLength));
            }

            byte[] buffer = new byte[expectedLength];
            ReadExactly(stream, buffer, 0, expectedLength);
            return buffer;
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading package data.");
                }

                totalRead += read;
            }
        }

        private static void CopyBytes(Stream source, Stream destination, int count)
        {
            byte[] buffer = new byte[DefaultCopyBufferSize];
            int remaining = count;
            while (remaining > 0)
            {
                int readSize = Math.Min(remaining, buffer.Length);
                int read = source.Read(buffer, 0, readSize);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected EOF while copying package bytes.");
                }

                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static string ReadUnrealString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == 0)
            {
                return string.Empty;
            }

            if (length < 0)
            {
                int characterCount = -length;
                if (characterCount <= 0)
                {
                    return string.Empty;
                }

                byte[] unicodeBytes = reader.ReadBytes(characterCount * sizeof(char));
                if (unicodeBytes.Length != characterCount * sizeof(char))
                {
                    throw new EndOfStreamException("Unexpected EOF while reading Unreal Unicode string.");
                }

                int byteCountWithoutTerminator = Math.Max(0, (characterCount - 1) * sizeof(char));
                return Encoding.Unicode.GetString(unicodeBytes, 0, byteCountWithoutTerminator);
            }

            byte[] asciiBytes = reader.ReadBytes(length);
            if (asciiBytes.Length != length)
            {
                throw new EndOfStreamException("Unexpected EOF while reading Unreal ANSI string.");
            }

            int byteCount = Math.Max(0, length - 1); // FString length includes null terminator
            return Encoding.Latin1.GetString(asciiBytes, 0, byteCount);
        }

        private static void ReadGuid(BinaryReader reader)
        {
            byte[] guidBytes = reader.ReadBytes(16);
            if (guidBytes.Length != 16)
            {
                throw new EndOfStreamException("Unexpected EOF while reading package GUID.");
            }
        }

        private static void PatchPackageFlags(byte[] headerBytes, long packageFlagsOffset, int packageFlags)
        {
            if (packageFlagsOffset < 0 || packageFlagsOffset + sizeof(int) > headerBytes.Length)
            {
                throw new InvalidDataException("Package flags offset is outside the package header.");
            }

            int patchedFlags = packageFlags & ~PkgStoreCompressed;
            Buffer.BlockCopy(BitConverter.GetBytes(patchedFlags), 0, headerBytes, (int)packageFlagsOffset, sizeof(int));
        }

        private static void RemoveCompressionTable(byte[] headerBytes, long compressionInfoOffset, int chunkCount)
        {
            if (compressionInfoOffset < 0 || compressionInfoOffset + sizeof(int) * 2 > headerBytes.Length)
            {
                throw new InvalidDataException("Compression info offset is outside the package header.");
            }

            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, headerBytes, (int)compressionInfoOffset, sizeof(int));
            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, headerBytes, (int)compressionInfoOffset + sizeof(int), sizeof(int));

            int chunkTableBytes = checked(chunkCount * CompressedChunkSizeBytes);
            int destinationPosition = (int)compressionInfoOffset + sizeof(int) * 2;
            int sourcePosition = destinationPosition + chunkTableBytes;
            if (sourcePosition > headerBytes.Length)
            {
                throw new InvalidDataException("Compression chunk table points beyond the package header.");
            }

            Buffer.BlockCopy(headerBytes, sourcePosition, headerBytes, destinationPosition, headerBytes.Length - sourcePosition);
        }

        private readonly record struct Ue3CompressedChunk(int UncompressedOffset, int UncompressedSize, int CompressedOffset, int CompressedSize);

        private readonly record struct Ue3Summary(
            long PackageFlagsOffset,
            int PackageFlags,
            long CompressionInfoOffset,
            int CompressionFlags,
            IReadOnlyList<Ue3CompressedChunk> CompressedChunks);

        private readonly record struct CompressedChunkBlock(int CompressedSize, int UncompressedSize);

        private readonly record struct CompressedChunkHeader(IReadOnlyList<CompressedChunkBlock> Blocks);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MirrorsEdgeTweaks.Services
{
    public sealed class OoaLicenseNotFoundException : InvalidOperationException
    {
        public string ExpectedPath { get; }

        public OoaLicenseNotFoundException(string expectedPath)
            : base($"EA license file not found at {expectedPath}. " +
                   "The game must be activated through the EA App to patch the executable.")
        {
            ExpectedPath = expectedPath;
        }
    }

    // Captured state from OOA section decryption, needed to re-encrypt.
    public sealed class OoaContext
    {
        internal List<SectionRange> Sections { get; } = new();
        internal int OoaSectionRawEnd { get; set; }

        internal readonly struct SectionRange
        {
            public int RawOffset { get; init; }
            public int RawSize { get; init; }
            public int EncBlockIndex { get; init; }
        }
    }

    // OOA (Origin Online Activation) decrypt/re-encrypt for EA App executables.
    // The .ooa PE section stores AES-128-CBC encryption metadata: content ID,
    // encrypted block list and the per section IVs are the 16 bytes immediately
    // preceding each section's raw data in the file.
    public static class OoaService
    {
        // .ooa section body offsets (Mirror's Edge PE32 variant)
        const int ContentIdOffset = 0x42;
        const int ContentIdMaxBytes = 0x200;
        const int BlockCountOffset = 0x3DE;
        const int BlocksStartOffset = 0x3DF;
        const int BlockEntrySize = 48;

        static readonly byte[] DlfDecryptionKey =
        {
            65, 50, 114, 45, 208, 130, 239, 176,
            220, 100, 87, 197, 118, 104, 202, 9
        };

        const string CipherKeyXmlTag = "<CipherKey>";
        const int CipherKeyBase64Chars = 24;
        const int AesBlockSize = 16;

        public static bool HasOoaSection(byte[] data)
        {
            if (!TryGetSectionTable(data, out int secTableOff, out int numSections))
                return false;

            for (int i = 0; i < numSections; i++)
            {
                int hdr = secTableOff + i * 40;
                if (hdr + 8 > data.Length) break;
                if (SectionNameStartsWith(data, hdr, ".ooa"))
                    return true;
            }

            return false;
        }

        // Returns null if the .ooa section is absent or the file doesn't exist on disk.
        public static string? FindLicensePath(byte[] data)
        {
            string? contentId = ExtractContentId(data);
            if (contentId == null) return null;

            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            string dlfPath = Path.Combine(
                programData, "Electronic Arts", "EA Services", "License",
                contentId + ".dlf");

            return File.Exists(dlfPath) ? dlfPath : null;
        }

        // Unlike FindLicensePath, does not check whether the file exists.
        public static string GetExpectedLicensePath(byte[] data)
        {
            string contentId = ExtractContentId(data) ?? "54744";
            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(
                programData, "Electronic Arts", "EA Services", "License",
                contentId + ".dlf");
        }

        // Decrypts an EA .dlf license file and extracts the 16 byte AES key.
        // The DLF is itself AES-128-CBC encrypted with a key common to all
        // OOA-protected titles - the plaintext is XML containing a base64
        // CipherKey element.
        public static byte[] DecryptDlf(byte[] dlfData)
        {
            // Some DLF files have a 0x41-byte header before the encrypted payload.
            foreach (int start in new[] { 0x41, 0 })
            {
                if (start >= dlfData.Length) continue;
                int len = dlfData.Length - start;
                len -= len % AesBlockSize;
                if (len < AesBlockSize) continue;

                byte[] decrypted;
                try
                {
                    decrypted = AesCbc(
                        DlfDecryptionKey, new byte[AesBlockSize],
                        dlfData, start, len, encrypt: false);
                }
                catch
                {
                    continue;
                }

                decrypted = StripPkcs7(decrypted);
                string xml = Encoding.UTF8.GetString(decrypted);
                int tagIdx = xml.IndexOf(CipherKeyXmlTag, StringComparison.Ordinal);
                if (tagIdx < 0) continue;

                int b64Start = tagIdx + CipherKeyXmlTag.Length;
                if (b64Start + CipherKeyBase64Chars > xml.Length) continue;

                byte[] decoded = Convert.FromBase64String(
                    xml.Substring(b64Start, CipherKeyBase64Chars));
                if (decoded.Length < AesBlockSize)
                    continue;

                byte[] key = new byte[AesBlockSize];
                Buffer.BlockCopy(decoded, 0, key, 0, AesBlockSize);
                return key;
            }

            throw new InvalidOperationException(
                "Failed to extract encryption key from EA license file. " +
                "The license may be corrupt or from a different game.");
        }

        // Decrypts OOA-encrypted PE sections (.text, .data) in the buffer.
        // The PE headers and .ooa section are left untouched - only the
        // encrypted section contents are replaced with plaintext.
        // Returns an "OoaContext" required for re-encryption.
        public static OoaContext DecryptSections(byte[] data, byte[] key)
        {
            var blocks = ParseEncryptedBlocks(data);
            var ooa = FindOoaSection(data);
            var ctx = new OoaContext { OoaSectionRawEnd = ooa.RawOffset + ooa.RawSize };

            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var (rawOff, rawSize) = FindSectionByVA(data, block.VA);
                if (rawSize != block.RawSize)
                    throw new InvalidOperationException(
                        $"Section raw size mismatch at VA 0x{block.VA:X8}: " +
                        $"PE header says 0x{rawSize:X}, .ooa says 0x{block.RawSize:X}.");

                byte[] iv = new byte[AesBlockSize];
                Buffer.BlockCopy(data, rawOff - AesBlockSize, iv, 0, AesBlockSize);

                byte[] plaintext = AesCbc(key, iv, data, rawOff, rawSize, encrypt: false);
                Buffer.BlockCopy(plaintext, 0, data, rawOff, rawSize);

                // PKCS7 padding artifact: OOA encryption pads sections that end with
                // zeros, producing 0x10-repeated tails after decryption. Zero them out
                // to recover the original plaintext.
                int tailOff = rawOff + rawSize - AesBlockSize;
                if (IsFilledWith(data, tailOff, AesBlockSize, 0x10))
                    Array.Clear(data, tailOff, AesBlockSize);

                ctx.Sections.Add(new OoaContext.SectionRange
                {
                    RawOffset = rawOff,
                    RawSize = rawSize,
                    EncBlockIndex = i,
                });
            }

            return ctx;
        }

        public static void ReencryptSections(byte[] data, byte[] key, OoaContext ctx)
        {
            foreach (var section in ctx.Sections)
            {
                // Restore PKCS7 padding artifact before encrypting
                int tailOff = section.RawOffset + section.RawSize - AesBlockSize;
                if (IsFilledWith(data, tailOff, AesBlockSize, 0x00))
                {
                    for (int i = tailOff; i < tailOff + AesBlockSize; i++)
                        data[i] = 0x10;
                }

                byte[] iv = new byte[AesBlockSize];
                Buffer.BlockCopy(data, section.RawOffset - AesBlockSize, iv, 0, AesBlockSize);

                byte[] ciphertext = AesCbc(
                    key, iv, data, section.RawOffset, section.RawSize, encrypt: true);
                Buffer.BlockCopy(ciphertext, 0, data, section.RawOffset, section.RawSize);
            }
        }

        // PE parsing

        static bool TryGetSectionTable(byte[] data,
            out int secTableOff, out int numSections)
        {
            secTableOff = numSections = 0;
            if (data.Length < 0x40 || data[0] != 0x4D || data[1] != 0x5A)
                return false;

            int peOff = (int)BitConverter.ToUInt32(data, 0x3C);
            if (peOff + 4 > data.Length) return false;
            if (data[peOff] != 0x50 || data[peOff + 1] != 0x45) return false;

            int fileHdr = peOff + 4;
            numSections = BitConverter.ToUInt16(data, fileHdr + 2);
            int optSize = BitConverter.ToUInt16(data, fileHdr + 16);
            secTableOff = fileHdr + 20 + optSize;
            return secTableOff + numSections * 40 <= data.Length;
        }

        static bool SectionNameStartsWith(byte[] data, int sectionHdrOff, string prefix)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(prefix);
            if (sectionHdrOff + nameBytes.Length > data.Length) return false;
            for (int i = 0; i < nameBytes.Length; i++)
            {
                if (data[sectionHdrOff + i] != nameBytes[i]) return false;
            }
            return true;
        }

        static (int rawOffset, int rawSize) FindSectionByVA(byte[] data, uint sectionVA)
        {
            if (!TryGetSectionTable(data, out int secTableOff, out int numSections))
                throw new InvalidOperationException("Invalid PE file.");

            for (int i = 0; i < numSections; i++)
            {
                int hdr = secTableOff + i * 40;
                uint va = BitConverter.ToUInt32(data, hdr + 12);
                if (va == sectionVA)
                {
                    int rawPtr = (int)BitConverter.ToUInt32(data, hdr + 20);
                    int rawSize = (int)BitConverter.ToUInt32(data, hdr + 16);
                    return (rawPtr, rawSize);
                }
            }

            throw new InvalidOperationException(
                $"No PE section with VirtualAddress 0x{sectionVA:X8}.");
        }

        // .ooa parsing

        struct EncBlock
        {
            public uint VA;
            public uint RawSize;
        }

        struct OoaSectionInfo
        {
            public int RawOffset;
            public int RawSize;
            public byte[] Data;
        }

        static string? ExtractContentId(byte[] data)
        {
            try
            {
                var ooa = FindOoaSection(data);
                if (ooa.Data.Length < ContentIdOffset + 2) return null;

                int end = Math.Min(ooa.Data.Length, ContentIdOffset + ContentIdMaxBytes);
                var sb = new StringBuilder();
                for (int i = ContentIdOffset; i + 1 < end; i += 2)
                {
                    ushort c = BitConverter.ToUInt16(ooa.Data, i);
                    if (c == 0) break;
                    sb.Append((char)c);
                }

                string full = sb.ToString();
                int comma = full.IndexOf(',');
                return comma >= 0 ? full.Substring(0, comma) : full;
            }
            catch
            {
                return null;
            }
        }

        static List<EncBlock> ParseEncryptedBlocks(byte[] peData)
        {
            var ooa = FindOoaSection(peData);
            if (ooa.Data.Length <= BlockCountOffset)
                throw new InvalidOperationException(
                    ".ooa section is too small to contain encryption metadata.");

            int count = ooa.Data[BlockCountOffset];
            var blocks = new List<EncBlock>(count);
            for (int i = 0; i < count; i++)
            {
                int off = BlocksStartOffset + i * BlockEntrySize;
                if (off + 8 > ooa.Data.Length)
                    throw new InvalidOperationException(
                        ".ooa section is truncated at encrypted block list.");

                blocks.Add(new EncBlock
                {
                    VA = BitConverter.ToUInt32(ooa.Data, off),
                    RawSize = BitConverter.ToUInt32(ooa.Data, off + 4),
                });
            }

            return blocks;
        }

        static OoaSectionInfo FindOoaSection(byte[] data)
        {
            if (!TryGetSectionTable(data, out int secTableOff, out int numSections))
                throw new InvalidOperationException("Invalid PE file.");

            for (int i = 0; i < numSections; i++)
            {
                int hdr = secTableOff + i * 40;
                if (!SectionNameStartsWith(data, hdr, ".ooa")) continue;

                int rawOffset = (int)BitConverter.ToUInt32(data, hdr + 20);
                int rawSize = (int)BitConverter.ToUInt32(data, hdr + 16);
                if (rawOffset + rawSize > data.Length)
                    throw new InvalidOperationException(
                        ".ooa section extends past end of file.");

                byte[] secData = new byte[rawSize];
                Buffer.BlockCopy(data, rawOffset, secData, 0, rawSize);
                return new OoaSectionInfo
                {
                    RawOffset = rawOffset,
                    RawSize = rawSize,
                    Data = secData,
                };
            }

            throw new InvalidOperationException(
                "No .ooa section found in executable.");
        }

        // Authenticode stripping

        // Zeros the PE Certificate Table data directory entry so that
        // WinVerifyTrust returns TRUST_E_NOSIGNATURE instead of
        // TRUST_E_BAD_DIGEST for a modified binary.
        public static void StripAuthenticode(byte[] data)
        {
            int peOff = (int)BitConverter.ToUInt32(data, 0x3C);
            int optHdr = peOff + 24;
            int certDdOff = optHdr + 96 + 4 * 8;
            BitConverter.GetBytes(0u).CopyTo(data, certDdOff);
            BitConverter.GetBytes(0u).CopyTo(data, certDdOff + 4);
        }

        public static byte[] TruncateOverlay(byte[] data, OoaContext ctx)
        {
            if (ctx.OoaSectionRawEnd > 0 && ctx.OoaSectionRawEnd < data.Length)
            {
                byte[] truncated = new byte[ctx.OoaSectionRawEnd];
                Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);
                return truncated;
            }

            return data;
        }

        // EncBlock CRC update

        const uint Crc32Mpeg2Poly = 0x04C11DB7;
        static uint[]? _crc32Table;

        // Must be called after patching but before re-encryption
        public static void UpdateEncBlockCrcs(byte[] data, OoaContext ctx)
        {
            var ooa = FindOoaSection(data);

            foreach (var section in ctx.Sections)
            {
                uint crc = Crc32Mpeg2Stride4(data, section.RawOffset, section.RawSize);
                int crcFieldOff = ooa.RawOffset + BlocksStartOffset
                                  + section.EncBlockIndex * BlockEntrySize + 0x10;
                BitConverter.GetBytes(crc).CopyTo(data, crcFieldOff);
            }
        }

        static uint Crc32Mpeg2Stride4(byte[] data, int offset, int length)
        {
            uint[] tbl = GetCrc32Table();
            uint crc = 0;
            int dwords = (length + 3) >> 2;
            for (int i = 0; i < dwords; i++)
            {
                byte b = data[offset + i * 4];
                int idx = (int)((crc >> 24) ^ b) & 0xFF;
                crc = (crc << 8) ^ tbl[idx];
            }

            return crc;
        }

        static uint[] GetCrc32Table()
        {
            if (_crc32Table != null) return _crc32Table;

            var tbl = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                uint c = (uint)i << 24;
                for (int j = 0; j < 8; j++)
                    c = (c & 0x80000000) != 0
                        ? (c << 1) ^ Crc32Mpeg2Poly
                        : c << 1;
                tbl[i] = c;
            }

            _crc32Table = tbl;
            return tbl;
        }

        // Crypto helpers

        static byte[] AesCbc(byte[] key, byte[] iv,
            byte[] data, int offset, int length, bool encrypt)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            using var transform = encrypt
                ? aes.CreateEncryptor()
                : aes.CreateDecryptor();
            return transform.TransformFinalBlock(data, offset, length);
        }

        static byte[] StripPkcs7(byte[] data)
        {
            if (data.Length == 0) return data;
            byte pad = data[data.Length - 1];
            if (pad < 1 || pad > AesBlockSize || pad > data.Length) return data;
            for (int i = data.Length - pad; i < data.Length; i++)
            {
                if (data[i] != pad) return data;
            }

            byte[] result = new byte[data.Length - pad];
            Buffer.BlockCopy(data, 0, result, 0, result.Length);
            return result;
        }

        static bool IsFilledWith(byte[] data, int offset, int length, byte value)
        {
            for (int i = offset; i < offset + length; i++)
            {
                if (data[i] != value) return false;
            }
            return true;
        }
    }
}

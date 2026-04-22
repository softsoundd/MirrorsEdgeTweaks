using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class ExeVersionDetector
    {
        private static readonly Dictionary<string, string> Sha256ToVersion = new()
        {
            ["58de3df21f40e9953e00be92f7749097832bad02e5b657d8485cdb61f360a3dd"] = "gog",
            ["a0f653b63b299d5d3899b8b4fa6e4d47eeb390bf26b1f020710482eabfe297f0"] = "steam",
            ["c6692e71956e2ce86c93b11816c9280ba72e771403a9261a6a516f3aa1c7b568"] = "retail",
            ["4d5ecc40887a9a324fa80c8fdc7a13e0e7afb8da090c0560e3517a21d13c4ba9"] = "dlc",
            ["c22fd2378d90cc1305a65c604be12d2a338f6a0c693e3135f7402eb917b0437a"] = "ea",
        };

        public static readonly Dictionary<string, string> VersionLabels = new()
        {
            ["gog"] = "GOG (1.0.1.0, DRM-free)",
            ["steam"] = "Steam (1.0.1.0, SteamStub)",
            ["retail"] = "Retail (1.0.1.0, SecuROM)",
            ["dlc"] = "DLC (1.1.0.0, SecuROM)",
            ["ea"] = "EA App (1.0.1.0, OOA-encrypted)",
        };

        private const long SteamFileSize = 31946072;

        private static readonly HashSet<string> KnownCaveTags = new() { "gog", "steam", "retail", "dlc" };
        private static readonly byte[] CaveSectionName = Encoding.ASCII.GetBytes(".cave\0\0\0");

        public static string? DetectVersion(byte[] peData, string? exePath = null)
        {
            byte[] hash = SHA256.HashData(peData);
            string hex = Convert.ToHexString(hash).ToLowerInvariant();
            if (Sha256ToVersion.TryGetValue(hex, out string? version))
                return version;

            string? tag = ReadCaveVersionTag(peData);
            if (tag != null && VersionLabels.ContainsKey(tag))
                return tag;

            if (exePath != null)
                return DetectByPeMetadata(exePath, peData);

            return null;
        }

        private static string? ReadCaveVersionTag(byte[] d)
        {
            if (d.Length < 0x40 || d[0] != (byte)'M' || d[1] != (byte)'Z')
                return null;

            int peOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x3C, 4));
            if (peOff + 4 > d.Length) return null;
            if (d[peOff] != 0x50 || d[peOff + 1] != 0x45 || d[peOff + 2] != 0 || d[peOff + 3] != 0)
                return null;

            int coffOff = peOff + 4;
            int numSec = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(coffOff + 2, 2));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(coffOff + 16, 2));
            int secBase = coffOff + 20 + optSize;

            for (int i = 0; i < numSec; i++)
            {
                int hdr = secBase + i * 40;
                if (hdr + 40 > d.Length) break;
                if (d.AsSpan(hdr, 8).SequenceEqual(CaveSectionName))
                {
                    int rawOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 20, 4));
                    if (rawOff + 12 <= d.Length)
                    {
                        byte[] tagBytes = new byte[8];
                        Buffer.BlockCopy(d, rawOff + 4, tagBytes, 0, 8);
                        int nul = Array.IndexOf(tagBytes, (byte)0);
                        string tag = Encoding.ASCII.GetString(tagBytes, 0, nul >= 0 ? nul : 8);
                        if (!string.IsNullOrEmpty(tag))
                            return tag;
                    }
                }
            }

            for (int i = 0; i < numSec; i++)
            {
                int hdr = secBase + i * 40;
                if (hdr + 40 > d.Length) break;
                int nameEnd = 8;
                while (nameEnd > 0 && d[hdr + nameEnd - 1] == 0) nameEnd--;
                if (nameEnd == 5 && d[hdr] == '.' && d[hdr + 1] == 't' &&
                    d[hdr + 2] == 'e' && d[hdr + 3] == 'x' && d[hdr + 4] == 't')
                {
                    int rawOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 20, 4));
                    int rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(hdr + 16, 4));
                    int end = rawOff + rawSize;
                    int limit = Math.Max(rawOff, end - 0x2000);
                    for (int off = end - 16; off > limit; off -= 16)
                    {
                        if (off + 12 > d.Length) continue;
                        uint wm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off, 4));
                        if (wm <= 12 || wm >= rawSize) continue;

                        byte[] tagBytes = new byte[8];
                        Buffer.BlockCopy(d, off + 4, tagBytes, 0, 8);
                        int nul = Array.IndexOf(tagBytes, (byte)0);
                        string tag = Encoding.ASCII.GetString(tagBytes, 0, nul >= 0 ? nul : 8);
                        if (KnownCaveTags.Contains(tag))
                            return tag;
                    }
                }
            }

            return null;
        }

        private static string? DetectByPeMetadata(string exePath, byte[] peData)
        {
            try
            {
                string? fileVersion = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                if (string.IsNullOrEmpty(fileVersion))
                    return null;

                if (fileVersion.StartsWith("1.1.0.0", StringComparison.OrdinalIgnoreCase))
                    return "dlc";

                if (!fileVersion.StartsWith("1.0.", StringComparison.OrdinalIgnoreCase))
                    return null;

                // 1.0.x.0 base game: GOG, Steam, Retail, or EA.
                if (OoaService.HasOoaSection(peData))
                    return "ea";

                long fileSize = new FileInfo(exePath).Length;
                if (fileSize == SteamFileSize)
                    return "steam";

                if (HasFullSectionTable(peData))
                    return "retail";

                return "gog";
            }
            catch
            {
                return null;
            }
        }

        private static bool HasFullSectionTable(byte[] d)
        {
            if (d.Length < 0x40 || d[0] != (byte)'M' || d[1] != (byte)'Z')
                return false;

            int peOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x3C, 4));
            if (peOff + 24 > d.Length) return false;

            int coffOff = peOff + 4;
            int numSec = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(coffOff + 2, 2));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(coffOff + 16, 2));
            int optOff = coffOff + 20;
            if (optOff + 64 > d.Length) return false;

            uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(optOff + 60, 4));
            int secBase = optOff + optSize;
            int nextHdrEnd = secBase + (numSec + 1) * 40;

            return nextHdrEnd > sizeOfHeaders;
        }
    }
}

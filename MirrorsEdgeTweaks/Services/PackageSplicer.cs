using System;
using System.Collections.Generic;

namespace MirrorsEdgeTweaks.Services
{
    // Shared splice/fixup infrastructure for UE3 bytecode patching.
    // Used by both apply and remove paths.
    public static class PackageSplicer
    {
        public const uint UE3_TAG = 0x9E2A83C1;

        public static byte[] InsertBytes(byte[] data, int filePos, byte[] blob)
        {
            var result = new byte[data.Length + blob.Length];
            Buffer.BlockCopy(data, 0, result, 0, filePos);
            Buffer.BlockCopy(blob, 0, result, filePos, blob.Length);
            Buffer.BlockCopy(data, filePos, result, filePos + blob.Length, data.Length - filePos);
            return result;
        }

        public static byte[] RemoveBytes(byte[] data, int filePos, int count)
        {
            var result = new byte[data.Length - count];
            Buffer.BlockCopy(data, 0, result, 0, filePos);
            Buffer.BlockCopy(data, filePos + count, result, filePos, data.Length - filePos - count);
            return result;
        }

        public static byte[] ReplaceBytes(byte[] data, int filePos, int oldLen, byte[] newBlob)
        {
            int delta = newBlob.Length - oldLen;
            var result = new byte[data.Length + delta];
            Buffer.BlockCopy(data, 0, result, 0, filePos);
            Buffer.BlockCopy(newBlob, 0, result, filePos, newBlob.Length);
            Buffer.BlockCopy(data, filePos + oldLen, result, filePos + newBlob.Length, data.Length - filePos - oldLen);
            return result;
        }

        // Targets at or above thresholdBc are shifted by delta.
        // Validates targets within [0, bss) to avoid corrupting float payloads.
        public static int FixJumpTargets(byte[] data, int bcStart, int bss, int thresholdBc, int delta)
        {
            int count = 0;
            int end = bcStart + bss;
            for (int pos = bcStart; pos < end; pos++)
            {
                byte opcode = data[pos];
                if (opcode == BytecodeBuilder.OP_JUMP_IF_NOT ||
                    opcode == BytecodeBuilder.OP_JUMP ||
                    opcode == BytecodeBuilder.OP_CASE)
                {
                    ushort target = BitConverter.ToUInt16(data, pos + 1);
                    if (target >= thresholdBc && target < bss && target != 0xFFFF)
                    {
                        int newTarget = target + delta;
                        if (newTarget >= 0 && newTarget <= 0xFFFF)
                        {
                            BitConverter.GetBytes((ushort)newTarget).CopyTo(data, pos + 1);
                            count++;
                        }
                    }
                }
            }
            return count;
        }

        public static uint ReadBSS(byte[] data, int exportStart)
        {
            return BitConverter.ToUInt32(data, exportStart + BytecodeBuilder.BSS_REL);
        }

        public static void WriteBSS(byte[] data, int exportStart, uint newBss)
        {
            BitConverter.GetBytes(newBss).CopyTo(data, exportStart + BytecodeBuilder.BSS_REL);
        }

        public static void UpdateBSS(byte[] data, int exportStart, int delta)
        {
            uint bss = ReadBSS(data, exportStart);
            long newBss = (long)bss + delta;
            if (newBss < 0) newBss = 0;
            WriteBSS(data, exportStart, (uint)newBss);
        }

        // UE3 package header parsing

        public struct PackageHeader
        {
            public int FileVersion;
            public int TotalHeaderSize;
            public int NameCount;
            public int NameOffset;
            public int ExportCount;
            public int ExportOffset;
            public int ImportCount;
            public int ImportOffset;
        }

        public static PackageHeader ParseHeader(byte[] data)
        {
            uint tag = BitConverter.ToUInt32(data, 0);
            if (tag != UE3_TAG)
                throw new InvalidOperationException($"Not a UE3 package (tag=0x{tag:X8})");

            int val8 = BitConverter.ToInt32(data, 8);
            int off;
            int totalHdr;
            if (val8 > 10_000 && val8 < data.Length)
            {
                totalHdr = val8;
                off = 12;
            }
            else
            {
                totalHdr = BitConverter.ToInt32(data, 12);
                off = 16;
            }

            int fstrLen = BitConverter.ToInt32(data, off);
            off += 4 + (fstrLen < 0 ? Math.Abs(fstrLen) * 2 : Math.Max(fstrLen, 0));
            off += 4; // PackageFlags

            int nc = BitConverter.ToInt32(data, off);
            int no = BitConverter.ToInt32(data, off + 4);
            int ec = BitConverter.ToInt32(data, off + 8);
            int eo = BitConverter.ToInt32(data, off + 12);
            int ic = BitConverter.ToInt32(data, off + 16);
            int io = BitConverter.ToInt32(data, off + 20);

            return new PackageHeader
            {
                TotalHeaderSize = totalHdr,
                NameCount = nc,
                NameOffset = no,
                ExportCount = ec,
                ExportOffset = eo,
                ImportCount = ic,
                ImportOffset = io,
            };
        }

        // Export table update - heuristic scan (Engine.u style)

        // Heuristic export table fixup: scan for (SerialSize, SerialOffset) pairs.
        // Used by Engine.u where we don't need export index precision.
        public static (int offsetsFixed, int sizesFixed) UpdateExportsHeuristic(
            byte[] buf, PackageHeader hdr, int exportStart, int lastInsPos, int origLen, int totalInsert)
        {
            int nOff = 0, nSz = 0;
            for (int soPos = hdr.ExportOffset + 4; soPos < hdr.TotalHeaderSize - 3; soPos += 4)
            {
                int so = BitConverter.ToInt32(buf, soPos);
                if (so < hdr.TotalHeaderSize || so >= origLen) continue;
                int ssPos = soPos - 4;
                int ss = BitConverter.ToInt32(buf, ssPos);
                if (ss <= 0 || ss >= 10_000_000 || so + ss > origLen) continue;

                int newSo = so, newSs = ss;
                if (so == exportStart)
                    newSs += totalInsert;
                else if (so > lastInsPos)
                    newSo += totalInsert;

                if (newSo != so)
                {
                    BitConverter.GetBytes(newSo).CopyTo(buf, soPos);
                    nOff++;
                }
                if (newSs != ss)
                {
                    BitConverter.GetBytes(newSs).CopyTo(buf, ssPos);
                    nSz++;
                }
            }
            return (nOff, nSz);
        }

        // Export table update - structural walk (TdGame.u style)

        // Structural export table fixup using export index matching.
        // Each modification is (serialOffset, insertSize, exportIndex).
        public static (int offsetsFixed, int sizesFixed) UpdateExportsStructural(
            byte[] buf, PackageHeader hdr,
            List<(int serialOffset, int insertSize, int exportIndex)> modifications)
        {
            var modByIdx = new Dictionary<int, int>();
            foreach (var (so, sz, idx) in modifications)
                modByIdx[idx] = sz;

            var sortedInserts = new List<(int serialOffset, int insertSize, int exportIndex)>(modifications);
            sortedInserts.Sort((a, b) => a.serialOffset.CompareTo(b.serialOffset));

            int nOff = 0, nSz = 0;
            int pos = hdr.ExportOffset;

            for (int exportNum = 1; exportNum <= hdr.ExportCount; exportNum++)
            {
                pos += 4 + 4 + 4 + 8 + 4 + 8; // skip to SerialSize
                int ssPos = pos;
                int serialSize = BitConverter.ToInt32(buf, pos); pos += 4;
                int soPos = pos;
                int serialOffset = BitConverter.ToInt32(buf, pos); pos += 4;
                int compCount = BitConverter.ToInt32(buf, pos); pos += 4;
                pos += compCount * 12;
                pos += 4; // ExportFlags
                int netCount = BitConverter.ToInt32(buf, pos); pos += 4;
                pos += netCount * 4;
                pos += 16 + 4; // GUID + PackageFlags

                if (serialSize <= 0) continue;

                int newSo = serialOffset;
                int newSs = serialSize;

                if (modByIdx.TryGetValue(exportNum, out int sizeDelta))
                    newSs += sizeDelta;

                foreach (var (insSo, insSz, insIdx) in sortedInserts)
                {
                    if (insIdx == exportNum) continue;
                    if (serialOffset > insSo)
                        newSo += insSz;
                }

                if (newSo != serialOffset)
                {
                    BitConverter.GetBytes(newSo).CopyTo(buf, soPos);
                    nOff++;
                }
                if (newSs != serialSize)
                {
                    BitConverter.GetBytes(newSs).CopyTo(buf, ssPos);
                    nSz++;
                }
            }
            return (nOff, nSz);
        }

        // Name table reader

        public static List<string> ReadNameTable(byte[] data, PackageHeader hdr)
        {
            var names = new List<string>();
            int pos = hdr.NameOffset;
            for (int i = 0; i < hdr.NameCount; i++)
            {
                int slen = BitConverter.ToInt32(data, pos); pos += 4;
                string name;
                if (slen < 0)
                {
                    int chars = Math.Abs(slen);
                    name = System.Text.Encoding.Unicode.GetString(data, pos, chars * 2).TrimEnd('\0');
                    pos += chars * 2;
                }
                else
                {
                    name = System.Text.Encoding.Latin1.GetString(data, pos, slen).TrimEnd('\0');
                    pos += slen;
                }
                pos += 8; // flags
                names.Add(name);
            }
            return names;
        }
    }
}

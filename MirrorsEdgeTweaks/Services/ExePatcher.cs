using System;
using System.Collections.Generic;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public enum ExePatchState { Unpatched, Patched, Unknown }

    // Bidirectional patcher for MirrorsEdge.exe
    public static class ExePatcher
    {
        static readonly byte[] FLOAT_0_5625 = BitConverter.GetBytes(0.5625f); // 9/16

        const byte JNE = 0x75;
        const byte JMP = 0xEB;

        public static ExePatchState DetectState(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            int site = FindPatchSite(data);
            if (site == -1) return ExePatchState.Unknown;
            if (data[site] == JNE) return ExePatchState.Unpatched;
            if (data[site] == JMP) return ExePatchState.Patched;
            return ExePatchState.Unknown;
        }

        public static void Apply(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            int site = FindPatchSite(data);
            if (site == -1)
                throw new InvalidOperationException("Cannot find render target patch site in executable");
            if (data[site] == JMP) return;
            if (data[site] != JNE)
                throw new InvalidOperationException($"Unexpected byte 0x{data[site]:X2} at patch site (expected JNE 0x75)");
            data[site] = JMP;
            File.WriteAllBytes(exePath, data);
        }

        public static void Remove(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            int site = FindPatchSite(data);
            if (site == -1)
                throw new InvalidOperationException("Cannot find render target patch site in executable");
            if (data[site] == JNE) return;
            if (data[site] != JMP)
                throw new InvalidOperationException($"Unexpected byte 0x{data[site]:X2} at patch site (expected JMP 0xEB)");
            data[site] = JNE;
            File.WriteAllBytes(exePath, data);
        }

        public static void Reconcile(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            int site = FindPatchSite(data);

            if (site != -1)
            {
                if (data[site] == JMP) return;
                if (data[site] != JNE)
                    throw new InvalidOperationException(
                        $"Unexpected byte 0x{data[site]:X2} at patch site (expected JNE 0x75)");
                data[site] = JMP;
                File.WriteAllBytes(exePath, data);
                return;
            }

            ReconcileOoa(exePath, data);
        }

        static void ReconcileOoa(string exePath, byte[] data)
        {
            if (!OoaService.HasOoaSection(data))
                throw new InvalidOperationException(
                    "Cannot find render target patch site in executable.");

            string? dlfPath = OoaService.FindLicensePath(data);
            if (dlfPath == null)
                throw new OoaLicenseNotFoundException(
                    OoaService.GetExpectedLicensePath(data));

            byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
            OoaService.StripAuthenticode(data);
            OoaContext ctx = OoaService.DecryptSections(data, key);

            int site = FindPatchSite(data);
            if (site == -1)
                throw new InvalidOperationException(
                    "Cannot find render target patch site in decrypted EA executable. " +
                    "The executable may be an unsupported version.");

            if (data[site] == JMP)
                return; // Already patched under encryption

            if (data[site] != JNE)
                throw new InvalidOperationException(
                    $"Unexpected byte 0x{data[site]:X2} at patch site in decrypted " +
                    $"EA executable (expected JNE 0x75).");

            data[site] = JMP;
            OoaService.UpdateEncBlockCrcs(data, ctx);
            OoaService.ReencryptSections(data, key, ctx);
            byte[] output = OoaService.TruncateOverlay(data, ctx);
            File.WriteAllBytes(exePath, output);
        }

        // Private

        struct PeSection
        {
            public string Name;
            public uint VA;
            public uint RawSize;
            public uint RawOffset;
        }

        static int FindPatchSite(byte[] data)
        {
            if (data.Length < 2 || data[0] != 0x4D || data[1] != 0x5A) return -1;

            uint peOff = BitConverter.ToUInt32(data, 0x3C);
            if (peOff + 4 > data.Length) return -1;
            if (data[peOff] != 0x50 || data[peOff + 1] != 0x45) return -1;

            uint imageBase = BitConverter.ToUInt32(data, (int)peOff + 0x34);
            ushort numSec = BitConverter.ToUInt16(data, (int)peOff + 6);
            ushort optSz = BitConverter.ToUInt16(data, (int)peOff + 20);
            int secStart = (int)peOff + 24 + optSz;

            var sections = new List<PeSection>();
            for (int s = 0; s < numSec; s++)
            {
                int so = secStart + s * 40;
                string sname = System.Text.Encoding.ASCII.GetString(data, so, 8).TrimEnd('\0');
                uint va = BitConverter.ToUInt32(data, so + 12);
                uint rawSz = BitConverter.ToUInt32(data, so + 16);
                uint rawOff = BitConverter.ToUInt32(data, so + 20);
                sections.Add(new PeSection { Name = sname, VA = va, RawSize = rawSz, RawOffset = rawOff });
            }

            var floatLocs = new List<int>();
            int pos = 0;
            while (true)
            {
                int idx = BytecodeBuilder.FindPattern(data, FLOAT_0_5625, pos);
                if (idx == -1) break;
                floatLocs.Add(idx);
                pos = idx + 1;
            }

            foreach (int floc in floatLocs)
            {
                uint? floatVa = FOffToVA((uint)floc, imageBase, sections);
                if (floatVa == null) continue;

                byte[] vaBytes = BitConverter.GetBytes(floatVa.Value);
                int searchPos = 0;
                while (true)
                {
                    int idx = BytecodeBuilder.FindPattern(data, vaBytes, searchPos);
                    if (idx == -1) break;

                    for (int j = idx - 1; j >= Math.Max(0, idx - 30); j--)
                    {
                        if (data[j] == JNE || data[j] == JMP)
                        {
                            if (FOffToVA((uint)j, imageBase, sections) != null)
                                return j;
                        }
                    }
                    searchPos = idx + 1;
                }
            }

            return -1;
        }

        static uint? FOffToVA(uint foff, uint imageBase, List<PeSection> sections)
        {
            foreach (var s in sections)
            {
                if (foff >= s.RawOffset && foff < s.RawOffset + s.RawSize)
                    return imageBase + s.VA + (foff - s.RawOffset);
            }
            return null;
        }
    }
}

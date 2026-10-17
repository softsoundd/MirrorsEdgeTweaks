using MirrorsEdgeTweaks.Helpers;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    // Fixes the white screen at aspect ratios narrower than 16:9.
    // DICE's FSceneRenderTargets::Allocate replaces the requested height with width*0.5625 unless a global flag is set.
    // Changing the flag test's jne to jmp keeps the requested height.
    public static class RenderTargetPatcher
    {
        const byte JNE = 0x75;
        const byte JMP = 0xEB;

        // These bytes follow the toggled opcode, from its rel8 through the fadd opcode.
        // They are the same on every build and unique in .text, so they match before and after patching.
        static readonly byte[] SiteSuffix = Convert.FromHexString("3E8BC785C089442414DB4424147D06D805");

        public static void Reconcile(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            if (OoaService.HasOoaSection(data))
            {
                bool changed = false;
                byte[] result = PatchUtility.ApplyUnderOoa(data, img => { changed = PatchImage(img); return img; });
                if (changed) PatchUtility.WritePreservingAttributes(exePath, result);
            }
            else if (PatchImage(data))
            {
                PatchUtility.WritePreservingAttributes(exePath, data);
            }
        }

        internal static bool PatchImage(byte[] data)
        {
            var text = PeImageLayout.Parse(data).FindSectionByName(".text")
                ?? throw new InvalidOperationException("No .text section in executable.");
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);

            int suffix = PatternHelper.FindUnique(data, SiteSuffix, start, end);
            if (suffix < 0)
                throw new InvalidOperationException("Cannot find render target patch site in executable.");

            int site = suffix - 1;
            if (data[site] == JMP) return false;
            if (data[site] != JNE)
                throw new InvalidOperationException(
                    $"Unexpected byte 0x{data[site]:X2} at render target patch site (expected JNE 0x75).");
            data[site] = JMP;
            return true;
        }
    }
}

using MirrorsEdgeTweaks.Helpers;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    // Fixes subtitles vanishing at aspect ratios wider than 16:9. UGameViewportClient::Draw and
    // FSubtitleManager::TrimRegionToSafeZone halve the subtitle band's vertical centring offset
    // (SizeY - SizeX*0.5625) with an UNSIGNED shift, so when that offset is negative (AR > 16:9) it
    // wraps to ~2^31 and the float->int conversion sends the band's Y to INT_MIN, off-screen. Per
    // site the fix is shr->sar (E8->F8) plus the guarding jge->jmp (7D->EB) so the unsigned +2^32
    // fixup is skipped and the signed offset is used. The sites are byte-identical across versions
    // bar the wildcarded fadd const
    public static class SubtitleRegionPatcher
    {
        const byte SHR = 0xE8, SAR = 0xF8, JGE = 0x7D, JMP = 0xEB;

        // Patch bytes are wildcarded so one pattern matches whether patched or not. The fixed `7D 06`
        // at offset 14 is the FIRST jge (a legitimate fixup, left alone); the patch point is the
        // SECOND jge.
        static readonly Site DrawSite = new("Draw",
            "2B C6 D1 ?? 85 F6 89 74 24 4C DB 44 24 4C 7D 06 D8 05 ?? ?? ?? ?? 85 C0 D9 5C 24 3C 89 44 24 4C DB 44 24 4C ?? 06 D8 05 ?? ?? ?? ??",
            shrOffset: 3, jgeOffset: 36);
        static readonly Site TrimSite = new("Trim",
            "2B C7 D1 ?? 85 FF 89 7C 24 1C DB 44 24 1C 7D 06 D8 05 ?? ?? ?? ?? 85 C0 89 44 24 1C DB 44 24 1C ?? 06 D8 05 ?? ?? ?? ??",
            shrOffset: 3, jgeOffset: 32);

        public static void Apply(string exePath) => Transform(exePath, patch: true);

        public static void Remove(string exePath) => Transform(exePath, patch: false);

        static void Transform(string exePath, bool patch)
        {
            byte[] data = File.ReadAllBytes(exePath);
            bool changed = false;
            if (OoaService.HasOoaSection(data))
            {
                byte[] result = PatchUtility.ApplyUnderOoa(data, img => { changed = PatchImage(img, patch); return img; });
                if (changed) PatchUtility.WritePreservingAttributes(exePath, result);
            }
            else if (PatchImage(data, patch))
            {
                PatchUtility.WritePreservingAttributes(exePath, data);
            }
        }

        static bool PatchImage(byte[] data, bool patch)
        {
            var text = PeImageLayout.Parse(data).FindSectionByName(".text")
                ?? throw new InvalidOperationException("No .text section in executable.");
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);
            bool drawChanged = ApplySite(data, start, end, DrawSite, patch);
            bool trimChanged = ApplySite(data, start, end, TrimSite, patch);
            return drawChanged || trimChanged;
        }

        static bool ApplySite(byte[] data, int start, int end, Site site, bool patch)
        {
            int pos = site.FindUnique(data, start, end);
            if (pos < 0)
                throw new InvalidOperationException($"Subtitle patch site '{site.Name}' not found or not unique.");

            byte shr = data[pos + site.ShrOffset];
            byte jge = data[pos + site.JgeOffset];
            bool known = (shr == SHR && jge == JGE) || (shr == SAR && jge == JMP);
            if (!known)
                throw new InvalidOperationException(
                    $"Subtitle patch site '{site.Name}' has unexpected bytes (0x{shr:X2}, 0x{jge:X2}).");

            byte wantShr = patch ? SAR : SHR;
            byte wantJge = patch ? JMP : JGE;
            if (shr == wantShr && jge == wantJge) return false;
            data[pos + site.ShrOffset] = wantShr;
            data[pos + site.JgeOffset] = wantJge;
            return true;
        }

        // Masked byte pattern ("??" = wildcard) flagging the two single-byte patch points.
        sealed class Site
        {
            readonly byte[] _bytes;
            readonly bool[] _fixed;

            public string Name { get; }
            public int ShrOffset { get; }
            public int JgeOffset { get; }

            public Site(string name, string spec, int shrOffset, int jgeOffset)
            {
                string[] tokens = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                _bytes = new byte[tokens.Length];
                _fixed = new bool[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i] == "??") continue;
                    _bytes[i] = Convert.ToByte(tokens[i], 16);
                    _fixed[i] = true;
                }
                Name = name;
                ShrOffset = shrOffset;
                JgeOffset = jgeOffset;
            }

            public int FindUnique(byte[] data, int start, int end)
            {
                int found = -1;
                int limit = Math.Min(end, data.Length) - _bytes.Length;
                for (int i = start; i <= limit; i++)
                {
                    bool match = true;
                    for (int j = 0; j < _bytes.Length; j++)
                        if (_fixed[j] && data[i + j] != _bytes[j]) { match = false; break; }
                    if (!match) continue;
                    if (found != -1) return -1;
                    found = i;
                }
                return found;
            }
        }
    }
}

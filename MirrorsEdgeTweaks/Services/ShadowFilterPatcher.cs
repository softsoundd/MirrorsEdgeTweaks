using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Services
{
    // Fixes the uniform PCF that dynamic shadows use while Branching PCF is off.
    // TShadowProjectionPixelShader::SetParameters rotates its sample offsets in pairs,
    // but reads every pair's X values from SampleOffsets[0] and SampleOffsets[1].
    // The 16-sample filter's 4x4 grid therefore collapses into two columns, which offsets each shadow by about a texel.
    // In each iteration, edi points at the pair's second Y, so the fix reads both X values relative to it:
    //   movss xmm1,[esi+58h] becomes movss xmm1,[edi-0Ch]
    //   movss xmm4,[esi+60h] becomes movss xmm4,[edi-4]
    // Each of the five sample policies has its own copy of the loop, with the same bytes on every version.
    // The 4-sample copies read the same values either way, so they are fixed with the rest.
    public static class ShadowFilterPatcher
    {
        // The leading movss xmm0,[edi-8] keeps both patterns unique to the filter loops.
        static readonly byte[] StockLoads = Convert.FromHexString("F30F1047F8F30F104E58F30F106660");
        static readonly byte[] FixedLoads = Convert.FromHexString("F30F1047F8F30F104FF4F30F1067FC");

        const int SiteCount = 5;

        public static void Apply(string exePath) => PatchUtility.RewriteExe(exePath, data => PatchImage(data, patch: true));

        public static void Remove(string exePath) => PatchUtility.RewriteExe(exePath, data => PatchImage(data, patch: false));

        // Returns true when any of the filter loops is fixed.
        // Returns null when the executable does not hold the five filter loops.
        public static bool? IsApplied(string exePath)
        {
            ExeImage exe = GameFileReadScope.ReadExe(exePath);
            return IsAppliedToImage(OoaService.HasOoaSection(exe.Raw) ? exe.GetDecrypted() : exe.Raw);
        }

        // EA images must already be decrypted.
        internal static bool? IsAppliedToImage(byte[] data)
        {
            var (stockSites, fixedSites) = FindSites(data);
            if (stockSites.Count + fixedSites.Count != SiteCount)
                return null;
            return fixedSites.Count > 0;
        }

        // EA images must already be decrypted.
        // Returns null when every site already has the wanted loads.
        internal static byte[]? PatchImage(byte[] data, bool patch)
        {
            var (stockSites, fixedSites) = FindSites(data);
            if (stockSites.Count + fixedSites.Count != SiteCount)
                throw new InvalidOperationException(
                    $"Found {stockSites.Count + fixedSites.Count} shadow filter patch sites in the executable, but expected {SiteCount}.");

            List<int> sites = patch ? stockSites : fixedSites;
            if (sites.Count == 0)
                return null;

            byte[] loads = patch ? FixedLoads : StockLoads;
            foreach (int site in sites)
                loads.CopyTo(data, site);
            return data;
        }

        private static (List<int> Stock, List<int> Fixed) FindSites(byte[] data)
        {
            var text = PeImageLayout.Parse(data).FindSectionByName(".text");
            if (text == null)
                return (new(), new());
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);

            return (PatternHelper.FindAll(data, StockLoads, start, end).ToList(),
                PatternHelper.FindAll(data, FixedLoads, start, end).ToList());
        }
    }
}

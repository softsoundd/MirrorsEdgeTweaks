using MirrorsEdgeTweaks.Services;
using System.IO;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum MultiFontScaleState { Unknown, Unpatched, Patched }

    // Patches the executable for the high-res UI fix.
    // Every UMultiFont text path, drawing and measuring alike, takes its scale from GetScalingFactor,
    // so layout always agrees with the drawn text.
    //
    //   1. GetResolutionTestTableIndex picks the font page.
    //      Stock takes the nearest page at or above the font height test, which is width*0.5625,
    //      and falls back to the 480p page when none qualifies, as happens above 1920 wide.
    //      The replacement falls back to the largest page instead.
    //   2. GetScalingFactor divides the font height test by the chosen page's height.
    //      Above 1920 wide, the replacement clamps the test to the visible 16:9 height,
    //      min(ResY, ResX*0.5625), so text on wider-than-16:9 modes is sized for the window.
    //   3. FUIScreenValue_Extent::GetValue scales the fixed-pixel extents of UIList and UIScrollbar,
    //      such as padding, bar width and arrow buttons, by the same factor above 1920 wide.
    //
    // A fourth, optional hook confines the loading-screen hint to the 16:9 bink region.
    // Plain UFont text, such as the console, keeps its stock scale,
    // because hooks 1 and 2 only replace the UMultiFont overrides.
    public static class MultiFontScalePatcher
    {
        private const float StockAuthoredHeight = 1080f;

        // The cave block ends with these floats, and ApplyToImage addresses each one by its slot.
        private static readonly byte[] CaveConstants = new[] { StockAuthoredHeight, 0.5625f, 0.5f, 0.0001f, float.PositiveInfinity }
            .SelectMany(f => BitConverter.GetBytes(f)).ToArray();

        // ---- UMultiFont::GetResolutionTestTableIndex (font page selection) ----------------------------
        // This function and GetScalingFactor are replaced whole.
        // Their locators wildcard the entry bytes so they still match after the detour.
        // Restoring writes each entry back from its Orig constant, which holds on every build
        // because the entries have no absolute operands.
        // The page locator also wildcards the per-build address of a 0.0001 constant.
        private static readonly BytePattern PageIndexPrefix = BytePattern.Parse(
            "?? ?? ?? ?? ?? ?? ?? ?? DD 05 ?? ?? ?? ?? 55 8B C1 8B 88 54 01 00 00 56 33 ED");
        private const int PageIndexHookOffset = 0;
        private static readonly byte[] PageIndexOrig = { 0x83, 0xEC, 0x10, 0xD9, 0x44, 0x24, 0x14, 0x53 };

        // ---- UMultiFont::GetScalingFactor (text scale) -------------------------------------------------
        private static readonly BytePattern GsfPrefix = BytePattern.Parse(
            "?? ?? ?? ?? ?? ?? ?? 8B 06 8B 90 2C 01 00 00 51 D9 1C 24 FF D2 3B 86 54 01 00 00 7D 11");
        private const int GsfHookOffset = 0;
        private static readonly byte[] GsfOrig = { 0xD9, 0x44, 0x24, 0x04, 0x56, 0x8B, 0xF1 };

        // ---- FUIScreenValue_Extent::GetValue (list and scrollbar pixel extents) -----------------------
        // The locator wildcards the 6 entry bytes, which the cave re-runs before resuming at entry+6.
        private static readonly BytePattern ExtentPrefix = BytePattern.Parse(
            "?? ?? ?? ?? ?? ?? 85 DB 56 8B F1 F3 0F 10 06 57 F3 0F 11 44 24 0C 74 63");
        private const int ExtentHookOffset = 0;
        private static readonly byte[] ExtentDisplaced = { 0x51, 0x53, 0x8B, 0x5C, 0x24, 0x0C };
        // UUIScrollbar is 0x4B0 bytes and UUIList is larger. Both hold all their extents below 0x4B0,
        // so an extent within this span of its owner is one of the owner's own members.
        private const uint ExtentOwnerSpan = 0x4B0;

        // ---- DrawStringWrapped 16:9 confine (loading-screen hint) --------------------------------------
        // The hook sits after the SEH prologue.
        // That prologue matches roughly 70 to 80 MSVC functions, so the locator also pins the body
        // that follows the displaced bytes.
        // Wildcards cover the displaced bytes and the per-build SEH scope-table and cookie addresses.
        private static readonly BytePattern DswConfinePrefix = BytePattern.Parse(
            "68 ?? ?? ?? ?? 64 A1 00 00 00 00 50 83 EC 40 53 55 56 57 A1 ?? ?? ?? ?? " +
            "33 C4 50 8D 44 24 54 64 A3 00 00 00 00 ?? ?? ?? ?? ?? ?? " +
            "3B FE 0F 84 74 04 00 00 8B 44 24 64 8B 48 04 8B");
        private const int DswConfineHookOffset = 37;
        private static readonly byte[] DswConfineOrig = { 0x8B, 0x7C, 0x24, 0x7C, 0x33, 0xF6 }; // mov edi,[esp+0x7c]; xor esi,esi

        // ---- UIList and UIScrollbar vtables ------------------------------------------------------------
        // The game strips MSVC RTTI from UObject classes,
        // so the extent hook reads its gating vtables from their constructors.
        private static readonly BytePattern UiListCtorPattern = BytePattern.Parse(
            "C7 86 DC 03 00 00 ?? ?? ?? ?? C6 44 24 20 01 C7 06 ?? ?? ?? ?? C7 86 DC 03 00 00");
        private const int UiListVtableImmOffset = 17;
        private static readonly BytePattern UiScrollbarCtorPattern = BytePattern.Parse(
            "8D 8E EC 03 00 00 C7 44 24 14 00 00 00 00 C7 06 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8D 8E 0C 04 00 00");
        private const int UiScrollbarVtableImmOffset = 16;

        // These must stay below the byte-array fields the builders read, because static initializers
        // run top to bottom.
        private static readonly int PageIndexCaveLen = BuildPageIndexCave(0, 0, 0).Length;
        private static readonly int GsfCaveLen = BuildGsfCave(0, 0, 0).Length;
        private static readonly int ExtentCaveLen = BuildExtentCave(0, 0, 0, 0, 0, 0, 0).Length;
        private static readonly int DswCaveLen = BuildDswConfineCave(0, 0, 0, 0, 0).Length;

        // The page-selection code opens the cave block, and its first instruction has no absolute operand.
        private static readonly byte[] PageIndexEntry = BuildPageIndexCave(0, 0, 0)[..6];

        public static MultiFontScaleState DetectState(string exePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(exePath);
                if (OoaService.HasOoaSection(data))
                    PatchUtility.DecryptOoaInPlace(data);
                var pe = PeImageLayout.Parse(data);
                if (!TryFindHook(data, pe, PageIndexPrefix, PageIndexHookOffset, out int off, out _))
                    return MultiFontScaleState.Unknown;
                if (data[off] == 0xE9) return MultiFontScaleState.Patched;
                if (IsOriginal(data, off, PageIndexOrig))
                    return MultiFontScaleState.Unpatched;
                return MultiFontScaleState.Unknown;
            }
            catch { return MultiFontScaleState.Unknown; }
        }

        public static bool IsApplied(string exePath) => DetectState(exePath) == MultiFontScaleState.Patched;

        public static void Apply(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            if (OoaService.HasOoaSection(data))
            {
                data = PatchUtility.ApplyUnderOoa(data, img => ApplyToImage(img, "ea"));
            }
            else
            {
                // An unrecognised exe must not be patched with a guessed tag,
                // because later detection and reapply logic relies on the cave version tag.
                string version = ExeVersionDetector.DetectVersion(data, exePath)
                    ?? throw new InvalidOperationException("Unknown or unsupported MirrorsEdge.exe version. Cannot apply the high-res UI fix.");
                data = ApplyToImage(data, version);
            }
            PatchUtility.WritePreservingAttributes(exePath, data);
        }

        internal static byte[] ApplyToImage(byte[] data, string versionTag)
        {
            if (LegacyChangeDetector.HasBetaHighResHook(data))
                throw LegacyChangeDetector.ExeVerificationRequired();

            var pe = PeImageLayout.Parse(data);

            uint gssResYVa = FindGssResYVa(data, pe)
                ?? throw new InvalidOperationException("GSystemSettings.ResY not found.");

            if (!TryFindHook(data, pe, PageIndexPrefix, PageIndexHookOffset, out int pageOff, out uint pageVa))
                throw new InvalidOperationException("Font page-selection hook site not found.");
            if (!TryFindHook(data, pe, GsfPrefix, GsfHookOffset, out int gsfOff, out uint gsfVa))
                throw new InvalidOperationException("Font scaling-factor hook site not found.");

            // On re-apply, the page-selection detour targets the existing cave base.
            uint? priorCaveBase = DetourTarget(data, pageOff, pageVa);

            RestoreIfDetoured(data, pageOff, PageIndexOrig);
            RestoreIfDetoured(data, gsfOff, GsfOrig);
            if (!IsOriginal(data, pageOff, PageIndexOrig))
                throw new InvalidOperationException($"Unexpected bytes at font page-selection hook 0x{pageVa:X8}.");
            if (!IsOriginal(data, gsfOff, GsfOrig))
                throw new InvalidOperationException($"Unexpected bytes at font scaling-factor hook 0x{gsfVa:X8}.");
            OptionalHooks optional = RestoreOptionalHooks(data, pe, out _);

            var cave = CaveSection.Open(data, versionTag: versionTag);

            // Re-apply overwrites the prior block in place, so the cave never grows and blocks above it stay intact.
            // This relies on the Alloc sequence below being the same on every apply.
            // Without a detour, the prior block is one that Remove could not reclaim.
            int? priorOffset = priorCaveBase != null
                ? (int)(priorCaveBase.Value - cave.SectionVa)
                : FindLeftoverBlock(cave, CaveFootprint(optional.DoExtent, optional.DoDsw));
            if (priorOffset != null)
                cave.ReuseFrom(priorOffset.Value);

            // The code blocks come first, so the page-selection block is the cave base.
            uint pageCodeVa = cave.Alloc(PageIndexCaveLen, 16);
            uint gsfCodeVa = cave.Alloc(GsfCaveLen, 16);
            uint extCodeVa = optional.DoExtent ? cave.Alloc(ExtentCaveLen, 16) : 0u;
            uint dswCodeVa = optional.DoDsw ? cave.Alloc(DswCaveLen, 16) : 0u;
            uint constVa = cave.Alloc(CaveConstants.Length, 4);
            uint c1080Va = constVa, c5625Va = constVa + 4, cHalfVa = constVa + 8;
            uint cEpsVa = constVa + 12, cInfVa = constVa + 16;

            cave.Write(constVa, CaveConstants);
            cave.Write(pageCodeVa, BuildPageIndexCave(pageCodeVa, cEpsVa, cInfVa));
            cave.Write(gsfCodeVa, BuildGsfCave(gsfCodeVa, gssResYVa, c5625Va));
            if (optional.DoExtent)
                cave.Write(extCodeVa, BuildExtentCave(
                    extCodeVa, gssResYVa, c5625Va, c1080Va, optional.UiListVt, optional.ScrollbarVt,
                    optional.ExtVa + (uint)ExtentDisplaced.Length));
            if (optional.DoDsw)
                cave.Write(dswCodeVa, BuildDswConfineCave(
                    dswCodeVa, gssResYVa, c5625Va, cHalfVa, optional.DswVa + (uint)DswConfineOrig.Length));
            data = cave.Finalize();

            var pe2 = PeImageLayout.Parse(data);
            if (!TryFindHook(data, pe2, PageIndexPrefix, PageIndexHookOffset, out pageOff, out pageVa) ||
                !TryFindHook(data, pe2, GsfPrefix, GsfHookOffset, out gsfOff, out gsfVa))
                throw new InvalidOperationException("Hook site vanished after cave finalize.");

            WriteDetour(data, pageOff, pageVa, pageCodeVa, PageIndexOrig.Length);
            WriteDetour(data, gsfOff, gsfVa, gsfCodeVa, GsfOrig.Length);
            if (optional.DoExtent && TryFindHook(data, pe2, ExtentPrefix, ExtentHookOffset, out int extOff, out uint extVa))
                WriteDetour(data, extOff, extVa, extCodeVa, ExtentDisplaced.Length);
            if (optional.DoDsw && TryFindHook(data, pe2, DswConfinePrefix, DswConfineHookOffset, out int dswOff, out uint dswVa))
                WriteDetour(data, dswOff, dswVa, dswCodeVa, DswConfineOrig.Length);

            return data;
        }

        public static void Remove(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            if (OoaService.HasOoaSection(data))
            {
                // For EA, decrypt, restore the detours, then re-encrypt and trim. Rewrite only when a
                // detour was present, since re-encrypting an already-clean image strips its signature.
                bool eaChanged = false;
                byte[] result;
                try
                {
                    result = PatchUtility.ApplyUnderOoa(data, img =>
                    {
                        img = RemoveFromImage(img, out eaChanged);
                        return img;
                    });
                }
                catch (OoaLicenseNotFoundException) { return; }
                if (eaChanged)
                    PatchUtility.WritePreservingAttributes(exePath, result);
                return;
            }

            data = RemoveFromImage(data, out bool changed);
            if (changed)
                PatchUtility.WritePreservingAttributes(exePath, data);
        }

        // Restores every detour, and reclaims the cave when no other patch was allocated above it,
        // so a later apply reuses the space. EA images must already be decrypted.
        internal static byte[] RemoveFromImage(byte[] data, out bool changed)
        {
            var pe = PeImageLayout.Parse(data);
            changed = false;

            uint? caveBase = null;
            if (TryFindHook(data, pe, PageIndexPrefix, PageIndexHookOffset, out int pageOff, out uint pageVa))
            {
                caveBase = DetourTarget(data, pageOff, pageVa);
                changed |= RestoreIfDetoured(data, pageOff, PageIndexOrig);
            }
            if (TryFindHook(data, pe, GsfPrefix, GsfHookOffset, out int gsfOff, out _))
                changed |= RestoreIfDetoured(data, gsfOff, GsfOrig);
            OptionalHooks optional = RestoreOptionalHooks(data, pe, out bool optionalChanged);
            changed |= optionalChanged;

            if (caveBase != null)
                data = CaveSection.ReclaimIfTopmost(data, caveBase.Value,
                    CaveFootprint(optional.DoExtent, optional.DoDsw));

            return data;
        }

        private sealed class OptionalHooks
        {
            public bool DoExtent;
            public int ExtOff;
            public uint ExtVa;
            public uint UiListVt;
            public uint ScrollbarVt;
            public bool DoDsw;
            public int DswOff;
            public uint DswVa;
        }

        // Finds the optional hooks and restores any detour on them.
        // Apply and Remove both use this, so Remove reclaims exactly the blocks that Apply allocated.
        // A hook is skipped when its site is missing or holds unexpected bytes.
        // The extent hook is also skipped when either gating vtable is missing.
        private static OptionalHooks RestoreOptionalHooks(byte[] data, PeImageLayout pe, out bool changed)
        {
            var hooks = new OptionalHooks();
            changed = false;

            uint? uiListVt = FindVtableByCtor(data, pe, UiListCtorPattern, UiListVtableImmOffset);
            uint? scrollbarVt = FindVtableByCtor(data, pe, UiScrollbarCtorPattern, UiScrollbarVtableImmOffset);
            if (uiListVt.HasValue && scrollbarVt.HasValue
                && TryFindHook(data, pe, ExtentPrefix, ExtentHookOffset, out hooks.ExtOff, out hooks.ExtVa))
            {
                changed |= RestoreIfDetoured(data, hooks.ExtOff, ExtentDisplaced);
                hooks.DoExtent = IsOriginal(data, hooks.ExtOff, ExtentDisplaced);
                hooks.UiListVt = uiListVt.Value;
                hooks.ScrollbarVt = scrollbarVt.Value;
            }

            if (TryFindHook(data, pe, DswConfinePrefix, DswConfineHookOffset, out hooks.DswOff, out hooks.DswVa))
            {
                changed |= RestoreIfDetoured(data, hooks.DswOff, DswConfineOrig);
                hooks.DoDsw = IsOriginal(data, hooks.DswOff, DswConfineOrig);
            }

            return hooks;
        }

        // Returns the cave size for the given optional hooks by mirroring ApplyToImage's Alloc sequence.
        // The first block is 16-aligned, so the size does not depend on the cave base.
        private static int CaveFootprint(bool doExtent, bool doDsw)
        {
            static int A16(int v) => (v + 15) & ~15;
            static int A4(int v) => (v + 3) & ~3;
            int wm = PageIndexCaveLen;                 // page selection
            wm = A16(wm) + GsfCaveLen;                 // scaling factor
            if (doExtent) wm = A16(wm) + ExtentCaveLen;
            if (doDsw) wm = A16(wm) + DswCaveLen;
            return A4(wm) + CaveConstants.Length;      // constants
        }

        // Returns the section offset of a block that Remove could not reclaim and that matches the footprint.
        // Returns null when there is none.
        private static int? FindLeftoverBlock(CaveSection cave, int footprint)
        {
            int constants = cave.IndexOf(CaveConstants);
            int start = constants + CaveConstants.Length - footprint;
            if (constants < 0 || start < 0) return null;
            return cave.IndexOf(PageIndexEntry, start) == start ? start : null;
        }

        // Builds the GetResolutionTestTableIndex replacement (thiscall, ret 4, index in eax).
        // It returns the index of the nearest entry at or above H - eps, which matches stock.
        // It returns the index of the largest entry when none qualifies, where stock returns 0.
        // Qualifying entries lie at most eps below H, so ranking them by v - H matches stock's |v - H|.
        // ecx = this, with the entry count at [this+0x154] and the entries at [this+0x150].
        internal static byte[] BuildPageIndexCave(uint codeVa, uint cEpsVa, uint cInfVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            mc.Emit(new byte[] { 0x8B, 0x81, 0x54, 0x01, 0x00, 0x00 });  // mov eax,[ecx+0x154]  num
            mc.Emit(new byte[] { 0x85, 0xC0 });                          // test eax,eax
            mc.EmitJle("ret0");
            mc.Emit((byte)0x53);                                         // push ebx
            mc.Emit((byte)0x57);                                         // push edi
            mc.Emit(new byte[] { 0x8B, 0xD8 });                          // mov ebx,eax          num
            mc.Emit(new byte[] { 0x8B, 0x91, 0x50, 0x01, 0x00, 0x00 });  // mov edx,[ecx+0x150]  data
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x44, 0x24, 0x0C });  // movss xmm0,[esp+0xC] H
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0xC8 });              // movss xmm1,xmm0
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5C, 0x0D }); mc.EmitUInt32(cEpsVa); // subss xmm1,[eps]     H-eps
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x15 }); mc.EmitUInt32(cInfVa); // movss xmm2,[+inf]    bestdiff
            mc.Emit(new byte[] { 0xB8, 0xFF, 0xFF, 0xFF, 0xFF });        // mov eax,-1           best
            mc.Emit(new byte[] { 0x33, 0xFF });                          // xor edi,edi          maxidx
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x1A });              // movss xmm3,[edx]     maxval=data[0]
            mc.Emit(new byte[] { 0x33, 0xC9 });                          // xor ecx,ecx          i
            mc.MarkLabel("loop");
            mc.Emit(new byte[] { 0x3B, 0xCB });                          // cmp ecx,ebx
            mc.EmitJge("done");
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x24, 0x8A });        // movss xmm4,[edx+ecx*4] v
            mc.Emit(new byte[] { 0x0F, 0x2F, 0xE3 });                    // comiss xmm4,xmm3     v vs maxval
            mc.EmitJbe("skipmax");
            mc.Emit(new byte[] { 0x0F, 0x28, 0xDC });                    // movaps xmm3,xmm4     maxval=v
            mc.Emit(new byte[] { 0x8B, 0xF9 });                          // mov edi,ecx          maxidx=i
            mc.MarkLabel("skipmax");
            mc.Emit(new byte[] { 0x0F, 0x2F, 0xE1 });                    // comiss xmm4,xmm1     v vs H-eps
            mc.EmitJb("next");                                           //                      skip v below H-eps
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5C, 0xE0 });              // subss xmm4,xmm0      diff=v-H
            mc.Emit(new byte[] { 0x0F, 0x2F, 0xE2 });                    // comiss xmm4,xmm2     diff vs bestdiff
            mc.EmitJae("next");                                          //                      skip unless diff is smaller
            mc.Emit(new byte[] { 0x0F, 0x28, 0xD4 });                    // movaps xmm2,xmm4     bestdiff=diff
            mc.Emit(new byte[] { 0x8B, 0xC1 });                          // mov eax,ecx          best=i
            mc.MarkLabel("next");
            mc.Emit((byte)0x41);                                         // inc ecx
            mc.EmitJmp("loop");
            mc.MarkLabel("done");
            mc.Emit(new byte[] { 0x83, 0xF8, 0xFF });                    // cmp eax,-1
            mc.EmitJnz("have");
            mc.Emit(new byte[] { 0x8B, 0xC7 });                          // mov eax,edi          best=maxidx
            mc.MarkLabel("have");
            mc.Emit((byte)0x5F);                                         // pop edi
            mc.Emit((byte)0x5B);                                         // pop ebx
            mc.Emit(new byte[] { 0xC2, 0x04, 0x00 });                    // ret 4
            mc.MarkLabel("ret0");
            mc.Emit(new byte[] { 0x33, 0xC0 });                          // xor eax,eax
            mc.Emit(new byte[] { 0xC2, 0x04, 0x00 });                    // ret 4
            return mc.Build();
        }

        // Builds the GetScalingFactor replacement (thiscall, ret 4, scale in st0).
        // It returns min(H, ResY, ResX*0.5625)/RTT[idx] above 1920 wide.
        // It returns the stock H/RTT[idx] at 1920 wide and below, or when ResY is 0.
        // It returns 1.0 when idx is out of range, as stock does.
        // The ResY and ResX terms bound text to the visible 16:9 height.
        // The H term keeps canvases smaller than the screen at their stock scale.
        // It calls GetResolutionTestTableIndex through vtable slot +0x12C, which reaches the replacement.
        internal static byte[] BuildGsfCave(uint codeVa, uint gssResYVa, uint c5625Va)
        {
            uint resXVa = gssResYVa - 4;
            var mc = new MachineCodeBuilder(codeVa);
            mc.Emit((byte)0x56);                                         // push esi
            mc.Emit(new byte[] { 0x8B, 0xF1 });                          // mov esi,ecx          this
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x08 });              // push dword [esp+8]   H
            mc.Emit(new byte[] { 0x8B, 0x06 });                          // mov eax,[esi]        vtable
            mc.Emit(new byte[] { 0x8B, 0x80, 0x2C, 0x01, 0x00, 0x00 });  // mov eax,[eax+0x12c]  GetResolutionTestTableIndex
            mc.Emit(new byte[] { 0x8B, 0xCE });                          // mov ecx,esi
            mc.Emit(new byte[] { 0xFF, 0xD0 });                          // call eax             eax=idx
            mc.Emit(new byte[] { 0x3B, 0x86, 0x54, 0x01, 0x00, 0x00 });  // cmp eax,[esi+0x154]  num
            mc.EmitJge("ret1");
            mc.Emit(new byte[] { 0x8B, 0x8E, 0x50, 0x01, 0x00, 0x00 });  // mov ecx,[esi+0x150]  data
            mc.Emit(new byte[] { 0xD9, 0x44, 0x24, 0x08 });              // fld dword [esp+8]    st0=H
            mc.Emit(new byte[] { 0x81, 0x3D }); mc.EmitUInt32(resXVa); mc.EmitUInt32(1920); // cmp dword [ResX],1920
            mc.EmitJle("div");
            mc.Emit(new byte[] { 0x83, 0x3D }); mc.EmitUInt32(gssResYVa); mc.Emit((byte)0x00); // cmp dword [ResY],0
            mc.EmitJz("div");
            mc.Emit(new byte[] { 0xDB, 0x05 }); mc.EmitUInt32(resXVa);    // fild dword [ResX]    st0=ResX,st1=H
            mc.Emit(new byte[] { 0xD8, 0x0D }); mc.EmitUInt32(c5625Va);   // fmul dword [0.5625]  st0=render,st1=H
            mc.Emit(new byte[] { 0xDB, 0xF1 });                          // fcomi st0,st1
            mc.Emit(new byte[] { 0xDB, 0xC1 });                          // fcmovnb st0,st1      st0=min(render,H),st1=H
            mc.Emit(new byte[] { 0xDD, 0xD9 });                          // fstp st1             st0=min(render,H)
            mc.Emit(new byte[] { 0xDB, 0x05 }); mc.EmitUInt32(gssResYVa); // fild dword [ResY]    st0=ResY,st1=min
            mc.Emit(new byte[] { 0xDB, 0xF1 });                          // fcomi st0,st1
            mc.Emit(new byte[] { 0xDB, 0xC1 });                          // fcmovnb st0,st1      st0=min(ResY,render,H),st1=min
            mc.Emit(new byte[] { 0xDD, 0xD9 });                          // fstp st1             st0=numerator
            mc.MarkLabel("div");
            mc.Emit(new byte[] { 0xD8, 0x34, 0x81 });                    // fdiv dword [ecx+eax*4]  st0=numerator/RTT[idx]
            mc.Emit((byte)0x5E);                                         // pop esi
            mc.Emit(new byte[] { 0xC2, 0x04, 0x00 });                    // ret 4
            mc.MarkLabel("ret1");
            mc.Emit(new byte[] { 0xD9, 0xE8 });                          // fld1
            mc.Emit((byte)0x5E);                                         // pop esi
            mc.Emit(new byte[] { 0xC2, 0x04, 0x00 });                    // ret 4
            return mc.Build();
        }

        // Builds the FUIScreenValue_Extent::GetValue hook (thiscall, ret 8, value in st0).
        // Above 1920 wide, it returns value*min(ResY, ResX*0.5625)/1080
        // for a Pixels extent requested as Pixels, when it is a member of a UIList or UIScrollbar.
        // It runs the displaced entry bytes and resumes the stock body in every other case.
        // ecx = extent, with the value at +0 and the scale type at +4.
        // [esp+4] = owner and [esp+8] = requested output type.
        // The owner-range check rejects CellSize templates,
        // which pass the list as their owner but live in heap arrays.
        internal static byte[] BuildExtentCave(uint codeVa, uint gssResYVa, uint c5625Va, uint c1080Va,
            uint uiListVt, uint scrollbarVt, uint returnVa)
        {
            uint resXVa = gssResYVa - 4;
            var mc = new MachineCodeBuilder(codeVa);
            mc.Emit(new byte[] { 0x81, 0x3D }); mc.EmitUInt32(resXVa); mc.EmitUInt32(1920); // cmp dword [ResX],1920
            mc.EmitJle("keep");
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x04 });              // mov eax,[esp+4]      owner
            mc.Emit(new byte[] { 0x85, 0xC0 });                          // test eax,eax
            mc.EmitJz("keep");
            mc.Emit(new byte[] { 0x80, 0x79, 0x04, 0x00 });              // cmp byte [ecx+4],0   extent is Pixels?
            mc.EmitJnz("keep");
            mc.Emit(new byte[] { 0x83, 0x7C, 0x24, 0x08, 0x00 });        // cmp dword [esp+8],0  output is Pixels?
            mc.EmitJnz("keep");
            mc.Emit(new byte[] { 0x3B, 0xC8 });                          // cmp ecx,eax          extent >= owner?
            mc.EmitJb("keep");
            mc.Emit(new byte[] { 0x8D, 0x90 }); mc.EmitUInt32(ExtentOwnerSpan); // lea edx,[eax+span]
            mc.Emit(new byte[] { 0x3B, 0xCA });                          // cmp ecx,edx          extent < owner+span?
            mc.EmitJae("keep");
            mc.Emit(new byte[] { 0x8B, 0x10 });                          // mov edx,[eax]        owner vtable
            mc.Emit(new byte[] { 0x81, 0xFA }); mc.EmitUInt32(uiListVt); // cmp edx,UIList vtable
            mc.EmitJz("scale");
            mc.Emit(new byte[] { 0x81, 0xFA }); mc.EmitUInt32(scrollbarVt); // cmp edx,UIScrollbar vtable
            mc.EmitJnz("keep");
            mc.MarkLabel("scale");
            mc.Emit(new byte[] { 0xDB, 0x05 }); mc.EmitUInt32(gssResYVa); // fild dword [ResY]    st0=ResY
            mc.Emit(new byte[] { 0xDB, 0x05 }); mc.EmitUInt32(resXVa);    // fild dword [ResX]    st0=ResX,st1=ResY
            mc.Emit(new byte[] { 0xD8, 0x0D }); mc.EmitUInt32(c5625Va);   // fmul dword [0.5625]  st0=render,st1=ResY
            mc.Emit(new byte[] { 0xDB, 0xF1 });                          // fcomi st0,st1
            mc.Emit(new byte[] { 0xDB, 0xC1 });                          // fcmovnb st0,st1      st0=min(render,ResY),st1=ResY
            mc.Emit(new byte[] { 0xDD, 0xD9 });                          // fstp st1             st0=min(render,ResY)
            mc.Emit(new byte[] { 0xD8, 0x35 }); mc.EmitUInt32(c1080Va);   // fdiv dword [1080]    st0=scale
            mc.Emit(new byte[] { 0xD8, 0x09 });                          // fmul dword [ecx]     st0=value*scale
            mc.Emit(new byte[] { 0xC2, 0x08, 0x00 });                    // ret 8
            mc.MarkLabel("keep");
            mc.Emit(ExtentDisplaced);                                    // push ecx; push ebx; mov ebx,[esp+0xC]
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // Builds the DrawStringWrapped hook. It runs after the SEH prologue, where esp = entry-0x60,
        // and before the body touches xmm.
        // TdGameEngine.OnLoadLevel places the loading hint with fullscreen fractions, X=0.35 and
        // WrapWidth=0.55, but the bink is always 16:9, so the hint overruns it on wider modes.
        // The cave remaps CurX ([esp+0x6c]) and the wrap width *XL ([esp+0x74]) into the 16:9 region:
        //   binkWidth = min(ResX, ResY*16/9), binkFrac = binkWidth/ResX, binkLeft = (ResX-binkWidth)/2
        //   CurX = binkLeft + CurX*binkFrac, *XL *= binkFrac
        // CurX is a value parameter and *XL is a fresh local on every render,
        // so the remap never accumulates. It is a no-op at 16:9 and narrower.
        internal static byte[] BuildDswConfineCave(uint codeVa, uint gssResYVa, uint c5625Va, uint cHalfVa, uint returnVa)
        {
            uint resXVa = gssResYVa - 4;
            var mc = new MachineCodeBuilder(codeVa);
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2A, 0x05 }); mc.EmitUInt32(resXVa);    // cvtsi2ss xmm0,[ResX]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2A, 0x0D }); mc.EmitUInt32(gssResYVa); // cvtsi2ss xmm1,[ResY]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5E, 0x0D }); mc.EmitUInt32(c5625Va);   // divss xmm1,[0.5625] -> ResY*16/9
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5D, 0xC8 });                          // minss xmm1,xmm0     -> binkWidth
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0xD1 });                          // movss xmm2,xmm1
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5E, 0xD0 });                          // divss xmm2,xmm0     -> binkFrac
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0xD8 });                          // movss xmm3,xmm0
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5C, 0xD9 });                          // subss xmm3,xmm1     -> ResX-binkWidth
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x59, 0x1D }); mc.EmitUInt32(cHalfVa);   // mulss xmm3,[0.5]    -> binkLeft
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x64, 0x24, 0x6C });              // movss xmm4,[esp+0x6c]  CurX
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x59, 0xE2 });                          // mulss xmm4,xmm2     -> CurX*binkFrac
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x58, 0xE3 });                          // addss xmm4,xmm3     -> +binkLeft
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x64, 0x24, 0x6C });              // movss [esp+0x6c],xmm4
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x74 });                         // mov eax,[esp+0x74]  XL ptr
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x28 });                          // movss xmm5,[eax]    *XL
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x59, 0xEA });                          // mulss xmm5,xmm2     -> *XL*binkFrac
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x28 });                          // movss [eax],xmm5    *XL=confined
            mc.Emit(new byte[] { 0x8B, 0x7C, 0x24, 0x7C });                         // mov edi,[esp+0x7c]  (displaced)
            mc.Emit(new byte[] { 0x33, 0xF6 });                                     // xor esi,esi         (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // Returns the VA of GSystemSettings.ResY, which every resolution change writes.
        // The caves use it instead of the render height, which ME locks to width*0.5625,
        // so 2560x1080 renders 1440 tall and the render height would over-scale text.
        // The fields are located through UGameEngine::Init's Parse(appCmdLine(), TEXT("ResX="), ResX)
        // and the matching ResY call. ResY must sit 4 bytes after ResX.
        private static uint? FindGssResYVa(byte[] data, PeImageLayout pe)
        {
            uint? resx = FindParseTargetAddr(data, pe, "ResX=");
            uint? resy = FindParseTargetAddr(data, pe, "ResY=");
            if (resx.HasValue && resy.HasValue && resy.Value == resx.Value + 4)
                return resy.Value;
            return null;
        }

        // Returns the field address pushed just before the match string's address in a
        // Parse(..., TEXT(match), GSystemSettings.<field>) call.
        // Returns null when a copy of the string is pushed more than once, or when copies disagree.
        private static uint? FindParseTargetAddr(byte[] data, PeImageLayout pe, string match)
        {
            var text = pe.FindSectionByName(".text");
            if (text == null) return null;
            int tstart = (int)text.PointerToRawData;
            int tend = Math.Min(tstart + (int)text.SizeOfRawData, data.Length);

            byte[] needle = Encoding.Unicode.GetBytes(match + "\0"); // UTF-16LE literal incl. terminator
            uint? result = null;
            int scan = 0;
            while (true)
            {
                int strOff = IndexOf(data, needle, scan, data.Length);
                if (strOff < 0) break;
                scan = strOff + 1;

                uint strVa;
                try { strVa = pe.OffsetToVa(strOff); } catch { continue; }

                byte[] pushStr = new byte[5];
                pushStr[0] = 0x68;                                  // push imm32 (the string VA)
                BitConverter.GetBytes(strVa).CopyTo(pushStr, 1);
                int pOff = IndexOf(data, pushStr, tstart, tend);
                if (pOff < 0) continue;
                if (IndexOf(data, pushStr, pOff + 1, tend) >= 0) return null; // ambiguous

                int immOff = pOff - 5;                              // preceding `push <&field>`
                if (immOff < tstart || data[immOff] != 0x68) continue;
                uint addr = BitConverter.ToUInt32(data, immOff + 1);
                if (result.HasValue && result.Value != addr) return null; // conflicting
                result = addr;
            }
            return result;
        }

        // Returns the vtable VA that a class's unique constructor writes with mov dword [reg], imm32.
        // Returns null when the pattern is missing or ambiguous.
        // Returns null when the immediate is not in a data section, since a vtable is never code.
        private static uint? FindVtableByCtor(byte[] data, PeImageLayout pe, BytePattern ctor, int immOffset)
        {
            int p = ctor.FindUniqueInText(data, pe);
            if (p < 0) return null;
            if (p + immOffset + 4 > data.Length) return null;
            uint vt = BitConverter.ToUInt32(data, p + immOffset);
            try
            {
                _ = pe.VaToOffset(vt);
                if (pe.IsExecutableVa(vt)) return null;
            }
            catch { return null; }
            return vt;
        }

        private static void WriteDetour(byte[] data, int hookOff, uint hookVa, uint codeVa, int origLen)
        {
            byte[] detour = new byte[origLen];
            detour[0] = 0xE9;
            Buffer.BlockCopy(MachineCodeBuilder.Rel32Bytes(hookVa, codeVa), 0, detour, 1, 4);
            for (int i = 5; i < origLen; i++) detour[i] = 0x90; // pad remaining bytes with nop
            Buffer.BlockCopy(detour, 0, data, hookOff, origLen);
        }

        private static bool RestoreIfDetoured(byte[] data, int hookOff, byte[] orig)
        {
            if (data[hookOff] != 0xE9) return false;
            Buffer.BlockCopy(orig, 0, data, hookOff, orig.Length);
            return true;
        }

        // Returns the target of the detour at hookOff.
        // Returns null when the site holds no detour.
        private static uint? DetourTarget(byte[] data, int hookOff, uint hookVa) =>
            data[hookOff] == 0xE9 ? hookVa + 5u + (uint)BitConverter.ToInt32(data, hookOff + 1) : null;

        private static bool IsOriginal(byte[] data, int hookOff, byte[] orig) =>
            data.AsSpan(hookOff, orig.Length).SequenceEqual(orig);

        private static bool TryFindHook(byte[] data, PeImageLayout pe, BytePattern prefix, int hookOffset,
            out int hookOff, out uint hookVa)
        {
            hookOff = 0; hookVa = 0;
            int p = prefix.FindUniqueInText(data, pe);
            if (p < 0) return false;

            hookOff = p + hookOffset;
            hookVa = pe.OffsetToVa(hookOff);
            return true;
        }

        private static int IndexOf(byte[] data, byte[] pat, int start, int end)
            => ((BytePattern)pat).IndexOf(data, start, end);
    }
}

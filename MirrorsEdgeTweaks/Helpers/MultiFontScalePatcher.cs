using MirrorsEdgeTweaks.Services;
using System.IO;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum MultiFontScaleState { Unknown, Unpatched, Patched }

    // Canvas/HUD text size fix (exe) layered on top of the script package lie+UIStyle UI fix.
    //
    // The lie+UIStyle fix makes UI scene text crisp/correct but leaves canvas/HUD text small because
    // HUD text has no UIStyle equivalent: with the lied table its GetScalingFactor returns approx 1.0. Each
    // canvas text function is hooked right after its GetScalingFactor call and, only for UMultiFont text,
    // the lied FontScale is replaced/scaled by the EmitBoost factor so HUD text is sized like the UI text.
    //
    // EmitBoost uses GSystemSettings.ResY (the requested window height, not the rendertarget height) medge
    // 16:9 locks the render to the width so on non-16:9 modes the render buffer is taller than the window
    // and scaling by it would oversize text. Its denominator (the font page the lie selects) makes the
    // boost match the bytecode UIStyle scale at every aspect and height
    //
    // All paths gate on the font being a UMultiFont (vtable slot 0x118 == UMultiFont::GetScalingFactor),
    // so the console and engine debug overlays (plain UFont) stay stock - the DrawString UI path also
    // boosts underscaled UMultiFont UI text (list cells, which ignore UIStyle_Text.Scale).
    public static class MultiFontScalePatcher
    {
        private const float StockAuthoredHeight = 1080f;

        private static readonly int DsCaveLen = BuildDrawStringCave(0, 0, 0, 0, 0, 0).Length;
        private static readonly int WpCaveLen = BuildWrappedPrintCave(0, 0, 0, 0, 0, 0).Length;
        private static readonly int DscCaveLen = BuildDrawStringCenteredCave(0, 0, 0, 0, 0, 0).Length;
        private static readonly int WsCaveLen = BuildWrapStringCave(0, 0, 0, 0, 0, 0).Length;
        private static readonly int DswCaveLen = BuildDrawStringWrappedCave(0, 0, 0, 0, 0, 0).Length;
        private static readonly int DswcCaveLen = BuildDswConfineCave(0, 0, 0, 0, 0).Length;
        private static readonly int DssCaveLen = BuildSubtitleSpacingCave(0, 0, 0, 0, 0, 0).Length;
        private static readonly int TsCaveLen = BuildTextSizeCave(0, 0, 0, 0, 0, 0, 0).Length;

        // ---- DrawString hook --------------------------------------------------------------------------
        // 16-byte prefix ending at the GetScalingFactor call; hook = +16 (relocates fld [ebp+0x20];
        // mov eax,[ebp+0x1C]).
        private static readonly byte[] DrawStringPrefix =
        {
            0x8B, 0x06, 0x8B, 0x90, 0x18, 0x01, 0x00, 0x00,
            0x51, 0x8B, 0xCE, 0xD9, 0x1C, 0x24, 0xFF, 0xD2,
        };
        private const int DrawStringHookOffset = 16;
        private static readonly byte[] DrawStringOrig = { 0xD9, 0x45, 0x20, 0x8B, 0x45, 0x1C };

        // ---- WrappedPrint hook ------------------------------------------------------------------------
        // 24-byte prefix (incl. fld [esp+0x40]; mov [esp+0x38],eax for uniqueness) ending at the
        // GetScalingFactor call; hook = +24 (relocates fld [esp+0x90]).
        private static readonly byte[] WrappedPrintPrefix =
        {
            0xD9, 0x44, 0x24, 0x40, 0x89, 0x44, 0x24, 0x38,
            0x8B, 0x07, 0x8B, 0x90, 0x18, 0x01, 0x00, 0x00,
            0x51, 0x8B, 0xCF, 0xD9, 0x1C, 0x24, 0xFF, 0xD2,
        };
        private const int WrappedPrintHookOffset = 24;
        private static readonly byte[] WrappedPrintOrig = { 0xD9, 0x84, 0x24, 0x90, 0x00, 0x00, 0x00 };

        // ---- DrawStringCentered hook (subtitles) ------------------------------------------------------
        // DrawStringCentered does: StringSize(Font,&XL,&YL,Text); DrawString(Canvas, StartX-(XL/2), ...).
        // The measure (XL) is at the lied 1.0 scale but the draw is upsized, so it miscentres. We scale
        // XL by the boost (same as the draw) before the StartX-(XL/2). 14byte prefix sits before the
        // hook so it still matches after the detour is written (fld1; fst [esp+4]; fstp [esp];
        // fld [esp+0x30]; push eax); hook = +14 (relocates mov eax,[esp+0x20]; cdq).
        private static readonly byte[] DrawStringCenteredPrefix =
        {
            0xD9, 0xE8, 0xD9, 0x54, 0x24, 0x04, 0xD9, 0x1C, 0x24, 0xD9, 0x44, 0x24, 0x30, 0x50,
        };
        private const int DrawStringCenteredHookOffset = 14;
        private static readonly byte[] DrawStringCenteredOrig = { 0x8B, 0x44, 0x24, 0x20, 0x99 };

        // ---- WrapString hook (subtitle / loading-hint word wrap) --------------------------------------
        // UUIString::WrapString wraps to Parameters.DrawXL using ScaleX = Parameters.Scaling.X * FontScale.
        // For canvas callers (ViewportHeight==0, Scaling==1) FontScale is the lied 1.0 so it wraps at the
        // wrong (small) scale while the draw is upsized -> text overruns before wrapping. We multiply
        // FontScale by the boost for VH==0 (canvas) UMultiFont text so wrapping matches the draw - UI
        // callers (VH>0) are untouched. 18-byte prefix ends at the GetScalingFactor call; hook = +18
        // (relocates movss xmm0,[edi+0x10]).
        private static readonly byte[] WrapStringPrefix =
        {
            0x89, 0x44, 0x24, 0x78, 0x8B, 0x01, 0x8B, 0x90, 0x18, 0x01, 0x00, 0x00,
            0x51, 0xD9, 0x1C, 0x24, 0xFF, 0xD2,
        };
        private const int WrapStringHookOffset = 18;
        private static readonly byte[] WrapStringOrig = { 0xF3, 0x0F, 0x10, 0x47, 0x10 };

        // ---- DrawStringWrapped hook (loading-screen hint overlays, TdGameEngine.AddOverlayWrapped) -----
        // Measures/word-wraps at EffectiveXScale = FontScale (lied 1.0) but draws each line upsized, so the
        // wrapped overlay text overruns. For UMultiFont text we multiply FontScale by the EmitBoost factor
        // so the wrap matches the draw. 18-byte prefix ends at the GetScalingFactor call; hook = +18
        // (relocates fstp [esp+0x18]; mov ecx,[esp+0x74]).
        private static readonly byte[] DrawStringWrappedPrefix =
        {
            0x51, 0x89, 0x44, 0x24, 0x28, 0xD9, 0x1C, 0x24, 0x8B, 0x82, 0x18, 0x01, 0x00, 0x00,
            0x8B, 0xCF, 0xFF, 0xD0,
        };
        private const int DrawStringWrappedHookOffset = 18;
        private static readonly byte[] DrawStringWrappedOrig = { 0xD9, 0x5C, 0x24, 0x18, 0x8B, 0x4C, 0x24, 0x74 };

        // ---- DrawStringWrapped 16:9 confine hook (loading screen / bink hint position+wrap) -----------
        // TdGameEngine.OnLoadLevel adds the loading hint via AddOverlayWrapped with fullscreen fractions
        // (X=0.35, WrapWidth=0.55); the movie driver turns those into pixels = fraction * ResX. But the
        // bink is always 16:9 so the hint overruns the bink horizontally.
        //  We remap CurX and the wrap width *XL into the centred 16:9 region:
        //   binkWidth = min(ResX, ResY*16/9); binkFrac = binkWidth/ResX; binkLeft = (ResX-binkWidth)/2;
        //   CurX = binkLeft + CurX*binkFrac;  *XL *= binkFrac.
        // No-op at <=16:9 (binkFrac=1, binkLeft=0). Hook is right after the SEH prologue (esp=entry-0x60),
        // before the body touches xmm: CurX@[esp+0x6c], XL*@[esp+0x74], Font@[esp+0x7c]. CurX is a value
        // param and *XL is a fresh per-render local, so the remap doesnt accumulate across frames. Hook =
        // +37; displaced = mov edi,[esp+0x7c]; xor esi,esi.
        //
        // The 0x40-frame SEH prologue alone is a generic MSVC shape (approx 70-80 sites) so the locator also
        // pins the version-stable body that follows the displaced bytes (cmp edi,esi; jz; load+call the
        // font vtable; ...). The two per-build absolute addresses (the SEH scope-table push
        //  and __security_cookie) and the 6 displaced bytes the detour overwrites are
        // wildcarded so the single match holds on every version AND survives being patched (Remove /
        // re-Apply still relocate it). The cave consumes none of the wildcarded bytes.
        private static readonly BytePattern DswConfinePrefix = BytePattern.Parse(
            "68 ?? ?? ?? ?? 64 A1 00 00 00 00 50 83 EC 40 53 55 56 57 A1 ?? ?? ?? ?? " +
            "33 C4 50 8D 44 24 54 64 A3 00 00 00 00 ?? ?? ?? ?? ?? ?? " +
            "3B FE 0F 84 74 04 00 00 8B 44 24 64 8B 48 04 8B");
        private const int DswConfineHookOffset = 37;
        private static readonly byte[] DswConfineOrig = { 0x8B, 0x7C, 0x24, 0x7C, 0x33, 0xF6 }; // mov edi,[esp+0x7c]; xor esi,esi

        // ---- Subtitle line-spacing hook (FSubtitleManager::DisplaySubtitleWordWrapped) ----------------
        // StrHeight = GetMaxCharHeight() * GetScalingFactor(HeightTest) decides the multi-line gap, but
        // GetScalingFactor is the lied 1.0 while the lines draw upsized -> lines overlap. We multiply the
        // FontScale by the boost before it is multiplied into StrHeight. 22-byte prefix
        // ends at the GetScalingFactor call; hook = +22 (relocates fmul [esp+0x38]; cmp [esp+0x40],ebp).
        // The leading fmul references a per-build float const (Const_u2f), so its 4 address bytes are
        // wildcarded; the rest of the prefix (load Font vtable -> GetScalingFactor -> call) is unique.
        private static readonly BytePattern SubtitleSpacingPrefix = BytePattern.Parse(
            "D8 05 ?? ?? ?? ?? 8B 07 8B 90 18 01 00 00 51 8B CF D9 1C 24 FF D2");
        private const int SubtitleSpacingHookOffset = 22;
        private static readonly byte[] SubtitleSpacingOrig = { 0xD8, 0x4C, 0x24, 0x38, 0x39, 0x6C, 0x24, 0x40 };

        // ---- Canvas.TextSize measure hook (UCanvas::execTextSize) -------------------------------------
        // Script HUD code positions elements relative to Canvas.TextSize(s, XL, YL). TextSize runs through
        // ClippedStrLen->UUIString::StringSize, which returns the 1080-authored size (the lied 1.0), while
        // the text itself draws upsized via the DrawString/WrappedPrint hooks -> overlaps (e.g. the time-
        // trial timer vs its target/qualifying time). We scale the returned XL/YL by the boost for
        // UMultiFont text so measured size matches the drawn size. The store block is SSE; the cave
        // rebuilds the whole convert+store sequence and returns past it (BlockLen below).
        private static readonly byte[] TextSizePrefix =
        {
            0xD9, 0xE8, 0x51, 0x8D, 0x4C, 0x24, 0x24, 0x51, 0x8D, 0x54, 0x24, 0x24, 0x52,
            0x83, 0xEC, 0x08, 0xD9, 0x54, 0x24, 0x04, 0xD9, 0x1C, 0x24, 0x50,
        };
        private const int TextSizeHookOffset = 29;   // 24-byte prefix + 5-byte ClippedStrLen call
        private static readonly byte[] TextSizeOrig = { 0xF3, 0x0F, 0x2A, 0x44, 0x24, 0x34 }; // cvtsi2ss xmm0,[esp+0x34]
        private const int TextSizeBlockLen = 0x1B;    // convert+store block replaced by the cave

        // ---- UMultiFont::GetScalingFactor (the "is MultiFont" discriminator constant) -----------------
        private static readonly byte[] GsfUniqueTail =
        {
            0x8B, 0x8E, 0x50, 0x01, 0x00, 0x00, 0xD9, 0x44, 0x24, 0x08, 0xD8, 0x34, 0x81,
        };
        private static readonly byte[] GsfPrologue = { 0xD9, 0x44, 0x24, 0x04, 0x56 };
        private const int MaxPrologueScanBack = 0x40;

        public static MultiFontScaleState DetectState(string exePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(exePath);
                if (OoaService.HasOoaSection(data))
                    PatchUtility.DecryptOoaInPlace(data);
                var pe = PeImageLayout.Parse(data);
                if (!TryFindHook(data, pe, DrawStringPrefix, DrawStringHookOffset, out int off, out _))
                    return MultiFontScaleState.Unknown;
                if (data[off] == 0xE9) return MultiFontScaleState.Patched;
                if (data.AsSpan(off, DrawStringOrig.Length).SequenceEqual(DrawStringOrig))
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
                // An unrecognised exe must not be patched with a guessed tag: the cave version tag
                // drives later detection/reapply logic, and a wrong tag corrupts that pipeline.
                string version = ExeVersionDetector.DetectVersion(data, exePath)
                    ?? throw new InvalidOperationException("Unknown or unsupported MirrorsEdge.exe version; cannot apply the HUD text fix.");
                data = ApplyToImage(data, version);
            }
            PatchUtility.WritePreservingAttributes(exePath, data);
        }

        private static byte[] ApplyToImage(byte[] data, string versionTag)
        {
            var pe = PeImageLayout.Parse(data);

            uint gsfVa = FindGsfVa(data, pe)
                ?? throw new InvalidOperationException("UMultiFont::GetScalingFactor not found.");
            // True window height (GSystemSettings.ResY). Every cave derives its boost from ResY (not the
            // render-target height) so text is sized for the window, not ME's 16:9-locked render buffer
            // (taller than the window on non-16:9 modes, which would over-scale text).
            uint gssResYVa = FindGssResYVa(data, pe)
                ?? throw new InvalidOperationException("GSystemSettings.ResY not found.");

            if (!TryFindHook(data, pe, DrawStringPrefix, DrawStringHookOffset, out int dsOff, out uint dsVa))
                throw new InvalidOperationException("DrawString hook site not found.");
            if (!TryFindHook(data, pe, WrappedPrintPrefix, WrappedPrintHookOffset, out int wpOff, out uint wpVa))
                throw new InvalidOperationException("WrappedPrint hook site not found.");

            // DrawStringCentered (subtitle centering), WrapString (subtitle/loading-hint wrap),
            // DrawStringWrapped (loading-hint overlays), subtitle line-spacing and execTextSize (HUD
            // layout) are optional - applied if their hook site is present.
            bool doDsc = TryFindHook(data, pe, DrawStringCenteredPrefix, DrawStringCenteredHookOffset, out int dscOff, out uint dscVa);
            bool doWs = TryFindHook(data, pe, WrapStringPrefix, WrapStringHookOffset, out int wsOff, out uint wsVa);
            bool doTs = TryFindHook(data, pe, TextSizePrefix, TextSizeHookOffset, out int tsOff, out uint tsVa);
            bool doDsw = TryFindHook(data, pe, DrawStringWrappedPrefix, DrawStringWrappedHookOffset, out int dswOff, out uint dswVa);
            bool doDswC = TryFindHook(data, pe, DswConfinePrefix, DswConfineHookOffset, out int dswcOff, out uint dswcVa);
            bool doDss = TryFindHook(data, pe, SubtitleSpacingPrefix, SubtitleSpacingHookOffset, out int dssOff, out uint dssVa);

            // If re-applying, DrawString's detour points at the existing cave base (code blocks are
            // allocated before the consts, so it is the lowest cave VA). Capture it before restoring so
            // the cave is reclaimed rather than regrown.
            uint? priorCaveBase = data[dsOff] == 0xE9
                ? dsVa + 5u + (uint)BitConverter.ToInt32(data, dsOff + 1)
                : (uint?)null;

            // Restore originals first so re-applying installs this build's caves cleanly.
            RestoreIfDetoured(data, dsOff, DrawStringOrig);
            RestoreIfDetoured(data, wpOff, WrappedPrintOrig);
            if (doDsc) RestoreIfDetoured(data, dscOff, DrawStringCenteredOrig);
            if (doWs) RestoreIfDetoured(data, wsOff, WrapStringOrig);
            if (doDsw) RestoreIfDetoured(data, dswOff, DrawStringWrappedOrig);
            if (doDswC) RestoreIfDetoured(data, dswcOff, DswConfineOrig);
            if (doDss) RestoreIfDetoured(data, dssOff, SubtitleSpacingOrig);
            if (doTs) RestoreIfDetoured(data, tsOff, TextSizeOrig);
            if (!data.AsSpan(dsOff, DrawStringOrig.Length).SequenceEqual(DrawStringOrig))
                throw new InvalidOperationException($"Unexpected bytes at DrawString hook 0x{dsVa:X8}.");
            if (!data.AsSpan(wpOff, WrappedPrintOrig.Length).SequenceEqual(WrappedPrintOrig))
                throw new InvalidOperationException($"Unexpected bytes at WrappedPrint hook 0x{wpVa:X8}.");
            if (doDsc && !data.AsSpan(dscOff, DrawStringCenteredOrig.Length).SequenceEqual(DrawStringCenteredOrig))
                doDsc = false; // unexpected bytes -> skip rather than risk a bad patch
            if (doWs && !data.AsSpan(wsOff, WrapStringOrig.Length).SequenceEqual(WrapStringOrig))
                doWs = false;
            if (doDsw && !data.AsSpan(dswOff, DrawStringWrappedOrig.Length).SequenceEqual(DrawStringWrappedOrig))
                doDsw = false;
            if (doDswC && !data.AsSpan(dswcOff, DswConfineOrig.Length).SequenceEqual(DswConfineOrig))
                doDswC = false;
            if (doDss && !data.AsSpan(dssOff, SubtitleSpacingOrig.Length).SequenceEqual(SubtitleSpacingOrig))
                doDss = false;
            if (doTs && !data.AsSpan(tsOff, TextSizeOrig.Length).SequenceEqual(TextSizeOrig))
                doTs = false;

            var cave = CaveSection.Open(data, versionTag: versionTag);
            // Re-applying (e.g. one apply per resolution change) overwrites the prior font cave in place
            // instead of reclaiming+reappending - so the cave never grows AND we never zero a co-resident
            // patch (supersample/logging) that was allocated above this one. Deterministic layout means
            // the same Alloc sequence reproduces the same offsets within the existing block.
            if (priorCaveBase != null)
                cave.ReuseFrom((int)(priorCaveBase.Value - cave.SectionVa));

            // Code blocks first (each right-sized to its actual length), so the lowest cave VA is a
            // detour target (the reuse/reclaim anchor); the float consts (absolute-addressed) follow.
            uint dsCodeVa = cave.Alloc(DsCaveLen, 16);
            uint wpCodeVa = cave.Alloc(WpCaveLen, 16);
            uint dscCodeVa = doDsc ? cave.Alloc(DscCaveLen, 16) : 0u;
            uint wsCodeVa = doWs ? cave.Alloc(WsCaveLen, 16) : 0u;
            uint dswCodeVa = doDsw ? cave.Alloc(DswCaveLen, 16) : 0u;
            uint dswcCodeVa = doDswC ? cave.Alloc(DswcCaveLen, 16) : 0u;
            uint dssCodeVa = doDss ? cave.Alloc(DssCaveLen, 16) : 0u;
            uint tsCodeVa = doTs ? cave.Alloc(TsCaveLen, 16) : 0u;
            uint c1080Va = cave.Alloc(4, 4);
            uint c5625Va = cave.Alloc(4, 4);
            uint cOneVa = cave.Alloc(4, 4);
            uint cHalfVa = cave.Alloc(4, 4);

            cave.Write(c1080Va, BitConverter.GetBytes(StockAuthoredHeight));
            cave.Write(c5625Va, BitConverter.GetBytes(0.5625f));
            cave.Write(cOneVa, BitConverter.GetBytes(1.0f));
            cave.Write(cHalfVa, BitConverter.GetBytes(0.5f));
            cave.Write(dsCodeVa, BuildDrawStringCave(dsCodeVa, gsfVa, gssResYVa, c5625Va, c1080Va, dsVa + (uint)DrawStringOrig.Length));
            cave.Write(wpCodeVa, BuildWrappedPrintCave(wpCodeVa, gsfVa, gssResYVa, c5625Va, c1080Va, wpVa + (uint)WrappedPrintOrig.Length));
            if (doDsc)
                cave.Write(dscCodeVa, BuildDrawStringCenteredCave(
                    dscCodeVa, gsfVa, gssResYVa, c5625Va, c1080Va, dscVa + (uint)DrawStringCenteredOrig.Length));
            if (doWs)
                cave.Write(wsCodeVa, BuildWrapStringCave(
                    wsCodeVa, gsfVa, gssResYVa, c5625Va, c1080Va, wsVa + (uint)WrapStringOrig.Length));
            if (doDsw)
                cave.Write(dswCodeVa, BuildDrawStringWrappedCave(
                    dswCodeVa, gsfVa, gssResYVa, c5625Va, c1080Va, dswVa + (uint)DrawStringWrappedOrig.Length));
            if (doDswC)
                cave.Write(dswcCodeVa, BuildDswConfineCave(
                    dswcCodeVa, gssResYVa, c5625Va, cHalfVa, dswcVa + (uint)DswConfineOrig.Length));
            if (doDss)
                cave.Write(dssCodeVa, BuildSubtitleSpacingCave(
                    dssCodeVa, gsfVa, gssResYVa, c5625Va, c1080Va, dssVa + (uint)SubtitleSpacingOrig.Length));
            if (doTs)
                cave.Write(tsCodeVa, BuildTextSizeCave(
                    tsCodeVa, gsfVa, gssResYVa, c5625Va, cOneVa, c1080Va, tsVa + (uint)TextSizeBlockLen));
            data = cave.Finalize();

            var pe2 = PeImageLayout.Parse(data);
            if (!TryFindHook(data, pe2, DrawStringPrefix, DrawStringHookOffset, out dsOff, out dsVa) ||
                !TryFindHook(data, pe2, WrappedPrintPrefix, WrappedPrintHookOffset, out wpOff, out wpVa))
                throw new InvalidOperationException("Hook site vanished after cave finalize.");

            WriteDetour(data, dsOff, dsVa, dsCodeVa, DrawStringOrig.Length);
            WriteDetour(data, wpOff, wpVa, wpCodeVa, WrappedPrintOrig.Length);
            if (doDsc && TryFindHook(data, pe2, DrawStringCenteredPrefix, DrawStringCenteredHookOffset, out dscOff, out dscVa))
                WriteDetour(data, dscOff, dscVa, dscCodeVa, DrawStringCenteredOrig.Length);
            if (doWs && TryFindHook(data, pe2, WrapStringPrefix, WrapStringHookOffset, out wsOff, out wsVa))
                WriteDetour(data, wsOff, wsVa, wsCodeVa, WrapStringOrig.Length);
            if (doDsw && TryFindHook(data, pe2, DrawStringWrappedPrefix, DrawStringWrappedHookOffset, out dswOff, out dswVa))
                WriteDetour(data, dswOff, dswVa, dswCodeVa, DrawStringWrappedOrig.Length);
            if (doDswC && TryFindHook(data, pe2, DswConfinePrefix, DswConfineHookOffset, out dswcOff, out dswcVa))
                WriteDetour(data, dswcOff, dswcVa, dswcCodeVa, DswConfineOrig.Length);
            if (doDss && TryFindHook(data, pe2, SubtitleSpacingPrefix, SubtitleSpacingHookOffset, out dssOff, out dssVa))
                WriteDetour(data, dssOff, dssVa, dssCodeVa, SubtitleSpacingOrig.Length);
            if (doTs && TryFindHook(data, pe2, TextSizePrefix, TextSizeHookOffset, out tsOff, out tsVa))
                WriteDetour(data, tsOff, tsVa, tsCodeVa, TextSizeOrig.Length);

            return data;
        }

        public static void Remove(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            if (OoaService.HasOoaSection(data))
            {
                // EA: decrypt, restore detours, re-encrypt + trim. Only rewrite if a detour was present
                // (re-encrypting an already-clean image would needlessly strip its signature).
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

        // Restores every detour in an in-memory PE image (already decrypted for EA) and reclaims the
        // font cave region so a later re-Apply reuses it instead of growing the cave. Returns the image
        // (the reclaim rewrites section bytes) and whether anything changed. No file I/O.
        private static byte[] RemoveFromImage(byte[] data, out bool changed)
        {
            var pe = PeImageLayout.Parse(data);

            // The existing cave base is DrawString's detour target (code is allocated before the
            // consts). Capture it before restoring the detour so the cave can be reclaimed.
            uint? caveBase = null;
            if (TryFindHook(data, pe, DrawStringPrefix, DrawStringHookOffset, out int dsHookOff, out uint dsHookVa)
                && data[dsHookOff] == 0xE9)
                caveBase = dsHookVa + 5u + (uint)BitConverter.ToInt32(data, dsHookOff + 1);

            changed = false;
            if (TryFindHook(data, pe, DrawStringPrefix, DrawStringHookOffset, out int dsOff, out _))
                changed |= RestoreIfDetoured(data, dsOff, DrawStringOrig);
            if (TryFindHook(data, pe, WrappedPrintPrefix, WrappedPrintHookOffset, out int wpOff, out _))
                changed |= RestoreIfDetoured(data, wpOff, WrappedPrintOrig);
            bool doDsc = TryFindHook(data, pe, DrawStringCenteredPrefix, DrawStringCenteredHookOffset, out int dscOff, out _);
            if (doDsc) changed |= RestoreIfDetoured(data, dscOff, DrawStringCenteredOrig);
            bool doWs = TryFindHook(data, pe, WrapStringPrefix, WrapStringHookOffset, out int wsOff, out _);
            if (doWs) changed |= RestoreIfDetoured(data, wsOff, WrapStringOrig);
            bool doDsw = TryFindHook(data, pe, DrawStringWrappedPrefix, DrawStringWrappedHookOffset, out int dswOff, out _);
            if (doDsw) changed |= RestoreIfDetoured(data, dswOff, DrawStringWrappedOrig);
            bool doDswC = TryFindHook(data, pe, DswConfinePrefix, DswConfineHookOffset, out int dswcOff, out _);
            if (doDswC) changed |= RestoreIfDetoured(data, dswcOff, DswConfineOrig);
            bool doDss = TryFindHook(data, pe, SubtitleSpacingPrefix, SubtitleSpacingHookOffset, out int dssOff, out _);
            if (doDss) changed |= RestoreIfDetoured(data, dssOff, SubtitleSpacingOrig);
            bool doTs = TryFindHook(data, pe, TextSizePrefix, TextSizeHookOffset, out int tsOff, out _);
            if (doTs) changed |= RestoreIfDetoured(data, tsOff, TextSizeOrig);

            // Reclaim the font cave so a later Apply starts fresh - but only when it's the topmost cave
            // allocation (fail-safe: if a co-resident patch sits above it, the block is left in place
            // and a re-Apply reuses it). caveBase != null implies the detours were just restored.
            if (caveBase != null)
                data = CaveSection.ReclaimIfTopmost(data, "", false, caveBase.Value,
                    CaveFootprint(doDsc, doWs, doDsw, doDswC, doDss, doTs));

            return data;
        }

        // Byte size of the font cave for the present hook set, mirroring the Alloc sequence in
        // ApplyToImage (code blocks align-16, then the four 4-byte consts). Base-independent because the
        // base is always 16-aligned (DrawString's block). Keep in sync with the allocation order.
        private static int CaveFootprint(bool doDsc, bool doWs, bool doDsw, bool doDswC, bool doDss, bool doTs)
        {
            static int A16(int v) => (v + 15) & ~15;
            static int A4(int v) => (v + 3) & ~3;
            int wm = DsCaveLen;                       // DrawString (base is 16-aligned)
            wm = A16(wm) + WpCaveLen;                 // WrappedPrint
            if (doDsc) wm = A16(wm) + DscCaveLen;
            if (doWs) wm = A16(wm) + WsCaveLen;
            if (doDsw) wm = A16(wm) + DswCaveLen;
            if (doDswC) wm = A16(wm) + DswcCaveLen;
            if (doDss) wm = A16(wm) + DssCaveLen;
            if (doTs) wm = A16(wm) + TsCaveLen;
            wm = A4(wm) + 4;                          // c1080
            wm = A4(wm) + 4;                          // c5625
            wm = A4(wm) + 4;                          // cOne
            wm = A4(wm) + 4;                          // cHalf
            return wm;
        }

        // Skips the boost (jumps to skipLabel) unless width > 1920, i.e. the 16:9 render height
        // ResX*0.5625 exceeds the 1080 authored page. At/below that the stock GetScalingFactor is already
        // correct, so boosting it oversizes sub-1080p / non-16:9 text. ResX is the INT before ResY.
        private static void EmitBoostGate(MachineCodeBuilder mc, uint gssResYVa, string skipLabel)
        {
            uint resXVa = gssResYVa - 4;
            mc.Emit(new byte[] { 0x81, 0x3D }); mc.EmitUInt32(resXVa); mc.EmitUInt32(1920); // cmp dword [ResX],1920
            mc.EmitJle(skipLabel);
        }

        // Emits x87 code that pushes the canvas/HUD scale factor onto st(0):
        //   boost = FMin(ResY, render) / FMax(1.0, FMin(render, 1080)),  render = ResX * 0.5625
        // i.e. visible height / the font page height the lie selects. Matches the bytecode UIStyle scale
        // (FMin(SizeY, SizeX*0.5625) / FMin(SizeX*0.5625, 1080)) so canvas text tracks UI text at every
        // aspect AND height: the numerator clamps to the 16:9-locked render height so wider-than-16:9 is
        // bounded by the window and taller-than-16:9 by the (shorter) render. For 16:9 it reduces to
        // ResY/FMin(ResY,1080). FMax(1.0, ..) guards div-by-zero. Branchless (FMin/FMax via fcmov), no GP
        // clobber, net +1 on the x87 stack. ResX is the INT before ResY (ResY == ResX + 4).
        private static void EmitBoost(MachineCodeBuilder mc, uint gssResYVa, uint c5625Va, uint c1080Va)
        {
            uint resXVa = gssResYVa - 4;
            mc.Emit(new byte[] { 0xDB, 0x05 }); mc.EmitUInt32(resXVa);    // fild dword [ResX]   st0=ResX
            mc.Emit(new byte[] { 0xD8, 0x0D }); mc.EmitUInt32(c5625Va);   // fmul dword [0.5625] st0=render
            mc.Emit(new byte[] { 0xD9, 0x05 }); mc.EmitUInt32(c1080Va);   // fld dword [1080]    st0=1080,st1=render
            mc.Emit(new byte[] { 0xDB, 0xF1 });                          // fcomi st0,st1
            mc.Emit(new byte[] { 0xDB, 0xC1 });                          // fcmovnb st0,st1     st0=FMin(render,1080),st1=render
            mc.Emit(new byte[] { 0xD9, 0xE8 });                          // fld1               st0=1.0,st1=FMin,st2=render
            mc.Emit(new byte[] { 0xDB, 0xF1 });                          // fcomi st0,st1
            mc.Emit(new byte[] { 0xDA, 0xC1 });                          // fcmovb st0,st1      st0=FMax(1,FMin)=denom,st1=FMin,st2=render
            mc.Emit(new byte[] { 0xDD, 0xD9 });                          // fstp st1            st0=denom,st1=render [drop FMin]
            mc.Emit(new byte[] { 0xDB, 0x05 }); mc.EmitUInt32(gssResYVa); // fild dword [ResY]   st0=ResY,st1=denom,st2=render
            mc.Emit(new byte[] { 0xDB, 0xF2 });                          // fcomi st0,st2
            mc.Emit(new byte[] { 0xDB, 0xC2 });                          // fcmovnb st0,st2     st0=FMin(ResY,render)=num,st1=denom,st2=render
            mc.Emit(new byte[] { 0xD8, 0xF1 });                          // fdiv st0,st1        st0=num/denom=boost,st1=denom,st2=render
            mc.Emit(new byte[] { 0xDD, 0xD9 });                          // fstp st1            st0=boost,st1=render [drop denom]
            mc.Emit(new byte[] { 0xDD, 0xD9 });                          // fstp st1            st0=boost [drop render]
        }

        // DrawString: on entry st(0) = (lied) FontScale. Scales by EmitBoost (ResY / FMin(ResX*0.5625,
        // 1080)) so canvas + list text track the window at every aspect ratio.
        //   ForcedViewportHeight ([ebp+0x2C]) null/zero  -> canvas/HUD:
        //        if UMultiFont: st(0) = boost
        //   ForcedViewportHeight set & non-zero          -> UI text:
        //        if UMultiFont and under-scaled (e.g. list cells, which ignore UIStyle_Text.Scale):
        //          st(0) = max(FontScale, boost/XScale) so effective scale reaches boost.
        //          Labels already arrive at XScale=boost, so their candidate is 1.0 -> unchanged.
        internal static byte[] BuildDrawStringCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            EmitBoostGate(mc, gssResYVa, "after");
            mc.Emit(new byte[] { 0x8B, 0x45, 0x2C });               // mov eax,[ebp+0x2C]  ForcedViewportHeight ptr
            mc.Emit(new byte[] { 0x85, 0xC0 });                     // test eax,eax
            mc.EmitJz("canvas");                                    //  null -> canvas
            mc.Emit(new byte[] { 0x83, 0x38, 0x00 });               // cmp dword [eax],0
            mc.EmitJz("canvas");                                    //  *FVH == 0 -> canvas

            // UI path: lift under-scaled MultiFont text (e.g. list cells) toward the boost.
            mc.Emit(new byte[] { 0x8B, 0x55, 0x18 });               // mov edx,[ebp+0x18]  Font
            mc.Emit(new byte[] { 0x8B, 0x12 });                     // mov edx,[edx]       vtable
            mc.Emit(new byte[] { 0x81, 0xBA, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [edx+0x118],gsfVa
            mc.EmitJnz("after");                                    //  not UMultiFont -> keep
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);             // st0=boost (st1=FontScale)
            mc.Emit(new byte[] { 0xD8, 0x75, 0x20 });               // fdiv dword [ebp+0x20] XScale -> candidate
            mc.Emit(new byte[] { 0xDB, 0xF1 });                     // fcomi st0,st1
            mc.Emit(new byte[] { 0xDA, 0xD1 });                     // fcmovbe st0,st1     -> st0=max(candidate,FontScale)
            mc.Emit(new byte[] { 0xDD, 0xD9 });                     // fstp st(1)          -> drop old FontScale
            mc.EmitJmp("after");

            mc.MarkLabel("canvas");
            mc.Emit(new byte[] { 0x8B, 0x45, 0x18 });               // mov eax,[ebp+0x18]  Font
            mc.Emit(new byte[] { 0x8B, 0x00 });                     // mov eax,[eax]       vtable
            mc.Emit(new byte[] { 0x81, 0xB8, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [eax+0x118],gsfVa
            mc.EmitJnz("after");                                    //  console/plain UFont -> keep
            mc.Emit(new byte[] { 0xDD, 0xD8 });                     // fstp st(0)
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);             // st0=boost

            mc.MarkLabel("after");
            mc.Emit(new byte[] { 0xD9, 0x45, 0x20 });               // fld [ebp+0x20]   XScale (displaced)
            mc.Emit(new byte[] { 0x8B, 0x45, 0x1C });               // mov eax,[ebp+0x1C] (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // WrappedPrint: on entry st(0) = (lied) FontScale, edi = Font (always HUD/canvas, no UI path).
        // Non-UMultiFont (console/plain UFont) is kept; otherwise st(0) is replaced with the boost.
        internal static byte[] BuildWrappedPrintCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            EmitBoostGate(mc, gssResYVa, "after");
            mc.Emit(new byte[] { 0x8B, 0x07 });                     // mov eax,[edi]    vtable (edi=Font)
            mc.Emit(new byte[] { 0x81, 0xB8, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [eax+0x118],gsfVa
            mc.EmitJnz("after");
            mc.Emit(new byte[] { 0xDD, 0xD8 });                     // fstp st(0)
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);             // st0=boost
            mc.MarkLabel("after");
            mc.Emit(new byte[] { 0xD9, 0x84, 0x24, 0x90, 0x00, 0x00, 0x00 }); // fld [esp+0x90] (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // DrawStringCentered: edi = Font, [esp+0x20] = measured XL. The original does mov eax,[esp+0x20];
        // cdq (start of XL/2). For UMultiFont we replace eax with round(XL * boost) so the centering
        // offset matches the upsized draw. Non-MultiFont -> XL unchanged.
        internal static byte[] BuildDrawStringCenteredCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            EmitBoostGate(mc, gssResYVa, "keepXL");                 // before push ebx so the skip leaves esp balanced
            mc.Emit((byte)0x53);                                    // push ebx
            mc.Emit(new byte[] { 0x8B, 0x1F });                     // mov ebx,[edi]   Font vtable
            mc.Emit(new byte[] { 0x81, 0xBB, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [ebx+0x118],gsfVa
            mc.Emit((byte)0x5B);                                    // pop ebx
            mc.EmitJnz("keepXL");                                   //  not UMultiFont -> keep
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);             // st0 = boost (FPU only, no GP clobber)
            mc.Emit(new byte[] { 0xDB, 0x44, 0x24, 0x20 });         // fild [esp+0x20]      XL
            mc.Emit(new byte[] { 0xDE, 0xC9 });                     // fmulp st1,st0        XL*boost
            mc.Emit(new byte[] { 0x83, 0xEC, 0x04 });               // sub esp,4            scratch slot
            mc.Emit(new byte[] { 0xDB, 0x1C, 0x24 });               // fistp [esp]          store scaled XL
            mc.Emit((byte)0x58);                                    // pop eax              scaled XL
            mc.EmitJmp("doneXL");
            mc.MarkLabel("keepXL");
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x20 });         // mov eax,[esp+0x20]   XL (displaced)
            mc.MarkLabel("doneXL");
            mc.Emit((byte)0x99);                                    // cdq                  (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // WrapString: edi = Parameters, st(0) = FontScale. For canvas (ViewportHeight==0) UMultiFont text,
        // multiply FontScale by boost (ResY/FMin(ResX*0.5625,1080)) so the wrap width matches the upsized
        // draw. UI callers (VH>0) and non-MultiFont are left unchanged.
        internal static byte[] BuildWrapStringCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            EmitBoostGate(mc, gssResYVa, "keep");
            mc.Emit(new byte[] { 0x83, 0xBF, 0x40, 0x00, 0x00, 0x00, 0x00 }); // cmp dword [edi+0x40],0  ViewportHeight
            mc.EmitJnz("keep");                                    //  VH != 0 -> UI, keep
            mc.Emit(new byte[] { 0x8B, 0x47, 0x18 });             // mov eax,[edi+0x18]  DrawFont
            mc.Emit(new byte[] { 0x8B, 0x00 });                   // mov eax,[eax]       vtable
            mc.Emit(new byte[] { 0x81, 0xB8, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [eax+0x118],gsfVa
            mc.EmitJnz("keep");                                    //  not UMultiFont -> keep
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);           // st0=boost (st1=FontScale), no GP clobber
            mc.Emit(new byte[] { 0xDE, 0xC9 });                   // fmulp st1,st0        FontScale*boost
            mc.MarkLabel("keep");
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x47, 0x10 }); // movss xmm0,[edi+0x10] (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // DrawStringWrapped: edi = Font, st(0) = FontScale. For UMultiFont text, multiply FontScale by
        // boost (ResY/FMin(ResX*0.5625,1080)) so the wrap measure matches the upsized draw (always canvas).
        internal static byte[] BuildDrawStringWrappedCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            EmitBoostGate(mc, gssResYVa, "keep");
            mc.Emit(new byte[] { 0x8B, 0x07 });                   // mov eax,[edi]   vtable (edi=Font)
            mc.Emit(new byte[] { 0x81, 0xB8, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [eax+0x118],gsfVa
            mc.EmitJnz("keep");                                   //  not UMultiFont -> keep
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);           // st0=boost (st1=FontScale)
            mc.Emit(new byte[] { 0xDE, 0xC9 });                   // fmulp st1,st0          FontScale*boost
            mc.MarkLabel("keep");
            mc.Emit(new byte[] { 0xD9, 0x5C, 0x24, 0x18 });       // fstp [esp+0x18]        (displaced)
            mc.Emit(new byte[] { 0x8B, 0x4C, 0x24, 0x74 });       // mov ecx,[esp+0x74]     (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // DrawStringWrapped 16:9 confine: just after the SEH prologue (esp=entry-0x60). Remaps the loading
        // hint's CurX ([esp+0x6c], value param) and wrap width *XL (ptr [esp+0x74], fresh per render) into
        // the centered 16:9 bink region. binkWidth = min(ResX, ResY*16/9); binkFrac = binkWidth/ResX;
        // binkLeft = (ResX-binkWidth)/2. CurX = binkLeft + CurX*binkFrac; *XL *= binkFrac. SSE only (xmm not
        // live yet; FPU stack untouched). No-op at <=16:9 (binkFrac=1, binkLeft=0).
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

        // DisplaySubtitleWordWrapped: edi = MediumFont, st(0) = FontScale (from GetScalingFactor), then
        // fmul [esp+0x38] (GetMaxCharHeight) -> StrHeight. For UMultiFont we multiply FontScale by boost
        // (ResY/FMin(ResX*0.5625,1080)) before the fmul so the line gap matches the upsized text.
        internal static byte[] BuildSubtitleSpacingCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            EmitBoostGate(mc, gssResYVa, "keep");
            mc.Emit(new byte[] { 0x8B, 0x07 });                   // mov eax,[edi]   vtable (edi=Font)
            mc.Emit(new byte[] { 0x81, 0xB8, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [eax+0x118],gsfVa
            mc.EmitJnz("keep");                                   //  not UMultiFont -> keep
            EmitBoost(mc, gssResYVa, c5625Va, c1080Va);           // st0=boost (st1=FontScale)
            mc.Emit(new byte[] { 0xDE, 0xC9 });                   // fmulp st1,st0          FontScale*boost
            mc.MarkLabel("keep");
            mc.Emit(new byte[] { 0xD8, 0x4C, 0x24, 0x38 });       // fmul dword [esp+0x38]  * GetMaxCharHeight (displaced)
            mc.Emit(new byte[] { 0x39, 0x6C, 0x24, 0x40 });       // cmp [esp+0x40],ebp     (displaced)
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // execTextSize: ebp = this (UCanvas), Font at [ebp+0x3c], YL_ptr in ebx; after ClippedStrLen the
        // ints XLi/YLi live at [esp+0x34]/[esp+0x38] and XL_ptr at [esp+0x30]. The cave rebuilds the SSE
        // convert+store block, multiplying both results by the boost for UMultiFont fonts (scale defaults
        // to 1.0 for non-MultiFont or if ResY==0), then returns past the block.
        internal static byte[] BuildTextSizeCave(uint codeVa, uint gsfVa, uint gssResYVa, uint c5625Va, uint cOneVa, uint c1080Va, uint returnVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            uint resXVa = gssResYVa - 4;
            mc.Emit(new byte[] { 0xB8, 0x00, 0x00, 0x80, 0x3F });   // mov eax,0x3F800000  (1.0f)
            mc.Emit(new byte[] { 0x66, 0x0F, 0x6E, 0xD0 });         // movd xmm2,eax       scale = 1.0
            EmitBoostGate(mc, gssResYVa, "stores");                 // after the scale=1.0 init, so the skip keeps scale=1.0
            mc.Emit(new byte[] { 0x8B, 0x45, 0x3C });               // mov eax,[ebp+0x3c]  Font
            mc.Emit(new byte[] { 0x8B, 0x00 });                     // mov eax,[eax]       vtable
            mc.Emit(new byte[] { 0x81, 0xB8, 0x18, 0x01, 0x00, 0x00 }); mc.EmitUInt32(gsfVa); // cmp [eax+0x118],gsfVa
            mc.EmitJnz("stores");                                   //  not UMultiFont -> scale = 1
            mc.Emit(new byte[] { 0xA1 }); mc.EmitUInt32(gssResYVa); // mov eax,[GSS.ResY]
            mc.Emit(new byte[] { 0x85, 0xC0 }); mc.EmitJz("stores"); // ResY==0 -> scale = 1
            // scale = FMin(ResY, render) / FMax(1.0, FMin(render, 1080))  (render=ResX*0.5625; matches EmitBoost)
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2A, 0xD0 });         // cvtsi2ss xmm2,eax    ResY
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2A, 0x1D }); mc.EmitUInt32(resXVa);  // cvtsi2ss xmm3,[ResX]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x59, 0x1D }); mc.EmitUInt32(c5625Va); // mulss xmm3,[0.5625]  render
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5D, 0xD3 });         // minss xmm2,xmm3      num=FMin(ResY,render)
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5D, 0x1D }); mc.EmitUInt32(c1080Va); // minss xmm3,[1080]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5F, 0x1D }); mc.EmitUInt32(cOneVa);  // maxss xmm3,[1.0]     denom
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5E, 0xD3 });         // divss xmm2,xmm3      num/denom
            mc.MarkLabel("stores");
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2A, 0x44, 0x24, 0x34 }); // cvtsi2ss xmm0,[esp+0x34]  XLi (displaced)
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x59, 0xC2 });         // mulss xmm0,xmm2
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x30 });         // mov eax,[esp+0x30]   XL_ptr
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x00 });         // movss [eax],xmm0
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2A, 0x44, 0x24, 0x38 }); // cvtsi2ss xmm0,[esp+0x38]  YLi
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x59, 0xC2 });         // mulss xmm0,xmm2
            mc.Emit(new byte[] { 0x83, 0xC4, 0x18 });               // add esp,0x18         (displaced)
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x03 });         // movss [ebx],xmm0
            mc.EmitJmpNear(returnVa);
            return mc.Build();
        }

        // Resolves the absolute VA of GSystemSettings.ResY (the true target/window height, written by
        // every resolution change via FSystemSettings::SetResolution). The hooks need this, not the
        // render-target/viewport height, which ME 16:9-locks to the width on non-16:9 modes (e.g.
        // 2560x1080 renders at a 1440-tall buffer) - reading the render height over-scales text by
        // renderH/windowH there. Anchored on UGameEngine::Init's
        // Parse(appCmdLine(), TEXT("ResX="), GSystemSettings.ResX) /
        // Parse(appCmdLine(), TEXT("ResY="), GSystemSettings.ResY), which push &ResX / &ResY as
        // immediates right before the "ResX="/"ResY=" string. Cross-checked by ResY == ResX + 4.
        private static uint? FindGssResYVa(byte[] data, PeImageLayout pe)
        {
            uint? resx = FindParseTargetAddr(data, pe, "ResX=");
            uint? resy = FindParseTargetAddr(data, pe, "ResY=");
            if (resx.HasValue && resy.HasValue && resy.Value == resx.Value + 4)
                return resy.Value;
            return null;
        }

        // For a Parse(..., TEXT(match), GSystemSettings.<field>) call, returns the &field immediate that
        // is `push`ed immediately before `push <match-string-VA>`. Requires a unique match string and a
        // unique push of it, so a stray substring or duplicate won't yield a false address.
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

        private static bool TryFindHook(byte[] data, PeImageLayout pe, BytePattern prefix, int hookOffset,
            out int hookOff, out uint hookVa)
        {
            hookOff = 0; hookVa = 0;
            var text = pe.FindSectionByName(".text");
            if (text == null) return false;
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);

            int p = IndexOf(data, prefix, start, end);
            if (p < 0) return false;
            if (IndexOf(data, prefix, p + 1, end) >= 0) return false; // ambiguous

            hookOff = p + hookOffset;
            hookVa = pe.OffsetToVa(hookOff);
            return true;
        }

        private static uint? FindGsfVa(byte[] data, PeImageLayout pe)
        {
            int off = FindGsfOffset(data, pe);
            return off < 0 ? null : pe.OffsetToVa(off);
        }

        private static int FindGsfOffset(byte[] data, PeImageLayout pe)
        {
            var text = pe.FindSectionByName(".text");
            if (text == null) return -1;
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);

            int tail = IndexOf(data, GsfUniqueTail, start, end);
            if (tail < 0) return -1;
            if (IndexOf(data, GsfUniqueTail, tail + 1, end) >= 0) return -1;

            for (int i = tail; i >= Math.Max(start, tail - MaxPrologueScanBack); i--)
            {
                if (i + GsfPrologue.Length <= data.Length &&
                    data.AsSpan(i, GsfPrologue.Length).SequenceEqual(GsfPrologue))
                    return i;
            }
            return -1;
        }

        private static int IndexOf(byte[] data, BytePattern pat, int start, int end)
        {
            int limit = end - pat.Length;
            for (int i = start; i <= limit; i++)
                if (pat.MatchesAt(data, i)) return i;
            return -1;
        }

        // Fixed byte sequences (no wildcards) reuse the same matcher via the implicit conversion.
        private static int IndexOf(byte[] data, byte[] pat, int start, int end)
            => IndexOf(data, (BytePattern)pat, start, end);

        // A byte pattern with optional wildcard positions. Lets a single hook signature stay unique
        // across builds (wildcarding the few version-varying absolute-address operands) and survive its
        // own detour (wildcarding the displaced bytes the E9 overwrites). A plain byte[] converts to an
        // all-fixed pattern, so non-wildcard call sites are unchanged.
        private readonly struct BytePattern
        {
            private readonly byte[] _bytes;
            private readonly bool[] _fixed; // true => _bytes[i] must match; false => wildcard

            private BytePattern(byte[] bytes, bool[] fixedMask)
            {
                _bytes = bytes;
                _fixed = fixedMask;
            }

            public int Length => _bytes.Length;

            public static implicit operator BytePattern(byte[] bytes)
            {
                var fixedMask = new bool[bytes.Length];
                Array.Fill(fixedMask, true);
                return new BytePattern(bytes, fixedMask);
            }

            // "8B 06 ?? FF": whitespace-separated hex bytes, with "??" marking a wildcard position.
            public static BytePattern Parse(string spec)
            {
                string[] tokens = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var bytes = new byte[tokens.Length];
                var fixedMask = new bool[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i] == "??")
                        continue; // wildcard: byte 0, not fixed
                    bytes[i] = Convert.ToByte(tokens[i], 16);
                    fixedMask[i] = true;
                }
                return new BytePattern(bytes, fixedMask);
            }

            public bool MatchesAt(byte[] data, int at)
            {
                for (int j = 0; j < _bytes.Length; j++)
                    if (_fixed[j] && data[at + j] != _bytes[j]) return false;
                return true;
            }
        }
    }
}

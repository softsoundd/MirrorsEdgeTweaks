using MirrorsEdgeTweaks.Helpers;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    // Fixes scene rendering at aspect ratios and render resolutions that the game was not built for.
    // DICE's FSceneRenderTargets::Allocate replaces the requested height with width*0.5625 unless a global flag is set,
    // which leaves a white screen at aspect ratios narrower than 16:9.
    // Changing the flag test's jne to jmp keeps the requested height.
    // The scene buffer never shrinks, and render resolutions below 100% draw into its top-left corner,
    // so the view can be smaller than the buffer.
    // Two DICE passes assume that the view fills the buffer, so a cave block sets the view's viewport for each:
    //   1. The sun haze pass draws its quad in buffer clip space,
    //      but TdDirHazeVertexShader unprojects that position with the view's matrix.
    //   2. The SDPG_Intermediate depth pass, whose depth confines first-person self-shadowing,
    //      draws after RHISetRenderTarget has reset the viewport to the whole buffer.
    public static class RenderTargetPatcher
    {
        const byte JNE = 0x75;
        const byte JMP = 0xEB;

        // These bytes follow the toggled opcode, from its rel8 through the fadd opcode.
        // They are the same on every build and unique in .text, so they match before and after patching.
        static readonly byte[] SiteSuffix = Convert.FromHexString("3E8BC785C089442414DB4424147D06D805");

        // Matches the haze pass's DrawDenormalizedQuad call both before and after its redirect.
        static readonly BytePattern HazeCallPattern = BytePattern.Parse(
            "D9 5C 24 08 D9 C9 D9 5C 24 04 D9 1C 24 E8 ?? ?? ?? ?? 83 C8 FF 83 C4 30");
        const int HazeCallOffset = 13;

        static readonly byte[] DrawQuadPrologue = Convert.FromHexString("558BEC83E4F081ECC0000000");

        // Matches the SDPG_Intermediate depth loop both before and after its hook.
        static readonly BytePattern DepthLoopPattern = BytePattern.Parse(
            "8B 75 44 03 74 24 1C ?? ?? ?? ?? ?? ?? 51 8B 4D 00 56 81 C1 80 06 00 00 E8");
        const int DepthHookOffset = 7;
        static readonly byte[] DepthDisplaced = { 0x8D, 0x8E, 0x94, 0x02, 0x00, 0x00 };

        // GDynamicRHI's address differs between builds, so it is read from the depth pass's RHISetRenderTarget call.
        static readonly BytePattern DynamicRhiPattern = BytePattern.Parse(
            "8B 35 ?? ?? ?? ?? 8B 00 8B 0D ?? ?? ?? ?? 8B 11 56 50 8B 82 00 01 00 00 FF D0 D9 E8");
        const int DynamicRhiOffset = 10;

        // These must stay below the byte-array fields the builders read, because static initializers run top to bottom.
        internal static readonly int HazeCaveLength = BuildHazeCave(0, 0, 0).Length;
        internal static readonly int DepthCaveLength = BuildDepthCave(0, 0, 0).Length;

        // Throws after writing the 16:9 fix when the viewport fixes cannot be applied.
        public static void Reconcile(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            Exception? viewportError = null;
            byte[]? patched;
            if (OoaService.HasOoaSection(data))
            {
                bool changed = false;
                byte[] result = PatchUtility.ApplyUnderOoa(data, img =>
                {
                    byte[]? image = PatchImage(img, "ea", out viewportError);
                    changed = image != null;
                    return image ?? img;
                });
                patched = changed ? result : null;
            }
            else
            {
                patched = PatchImage(data, ExeVersionDetector.DetectVersion(data, exePath), out viewportError);
            }

            if (patched != null)
                PatchUtility.WritePreservingAttributes(exePath, patched);
            if (viewportError != null)
                throw new InvalidOperationException(
                    $"The sun haze and first-person shadow fixes could not be applied. {viewportError.Message}", viewportError);
        }

        // Returns null when the image already has every fix.
        // When the viewport fixes cannot be applied, viewportError holds the reason and the 16:9 fix is applied anyway.
        internal static byte[]? PatchImage(byte[] data, string? versionTag, out Exception? viewportError)
        {
            bool flipped = ApplyAllocateFix(data);
            viewportError = null;
            if (versionTag == null)
            {
                viewportError = new InvalidOperationException("Unknown or unsupported MirrorsEdge.exe version.");
            }
            else
            {
                try
                {
                    byte[]? withViewports = ApplyViewportFixes(data, versionTag);
                    if (withViewports != null)
                        return withViewports;
                }
                catch (InvalidOperationException ex)
                {
                    viewportError = ex;
                }
            }
            return flipped ? data : null;
        }

        // Returns null when the executable has no recognisable render target site.
        public static bool? IsApplied(string exePath)
        {
            ExeImage exe = GameFileReadScope.ReadExe(exePath);
            return HasAnyFix(OoaService.HasOoaSection(exe.Raw) ? exe.GetDecrypted() : exe.Raw);
        }

        public static void Remove(string exePath) => PatchUtility.RewriteExe(exePath, RemoveFromImage);

        // EA images must already be decrypted.
        // Returns null when the image has no recognisable render target site.
        internal static bool? HasAnyFix(byte[] data)
        {
            int site = FindAllocateSite(data);
            if (site < 0 || data[site] is not (JNE or JMP))
                return null;
            if (data[site] == JMP)
                return true;

            var pe = PeImageLayout.Parse(data);
            if (!TryFindViewportSites(data, pe, out ViewportSites s))
                return false;
            return UnpatchedHazeTarget(data, pe, s.HazeOff, s.HazeVa, s.DynamicRhiVa) == null
                || !IsDepthHookUnpatched(data, pe, s.DepthOff, s.DepthVa, s.ResumeVa, s.DynamicRhiVa);
        }

        // Returns null when the image has none of the fixes.
        // Each block is freed only when it is the top of its cave, so a block below another patch's block stays allocated.
        internal static byte[]? RemoveFromImage(byte[] data)
        {
            int site = FindAllocateSite(data);
            if (site < 0)
                return null;

            if (data[site] is not (JNE or JMP))
                throw new InvalidOperationException(
                    $"Unexpected byte 0x{data[site]:X2} at render target patch site (expected JNE 0x75 or JMP 0xEB).");
            bool changed = data[site] == JMP;
            data[site] = JNE;

            var pe = PeImageLayout.Parse(data);
            if (!TryFindViewportSites(data, pe, out ViewportSites s))
                return changed ? data : null;

            uint? hazeBlockVa = null, depthBlockVa = null;
            if (UnpatchedHazeTarget(data, pe, s.HazeOff, s.HazeVa, s.DynamicRhiVa) == null)
            {
                hazeBlockVa = Rel32Target(data, s.HazeOff, s.HazeVa);
                int tailOff = pe.FileOffsetOf(hazeBlockVa.Value, HazeCaveLength) + HazeCaveLength - 5;
                uint drawQuadVa = Rel32Target(data, tailOff, hazeBlockVa.Value + (uint)(HazeCaveLength - 5));
                WriteBranch(data, s.HazeOff, s.HazeVa, 0xE8, drawQuadVa, 5);
            }
            if (!IsDepthHookUnpatched(data, pe, s.DepthOff, s.DepthVa, s.ResumeVa, s.DynamicRhiVa))
            {
                depthBlockVa = Rel32Target(data, s.DepthOff, s.DepthVa);
                DepthDisplaced.CopyTo(data, s.DepthOff);
            }

            // ApplyViewportFixes allocates the depth block after the haze block.
            if (depthBlockVa != null)
                data = Reclaim(data, depthBlockVa.Value, DepthCaveLength);
            if (hazeBlockVa != null)
                data = Reclaim(data, hazeBlockVa.Value, HazeCaveLength);

            return changed || hazeBlockVa != null || depthBlockVa != null ? data : null;
        }

        // Returns false when the flag test already jumps.
        internal static bool ApplyAllocateFix(byte[] data)
        {
            int site = FindAllocateSite(data);
            if (site < 0)
                throw new InvalidOperationException("Cannot find render target patch site in executable.");

            if (data[site] == JMP) return false;
            if (data[site] != JNE)
                throw new InvalidOperationException(
                    $"Unexpected byte 0x{data[site]:X2} at render target patch site (expected JNE 0x75).");
            data[site] = JMP;
            return true;
        }

        // Returns null when the image already has both viewport fixes.
        // A site that already reaches its block is left as it is.
        internal static byte[]? ApplyViewportFixes(byte[] data, string versionTag)
        {
            var pe = PeImageLayout.Parse(data);
            int hazeOff = FindUnique(data, pe, HazeCallPattern, "the sun haze patch site") + HazeCallOffset;
            int depthOff = FindUnique(data, pe, DepthLoopPattern, "the first-person depth patch site") + DepthHookOffset;
            int rhiOff = FindUnique(data, pe, DynamicRhiPattern, "the GDynamicRHI load") + DynamicRhiOffset;
            uint dynamicRhiVa = BitConverter.ToUInt32(data, rhiOff);
            if (!pe.IsDataVa(dynamicRhiVa))
                throw new InvalidOperationException($"GDynamicRHI address 0x{dynamicRhiVa:X8} is not in a data section.");

            uint hazeVa = pe.OffsetToVa(hazeOff);
            uint depthVa = pe.OffsetToVa(depthOff);
            uint resumeVa = depthVa + (uint)DepthDisplaced.Length;
            uint? drawQuadVa = UnpatchedHazeTarget(data, pe, hazeOff, hazeVa, dynamicRhiVa);
            bool depthUnpatched = IsDepthHookUnpatched(data, pe, depthOff, depthVa, resumeVa, dynamicRhiVa);
            if (drawQuadVa == null && !depthUnpatched)
                return null;

            var cave = CaveSection.Open(data, versionTag: versionTag);
            uint hazeCodeVa = 0, depthCodeVa = 0;
            if (drawQuadVa != null)
            {
                hazeCodeVa = cave.Alloc(HazeCaveLength);
                cave.Write(hazeCodeVa, BuildHazeCave(hazeCodeVa, dynamicRhiVa, drawQuadVa.Value));
            }
            if (depthUnpatched)
            {
                depthCodeVa = cave.Alloc(DepthCaveLength);
                cave.Write(depthCodeVa, BuildDepthCave(depthCodeVa, dynamicRhiVa, resumeVa));
            }
            data = cave.Finalize();

            // Finalize can move the image to a larger array, but the .text offsets stay the same.
            if (drawQuadVa != null)
                WriteBranch(data, hazeOff, hazeVa, 0xE8, hazeCodeVa, 5);
            if (depthUnpatched)
                WriteBranch(data, depthOff, depthVa, 0xE9, depthCodeVa, DepthDisplaced.Length);

            return data;
        }

        // Sets the viewport to the view rect, then gives the quad the viewport's origin and the view as its target.
        // TdDirHazeVertexShader then unprojects a clip-space position that is relative to the view.
        // The texture coordinates are left alone, because they already address the view's part of the buffer.
        internal static byte[] BuildHazeCave(uint codeVa, uint dynamicRhiVa, uint drawQuadVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2C, 0x4C, 0x24, 0x0C });  // cvttss2si ecx,[esp+0xC]  SizeX
            mc.Emit(new byte[] { 0x89, 0x4C, 0x24, 0x24 });              // mov [esp+0x24],ecx       TargetSizeX
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2C, 0x54, 0x24, 0x10 });  // cvttss2si edx,[esp+0x10] SizeY
            mc.Emit(new byte[] { 0x89, 0x54, 0x24, 0x28 });              // mov [esp+0x28],edx       TargetSizeY
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2C, 0x44, 0x24, 0x04 });  // cvttss2si eax,[esp+4]    X
            mc.Emit(new byte[] { 0x03, 0xC8 });                          // add ecx,eax              MaxX
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2C, 0x44, 0x24, 0x08 });  // cvttss2si eax,[esp+8]    Y
            mc.Emit(new byte[] { 0x03, 0xD0 });                          // add edx,eax              MaxY
            mc.Emit(new byte[] { 0x68, 0x00, 0x00, 0x80, 0x3F });        // push 1.0f                MaxZ
            mc.Emit((byte)0x52);                                         // push edx                 MaxY
            mc.Emit((byte)0x51);                                         // push ecx                 MaxX
            mc.Emit(new byte[] { 0x6A, 0x00 });                          // push 0.0f                MinZ
            mc.Emit((byte)0x50);                                         // push eax                 MinY
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x2C, 0x44, 0x24, 0x18 });  // cvttss2si eax,[esp+0x18] X
            mc.Emit((byte)0x50);                                         // push eax                 MinX
            EmitSetViewport(mc, dynamicRhiVa);
            mc.Emit(new byte[] { 0x33, 0xC0 });                          // xor eax,eax
            mc.Emit(new byte[] { 0x89, 0x44, 0x24, 0x04 });              // mov [esp+4],eax          X = 0
            mc.Emit(new byte[] { 0x89, 0x44, 0x24, 0x08 });              // mov [esp+8],eax          Y = 0
            mc.EmitJmpNear(drawQuadVa);
            return mc.Build();
        }

        // Sets the viewport to the rect of the view in esi, before the SDPG_Intermediate depth loop draws that view.
        internal static byte[] BuildDepthCave(uint codeVa, uint dynamicRhiVa, uint resumeVa)
        {
            var mc = new MachineCodeBuilder(codeVa);
            mc.Emit(new byte[] { 0x68, 0x00, 0x00, 0x80, 0x3F });        // push 1.0f                MaxZ
            mc.Emit(new byte[] { 0x8B, 0x46, 0x40 });                    // mov eax,[esi+0x40]       RenderTargetSizeY
            mc.Emit(new byte[] { 0x03, 0x46, 0x38 });                    // add eax,[esi+0x38]       +RenderTargetY
            mc.Emit((byte)0x50);                                         // push eax                 MaxY
            mc.Emit(new byte[] { 0x8B, 0x46, 0x3C });                    // mov eax,[esi+0x3C]       RenderTargetSizeX
            mc.Emit(new byte[] { 0x03, 0x46, 0x34 });                    // add eax,[esi+0x34]       +RenderTargetX
            mc.Emit((byte)0x50);                                         // push eax                 MaxX
            mc.Emit(new byte[] { 0x6A, 0x00 });                          // push 0.0f                MinZ
            mc.Emit(new byte[] { 0xFF, 0x76, 0x38 });                    // push dword [esi+0x38]    MinY
            mc.Emit(new byte[] { 0xFF, 0x76, 0x34 });                    // push dword [esi+0x34]    MinX
            EmitSetViewport(mc, dynamicRhiVa);
            mc.Emit(DepthDisplaced);                                     // lea ecx,[esi+0x294]
            mc.EmitJmpNear(resumeVa);
            return mc.Build();
        }

        // The caller pushes RHISetViewport's six arguments first.
        // The thiscall pops them, and it clobbers eax, ecx and edx.
        private static void EmitSetViewport(MachineCodeBuilder mc, uint dynamicRhiVa)
        {
            mc.Emit(new byte[] { 0x8B, 0x0D }); mc.EmitUInt32(dynamicRhiVa); // mov ecx,[GDynamicRHI]
            mc.Emit(new byte[] { 0x8B, 0x01 });                              // mov eax,[ecx]           vtable
            mc.Emit(new byte[] { 0xFF, 0x90, 0xC4, 0x00, 0x00, 0x00 });      // call [eax+0xC4]         SetViewport
        }

        // Returns DrawDenormalizedQuad's address while the haze call still reaches it.
        // Returns null once the call reaches the haze block.
        // Throws when the call reaches anything else.
        private static uint? UnpatchedHazeTarget(byte[] data, PeImageLayout pe, int callOff, uint callVa, uint dynamicRhiVa)
        {
            uint target = Rel32Target(data, callOff, callVa);
            if (MatchesAtVa(data, pe, target, DrawQuadPrologue))
                return target;

            int blockOff = pe.FileOffsetOf(target, HazeCaveLength);
            if (blockOff >= 0)
            {
                int tailOff = blockOff + HazeCaveLength - 5;
                uint drawQuadVa = Rel32Target(data, tailOff, target + (uint)(HazeCaveLength - 5));
                if (MatchesAtVa(data, pe, drawQuadVa, DrawQuadPrologue)
                    && data.AsSpan(blockOff, HazeCaveLength).SequenceEqual(BuildHazeCave(target, dynamicRhiVa, drawQuadVa)))
                    return null;
            }

            throw new InvalidOperationException($"Unexpected call target at the sun haze patch site 0x{callVa:X8}.");
        }

        // Returns true while the depth hook still holds the displaced lea.
        // Returns false once it jumps to the depth block.
        // Throws when it holds anything else.
        private static bool IsDepthHookUnpatched(byte[] data, PeImageLayout pe, int hookOff, uint hookVa, uint resumeVa,
            uint dynamicRhiVa)
        {
            if (data.AsSpan(hookOff, DepthDisplaced.Length).SequenceEqual(DepthDisplaced))
                return true;

            if (data[hookOff] == 0xE9 && data[hookOff + 5] == 0x90)
            {
                uint target = Rel32Target(data, hookOff, hookVa);
                int blockOff = pe.FileOffsetOf(target, DepthCaveLength);
                if (blockOff >= 0 && data.AsSpan(blockOff, DepthCaveLength)
                        .SequenceEqual(BuildDepthCave(target, dynamicRhiVa, resumeVa)))
                    return false;
            }
            throw new InvalidOperationException($"Unexpected bytes at the first-person depth patch site 0x{hookVa:X8}.");
        }

        private static int FindUnique(byte[] data, PeImageLayout pe, BytePattern pattern, string what)
        {
            int at = pattern.FindUniqueInText(data, pe);
            if (at < 0)
                throw new InvalidOperationException($"Cannot find {what} in the executable, or it is not unique.");
            return at;
        }

        // Returns the offset of the flag test's jump, which the suffix locates both before and after patching.
        // Returns -1 when the executable has no such site.
        // Throws when the suffix occurs more than once.
        private static int FindAllocateSite(byte[] data)
        {
            var text = PeImageLayout.Parse(data).FindSectionByName(".text");
            if (text == null)
                return -1;
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);

            int suffix = PatternHelper.FindUnique(data, SiteSuffix, start, end);
            return suffix < 0 ? -1 : suffix - 1;
        }

        private readonly record struct ViewportSites(int HazeOff, uint HazeVa, int DepthOff, uint DepthVa, uint DynamicRhiVa)
        {
            public uint ResumeVa => DepthVa + (uint)DepthDisplaced.Length;
        }

        // Returns false when a site is missing or not unique, because ApplyViewportFixes cannot have patched such an image.
        private static bool TryFindViewportSites(byte[] data, PeImageLayout pe, out ViewportSites sites)
        {
            int haze = HazeCallPattern.FindUniqueInText(data, pe);
            int depth = DepthLoopPattern.FindUniqueInText(data, pe);
            int rhi = DynamicRhiPattern.FindUniqueInText(data, pe);
            if (haze < 0 || depth < 0 || rhi < 0)
            {
                sites = default;
                return false;
            }

            int hazeOff = haze + HazeCallOffset, depthOff = depth + DepthHookOffset;
            sites = new ViewportSites(hazeOff, pe.OffsetToVa(hazeOff), depthOff, pe.OffsetToVa(depthOff),
                BitConverter.ToUInt32(data, rhi + DynamicRhiOffset));
            return true;
        }

        // Each allocation starts on a 4-byte boundary, so once a later block is freed,
        // the watermark can sit up to 3 bytes past the end of the block before it.
        private static byte[] Reclaim(byte[] data, uint blockVa, int length)
        {
            data = CaveSection.ReclaimIfTopmost(data, blockVa, length);
            int aligned = (length + 3) & ~3;
            return aligned == length ? data : CaveSection.ReclaimIfTopmost(data, blockVa, aligned);
        }

        private static bool MatchesAtVa(byte[] data, PeImageLayout pe, uint va, byte[] expected)
        {
            int off = pe.FileOffsetOf(va, expected.Length);
            return off >= 0 && data.AsSpan(off, expected.Length).SequenceEqual(expected);
        }

        private static uint Rel32Target(byte[] data, int off, uint va) =>
            va + 5u + (uint)BitConverter.ToInt32(data, off + 1);

        private static void WriteBranch(byte[] data, int off, uint va, byte opcode, uint targetVa, int length)
        {
            data[off] = opcode;
            MachineCodeBuilder.Rel32Bytes(va, targetVa).CopyTo(data, off + 1);
            data.AsSpan(off + 5, length - 5).Fill(0x90);
        }
    }
}

using MirrorsEdgeTweaks.Helpers;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MirrorsEdgeTweaks.Services
{
    public enum ReversedDepthState
    {
        Unknown,
        Unpatched,
        Patched
    }

    // Reverses the scene depth buffer, with near at 1 and far at 0.
    // This only gains precision when the D3D9 runtime backs D24S8 with a float format, as DXVK does with d3d9.useD32forD24.
    // Eight FD3D9DynamicRHI vtable slots point at wrappers in one cave block, and each wrapper calls its original.
    // While a D24S8 surface is bound, the wrappers map every depth d to 1 - d.
    // They set the view-projection z column to w - z, flip depth tests,
    // mirror depth clears and depth bounds, and negate depth bias.
    // Every depth goes through the same map, so every depth comparison and clip keeps its outcome.
    // Shadow maps use D24X8 surfaces, so they keep standard depth.
    public static class ReversedDepthPatcher
    {
        // These are byte offsets into the FD3D9DynamicRHI vtable.
        private const int SetRasterizerStateSlot = 0xBC;
        private const int SetRasterizerStateImmediateSlot = 0xC0;
        private const int SetDepthBoundsTestSlot = 0xCC;
        private const int SetVertexShaderParameterSlot = 0xD8;
        private const int SetViewParametersSlot = 0xE4;
        private const int SetDepthStateSlot = 0xF4;
        private const int SetStencilStateSlot = 0xF8;
        private const int SetRenderTargetSlot = 0x100;
        private const int ClearSlot = 0x14C;

        // The patch never changes these functions, so they are found the same way before and after patching.
        // SetStencilState anchors the vtable because its slot is never redirected.
        private static readonly BytePattern SetStencilStatePattern = BytePattern.Parse(
            "8B 44 24 04 85 C0 56 57 8B F9 74 05 8D 70 F8 EB 02 33 F6 8B 56 08 8B 47 10 8B 08 52 6A 34 50");
        private static readonly BytePattern SetDepthStatePattern = BytePattern.Parse(
            "8B 44 24 04 85 C0 56 57 8B F9 74 05 8D 70 F8 EB 02 33 F6 8B 56 08 8B 47 10 8B 08 52 6A 07 50 " +
            "8B 81 E4 00 00 00 FF D0 8B 56 0C 8B 47 10 8B 08 52 6A 0E 50");
        private static readonly BytePattern SetRasterizerStatePattern = BytePattern.Parse(
            "8B 44 24 04 85 C0 56 57 8B F9 74 05 8D 70 F8 EB 02 33 F6 8B 56 08 8B 47 10 8B 08 52 6A 08 50 " +
            "8B 81 E4 00 00 00 FF D0 8B 56 0C 8B 47 10 8B 08 52 6A 16 50 8B 81 E4 00 00 00 FF D0 " +
            "F3 0F 10 46 10 F3 0F 58 05 ?? ?? ?? ??");
        private const int DepthBiasOffsetOperand = 0x44;
        private static readonly BytePattern SetRasterizerStateImmediatePattern = BytePattern.Parse(
            "56 57 8B 7C 24 0C 8B 07 83 E8 00 8B F1 8B 4E 10 74 13 83 E8 01 74 07 B8 03 00 00 00 EB 0C " +
            "B8 02 00 00 00 EB 05 B8 01 00 00 00 8B 11 50 8B 82 E4 00 00 00 6A 08");
        private static readonly BytePattern SetDepthBoundsTestPattern = BytePattern.Parse(
            "83 EC 20 83 7C 24 24 00 56 8B F1 0F 84 ?? ?? ?? ?? 83 7E 3C 00 8B 44 24 2C 0F 84 ?? ?? ?? ?? " +
            "F3 0F 10 40 08 0F 57 C9 0F 2F C1");
        // SetPixelShaderParameter shares this code up to the final constant upload, which uses vtable +0x1B4.
        private static readonly BytePattern SetVertexShaderParameterPattern = BytePattern.Parse(
            "53 55 8B 69 10 56 57 8B 7C 24 20 8D 5F 0F 8B F3 83 E6 F0 3B F7 74 ?? F6 05 ?? ?? ?? ?? 01 75 ?? " +
            "83 0D ?? ?? ?? ?? 01 33 C0 68 ?? ?? ?? ?? A3 ?? ?? ?? ?? A3 ?? ?? ?? ?? A3 ?? ?? ?? ?? E8 ?? ?? ?? ?? " +
            "83 C4 04 39 35 ?? ?? ?? ?? 73 ?? 56 B9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 6A 08 6A 01 56 B9 ?? ?? ?? ?? " +
            "E8 ?? ?? ?? ?? 8B 44 24 24 8B 0D ?? ?? ?? ?? 57 50 51 E8 ?? ?? ?? ?? 8B 15 ?? ?? ?? ?? 2B F7 56 " +
            "03 D7 6A 00 52 E8 ?? ?? ?? ?? A1 ?? ?? ?? ?? 83 C4 18 EB 04 8B 44 24 24 8B 54 24 1C 8B 4D 00 " +
            "C1 EB 04 53 50 8B 81 78 01 00 00");
        private static readonly BytePattern SetViewParametersPattern = BytePattern.Parse(
            "8B 54 24 08 56 57 6A 04 8B F1 8B 46 10 8B 08 52 6A 00 50 8B 81 78 01 00 00 FF D0 " +
            "8B 54 24 14 8B 46 10 8B 08 6A 01 52 6A 04 50");
        private static readonly BytePattern SetRenderTargetPattern = BytePattern.Parse(
            "83 EC 18 80 3D ?? ?? ?? ?? 00 55 57 8B F9 74 04 33 C9 EB 04 8B 4C 24 28 8B 44 24 24 85 C0 74 05 8D 68 F4");
        private const int ForceNoDepthOperand = 5;
        private static readonly BytePattern ClearPattern = BytePattern.Parse(
            "53 56 33 F6 39 74 24 0C 57 8B F9 74 05 BE 01 00 00 00 83 7C 24 18 00 74 03 83 CE 02 " +
            "83 7C 24 20 00 74 03 83 CE 04");

        // The runtime data sits at the start of the block.
        // The depth-bounds vectors are only read at +8 (z) and +0xC (w), so each one overlaps the data before it.
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("METRZ001");
        private const int ReversedOff = 0x08;
        private const int ZFuncOff = 0x0C;
        private const int BiasOff = 0x10;
        private const int SlopeOff = 0x14;
        private const int HaveMatrixOff = 0x18;
        private const int NearZOff = 0x1C, NearWOff = 0x20, FarZOff = 0x24, FarWOff = 0x28;
        private const int NearVecOff = NearZOff - 8, FarVecOff = FarZOff - 8;
        private const int FlipTableOff = 0x2C;
        private const int MatrixOff = 0x40;
        private const int CodeOff = 0x80;

        private const uint D3DCMP_LESSEQUAL = 4;
        private const uint D3DFMT_D24S8 = 0x4B;
        private const byte D3DRS_ZFUNC = 0x17;
        private const uint D3DRS_SLOPESCALEDEPTHBIAS = 0xAF;
        private const uint D3DRS_DEPTHBIAS = 0xC3;

        // Maps each D3DCMPFUNC to its mirror, swapping LESS with GREATER and LESSEQUAL with GREATEREQUAL.
        private static readonly byte[] FlipTable = { 0, 1, 5, 3, 7, 2, 6, 4, 8 };

        internal sealed record RhiSites(
            uint Vtable,
            uint SetRasterizerState,
            uint SetRasterizerStateImmediate,
            uint SetDepthBoundsTest,
            uint SetVertexShaderParameter,
            uint SetViewParameters,
            uint SetDepthState,
            uint SetRenderTarget,
            uint Clear,
            uint ForceNoDepthFlag,
            uint DepthBiasOffset);

        // Holds the block offsets of the wrappers that the vtable slots point at.
        internal readonly record struct Entries(
            int SetRasterizerState,
            int SetRasterizerStateImmediate,
            int SetDepthBoundsTest,
            int SetVertexShaderParameter,
            int SetViewParameters,
            int SetDepthState,
            int SetRenderTarget,
            int Clear);

        // Every instruction has a fixed length, so the block size and entry offsets do not depend on addresses.
        internal static readonly int BlockLength;
        internal static readonly Entries EntryOffsets;

        static ReversedDepthPatcher()
        {
            var placeholder = new RhiSites(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            BlockLength = BuildBlock(0, placeholder, out EntryOffsets).Length;
        }

        public static ReversedDepthState GetState(string exePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(exePath);
                if (OoaService.HasOoaSection(data))
                    PatchUtility.DecryptOoaInPlace(data);
                return DetectState(data);
            }
            catch
            {
                return ReversedDepthState.Unknown;
            }
        }

        public static void Apply(string exePath) => Rewrite(exePath, apply: true);

        public static void Remove(string exePath) => Rewrite(exePath, apply: false);

        private static void Rewrite(string exePath, bool apply)
        {
            byte[] data = File.ReadAllBytes(exePath);
            byte[]? result;
            if (OoaService.HasOoaSection(data))
            {
                bool changed = false;
                byte[] reencrypted = PatchUtility.ApplyUnderOoa(data, img =>
                {
                    byte[]? image = apply ? ApplyToImage(img, "ea") : RemoveFromImage(img);
                    changed = image != null;
                    return image ?? img;
                });
                result = changed ? reencrypted : null;
            }
            else if (apply)
            {
                string version = ExeVersionDetector.DetectVersion(data, exePath)
                    ?? throw new InvalidOperationException("Unknown or unsupported MirrorsEdge.exe version.");
                result = ApplyToImage(data, version);
            }
            else
            {
                result = RemoveFromImage(data);
            }

            if (result != null)
                PatchUtility.WritePreservingAttributes(exePath, result);
        }

        // Returns null when the image already has the patch.
        internal static byte[]? ApplyToImage(byte[] data, string versionTag)
        {
            var pe = PeImageLayout.Parse(data);
            RhiSites sites = FindSites(data, pe);
            ReversedDepthState state = DetectState(data, pe, sites, out _);
            if (state == ReversedDepthState.Patched)
                return null;
            if (state != ReversedDepthState.Unpatched)
                throw new InvalidOperationException("The depth patch's vtable slots hold unexpected values.");

            int[] slotOffsets = SlotFileOffsets(pe, sites.Vtable);

            var cave = CaveSection.OpenSecondary(data, versionTag: versionTag);
            int? leftover = FindLeftoverBlock(cave, sites);
            if (leftover != null)
                cave.ReuseFrom(leftover.Value);
            uint blockVa;
            try
            {
                blockVa = cave.Alloc(BlockLength);
            }
            catch (InvalidOperationException ex) when (cave.Capacity != int.MaxValue)
            {
                throw new InvalidOperationException(
                    "This executable has no patch space left for reversed depth, which shares its limited space with logging.", ex);
            }
            cave.Write(blockVa, BuildBlock(blockVa, sites, out Entries entries));
            data = cave.Finalize();

            // Finalize can move the image to a larger array, but the .rdata offsets stay the same.
            uint[] targets = WrapperVas(blockVa, entries);
            for (int i = 0; i < slotOffsets.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(slotOffsets[i], 4), targets[i]);
            return data;
        }

        // Returns null when the image does not have the patch.
        // The block is freed when it is the top of the cave, and otherwise left for the next apply to reuse.
        internal static byte[]? RemoveFromImage(byte[] data)
        {
            var pe = PeImageLayout.Parse(data);
            RhiSites sites = FindSites(data, pe);
            ReversedDepthState state = DetectState(data, pe, sites, out uint blockVa);
            if (state == ReversedDepthState.Unpatched)
                return null;
            if (state != ReversedDepthState.Patched)
                throw new InvalidOperationException("The depth patch's vtable slots hold unexpected values.");

            int[] slotOffsets = SlotFileOffsets(pe, sites.Vtable);
            uint[] originals = OriginalVas(sites);
            for (int i = 0; i < slotOffsets.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(slotOffsets[i], 4), originals[i]);
            return CaveSection.ReclaimIfTopmost(data, blockVa, BlockLength);
        }

        internal static ReversedDepthState DetectState(byte[] data)
        {
            var pe = PeImageLayout.Parse(data);
            return DetectState(data, pe, FindSites(data, pe), out _);
        }

        private static ReversedDepthState DetectState(byte[] data, PeImageLayout pe, RhiSites sites, out uint blockVa)
        {
            blockVa = 0;
            int[] slotOffsets = SlotFileOffsets(pe, sites.Vtable);
            uint[] current = slotOffsets.Select(o => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o, 4))).ToArray();

            if (current.SequenceEqual(OriginalVas(sites)))
                return ReversedDepthState.Unpatched;

            uint candidate = current[0] - (uint)EntryOffsets.SetRasterizerState;
            int blockOff = pe.FileOffsetOf(candidate, BlockLength);
            if (blockOff < 0
                || !current.SequenceEqual(WrapperVas(candidate, EntryOffsets))
                || !data.AsSpan(blockOff, BlockLength).SequenceEqual(BuildBlock(candidate, sites, out _)))
                return ReversedDepthState.Unknown;

            blockVa = candidate;
            return ReversedDepthState.Patched;
        }

        internal static RhiSites FindSites(byte[] data, PeImageLayout pe)
        {
            uint Find(BytePattern pattern, string name)
            {
                int off = pattern.FindUniqueInText(data, pe);
                if (off < 0)
                    throw new InvalidOperationException($"Cannot find {name} in the executable, or it is not unique.");
                return pe.OffsetToVa(off);
            }

            uint stencil = Find(SetStencilStatePattern, "FD3D9DynamicRHI::SetStencilState");
            uint vtable = FindVtable(data, pe, stencil);
            uint setRenderTarget = Find(SetRenderTargetPattern, "FD3D9DynamicRHI::SetRenderTarget");
            uint setRasterizerState = Find(SetRasterizerStatePattern, "FD3D9DynamicRHI::SetRasterizerState");

            uint forceNoDepth = ReadU32AtVa(pe, data, setRenderTarget + ForceNoDepthOperand);
            uint depthBiasOffset = ReadU32AtVa(pe, data, setRasterizerState + DepthBiasOffsetOperand);
            if (!pe.IsDataVa(forceNoDepth) || !pe.IsDataVa(depthBiasOffset))
                throw new InvalidOperationException("The depth patch's data addresses are not in a data section.");

            return new RhiSites(
                vtable,
                setRasterizerState,
                Find(SetRasterizerStateImmediatePattern, "FD3D9DynamicRHI::SetRasterizerStateImmediate"),
                Find(SetDepthBoundsTestPattern, "FD3D9DynamicRHI::SetDepthBoundsTest"),
                Find(SetVertexShaderParameterPattern, "FD3D9DynamicRHI::SetVertexShaderParameter"),
                Find(SetViewParametersPattern, "FD3D9DynamicRHI::SetViewParameters"),
                Find(SetDepthStatePattern, "FD3D9DynamicRHI::SetDepthState"),
                setRenderTarget,
                Find(ClearPattern, "FD3D9DynamicRHI::Clear"),
                forceNoDepth,
                depthBiasOffset);
        }

        // The vtable holds the only aligned .rdata pointer to SetStencilState.
        private static uint FindVtable(byte[] data, PeImageLayout pe, uint setStencilState)
        {
            var rdata = pe.FindSectionByName(".rdata")
                ?? throw new InvalidOperationException("No .rdata section in executable.");
            int start = (int)rdata.PointerToRawData;
            int end = Math.Min(start + (int)rdata.SizeOfRawData, data.Length);
            byte[] needle = BitConverter.GetBytes(setStencilState);

            int first = -1;
            for (int at = PatternHelper.FindPattern(data, needle, start, end); at >= 0;
                 at = PatternHelper.FindPattern(data, needle, at + 1, end))
            {
                if ((at - start) % 4 != 0) continue;
                if (first >= 0)
                    throw new InvalidOperationException("The FD3D9DynamicRHI vtable is not unique.");
                first = at;
            }
            if (first < 0)
                throw new InvalidOperationException("Cannot find the FD3D9DynamicRHI vtable.");
            return pe.OffsetToVa(first) - SetStencilStateSlot;
        }

        // The order matches SlotFileOffsets and WrapperVas.
        private static uint[] OriginalVas(RhiSites s) => new[]
        {
            s.SetRasterizerState, s.SetRasterizerStateImmediate, s.SetDepthBoundsTest, s.SetVertexShaderParameter,
            s.SetViewParameters, s.SetDepthState, s.SetRenderTarget, s.Clear,
        };

        private static uint[] WrapperVas(uint blockVa, Entries e) => new[]
        {
            blockVa + (uint)e.SetRasterizerState, blockVa + (uint)e.SetRasterizerStateImmediate,
            blockVa + (uint)e.SetDepthBoundsTest, blockVa + (uint)e.SetVertexShaderParameter,
            blockVa + (uint)e.SetViewParameters, blockVa + (uint)e.SetDepthState,
            blockVa + (uint)e.SetRenderTarget, blockVa + (uint)e.Clear,
        };

        private static int[] SlotFileOffsets(PeImageLayout pe, uint vtable) => new[]
        {
            SetRasterizerStateSlot, SetRasterizerStateImmediateSlot, SetDepthBoundsTestSlot, SetVertexShaderParameterSlot,
            SetViewParametersSlot, SetDepthStateSlot, SetRenderTargetSlot, ClearSlot,
        }.Select(slot => pe.VaToOffset(vtable + (uint)slot)).ToArray();

        // Returns the section offset of a block that Remove left behind, when it still matches these sites.
        private static int? FindLeftoverBlock(CaveSection cave, RhiSites sites)
        {
            for (int at = cave.IndexOf(Magic); at >= 0; at = cave.IndexOf(Magic, at + 1))
            {
                uint va = cave.SectionVa + (uint)at;
                byte[] expected = BuildBlock(va, sites, out _);
                if (cave.IndexOf(expected, at) == at)
                    return at;
            }
            return null;
        }

        // Lays out the runtime data, then the wrappers.
        // The wrappers are thiscall like the originals, and they keep this in esi.
        internal static byte[] BuildBlock(uint blockVa, RhiSites s, out Entries entries)
        {
            uint D(int off) => blockVa + (uint)off;

            byte[] block = new byte[CodeOff];
            Magic.CopyTo(block, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(ZFuncOff), D3DCMP_LESSEQUAL);
            BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(NearWOff), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(FarWOff), 1f);
            FlipTable.CopyTo(block, FlipTableOff);

            var mc = new MachineCodeBuilder(D(CodeOff));
            void CmpReversed() { mc.Emit(new byte[] { 0x83, 0x3D }); mc.EmitUInt32(D(ReversedOff)); mc.Emit(0x00); }

            // srs calls SetRenderState(edx, eax).
            mc.MarkLabel("srs");
            mc.Emit(new byte[] { 0x50, 0x52 });                              // push eax; push edx
            mc.Emit(new byte[] { 0x8B, 0x4E, 0x10, 0x51 });                  // mov ecx,[esi+0x10]; push ecx   device
            mc.Emit(new byte[] { 0x8B, 0x01 });                              // mov eax,[ecx]
            mc.Emit(new byte[] { 0xFF, 0x90, 0xE4, 0x00, 0x00, 0x00 });      // call [eax+0xE4]                SetRenderState
            mc.Emit(0xC3);                                                   // ret

            // flip mirrors the D3DCMPFUNC in eax while reversed.
            mc.MarkLabel("flip");
            CmpReversed();
            mc.EmitJzShort("flip_ret");
            mc.Emit(new byte[] { 0x83, 0xF8, 0x08 });                        // cmp eax,8
            mc.EmitJaShort("flip_ret");
            mc.Emit(new byte[] { 0x0F, 0xB6, 0x80 }); mc.EmitUInt32(D(FlipTableOff)); // movzx eax,byte [eax+FlipTable]
            mc.MarkLabel("flip_ret");
            mc.Emit(0xC3);

            // neg negates the float in eax while reversed.
            mc.MarkLabel("neg");
            CmpReversed();
            mc.EmitJzShort("neg_ret");
            mc.Emit(new byte[] { 0x35, 0x00, 0x00, 0x00, 0x80 });            // xor eax,0x80000000
            mc.MarkLabel("neg_ret");
            mc.Emit(0xC3);

            // bias sends the remembered depth bias for the bound surface.
            mc.MarkLabel("bias");
            mc.Emit(0xA1); mc.EmitUInt32(D(BiasOff));                        // mov eax,[Bias]
            mc.EmitCall("neg");
            mc.Emit(0xBA); mc.EmitUInt32(D3DRS_DEPTHBIAS);                   // mov edx,D3DRS_DEPTHBIAS
            mc.EmitCall("srs");
            mc.Emit(0xA1); mc.EmitUInt32(D(SlopeOff));                       // mov eax,[Slope]
            mc.EmitCall("neg");
            mc.Emit(0xBA); mc.EmitUInt32(D3DRS_SLOPESCALEDEPTHBIAS);         // mov edx,D3DRS_SLOPESCALEDEPTHBIAS
            mc.EmitJmp("srs");

            // send uploads the matrix at eax to c0-c3.
            mc.MarkLabel("send");
            mc.Emit(new byte[] { 0x6A, 0x04, 0x50, 0x6A, 0x00 });            // push 4; push eax; push 0
            mc.Emit(new byte[] { 0x8B, 0x4E, 0x10, 0x51 });                  // mov ecx,[esi+0x10]; push ecx
            mc.Emit(new byte[] { 0x8B, 0x11 });                              // mov edx,[ecx]
            mc.Emit(new byte[] { 0xFF, 0x92, 0x78, 0x01, 0x00, 0x00 });      // call [edx+0x178]               SetVertexShaderConstantF
            mc.Emit(0xC3);

            // upload sends the remembered view-projection matrix, with its z column set to w - z while reversed.
            mc.MarkLabel("upload");
            mc.Emit(new byte[] { 0x83, 0x3D }); mc.EmitUInt32(D(HaveMatrixOff)); mc.Emit(0x00); // cmp [HaveMatrix],0
            mc.EmitJzShort("up_ret");
            mc.Emit(0xB8); mc.EmitUInt32(D(MatrixOff));                      // mov eax,Matrix
            CmpReversed();
            mc.EmitJzShort("send");
            mc.Emit(new byte[] { 0x83, 0xEC, 0x40 });                        // sub esp,0x40
            mc.Emit(new byte[] { 0x31, 0xC9 });                              // xor ecx,ecx
            mc.MarkLabel("up_row");
            mc.Emit(new byte[] { 0x0F, 0x10, 0x04, 0x08 });                  // movups xmm0,[eax+ecx]
            mc.Emit(new byte[] { 0x0F, 0x11, 0x04, 0x0C });                  // movups [esp+ecx],xmm0
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x4C, 0x08, 0x0C });      // movss xmm1,[eax+ecx+0xC]       w
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5C, 0x4C, 0x08, 0x08 });      // subss xmm1,[eax+ecx+8]         - z
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x4C, 0x0C, 0x08 });      // movss [esp+ecx+8],xmm1
            mc.Emit(new byte[] { 0x83, 0xC1, 0x10 });                        // add ecx,0x10
            mc.Emit(new byte[] { 0x83, 0xF9, 0x40 });                        // cmp ecx,0x40
            mc.EmitJbShort("up_row");
            mc.Emit(new byte[] { 0x8B, 0xC4 });                              // mov eax,esp
            mc.EmitCall("send");
            mc.Emit(new byte[] { 0x83, 0xC4, 0x40 });                        // add esp,0x40
            mc.MarkLabel("up_ret");
            mc.Emit(0xC3);

            // reapply resends the depth test, depth bias and matrix after the bound surface changes kind.
            mc.MarkLabel("reapply");
            mc.Emit(0xA1); mc.EmitUInt32(D(ZFuncOff));                       // mov eax,[ZFunc]
            mc.EmitCall("flip");
            mc.Emit(new byte[] { 0x6A, D3DRS_ZFUNC, 0x5A });                 // push D3DRS_ZFUNC; pop edx
            mc.EmitCall("srs");
            mc.EmitCall("bias");
            mc.EmitJmp("upload");

            // SetRenderTarget(NewRenderTarget, NewDepthStencilTarget) turns reversal on while the bound depth surface is D24S8.
            int setRenderTarget = mc.CurrentOffset;
            mc.Emit(new byte[] { 0x56, 0x8B, 0xF1 });                        // push esi; mov esi,ecx
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x0C });                  // push [esp+0xC]                 NewDepthStencilTarget
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x0C });                  // push [esp+0xC]                 NewRenderTarget
            mc.Emit(new byte[] { 0x8B, 0xCE });                              // mov ecx,esi
            mc.EmitCall(s.SetRenderTarget);
            mc.Emit(new byte[] { 0x31, 0xC0 });                              // xor eax,eax
            mc.Emit(new byte[] { 0x80, 0x3D }); mc.EmitUInt32(s.ForceNoDepthFlag); mc.Emit(0x00); // cmp byte [ForceNoDepth],0
            mc.EmitJnzShort("srt_kind");
            mc.Emit(new byte[] { 0x8B, 0x4C, 0x24, 0x0C });                  // mov ecx,[esp+0xC]              NewDepthStencilTarget
            mc.EmitJecxzShort("srt_kind");
            mc.Emit(new byte[] { 0x8B, 0x41, 0xFC });                        // mov eax,[ecx-4]                IDirect3DSurface9
            mc.MarkLabel("srt_kind");
            mc.Emit(new byte[] { 0x31, 0xD2, 0x85, 0xC0 });                  // xor edx,edx; test eax,eax
            mc.EmitJzShort("srt_set");
            mc.Emit(new byte[] { 0x83, 0xEC, 0x20 });                        // sub esp,0x20                   D3DSURFACE_DESC
            mc.Emit(new byte[] { 0x83, 0x24, 0x24, 0x00 });                  // and dword [esp],0
            mc.Emit(new byte[] { 0x54, 0x50 });                              // push esp; push eax
            mc.Emit(new byte[] { 0x8B, 0x08 });                              // mov ecx,[eax]
            mc.Emit(new byte[] { 0xFF, 0x51, 0x30 });                        // call [ecx+0x30]                GetDesc
            mc.Emit(new byte[] { 0x31, 0xD2 });                              // xor edx,edx
            mc.Emit(new byte[] { 0x83, 0x3C, 0x24, (byte)D3DFMT_D24S8 });    // cmp dword [esp],D3DFMT_D24S8
            mc.Emit(new byte[] { 0x0F, 0x94, 0xC2 });                        // sete dl
            mc.Emit(new byte[] { 0x83, 0xC4, 0x20 });                        // add esp,0x20
            mc.MarkLabel("srt_set");
            mc.Emit(new byte[] { 0x3B, 0x15 }); mc.EmitUInt32(D(ReversedOff)); // cmp edx,[Reversed]
            mc.EmitJzShort("srt_done");
            mc.Emit(new byte[] { 0x89, 0x15 }); mc.EmitUInt32(D(ReversedOff)); // mov [Reversed],edx
            mc.EmitCall("reapply");
            mc.MarkLabel("srt_done");
            mc.Emit(new byte[] { 0x5E, 0xC2, 0x08, 0x00 });                  // pop esi; ret 8

            // SetViewParameters(View, ViewProjectionMatrix, ViewOrigin) remembers the matrix, then resends it through upload while reversed.
            int setViewParameters = mc.CurrentOffset;
            mc.Emit(new byte[] { 0x56, 0x8B, 0xF1 });                        // push esi; mov esi,ecx
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x0C });                  // mov eax,[esp+0xC]              ViewProjectionMatrix
            for (int row = 0; row < 4; row++)
            {
                mc.Emit(row == 0 ? new byte[] { 0x0F, 0x10, 0x00 } : new byte[] { 0x0F, 0x10, 0x40, (byte)(row * 16) }); // movups xmm0,[eax+row]
                mc.Emit(new byte[] { 0x0F, 0x11, 0x05 }); mc.EmitUInt32(D(MatrixOff + row * 16));                        // movups [Matrix+row],xmm0
            }
            mc.Emit(new byte[] { 0xC7, 0x05 }); mc.EmitUInt32(D(HaveMatrixOff)); mc.EmitUInt32(1); // mov [HaveMatrix],1
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x10 });                  // push [esp+0x10]                ViewOrigin
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x10 });                  // push [esp+0x10]                ViewProjectionMatrix
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x10 });                  // push [esp+0x10]                View
            mc.Emit(new byte[] { 0x8B, 0xCE });                              // mov ecx,esi
            mc.EmitCall(s.SetViewParameters);
            CmpReversed();
            mc.EmitJzShort("svp_done");
            mc.EmitCall("upload");
            mc.MarkLabel("svp_done");
            mc.Emit(new byte[] { 0x5E, 0xC2, 0x0C, 0x00 });                  // pop esi; ret 0xC

            // SetDepthState(NewState) remembers the depth test, then resends it flipped while reversed.
            int setDepthState = mc.CurrentOffset;
            mc.Emit(new byte[] { 0x56, 0x8B, 0xF1 });                        // push esi; mov esi,ecx
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x08 });                  // mov eax,[esp+8]                NewState
            mc.Emit(new byte[] { 0x8B, 0x40, 0x08 });                        // mov eax,[eax+8]                ZFunc
            mc.Emit(0xA3); mc.EmitUInt32(D(ZFuncOff));                       // mov [ZFunc],eax
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x08 });                  // push [esp+8]
            mc.Emit(new byte[] { 0x8B, 0xCE });                              // mov ecx,esi
            mc.EmitCall(s.SetDepthState);
            CmpReversed();
            mc.EmitJzShort("sds_done");
            mc.Emit(0xA1); mc.EmitUInt32(D(ZFuncOff));                       // mov eax,[ZFunc]
            mc.EmitCall("flip");
            mc.Emit(new byte[] { 0x6A, D3DRS_ZFUNC, 0x5A });                 // push D3DRS_ZFUNC; pop edx
            mc.EmitCall("srs");
            mc.MarkLabel("sds_done");
            mc.Emit(new byte[] { 0x5E, 0xC2, 0x04, 0x00 });                  // pop esi; ret 4

            // SetRasterizerState(NewState) and SetRasterizerStateImmediate(Initializer) remember the depth bias,
            // then resend it negated while reversed.
            // Both arguments hold DepthBias at +8 and SlopeScaleDepthBias at +0xC.
            int setRasterizerState = mc.CurrentOffset;
            mc.Emit(0xB8); mc.EmitUInt32(s.SetRasterizerState);              // mov eax,SetRasterizerState
            mc.EmitJmpShort("rast");
            int setRasterizerStateImmediate = mc.CurrentOffset;
            mc.Emit(0xB8); mc.EmitUInt32(s.SetRasterizerStateImmediate);     // mov eax,SetRasterizerStateImmediate
            mc.MarkLabel("rast");
            mc.Emit(new byte[] { 0x56, 0x8B, 0xF1 });                        // push esi; mov esi,ecx
            mc.Emit(new byte[] { 0xFF, 0x74, 0x24, 0x08 });                  // push [esp+8]
            mc.Emit(new byte[] { 0x8B, 0xCE, 0xFF, 0xD0 });                  // mov ecx,esi; call eax
            mc.Emit(new byte[] { 0x8B, 0x4C, 0x24, 0x08 });                  // mov ecx,[esp+8]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x41, 0x08 });            // movss xmm0,[ecx+8]             DepthBias
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x58, 0x05 }); mc.EmitUInt32(s.DepthBiasOffset); // addss xmm0,[GDepthBiasOffset]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); mc.EmitUInt32(D(BiasOff));        // movss [Bias],xmm0
            mc.Emit(new byte[] { 0x8B, 0x49, 0x0C });                        // mov ecx,[ecx+0xC]              SlopeScaleDepthBias
            mc.Emit(new byte[] { 0x89, 0x0D }); mc.EmitUInt32(D(SlopeOff));  // mov [Slope],ecx
            CmpReversed();
            mc.EmitJzShort("rast_done");
            mc.EmitCall("bias");
            mc.MarkLabel("rast_done");
            mc.Emit(new byte[] { 0x5E, 0xC2, 0x04, 0x00 });                  // pop esi; ret 4

            // Clear(bClearColor, Color, bClearDepth, Depth, bClearStencil, Stencil) clears depth to 1 - Depth while reversed.
            int clear = mc.CurrentOffset;
            CmpReversed();
            mc.EmitJzShort("clr_go");
            mc.Emit(new byte[] { 0x83, 0x7C, 0x24, 0x0C, 0x00 });            // cmp [esp+0xC],0                bClearDepth
            mc.EmitJzShort("clr_go");
            mc.Emit(new byte[] { 0xD9, 0xE8 });                              // fld1
            mc.Emit(new byte[] { 0xD8, 0x64, 0x24, 0x10 });                  // fsub dword [esp+0x10]
            mc.Emit(new byte[] { 0xD9, 0x5C, 0x24, 0x10 });                  // fstp dword [esp+0x10]
            mc.MarkLabel("clr_go");
            mc.EmitJmpNear(s.Clear);

            // SetDepthBoundsTest(bEnable, ClipSpaceNearPos, ClipSpaceFarPos) swaps the two points and replaces each z with w - z.
            // The original then computes 1 - max and 1 - min on both its NVDB and clip-plane paths.
            int setDepthBoundsTest = mc.CurrentOffset;
            CmpReversed();
            mc.EmitJzShort("sdb_go");
            mc.Emit(new byte[] { 0x83, 0x7C, 0x24, 0x04, 0x00 });            // cmp [esp+4],0                  bEnable
            mc.EmitJzShort("sdb_go");
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x0C });                  // mov eax,[esp+0xC]              ClipSpaceFarPos
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x40, 0x0C });            // movss xmm0,[eax+0xC]           w
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); mc.EmitUInt32(D(NearWOff)); // movss [NearW],xmm0
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5C, 0x40, 0x08 });            // subss xmm0,[eax+8]             w - z
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); mc.EmitUInt32(D(NearZOff)); // movss [NearZ],xmm0
            mc.Emit(new byte[] { 0x8B, 0x44, 0x24, 0x08 });                  // mov eax,[esp+8]                ClipSpaceNearPos
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x10, 0x40, 0x0C });            // movss xmm0,[eax+0xC]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); mc.EmitUInt32(D(FarWOff));  // movss [FarW],xmm0
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x5C, 0x40, 0x08 });            // subss xmm0,[eax+8]
            mc.Emit(new byte[] { 0xF3, 0x0F, 0x11, 0x05 }); mc.EmitUInt32(D(FarZOff));  // movss [FarZ],xmm0
            mc.Emit(new byte[] { 0xC7, 0x44, 0x24, 0x08 }); mc.EmitUInt32(D(NearVecOff)); // mov [esp+8],NearVec
            mc.Emit(new byte[] { 0xC7, 0x44, 0x24, 0x0C }); mc.EmitUInt32(D(FarVecOff));  // mov [esp+0xC],FarVec
            mc.MarkLabel("sdb_go");
            mc.EmitJmpNear(s.SetDepthBoundsTest);

            // SetVertexShaderParameter(Shader, BufferIndex, BaseIndex, NumBytes, Value) forgets the matrix
            // when a write starts below c4, so that reapply cannot overwrite the new constants.
            int setVertexShaderParameter = mc.CurrentOffset;
            mc.Emit(new byte[] { 0x83, 0x7C, 0x24, 0x0C, 0x40 });            // cmp [esp+0xC],0x40             BaseIndex, in bytes
            mc.EmitJaeShort("svsp_go");
            mc.Emit(new byte[] { 0x83, 0x25 }); mc.EmitUInt32(D(HaveMatrixOff)); mc.Emit(0x00); // and [HaveMatrix],0
            mc.MarkLabel("svsp_go");
            mc.EmitJmpNear(s.SetVertexShaderParameter);

            entries = new Entries(
                CodeOff + setRasterizerState,
                CodeOff + setRasterizerStateImmediate,
                CodeOff + setDepthBoundsTest,
                CodeOff + setVertexShaderParameter,
                CodeOff + setViewParameters,
                CodeOff + setDepthState,
                CodeOff + setRenderTarget,
                CodeOff + clear);

            byte[] code = mc.Build();
            byte[] result = new byte[CodeOff + code.Length];
            block.CopyTo(result, 0);
            code.CopyTo(result, CodeOff);
            return result;
        }

        private static uint ReadU32AtVa(PeImageLayout pe, byte[] data, uint va) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pe.VaToOffset(va), 4));
    }
}

using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.Buffers.Binary;
using static MirrorsEdgeTweaks.Tests.TestSupport.SyntheticPe;

namespace MirrorsEdgeTweaks.Tests
{
    public class ReversedDepthPatcherTests
    {
        // The flat images map file offsets to RVAs one to one.
        private const int StencilSite = 0x1100, DepthStateSite = 0x1130, RasterSite = 0x1170, ImmediateSite = 0x11C0,
            BoundsSite = 0x1200, VsParamSite = 0x1230, ViewParamsSite = 0x12F0, RenderTargetSite = 0x1320,
            ClearSite = 0x1350;
        private const int VtableSite = 0x2100;
        private const uint ForceNoDepthVa = ImageBase + FlatDataRva + 0x10;
        private const uint DepthBiasOffsetVa = ImageBase + FlatDataRva + 0x20;
        private const int DepthStateSlot = 0xF4, StencilSlot = 0xF8, RenderTargetSlot = 0x100, ClearSlot = 0x14C;
        private const int CaveHeaderSize = 12;
        // Retail 1.0.1.0 has the smallest .rdata tail of the SecuROM builds.
        private const int RetailRdataTail = 1504;

        // Slots and Sites are parallel arrays in the patcher's slot order.
        private static readonly int[] Slots = { 0xBC, 0xC0, 0xCC, 0xD8, 0xE4, DepthStateSlot, RenderTargetSlot, ClearSlot };
        private static readonly int[] Sites =
            { RasterSite, ImmediateSite, BoundsSite, VsParamSite, ViewParamsSite, DepthStateSite, RenderTargetSite, ClearSite };

        [Fact]
        public void FindSites_ReadsTheVtableAndTheDataAddresses()
        {
            byte[] image = BuildRhiImage(BuildFlat());

            var sites = ReversedDepthPatcher.FindSites(image, PeImageLayout.Parse(image));

            Assert.Equal(Va(VtableSite), sites.Vtable);
            Assert.Equal(Va(RenderTargetSite), sites.SetRenderTarget);
            Assert.Equal(Va(VsParamSite), sites.SetVertexShaderParameter);
            Assert.Equal(ForceNoDepthVa, sites.ForceNoDepthFlag);
            Assert.Equal(DepthBiasOffsetVa, sites.DepthBiasOffset);
        }

        [Fact]
        public void ApplyToImage_RedirectsEverySlot_IntoOneMatchingBlock()
        {
            byte[] stock = BuildRhiImage(BuildFlat());
            byte[] patched = ReversedDepthPatcher.ApplyToImage((byte[])stock.Clone(), "steam")!;

            var sites = ReversedDepthPatcher.FindSites(patched, PeImageLayout.Parse(patched));
            uint block = BlockVaOf(patched);
            Assert.Equal(ReversedDepthPatcher.BuildBlock(block, sites, out var entries),
                Block(patched, block, ReversedDepthPatcher.BlockLength));
            Assert.Equal(block + (uint)entries.Clear, SlotValue(patched, ClearSlot));
            Assert.Equal(block + (uint)entries.SetDepthState, SlotValue(patched, DepthStateSlot));

            var cave = PeImageLayout.Parse(patched).FindSectionByName(".cave");
            Assert.NotNull(cave);
            Assert.InRange(block, ImageBase + cave.VirtualAddress, ImageBase + cave.VirtualAddress + cave.VirtualSize - 1);

            // The stencil slot anchors the vtable, so it never moves.
            Assert.Equal(Va(StencilSite), SlotValue(patched, StencilSlot));
            Assert.Equal(ReversedDepthState.Patched, ReversedDepthPatcher.DetectState(patched));
            Assert.Equal(ReversedDepthState.Unpatched, ReversedDepthPatcher.DetectState(stock));
        }

        [Fact]
        public void ApplyToImage_ReturnsNull_OnceApplied()
        {
            byte[] patched = ReversedDepthPatcher.ApplyToImage(BuildRhiImage(BuildFlat()), "steam")!;
            byte[] again = (byte[])patched.Clone();

            Assert.Null(ReversedDepthPatcher.ApplyToImage(again, "steam"));
            Assert.Equal(patched, again);
        }

        [Fact]
        public void RemoveFromImage_RestoresTheSlots_AndFreesTheTopmostBlock()
        {
            byte[] stock = BuildRhiImage(BuildFlat());
            byte[] patched = ReversedDepthPatcher.ApplyToImage((byte[])stock.Clone(), "steam")!;

            byte[] removed = ReversedDepthPatcher.RemoveFromImage((byte[])patched.Clone())!;

            for (int i = 0; i < Slots.Length; i++)
                Assert.Equal(Va(Sites[i]), SlotValue(removed, Slots[i]));
            Assert.Equal(ReversedDepthState.Unpatched, ReversedDepthPatcher.DetectState(removed));
            Assert.Equal(CaveHeaderSize, CaveSection.Open(removed).CurrentWatermark);
            Assert.Null(ReversedDepthPatcher.RemoveFromImage((byte[])removed.Clone()));
        }

        [Fact]
        public void ApplyToImage_ReusesTheBlockThatRemoveLeftBehind()
        {
            byte[] patched = ReversedDepthPatcher.ApplyToImage(BuildRhiImage(BuildFlat()), "steam")!;
            uint block = BlockVaOf(patched);
            var cave = CaveSection.Open(patched);
            cave.Alloc(64);
            byte[] buried = cave.Finalize();
            int watermark = CaveSection.Open(buried).CurrentWatermark;

            byte[] removed = ReversedDepthPatcher.RemoveFromImage(buried)!;
            byte[] reapplied = ReversedDepthPatcher.ApplyToImage(removed, "steam")!;

            Assert.Equal(block, BlockVaOf(reapplied));
            Assert.Equal(watermark, CaveSection.Open(reapplied).CurrentWatermark);
        }

        [Fact]
        public void ApplyToImage_Throws_WhenASlotHoldsAForeignPointer()
        {
            byte[] image = BuildRhiImage(BuildFlat());
            WriteSlot(image, DepthStateSlot, Va(ClearSite));

            Assert.Throws<InvalidOperationException>(() => ReversedDepthPatcher.ApplyToImage(image, "steam"));
            Assert.Equal(ReversedDepthState.Unknown, ReversedDepthPatcher.DetectState(image));
        }

        [Fact]
        public void ApplyToImage_Throws_WhenAFunctionIsMissing()
        {
            byte[] image = BuildRhiImage(BuildFlat());
            image.AsSpan(ClearSite, 8).Clear();

            Assert.Throws<InvalidOperationException>(() => ReversedDepthPatcher.ApplyToImage(image, "steam"));
        }

        [Fact]
        public void ApplyToImage_UsesTheRdataTail_WhenTheImageCannotAddASection()
        {
            byte[] stock = BuildRhiImage(BuildFixedCave(256, RetailRdataTail));
            byte[] patched = ReversedDepthPatcher.ApplyToImage((byte[])stock.Clone(), "retail")!;

            var rdata = PeImageLayout.Parse(patched).FindSectionByName(".rdata")!;
            uint tailVa = ImageBase + rdata.VirtualAddress + rdata.VirtualSize;
            uint endVa = ImageBase + rdata.VirtualAddress + rdata.SizeOfRawData;
            Assert.InRange(BlockVaOf(patched), tailVa, endVa - (uint)ReversedDepthPatcher.BlockLength);
            Assert.Equal(0xE0000040u, rdata.Characteristics);
            Assert.Equal(ReversedDepthState.Patched, ReversedDepthPatcher.DetectState(patched));

            // The .text padding stays free for the core cave patches.
            Assert.True(patched.AsSpan(FixedCaveStart, 256).SequenceEqual(stock.AsSpan(FixedCaveStart, 256)));
        }

        [Fact]
        public void ApplyToImage_ExplainsAFullRdataTail()
        {
            var cave = CaveSection.OpenSecondary(BuildRhiImage(BuildFixedCave(256, RetailRdataTail)), "retail");
            cave.Alloc(cave.Capacity - cave.CurrentWatermark - ReversedDepthPatcher.BlockLength + 4);
            byte[] crowded = cave.Finalize();

            var ex = Assert.Throws<UserFacingException>(() => ReversedDepthPatcher.ApplyToImage(crowded, "retail"));
            Assert.Contains("not enough room", ex.Message);
        }

        [Fact]
        public void Block_HoldsTheInitialData_AndChainsToEveryOriginal()
        {
            const uint BlockVa = 0x02400000;
            const int ReversedOff = 0x08, ZFuncOff = 0x0C, FlipTableOff = 0x2C, OneZOff = 0x38, OneWOff = 0x3C;
            const uint D3DCMP_LESSEQUAL = 4;
            byte[] image = BuildRhiImage(BuildFlat());
            var sites = ReversedDepthPatcher.FindSites(image, PeImageLayout.Parse(image));
            byte[] block = ReversedDepthPatcher.BuildBlock(BlockVa, sites, out var e);

            Assert.Equal("METRZ001"u8.ToArray(), block[..8]);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(ReversedOff)));
            Assert.Equal(D3DCMP_LESSEQUAL, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(ZFuncOff)));
            Assert.Equal(new byte[] { 0, 1, 5, 3, 7, 2, 6, 4, 8 }, block[FlipTableOff..(FlipTableOff + 9)]);

            // SetDepthBoundsTest passes this point as the far point when the near point's z is not positive.
            Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(OneZOff)));
            Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(OneWOff)));

            // Each wrapper calls or jumps to its original.
            Assert.True(ContainsRel32(block, BlockVa, 0xE8, sites.SetRenderTarget, e.SetRenderTarget));
            Assert.True(ContainsRel32(block, BlockVa, 0xE8, sites.SetViewParameters, e.SetViewParameters));
            Assert.True(ContainsRel32(block, BlockVa, 0xE8, sites.SetDepthState, e.SetDepthState));
            Assert.True(ContainsRel32(block, BlockVa, 0xE9, sites.Clear, e.Clear));
            Assert.True(ContainsRel32(block, BlockVa, 0xE9, sites.SetDepthBoundsTest, e.SetDepthBoundsTest));
            Assert.True(ContainsRel32(block, BlockVa, 0xE9, sites.SetVertexShaderParameter, e.SetVertexShaderParameter));
            Assert.Equal(0xB8, block[e.SetRasterizerState]);
            Assert.Equal(sites.SetRasterizerState, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(e.SetRasterizerState + 1)));
            Assert.Equal(sites.SetRasterizerStateImmediate,
                BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(e.SetRasterizerStateImmediate + 1)));
        }

        // Real executables live in the git-ignored reference folder, so these tests skip without it.
        // The SecuROM builds have no header room for a .cave section, so the block goes into the .rdata tail.
        [Theory]
        [InlineData("MirrorsEdgeGOG", "gog", 0x01DBEC20u, 0x01FFCBD0u, 0x01FFD9F4u, ".cave")]
        [InlineData("MirrorsEdgeSteam", "steam", 0x01DBEC20u, 0x01FFCBD0u, 0x01FFD9F4u, ".cave")]
        [InlineData("MirrorsEdgeRetail", "retail", 0x01DC0CE0u, 0x02015C90u, 0x02016AB4u, ".rdata")]
        [InlineData("MirrorsEdgeRetailOld", "retold", 0x01DC0CD0u, 0x02015C90u, 0x02016AB4u, ".rdata")]
        [InlineData("MirrorsEdgeDLC", "dlc", 0x01DC7370u, 0x0201CD58u, 0x0201DB80u, ".rdata")]
        public void KnownExecutables_PatchAndUnpatch(
            string name, string tag, uint vtable, uint forceNoDepth, uint depthBiasOffset, string blockSection)
        {
            string? path = FindKbExecutable(name);
            Assert.SkipWhen(path == null, $"{name}.exe is not in ref/Mirrors-Edge-Version-KB.");

            byte[] stock = File.ReadAllBytes(path!);
            var sites = ReversedDepthPatcher.FindSites(stock, PeImageLayout.Parse(stock));
            Assert.Equal(vtable, sites.Vtable);
            Assert.Equal(forceNoDepth, sites.ForceNoDepthFlag);
            Assert.Equal(depthBiasOffset, sites.DepthBiasOffset);
            Assert.Equal(ReversedDepthState.Unpatched, ReversedDepthPatcher.DetectState(stock));

            byte[] patched = ReversedDepthPatcher.ApplyToImage((byte[])stock.Clone(), tag)!;
            Assert.Equal(ReversedDepthState.Patched, ReversedDepthPatcher.DetectState(patched));
            Assert.Equal(blockSection, SectionOf(patched, BlockVaOf(patched)));
            Assert.Null(ReversedDepthPatcher.ApplyToImage((byte[])patched.Clone(), tag));

            // The .rdata tail keeps the emptied cave's header, so only the section's own data is compared.
            byte[] removed = ReversedDepthPatcher.RemoveFromImage(patched)!;
            Assert.Equal(ReversedDepthState.Unpatched, ReversedDepthPatcher.DetectState(removed));
            var rdata = PeImageLayout.Parse(stock).FindSectionByName(".rdata")!;
            Assert.True(removed.AsSpan((int)rdata.PointerToRawData, (int)rdata.VirtualSize)
                .SequenceEqual(stock.AsSpan((int)rdata.PointerToRawData, (int)rdata.VirtualSize)));
        }

        // Logging's restorations leave room for reversed depth, so both fit on every SecuROM build in either order.
        [Theory]
        [InlineData("MirrorsEdgeRetail", "retail")]
        [InlineData("MirrorsEdgeRetailOld", "retold")]
        [InlineData("MirrorsEdgeDLC", "dlc")]
        public void KnownSecuRomExecutables_HoldReversedDepthAndFullLogging_InTheRdataTail(string name, string tag)
        {
            string? path = FindKbExecutable(name);
            Assert.SkipWhen(path == null, $"{name}.exe is not in ref/Mirrors-Edge-Version-KB.");

            byte[] stock = File.ReadAllBytes(path!);
            var table = VersionAddressTable.Load(tag);
            byte[] depthFirst = LoggingPatchHelper.ApplyToImage(
                ReversedDepthPatcher.ApplyToImage((byte[])stock.Clone(), tag)!, tag, table)!;
            byte[] loggingFirst = ReversedDepthPatcher.ApplyToImage(
                LoggingPatchHelper.ApplyToImage((byte[])stock.Clone(), tag, table)!, tag)!;

            var text = PeImageLayout.Parse(stock).FindSectionByName(".text")!;
            int paddingStart = (int)(text.PointerToRawData + text.VirtualSize);
            int paddingLength = (int)(text.PointerToRawData + text.SizeOfRawData) - paddingStart;
            foreach (byte[] image in new[] { depthFirst, loggingFirst })
            {
                Assert.Equal(ReversedDepthState.Patched, ReversedDepthPatcher.DetectState(image));
                Assert.True(LoggingPatchHelper.RestorePresent(image, table.Symbols));
                Assert.True(image.AsSpan(paddingStart, paddingLength).SequenceEqual(stock.AsSpan(paddingStart, paddingLength)));
            }
        }

        // The .rdata cave makes .rdata executable, which must not hide the UI vtables from the high-res UI fix.
        [Theory]
        [InlineData("MirrorsEdgeRetail", "retail")]
        [InlineData("MirrorsEdgeRetailOld", "retold")]
        [InlineData("MirrorsEdgeDLC", "dlc")]
        public void KnownSecuRomExecutables_KeepEveryHighResHook_BesideTheRdataCave(string name, string tag)
        {
            string? path = FindKbExecutable(name);
            Assert.SkipWhen(path == null, $"{name}.exe is not in ref/Mirrors-Edge-Version-KB.");

            byte[] stock = File.ReadAllBytes(path!);
            int alone = CaveSection.Open(MultiFontScalePatcher.ApplyToImage((byte[])stock.Clone(), tag)).CurrentWatermark;
            byte[] withDepth = ReversedDepthPatcher.ApplyToImage((byte[])stock.Clone(), tag)!;

            Assert.Equal(alone, CaveSection.Open(MultiFontScalePatcher.ApplyToImage(withDepth, tag)).CurrentWatermark);
        }

        // Lays out the nine RHI functions in .text and the FD3D9DynamicRHI vtable in .rdata.
        internal static byte[] BuildRhiImage(byte[] image)
        {
            Put(image, StencilSite, "8B 44 24 04 85 C0 56 57 8B F9 74 05 8D 70 F8 EB 02 33 F6 8B 56 08 8B 47 10 8B 08 52 6A 34 50 C3");
            Put(image, DepthStateSite, "8B 44 24 04 85 C0 56 57 8B F9 74 05 8D 70 F8 EB 02 33 F6 8B 56 08 8B 47 10 8B 08 52 6A 07 50 " +
                "8B 81 E4 00 00 00 FF D0 8B 56 0C 8B 47 10 8B 08 52 6A 0E 50 C3");
            Put(image, RasterSite, "8B 44 24 04 85 C0 56 57 8B F9 74 05 8D 70 F8 EB 02 33 F6 8B 56 08 8B 47 10 8B 08 52 6A 08 50 " +
                "8B 81 E4 00 00 00 FF D0 8B 56 0C 8B 47 10 8B 08 52 6A 16 50 8B 81 E4 00 00 00 FF D0 " +
                $"F3 0F 10 46 10 F3 0F 58 05 {Le(DepthBiasOffsetVa)} C3");
            Put(image, ImmediateSite, "56 57 8B 7C 24 0C 8B 07 83 E8 00 8B F1 8B 4E 10 74 13 83 E8 01 74 07 B8 03 00 00 00 EB 0C " +
                "B8 02 00 00 00 EB 05 B8 01 00 00 00 8B 11 50 8B 82 E4 00 00 00 6A 08 C3");
            Put(image, BoundsSite, "83 EC 20 83 7C 24 24 00 56 8B F1 0F 84 00 00 00 00 83 7E 3C 00 8B 44 24 2C 0F 84 00 00 00 00 " +
                "F3 0F 10 40 08 0F 57 C9 0F 2F C1 C3");
            Put(image, VsParamSite, "53 55 8B 69 10 56 57 8B 7C 24 20 8D 5F 0F 8B F3 83 E6 F0 3B F7 74 7F F6 05 00 00 00 00 01 75 25 " +
                "83 0D 00 00 00 00 01 33 C0 68 00 00 00 00 A3 00 00 00 00 A3 00 00 00 00 A3 00 00 00 00 E8 00 00 00 00 " +
                "83 C4 04 39 35 00 00 00 00 73 1A 56 B9 00 00 00 00 E8 00 00 00 00 6A 08 6A 01 56 B9 00 00 00 00 " +
                "E8 00 00 00 00 8B 44 24 24 8B 0D 00 00 00 00 57 50 51 E8 00 00 00 00 8B 15 00 00 00 00 2B F7 56 " +
                "03 D7 6A 00 52 E8 00 00 00 00 A1 00 00 00 00 83 C4 18 EB 04 8B 44 24 24 8B 54 24 1C 8B 4D 00 " +
                "C1 EB 04 53 50 8B 81 78 01 00 00 C3");
            Put(image, ViewParamsSite, "8B 54 24 08 56 57 6A 04 8B F1 8B 46 10 8B 08 52 6A 00 50 8B 81 78 01 00 00 FF D0 " +
                "8B 54 24 14 8B 46 10 8B 08 6A 01 52 6A 04 50 C3");
            Put(image, RenderTargetSite, $"83 EC 18 80 3D {Le(ForceNoDepthVa)} 00 55 57 8B F9 74 04 33 C9 EB 04 8B 4C 24 28 " +
                "8B 44 24 24 85 C0 74 05 8D 68 F4 C3");
            Put(image, ClearSite, "53 56 33 F6 39 74 24 0C 57 8B F9 74 05 BE 01 00 00 00 83 7C 24 18 00 74 03 83 CE 02 " +
                "83 7C 24 20 00 74 03 83 CE 04 C3");

            for (int i = 0; i < Slots.Length; i++)
                WriteSlot(image, Slots[i], Va(Sites[i]));
            WriteSlot(image, StencilSlot, Va(StencilSite));
            return image;
        }

        private static string? FindKbExecutable(string name)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "ref", "Mirrors-Edge-Version-KB", name, name + ".exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static uint Va(int offset) => ImageBase + (uint)offset;

        private static void WriteSlot(byte[] image, int slot, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(VtableSite + slot), value);

        private static uint BlockVaOf(byte[] image) =>
            SlotValue(image, RenderTargetSlot) - (uint)ReversedDepthPatcher.EntryOffsets.SetRenderTarget;

        private static uint SlotValue(byte[] image, int slot)
        {
            var pe = PeImageLayout.Parse(image);
            var sites = ReversedDepthPatcher.FindSites(image, pe);
            return BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe.VaToOffset(sites.Vtable + (uint)slot)));
        }

        private static string SectionOf(byte[] image, uint va)
        {
            var pe = PeImageLayout.Parse(image);
            return pe.Sections.First(s => va - pe.ImageBase - s.VirtualAddress < Math.Max(s.VirtualSize, s.SizeOfRawData)).Name;
        }

        private static byte[] Block(byte[] image, uint va, int length)
        {
            var pe = PeImageLayout.Parse(image);
            return image.AsSpan(pe.VaToOffset(va), length).ToArray();
        }

        // Looks for a call or jmp to target in the wrapper that starts at entry.
        private static bool ContainsRel32(byte[] block, uint blockVa, byte opcode, uint target, int entry)
        {
            for (int i = entry; i + 5 <= block.Length && i < entry + 0x80; i++)
            {
                if (block[i] != opcode) continue;
                uint dest = blockVa + (uint)i + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(i + 1));
                if (dest == target) return true;
            }
            return false;
        }
    }
}

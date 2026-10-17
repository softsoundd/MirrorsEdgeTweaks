using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Buffers.Binary;

namespace MirrorsEdgeTweaks.Tests
{
    public class LoggingPatchHelperTests
    {
        private const uint ImageBase = 0x00400000;

        // The flat logging image holds the hook sites in .text, the Serialize slots in .rdata and the
        // execScriptTrace native entry in .data, each at its game-like role.
        private const uint ExecSite = 0x00401100, PreInitSite = 0x00401200, OpenSite = 0x00401300;
        private const uint StubVa = 0x00401400, TraceFnVa = 0x00401410;
        private const uint FrameSlot = 0x00402000, StateSlot = 0x00402010, NameVa = 0x00402100;
        private const uint EntryVa = 0x00403000;

        private static readonly byte[] Ret8 = { 0xC2, 0x08, 0x00 };

        private static bool Contains(byte[] haystack, params byte[] needle)
            => PatternHelper.FindPattern(haystack, needle) != -1;

        private static int Count(byte[] haystack, params byte[] needle)
        {
            int count = 0;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                    count++;
            return count;
        }

        [Fact]
        public void WarnForwarder_IsDeterministic_FormatsThroughGLog_AndReturns8()
        {
            var sym = LoggingTable(fullMode: true).Symbols;
            const uint CodeVa = 0x02400000, FmtVa = 0x02400800;
            byte[] a = LoggingPatchHelper.BuildWarnForwarder(CodeVa, sym, FmtVa);
            Assert.Equal(a, LoggingPatchHelper.BuildWarnForwarder(CodeVa, sym, FmtVa));

            // It reads Object (+8) and Node (+4) from the frame and names both with GetFullName.
            Assert.True(Contains(a, 0x8B, 0x4E, 0x08));
            Assert.True(Contains(a, 0x8B, 0x4E, 0x04));
            // The object name is pushed for Logf and again to be freed.
            Assert.Equal(2, Count(a, 0xFF, 0x75, 0xF4));
            Assert.True(Contains(a, 0x68, 0x00, 0x08, 0x40, 0x02)); // push FmtVa
            Assert.Equal(Ret8, a[^3..]);
        }

        [Fact]
        public void ScriptTrace_CallsTheStockNativeFirst_AndReturns8()
        {
            var sym = LoggingTable(fullMode: true).Symbols;
            const uint CodeVa = 0x02400000;
            byte[] a = LoggingPatchHelper.BuildScriptTrace(CodeVa, sym);
            Assert.Equal(a, LoggingPatchHelper.BuildScriptTrace(CodeVa, sym));

            // The frame and result are forwarded to the stock native, whose call is the first one.
            Assert.Equal(new byte[] { 0xFF, 0x75, 0x0C, 0xFF, 0x75, 0x08, 0xE8 }, a[6..13]);
            Assert.Equal(TraceFnVa, CodeVa + 13 + 4 + (uint)BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(13, 4)));
            Assert.Equal(Ret8, a[^3..]);
        }

        [Fact]
        public void LazyOpen_ReplaysTheStockCheck_AndExitsToTheSkipTarget()
        {
            var sym = LoggingTable(fullMode: true).Symbols;
            const uint CodeVa = 0x02400000, SkipVa = 0x01133BD6, ContinueVa = 0x01133A16;
            byte[] a = LoggingPatchHelper.BuildLazyOpen(CodeVa, sym, SkipVa, ContinueVa);
            Assert.Equal(a, LoggingPatchHelper.BuildLazyOpen(CodeVa, sym, SkipVa, ContinueVa));

            // It opens with the stock `cmp [edx+4], ebx` it replaced.
            Assert.Equal(new byte[] { 0x39, 0x5A, 0x04 }, a[..3]);
            // It checks the Dead flag and the filename before opening through GFileManager's vtable slot +8.
            Assert.True(Contains(a, 0x39, 0x9A, 0x0C, 0x08, 0x00, 0x00));
            Assert.True(Contains(a, 0x66, 0x39, 0x5A, 0x08));
            Assert.True(Contains(a, 0xFF, 0x50, 0x08));
            // The block ends with the jump to the stock skip target, the je it replaced.
            Assert.Equal(0xE9, a[^5]);
            Assert.Equal(SkipVa, CodeVa + (uint)a.Length + (uint)BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(a.Length - 4, 4)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ApplyToImage_DetoursEveryHook_AndRemoveRestoresStock(bool fullMode)
        {
            byte[] stock = BuildLoggingImage();
            var table = LoggingTable(fullMode);
            byte[] patched = LoggingPatchHelper.ApplyToImage((byte[])stock.Clone(), "steam", table)!;

            var pe = PeImageLayout.Parse(patched);
            var cave = pe.FindSectionByName(".cave")!;
            uint caveLo = pe.ImageBase + cave.VirtualAddress, caveHi = caveLo + cave.VirtualSize;
            bool InCave(uint va) => va >= caveLo && va < caveHi;

            Assert.True(InCave(DetourTarget(patched, ExecSite)));
            if (fullMode)
                Assert.True(InCave(DetourTarget(patched, PreInitSite)));
            Assert.True(InCave(ReadU32(patched, FrameSlot)));
            Assert.Equal(ReadU32(patched, FrameSlot), ReadU32(patched, StateSlot));
            Assert.True(InCave(ReadU32(patched, EntryVa + 4)));
            Assert.True(InCave(DetourTarget(patched, OpenSite)));

            byte[] removed = LoggingPatchHelper.RemoveFromImage((byte[])patched.Clone(), table, out bool changed);
            Assert.True(changed);
            Assert.Equal(Sections(stock), Sections(removed));
            Assert.Equal(12, CaveSection.Open(removed).CurrentWatermark);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Reapply_RewritesTheBlockRemoveCouldNotReclaim(bool fullMode)
        {
            var table = LoggingTable(fullMode);
            byte[] patched = LoggingPatchHelper.ApplyToImage(BuildLoggingImage(), "steam", table)!;

            // A later patch allocated above the logging block keeps Remove from reclaiming it.
            var cave = CaveSection.Open(patched);
            cave.Alloc(64);
            byte[] buried = cave.Finalize();

            byte[] removed = LoggingPatchHelper.RemoveFromImage((byte[])buried.Clone(), table, out _);

            Assert.Equal(buried, LoggingPatchHelper.ApplyToImage(removed, "steam", table));
        }

        [Fact]
        public void Reapply_TakesAFreshBlock_WhenTheLeftoverWasBuiltFromOtherSymbols()
        {
            byte[] patched = LoggingPatchHelper.ApplyToImage(BuildLoggingImage(), "steam", LoggingTable(true))!;
            var cave = CaveSection.Open(patched);
            cave.Alloc(64);
            byte[] removed = LoggingPatchHelper.RemoveFromImage(cave.Finalize(), LoggingTable(true), out _);
            int before = CaveSection.Open((byte[])removed.Clone()).CurrentWatermark;

            // GLog moved, so this apply writes different bytes and must not reuse the old block.
            byte[] reapplied = LoggingPatchHelper.ApplyToImage(removed, "steam", LoggingTable(true, glog: 0x00403140))!;

            Assert.True(CaveSection.Open(reapplied).CurrentWatermark > before);
        }

        [Fact]
        public void ApplyToImage_ReturnsNull_WhenExecLogIsAlreadyDetoured()
        {
            var table = LoggingTable(fullMode: true);
            byte[] patched = LoggingPatchHelper.ApplyToImage(BuildLoggingImage(), "steam", table)!;

            Assert.Null(LoggingPatchHelper.ApplyToImage(patched, "steam", table));
        }

        [Fact]
        public void ApplyToImage_OmitsRestorations_WhenAFixedCaveLacksHeadroom()
        {
            var table = LoggingTable(fullMode: true);
            byte[] patched = LoggingPatchHelper.ApplyToImage(BuildFixedCaveLoggingImage(1200), "steam", table)!;

            // Core logging applies, but the restorations are dropped so the other cave patches still fit.
            Assert.Equal(0xE9, ByteAtVa(patched, ExecSite));
            Assert.Equal(StubVa, ReadU32(patched, FrameSlot));
            Assert.Equal(StubVa, ReadU32(patched, StateSlot));
            Assert.Equal(TraceFnVa, ReadU32(patched, EntryVa + 4));
            Assert.NotEqual(0xE9, ByteAtVa(patched, OpenSite));
        }

        [Fact]
        public void ApplyToImage_IncludesRestorations_WhenAFixedCaveHasHeadroom()
        {
            var table = LoggingTable(fullMode: true);
            byte[] patched = LoggingPatchHelper.ApplyToImage(BuildFixedCaveLoggingImage(3000), "steam", table)!;

            var cave = CaveSection.Open(patched);
            uint caveLo = cave.SectionVa, caveHi = caveLo + (uint)cave.Capacity;
            bool InCave(uint va) => va >= caveLo && va < caveHi;

            Assert.Equal(0xE9, ByteAtVa(patched, ExecSite));
            Assert.True(InCave(ReadU32(patched, FrameSlot)));
            Assert.Equal(ReadU32(patched, FrameSlot), ReadU32(patched, StateSlot));
            Assert.True(InCave(ReadU32(patched, EntryVa + 4)));
            Assert.True(InCave(DetourTarget(patched, OpenSite)));
        }

        [Fact]
        public void Reapply_ReusesTheLeftover_OnAFixedTextPaddingCave()
        {
            var table = LoggingTable(fullMode: true);
            byte[] patched = LoggingPatchHelper.ApplyToImage(BuildFixedCaveLoggingImage(3000), "steam", table)!;

            // A later patch above the logging block keeps Remove from reclaiming it.
            var cave = CaveSection.Open(patched);
            cave.Alloc(64);
            byte[] buried = cave.Finalize();

            byte[] removed = LoggingPatchHelper.RemoveFromImage((byte[])buried.Clone(), table, out _);

            Assert.Equal(buried, LoggingPatchHelper.ApplyToImage(removed, "steam", table));
        }

        [Fact]
        public void RemoveFromImage_ReportsNoChange_OnAStockImage()
        {
            byte[] stock = BuildLoggingImage();
            byte[] removed = LoggingPatchHelper.RemoveFromImage((byte[])stock.Clone(), LoggingTable(fullMode: true), out bool changed);

            Assert.False(changed);
            Assert.Equal(stock, removed);
        }

        [Fact]
        public void FindLeftoverBlock_RejectsAnAppInitLeftoverWhoseInitBlockDiffers()
        {
            var sym = LoggingTable(fullMode: false).Symbols;
            var seed = CaveSection.Open(SyntheticPe.Build(), versionTag: "retail");
            uint blockVa = seed.Alloc(AppInitLayout(0, sym, withInit: true).Footprint);
            foreach (var (off, code) in AppInitLayout(blockVa, sym, withInit: true).Blocks)
                seed.Write(blockVa + (uint)off, code);
            byte[] image = seed.Finalize();
            int expected = (int)(blockVa - seed.SectionVa);

            // The leftover was written with an init block, so only the matching layout finds it.
            Assert.Equal(expected, LoggingPatchHelper.FindLeftoverBlock(CaveSection.Open(image),
                AppInitLayout(0, sym, withInit: true).Footprint, 4, va => AppInitLayout(va, sym, withInit: true).Blocks));
            Assert.Null(LoggingPatchHelper.FindLeftoverBlock(CaveSection.Open(image),
                AppInitLayout(0, sym, withInit: false).Footprint, 4, va => AppInitLayout(va, sym, withInit: false).Blocks));
        }

        [Fact]
        public void FindLeftoverBlock_AcceptsALaterCandidate_WhenAnEarlierAnchorCannotStartABlock()
        {
            byte[] anchor = LoggingPatchHelper.BuildCaveDataFull();
            const int StringsOffset = 0x20;
            byte[] marker = Enumerable.Repeat((byte)0xAB, StringsOffset).ToArray();
            int footprint = StringsOffset + anchor.Length;
            List<(int Offset, byte[] Code)> Build(uint _) => new() { (0, marker), (StringsOffset, anchor) };

            // A stray anchor sits first, too close to the cave header for a block to start before it.
            var seed = CaveSection.Open(SyntheticPe.Build(), versionTag: "gog");
            uint strayVa = seed.Alloc(anchor.Length);
            seed.Write(strayVa, anchor);
            uint blockVa = seed.Alloc(footprint);
            foreach (var (off, code) in Build(blockVa))
                seed.Write(blockVa + (uint)off, code);
            byte[] image = seed.Finalize();

            int expected = (int)(blockVa - seed.SectionVa);
            Assert.Equal(expected, LoggingPatchHelper.FindLeftoverBlock(CaveSection.Open(image), footprint, StringsOffset, Build));
        }

        [Fact]
        public void RestorationHooks_WriteThenRestore_RoundTripsToStock()
        {
            byte[] data = BuildLoggingImage();
            var sym = LoggingTable(fullMode: true).Symbols;
            var openDef = LoggingTable(fullMode: true).Hooks["fileSerialize_openCheck"];
            byte[] stock = (byte[])data.Clone();
            Assert.False(LoggingPatchHelper.RestorePresent(data, sym));

            const uint WarnVa = 0x02050000, TraceVa = 0x02050200, LazyVa = 0x02050400;
            LoggingPatchHelper.WriteRestorationHooks(data, sym, OpenSite, WarnVa, TraceVa, LazyVa);

            Assert.Equal(WarnVa, ReadU32(data, FrameSlot));
            Assert.Equal(WarnVa, ReadU32(data, StateSlot));
            Assert.Equal(TraceVa, ReadU32(data, EntryVa + 4));
            Assert.Equal(LazyVa, DetourTarget(data, OpenSite));
            Assert.True(LoggingPatchHelper.RestorePresent(data, sym));

            LoggingPatchHelper.RestoreRestorations(data, sym, openDef);
            Assert.Equal(stock, data);
            Assert.False(LoggingPatchHelper.RestorePresent(data, sym));
        }

        // Builds the AppInit core layout the way ApplyPatch does, with or without the app-init block.
        private static (List<(int Offset, byte[] Code)> Blocks, int Footprint) AppInitLayout(
            uint baseVa, Dictionary<string, uint> sym, bool withInit)
        {
            byte[] strings = LoggingPatchHelper.BuildCaveDataFull();
            byte[] dataBlock = new byte[4 + strings.Length];
            strings.CopyTo(dataBlock, 4);
            uint ptr = baseVa, str = baseVa + 4, def = str + 0x20;
            uint archive = sym["FOutputDeviceFile.FileArchive"];

            var blocks = new List<(int Offset, byte[] Code)> { (0, dataBlock) };
            int off = Align4(dataBlock.Length);
            if (withInit)
            {
                byte[] init = LoggingPatchHelper.BuildCaveInit(baseVa + (uint)off, sym, ExecSite + 5, ptr, def,
                    new byte[] { 0x68, 0x01, 0x02, 0x03, 0x04 }, archive, str, str + 0x0C, sym["FOutputDeviceFile.Filename"]);
                blocks.Add((off, init));
                off = Align4(off + init.Length);
            }
            byte[] unified = LoggingPatchHelper.BuildCaveUnified(baseVa + (uint)off, sym, ExecSite + 6, ptr, def, archive);
            blocks.Add((off, unified));
            return (blocks, Align4(off + unified.Length));
        }

        private static byte[] BuildLoggingImage() => PopulateLoggingSites(SyntheticPe.BuildFlat());

        // Same sites on a fixed .text-padding cave of caveBytes, as on the SecuROM game executables.
        private static byte[] BuildFixedCaveLoggingImage(int caveBytes)
            => PopulateLoggingSites(SyntheticPe.BuildFixedCave(caveBytes));

        private static byte[] PopulateLoggingSites(byte[] image)
        {
            PutVa(image, ExecSite, "8B 44 24 14 3B C7");
            PutVa(image, PreInitSite, "8B 35 54 BA A8 01");
            PutVa(image, OpenSite, "39 5A 04 0F 84 C0 01 00 00");
            PutVa(image, StubVa, "C2 08 00");
            PutVa(image, TraceFnVa, "C2 08 00");
            WriteU32(image, FrameSlot, StubVa);
            WriteU32(image, StateSlot, StubVa);
            WriteU32(image, EntryVa, NameVa);
            WriteU32(image, EntryVa + 4, TraceFnVa);
            return image;
        }

        // Every symbol the core logging and its restorations read, at distinct dummy VAs.
        private static VersionAddressTable LoggingTable(bool fullMode, uint glog = 0x00403100)
        {
            var symbols = new Dictionary<string, uint>
            {
                ["Logf"] = 0x00401500,
                ["GLog"] = glog,
                ["GFileManager"] = 0x00403104,
                ["GCmdLine"] = 0x00403108,
                ["Parse"] = 0x00401510,
                ["FOutputDeviceFile.FileArchive"] = 0x0040310C,
                ["FOutputDeviceFile.Filename"] = 0x00403110,
                ["FmtPercentS"] = 0x00402200,
                ["GMalloc"] = 0x00403114,
                ["FFrame.Serialize"] = StubVa,
                ["FFrame.SerializeSlot"] = FrameSlot,
                ["FStateFrame.SerializeSlot"] = StateSlot,
                ["FFrame.GetStackTrace"] = 0x00401520,
                ["UObject.GetFullName"] = 0x00401530,
                ["UObject.execScriptTrace"] = TraceFnVa,
                ["UObject.execScriptTraceEntry"] = EntryVa,
            };
            var hooks = new Dictionary<string, HookDefinition>
            {
                ["execLog"] = Hook("8B 44 24 14 3B C7", 6, ExecSite),
                ["fileSerialize_openCheck"] = Hook("39 5A 04 0F 84 C0 01 00 00", 9, OpenSite),
            };
            if (fullMode)
                hooks["preInit_fileLog"] = Hook("8B 35 54 BA A8 01", 6, PreInitSite);
            return new VersionAddressTable { Version = "steam", Symbols = symbols, Hooks = hooks };
        }

        private static HookDefinition Hook(string hex, int size, uint site) => new()
        {
            Pattern = Convert.FromHexString(hex.Replace(" ", "")),
            Size = size,
            ReturnOffset = size,
            ScanStart = site,
            ScanSize = 0x40,
        };

        private static byte[] Sections(byte[] image)
            => image.AsSpan((int)SyntheticPe.FlatTextRva, (int)(SyntheticPe.FlatDataRva + SyntheticPe.FlatSectionSize - SyntheticPe.FlatTextRva)).ToArray();

        private static uint DetourTarget(byte[] image, uint siteVa)
        {
            int off = (int)(siteVa - ImageBase);
            Assert.Equal(0xE9, image[off]);
            return siteVa + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(off + 1, 4));
        }

        private static int Align4(int v) => (v + 3) & ~3;

        private static void PutVa(byte[] image, uint va, string hex) => SyntheticPe.Put(image, (int)(va - ImageBase), hex);

        private static byte ByteAtVa(byte[] image, uint va) => image[(int)(va - ImageBase)];

        private static uint ReadU32(byte[] d, uint va)
            => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)(va - ImageBase), 4));

        private static void WriteU32(byte[] d, uint va, uint value)
            => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((int)(va - ImageBase), 4), value);
    }
}

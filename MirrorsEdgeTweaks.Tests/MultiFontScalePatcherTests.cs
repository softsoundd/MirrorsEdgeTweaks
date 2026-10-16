using MirrorsEdgeTweaks.Helpers;
using System.Buffers.Binary;
using System.Text;

namespace MirrorsEdgeTweaks.Tests
{
    public class MultiFontScalePatcherTests
    {
        // Distinct dummy VAs keep each operand identifiable in the emitted bytes.
        private const uint CodeVa = 0x03000000;
        private const uint ResYVa = 0x02000000;
        private const uint C5625 = 0x03000100, C1080 = 0x03000104, CEps = 0x0300010C, CInf = 0x03000110;
        private const uint UiListVt = 0x01C4BDB8, ScrollbarVt = 0x01C4B038, ReturnVa = 0x00C1F566;

        private static readonly byte[] Ret4 = { 0xC2, 0x04, 0x00 };

        // Layout of the synthetic image that the apply and remove tests patch.
        private const uint ImageBase = 0x00400000;
        private const uint TextRva = 0x1000, RdataRva = 0x2000;
        private const int TextRaw = 0x400, TextSize = 0x400, RdataRaw = 0x800, RdataSize = 0x200;
        private const int PageSite = TextRaw, GsfSite = TextRaw + 0x40, ExtentSite = TextRaw + 0x80;
        private const int DswSite = TextRaw + 0xC0 + 37;

        private static bool EndsWith(byte[] data, params byte[] tail)
        {
            if (data.Length < tail.Length) return false;
            return data.AsSpan(data.Length - tail.Length).SequenceEqual(tail);
        }

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
        public void PageIndexCave_IsDeterministic_AndReturns4()
        {
            byte[] a = MultiFontScalePatcher.BuildPageIndexCave(CodeVa, CEps, CInf);
            byte[] b = MultiFontScalePatcher.BuildPageIndexCave(CodeVa, CEps, CInf);
            Assert.Equal(a, b);

            // The entry reads the ResolutionTestTable count with mov eax,[ecx+0x154].
            Assert.True(a.AsSpan(0, 6).SequenceEqual(new byte[] { 0x8B, 0x81, 0x54, 0x01, 0x00, 0x00 }));
            // Both exits are thiscall `ret 4`.
            Assert.True(EndsWith(a, Ret4));
            Assert.Equal(2, Count(a, Ret4));
        }

        [Fact]
        public void GsfCave_CallsIndexViaVtable_AndReturns4()
        {
            byte[] cave = MultiFontScalePatcher.BuildGsfCave(CodeVa, ResYVa, C5625);
            Assert.Equal(cave, MultiFontScalePatcher.BuildGsfCave(CodeVa, ResYVa, C5625));

            // It loads GetResolutionTestTableIndex from vtable slot +0x12C and calls it.
            Assert.True(Contains(cave, 0x8B, 0x80, 0x2C, 0x01, 0x00, 0x00));
            Assert.True(Contains(cave, 0xFF, 0xD0));
            // The clamped and stock numerators share one division by RTT[idx].
            Assert.Equal(1, Count(cave, 0xD8, 0x34, 0x81)); // fdiv dword [ecx+eax*4]
            // The division and the out-of-range 1.0 fallback both exit with thiscall `ret 4`.
            Assert.True(EndsWith(cave, Ret4));
            Assert.Equal(2, Count(cave, Ret4));
        }

        [Fact]
        public void ExtentCave_GatesOnBothVtables_AndFallsThrough()
        {
            byte[] cave = MultiFontScalePatcher.BuildExtentCave(
                CodeVa, ResYVa, C5625, C1080, UiListVt, ScrollbarVt, ReturnVa);
            Assert.Equal(cave, MultiFontScalePatcher.BuildExtentCave(
                CodeVa, ResYVa, C5625, C1080, UiListVt, ScrollbarVt, ReturnVa));

            // Both owner vtables are compared with cmp edx,imm32.
            Assert.True(Contains(cave, 0x81, 0xFA, 0xB8, 0xBD, 0xC4, 0x01)); // UiListVt
            Assert.True(Contains(cave, 0x81, 0xFA, 0x38, 0xB0, 0xC4, 0x01)); // ScrollbarVt
            // The scaled path multiplies the stored value and returns with `ret 8`.
            Assert.True(Contains(cave, 0xD8, 0x09));       // fmul dword [ecx]
            Assert.True(Contains(cave, 0xC2, 0x08, 0x00)); // ret 8
            // The pass-through path re-runs the displaced entry bytes and jumps back to entry+6.
            Assert.True(Contains(cave, 0x51, 0x53, 0x8B, 0x5C, 0x24, 0x0C));
            Assert.Equal(0xE9, cave[^5]);
        }

        [Fact]
        public void ApplyToImage_DetoursEverySite_AndReapplyIsStable()
        {
            byte[] patched = MultiFontScalePatcher.ApplyToImage(BuildPatchableImage(), "steam");

            foreach (int site in new[] { PageSite, GsfSite, ExtentSite, DswSite })
                AssertDetouredIntoCave(patched, site);
            Assert.Equal(patched, MultiFontScalePatcher.ApplyToImage((byte[])patched.Clone(), "steam"));
        }

        [Fact]
        public void RemoveFromImage_RestoresText_AndReclaimsCave()
        {
            byte[] stock = BuildPatchableImage();
            byte[] patched = MultiFontScalePatcher.ApplyToImage((byte[])stock.Clone(), "steam");

            byte[] removed = MultiFontScalePatcher.RemoveFromImage((byte[])patched.Clone(), out bool changed);
            Assert.True(changed);
            Assert.Equal(Text(stock), Text(removed));

            MultiFontScalePatcher.RemoveFromImage((byte[])removed.Clone(), out bool changedAgain);
            Assert.False(changedAgain);

            // A new apply only lands on the first apply's layout when Remove reclaimed the whole cave.
            Assert.Equal(patched, MultiFontScalePatcher.ApplyToImage(removed, "steam"));
        }

        [Fact]
        public void ExtentHook_IsSkippedWithoutVtables_AndRoundTripStillReclaims()
        {
            byte[] stock = BuildPatchableImage(withVtableCtors: false);
            byte[] patched = MultiFontScalePatcher.ApplyToImage((byte[])stock.Clone(), "steam");

            Assert.Equal(stock[ExtentSite], patched[ExtentSite]);
            foreach (int site in new[] { PageSite, GsfSite, DswSite })
                AssertDetouredIntoCave(patched, site);

            byte[] removed = MultiFontScalePatcher.RemoveFromImage((byte[])patched.Clone(), out _);
            Assert.Equal(Text(stock), Text(removed));
            Assert.Equal(patched, MultiFontScalePatcher.ApplyToImage(removed, "steam"));
        }

        [Fact]
        public void DetectState_ReportsPatchedOnlyAfterApply()
        {
            byte[] stock = BuildPatchableImage();
            byte[] patched = MultiFontScalePatcher.ApplyToImage((byte[])stock.Clone(), "steam");

            string tempExe = Path.Combine(Path.GetTempPath(), "metweaks_test_" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(tempExe, stock);
                Assert.Equal(MultiFontScaleState.Unpatched, MultiFontScalePatcher.DetectState(tempExe));

                File.WriteAllBytes(tempExe, patched);
                Assert.Equal(MultiFontScaleState.Patched, MultiFontScalePatcher.DetectState(tempExe));
            }
            finally
            {
                if (File.Exists(tempExe))
                    File.Delete(tempExe);
            }
        }

        private static byte[] Text(byte[] image) => image.AsSpan(TextRaw, TextSize).ToArray();

        private static void AssertDetouredIntoCave(byte[] image, int site)
        {
            var pe = PeImageLayout.Parse(image);
            var cave = pe.FindSectionByName(".cave");
            Assert.NotNull(cave);

            uint caveVa = pe.ImageBase + cave.VirtualAddress;
            uint target = pe.OffsetToVa(site) + 5 + (uint)BitConverter.ToInt32(image, site + 1);
            Assert.Equal(0xE9, image[site]);
            Assert.InRange(target, caveVa, caveVa + cave.VirtualSize - 1);
        }

        // Builds a PE32 image that holds one stock copy of every site the patcher locates.
        // .text holds the hook sites, the vtable constructor writes and the Parse pushes.
        // .rdata holds the vtables, the "ResX=" and "ResY=" strings and GSystemSettings.ResX and ResY.
        private static byte[] BuildPatchableImage(bool withVtableCtors = true)
        {
            byte[] image = new byte[RdataRaw + RdataSize];

            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            const int peOffset = 0x80;
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C, 4), peOffset);
            image[peOffset] = (byte)'P';
            image[peOffset + 1] = (byte)'E';

            int coff = peOffset + 4;
            const ushort optHeaderSize = 0xE0;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16, 2), optHeaderSize);

            int opt = coff + 20;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt, 2), 0x10B);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 28, 4), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32, 4), 0x1000); // SectionAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36, 4), 0x200);  // FileAlignment
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60, 4), 0x400);  // SizeOfHeaders, with room for .cave

            int sectionTable = opt + optHeaderSize;
            WriteSection(image, sectionTable, ".text", TextSize, TextRva, TextSize, TextRaw, 0x60000020);
            WriteSection(image, sectionTable + 40, ".rdata", RdataSize, RdataRva, RdataSize, RdataRaw, 0x40000040);

            uint uiListVt = ImageBase + RdataRva, scrollbarVt = uiListVt + 0x10;
            uint resXString = ImageBase + RdataRva + 0x20, resYString = ImageBase + RdataRva + 0x40;
            uint resX = ImageBase + RdataRva + 0x100;
            Encoding.Unicode.GetBytes("ResX=\0").CopyTo(image, RdataRaw + 0x20);
            Encoding.Unicode.GetBytes("ResY=\0").CopyTo(image, RdataRaw + 0x40);

            Put(image, PageSite, "83 EC 10 D9 44 24 14 53 DD 05 A0 5B A9 01 55 8B C1 8B 88 54 01 00 00 56 33 ED");
            Put(image, GsfSite, "D9 44 24 04 56 8B F1 8B 06 8B 90 2C 01 00 00 51 D9 1C 24 FF D2 3B 86 54 01 00 00 7D 11");
            Put(image, ExtentSite, "51 53 8B 5C 24 0C 85 DB 56 8B F1 F3 0F 10 06 57 F3 0F 11 44 24 0C 74 63");
            Put(image, DswSite - 37,
                "68 11 22 33 44 64 A1 00 00 00 00 50 83 EC 40 53 55 56 57 A1 55 66 77 88 33 C4 50 8D 44 24 54 " +
                "64 A3 00 00 00 00 8B 7C 24 7C 33 F6 3B FE 0F 84 74 04 00 00 8B 44 24 64 8B 48 04 8B");
            if (withVtableCtors)
            {
                Put(image, TextRaw + 0x100, $"C7 86 DC 03 00 00 00 00 00 00 C6 44 24 20 01 C7 06 {Le(uiListVt)} C7 86 DC 03 00 00");
                Put(image, TextRaw + 0x140, $"8D 8E EC 03 00 00 C7 44 24 14 00 00 00 00 C7 06 {Le(scrollbarVt)} E8 00 00 00 00 8D 8E 0C 04 00 00");
            }
            Put(image, TextRaw + 0x180, $"68 {Le(resX)} 68 {Le(resXString)}");
            Put(image, TextRaw + 0x190, $"68 {Le(resX + 4)} 68 {Le(resYString)}");

            return image;
        }

        private static void Put(byte[] image, int offset, string hex) =>
            Convert.FromHexString(hex.Replace(" ", "")).CopyTo(image, offset);

        private static string Le(uint value) => Convert.ToHexString(BitConverter.GetBytes(value));

        private static void WriteSection(byte[] buffer, int offset, string name, int virtualSize,
            uint virtualAddress, int rawSize, int rawOffset, uint characteristics)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(buffer, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8, 4), (uint)virtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 12, 4), virtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 16, 4), (uint)rawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 20, 4), (uint)rawOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 36, 4), characteristics);
        }
    }
}

using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using static MirrorsEdgeTweaks.Tests.TestSupport.SyntheticPe;

namespace MirrorsEdgeTweaks.Tests
{
    public class RenderTargetPatcherTests
    {
        private const int DecoyJne = TextRaw + 0x20;
        private const int DecoyConstant = TextRaw + 0x40;
        private const int Site = TextRaw + 0x200;

        // The viewport images are flat, so these file offsets are also RVAs.
        private const int HazeSite = 0x1100, DrawQuad = 0x1180, DepthSite = 0x1200, RhiSite = 0x1280, FlipSite = 0x1300;
        private const int HazeCall = HazeSite + 13, DepthHook = DepthSite + 7;
        private const uint DynamicRhiVa = ImageBase + FlatDataRva + 0x10;

        private const int CaveHeaderSize = 12;
        // Retail has the least .text padding of the builds that cannot add a .cave section.
        private const int RetailTextPadding = 1536;

        [Fact]
        public void ApplyAllocateFix_FlipsOnlyTheAnchoredJne_AndIsIdempotent()
        {
            byte[] image = BuildImage();
            byte[] expected = (byte[])image.Clone();
            expected[Site] = 0xEB;

            Assert.True(RenderTargetPatcher.ApplyAllocateFix(image));
            Assert.Equal(expected, image);

            Assert.False(RenderTargetPatcher.ApplyAllocateFix(image));
            Assert.Equal(expected, image);
        }

        [Fact]
        public void ApplyAllocateFix_Throws_WhenTheSiteIsMissing()
        {
            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.ApplyAllocateFix(SyntheticPe.Build()));
        }

        [Fact]
        public void HazeCave_SetsTheViewport_ThenJumpsToTheQuad()
        {
            const uint CodeVa = 0x03000000, RhiVa = 0x02000000, QuadVa = 0x010C5B30;
            byte[] cave = RenderTargetPatcher.BuildHazeCave(CodeVa, RhiVa, QuadVa);

            Assert.True(Contains(cave, 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x02));             // mov ecx,[GDynamicRHI]
            Assert.True(Contains(cave, 0xFF, 0x90, 0xC4, 0x00, 0x00, 0x00));             // call [eax+0xC4]

            // The quad's target becomes the view, and its origin moves to the viewport's corner.
            Assert.True(Contains(cave, 0x89, 0x4C, 0x24, 0x24));                         // mov [esp+0x24],ecx
            Assert.True(Contains(cave, 0x89, 0x54, 0x24, 0x28));                         // mov [esp+0x28],edx
            Assert.True(Contains(cave, 0x89, 0x44, 0x24, 0x04, 0x89, 0x44, 0x24, 0x08)); // X = Y = 0

            Assert.Equal(QuadVa, TailTarget(cave, CodeVa));
        }

        [Fact]
        public void DepthCave_SetsTheViewport_ThenReplaysTheLeaAndResumes()
        {
            const uint CodeVa = 0x03000000, RhiVa = 0x02000000, ResumeVa = 0x00CE71DD;
            byte[] cave = RenderTargetPatcher.BuildDepthCave(CodeVa, RhiVa, ResumeVa);

            Assert.True(Contains(cave, 0xFF, 0x76, 0x38, 0xFF, 0x76, 0x34));             // push RenderTargetY and RenderTargetX
            Assert.True(Contains(cave, 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x02));             // mov ecx,[GDynamicRHI]
            Assert.True(Contains(cave, 0xFF, 0x90, 0xC4, 0x00, 0x00, 0x00));             // call [eax+0xC4]
            Assert.True(cave.AsSpan(cave.Length - 11, 6)
                .SequenceEqual(new byte[] { 0x8D, 0x8E, 0x94, 0x02, 0x00, 0x00 }));      // lea ecx,[esi+0x294]
            Assert.Equal(ResumeVa, TailTarget(cave, CodeVa));
        }

        [Fact]
        public void ApplyViewportFixes_RedirectsBothSites_IntoTheirBlocks()
        {
            byte[] patched = RenderTargetPatcher.ApplyViewportFixes(BuildViewportImage(BuildFlat()), "steam")!;

            Assert.Equal(0xE8, patched[HazeCall]);
            uint hazeBlock = Target(patched, HazeCall);
            Assert.Equal(RenderTargetPatcher.BuildHazeCave(hazeBlock, DynamicRhiVa, Va(DrawQuad)),
                Block(patched, hazeBlock, RenderTargetPatcher.HazeCaveLength));

            Assert.Equal(0xE9, patched[DepthHook]);
            Assert.Equal(0x90, patched[DepthHook + 5]);
            uint depthBlock = Target(patched, DepthHook);
            Assert.Equal(RenderTargetPatcher.BuildDepthCave(depthBlock, DynamicRhiVa, Va(DepthHook + 6)),
                Block(patched, depthBlock, RenderTargetPatcher.DepthCaveLength));

            var cave = PeImageLayout.Parse(patched).FindSectionByName(".cave");
            Assert.NotNull(cave);
            uint caveVa = ImageBase + cave.VirtualAddress;
            Assert.InRange(hazeBlock, caveVa, caveVa + cave.VirtualSize - 1);
            Assert.InRange(depthBlock, caveVa, caveVa + cave.VirtualSize - 1);
        }

        [Fact]
        public void ApplyViewportFixes_ReturnsNull_OnceBothSitesReachTheirBlocks()
        {
            byte[] patched = RenderTargetPatcher.ApplyViewportFixes(BuildViewportImage(BuildFlat()), "steam")!;
            byte[] again = (byte[])patched.Clone();

            Assert.Null(RenderTargetPatcher.ApplyViewportFixes(again, "steam"));
            Assert.Equal(patched, again);
        }

        [Fact]
        public void ApplyViewportFixes_RepatchesOnlyTheSiteThatLostItsBlock()
        {
            byte[] patched = RenderTargetPatcher.ApplyViewportFixes(BuildViewportImage(BuildFlat()), "steam")!;
            uint hazeBlock = Target(patched, HazeCall);
            Put(patched, DepthHook, "8D 8E 94 02 00 00");

            byte[] repatched = RenderTargetPatcher.ApplyViewportFixes(patched, "steam")!;

            Assert.Equal(hazeBlock, Target(repatched, HazeCall));
            uint depthBlock = Target(repatched, DepthHook);
            Assert.Equal(RenderTargetPatcher.BuildDepthCave(depthBlock, DynamicRhiVa, Va(DepthHook + 6)),
                Block(repatched, depthBlock, RenderTargetPatcher.DepthCaveLength));
        }

        [Fact]
        public void ApplyViewportFixes_WorksInAFixedTextPaddingCave()
        {
            byte[] patched = RenderTargetPatcher.ApplyViewportFixes(BuildViewportImage(BuildFixedCave(256)), "retail")!;

            // The blocks land in .text, where the locators must still match only the game's own code.
            var text = PeImageLayout.Parse(patched).FindSectionByName(".text")!;
            uint textEndVa = ImageBase + text.VirtualAddress + text.SizeOfRawData - 1;
            Assert.InRange(Target(patched, HazeCall), Va(FixedCaveStart), textEndVa);
            Assert.InRange(Target(patched, DepthHook), Va(FixedCaveStart), textEndVa);
            Assert.Null(RenderTargetPatcher.ApplyViewportFixes((byte[])patched.Clone(), "retail"));
        }

        [Fact]
        public void ApplyViewportFixes_FitsInRetailPadding_AlongsideEveryOtherCorePatch()
        {
            // Logging and reversed depth use the .rdata tail, and the console set-command fix only applies to DLC,
            // so Retail's .text padding also holds the high-res UI fix and supersampling.
            int highResBytes = CaveSection.Open(MultiFontScalePatcher.ApplyToImage(
                MultiFontScalePatcherTests.BuildPatchableImage(), "retail")).CurrentWatermark - CaveHeaderSize;
            var cave = CaveSection.Open(BuildViewportImage(BuildFixedCave(RetailTextPadding)), "retail");
            cave.Alloc(highResBytes);
            cave.Alloc(SupersamplePatchHelper.CaveFootprint);
            byte[] crowded = cave.Finalize();

            Assert.NotNull(RenderTargetPatcher.ApplyViewportFixes(crowded, "retail"));
        }

        [Fact]
        public void ApplyViewportFixes_Throws_WhenASiteIsMissing()
        {
            byte[] image = BuildViewportImage(BuildFlat());
            image.AsSpan(RhiSite, 28).Clear();

            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.ApplyViewportFixes(image, "steam"));
        }

        [Fact]
        public void ApplyViewportFixes_Throws_WhenASiteHoldsForeignCode()
        {
            byte[] foreignJump = BuildViewportImage(BuildFlat());
            Put(foreignJump, DepthHook, "E9 00 00 00 00 90");
            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.ApplyViewportFixes(foreignJump, "steam"));

            byte[] foreignCall = BuildViewportImage(BuildFlat());
            Put(foreignCall, HazeCall + 1, Le(Va(RhiSite) - Va(HazeCall + 5)));
            Assert.Throws<InvalidOperationException>(() => RenderTargetPatcher.ApplyViewportFixes(foreignCall, "steam"));
        }

        [Fact]
        public void PatchImage_AppliesEveryFix_ThenReportsNothingToDo()
        {
            byte[] patched = RenderTargetPatcher.PatchImage(BuildViewportImage(BuildFlat()), "steam", out Exception? error)!;

            Assert.Null(error);
            Assert.Equal(0xEB, patched[FlipSite]);
            Assert.Equal(0xE8, patched[HazeCall]);
            Assert.Equal(0xE9, patched[DepthHook]);

            Assert.Null(RenderTargetPatcher.PatchImage((byte[])patched.Clone(), "steam", out error));
            Assert.Null(error);
        }

        [Fact]
        public void PatchImage_KeepsTheAllocateFix_WhenTheViewportFixesFail()
        {
            byte[]? patched = RenderTargetPatcher.PatchImage(BuildImage(), "steam", out Exception? error);

            Assert.NotNull(patched);
            Assert.Equal(0xEB, patched[Site]);
            Assert.IsType<InvalidOperationException>(error);

            Assert.Null(RenderTargetPatcher.PatchImage(patched, "steam", out error));
            Assert.IsType<InvalidOperationException>(error);
        }

        [Fact]
        public void RemoveFromImage_UndoesEveryFix_AndFreesTheirBlocks()
        {
            byte[] stock = BuildViewportImage(BuildFlat());
            byte[] patched = RenderTargetPatcher.PatchImage((byte[])stock.Clone(), "steam", out _)!;
            Assert.True(RenderTargetPatcher.HasAnyFix(patched));

            byte[] restored = RenderTargetPatcher.RemoveFromImage(patched)!;

            Assert.False(RenderTargetPatcher.HasAnyFix(restored));
            Assert.True(restored.AsSpan((int)FlatTextRva, FlatSectionSize)
                .SequenceEqual(stock.AsSpan((int)FlatTextRva, FlatSectionSize)));
            Assert.Equal(CaveHeaderSize, CaveSection.Open(restored).CurrentWatermark);
        }

        [Fact]
        public void RemoveFromImage_FreesBothBlocks_FromAFixedTextPaddingCave()
        {
            byte[] stock = BuildViewportImage(BuildFixedCave(256));
            byte[] patched = RenderTargetPatcher.PatchImage((byte[])stock.Clone(), "retail", out _)!;

            byte[] restored = RenderTargetPatcher.RemoveFromImage(patched)!;

            int codeLength = FixedCaveStart - (int)FlatTextRva;
            Assert.True(restored.AsSpan((int)FlatTextRva, codeLength).SequenceEqual(stock.AsSpan((int)FlatTextRva, codeLength)));
            Assert.Equal(CaveHeaderSize, CaveSection.Open(restored).CurrentWatermark);
        }

        [Fact]
        public void RemoveFromImage_UndoesTheAllocateFix_WhenTheImageHasNoViewportSites()
        {
            byte[] image = BuildImage();
            byte[] stock = (byte[])image.Clone();
            RenderTargetPatcher.ApplyAllocateFix(image);

            Assert.Equal(stock, RenderTargetPatcher.RemoveFromImage(image));
        }

        [Fact]
        public void RemoveFromImage_ReturnsNull_ForAStockImage()
        {
            byte[] stock = BuildViewportImage(BuildFlat());

            Assert.False(RenderTargetPatcher.HasAnyFix(stock));
            Assert.Null(RenderTargetPatcher.RemoveFromImage((byte[])stock.Clone()));
        }

        [Fact]
        public void HasAnyFix_ReturnsNull_WhenTheImageHasNoRenderTargetSite()
        {
            Assert.Null(RenderTargetPatcher.HasAnyFix(SyntheticPe.Build()));
            Assert.Null(RenderTargetPatcher.RemoveFromImage(SyntheticPe.Build()));
        }

        [Fact]
        public void PatchImage_SkipsTheViewportFixes_ForAnUnknownVersion()
        {
            byte[] stock = BuildViewportImage(BuildFlat());

            byte[]? patched = RenderTargetPatcher.PatchImage((byte[])stock.Clone(), null, out Exception? error);

            Assert.NotNull(patched);
            Assert.Equal(0xEB, patched[FlipSite]);
            Assert.True(patched.AsSpan(HazeSite, 0x200).SequenceEqual(stock.AsSpan(HazeSite, 0x200)));
            Assert.NotNull(error);
        }

        // Before the site, .text holds a 0.5625 constant whose reference follows a stray jne byte.
        // The high-res UI fix's cave has the same shape in Retail and DLC .text padding.
        private static byte[] BuildImage()
        {
            byte[] image = SyntheticPe.Build();
            uint decoyConstantVa = ImageBase + TextRva + (DecoyConstant - TextRaw);
            Put(image, DecoyJne, $"75 02 D8 0D {Le(decoyConstantVa)}");
            BitConverter.GetBytes(0.5625f).CopyTo(image, DecoyConstant);
            Put(image, Site, "75 3E 8B C7 85 C0 89 44 24 14 DB 44 24 14 7D 06 D8 05 20 93 A9 01 D8 0D D0 4E C5 01");
            return image;
        }

        internal static byte[] BuildViewportImage(byte[] image)
        {
            uint quadRel = Va(DrawQuad) - Va(HazeCall + 5);
            Put(image, HazeSite, $"D9 5C 24 08 D9 C9 D9 5C 24 04 D9 1C 24 E8 {Le(quadRel)} 83 C8 FF 83 C4 30");
            Put(image, DrawQuad, "55 8B EC 83 E4 F0 81 EC C0 00 00 00");
            Put(image, DepthSite, "8B 75 44 03 74 24 1C 8D 8E 94 02 00 00 51 8B 4D 00 56 81 C1 80 06 00 00 E8 00 00 00 00");
            Put(image, RhiSite, $"8B 35 80 CC FF 01 8B 00 8B 0D {Le(DynamicRhiVa)} 8B 11 56 50 8B 82 00 01 00 00 FF D0 D9 E8");
            Put(image, FlipSite, "75 3E 8B C7 85 C0 89 44 24 14 DB 44 24 14 7D 06 D8 05 20 93 A9 01 D8 0D D0 4E C5 01");
            return image;
        }

        private static uint Va(int offset) => ImageBase + (uint)offset;

        private static uint Target(byte[] image, int site) => Va(site) + 5 + (uint)BitConverter.ToInt32(image, site + 1);

        private static uint TailTarget(byte[] cave, uint codeVa)
        {
            Assert.Equal(0xE9, cave[^5]);
            return codeVa + (uint)cave.Length + (uint)BitConverter.ToInt32(cave, cave.Length - 4);
        }

        private static byte[] Block(byte[] image, uint va, int length) =>
            image.AsSpan((int)(va - ImageBase), length).ToArray();

        private static bool Contains(byte[] haystack, params byte[] needle) =>
            PatternHelper.FindPattern(haystack, needle) != -1;
    }
}

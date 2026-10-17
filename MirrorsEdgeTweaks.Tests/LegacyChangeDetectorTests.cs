using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.Text;
using static MirrorsEdgeTweaks.Tests.TestSupport.SyntheticPe;

namespace MirrorsEdgeTweaks.Tests
{
    public class LegacyChangeDetectorTests
    {
        // The beta sites follow the current font patch's sites in .text, one per slot.
        private const int BetaSites = TextRaw + 0x200;
        private const int BetaSiteStride = 0x30;

        private static readonly (string Prefix, int HookOffset)[] BetaHooks =
        {
            ("8B 06 8B 90 18 01 00 00 51 8B CE D9 1C 24 FF D2", 16),
            ("D9 44 24 40 89 44 24 38 8B 07 8B 90 18 01 00 00 51 8B CF D9 1C 24 FF D2", 24),
            ("D9 E8 D9 54 24 04 D9 1C 24 D9 44 24 30 50", 14),
            ("89 44 24 78 8B 01 8B 90 18 01 00 00 51 D9 1C 24 FF D2", 18),
            ("51 89 44 24 28 D9 1C 24 8B 82 18 01 00 00 8B CF FF D0", 18),
            ("D8 05 78 56 34 12 8B 07 8B 90 18 01 00 00 51 8B CF D9 1C 24 FF D2", 22),
            ("D9 E8 51 8D 4C 24 24 51 8D 54 24 24 52 83 EC 08 D9 54 24 04 D9 1C 24 50", 29),
        };

        [Fact]
        public void DetectExeImage_IgnoresUnhookedBetaSites()
        {
            Assert.Empty(LegacyChangeDetector.DetectExeImage(BuildBetaSiteImage(), null));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void DetectExeImage_ReportsEachBetaHook(int hook)
        {
            byte[] image = BuildBetaSiteImage();
            image[BetaSites + hook * BetaSiteStride + BetaHooks[hook].HookOffset] = 0xE9;

            var change = Assert.Single(LegacyChangeDetector.DetectExeImage(image, null));
            Assert.Equal(LegacyFile.Exe, change.File);
            Assert.Equal(LegacyIntent.HighResFix, change.Intent);
        }

        [Fact]
        public void DetectExeImage_IgnoresAnAmbiguousBetaPrefix()
        {
            byte[] image = BuildBetaSiteImage();
            Put(image, TextRaw + 0x380, BetaHooks[0].Prefix);
            image[BetaSites + BetaHooks[0].HookOffset] = 0xE9;

            Assert.Empty(LegacyChangeDetector.DetectExeImage(image, null));
        }

        [Fact]
        public void DetectExeImage_IgnoresABetaPrefixOutsideText()
        {
            byte[] image = Build();
            Put(image, RdataRaw, BetaHooks[0].Prefix + " E9");

            Assert.Empty(LegacyChangeDetector.DetectExeImage(image, null));
        }

        [Fact]
        public void DetectExeImage_IgnoresTheCurrentFontHooks()
        {
            byte[] image = MultiFontScalePatcher.ApplyToImage(BuildBetaSiteImage(), "steam");

            Assert.Empty(LegacyChangeDetector.DetectExeImage(image, "steam"));
        }

        [Theory]
        [InlineData("CmdLineArgs\0tdmainmenu?-windowed\0", true)]
        [InlineData("FlybyFlight\0escape_p?Loadcheckpoint=ChaseFlyby\0", false)]
        public void DetectExeImage_ReportsTheLaunchArgumentsHijack(string strings, bool hijacked)
        {
            byte[] image = Build();
            Encoding.Unicode.GetBytes(strings).CopyTo(image, RdataRaw);

            Assert.Equal(hijacked, LegacyChangeDetector.DetectExeImage(image, null).Count == 1);
        }

        [Theory]
        [InlineData("01 00 00 68 98 11 05 02 C7 05 A0 13 05 02 00 00", true)]
        [InlineData("01 00 00 68 D8 80 03 02 C7 05 E0 82 03 02 00 00", true)]
        [InlineData("01 00 00 68 98 11 05 02 C7 05 A0 13 05 02 01 00", false)]
        [InlineData("01 00 00 68 D8 80 03 02 C7 05 E0 82 03 02 01 00", false)]
        public void DetectExeImage_ReportsTheLegacyConfigUnlock(string code, bool unlocked)
        {
            byte[] image = Build();
            Put(image, TextRaw, code);

            Assert.Equal(unlocked, LegacyChangeDetector.DetectExeImage(image, null).Count == 1);
        }

        [Theory]
        [InlineData("set MultiFont ResolutionTestTable (480,720,")]
        [InlineData("set UIStyle_Text Scale (X=")]
        [InlineData("set TdUIScene bRefreshWidgetStyles true")]
        public void ContainsBetaHighResCommand_MatchesTheBetasCommands(string command)
        {
            Assert.True(LegacyChangeDetector.ContainsBetaHighResCommand(BytecodeBuilder.StrConst(command)));
        }

        [Fact]
        public void ContainsBetaHighResCommand_IgnoresTheCurrentBlobAndThePropertyName()
        {
            Assert.False(LegacyChangeDetector.ContainsBetaHighResCommand(BytecodeBuilder.HighResSignature));

            // Stock Engine.u has the bare property name in its name table.
            Assert.False(LegacyChangeDetector.ContainsBetaHighResCommand(Encoding.ASCII.GetBytes("\0bRefreshWidgetStyles\0")));
        }

        [Theory]
        [InlineData(1080f)]
        [InlineData(2160f)]
        public void ReadLastResolutionTest_ReadsTheFinalEntry(float last)
        {
            byte[] export = BuildFontExport(nameIndex: 563, 480f, 720f, last);

            Assert.Equal(last, LegacyChangeDetector.ReadLastResolutionTest(export, 0, export.Length, 563));
        }

        [Fact]
        public void ReadLastResolutionTest_ReturnsNullForAMissingOrTruncatedTable()
        {
            byte[] export = BuildFontExport(nameIndex: 563, 480f, 720f, 1080f);

            Assert.Null(LegacyChangeDetector.ReadLastResolutionTest(export, 0, export.Length, 564));
            Assert.Null(LegacyChangeDetector.ReadLastResolutionTest(export, 0, export.Length - 1, 563));
        }

        private static byte[] BuildBetaSiteImage()
        {
            byte[] image = MultiFontScalePatcherTests.BuildPatchableImage();
            for (int i = 0; i < BetaHooks.Length; i++)
                Put(image, BetaSites + i * BetaSiteStride, BetaHooks[i].Prefix);
            return image;
        }

        // Lays out the export's ResolutionTestTable tag after some unrelated data.
        // The tag holds the name, type name, size and array index, followed by the count and the floats.
        private static byte[] BuildFontExport(int nameIndex, params float[] table)
        {
            var export = new List<byte>(new byte[12]);
            export.AddRange(BitConverter.GetBytes((long)nameIndex));
            export.AddRange(BitConverter.GetBytes(7L));
            export.AddRange(BitConverter.GetBytes(sizeof(int) + table.Length * sizeof(float)));
            export.AddRange(BitConverter.GetBytes(0));
            export.AddRange(BitConverter.GetBytes(table.Length));
            foreach (float value in table)
                export.AddRange(BitConverter.GetBytes(value));
            return export.ToArray();
        }
    }
}

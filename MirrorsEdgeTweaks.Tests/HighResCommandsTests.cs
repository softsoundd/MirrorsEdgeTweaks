using MirrorsEdgeTweaks.Services;
using System.Text;

namespace MirrorsEdgeTweaks.Tests
{
    // The exe patch owns font and UI sizing, so the script blob emits only subtitle-region commands.
    public class HighResCommandsTests
    {
        private static byte[] BuildBlob() => BytecodeBuilder.BuildHighResApplyBlob(
            playerOwnerRef: 1, consoleCmdNameIdx: 2, consoleReturnValueRef: 3,
            hudSizeXRef: 4, hudSizeYRef: 5);

        private static string Ascii(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            foreach (byte b in data)
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '\n');
            return sb.ToString();
        }

        [Fact]
        public void BuildHighResApplyBlob_EmitsOnlySubtitleRegionCommands()
        {
            string text = Ascii(BuildBlob());

            Assert.Contains("set TdGameViewportClient SubtitleMinRegion (X=", text);
            Assert.Contains("set TdGameViewportClient SubtitleMaxRegion (X=", text);

            // Font-table and widget-style commands would fight the exe patch's sizing.
            Assert.DoesNotContain("ResolutionTestTable", text);
            Assert.DoesNotContain("UIStyle_Text", text);
            Assert.DoesNotContain("bRefreshWidgetStyles", text);
        }

        [Fact]
        public void HighResSignature_IsFoundInTheBlob()
        {
            // ApplyEngine and RemoveEngine detect an install by this signature.
            Assert.NotEqual(-1, BytecodeBuilder.FindPattern(BuildBlob(), BytecodeBuilder.HighResSignature));
        }
    }
}

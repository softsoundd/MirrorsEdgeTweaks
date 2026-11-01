using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class TweaksScriptsSettingsHelperTests
    {
        [Fact]
        public void GetPath_AppendsBinariesFileName()
        {
            Assert.Equal(
                Path.Combine(@"C:\Games\Mirror's Edge", "Binaries", "TweaksScriptsSettings"),
                TweaksScriptsSettingsHelper.GetPath(@"C:\Games\Mirror's Edge"));
        }

        [Fact]
        public void ReadValues_LastLineWinsAndSkipsComments()
        {
            Dictionary<string, string> values = TweaksScriptsSettingsHelper.ReadValues(new[]
            {
                "// TimerVisibility false",
                "TimerVisibility true",
                "  baghud   ON  ",
                "BagHUD off",
                "ScrollUpMacroKey",
            });

            Assert.Equal("true", values["TimerVisibility"]);
            Assert.Equal("off", values["BagHUD"]);
            Assert.Equal(string.Empty, values["ScrollUpMacroKey"]);
            Assert.False(values.ContainsKey("Speedometer"));
        }

        [Fact]
        public void SetValue_RewritesEveryMatchingLineAndAppendsWhenMissing()
        {
            string[] lines =
            {
                "// TimerVisibility true",
                "TimerVisibility true",
                "SimpleTimer true",
                "timervisibility on",
            };

            Assert.Equal(
                new[]
                {
                    "// TimerVisibility true",
                    "TimerVisibility false",
                    "SimpleTimer true",
                    "TimerVisibility false",
                },
                TweaksScriptsSettingsHelper.SetValue(lines, "TimerVisibility", "false"));
            Assert.Equal(
                lines.Append("InputOverlay true"),
                TweaksScriptsSettingsHelper.SetValue(lines, "InputOverlay", "true"));
            Assert.Equal(
                new[] { "ScrollUpMacroKey" },
                TweaksScriptsSettingsHelper.SetValue(new[] { "ScrollUpMacroKey F8" }, "ScrollUpMacroKey", string.Empty));
        }

        [Fact]
        public void MergeValues_KeepsNewFileKeysAndDropsRemovedOnes()
        {
            string[] packaged =
            {
                "// SOFTIMER SETTINGS",
                "TimerVisibility true",
                "SimpleTimer false",
                "InputOverlay false",
            };
            string[] previous =
            {
                "timervisibility off",
                "SimpleTimer true",
                "ScrollDownMacroKey F5",
            };

            Assert.Equal(
                new[]
                {
                    "// SOFTIMER SETTINGS",
                    "TimerVisibility off",
                    "SimpleTimer true",
                    "InputOverlay false",
                },
                TweaksScriptsSettingsHelper.MergeValues(packaged, previous));
        }
    }
}

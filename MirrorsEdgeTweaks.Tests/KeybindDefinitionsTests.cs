using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Tests
{
    public class KeybindDefinitionsTests
    {
        [Fact]
        public void KeybindMap_ContainsAllNineteenKeybindFields()
        {
            string[] expected =
            {
                "RestartLevelKeyTextBox", "LoadLastCheckpointKeyTextBox", "RestartTimeTrialKeyTextBox",
                "ResetReactionTimeKeyTextBox", "GodModeKeyTextBox", "KillBotsKeyTextBox",
                "ThirdPersonKeyTextBox", "ToggleHUDKeyTextBox", "FPSIndicatorKeyTextBox",
                "LevelStatsKeyTextBox", "TriggersVolumesKeyTextBox", "ShowCollisionKeyTextBox",
                "NoclipKeyTextBox", "SaveStateKeyTextBox", "LoadSavedStateKeyTextBox",
                "SaveTimerLocationKeyTextBox", "DeleteViewedActorKeyTextBox",
                "ScrollDownMacroKeyTextBox", "ScrollUpMacroKeyTextBox"
            };

            Assert.Equal(19, KeybindDefinitions.KeybindMap.Count);
            foreach (var name in expected)
            {
                Assert.True(KeybindDefinitions.KeybindMap.ContainsKey(name), $"Missing keybind entry: {name}");
            }
        }

        [Theory]
        [InlineData("GodModeKeyTextBox", KeybindType.GodMode)]
        [InlineData("TriggersVolumesKeyTextBox", KeybindType.TriggersVolumes)]
        [InlineData("ScrollDownMacroKeyTextBox", KeybindType.Macro)]
        [InlineData("ScrollUpMacroKeyTextBox", KeybindType.Macro)]
        [InlineData("RestartLevelKeyTextBox", KeybindType.Standard)]
        [InlineData("KillBotsKeyTextBox", KeybindType.Standard)]
        public void KeybindMap_HasExpectedType(string field, KeybindType expectedType)
        {
            Assert.Equal(expectedType, KeybindDefinitions.KeybindMap[field].Type);
        }

        [Fact]
        public void KeybindMap_StandardEntry_HasExpectedCommand()
        {
            Assert.Equal("RestartLevel", KeybindDefinitions.KeybindMap["RestartLevelKeyTextBox"].SetCommand);
            Assert.Equal("nxvis collision", KeybindDefinitions.KeybindMap["ShowCollisionKeyTextBox"].SetCommand);
        }

        [Fact]
        public void KeybindInfo_RemoveCommand_DefaultsToSetCommandWhenNotSpecified()
        {
            var info = KeybindDefinitions.KeybindMap["RestartLevelKeyTextBox"];
            Assert.Equal(info.SetCommand, info.RemoveCommand);
        }

        [Fact]
        public void KeybindInfo_DistinctRemoveCommand_IsPreserved()
        {
            // ResetReactionTime has a hold-style set command but a simpler remove command.
            var info = KeybindDefinitions.KeybindMap["ResetReactionTimeKeyTextBox"];
            Assert.NotEqual(info.SetCommand, info.RemoveCommand);
            Assert.Equal("set TdPlayerController ReactionTimeEnergy 0", info.RemoveCommand);
        }

        [Theory]
        [InlineData("OemTilde", "Tilde")]
        [InlineData("Space", "SpaceBar")]
        [InlineData("D1", "ONE")]
        [InlineData("NumPad0", "NumPadZero")]
        [InlineData("Return", "Enter")]
        public void Ue3KeyMap_TranslatesWpfKeyToUe3Name(string wpfKey, string ue3Name)
        {
            Assert.Equal(ue3Name, KeybindDefinitions.Ue3KeyMap[wpfKey]);
        }

        [Fact]
        public void Ue3KeyMap_HasNoEmptyMappings()
        {
            Assert.All(KeybindDefinitions.Ue3KeyMap, kvp =>
            {
                Assert.False(string.IsNullOrWhiteSpace(kvp.Key));
                Assert.False(string.IsNullOrWhiteSpace(kvp.Value));
            });
        }
    }
}

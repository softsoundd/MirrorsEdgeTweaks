namespace MirrorsEdgeTweaks.Helpers
{
    public enum KeybindType { Standard, GodMode, TriggersVolumes, Macro }

    public class KeybindInfo
    {
        public string SetCommand { get; }
        public string RemoveCommand { get; }
        public KeybindType Type { get; }
        public KeybindInfo(string? setCommand, string? removeCommand = null, KeybindType type = KeybindType.Standard)
        {
            SetCommand = setCommand ?? string.Empty;
            RemoveCommand = removeCommand ?? setCommand ?? string.Empty;
            Type = type;
        }
    }

    // Static keybind metadata: the mapping of UI text-box names to their in-game console
    // commands, and the WPF-key-to-UE3-key name translation table.
    public static class KeybindDefinitions
    {
        public static readonly Dictionary<string, KeybindInfo> KeybindMap = new Dictionary<string, KeybindInfo>
        {
            ["RestartLevelKeyTextBox"] = new KeybindInfo("RestartLevel"),
            ["LoadLastCheckpointKeyTextBox"] = new KeybindInfo("RestartFromLastCheckpoint"),
            ["RestartTimeTrialKeyTextBox"] = new KeybindInfo("TriggerRestartRaceblink"),
            ["ResetReactionTimeKeyTextBox"] = new KeybindInfo(
                "set TdPlayerController ReactionTimeEnergy 0 | OnRelease set TdPlayerController ReactionTimeEnergy 100",
                "set TdPlayerController ReactionTimeEnergy 0"),
            ["GodModeKeyTextBox"] = new KeybindInfo(null, null, KeybindType.GodMode),
            ["KillBotsKeyTextBox"] = new KeybindInfo("killbots"),
            ["ThirdPersonKeyTextBox"] = new KeybindInfo("FreeFlightCamera"),
            ["ToggleHUDKeyTextBox"] = new KeybindInfo("Showhud"),
            ["FPSIndicatorKeyTextBox"] = new KeybindInfo("stat xunit"),
            ["LevelStatsKeyTextBox"] = new KeybindInfo("stat levels"),
            ["TriggersVolumesKeyTextBox"] = new KeybindInfo(null, null, KeybindType.TriggersVolumes),
            ["ShowCollisionKeyTextBox"] = new KeybindInfo("nxvis collision"),
            ["NoclipKeyTextBox"] = new KeybindInfo("Noclip"),
            ["SaveStateKeyTextBox"] = new KeybindInfo("SaveLocation"),
            ["LoadSavedStateKeyTextBox"] = new KeybindInfo(
                "TpToSavedLocation | OnRelease TpToSavedLocation_OnRelease",
                "TpToSavedLocation"),
            ["SaveTimerLocationKeyTextBox"] = new KeybindInfo("SaveTimerLocation"),
            ["DeleteViewedActorKeyTextBox"] = new KeybindInfo("DestroyViewedActor"),
            ["ScrollDownMacroKeyTextBox"] = new KeybindInfo("ScrollDownMacroKey", "ScrollDownMacroKey", KeybindType.Macro),
            ["ScrollUpMacroKeyTextBox"] = new KeybindInfo("ScrollUpMacroKey", "ScrollUpMacroKey", KeybindType.Macro),
        };

        public static readonly Dictionary<string, string> Ue3KeyMap = new Dictionary<string, string>
        {
            // Function keys
            { "F1", "F1" }, { "F2", "F2" }, { "F3", "F3" }, { "F4", "F4" },
            { "F5", "F5" }, { "F6", "F6" }, { "F7", "F7" }, { "F8", "F8" },
            { "F9", "F9" }, { "F10", "F10" }, { "F11", "F11" }, { "F12", "F12" },
            
            // Special keys
            { "Escape", "Escape" }, { "Tab", "Tab" }, { "OemTilde", "Tilde" },
            { "Scroll", "ScrollLock" }, { "Pause", "Pause" },
            { "D1", "ONE" }, { "D2", "TWO" }, { "D3", "THREE" }, { "D4", "FOUR" },
            { "D5", "FIVE" }, { "D6", "SIX" }, { "D7", "SEVEN" }, { "D8", "EIGHT" },
            { "D9", "NINE" }, { "D0", "ZERO" },
            { "OemMinus", "Underscore" }, { "OemPlus", "Equals" },
            { "OemBackslash", "Backslash" }, { "OemPipe", "Backslash" },
            { "OemOpenBrackets", "LeftBracket" }, { "OemCloseBrackets", "RightBracket" },
            { "Return", "Enter" }, { "Enter", "Enter" }, { "Capital", "CapsLock" },
            { "OemSemicolon", "Semicolon" }, { "OemQuotes", "Quote" },
            { "LeftShift", "LeftShift" }, { "RightShift", "RightShift" },
            { "OemComma", "Comma" }, { "OemPeriod", "Period" }, { "OemQuestion", "Slash" },
            { "LeftCtrl", "LeftControl" }, { "RightCtrl", "RightControl" },
            { "LeftAlt", "LeftAlt" }, { "RightAlt", "RightAlt" },
            { "Space", "SpaceBar" },
            { "Left", "Left" }, { "Up", "Up" }, { "Down", "Down" }, { "Right", "Right" },
            { "Home", "Home" }, { "End", "End" }, { "Insert", "Insert" },
            { "PageUp", "PageUp" }, { "Delete", "Delete" }, { "PageDown", "PageDown" },
            { "NumLock", "NumLock" },
            { "Divide", "Divide" }, { "Multiply", "Multiply" },
            { "Subtract", "Subtract" }, { "Add", "Add" },
            { "NumPad0", "NumPadZero" }, { "NumPad1", "NumPadOne" },
            { "NumPad2", "NumPadTwo" }, { "NumPad3", "NumPadThree" },
            { "NumPad4", "NumPadFour" }, { "NumPad5", "NumPadFive" },
            { "NumPad6", "NumPadSix" }, { "NumPad7", "NumPadSeven" },
            { "NumPad8", "NumPadEight" }, { "NumPad9", "NumPadNine" },
            { "Decimal", "Decimal" }
        };
    }
}

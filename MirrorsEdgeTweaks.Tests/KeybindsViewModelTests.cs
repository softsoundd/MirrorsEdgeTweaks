using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;
using MirrorsEdgeTweaks.Tests.TestSupport;
using MirrorsEdgeTweaks.ViewModels;

namespace MirrorsEdgeTweaks.Tests
{
    public class KeybindsViewModelTests
    {
        private const string JumpKey = "SpaceBar";
        private const string ScrollDownMacroKey = "F5";

        [Theory]
        [InlineData("SofTimerMenu")]
        [InlineData("EnableGodMode")]
        [InlineData("ShowTriggersAndVolumes")]
        public async Task ApplyKeyAsync_KeyAssignedToScrollMacro_KeepsExistingBinding(string command)
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, tdInputPath, _) = CreateViewModel(temp.Path, Binding("F4", command));
            KeybindEntryViewModel entry = command switch
            {
                "SofTimerMenu" => viewModel.SoftimerMenu,
                "EnableGodMode" => viewModel.GodMode,
                "ShowTriggersAndVolumes" => viewModel.TriggersVolumes,
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };
            string[] before = File.ReadAllLines(tdInputPath);

            await viewModel.ApplyKeyAsync(entry, ScrollDownMacroKey);

            Assert.Equal(before, File.ReadAllLines(tdInputPath));
            Assert.Equal("F4", entry.DisplayKey);
            var warning = Assert.Single(dialogs.Messages);
            Assert.Equal("Duplicate Key Binding", warning.Title);
            Assert.Contains("Scroll Down Macro Key", warning.Message);
        }

        [Fact]
        public async Task ApplyKeyAsync_KeyBoundToAnotherCommand_KeepsExistingBinding()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, tdInputPath, _) = CreateViewModel(temp.Path, Binding("F4", "SofTimerMenu"));
            string[] before = File.ReadAllLines(tdInputPath);

            await viewModel.ApplyKeyAsync(viewModel.SoftimerMenu, JumpKey);

            Assert.Equal(before, File.ReadAllLines(tdInputPath));
            Assert.Equal("F4", viewModel.SoftimerMenu.DisplayKey);
            var warning = Assert.Single(dialogs.Messages);
            Assert.Contains("'Jump'", warning.Message);
        }

        [Fact]
        public async Task ApplyKeyAsync_MacroKeyBoundInTdInput_KeepsExistingMacroKey()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, _, settingsPath) = CreateViewModel(temp.Path);
            string[] before = File.ReadAllLines(settingsPath);

            await viewModel.ApplyKeyAsync(viewModel.ScrollDownMacro, JumpKey);

            Assert.Equal(before, File.ReadAllLines(settingsPath));
            Assert.Equal(ScrollDownMacroKey, viewModel.ScrollDownMacro.DisplayKey);
            var warning = Assert.Single(dialogs.Messages);
            Assert.Contains("'Jump'", warning.Message);
        }

        [Theory]
        [InlineData("F7", "ScrollUpMacroKey F7")]
        [InlineData("", "ScrollUpMacroKey")]
        public async Task ApplyKeyAsync_MacroKey_RewritesItsSettingsLine(string key, string expectedLine)
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[] { $"ScrollDownMacroKey {ScrollDownMacroKey}", "ScrollUpMacroKey F8" });

            await viewModel.ApplyKeyAsync(viewModel.ScrollUpMacro, key);

            Assert.Equal(new[] { $"ScrollDownMacroKey {ScrollDownMacroKey}", expectedLine }, File.ReadAllLines(settingsPath));
            Assert.Equal(key, viewModel.ScrollUpMacro.DisplayKey);
            Assert.Empty(dialogs.Messages);
        }

        [Fact]
        public async Task ApplyKeyAsync_FreeKey_RebindsAndUpdatesField()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, tdInputPath, _) = CreateViewModel(temp.Path, Binding("F4", "SofTimerMenu"));

            await viewModel.ApplyKeyAsync(viewModel.SoftimerMenu, "F6");

            string[] lines = File.ReadAllLines(tdInputPath);
            Assert.Contains(Binding("F6", "SofTimerMenu"), lines);
            Assert.DoesNotContain(Binding("F4", "SofTimerMenu"), lines);
            Assert.Equal("F6", viewModel.SoftimerMenu.DisplayKey);
            Assert.Empty(dialogs.Messages);
        }

        [Fact]
        public async Task ApplyKeyAsync_NoMouseSmoothingLine_InsertsBindingAfterSectionHeader()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, tdInputPath, _) = CreateViewModel(temp.Path);
            File.WriteAllLines(tdInputPath, new[] { "[Engine.PlayerInput]", Binding(JumpKey, "Jump") });

            await viewModel.ApplyKeyAsync(viewModel.SoftimerMenu, "F6");

            Assert.Equal(
                new[] { "[Engine.PlayerInput]", Binding("F6", "SofTimerMenu"), Binding(JumpKey, "Jump") },
                File.ReadAllLines(tdInputPath));
            Assert.Equal("F6", viewModel.SoftimerMenu.DisplayKey);
        }

        [Fact]
        public async Task ApplyKeyAsync_NoPlayerInputSection_ShowsErrorAndKeepsField()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, tdInputPath, _) = CreateViewModel(temp.Path);
            File.WriteAllLines(tdInputPath, new[] { "[Engine.Engine]" });

            await viewModel.ApplyKeyAsync(viewModel.SoftimerMenu, "F6");

            Assert.Equal(new[] { "[Engine.Engine]" }, File.ReadAllLines(tdInputPath));
            Assert.Equal(string.Empty, viewModel.SoftimerMenu.DisplayKey);
            Assert.Equal(DialogMessageType.Error, Assert.Single(dialogs.Messages).Type);
        }

        [Fact]
        public async Task ApplyKeyAsync_EmptyKey_RemovesBindingAndClearsField()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, tdInputPath, _) = CreateViewModel(temp.Path, Binding("F4", "SofTimerMenu"));

            await viewModel.ApplyKeyAsync(viewModel.SoftimerMenu, "");

            Assert.DoesNotContain(Binding("F4", "SofTimerMenu"), File.ReadAllLines(tdInputPath));
            Assert.Equal(string.Empty, viewModel.SoftimerMenu.DisplayKey);
            Assert.Empty(dialogs.Messages);
        }

        [Theory]
        [InlineData("ResetReactionTimeKeyTextBox")]
        [InlineData("LoadSavedStateKeyTextBox")]
        public async Task ApplyKeyAsync_EmptyKey_RemovesBindingWithOnReleasePart(string keybindName)
        {
            KeybindInfo info = KeybindDefinitions.KeybindMap[keybindName];
            using var temp = new TempDirectory();
            var (viewModel, _, tdInputPath, _) = CreateViewModel(temp.Path, Binding("R", info.SetCommand));
            KeybindEntryViewModel entry = viewModel.CustomKeybinds.Concat(viewModel.CheatTrainerKeybinds).Single(e => e.Info == info);

            await viewModel.ApplyKeyAsync(entry, "");

            Assert.DoesNotContain(Binding("R", info.SetCommand), File.ReadAllLines(tdInputPath));
            Assert.Equal(string.Empty, entry.DisplayKey);
        }

        [Theory]
        [InlineData("DisableGodMode")]
        [InlineData("HideTriggersAndVolumes")]
        public void LoadCustomKeybinds_ToggleKeySavedInOtherState_ShowsKey(string command)
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, _) = CreateViewModel(temp.Path, Binding("G", command));
            KeybindEntryViewModel entry = command switch
            {
                "DisableGodMode" => viewModel.GodMode,
                "HideTriggersAndVolumes" => viewModel.TriggersVolumes,
                _ => throw new ArgumentOutOfRangeException(nameof(command)),
            };

            Assert.Equal("G", entry.DisplayKey);
        }

        [Theory]
        [InlineData("G")]
        [InlineData("H")]
        public async Task ApplyKeyAsync_GodModeKeySavedInOtherState_RewritesItForTheNewKey(string newKey)
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, tdInputPath, _) = CreateViewModel(temp.Path, Binding("G", "DisableGodMode"));

            await viewModel.ApplyKeyAsync(viewModel.GodMode, newKey);

            string[] lines = File.ReadAllLines(tdInputPath);
            Assert.Single(lines, line => line.Contains("Command=\"EnableGodMode\"") || line.Contains("Command=\"DisableGodMode\""));
            Assert.Contains(Binding(newKey, "EnableGodMode"), lines);
            Assert.Contains(lines, line => line.StartsWith("Bindings=(Name=\"EnableGodMode\"") && line.Contains($"| SetBind {newKey} DisableGodMode\""));
            Assert.Contains(lines, line => line.StartsWith("Bindings=(Name=\"DisableGodMode\"") && line.Contains($"| SetBind {newKey} EnableGodMode\""));
            Assert.Equal(newKey, viewModel.GodMode.DisplayKey);
            Assert.Empty(dialogs.Messages);
        }

        [Fact]
        public async Task ApplyKeyAsync_EmptyKey_RemovesGodModeKeySavedInOtherState()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, tdInputPath, _) = CreateViewModel(temp.Path, Binding("G", "DisableGodMode"));

            await viewModel.ApplyKeyAsync(viewModel.GodMode, "");

            Assert.DoesNotContain(Binding("G", "DisableGodMode"), File.ReadAllLines(tdInputPath));
            Assert.Equal(string.Empty, viewModel.GodMode.DisplayKey);
        }

        [Fact]
        public void LoadCustomKeybinds_BindingNoLongerInFile_ClearsField()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, tdInputPath, _) = CreateViewModel(temp.Path, Binding("F4", "SofTimerMenu"));
            File.WriteAllLines(tdInputPath, new[] { "[Engine.PlayerInput]" });

            viewModel.LoadCustomKeybinds();

            Assert.Equal(string.Empty, viewModel.SoftimerMenu.DisplayKey);
        }

        [Fact]
        public void LoadMacroKeybinds_ReadsLastLineForEachKey()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[]
            {
                "// ScrollDownMacroKey F1",
                "ScrollDownMacroKey F2",
                "  scrolldownmacrokey   F3  ",
                "ScrollUpMacroKey F4",
            });

            viewModel.LoadMacroKeybinds();

            Assert.Equal("F3", viewModel.ScrollDownMacro.DisplayKey);
            Assert.Equal("F4", viewModel.ScrollUpMacro.DisplayKey);
        }

        [Fact]
        public void LoadMacroKeybinds_KeyNoLongerInFile_ClearsField()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[] { "ScrollUpMacroKey" });

            viewModel.LoadMacroKeybinds();

            Assert.Equal(string.Empty, viewModel.ScrollDownMacro.DisplayKey);
        }

        [Theory]
        [InlineData("TimerVisibility true", 0)]
        [InlineData("timervisibility ON", 0)]
        [InlineData("TimerVisibility 1", 0)]
        [InlineData("TimerVisibility false", 1)]
        [InlineData("TimerVisibility off", 1)]
        [InlineData("TimerVisibility 0", 1)]
        [InlineData("TimerVisibility maybe", -1)]
        [InlineData("TimerVisibility", -1)]
        [InlineData("// TimerVisibility true", -1)]
        public void LoadSoftimerSettings_MapsFileValueToComboBoxIndex(string line, int expectedIndex)
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[] { line });

            viewModel.LoadSoftimerSettings();

            Assert.Equal(expectedIndex, Setting(viewModel, "TimerVisibility").SelectedIndex);
        }

        [Fact]
        public void LoadSoftimerSettings_KeyRepeated_UsesLastLine()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[] { "BagHUD true", "BagHUD false" });

            viewModel.LoadSoftimerSettings();

            Assert.Equal(1, Setting(viewModel, "BagHUD").SelectedIndex);
        }

        [Fact]
        public void LoadSoftimerSettings_KeyNoLongerInFile_ClearsField()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[] { "BagHUD true" });
            viewModel.LoadSoftimerSettings();
            File.WriteAllLines(settingsPath, new[] { "Speedometer true" });

            viewModel.LoadSoftimerSettings();

            Assert.Equal(-1, Setting(viewModel, "BagHUD").SelectedIndex);
        }

        [Fact]
        public async Task LoadSoftimerSettings_DoesNotRewriteSettingsFile()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path, out GameSession session);
            File.WriteAllLines(settingsPath, new[] { "BagHUD ON" });

            viewModel.LoadSoftimerSettings();
            await WaitForAppliesAsync(session);

            Assert.Equal(new[] { "BagHUD ON" }, File.ReadAllLines(settingsPath));
        }

        [Fact]
        public async Task ApplySoftimerSettingAsync_RewritesEveryLineForThatKeyOnly()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[]
            {
                "// TimerVisibility true",
                "TimerVisibility true",
                "SimpleTimer true",
                $"ScrollDownMacroKey {ScrollDownMacroKey}",
                "timervisibility on",
            });

            await viewModel.ApplySoftimerSettingAsync(Setting(viewModel, "TimerVisibility"), enabled: false);

            Assert.Equal(new[]
            {
                "// TimerVisibility true",
                "TimerVisibility false",
                "SimpleTimer true",
                $"ScrollDownMacroKey {ScrollDownMacroKey}",
                "TimerVisibility false",
            }, File.ReadAllLines(settingsPath));
            Assert.Empty(dialogs.Messages);
        }

        [Fact]
        public async Task ApplySoftimerSettingAsync_KeyNotInFile_AppendsLine()
        {
            using var temp = new TempDirectory();
            var (viewModel, _, _, settingsPath) = CreateViewModel(temp.Path);
            string[] before = File.ReadAllLines(settingsPath);

            await viewModel.ApplySoftimerSettingAsync(Setting(viewModel, "InputOverlay"), enabled: true);

            Assert.Equal(before.Append("InputOverlay true"), File.ReadAllLines(settingsPath));
        }

        [Fact]
        public async Task ApplySoftimerSettingAsync_NoSettingsFile_WarnsAndClearsField()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, _, settingsPath) = CreateViewModel(temp.Path);
            File.WriteAllLines(settingsPath, new[] { "Speedometer false" });
            viewModel.LoadSoftimerSettings();
            File.Delete(settingsPath);
            SoftimerSettingEntryViewModel speedometer = Setting(viewModel, "Speedometer");

            await viewModel.ApplySoftimerSettingAsync(speedometer, enabled: true);

            Assert.False(File.Exists(settingsPath));
            Assert.Equal(-1, speedometer.SelectedIndex);
            Assert.Equal("Tweaks Scripts Not Installed", Assert.Single(dialogs.Messages).Title);
        }

        [Fact]
        public async Task SoftimerSettingSelectedIndex_Changed_WritesValueToSettingsFile()
        {
            using var temp = new TempDirectory();
            var (viewModel, dialogs, _, settingsPath) = CreateViewModel(temp.Path, out GameSession session);
            File.WriteAllLines(settingsPath, new[] { "BagHUD true", "Speedometer false" });
            viewModel.LoadSoftimerSettings();

            Setting(viewModel, "BagHUD").SelectedIndex = 1;
            Setting(viewModel, "Speedometer").SelectedIndex = 0;
            await WaitForAppliesAsync(session);

            Assert.Equal(new[] { "BagHUD false", "Speedometer true" }, File.ReadAllLines(settingsPath));
            Assert.Empty(dialogs.Messages);
        }

        private static string Binding(string key, string command) =>
            $"Bindings=(Name=\"{key}\",Command=\"{command}\",Control=False,Shift=False,Alt=False)";

        private static SoftimerSettingEntryViewModel Setting(KeybindsViewModel viewModel, string key) =>
            viewModel.SoftimerSettings.Single(setting => setting.Key == key);

        // The gate runs one apply at a time, so this completes after every apply queued before it.
        private static Task WaitForAppliesAsync(GameSession session) =>
            session.ApplyGate.RunAsync(() => Task.CompletedTask);

        private static (KeybindsViewModel ViewModel, RecordingDialogService Dialogs, string TdInputPath, string SettingsPath) CreateViewModel(
            string root, params string[] extraBindings) =>
            CreateViewModel(root, out _, extraBindings);

        private static (KeybindsViewModel ViewModel, RecordingDialogService Dialogs, string TdInputPath, string SettingsPath) CreateViewModel(
            string root, out GameSession session, params string[] extraBindings)
        {
            session = new GameSession();
            session.Config.UserFolderPath = Path.Combine(root, "TdGame");
            session.Config.GameDirectoryPath = Path.Combine(root, "Game");

            string tdInputPath = UserTdGamePathHelper.GetTdInputIniPath(session.Config);
            Directory.CreateDirectory(Path.GetDirectoryName(tdInputPath)!);
            File.WriteAllLines(tdInputPath,
                new[] { "[Engine.PlayerInput]", "bEnableMouseSmoothing=false", Binding(JumpKey, "Jump") }.Concat(extraBindings));

            string settingsPath = Path.Combine(session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllLines(settingsPath, new[] { $"ScrollDownMacroKey {ScrollDownMacroKey}", "ScrollUpMacroKey" });

            var dialogs = new RecordingDialogService();
            var viewModel = new KeybindsViewModel(dialogs, session);
            viewModel.LoadCustomKeybinds();
            viewModel.LoadMacroKeybinds();
            viewModel.LoadSoftimerSettings();
            return (viewModel, dialogs, tdInputPath, settingsPath);
        }
    }
}

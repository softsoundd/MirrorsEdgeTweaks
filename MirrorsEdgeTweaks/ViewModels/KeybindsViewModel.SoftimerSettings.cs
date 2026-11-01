using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class SoftimerSettingEntryViewModel : ObservableObject
    {
        private readonly KeybindsViewModel _owner;

        [ObservableProperty] private int _selectedIndex = -1;

        public string Key { get; }
        public string Label { get; }
        public string InfoTitle { get; }
        public string InfoBody { get; }

        public SoftimerSettingEntryViewModel(KeybindsViewModel owner, string key, string label, string infoTitle, string infoBody)
        {
            _owner = owner;
            Key = key;
            Label = label;
            InfoTitle = infoTitle;
            InfoBody = infoBody;
        }

        partial void OnSelectedIndexChanged(int value) => _owner.OnSoftimerSettingChanged(this, value);

        [RelayCommand]
        private void ShowInfo() => _owner.ShowInfo(this);
    }

    public partial class KeybindsViewModel
    {
        public IReadOnlyList<SoftimerSettingEntryViewModel> SoftimerSettings { get; }

        private SoftimerSettingEntryViewModel[] CreateSoftimerSettings()
        {
            SoftimerSettingEntryViewModel Make(string key, string label, string title, string body) =>
                new SoftimerSettingEntryViewModel(this, key, label, title,
                    body + "\n\nNote: Requires the Tweaks Scripts package. Softimer settings apply while Softimer is active.");

            return new[]
            {
                Make("TimerVisibility", "Timer Visibility:", "Timer Visibility Information",
                    "Shows the load removed timer. When disabled, the timer stays hidden until a run is finished."),

                Make("SimpleTimer", "Simple Timer:", "Simple Timer Information",
                    "Removes the \"LRT\" prefix from the load removed timer and shortens the chapter split and segment timer prefixes."),

                Make("SplitTimer", "Split Timer:", "Split Timer Information",
                    "Shows the time of the last chapter split."),

                Make("SegmentTimer", "Segment Timer:", "Segment Timer Information",
                    "Shows the segment timer, which counts the time spent in the current chapter."),

                Make("BagHUD", "Bag HUD:", "Bag HUD Information",
                    "Keeps the bag status popup on screen. Only has an effect in the 100% and All Achievements categories."),

                Make("TrainerHUD", "Trainer HUD:", "Trainer HUD Information",
                    "Shows trainer information, such as health, reaction time, movement state, speed, position and view angles. " +
                    "Checkpoint timers are also available while Cheats + Trainer mode is active."),

                Make("Speedometer", "Speedometer:", "Speedometer Information",
                    "Shows a speedometer in the style of the time trial speedometer, for use in story mode and individual levels (ILs)."),

                Make("MacroFeedback", "Macro Feedback:", "Macro Feedback Information",
                    "Shows on-screen feedback when the native macro is used."),

                Make("ChapterSplits", "Chapter Splits:", "Chapter Splits Information",
                    "Shows chapter splits in the style of the time trial progress bar, except in individual levels (ILs).\n\n" +
                    "If Checkpoint Splits is also enabled, the two cycle every 5 seconds."),

                Make("CheckpointSplits", "Checkpoint Splits:", "Checkpoint Splits Information",
                    "Shows hard checkpoint splits in the style of the time trial progress bar.\n\n" +
                    "If Chapter Splits is also enabled, the two cycle every 5 seconds."),

                Make("UseGlitchSoundFix", "Use Glitch Sound Fix:", "Use Glitch Sound Fix Information",
                    "Prevents the same sound source from playing concurrently, keeping it at the same volume as regular use interactions."),

                Make("InputOverlay", "Input Overlay:", "Input Overlay Information",
                    "Shows the keys and buttons being pressed."),
            };
        }

        public void ShowInfo(SoftimerSettingEntryViewModel setting) =>
            _dialogService.ShowMessage(setting.InfoTitle, setting.InfoBody, DialogMessageType.Information);

        public void LoadSoftimerSettings()
        {
            var values = new Dictionary<string, string>();

            try
            {
                if (TweaksScriptsSettingsPath is string settingsFilePath && File.Exists(settingsFilePath))
                    values = TweaksScriptsSettingsHelper.ReadValues(File.ReadAllLines(settingsFilePath));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load Softimer settings: {ex.Message}");
            }

            try
            {
                _isLoading = true;

                foreach (var setting in SoftimerSettings)
                    setting.SelectedIndex = values.TryGetValue(setting.Key, out string? value) ? ToSettingIndex(value) : -1;
            }
            finally
            {
                _isLoading = false;
            }
        }

        internal void OnSoftimerSettingChanged(SoftimerSettingEntryViewModel setting, int index)
        {
            if (_isLoading || _session.IsProcessingGameDirectory || index < 0)
                return;

            _ = ApplySoftimerSettingAsync(setting, enabled: index == 0);
        }

        // The combo box already shows the new value, so a failed save reloads the settings to revert it.
        internal Task ApplySoftimerSettingAsync(SoftimerSettingEntryViewModel setting, bool enabled) =>
            _session.ApplyGate.RunAsync(async () =>
            {
                if (TweaksScriptsSettingsPath is not string settingsFilePath || !File.Exists(settingsFilePath))
                {
                    await _dialogService.ShowMessageAsync("Tweaks Scripts Not Installed",
                        "TweaksScriptsSettings was not found.\n\n" +
                        "Install the Tweaks Scripts package from Game Tweaks before configuring Softimer settings.",
                        DialogMessageType.Warning);
                    LoadSoftimerSettings();
                    return;
                }

                try
                {
                    await Task.Run(() => WriteTweaksScriptsSetting(settingsFilePath, setting.Key, enabled ? "true" : "false"));
                }
                catch (Exception ex)
                {
                    await _dialogService.ShowMessageAsync("Error", $"Failed to update Softimer setting:\n\n{ex.Message}", DialogMessageType.Error);
                    LoadSoftimerSettings();
                }
            });

        // Matches the values that the Tweaks Scripts exec functions accept.
        private static int ToSettingIndex(string value) => value.ToLowerInvariant() switch
        {
            "true" or "1" or "on" => 0,
            "false" or "0" or "off" => 1,
            _ => -1,
        };
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    // View model for the "Initialisation Settings" section of the Other Tweaks tab: intro video,
    // main menu delay, time trial countdown and the skip-online-check patch. Skip Online Check
    // drives TdGamePatcher with the FOV-agnostic-sens / compensated-clip flags owned by the Graphics
    // tab, so it reads those from GraphicsTweaksViewModel.
    public partial class InitialisationSettingsViewModel : ObservableObject
    {
        private readonly IDialogService _dialogService;
        private readonly GameSession _session;
        private readonly UnlockedConfigsViewModel _unlockedConfigs;
        private readonly GraphicsTweaksViewModel _graphics;

        private bool _isLoading;

        [ObservableProperty] private int _introVideoIndex = -1;        // 0 = Enabled, 1 = Disabled
        [ObservableProperty] private int _mainMenuDelayIndex = -1;     // 0 = Enabled, 1 = Disabled
        [ObservableProperty] private int _timeTrialCountdownIndex = -1; // 0..3
        [ObservableProperty] private int _skipOnlineIndex = -1;        // 0 = Disabled, 1 = Enabled

        public InitialisationSettingsViewModel(
            IDialogService dialogService,
            GameSession session,
            UnlockedConfigsViewModel unlockedConfigs,
            GraphicsTweaksViewModel graphics)
        {
            _dialogService = dialogService;
            _session = session;
            _unlockedConfigs = unlockedConfigs;
            _graphics = graphics;
        }

        private static string TdEngineIniPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

        private void SetSilently(Action action)
        {
            bool previous = _isLoading;
            _isLoading = true;
            try { action(); }
            finally { _isLoading = previous; }
        }

        // ---- Intro video ----

        partial void OnIntroVideoIndexChanged(int value) => _ = OnIntroVideoChangedAsync(value);

        private async Task OnIntroVideoChangedAsync(int value)
        {
            if (value < 0 || _isLoading)
                return;

            try
            {
                string tdEnginePath = TdEngineIniPath;

                if (!File.Exists(tdEnginePath))
                {
                    _dialogService.ShowMessage("Error", "TdEngine.ini not found. Please launch Mirror's Edge at least once to create the configuration file.", DialogMessageType.Error);
                    return;
                }

                bool enableIntroVideo = value == 0;

                await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(tdEnginePath))
                        {
                            throw new FileNotFoundException($"TdEngine.ini not found at: {tdEnginePath}");
                        }

                        var lines = File.ReadAllLines(tdEnginePath);
                        bool inFullScreenMovieSection = false;
                        bool modified = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string trimmedLine = lines[i].Trim();

                            if (trimmedLine.StartsWith("["))
                            {
                                inFullScreenMovieSection = trimmedLine == "[FullScreenMovie]";
                                continue;
                            }

                            if (inFullScreenMovieSection)
                            {
                                if (trimmedLine.StartsWith(";StartupMovies=") || trimmedLine.StartsWith("StartupMovies="))
                                {
                                    if (enableIntroVideo)
                                    {
                                        lines[i] = "StartupMovies=StartupMovie";
                                    }
                                    else
                                    {
                                        if (!lines[i].TrimStart().StartsWith(";"))
                                        {
                                            int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                            string indent = lines[i].Substring(0, indentLength);
                                            lines[i] = indent + ";StartupMovies=StartupMovie";
                                        }
                                    }
                                    modified = true;
                                    break;
                                }
                            }
                        }

                        if (modified)
                        {
                            FileAttributes attributes = File.GetAttributes(tdEnginePath);
                            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                File.SetAttributes(tdEnginePath, attributes & ~FileAttributes.ReadOnly);

                            File.WriteAllLines(tdEnginePath, lines);
                            File.SetAttributes(tdEnginePath, File.GetAttributes(tdEnginePath) | FileAttributes.ReadOnly);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        throw;
                    }
                    catch (IOException ex)
                    {
                        throw new IOException($"Failed to access TdEngine.ini: {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to modify intro video setting:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        public void RefreshIntroVideo()
        {
            try
            {
                string tdEnginePath = TdEngineIniPath;

                if (!File.Exists(tdEnginePath))
                    return;

                var lines = File.ReadAllLines(tdEnginePath);
                bool inFullScreenMovieSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inFullScreenMovieSection = trimmedLine == "[FullScreenMovie]";
                        continue;
                    }

                    if (inFullScreenMovieSection)
                    {
                        if (trimmedLine.StartsWith(";StartupMovies="))
                        {
                            SetSilently(() => IntroVideoIndex = 1);
                            return;
                        }
                        else if (trimmedLine.StartsWith("StartupMovies="))
                        {
                            SetSilently(() => IntroVideoIndex = 0);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load intro video setting: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ShowIntroVideoInfo()
        {
            _dialogService.ShowMessage("Intro Video Information",
                "Controls whether the intro video plays when launching the game. Disabling this setting saves 14 seconds.",
                DialogMessageType.Information);
        }

        // ---- Main menu delay ----

        partial void OnMainMenuDelayIndexChanged(int value) => _ = OnMainMenuDelayChangedAsync(value);

        private async Task OnMainMenuDelayChangedAsync(int value)
        {
            if (value < 0 || _isLoading || string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return;

            try
            {
                string defaultUIPath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "Config", "DefaultUI.ini");

                if (!File.Exists(defaultUIPath))
                {
                    _dialogService.ShowMessage("Error", "DefaultUI.ini not found in the game directory.", DialogMessageType.Error);
                    return;
                }

                bool enableDelay = value == 0;

                if (!enableDelay)
                {
                    bool isPatched = _unlockedConfigs.UnlockedConfigsStatus == "Patched";
                    if (!isPatched)
                    {
                        _dialogService.ShowMessage("Warning", "The config modification patch in the 'Game Tweaks' section is not applied. " +
                            "Please apply the patch in order for your game to launch with the disabled main menu delay.", DialogMessageType.Warning);
                    }
                }

                await Task.Run(() =>
                {
                    var lines = File.ReadAllLines(defaultUIPath);
                    bool inTdUISceneStartSection = false;
                    bool modified = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmedLine = lines[i].Trim();

                        if (trimmedLine.StartsWith("["))
                        {
                            inTdUISceneStartSection = trimmedLine == "[TdGame.TdUIScene_Start]";
                            continue;
                        }

                        if (inTdUISceneStartSection)
                        {
                            if (trimmedLine.StartsWith("TimeTillStartButton="))
                            {
                                string newValue = enableDelay ? "4" : "0";

                                int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                string indent = lines[i].Substring(0, indentLength);
                                lines[i] = indent + "TimeTillStartButton=" + newValue;

                                modified = true;
                                break;
                            }
                        }
                    }

                    if (modified)
                    {
                        File.WriteAllLines(defaultUIPath, lines);
                    }
                });
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to modify main menu delay setting:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        public void RefreshMainMenuDelay()
        {
            try
            {
                if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                    return;

                string defaultUIPath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "Config", "DefaultUI.ini");

                if (!File.Exists(defaultUIPath))
                    return;

                var lines = File.ReadAllLines(defaultUIPath);
                bool inTdUISceneStartSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inTdUISceneStartSection = trimmedLine == "[TdGame.TdUIScene_Start]";
                        continue;
                    }

                    if (inTdUISceneStartSection)
                    {
                        if (trimmedLine.StartsWith("TimeTillStartButton="))
                        {
                            string value = trimmedLine.Substring("TimeTillStartButton=".Length);
                            if (value == "0")
                                SetSilently(() => MainMenuDelayIndex = 1);
                            else if (value == "4")
                                SetSilently(() => MainMenuDelayIndex = 0);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load main menu delay setting: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ShowMainMenuDelayInfo()
        {
            _dialogService.ShowMessage("Main Menu Delay Information",
                "When launching the game, you have to wait 4 seconds at the title screen before you can pass input to proceed to the main menu. " +
                "Disabling this delay allows any input to be pressed immediately, getting you to the main menu faster.",
                DialogMessageType.Information);
        }

        // ---- Time trial countdown ----

        partial void OnTimeTrialCountdownIndexChanged(int value) => _ = OnTimeTrialCountdownChangedAsync(value);

        private async Task OnTimeTrialCountdownChangedAsync(int value)
        {
            if (value < 0 || _isLoading || string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return;

            try
            {
                string defaultGamePath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "Config", "DefaultGame.ini");

                if (!File.Exists(defaultGamePath))
                {
                    _dialogService.ShowMessage("Error", "DefaultGame.ini not found in the game directory.", DialogMessageType.Error);
                    return;
                }

                string newValue = value switch
                {
                    0 => "3",
                    1 => "2",
                    2 => "1",
                    3 => "0",
                    _ => "3"
                };

                if (value != 0)
                {
                    bool isPatched = _unlockedConfigs.UnlockedConfigsStatus == "Patched";
                    if (!isPatched)
                    {
                        _dialogService.ShowMessage("Warning", "The config modification patch in the 'Game Tweaks' section is not applied. " +
                            "Please apply the patch in order for your game to launch with the custom time trial countdown.", DialogMessageType.Warning);
                    }
                }

                await Task.Run(() =>
                {
                    var lines = File.ReadAllLines(defaultGamePath);
                    bool inTdSPTimeTrialGameSection = false;
                    bool modified = false;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmedLine = lines[i].Trim();

                        if (trimmedLine.StartsWith("["))
                        {
                            inTdSPTimeTrialGameSection = trimmedLine == "[TdGame.TdSPTimeTrialGame]";
                            continue;
                        }

                        if (inTdSPTimeTrialGameSection)
                        {
                            if (trimmedLine.StartsWith("RaceCountDownTime="))
                            {
                                int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                string indent = lines[i].Substring(0, indentLength);
                                lines[i] = indent + "RaceCountDownTime=" + newValue;

                                modified = true;
                                break;
                            }
                        }
                    }

                    if (modified)
                    {
                        File.WriteAllLines(defaultGamePath, lines);
                    }
                });
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to modify time trial countdown setting:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        public void RefreshTimeTrialCountdown()
        {
            try
            {
                if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                    return;

                string defaultGamePath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "Config", "DefaultGame.ini");

                if (!File.Exists(defaultGamePath))
                    return;

                var lines = File.ReadAllLines(defaultGamePath);
                bool inTdSPTimeTrialGameSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inTdSPTimeTrialGameSection = trimmedLine == "[TdGame.TdSPTimeTrialGame]";
                        continue;
                    }

                    if (inTdSPTimeTrialGameSection)
                    {
                        if (trimmedLine.StartsWith("RaceCountDownTime="))
                        {
                            string value = trimmedLine.Substring("RaceCountDownTime=".Length);
                            int index = value switch
                            {
                                "3" => 0,
                                "2" => 1,
                                "1" => 2,
                                "0" => 3,
                                _ => -1
                            };
                            if (index != -1)
                                SetSilently(() => TimeTrialCountdownIndex = index);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load time trial countdown setting: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ShowTimeTrialCountdownInfo()
        {
            _dialogService.ShowMessage("Time Trial Countdown Information",
                "Controls the countdown timer duration before the start of a time trial. " +
                "If Softimer is active, the countdown timer will revert to the default value of 4 seconds regardless of the setting selected here.",
                DialogMessageType.Information);
        }

        // ---- Skip online check ----

        partial void OnSkipOnlineIndexChanged(int value)
        {
            _session.OnlineSkipEnabled = value == 1;
            _ = OnSkipOnlineChangedAsync(value);
        }

        private async Task OnSkipOnlineChangedAsync(int value)
        {
            if (value < 0 || _isLoading)
                return;

            var tdGamePath = _session.Config.TdGamePackagePath;
            if (tdGamePath == null || !File.Exists(tdGamePath))
                return;

            try
            {
                bool enableOnlineSkip = value == 1;
                bool enableSens = _graphics.FovAgnosticSens;
                bool enableClip = _graphics.CompensatedClip;

                await Task.Run(() =>
                {
                    TdGamePatcher.Reconcile(tdGamePath, enableSens, enableClip, enableOnlineSkip);
                });
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply skip online check patch: {ex.Message}", DialogMessageType.Error);
            }
        }

        // Sets the Skip Online Check index from a known package state without re-applying.
        public void SetSkipOnlineFromState(bool onlineSkipApplied) =>
            SetSilently(() => SkipOnlineIndex = onlineSkipApplied ? 1 : 0);

        public void RefreshSkipOnlineCheck()
        {
            try
            {
                var tdGamePath = _session.Config.TdGamePackagePath;
                if (tdGamePath == null || !File.Exists(tdGamePath))
                    return;

                var tdState = TdGamePatcher.DetectState(tdGamePath);
                SetSilently(() => SkipOnlineIndex = tdState.OnlineSkipApplied ? 1 : 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load skip online check setting: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ShowSkipOnlineCheckInfo()
        {
            _dialogService.ShowMessage("Skip Online Check Information",
                "Skips the dead EA online login attempt for Time Trials and Speedruns. " +
                "The game normally tries to connect to EA servers that no longer exist, " +
                "causing a delay of three intermediate connection check UI scenes before reaching the offline mode. " +
                "With this enabled, the game goes straight to the offline mode.",
                DialogMessageType.Information);
        }
    }
}

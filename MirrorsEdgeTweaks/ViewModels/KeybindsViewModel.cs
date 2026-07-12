using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    // A single keybind row: its display label, captured key (DisplayKey), the click-to-capture
    // command and the info command, plus the static KeybindInfo metadata and info-dialog text.
    // Rendered by the shared keybind-row DataTemplate in MainWindow.xaml.
    public partial class KeybindEntryViewModel : ObservableObject
    {
        private readonly KeybindsViewModel _owner;

        [ObservableProperty] private string _displayKey = "";

        public string Label { get; }
        public KeybindInfo Info { get; }
        public string InfoTitle { get; }
        public string InfoBody { get; }

        public KeybindEntryViewModel(KeybindsViewModel owner, string label, KeybindInfo info, string infoTitle, string infoBody)
        {
            _owner = owner;
            Label = label;
            Info = info;
            InfoTitle = infoTitle;
            InfoBody = infoBody;
        }

        [RelayCommand]
        private Task Capture() => _owner.CaptureAsync(this);

        [RelayCommand]
        private void ShowInfo() => _owner.ShowInfo(this);
    }

    // View model for the Custom / Cheat-Trainer / Macro keybind sections of the Other Tweaks tab.
    // Owns the 19 keybind entries, the capture flow (KeybindCaptureDialog), and the TdInput.ini /
    // TweaksScriptsSettings apply-load logic.
    public partial class KeybindsViewModel : ObservableObject
    {
        private readonly IDialogService _dialogService;
        private readonly GameSession _session;

        private bool _isLoading;

        public KeybindEntryViewModel RestartLevel { get; }
        public KeybindEntryViewModel LoadLastCheckpoint { get; }
        public KeybindEntryViewModel RestartTimeTrial { get; }
        public KeybindEntryViewModel ResetReactionTime { get; }
        public KeybindEntryViewModel GodMode { get; }
        public KeybindEntryViewModel KillBots { get; }
        public KeybindEntryViewModel ThirdPerson { get; }
        public KeybindEntryViewModel ToggleHUD { get; }
        public KeybindEntryViewModel FPSIndicator { get; }
        public KeybindEntryViewModel LevelStats { get; }
        public KeybindEntryViewModel TriggersVolumes { get; }
        public KeybindEntryViewModel ShowCollision { get; }
        public KeybindEntryViewModel Noclip { get; }
        public KeybindEntryViewModel SaveState { get; }
        public KeybindEntryViewModel LoadSavedState { get; }
        public KeybindEntryViewModel SaveTimerLocation { get; }
        public KeybindEntryViewModel DeleteViewedActor { get; }
        public KeybindEntryViewModel ScrollDownMacro { get; }
        public KeybindEntryViewModel ScrollUpMacro { get; }

        // Section collections rendered by the shared keybind-row DataTemplate (ItemsControl).
        public IReadOnlyList<KeybindEntryViewModel> CustomKeybinds { get; }
        public IReadOnlyList<KeybindEntryViewModel> CheatTrainerKeybinds { get; }
        public IReadOnlyList<KeybindEntryViewModel> MacroKeybinds { get; }

        public KeybindsViewModel(IDialogService dialogService, GameSession session)
        {
            _dialogService = dialogService;
            _session = session;

            KeybindEntryViewModel Make(string textBoxName, string label, string title, string body) =>
                new KeybindEntryViewModel(this, label, KeybindDefinitions.KeybindMap[textBoxName], title, body);

            RestartLevel = Make("RestartLevelKeyTextBox", "Restart Level:", "Restart Level Keybind Information",
                "Restarts the level from where you started (this does not respect checkpoints reached, refer to the \"Load last checkpoint\" keybind for this).\n\n" +
                "In time trial and speedrun modes, this will reload the level back to the start.\n\nIn chapter mode, this will reload the level back to the checkpoint that was selected in the main menu.\n\n" +
                "In story mode, this will reload the level back to where you started when you pressed \"Continue Game\" (except when you complete a chapter, you'll instead respawn at checkpoint A of the next chapter).");

            LoadLastCheckpoint = Make("LoadLastCheckpointKeyTextBox", "Load Last Checkpoint:", "Load Last Checkpoint Keybind Information",
                "In earlier dev/review builds of Mirror's Edge there used to be a dedicated \"Load last checkpoint\" button in the pause menu that would reload Faith " +
                "to the last hard or soft checkpoint that was reached, however, this never made its way into the game's final release.\n\nAlthough the UI for this was removed, " +
                "the underlying function for this still exists in retail builds, and Mirror's Edge Tweaks can patch it to become executable via keybinds/console commands. " +
                "This is essentially a faster way to reset without having to force a death.");

            RestartTimeTrial = Make("RestartTimeTrialKeyTextBox", "Restart Time Trial:", "Restart Time Trial Keybind Information",
                "Restarts the time trial directly to the count down screen — this bypasses having to access it from the \"Restart Race\" button in the pause menu which can make resetting runs less tedious.\n\n" +
                "By default this command is not accessible, Mirror's Edge Tweaks performs a patch to make this function executable via keybinds/console commands.");

            ResetReactionTime = Make("ResetReactionTimeKeyTextBox", "Reset Reaction Time:", "Reset Reaction Time Keybind Information",
                "Restores reaction time without needing to build up the required momentum. Toggling this keybind while reaction time is active will immediately disengage it.");

            GodMode = Make("GodModeKeyTextBox", "God Mode:", "God Mode Keybind Information",
                "Toggles invincibility, as well as additional commands for disabling kill volumes that god mode by itself misses.");

            KillBots = Make("KillBotsKeyTextBox", "Kill Bots:", "Kill Bots Keybind Information",
                "Kills (deletes) all current bots and enemy helicopters.");

            ThirdPerson = Make("ThirdPersonKeyTextBox", "Third Person:", "Third Person Keybind Information",
                "Cycles through different third person camera perspectives. The 6th press will return you to normal first person view.");

            ToggleHUD = Make("ToggleHUDKeyTextBox", "Toggle HUD:", "Toggle HUD Keybind Information",
                "Toggles the visibility of the crosshair and timer/checkpoint elements.");

            FPSIndicator = Make("FPSIndicatorKeyTextBox", "FPS Indicator:", "FPS Indicator Keybind Information",
                "Toggles an overlay displaying the frames per second and other rendering statistics.");

            LevelStats = Make("LevelStatsKeyTextBox", "Level Stats:", "Level Stats Keybind Information",
                "Toggles an overlay displaying level streaming statistics, listing the levels for the current map. Red levels indicate the level is loaded and visible, " +
                "with the number of seconds next to the level name representing the time taken from load request to load finish. Green levels indicate unloaded levels.");

            TriggersVolumes = Make("TriggersVolumesKeyTextBox", "Triggers & Volumes:", "Triggers & Volumes Keybind Information",
                "Toggles the display of the bounding boxes of ALL triggers (checkpoints, level loads, other scripted gameplay events) and volumes " +
                "(areas that put Faith in a specific movement state, kill barriers, etc.).\n\nThis command also shows invisible blocking volumes the player can collide with, " +
                "making it a more performant alternative to using \"nxvis collision\" ('Show Collision' keybind).");

            ShowCollision = Make("ShowCollisionKeyTextBox", "Show Collision:", "Show Collision Keybind Information",
                "Note: This command is very performance intensive and in some cases can crash the game.\n\n" +
                "Toggles the display of the PhysX collision data for the level, allowing you to see the wireframes and volumes for ALL collision objects with which rigid bodies interact.");

            Noclip = Make("NoclipKeyTextBox", "Noclip:", "Noclip Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Toggles the use of noclip (flying with no collision). Keybinds for noclip movement speed can be set in the TweaksScriptsSettings file in the Binaries folder.");

            SaveState = Make("SaveStateKeyTextBox", "Save State:", "Save State Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Saves Faith's current position and state. If bots were manually spawned, their states will also be saved.");

            LoadSavedState = Make("LoadSavedStateKeyTextBox", "Load Saved State:", "Load Saved State Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Restores Faith to the saved state. This will also restore manually spawned bots.");

            SaveTimerLocation = Make("SaveTimerLocationKeyTextBox", "Save Timer Location:", "Save Timer Location Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Saves the current player location as the checkpoint for the timer in the trainer HUD.");

            DeleteViewedActor = Make("DeleteViewedActorKeyTextBox", "Delete Viewed Actor:", "Delete Viewed Actor Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and when the Cheats + Trainer mode is active.\n\n" +
                "Deletes the bot/object currently looked at (some objects are connected to essential world geometry and are excluded).");

            ScrollDownMacro = Make("ScrollDownMacroKeyTextBox", "Scroll Down Macro Key:", "Scroll Down Macro Key Information",
                "Set the keybind that will macro the action that is assigned to 'Scroll Down' in the game's control settings menu.\n\n" +
                "Note: This setting requires the Tweaks Scripts package to be installed. Macros are available while Softimer is active.");

            ScrollUpMacro = Make("ScrollUpMacroKeyTextBox", "Scroll Up Macro Key:", "Scroll Up Macro Key Information",
                "Set the keybind that will macro the action that is assigned to 'Scroll Up' in the game's control settings menu.\n\n" +
                "Note: This setting requires the Tweaks Scripts package to be installed. Macros are available while Softimer is active.");

            CustomKeybinds = new[]
            {
                RestartLevel, LoadLastCheckpoint, RestartTimeTrial, ResetReactionTime, GodMode,
                KillBots, ThirdPerson, ToggleHUD, FPSIndicator, LevelStats, TriggersVolumes, ShowCollision,
            };
            CheatTrainerKeybinds = new[]
            {
                Noclip, SaveState, LoadSavedState, SaveTimerLocation, DeleteViewedActor,
            };
            MacroKeybinds = new[]
            {
                ScrollDownMacro, ScrollUpMacro,
            };
        }

        private static string TdInputIniPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

        public void ShowInfo(KeybindEntryViewModel entry) =>
            _dialogService.ShowMessage(entry.InfoTitle, entry.InfoBody, DialogMessageType.Information);

        // ---- Capture + dispatch ----

        public async Task CaptureAsync(KeybindEntryViewModel entry)
        {
            if (_isLoading)
                return;

            var info = entry.Info;

            if (info.Type == KeybindType.Macro)
            {
                if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                    return;

                string settingsFilePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");
                if (!File.Exists(settingsFilePath))
                {
                    _dialogService.ShowMessage("Tweaks Scripts Not Installed",
                        "The TweaksScriptsSettings file was not found.\n\n" +
                        "Please install the Tweaks Scripts package from the Game Tweaks section before configuring macro keybinds.",
                        DialogMessageType.Warning);
                    return;
                }
            }

            var result = await _dialogService.ShowDialogAsync(new KeybindCaptureDialog(KeybindDefinitions.Ue3KeyMap));

            await Task.Yield();

            if (result is string ue3Key)
            {
                if (string.IsNullOrEmpty(ue3Key))
                {
                    entry.DisplayKey = string.Empty;
                    switch (info.Type)
                    {
                        case KeybindType.GodMode:
                            await RemoveGodModeKeybind();
                            break;
                        case KeybindType.TriggersVolumes:
                            await RemoveTriggersVolumesKeybind();
                            break;
                        case KeybindType.Macro:
                            await UpdateMacroKeybind(info.RemoveCommand, "");
                            break;
                        default:
                            await RemoveKeybind(info.RemoveCommand);
                            break;
                    }
                }
                else
                {
                    entry.DisplayKey = ue3Key;
                    switch (info.Type)
                    {
                        case KeybindType.GodMode:
                            await UpdateGodModeKeybind(entry, ue3Key);
                            break;
                        case KeybindType.TriggersVolumes:
                            await UpdateTriggersVolumesKeybind(entry, ue3Key);
                            break;
                        case KeybindType.Macro:
                            if (!await UpdateMacroKeybind(info.SetCommand, ue3Key))
                                entry.DisplayKey = string.Empty;
                            break;
                        default:
                            await UpdateKeybind(entry, info.SetCommand, ue3Key);
                            break;
                    }
                }
            }
        }

        // ---- Load / display ----

        public void LoadCustomKeybinds()
        {
            try
            {
                _isLoading = true;

                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                    return;

                string[] lines = File.ReadAllLines(tdInputPath);

                bool inPlayerInput = false;
                foreach (string line in lines)
                {
                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                        continue;
                    }

                    if (!inPlayerInput)
                        continue;

                    if (line.Contains("Command=\"RestartLevel\"")) SetFromBinding(line, RestartLevel);
                    else if (line.Contains("Command=\"RestartFromLastCheckpoint\"")) SetFromBinding(line, LoadLastCheckpoint);
                    else if (line.Contains("Command=\"TriggerRestartRaceblink\"")) SetFromBinding(line, RestartTimeTrial);
                    else if (line.Contains("Command=\"set TdPlayerController ReactionTimeEnergy 0")) SetFromBinding(line, ResetReactionTime);
                    else if (line.Contains("Command=\"EnableGodMode\"")) SetFromBinding(line, GodMode);
                    else if (line.Contains("Command=\"killbots\"")) SetFromBinding(line, KillBots);
                    else if (line.Contains("Command=\"FreeFlightCamera\"")) SetFromBinding(line, ThirdPerson);
                    else if (line.Contains("Command=\"Showhud\"")) SetFromBinding(line, ToggleHUD);
                    else if (line.Contains("Command=\"stat xunit\"")) SetFromBinding(line, FPSIndicator);
                    else if (line.Contains("Command=\"stat levels\"")) SetFromBinding(line, LevelStats);
                    else if (line.Contains("Command=\"ShowTriggersAndVolumes\"")) SetFromBinding(line, TriggersVolumes);
                    else if (line.Contains("Command=\"nxvis collision\"")) SetFromBinding(line, ShowCollision);
                    else if (line.Contains("Command=\"Noclip\"")) SetFromBinding(line, Noclip);
                    else if (line.Contains("Command=\"SaveLocation\"")) SetFromBinding(line, SaveState);
                    else if (line.Contains("Command=\"TpToSavedLocation")) SetFromBinding(line, LoadSavedState);
                    else if (line.Contains("Command=\"SaveTimerLocation\"")) SetFromBinding(line, SaveTimerLocation);
                    else if (line.Contains("Command=\"DestroyViewedActor\"")) SetFromBinding(line, DeleteViewedActor);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load custom keybinds: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private static void SetFromBinding(string line, KeybindEntryViewModel entry)
        {
            int nameStart = line.IndexOf("Name=\"") + 6;
            int nameEnd = line.IndexOf("\"", nameStart);
            if (nameStart > 5 && nameEnd > nameStart)
            {
                entry.DisplayKey = line.Substring(nameStart, nameEnd - nameStart);
            }
        }

        public void LoadMacroKeybinds()
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return;

            try
            {
                _isLoading = true;

                string settingsFilePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");

                if (!File.Exists(settingsFilePath))
                {
                    ScrollDownMacro.DisplayKey = string.Empty;
                    ScrollUpMacro.DisplayKey = string.Empty;
                    return;
                }

                var lines = File.ReadAllLines(settingsFilePath);

                foreach (var line in lines)
                {
                    if (line.StartsWith("ScrollDownMacroKey"))
                    {
                        var parts = line.Split(new[] { ' ' }, 2);
                        ScrollDownMacro.DisplayKey = parts.Length > 1 ? parts[1] : string.Empty;
                    }
                    else if (line.StartsWith("ScrollUpMacroKey"))
                    {
                        var parts = line.Split(new[] { ' ' }, 2);
                        ScrollUpMacro.DisplayKey = parts.Length > 1 ? parts[1] : string.Empty;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ---- Standard apply / remove ----

        private async Task UpdateKeybind(KeybindEntryViewModel entry, string command, string key)
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                {
                    _dialogService.ShowMessage("Error",
                        $"Cannot set keybind, 'TdInput.ini' file is missing from \"{tdInputPath}\".\n\n" +
                        "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
                        DialogMessageType.Error);
                    return;
                }

                string? conflictingCommand = null;

                await Task.Run(() =>
                {
                    string[] lines = File.ReadAllLines(tdInputPath);
                    bool foundSection = false;
                    bool foundBinding = false;
                    int mouseSmoothingIndex = -1;

                    foundSection = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Trim() == "[Engine.PlayerInput]")
                        {
                            foundSection = true;
                            continue;
                        }

                        if (foundSection && lines[i].Trim().StartsWith("[") && lines[i].Trim().EndsWith("]"))
                        {
                            break;
                        }

                        if (foundSection && lines[i].Contains("Bindings=") && lines[i].Contains($"Name=\"{key}\""))
                        {
                            int cmdStart = lines[i].IndexOf("Command=\"") + 9;
                            int cmdEnd = lines[i].IndexOf("\"", cmdStart);
                            if (cmdStart > 8 && cmdEnd > cmdStart)
                            {
                                string existingCommand = lines[i].Substring(cmdStart, cmdEnd - cmdStart);
                                if (existingCommand != command)
                                {
                                    conflictingCommand = existingCommand;
                                    return;
                                }
                            }
                        }
                    }

                    foundSection = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Trim() == "[Engine.PlayerInput]")
                        {
                            foundSection = true;
                        }

                        if (foundSection && lines[i].Contains("bEnableMouseSmoothing"))
                        {
                            mouseSmoothingIndex = i;
                        }

                        if (lines[i].Contains($"Command=\"{command}\""))
                        {
                            int nameStart = lines[i].IndexOf("Name=\"") + 6;
                            int nameEnd = lines[i].IndexOf("\"", nameStart);
                            if (nameStart > 5 && nameEnd > nameStart)
                            {
                                string beforeName = lines[i].Substring(0, nameStart);
                                string afterName = lines[i].Substring(nameEnd);
                                lines[i] = beforeName + key + afterName;
                            }
                            foundBinding = true;
                        }
                    }

                    if (!foundBinding && mouseSmoothingIndex >= 0)
                    {
                        var newLines = new List<string>(lines);
                        string newBinding = $"Bindings=(Name=\"{key}\",Command=\"{command}\",Control=False,Shift=False,Alt=False)";
                        newLines.Insert(mouseSmoothingIndex + 1, newBinding);
                        lines = newLines.ToArray();
                    }

                    FileAttributes attributes = File.GetAttributes(tdInputPath);
                    bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

                    if (wasReadOnly)
                    {
                        File.SetAttributes(tdInputPath, attributes & ~FileAttributes.ReadOnly);
                    }

                    File.WriteAllLines(tdInputPath, lines);

                    if (wasReadOnly)
                    {
                        File.SetAttributes(tdInputPath, attributes);
                    }
                });

                if (conflictingCommand != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                        "Please choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogMessageType.Warning);

                    entry.DisplayKey = string.Empty;
                }

                if (conflictingCommand == null)
                {
                    string? macroConflict = CheckMacroKeybindConflict(key);
                    if (macroConflict != null)
                    {
                        conflictingCommand = macroConflict;
                        await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                            $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                            "Please choose a different key.",
                            DialogMessageType.Warning);
                    }
                }

                if (conflictingCommand == null)
                {
                    // need to add exec flags to these functions, by default they are not executable via keybinds
                    if (command == "RestartFromLastCheckpoint")
                    {
                        await ExecFlagPatcher.AddExecFlag(_session.Config.GameDirectoryPath, "TdSPGame", "RestartFromLastCheckpoint");
                    }
                    else if (command == "TriggerRestartRaceblink")
                    {
                        await ExecFlagPatcher.AddExecFlag(_session.Config.GameDirectoryPath, "TdTimeTrialHUD", "TriggerRestartRaceblink");
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to update keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        private async Task RemoveKeybind(string command)
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                {
                    return;
                }

                await Task.Run(() =>
                {
                    string[] lines = File.ReadAllLines(tdInputPath);
                    List<string> newLines = new List<string>();

                    foreach (string line in lines)
                    {
                        if (!line.Contains($"Command=\"{command}\""))
                        {
                            newLines.Add(line);
                        }
                    }

                    if (newLines.Count < lines.Length)
                    {
                        FileAttributes attributes = File.GetAttributes(tdInputPath);
                        bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdInputPath, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.WriteAllLines(tdInputPath, newLines.ToArray());

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdInputPath, attributes);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        // ---- God Mode (3-binding toggle) ----

        private async Task UpdateGodModeKeybind(KeybindEntryViewModel entry, string key)
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                {
                    _dialogService.ShowMessage("File Not Found",
                        "TdInput.ini not found. Please launch Mirror's Edge at least once to create the configuration file.",
                        DialogMessageType.Error);
                    return;
                }

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                bool inPlayerInput = false;
                string? conflictingCommand = null;

                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                    }
                    else if (inPlayerInput && line.Contains($"Name=\"{key}\"") && line.Contains("Command="))
                    {
                        int commandStart = line.IndexOf("Command=\"") + 9;
                        int commandEnd = line.IndexOf("\"", commandStart);
                        if (commandStart > 8 && commandEnd > commandStart)
                        {
                            string existingCommand = line.Substring(commandStart, commandEnd - commandStart);

                            if (existingCommand != "EnableGodMode" &&
                                !existingCommand.Contains("bGodMode"))
                            {
                                conflictingCommand = existingCommand;
                                break;
                            }
                        }
                    }
                }

                if (conflictingCommand != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                        "Please choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogMessageType.Warning);
                    entry.DisplayKey = string.Empty;
                    return;
                }

                string? macroConflict = CheckMacroKeybindConflict(key);
                if (macroConflict != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Please choose a different key.",
                        DialogMessageType.Warning);
                    entry.DisplayKey = string.Empty;
                    return;
                }

                var modifiedLines = new List<string>();
                inPlayerInput = false;
                bool foundEnableGodMode = false;
                bool foundDisableGodMode = false;
                bool foundEnableGodModeCommand = false;
                int bEnableMouseSmoothingIndex = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                        modifiedLines.Add(line);
                    }
                    else if (inPlayerInput && line.Contains("Command=\"EnableGodMode\"") && !line.Contains("Name=\"EnableGodMode\""))
                    {
                        if (!foundEnableGodMode)
                        {
                            modifiedLines.Add($"Bindings=(Name=\"{key}\",Command=\"EnableGodMode\",Control=False,Shift=False,Alt=False)");
                            foundEnableGodMode = true;
                        }
                    }
                    else if (inPlayerInput && line.Contains("Name=\"EnableGodMode\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"EnableGodMode\",Command=\"set TdPlayerController bGodMode 1 | set TdKillVolume CollisionType COLLIDE_NoCollision | set TdKillZoneVolume CollisionType COLLIDE_NoCollision | set TdKillZoneKiller CollisionType COLLIDE_NoCollision | set TdFallHeightVolume CollisionType COLLIDE_NoCollision | set TdBarbedWireVolume CollisionType COLLIDE_NoCollision | SetBind {key} DisableGodMode\",Control=False,Shift=False,Alt=False)");
                        foundEnableGodModeCommand = true;
                    }
                    else if (inPlayerInput && line.Contains("Name=\"DisableGodMode\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"DisableGodMode\",Command=\"set TdPlayerController bGodMode 0 | set TdKillVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneKiller CollisionType COLLIDE_CustomDefault | set TdFallHeightVolume CollisionType COLLIDE_CustomDefault | set TdBarbedWireVolume CollisionType COLLIDE_CustomDefault | SetBind {key} EnableGodMode\",Control=False,Shift=False,Alt=False)");
                        foundDisableGodMode = true;
                    }
                    else
                    {
                        if (inPlayerInput && line.Contains("bEnableMouseSmoothing"))
                        {
                            bEnableMouseSmoothingIndex = modifiedLines.Count;
                        }
                        modifiedLines.Add(line);
                    }
                }

                if (!foundEnableGodMode || !foundEnableGodModeCommand || !foundDisableGodMode)
                {
                    if (bEnableMouseSmoothingIndex >= 0)
                    {
                        if (!foundEnableGodMode)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"{key}\",Command=\"EnableGodMode\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundEnableGodModeCommand)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"EnableGodMode\",Command=\"set TdPlayerController bGodMode 1 | set TdKillVolume CollisionType COLLIDE_NoCollision | set TdKillZoneVolume CollisionType COLLIDE_NoCollision | set TdKillZoneKiller CollisionType COLLIDE_NoCollision | set TdFallHeightVolume CollisionType COLLIDE_NoCollision | set TdBarbedWireVolume CollisionType COLLIDE_NoCollision | SetBind {key} DisableGodMode\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundDisableGodMode)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"DisableGodMode\",Command=\"set TdPlayerController bGodMode 0 | set TdKillVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneKiller CollisionType COLLIDE_CustomDefault | set TdFallHeightVolume CollisionType COLLIDE_CustomDefault | set TdBarbedWireVolume CollisionType COLLIDE_CustomDefault | SetBind {key} EnableGodMode\",Control=False,Shift=False,Alt=False)");
                        }
                    }
                }

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to update God Mode keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        private async Task RemoveGodModeKeybind()
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                    return;

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                var modifiedLines = lines.Where(line =>
                    !line.Contains("Command=\"EnableGodMode\"") &&
                    !line.Contains("Name=\"EnableGodMode\"") &&
                    !line.Contains("Name=\"DisableGodMode\"")).ToList();

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove God Mode keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        // ---- Triggers & Volumes (3-binding toggle) ----

        private async Task UpdateTriggersVolumesKeybind(KeybindEntryViewModel entry, string key)
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                {
                    _dialogService.ShowMessage("File Not Found",
                        "TdInput.ini not found. Please launch Mirror's Edge at least once to create the configuration file.",
                        DialogMessageType.Error);
                    return;
                }

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                bool inPlayerInput = false;
                string? conflictingCommand = null;

                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                    }
                    else if (inPlayerInput && line.Contains($"Name=\"{key}\"") && line.Contains("Command="))
                    {
                        int commandStart = line.IndexOf("Command=\"") + 9;
                        int commandEnd = line.IndexOf("\"", commandStart);
                        if (commandStart > 8 && commandEnd > commandStart)
                        {
                            string existingCommand = line.Substring(commandStart, commandEnd - commandStart);

                            if (existingCommand != "ShowTriggersAndVolumes" &&
                                !existingCommand.Contains("show collision"))
                            {
                                conflictingCommand = existingCommand;
                                break;
                            }
                        }
                    }
                }

                if (conflictingCommand != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                        "Please choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogMessageType.Warning);
                    entry.DisplayKey = string.Empty;
                    return;
                }

                string? macroConflict = CheckMacroKeybindConflict(key);
                if (macroConflict != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Please choose a different key.",
                        DialogMessageType.Warning);
                    entry.DisplayKey = string.Empty;
                    return;
                }

                string showCommand = "show collision | set Trigger bHidden 0 | set TriggerVolume bHidden 0 | set BlockingVolume bHidden 0 | set TdAIBlockingVolume bHidden 0 | set TdAIKeepMovingVolume bHidden 0 | set TdAIPawnBlockingVolume bHidden 0 | set TdBalanceWalkVolume bHidden 0 | set TdBarbedWireVolume bHidden 0 | set TdCheckpointVolume bHidden 0 | set TdConfinedVolumePathNode bHidden 0 | set TdCoverGroupVolume bHidden 0 | set TdFallHeightVolume bHidden 0 | set TdKillVolume bHidden 0 | set TdKillZoneVolume bHidden 0 | set TdLadderVolume bHidden 0 | set TdLedgeWalkVolume bHidden 0 | set TdMovementExclusion bHidden 0 | set TdMovementVolume bHidden 0 | set TdMoveVolumeRenderComponent bHidden 0 | set TdPathLimitsVolume bHidden 0 | set TdSwingVolume bHidden 0 | set TdTriggerVolume bHidden 0 | set TdZiplineVolume bHidden 0 | SetBind " + key + " HideTriggersAndVolumes";
                string hideCommand = "show collision | set Trigger bHidden 1 | set TriggerVolume bHidden 1 | set BlockingVolume bHidden 1 | set TdAIBlockingVolume bHidden 1 | set TdAIKeepMovingVolume bHidden 1 | set TdAIPawnBlockingVolume bHidden 1 | set TdBalanceWalkVolume bHidden 1 | set TdBarbedWireVolume bHidden 1 | set TdCheckpointVolume bHidden 1 | set TdConfinedVolumePathNode bHidden 1 | set TdCoverGroupVolume bHidden 1 | set TdFallHeightVolume bHidden 1 | set TdKillVolume bHidden 1 | set TdKillZoneVolume bHidden 1 | set TdLadderVolume bHidden 1 | set TdLedgeWalkVolume bHidden 1 | set TdMovementExclusion bHidden 1 | set TdMovementVolume bHidden 1 | set TdMoveVolumeRenderComponent bHidden 1 | set TdPathLimitsVolume bHidden 1 | set TdSwingVolume bHidden 1 | set TdTriggerVolume bHidden 1 | set TdZiplineVolume bHidden 1 | SetBind " + key + " ShowTriggersAndVolumes";

                var modifiedLines = new List<string>();
                inPlayerInput = false;
                bool foundShowTriggersAndVolumes = false;
                bool foundShowCommand = false;
                bool foundHideCommand = false;
                int bEnableMouseSmoothingIndex = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    if (line.Trim().StartsWith("["))
                    {
                        inPlayerInput = line.Trim() == "[Engine.PlayerInput]";
                        modifiedLines.Add(line);
                    }
                    else if (inPlayerInput && line.Contains("Command=\"ShowTriggersAndVolumes\"") && !line.Contains("Name=\"ShowTriggersAndVolumes\""))
                    {
                        if (!foundShowTriggersAndVolumes)
                        {
                            modifiedLines.Add($"Bindings=(Name=\"{key}\",Command=\"ShowTriggersAndVolumes\",Control=False,Shift=False,Alt=False)");
                            foundShowTriggersAndVolumes = true;
                        }
                    }
                    else if (inPlayerInput && line.Contains("Name=\"ShowTriggersAndVolumes\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"ShowTriggersAndVolumes\",Command=\"{showCommand}\",Control=False,Shift=False,Alt=False)");
                        foundShowCommand = true;
                    }
                    else if (inPlayerInput && line.Contains("Name=\"HideTriggersAndVolumes\""))
                    {
                        modifiedLines.Add($"Bindings=(Name=\"HideTriggersAndVolumes\",Command=\"{hideCommand}\",Control=False,Shift=False,Alt=False)");
                        foundHideCommand = true;
                    }
                    else
                    {
                        if (inPlayerInput && line.Contains("bEnableMouseSmoothing"))
                        {
                            bEnableMouseSmoothingIndex = modifiedLines.Count;
                        }
                        modifiedLines.Add(line);
                    }
                }

                if (!foundShowTriggersAndVolumes || !foundShowCommand || !foundHideCommand)
                {
                    if (bEnableMouseSmoothingIndex >= 0)
                    {
                        if (!foundShowTriggersAndVolumes)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"{key}\",Command=\"ShowTriggersAndVolumes\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundShowCommand)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"ShowTriggersAndVolumes\",Command=\"{showCommand}\",Control=False,Shift=False,Alt=False)");
                            bEnableMouseSmoothingIndex++;
                        }
                        if (!foundHideCommand)
                        {
                            modifiedLines.Insert(bEnableMouseSmoothingIndex + 1, $"Bindings=(Name=\"HideTriggersAndVolumes\",Command=\"{hideCommand}\",Control=False,Shift=False,Alt=False)");
                        }
                    }
                }

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to update Triggers & Volumes keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        private async Task RemoveTriggersVolumesKeybind()
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                    return;

                var lines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                var modifiedLines = lines.Where(line =>
                    !line.Contains("Command=\"ShowTriggersAndVolumes\"") &&
                    !line.Contains("Name=\"ShowTriggersAndVolumes\"") &&
                    !line.Contains("Name=\"HideTriggersAndVolumes\"")).ToList();

                var fileInfo = new FileInfo(tdInputPath);
                bool wasReadOnly = fileInfo.IsReadOnly;
                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = false;
                }

                await Task.Run(() => File.WriteAllLines(tdInputPath, modifiedLines));

                if (wasReadOnly)
                {
                    fileInfo.IsReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove Triggers & Volumes keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        // ---- Macro keybinds (TweaksScriptsSettings file) ----

        private string? CheckMacroKeybindConflict(string ue3Key, string? excludeSetting = null)
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return null;

            string settingsFilePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");
            if (!File.Exists(settingsFilePath))
                return null;

            var macroSettings = new Dictionary<string, string>
            {
                ["ScrollDownMacroKey"] = "Scroll Down Macro Key",
                ["ScrollUpMacroKey"] = "Scroll Up Macro Key"
            };

            foreach (var line in File.ReadAllLines(settingsFilePath))
            {
                foreach (var kvp in macroSettings)
                {
                    if (kvp.Key == excludeSetting)
                        continue;

                    if (line.StartsWith(kvp.Key))
                    {
                        var parts = line.Split(new[] { ' ' }, 2);
                        if (parts.Length > 1 && parts[1] == ue3Key)
                            return kvp.Value;
                    }
                }
            }

            return null;
        }

        private async Task<bool> UpdateMacroKeybind(string settingName, string ue3Key)
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return false;

            if (!string.IsNullOrEmpty(ue3Key))
            {
                string tdInputPath = TdInputIniPath;

                if (File.Exists(tdInputPath))
                {
                    string? conflictingCommand = null;

                    await Task.Run(() =>
                    {
                        string[] iniLines = File.ReadAllLines(tdInputPath);
                        bool inPlayerInput = false;

                        for (int i = 0; i < iniLines.Length; i++)
                        {
                            if (iniLines[i].Trim() == "[Engine.PlayerInput]")
                            {
                                inPlayerInput = true;
                                continue;
                            }

                            if (inPlayerInput && iniLines[i].Trim().StartsWith("[") && iniLines[i].Trim().EndsWith("]"))
                                break;

                            if (inPlayerInput && iniLines[i].Contains("Bindings=") && iniLines[i].Contains($"Name=\"{ue3Key}\""))
                            {
                                int cmdStart = iniLines[i].IndexOf("Command=\"") + 9;
                                int cmdEnd = iniLines[i].IndexOf("\"", cmdStart);
                                if (cmdStart > 8 && cmdEnd > cmdStart)
                                {
                                    conflictingCommand = iniLines[i].Substring(cmdStart, cmdEnd - cmdStart);
                                    return;
                                }
                            }
                        }
                    });

                    if (conflictingCommand != null)
                    {
                        await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                            $"The key '{ue3Key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                            "Please choose a different key or remove the existing binding in TdInput.ini first.",
                            DialogMessageType.Warning);
                        return false;
                    }
                }

                string? macroConflict = CheckMacroKeybindConflict(ue3Key, excludeSetting: settingName);
                if (macroConflict != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{ue3Key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Please choose a different key.",
                        DialogMessageType.Warning);
                    return false;
                }
            }

            try
            {
                string settingsFilePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");

                var lines = File.ReadAllLines(settingsFilePath).ToList();
                bool found = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith(settingName))
                    {
                        lines[i] = string.IsNullOrEmpty(ue3Key) ? settingName : $"{settingName} {ue3Key}";
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    lines.Add(string.IsNullOrEmpty(ue3Key) ? settingName : $"{settingName} {ue3Key}");
                }

                File.WriteAllLines(settingsFilePath, lines);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to update macro keybind: {ex.Message}", DialogMessageType.Error);
                return false;
            }

            return true;
        }
    }
}

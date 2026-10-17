using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
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

    public partial class KeybindsViewModel : ObservableObject
    {
        private const string PlayerInputSection = "[Engine.PlayerInput]";

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
        public KeybindEntryViewModel SoftimerMenu { get; }
        public KeybindEntryViewModel ScrollDownMacro { get; }
        public KeybindEntryViewModel ScrollUpMacro { get; }

        public IReadOnlyList<KeybindEntryViewModel> CustomKeybinds { get; }
        public IReadOnlyList<KeybindEntryViewModel> CheatTrainerKeybinds { get; }
        public IReadOnlyList<KeybindEntryViewModel> SoftimerKeybinds { get; }

        public KeybindsViewModel(IDialogService dialogService, GameSession session)
        {
            _dialogService = dialogService;
            _session = session;

            KeybindEntryViewModel Make(string textBoxName, string label, string title, string body) =>
                new KeybindEntryViewModel(this, label, KeybindDefinitions.KeybindMap[textBoxName], title, body);

            RestartLevel = Make("RestartLevelKeyTextBox", "Restart Level:", "Restart Level Keybind Information",
                "Restarts the level from where you started. This does not respect checkpoints reached; refer to the \"Load last checkpoint\" keybind for that.\n\n" +
                "In time trial and speedrun modes, this reloads the level back to the start.\n\n" +
                "In chapter mode, this reloads the level back to the checkpoint selected in the main menu.\n\n" +
                "In story mode, this reloads the level back to where you started when you pressed \"Continue Game\" (except when you complete a chapter, in which case you respawn at checkpoint A of the next chapter).");

            LoadLastCheckpoint = Make("LoadLastCheckpointKeyTextBox", "Load Last Checkpoint:", "Load Last Checkpoint Keybind Information",
                "Earlier dev and review builds of Mirror's Edge had a dedicated \"Load last checkpoint\" button in the pause menu that reloaded Faith " +
                "to the last hard or soft checkpoint reached. The UI was removed before release, but the underlying function still exists in retail builds.\n\n" +
                "Mirror's Edge Tweaks patches it to be executable via keybinds or console commands — a faster way to reset without forcing a death.");

            RestartTimeTrial = Make("RestartTimeTrialKeyTextBox", "Restart Time Trial:", "Restart Time Trial Keybind Information",
                "Restarts the time trial directly to the countdown screen, bypassing the \"Restart Race\" button in the pause menu.\n\n" +
                "By default this command is not accessible. Mirror's Edge Tweaks applies a patch to make it executable via keybinds or console commands.");

            ResetReactionTime = Make("ResetReactionTimeKeyTextBox", "Reset Reaction Time:", "Reset Reaction Time Keybind Information",
                "Restores reaction time without needing to build up the required momentum. Toggling this keybind while reaction time is active will immediately disengage it.");

            GodMode = Make("GodModeKeyTextBox", "God Mode:", "God Mode Keybind Information",
                "Toggles invincibility, as well as additional commands for disabling kill volumes that god mode by itself does not cover.");

            KillBots = Make("KillBotsKeyTextBox", "Kill Bots:", "Kill Bots Keybind Information",
                "Kills (deletes) all current bots and enemy helicopters.");

            ThirdPerson = Make("ThirdPersonKeyTextBox", "Third Person:", "Third Person Keybind Information",
                "Cycles through different third-person camera perspectives. The sixth press returns you to normal first-person view.");

            ToggleHUD = Make("ToggleHUDKeyTextBox", "Toggle HUD:", "Toggle HUD Keybind Information",
                "Toggles the visibility of the crosshair and timer/checkpoint elements.");

            FPSIndicator = Make("FPSIndicatorKeyTextBox", "FPS Indicator:", "FPS Indicator Keybind Information",
                "Toggles an overlay displaying frames per second and other rendering statistics.");

            LevelStats = Make("LevelStatsKeyTextBox", "Level Stats:", "Level Stats Keybind Information",
                "Toggles an overlay displaying level streaming statistics for the current map. Red levels indicate the level is loaded and visible; " +
                "the number of seconds beside each level name is the time from load request to load finish. Green levels indicate unloaded levels.");

            TriggersVolumes = Make("TriggersVolumesKeyTextBox", "Triggers & Volumes:", "Triggers & Volumes Keybind Information",
                "Toggles the bounding boxes of triggers (checkpoints, level loads, other scripted gameplay events) and volumes " +
                "(areas that put Faith in a specific movement state, kill barriers, etc.).\n\n" +
                "This command also shows invisible blocking volumes the player can collide with, " +
                "making it a more performant alternative to \"nxvis collision\" (Show Collision keybind).");

            ShowCollision = Make("ShowCollisionKeyTextBox", "Show Collision:", "Show Collision Keybind Information",
                "Note: This command is very performance intensive and can crash the game in some cases.\n\n" +
                "Toggles the display of PhysX collision data for the level — wireframes and volumes for all collision objects with which rigid bodies interact.");

            Noclip = Make("NoclipKeyTextBox", "Noclip:", "Noclip Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and Cheats + Trainer mode is active.\n\n" +
                "Toggles noclip (flying with no collision). Noclip movement speed keybinds can be set in TweaksScriptsSettings in the Binaries folder.");

            SaveState = Make("SaveStateKeyTextBox", "Save State:", "Save State Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and Cheats + Trainer mode is active.\n\n" +
                "Saves Faith's current position and state. If bots were manually spawned, their states are also saved.");

            LoadSavedState = Make("LoadSavedStateKeyTextBox", "Load Saved State:", "Load Saved State Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and Cheats + Trainer mode is active.\n\n" +
                "Restores Faith to the saved state. Manually spawned bots are also restored.");

            SaveTimerLocation = Make("SaveTimerLocationKeyTextBox", "Save Timer Location:", "Save Timer Location Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and Cheats + Trainer mode is active.\n\n" +
                "Saves the current player location as the checkpoint for the timer in the trainer HUD.");

            DeleteViewedActor = Make("DeleteViewedActorKeyTextBox", "Delete Viewed Actor:", "Delete Viewed Actor Keybind Information",
                "Note: This cheat only works if the Tweaks Scripts package is installed and Cheats + Trainer mode is active.\n\n" +
                "Deletes the bot or object currently in view. Some objects connected to essential world geometry are excluded.");

            SoftimerMenu = Make("SoftimerMenuKeyTextBox", "Softimer Menu:", "Softimer Menu Keybind Information",
                "Opens the Softimer Menu, which provides various speedrunning-related features, including quick chapter + checkpoint launching and gameplay modifiers.\n\n" +
                "Note: Requires the Tweaks Scripts package. The Softimer Menu is available while Softimer is active and when the player is in a level.");

            ScrollDownMacro = Make("ScrollDownMacroKeyTextBox", "Scroll Down Macro Key:", "Scroll Down Macro Key Information",
                "Sets the keybind that macros the action assigned to 'Scroll Down' in the game's control settings.\n\n" +
                "Note: Requires the Tweaks Scripts package. Macros are available while Softimer is active.");

            ScrollUpMacro = Make("ScrollUpMacroKeyTextBox", "Scroll Up Macro Key:", "Scroll Up Macro Key Information",
                "Sets the keybind that macros the action assigned to 'Scroll Up' in the game's control settings.\n\n" +
                "Note: Requires the Tweaks Scripts package. Macros are available while Softimer is active.");

            CustomKeybinds = new[]
            {
                RestartLevel, LoadLastCheckpoint, RestartTimeTrial, ResetReactionTime, GodMode,
                KillBots, ThirdPerson, ToggleHUD, FPSIndicator, LevelStats, TriggersVolumes, ShowCollision,
            };
            CheatTrainerKeybinds = new[]
            {
                Noclip, SaveState, LoadSavedState, SaveTimerLocation, DeleteViewedActor,
            };
            SoftimerKeybinds = new[]
            {
                SoftimerMenu, ScrollDownMacro, ScrollUpMacro,
            };
        }

        private string TdInputIniPath =>
            UserTdGamePathHelper.GetTdInputIniPath(_session.Config);

        public void ShowInfo(KeybindEntryViewModel entry) =>
            _dialogService.ShowMessage(entry.InfoTitle, entry.InfoBody, DialogMessageType.Information);

        public async Task CaptureAsync(KeybindEntryViewModel entry)
        {
            if (_isLoading || _session.IsProcessingGameDirectory)
                return;

            if (entry.Info.Type == KeybindType.Macro)
            {
                if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                    return;

                string settingsFilePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");
                if (!File.Exists(settingsFilePath))
                {
                    _dialogService.ShowMessage("Tweaks Scripts Not Installed",
                        "TweaksScriptsSettings was not found.\n\n" +
                        "Install the Tweaks Scripts package from Game Tweaks before configuring macro keybinds.",
                        DialogMessageType.Warning);
                    return;
                }
            }

            var result = await _dialogService.ShowDialogAsync(new KeybindCaptureDialog(KeybindDefinitions.Ue3KeyMap));

            await Task.Yield();

            if (result is string ue3Key)
                await ApplyKeyAsync(entry, ue3Key);
        }

        // DisplayKey is updated only after the change is saved, so it never shows a rejected key.
        internal Task ApplyKeyAsync(KeybindEntryViewModel entry, string ue3Key) =>
            _session.ApplyGate.RunAsync(async () =>
            {
                var info = entry.Info;
                bool saved;

                if (string.IsNullOrEmpty(ue3Key))
                {
                    saved = info.Type switch
                    {
                        KeybindType.GodMode => await RemoveToggleKeybind(GodModeToggle),
                        KeybindType.TriggersVolumes => await RemoveToggleKeybind(TriggersVolumesToggle),
                        KeybindType.Macro => await UpdateMacroKeybind(info.RemoveCommand, ""),
                        _ => await RemoveKeybind(info.RemoveCommand),
                    };
                }
                else
                {
                    saved = info.Type switch
                    {
                        KeybindType.GodMode => await UpdateToggleKeybind(GodModeToggle, ue3Key),
                        KeybindType.TriggersVolumes => await UpdateToggleKeybind(TriggersVolumesToggle, ue3Key),
                        KeybindType.Macro => await UpdateMacroKeybind(info.SetCommand, ue3Key),
                        _ => await UpdateKeybind(info.SetCommand, ue3Key),
                    };
                }

                if (saved)
                    entry.DisplayKey = ue3Key;
            });


        public void LoadCustomKeybinds()
        {
            try
            {
                _isLoading = true;

                foreach (var entry in CustomKeybinds.Concat(CheatTrainerKeybinds).Concat(SoftimerKeybinds))
                {
                    if (entry.Info.Type != KeybindType.Macro)
                        entry.DisplayKey = string.Empty;
                }

                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                    return;

                string[] lines = File.ReadAllLines(tdInputPath);

                foreach (int i in PlayerInputLineIndexes(lines))
                {
                    string line = lines[i];

                    if (line.Contains("Command=\"RestartLevel\"")) SetFromBinding(line, RestartLevel);
                    else if (line.Contains("Command=\"RestartFromLastCheckpoint\"")) SetFromBinding(line, LoadLastCheckpoint);
                    else if (line.Contains("Command=\"TriggerRestartRaceblink\"")) SetFromBinding(line, RestartTimeTrial);
                    else if (line.Contains("Command=\"set TdPlayerController ReactionTimeEnergy 0")) SetFromBinding(line, ResetReactionTime);
                    else if (GodModeToggle.IsKeyLine(line)) SetFromBinding(line, GodMode);
                    else if (line.Contains("Command=\"killbots\"")) SetFromBinding(line, KillBots);
                    else if (line.Contains("Command=\"FreeFlightCamera\"")) SetFromBinding(line, ThirdPerson);
                    else if (line.Contains("Command=\"Showhud\"")) SetFromBinding(line, ToggleHUD);
                    else if (line.Contains("Command=\"stat xunit\"")) SetFromBinding(line, FPSIndicator);
                    else if (line.Contains("Command=\"stat levels\"")) SetFromBinding(line, LevelStats);
                    else if (TriggersVolumesToggle.IsKeyLine(line)) SetFromBinding(line, TriggersVolumes);
                    else if (line.Contains("Command=\"nxvis collision\"")) SetFromBinding(line, ShowCollision);
                    else if (line.Contains("Command=\"Noclip\"")) SetFromBinding(line, Noclip);
                    else if (line.Contains("Command=\"SaveLocation\"")) SetFromBinding(line, SaveState);
                    else if (line.Contains("Command=\"TpToSavedLocation")) SetFromBinding(line, LoadSavedState);
                    else if (line.Contains("Command=\"SaveTimerLocation\"")) SetFromBinding(line, SaveTimerLocation);
                    else if (line.Contains("Command=\"DestroyViewedActor\"")) SetFromBinding(line, DeleteViewedActor);
                    else if (line.Contains("Command=\"SofTimerMenu\"")) SetFromBinding(line, SoftimerMenu);
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
            if (BindingName(line) is string name)
                entry.DisplayKey = name;
        }

        public void LoadMacroKeybinds()
        {
            ScrollDownMacro.DisplayKey = string.Empty;
            ScrollUpMacro.DisplayKey = string.Empty;

            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return;

            try
            {
                _isLoading = true;

                string settingsFilePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "TweaksScriptsSettings");

                if (!File.Exists(settingsFilePath))
                    return;

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

        private async Task<bool> UpdateKeybind(string command, string key)
        {
            bool written = await WriteKeybind(key, new[] { command }, lines =>
            {
                bool found = false;
                foreach (int i in PlayerInputLineIndexes(lines))
                {
                    if (BindingCommand(lines[i]) == command && BindingName(lines[i]) != null)
                    {
                        lines[i] = WithBindingName(lines[i], key);
                        found = true;
                    }
                }

                return found ? new List<string>() : new List<string> { Binding(key, command) };
            });

            if (!written)
                return false;

            try
            {
                // These functions are not executable via keybinds until their exec flag is added.
                if (command == "RestartFromLastCheckpoint")
                {
                    await ExecFlagPatcher.AddExecFlag(_session.Config.GameDirectoryPath, "TdSPGame", "RestartFromLastCheckpoint");
                }
                else if (command == "TriggerRestartRaceblink")
                {
                    await ExecFlagPatcher.AddExecFlag(_session.Config.GameDirectoryPath, "TdTimeTrialHUD", "TriggerRestartRaceblink");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to update keybind:\n\n{ex.Message}", DialogMessageType.Error);
            }

            return true;
        }

        // Also matches chains that start with command, such as those with an OnRelease part.
        private Task<bool> RemoveKeybind(string command) =>
            RemoveBindings(line => BindingCommand(line) is string bound &&
                (bound == command || bound.StartsWith(command + " |", StringComparison.Ordinal)));

        // The keybind's key runs one of two aliases, and each alias rebinds the key to the other.
        private sealed record ToggleBinding(string EnableName, string DisableName, string EnableCommand, string DisableCommand)
        {
            // The game saves SetBind changes to TdInput.ini, so the key's line can name either alias.
            public bool IsKeyLine(string line) =>
                BindingCommand(line) is string command && (command == EnableName || command == DisableName);

            public bool Owns(string line) =>
                IsKeyLine(line) || (BindingName(line) is string name && (name == EnableName || name == DisableName));
        }

        private static readonly ToggleBinding GodModeToggle = new("EnableGodMode", "DisableGodMode",
            "set TdPlayerController bGodMode 1 | set TdKillVolume CollisionType COLLIDE_NoCollision | set TdKillZoneVolume CollisionType COLLIDE_NoCollision | set TdKillZoneKiller CollisionType COLLIDE_NoCollision | set TdFallHeightVolume CollisionType COLLIDE_NoCollision | set TdBarbedWireVolume CollisionType COLLIDE_NoCollision",
            "set TdPlayerController bGodMode 0 | set TdKillVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneVolume CollisionType COLLIDE_CustomDefault | set TdKillZoneKiller CollisionType COLLIDE_CustomDefault | set TdFallHeightVolume CollisionType COLLIDE_CustomDefault | set TdBarbedWireVolume CollisionType COLLIDE_CustomDefault");

        private static readonly ToggleBinding TriggersVolumesToggle = new("ShowTriggersAndVolumes", "HideTriggersAndVolumes",
            "show collision | set Trigger bHidden 0 | set TriggerVolume bHidden 0 | set BlockingVolume bHidden 0 | set TdAIBlockingVolume bHidden 0 | set TdAIKeepMovingVolume bHidden 0 | set TdAIPawnBlockingVolume bHidden 0 | set TdBalanceWalkVolume bHidden 0 | set TdBarbedWireVolume bHidden 0 | set TdCheckpointVolume bHidden 0 | set TdConfinedVolumePathNode bHidden 0 | set TdCoverGroupVolume bHidden 0 | set TdFallHeightVolume bHidden 0 | set TdKillVolume bHidden 0 | set TdKillZoneVolume bHidden 0 | set TdLadderVolume bHidden 0 | set TdLedgeWalkVolume bHidden 0 | set TdMovementExclusion bHidden 0 | set TdMovementVolume bHidden 0 | set TdMoveVolumeRenderComponent bHidden 0 | set TdPathLimitsVolume bHidden 0 | set TdSwingVolume bHidden 0 | set TdTriggerVolume bHidden 0 | set TdZiplineVolume bHidden 0",
            "show collision | set Trigger bHidden 1 | set TriggerVolume bHidden 1 | set BlockingVolume bHidden 1 | set TdAIBlockingVolume bHidden 1 | set TdAIKeepMovingVolume bHidden 1 | set TdAIPawnBlockingVolume bHidden 1 | set TdBalanceWalkVolume bHidden 1 | set TdBarbedWireVolume bHidden 1 | set TdCheckpointVolume bHidden 1 | set TdConfinedVolumePathNode bHidden 1 | set TdCoverGroupVolume bHidden 1 | set TdFallHeightVolume bHidden 1 | set TdKillVolume bHidden 1 | set TdKillZoneVolume bHidden 1 | set TdLadderVolume bHidden 1 | set TdLedgeWalkVolume bHidden 1 | set TdMovementExclusion bHidden 1 | set TdMovementVolume bHidden 1 | set TdMoveVolumeRenderComponent bHidden 1 | set TdPathLimitsVolume bHidden 1 | set TdSwingVolume bHidden 1 | set TdTriggerVolume bHidden 1 | set TdZiplineVolume bHidden 1");

        private Task<bool> UpdateToggleKeybind(ToggleBinding toggle, string key) =>
            WriteKeybind(key, new[] { toggle.EnableName, toggle.DisableName }, lines =>
            {
                string keyLine = Binding(key, toggle.EnableName);
                string enableLine = Binding(toggle.EnableName, $"{toggle.EnableCommand} | SetBind {key} {toggle.DisableName}");
                string disableLine = Binding(toggle.DisableName, $"{toggle.DisableCommand} | SetBind {key} {toggle.EnableName}");
                var pending = new List<string> { keyLine, enableLine, disableLine };

                // The first copy of each line is rewritten in place, and later copies are dropped.
                var duplicates = new List<int>();
                foreach (int i in PlayerInputLineIndexes(lines))
                {
                    string? replacement =
                        toggle.IsKeyLine(lines[i]) ? keyLine :
                        BindingName(lines[i]) == toggle.EnableName ? enableLine :
                        BindingName(lines[i]) == toggle.DisableName ? disableLine :
                        null;

                    if (replacement == null)
                        continue;

                    if (pending.Remove(replacement))
                        lines[i] = replacement;
                    else
                        duplicates.Add(i);
                }
                for (int d = duplicates.Count - 1; d >= 0; d--)
                    lines.RemoveAt(duplicates[d]);

                return pending;
            });

        private Task<bool> RemoveToggleKeybind(ToggleBinding toggle) => RemoveBindings(toggle.Owns);


        // Rejects key with a warning when a macro or a command outside ownCommands already uses it.
        // Otherwise, updateBindings rewrites the keybind's lines and returns the ones still to add.
        // Returns whether TdInput.ini was written.
        private async Task<bool> WriteKeybind(string key, string[] ownCommands, Func<List<string>, List<string>> updateBindings)
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                {
                    _dialogService.ShowMessage("Error",
                        $"Cannot set keybind, 'TdInput.ini' file is missing from \"{tdInputPath}\".\n\n" +
                        "Launch Mirror's Edge at least once to create the configuration file.",
                        DialogMessageType.Error);
                    return false;
                }

                string? macroConflict = CheckMacroKeybindConflict(key);
                if (macroConflict != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Choose a different key.",
                        DialogMessageType.Warning);
                    return false;
                }

                var lines = new List<string>(await Task.Run(() => File.ReadAllLines(tdInputPath)));

                string? conflictingCommand = FindConflictingCommand(lines, key, ownCommands);
                if (conflictingCommand != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{key}' is already bound to '{conflictingCommand}'.\n\n" +
                        "Choose a different key or remove the existing binding in TdInput.ini first.",
                        DialogMessageType.Warning);
                    return false;
                }

                List<string> missingLines = updateBindings(lines);
                if (missingLines.Count > 0)
                {
                    int insertIndex = FindBindingInsertIndex(lines);
                    if (insertIndex < 0)
                    {
                        _dialogService.ShowMessage("Error",
                            $"Cannot set keybind because 'TdInput.ini' has no {PlayerInputSection} section.",
                            DialogMessageType.Error);
                        return false;
                    }

                    lines.InsertRange(insertIndex, missingLines);
                }

                await Task.Run(() => WritePreservingReadOnly(tdInputPath, lines));
                return true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to update keybind:\n\n{ex.Message}", DialogMessageType.Error);
                return false;
            }
        }

        // Returns whether the bindings are gone, which includes when TdInput.ini does not exist.
        private async Task<bool> RemoveBindings(Func<string, bool> isOwned)
        {
            try
            {
                string tdInputPath = TdInputIniPath;

                if (!File.Exists(tdInputPath))
                    return true;

                await Task.Run(() =>
                {
                    string[] lines = File.ReadAllLines(tdInputPath);
                    var owned = PlayerInputLineIndexes(lines).Where(i => isOwned(lines[i])).ToHashSet();
                    if (owned.Count > 0)
                        WritePreservingReadOnly(tdInputPath, lines.Where((_, i) => !owned.Contains(i)));
                });
                return true;
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove keybind:\n\n{ex.Message}", DialogMessageType.Error);
                return false;
            }
        }

        // Bindings to one of ownCommands do not count as conflicts.
        private static string? FindConflictingCommand(IReadOnlyList<string> lines, string key, params string[] ownCommands)
        {
            foreach (int i in PlayerInputLineIndexes(lines))
            {
                if (BindingName(lines[i]) == key && BindingCommand(lines[i]) is string command && !ownCommands.Contains(command))
                    return command;
            }

            return null;
        }

        // Returns the index just after bEnableMouseSmoothing in [Engine.PlayerInput].
        // Returns the index just after the section header when that line is missing.
        // Returns -1 when the section is missing.
        private static int FindBindingInsertIndex(IReadOnlyList<string> lines)
        {
            foreach (int i in PlayerInputLineIndexes(lines))
            {
                if (lines[i].TrimStart().StartsWith("bEnableMouseSmoothing"))
                    return i + 1;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == PlayerInputSection)
                    return i + 1;
            }

            return -1;
        }

        private static IEnumerable<int> PlayerInputLineIndexes(IReadOnlyList<string> lines)
        {
            bool inPlayerInput = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("["))
                    inPlayerInput = trimmed == PlayerInputSection;
                else if (inPlayerInput)
                    yield return i;
            }
        }

        private static string Binding(string name, string command) =>
            $"Bindings=(Name=\"{name}\",Command=\"{command}\",Control=False,Shift=False,Alt=False)";

        private static string? BindingName(string line) => ReadQuotedValue(line, "Name=\"");

        private static string? BindingCommand(string line) => ReadQuotedValue(line, "Command=\"");

        private static string WithBindingName(string line, string name)
        {
            int start = line.IndexOf("Name=\"", StringComparison.Ordinal) + "Name=\"".Length;
            int end = line.IndexOf('"', start);
            return line[..start] + name + line[end..];
        }

        private static string? ReadQuotedValue(string line, string prefix)
        {
            int start = line.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += prefix.Length;
            int end = line.IndexOf('"', start);
            return end > start ? line[start..end] : null;
        }

        private static void WritePreservingReadOnly(string path, IEnumerable<string> lines)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            if (wasReadOnly)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

            try
            {
                File.WriteAllLines(path, lines);
            }
            finally
            {
                if (wasReadOnly)
                    File.SetAttributes(path, attributes);
            }
        }


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
                    string[] iniLines = await Task.Run(() => File.ReadAllLines(tdInputPath));
                    string? conflictingCommand = FindConflictingCommand(iniLines, ue3Key);

                    if (conflictingCommand != null)
                    {
                        await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                            $"The key '{ue3Key}' is already bound to the command '{conflictingCommand}'.\n\n" +
                            "Choose a different key or remove the existing binding in TdInput.ini first.",
                            DialogMessageType.Warning);
                        return false;
                    }
                }

                string? macroConflict = CheckMacroKeybindConflict(ue3Key, excludeSetting: settingName);
                if (macroConflict != null)
                {
                    await _dialogService.ShowMessageAsync("Duplicate Key Binding",
                        $"The key '{ue3Key}' is already assigned to '{macroConflict}'.\n\n" +
                        "Choose a different key.",
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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.Globalization;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class InputSettingsViewModel : BusyViewModel
    {
        private readonly IDialogService _dialogService;
        private readonly IDecompressionService _decompressionService;
        private readonly IAppSettingsService _settings;
        private bool _isLoading;

        protected override bool IsApplySuppressed => base.IsApplySuppressed || _isLoading;

        [ObservableProperty] private int _mouseSmoothingIndex = -1;
        [ObservableProperty] private int _uniformSensitivityIndex = -1;
        [ObservableProperty] private int _gamepadButtonsIndex = -1;
        [ObservableProperty] private string _dpi = "";
        [ObservableProperty] private string _cm360 = "";

        public InputSettingsViewModel(
            IDialogService dialogService,
            IDecompressionService decompressionService,
            IAppSettingsService settings,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _decompressionService = decompressionService;
            _settings = settings;
        }

        private static string TdInputIniPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EA Games", "Mirror's Edge", "TdGame", "Config", "TdInput.ini");

        private void SetSilently(Action action)
        {
            bool previous = _isLoading;
            _isLoading = true;
            try { action(); }
            finally { _isLoading = previous; }
        }


        partial void OnMouseSmoothingIndexChanged(int value) => EnqueueApply(() => ApplyMouseSmoothingChange(value));

        private void ApplyMouseSmoothingChange(int value)
        {
            if (_isLoading || value < 0)
                return;

            string tdInputIniPath = TdInputIniPath;

            if (!File.Exists(tdInputIniPath))
            {
                _dialogService.ShowMessage("Error",
                    $"Cannot edit mouse smoothing, 'TdInput.ini' file is missing from \"{tdInputIniPath}\".\n\n" +
                    "Launch Mirror's Edge at least once to create the configuration file.",
                    DialogMessageType.Error);
                return;
            }

            try
            {
                ApplyMouseSmoothing(tdInputIniPath, value == 0);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply mouse smoothing: {ex.Message}", DialogMessageType.Error);
            }
        }

        private static void ApplyMouseSmoothing(string iniPath, bool enabled)
        {
            if (!File.Exists(iniPath))
            {
                throw new FileNotFoundException($"TdInput.ini not found at: {iniPath}");
            }

            FileAttributes attributes = File.GetAttributes(iniPath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(iniPath, attributes & ~FileAttributes.ReadOnly);

            try
            {
                string[] lines = File.ReadAllLines(iniPath);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains('='))
                    {
                        string key = lines[i].Split('=')[0].Trim();
                        if (key == "bEnableMouseSmoothing")
                        {
                            lines[i] = $"bEnableMouseSmoothing={enabled.ToString().ToLower()}";
                        }
                    }
                }

                File.WriteAllLines(iniPath, lines);
            }
            finally
            {
                File.SetAttributes(iniPath, File.GetAttributes(iniPath) | FileAttributes.ReadOnly);
            }
        }

        public void RefreshMouseSmoothing()
        {
            string tdInputIniPath = TdInputIniPath;
            if (!File.Exists(tdInputIniPath))
                return;

            try
            {
                string[] lines = File.ReadAllLines(tdInputIniPath);

                foreach (string line in lines)
                {
                    if (line.Contains('='))
                    {
                        string key = line.Split('=')[0].Trim();
                        string value = line.Split('=')[1].Trim();

                        if (key == "bEnableMouseSmoothing")
                        {
                            SetSilently(() => MouseSmoothingIndex = value.ToLower() == "true" ? 0 : 1);
                            break;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        [RelayCommand]
        private void ShowMouseSmoothingInfo()
        {
            _dialogService.ShowMessage("Mouse Smoothing Information",
                "Mouse smoothing variably adjusts your mouse sensitivity, generally making it more inconsistent. Disabling mouse smoothing is recommended for a better experience.",
                DialogMessageType.Information);
        }


        partial void OnUniformSensitivityIndexChanged(int value) => _ = OnUniformSensitivityChangedAsync(value);

        private async Task OnUniformSensitivityChangedAsync(int value)
        {
            if (_isLoading || value < 0)
                return;

            var path = _session.Config.TdGamePackagePath;
            if (string.IsNullOrEmpty(path))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }

            if (!File.Exists(path))
            {
                _dialogService.ShowMessage("Error", $"TdGame.u package not found at: {path}", DialogMessageType.Error);
                return;
            }

            bool enabled = value == 0;

            if (enabled)
            {
                var result = await _dialogService.ShowConfirmationAsync(
                    "Speedrun Warning",
                    "Warning: Enabling uniform sensitivity is banned in official Mirror's Edge speedrun categories. " +
                    "Only enable this if you are playing casually.\n\n" +
                    "Do you want to continue?");

                if (!result)
                {
                    SetSilently(() => UniformSensitivityIndex = 1);
                    return;
                }
            }

            float targetValue = enabled ? UniformSensitivityPatcher.EnabledValue : UniformSensitivityPatcher.DisabledValue;

            await RunApplyAsync(async () =>
            {
                try
                {
                    ShowProgress("Applying uniform sensitivity setting...", true);

                    await Task.Run(() => UniformSensitivityPatcher.Apply(path, targetValue));

                    HideProgress();

                    string message = enabled
                        ? "Uniform sensitivity enabled. Mouse sensitivity will now remain consistent regardless of vertical view angle."
                        : "Uniform sensitivity disabled (default behaviour restored).";

                    _dialogService.ShowMessage("Success", message, DialogMessageType.Success);
                }
                catch (Exception ex)
                {
                    HideProgress();
                    _dialogService.ShowMessage("Error", $"Failed to apply uniform sensitivity: {ex.Message}", DialogMessageType.Error);
                }
            });
        }

        public void RefreshUniformSensitivity()
        {
            bool? enabled = UniformSensitivityPatcher.ReadIsEnabled(_session.Config.TdGamePackagePath);
            if (enabled.HasValue)
                SetSilently(() => UniformSensitivityIndex = enabled.Value ? 0 : 1);
        }

        public async Task RefreshUniformSensitivityAsync()
        {
            var path = _session.Config.TdGamePackagePath;
            bool? enabled = await Task.Run(() => UniformSensitivityPatcher.ReadIsEnabled(path));
            if (enabled.HasValue)
                SetSilently(() => UniformSensitivityIndex = enabled.Value ? 0 : 1);
        }

        public float? GetUniformSensitivityTargetValue() => UniformSensitivityIndex switch
        {
            0 => UniformSensitivityPatcher.EnabledValue,
            1 => UniformSensitivityPatcher.DisabledValue,
            _ => null
        };

        [RelayCommand]
        private void ShowUniformSensitivityInfo()
        {
            _dialogService.ShowMessage("Uniform Sensitivity Information",
                "Warning: Enabling uniform sensitivity is banned in official Mirror's Edge speedrun categories. " +
                "Only enable this if you are playing casually.\n\n" +
                "When pitching the camera more than 63° from the horizon, horizontal camera sensitivity is reduced by 60%. " +
                "Enabling uniform sensitivity keeps sensitivity consistent at all vertical angles.",
                DialogMessageType.Information);
        }


        [RelayCommand]
        private Task ApplyCm360() => RunApplyAsync(ApplyCm360Core);

        private async Task ApplyCm360Core()
        {
            string tdInputIniPath = TdInputIniPath;

            if (!File.Exists(tdInputIniPath))
            {
                _dialogService.ShowMessage("Error",
                    $"Cannot modify sensitivity, 'TdInput.ini' file is missing from \"{tdInputIniPath}\".\n\n" +
                    "Launch Mirror's Edge at least once to create the configuration file.",
                    DialogMessageType.Error);
                return;
            }

            try
            {
                if (!double.TryParse(Dpi, NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi) || dpi <= 0)
                {
                    _dialogService.ShowMessage("Invalid Input", "Enter a valid DPI value greater than 0.", DialogMessageType.Error);
                    return;
                }

                if (!double.TryParse(Cm360, NumberStyles.Float, CultureInfo.InvariantCulture, out double cm360) || cm360 <= 0)
                {
                    _dialogService.ShowMessage("Invalid Input", "Enter a valid cm/360° value greater than 0.", DialogMessageType.Error);
                    return;
                }

                double calculatedValue = (360 * 2.54) / (cm360 * dpi * 0.1538);

                _session.Config.Dpi = Dpi;
                _session.Config.Cm360 = Cm360;

                await RunBusyAsync("Applying sensitivity settings...", () =>
                {
                    ApplySensitivityMultiplier(tdInputIniPath, calculatedValue);
                    _settings.Save();
                });

                _dialogService.ShowMessage("Success",
                    $"Sensitivity multiplier set to {calculatedValue:F6}\n\n" +
                    $"Based on {dpi} DPI and {cm360} cm/360°\n\n" +
                    "Important: Disable mouse smoothing and enable FOV-agnostic sensitivity (if applicable) for consistent sensitivity behaviour.",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply sensitivity settings: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private void ResetCm360() => EnqueueApply(ResetCm360Core);

        private void ResetCm360Core()
        {
            string tdInputIniPath = TdInputIniPath;

            if (!File.Exists(tdInputIniPath))
            {
                _dialogService.ShowMessage("Error",
                    $"Cannot modify sensitivity, 'TdInput.ini' file is missing from \"{tdInputIniPath}\".\n\n" +
                    "Launch Mirror's Edge at least once to create the configuration file.",
                    DialogMessageType.Error);
                return;
            }

            try
            {
                ApplySensitivityMultiplier(tdInputIniPath, null);

                Dpi = string.Empty;
                Cm360 = string.Empty;

                _session.Config.Dpi = Dpi;
                _session.Config.Cm360 = Cm360;
                _settings.Save();

                _dialogService.ShowMessage("Success",
                    "Sensitivity behaviour reset to default.",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to reset sensitivity settings: {ex.Message}", DialogMessageType.Error);
            }
        }

        private static void ApplySensitivityMultiplier(string iniPath, double? calculatedValue)
        {
            if (!File.Exists(iniPath))
            {
                throw new FileNotFoundException($"TdInput.ini not found at: {iniPath}");
            }

            FileAttributes attributes = File.GetAttributes(iniPath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(iniPath, attributes & ~FileAttributes.ReadOnly);

            try
            {
                string[] lines = File.ReadAllLines(iniPath);
                bool inCorrectSection = false;
                string formattedMultiplier = calculatedValue.HasValue
                    ? calculatedValue.Value.ToString("F6", CultureInfo.InvariantCulture)
                    : string.Empty;

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmedLine = lines[i].Trim();

                    if (trimmedLine == "[TdGame.TdPlayerInputConsole]")
                    {
                        inCorrectSection = true;
                        continue;
                    }

                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        inCorrectSection = false;
                        continue;
                    }

                    if (inCorrectSection && lines[i].Contains('='))
                    {
                        string key = lines[i].Split('=')[0].Trim();

                        if (key == "MaxSensitivityMultiplier")
                        {
                            lines[i] = calculatedValue.HasValue
                                ? $"MaxSensitivityMultiplier={formattedMultiplier}"
                                : "MaxSensitivityMultiplier=1.800000";
                        }
                        else if (key == "MinSensitivityMultiplier")
                        {
                            lines[i] = calculatedValue.HasValue
                                ? $"MinSensitivityMultiplier={formattedMultiplier}"
                                : "MinSensitivityMultiplier=0.200000";
                        }
                    }
                }

                File.WriteAllLines(iniPath, lines);
            }
            finally
            {
                File.SetAttributes(iniPath, File.GetAttributes(iniPath) | FileAttributes.ReadOnly);
            }
        }

        [RelayCommand]
        private void ShowCm360Info()
        {
            _dialogService.ShowMessage("cm/360° Converter Information",
                "Converts real-world mouse sensitivity (measured in centimetres per 360° turn) into Mirror's Edge sensitivity values.\n\n" +
                "1. Enter your mouse DPI\n" +
                "2. Enter your desired cm/360° (how many centimetres you want to move the mouse for a full 360° turn)\n" +
                "3. Check the 'Apply' box to calculate and apply the sensitivity\n\n" +
                "Adjusting sensitivity in-game has no effect while this is enabled. Click 'Reset' to restore the default sensitivity behaviour.",
                DialogMessageType.Information);
        }


        partial void OnGamepadButtonsIndexChanged(int value) => EnqueueApply(() => OnGamepadButtonsChangedAsync(value));

        private async Task OnGamepadButtonsChangedAsync(int value)
        {
            if (_isLoading || value < 0)
                return;

            string buttonType = value == 0 ? "xbox" : "ps3";
            await ApplyGamepadButtons(buttonType);
        }

        private async Task ApplyGamepadButtons(string buttonType)
        {
            try
            {
                var gameDir = _session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                {
                    _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                    return;
                }

                if (!Directory.Exists(gameDir))
                {
                    _dialogService.ShowMessage("Error", "The selected game directory does not exist.", DialogMessageType.Error);
                    return;
                }

                ShowProgress($"Applying {buttonType.ToUpper()} gamepad buttons...", true);

                await Task.Run(() =>
                {
                    string cookedPcPath = Path.Combine(gameDir, "TdGame", "CookedPC");

                    if (!Directory.Exists(cookedPcPath))
                    {
                        throw new DirectoryNotFoundException($"CookedPC directory not found at: {cookedPcPath}");
                    }

                    string[] tsLocFiles = Directory.GetFiles(cookedPcPath, "Ts_LOC_*.upk");

                    if (tsLocFiles.Length == 0)
                    {
                        throw new FileNotFoundException("No Ts_LOC_*.upk files found in CookedPC directory.");
                    }

                    foreach (string tsLocFilePath in tsLocFiles)
                    {
                        try
                        {
                            _decompressionService.RunDecompressor(tsLocFilePath);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Failed to decompress {Path.GetFileName(tsLocFilePath)}: {ex.Message}");
                        }
                    }

                    GamepadButtonPatcher.ApplyButtonPatches(cookedPcPath, buttonType);
                });

                HideProgress();
            }
            catch (Exception ex)
            {
                HideProgress();
                _dialogService.ShowMessage("Error", $"Failed to apply gamepad buttons:\n\n{ex.Message}", DialogMessageType.Error);
            }
        }

        public void RefreshGamepadButtons()
        {
            bool? isPs3 = GamepadButtonPatcher.ReadIsPs3(_session.Config.GameDirectoryPath);
            SetSilently(() => GamepadButtonsIndex = isPs3 == true ? 1 : 0);
        }

        public async Task RefreshGamepadButtonsAsync()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            bool? isPs3 = await Task.Run(() => GamepadButtonPatcher.ReadIsPs3(gameDir));
            SetSilently(() => GamepadButtonsIndex = isPs3 == true ? 1 : 0);
        }

        public string? GetGamepadButtonType() => GamepadButtonsIndex switch
        {
            0 => "xbox",
            1 => "ps3",
            _ => null
        };

        [RelayCommand]
        private void ShowGamepadButtonsInfo()
        {
            _dialogService.ShowMessage("Gamepad Buttons Information",
                "By default, Mirror's Edge shows only Xbox button prompts when using a controller. " +
                "This setting toggles between PS3 and Xbox button prompts.\n\n" +
                "Note: This only affects the UI; it does not enable Sixaxis support for PS3 controllers. " +
                "An XInput wrapper is required for DualShock or DualSense controllers (e.g. Steam Input, DS4Windows, DualSenseX).",
                DialogMessageType.Information);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class LanguageSettingsViewModel : BusyViewModel
    {
        private const string CorruptedIniMessage = "TdEngine.ini is corrupted or missing its language setting.";

        private static readonly string[] LanguageNames =
        {
            "Čeština (CZE)",
            "Deutsch (DEU)",
            "English (INT)",
            "Español (ESN)",
            "Français (FRA)",
            "Italiano (ITA)",
            "Magyar (HUN)",
            "Polski (POL)",
            "Português (POR)",
            "Русский (RUS)",
            "한국어 (KOR)",
            "台灣繁體中文 (CHT)",
            "日本語 (JPN)",
            "简体中文 (CHS)",
        };

        private readonly IDialogService _dialogService;
        private readonly IDownloadService _download;
        private readonly IFileService _fileService;
        private readonly IAssetUrlProvider _assetUrls;
        private readonly GraphicsTweaksViewModel _graphics;
        private readonly ISteamService _steamService;
        private readonly GameLanguageService _gameLanguage;

        private bool _isLoading;
        private bool _isApplyingLanguage;
        private bool _isProgrammaticLanguageUpdate;
        private (int FromIndex, int ToIndex)? _failedSelectionEchoGuard;

        // Is false when the registry overrides TdEngine.ini's language and the game language patch could not be applied.
        // Is null before the first check, and when the last check found no override.
        private bool? _languagePatchApplied;

        protected override bool IsApplySuppressed => base.IsApplySuppressed || _isLoading;

        [ObservableProperty] private int _selectedLanguageIndex = -1;

        public LanguageSettingsViewModel(
            IDialogService dialogService,
            IDownloadService download,
            IFileService fileService,
            IAssetUrlProvider assetUrls,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress,
            GraphicsTweaksViewModel graphics,
            ISteamService steamService,
            GameLanguageService gameLanguage)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _download = download;
            _fileService = fileService;
            _assetUrls = assetUrls;
            _graphics = graphics;
            _steamService = steamService;
            _gameLanguage = gameLanguage;
        }

        public void Refresh()
        {
            try
            {
                _isLoading = true;

                int? index = TryGetLoadedLanguageIndex();
                if (index.HasValue)
                {
                    SelectedLanguageIndex = index.Value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load game language setting: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }

            TryReapplySteamLanguageFix();
        }

        // Applies the patch without asking when the registry overrides TdEngine.ini's language,
        // such as after a game file verification restores a stock MirrorsEdge.exe.
        public async Task CheckRegistryOverrideAsync()
        {
            _languagePatchApplied = null;

            string? gameDirectory = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDirectory))
                return;

            try
            {
                string exePath = GetExePath(gameDirectory);
                if (ReadInstalledLanguageCode(_session.Config) is not { } languageCode || !File.Exists(exePath))
                    return;

                _languagePatchApplied = await Task.Run(() => _gameLanguage.ReapplyPatchIfOverridden(exePath, languageCode));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to check the game language patch: {ex.Message}");
            }
        }

        private int? TryGetLoadedLanguageIndex() =>
            ReadInstalledLanguageCode(_session.Config) is { } code
                ? MapTdEngineLanguageCodeToIndex(
                    _gameLanguage.ResolveLoadedLanguage(code, registryBypassed: _languagePatchApplied != false))
                : null;

        // Returns the Language= value from TdEngine.ini, such as "int".
        // Returns null when the ini or the entry is missing.
        internal static string? ReadInstalledLanguageCode(GameConfiguration config)
        {
            string tdEnginePath = UserTdGamePathHelper.GetTdEngineIniPath(config);

            if (!File.Exists(tdEnginePath))
                return null;

            foreach (var line in File.ReadAllLines(tdEnginePath))
            {
                string trimmedLine = line.Trim();

                if (trimmedLine.StartsWith("Language="))
                    return trimmedLine.Substring("Language=".Length).Trim();
            }

            return null;
        }

        // Returns the runtime-assets language pack for a Language= value.
        // Returns null for an unknown language.
        internal static string? LanguagePackFor(string languageCode) =>
            MapTdEngineLanguageCodeToIndex(languageCode) is { } index
                ? GetLanguageConfig(LanguageNames[index])?.ZipFileName
                : null;

        private static int? MapTdEngineLanguageCodeToIndex(string value) => value.ToLower() switch
        {
            "cze" => 0,
            "deu" => 1,
            "int" => 2,
            "esn" => 3,
            "fra" => 4,
            "ita" => 5,
            "hun" => 6,
            "pol" => 7,
            "por" => 8,
            "rus" => 9,
            "kor" => 10,
            "cht" => 11,
            "jpn" => 12,
            "chs" => 13,
            _ => null
        };

        internal static string LanguageNameFor(string languageCode) =>
            MapTdEngineLanguageCodeToIndex(languageCode) is { } index ? LanguageNames[index] : languageCode.ToUpperInvariant();

        private static string GetExePath(string gameDirectory) =>
            Path.Combine(gameDirectory, "Binaries", "MirrorsEdge.exe");

        private void TryReapplySteamLanguageFix()
        {
            string? gameDirectory = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDirectory))
                return;

            try
            {
                _steamService.ApplyLanguageFix(gameDirectory);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to re-apply Steam language fix: {ex.Message}");
            }
        }

        private void SetLanguageIndexSilently(int index)
        {
            if (SelectedLanguageIndex == index)
                return;

            _isProgrammaticLanguageUpdate = true;
            bool previous = _isLoading;
            _isLoading = true;
            try
            {
                SelectedLanguageIndex = index;
            }
            finally
            {
                _isLoading = previous;
                Post(() => _isProgrammaticLanguageUpdate = false);
            }
        }

        partial void OnSelectedLanguageIndexChanged(int oldValue, int newValue)
        {
            if (_isProgrammaticLanguageUpdate || _isLoading || _isApplyingLanguage || newValue < 0 || newValue == oldValue || IsApplySuppressed)
                return;

            if (ShouldIgnoreFailedSelectionEcho(newValue))
                return;

            _isApplyingLanguage = true;
            EnqueueApply(() => OnLanguageChangedAsync(oldValue, newValue));
        }

        private bool ShouldIgnoreFailedSelectionEcho(int newValue)
        {
            if (_failedSelectionEchoGuard is not { } guard)
                return false;

            if (newValue == guard.FromIndex || newValue == guard.ToIndex)
                return true;

            _failedSelectionEchoGuard = null;
            return false;
        }

        private async Task OnLanguageChangedAsync(int previousIndex, int value)
        {
            bool switched = false;

            try
            {
                if (value < 0 || _isLoading)
                    return;

                if (value != SelectedLanguageIndex)
                    return;

                if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath) || value >= LanguageNames.Length)
                {
                    SetLanguageIndexSilently(previousIndex);
                    return;
                }

                string language = LanguageNames[value];

                var languageConfig = GetLanguageConfig(language);
                if (languageConfig == null)
                {
                    SetLanguageIndexSilently(previousIndex);
                    return;
                }

                _gameStatus.IsUiEnabled = false;
                _failedSelectionEchoGuard = (previousIndex, value);

                try
                {
                    string tdEnginePath = UserTdGamePathHelper.GetTdEngineIniPath(_session.Config);

                    if (!File.Exists(tdEnginePath))
                    {
                        _dialogService.ShowMessage("Error",
                            "TdEngine.ini was not found. Launch Mirror's Edge at least once to create it.",
                            DialogMessageType.Error,
                            tdEnginePath);
                        return;
                    }

                    if (ReadInstalledLanguageCode(_session.Config) == null)
                        throw new UserFacingException(CorruptedIniMessage);

                    ShowProgress("Updating the game language...", isIndeterminate: true);

                    // This runs before TdEngine.ini and the language files change,
                    // so a declined administrator prompt leaves them as they were.
                    if (!await EnsureGameUsesIniLanguageAsync(languageConfig.TdEngineLanguage, language))
                        return;

                    await Task.Run(() =>
                    {
                        try
                        {
                            if (!File.Exists(tdEnginePath))
                            {
                                throw new FileNotFoundException($"TdEngine.ini not found at: {tdEnginePath}");
                            }

                            var lines = File.ReadAllLines(tdEnginePath);
                            bool modified = false;

                            for (int i = 0; i < lines.Length; i++)
                            {
                                if (lines[i].TrimStart().StartsWith("Language="))
                                {
                                    int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                    string indent = lines[i].Substring(0, indentLength);
                                    lines[i] = indent + "Language=" + languageConfig.TdEngineLanguage;
                                    modified = true;
                                    break;
                                }
                            }

                            if (!modified)
                            {
                                // This throws instead of showing a dialog, because it runs on a background thread
                                // and a corrupted ini must also stop the language pack download below.
                                throw new UserFacingException(CorruptedIniMessage);
                            }

                            FileAttributes attributes = File.GetAttributes(tdEnginePath);
                            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                File.SetAttributes(tdEnginePath, attributes & ~FileAttributes.ReadOnly);

                            File.WriteAllLines(tdEnginePath, lines);
                            File.SetAttributes(tdEnginePath, File.GetAttributes(tdEnginePath) | FileAttributes.ReadOnly);
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

                    await _assetUrls.EnsureLoadedAsync();
                    await RunDownloadAndExtractAsync(
                        _download,
                        _fileService,
                        _assetUrls.For(languageConfig.ZipFileName),
                        _session.Config.GameDirectoryPath!,
                        "language files",
                        afterExtract: () => _graphics.ReapplyHighResUIFixIfNeededAsync(showDialogs: false));

                    SteamInstallScriptFixResult steamFixResult = await Task.Run(() =>
                        _steamService.ApplyLanguageFix(_session.Config.GameDirectoryPath!));

                    if (steamFixResult.AnyFailed)
                    {
                        _dialogService.ShowMessage(
                            "Warning",
                            "Game language was updated, but Mirror's Edge Tweaks could not update some of Steam's files for Mirror's Edge, " +
                            "so the language may revert when you launch the game through Steam.",
                            DialogMessageType.Warning);
                    }

                    switched = true;
                    _failedSelectionEchoGuard = null;
                    _dialogService.ShowMessage("Success", $"Game language has been changed to {language}.", DialogMessageType.Success);
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError("Could not switch the game language.", ex);
                }
                finally
                {
                    if (!switched)
                    {
                        SetLanguageIndexSilently(previousIndex);
                        HideProgress();
                    }

                    _gameStatus.IsUiEnabled = true;
                }
            }
            finally
            {
                Post(() => _isApplyingLanguage = false);
            }
        }

        // Tells the user and returns false when the registry still makes the game load another language.
        private async Task<bool> EnsureGameUsesIniLanguageAsync(string languageCode, string languageName)
        {
            IniLanguageOutcome outcome = await _gameLanguage.EnsureIniLanguageAsync(
                GetExePath(_session.Config.GameDirectoryPath!),
                languageCode,
                (overridingCode, patchError) => ConfirmRegistryChangeAsync(languageName, overridingCode, patchError));

            if (outcome == IniLanguageOutcome.Patched)
                _languagePatchApplied = true;

            if (outcome != IniLanguageOutcome.Declined)
                return true;

            string overridingName = LanguageNameFor(_gameLanguage.FindRegistryOverride(languageCode) ?? languageCode);
            _dialogService.ShowMessage("Game Language Not Changed",
                $"The game language was not changed. Mirror's Edge will keep using {overridingName}.",
                DialogMessageType.Warning);
            return false;
        }

        private Task<bool> ConfirmRegistryChangeAsync(string languageName, string overridingCode, Exception patchError) =>
            _dialogService.ShowConfirmationAsync("Administrator Permission Needed",
                $"Mirror's Edge Tweaks could not patch MirrorsEdge.exe. {DescribePatchError(patchError)}\n\n" +
                $"Without this patch, Mirror's Edge will keep using {LanguageNameFor(overridingCode)}. " +
                $"Instead, Mirror's Edge Tweaks can switch to {languageName} by changing the game's language setting in Windows, " +
                "which needs administrator permission.\n\n" +
                "Do you want to continue?");

        // ErrorMessageHelper.Explain's generic text refers to a technical details section, which the confirmation does not have.
        private static string DescribePatchError(Exception exception) =>
            exception is UserFacingException or IOException or UnauthorizedAccessException
                ? ErrorMessageHelper.Explain(exception)
                : "It may be from an unsupported version of the game, or another tool may have changed it.";

        private class LanguageConfig
        {
            public string ZipFileName { get; set; } = "";
            public string TdEngineLanguage { get; set; } = "";
        }

        private static LanguageConfig? GetLanguageConfig(string language)
        {
            return language switch
            {
                "Čeština (CZE)" => new LanguageConfig
                {
                    ZipFileName = "CZE.zip",
                    TdEngineLanguage = "cze"
                },
                "Deutsch (DEU)" => new LanguageConfig
                {
                    ZipFileName = "DEU.zip",
                    TdEngineLanguage = "deu"
                },
                "English (INT)" => new LanguageConfig
                {
                    ZipFileName = "INT.zip",
                    TdEngineLanguage = "int"
                },
                "Español (ESN)" => new LanguageConfig
                {
                    ZipFileName = "ESN.zip",
                    TdEngineLanguage = "esn"
                },
                "Français (FRA)" => new LanguageConfig
                {
                    ZipFileName = "FRA.zip",
                    TdEngineLanguage = "fra"
                },
                "Italiano (ITA)" => new LanguageConfig
                {
                    ZipFileName = "ITA.zip",
                    TdEngineLanguage = "ita"
                },
                "Magyar (HUN)" => new LanguageConfig
                {
                    ZipFileName = "HUN.zip",
                    TdEngineLanguage = "hun"
                },
                "Polski (POL)" => new LanguageConfig
                {
                    ZipFileName = "POL.zip",
                    TdEngineLanguage = "pol"
                },
                "Português (POR)" => new LanguageConfig
                {
                    ZipFileName = "POR.zip",
                    TdEngineLanguage = "por"
                },
                "Русский (RUS)" => new LanguageConfig
                {
                    ZipFileName = "RUS.zip",
                    TdEngineLanguage = "rus"
                },
                "한국어 (KOR)" => new LanguageConfig
                {
                    ZipFileName = "KOR.zip",
                    TdEngineLanguage = "kor"
                },
                "台灣繁體中文 (CHT)" => new LanguageConfig
                {
                    ZipFileName = "CHT.zip",
                    TdEngineLanguage = "cht"
                },
                "日本語 (JPN)" => new LanguageConfig
                {
                    ZipFileName = "JPN.zip",
                    TdEngineLanguage = "jpn"
                },
                "简体中文 (CHS)" => new LanguageConfig
                {
                    ZipFileName = "CHS.zip",
                    TdEngineLanguage = "chs"
                },
                _ => null
            };
        }

        [RelayCommand]
        private void ShowGameLanguageInfo()
        {
            _dialogService.ShowMessage("Game Language Information",
                "Change to any of the game's 14 supported languages.\n\n" +
                "Mirror's Edge Tweaks may patch MirrorsEdge.exe so that the game uses the language you choose. " +
                "If MirrorsEdge.exe cannot be patched, changing the language needs administrator permission instead.\n\n" +
                "The following languages support UI and subtitles only: Czech, Hungarian, Portuguese (Brazil), Korean, Traditional Chinese (Taiwan), and Simplified Chinese.",
                DialogMessageType.Information);
        }
    }
}

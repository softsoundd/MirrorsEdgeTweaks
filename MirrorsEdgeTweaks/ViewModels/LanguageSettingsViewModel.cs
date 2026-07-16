using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class LanguageSettingsViewModel : BusyViewModel
    {
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

        private bool _isLoading;
        private bool _isApplyingLanguage;
        private bool _isProgrammaticLanguageUpdate;
        private (int FromIndex, int ToIndex)? _failedSelectionEchoGuard;

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
            ISteamService steamService)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _download = download;
            _fileService = fileService;
            _assetUrls = assetUrls;
            _graphics = graphics;
            _steamService = steamService;
        }

        public void Refresh()
        {
            try
            {
                _isLoading = true;

                int? index = TryGetInstalledLanguageIndex();
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

        private int? TryGetInstalledLanguageIndex()
        {
            string tdEnginePath = UserTdGamePathHelper.GetTdEngineIniPath(_session.Config);

            if (!File.Exists(tdEnginePath))
                return null;

            foreach (var line in File.ReadAllLines(tdEnginePath))
            {
                string trimmedLine = line.Trim();

                if (trimmedLine.StartsWith("Language="))
                {
                    string value = trimmedLine.Substring("Language=".Length).Trim();
                    return MapTdEngineLanguageCodeToIndex(value);
                }
            }

            return null;
        }

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
                    UpdateRegistryLanguage(languageConfig);

                string tdEnginePath = UserTdGamePathHelper.GetTdEngineIniPath(_session.Config);

                if (!File.Exists(tdEnginePath))
                {
                    _dialogService.ShowMessage("Error",
                        $"Cannot switch language, 'TdEngine.ini' file is missing from \"{tdEnginePath}\".\n\n" +
                        "Launch Mirror's Edge at least once to create the configuration file.",
                        DialogMessageType.Error);
                    return;
                }

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
                            // Thrown (not shown) here: this runs on a background thread, and a
                            // corrupted INI must also abort the language-pack download below.
                            throw new InvalidDataException("TdEngine.ini file is corrupted (no Language= entry found).");
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
                        "Game language was updated, but one or more Steam install scripts could not be updated. " +
                        "Language changes may revert when launching via Steam.",
                        DialogMessageType.Warning);
                }

                switched = true;
                _failedSelectionEchoGuard = null;
                _dialogService.ShowMessage("Success", $"Game language has been changed to {language}.", DialogMessageType.Success);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    _dialogService.ShowMessage("Administrator Access Required",
                        "Changing the game language requires administrator privileges to modify the Windows Registry.\n\n" +
                        "To switch languages:\n\n" +
                        "1. Close Mirror's Edge Tweaks\n" +
                        "2. Restart it as administrator\n" +
                        "3. Change the language again",
                        DialogMessageType.Error);
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage("Error", $"Failed to switch language:\n\n{ex.Message}", DialogMessageType.Error);
                }
                finally
                {
                    if (!switched)
                    {
                        SetLanguageIndexSilently(previousIndex);
                    }

                    _gameStatus.IsUiEnabled = true;
                }
            }
            finally
            {
                Post(() => _isApplyingLanguage = false);
            }
        }

        private class LanguageConfig
        {
            public string ZipFileName { get; set; } = "";
            public string RegistryLanguage { get; set; } = "";
            public string Locale { get; set; } = "";
            public string TdEngineLanguage { get; set; } = "";
        }

        private LanguageConfig? GetLanguageConfig(string language)
        {
            return language switch
            {
                "Čeština (CZE)" => new LanguageConfig
                {
                    ZipFileName = "CZE.zip",
                    RegistryLanguage = "Czech",
                    Locale = "cs",
                    TdEngineLanguage = "cze"
                },
                "Deutsch (DEU)" => new LanguageConfig
                {
                    ZipFileName = "DEU.zip",
                    RegistryLanguage = "German",
                    Locale = "de_DE",
                    TdEngineLanguage = "deu"
                },
                "English (INT)" => new LanguageConfig
                {
                    ZipFileName = "INT.zip",
                    RegistryLanguage = "English",
                    Locale = "en_UK",
                    TdEngineLanguage = "int"
                },
                "Español (ESN)" => new LanguageConfig
                {
                    ZipFileName = "ESN.zip",
                    RegistryLanguage = "Spanish",
                    Locale = "es_ES",
                    TdEngineLanguage = "esn"
                },
                "Français (FRA)" => new LanguageConfig
                {
                    ZipFileName = "FRA.zip",
                    RegistryLanguage = "French",
                    Locale = "fr_FR",
                    TdEngineLanguage = "fra"
                },
                "Italiano (ITA)" => new LanguageConfig
                {
                    ZipFileName = "ITA.zip",
                    RegistryLanguage = "Italian",
                    Locale = "it_IT",
                    TdEngineLanguage = "ita"
                },
                "Magyar (HUN)" => new LanguageConfig
                {
                    ZipFileName = "HUN.zip",
                    RegistryLanguage = "Hungarian",
                    Locale = "hu_HU",
                    TdEngineLanguage = "hun"
                },
                "Polski (POL)" => new LanguageConfig
                {
                    ZipFileName = "POL.zip",
                    RegistryLanguage = "Polish",
                    Locale = "pl_PL",
                    TdEngineLanguage = "pol"
                },
                "Português (POR)" => new LanguageConfig
                {
                    ZipFileName = "POR.zip",
                    RegistryLanguage = "Portuguese Brazil",
                    Locale = "pt_PT",
                    TdEngineLanguage = "por"
                },
                "Русский (RUS)" => new LanguageConfig
                {
                    ZipFileName = "RUS.zip",
                    RegistryLanguage = "Russian",
                    Locale = "ru_RU",
                    TdEngineLanguage = "rus"
                },
                "한국어 (KOR)" => new LanguageConfig
                {
                    ZipFileName = "KOR.zip",
                    RegistryLanguage = "Korean",
                    Locale = "ko_KR",
                    TdEngineLanguage = "kor"
                },
                "台灣繁體中文 (CHT)" => new LanguageConfig
                {
                    ZipFileName = "CHT.zip",
                    RegistryLanguage = "Traditional Chinese Taiwan",
                    Locale = "zh-TW",
                    TdEngineLanguage = "cht"
                },
                "日本語 (JPN)" => new LanguageConfig
                {
                    ZipFileName = "JPN.zip",
                    RegistryLanguage = "Japanese",
                    Locale = "ja_JP",
                    TdEngineLanguage = "jpn"
                },
                "简体中文 (CHS)" => new LanguageConfig
                {
                    ZipFileName = "CHS.zip",
                    RegistryLanguage = "Simplified Chinese",
                    Locale = "zh_CN",
                    TdEngineLanguage = "chs"
                },
                _ => null
            };
        }

        private static void UpdateRegistryLanguage(LanguageConfig languageConfig)
        {
            const string keyPath = @"SOFTWARE\WOW6432Node\EA Games\Mirror's Edge";
            UpdateRegistryValue(keyPath, "Language", languageConfig.RegistryLanguage);
            UpdateRegistryValue(keyPath, "Locale", languageConfig.Locale);
        }

        private static void UpdateRegistryValue(string keyPath, string valueName, string newValue)
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, true))
            {
                if (key == null)
                {
                    using (var createdKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath))
                    {
                        createdKey?.SetValue(valueName, newValue, Microsoft.Win32.RegistryValueKind.String);
                    }
                }
                else
                {
                    key.SetValue(valueName, newValue, Microsoft.Win32.RegistryValueKind.String);
                }
            }
        }

        [RelayCommand]
        private void ShowGameLanguageInfo()
        {
            _dialogService.ShowMessage("Game Language Information",
                "Change to any of the game's 14 supported languages.\n\n" +
                "Note: Requires administrator privileges to modify registry values.\n\n" +
                "The following languages support UI and subtitles only: Czech, Hungarian, Portuguese (Brazil), Korean, Traditional Chinese (Taiwan), and Simplified Chinese.",
                DialogMessageType.Information);
        }
    }
}

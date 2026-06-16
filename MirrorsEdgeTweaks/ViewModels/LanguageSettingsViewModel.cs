using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace MirrorsEdgeTweaks.ViewModels
{
    // View model for the Game Language setting: detects the configured language, and on change
    // updates the Windows registry + TdEngine.ini and downloads/extracts the matching language
    // pack (reapplying the high-res UI fix afterwards).
    public partial class LanguageSettingsViewModel : ObservableObject
    {
        private const long SteamMirrorsEdgeExeSize = 31946072;

        private static readonly string[] LanguageNames =
        {
            "Čeština (CZE)",   // 0
            "Deutsch (DEU)",   // 1
            "English (INT)",   // 2
            "Español (ESN)",   // 3
            "Français (FRA)",  // 4
            "Italiano (ITA)",  // 5
            "Magyar (HUN)",    // 6
            "Polski (POL)",    // 7
            "Português (POR)", // 8
            "Русский (RUS)",   // 9
            "한국어 (KOR)",       // 10
            "台灣繁體中文 (CHT)",  // 11
            "日本語 (JPN)",      // 12
            "简体中文 (CHS)",     // 13
        };

        private readonly IDialogService _dialogService;
        private readonly GameSession _session;
        private readonly GameStatusViewModel _gameStatus;
        private readonly DownloadProgressViewModel _downloadProgress;
        private readonly GraphicsTweaksViewModel _graphics;

        private bool _isLoading;

        [ObservableProperty] private int _selectedLanguageIndex = -1;

        public LanguageSettingsViewModel(
            IDialogService dialogService,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress,
            GraphicsTweaksViewModel graphics)
        {
            _dialogService = dialogService;
            _session = session;
            _gameStatus = gameStatus;
            _downloadProgress = downloadProgress;
            _graphics = graphics;
        }

        public void Refresh()
        {
            try
            {
                _isLoading = true;

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                    return;

                var lines = File.ReadAllLines(tdEnginePath);

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("Language="))
                    {
                        string value = trimmedLine.Substring("Language=".Length).Trim();

                        int index = value.ToLower() switch
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
                            _ => -1
                        };

                        if (index >= 0)
                        {
                            SelectedLanguageIndex = index;
                        }
                        return;
                    }
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
        }

        partial void OnSelectedLanguageIndexChanged(int value) => _ = OnLanguageChangedAsync(value);

        private async Task OnLanguageChangedAsync(int value)
        {
            if (value < 0 || _isLoading || string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return;

            if (value >= LanguageNames.Length)
                return;

            string language = LanguageNames[value];

            var languageConfig = GetLanguageConfig(language);
            if (languageConfig == null)
                return;

            _gameStatus.IsUiEnabled = false;

            try
            {
                string exePath = Path.Combine(_session.Config.GameDirectoryPath, "Binaries", "MirrorsEdge.exe");
                if (File.Exists(exePath))
                {
                    var exeFileInfo = new FileInfo(exePath);
                    if (exeFileInfo.Length == SteamMirrorsEdgeExeSize) // Steam version
                    {
                        _dialogService.ShowMessage("Warning",
                            $"You're currently using the Steam version of Mirror's Edge, which does not support language changes made outside the Steam client. " +
                            $"Each time the game is launched via Steam, the language will automatically revert to the setting configured in your Steam client. " +
                            $"If you want the language changes made with Mirror's Edge Tweaks to remain, you will need to either:\n\n" +
                            $"1. Launch Mirror's Edge with one of the Launch Game buttons at the top of the window\n\n" +
                            $"2. Launch the Mirror's Edge executable directly (found here: \"{exePath}\"), or\n\n" +
                            $"3. Add the aforementioned executable as a Non-Steam game.",
                            DialogMessageType.Warning);
                    }
                }

                UpdateRegistryValue(@"SOFTWARE\WOW6432Node\EA Games\Mirror's Edge", "Language", languageConfig.RegistryLanguage);
                UpdateRegistryValue(@"SOFTWARE\WOW6432Node\EA Games\Mirror's Edge", "Locale", languageConfig.Locale);

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                {
                    _dialogService.ShowMessage("Error",
                        $"Cannot switch language, 'TdEngine.ini' file is missing from \"{tdEnginePath}\".\n\n" +
                        "Please ensure you have launched Mirror's Edge at least once so that this file can be created.",
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
                            _dialogService.ShowMessage("Error", "TdEngine.ini file is corrupted.", DialogMessageType.Error);
                            return;
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

                await DownloadAndExtractLanguageFiles(languageConfig.DownloadUrl);

                _dialogService.ShowMessage("Success", $"Game language has been changed to {language}.", DialogMessageType.Success);
            }
            catch (System.Security.SecurityException)
            {
                _dialogService.ShowMessage("Administrator Access Required",
                    "Changing the game language requires administrator privileges to modify the Windows Registry.\n\n" +
                    "To switch languages, please:\n\n" +
                    "1. Close Mirror's Edge Tweaks\n" +
                    "2. Right-click on MirrorsEdgeTweaks.exe\n" +
                    "3. Select 'Run as administrator'\n" +
                    "4. Try changing the language again",
                    DialogMessageType.Error);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to switch language:\n\n{ex.Message}", DialogMessageType.Error);
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
            }
        }

        private class LanguageConfig
        {
            public string DownloadUrl { get; set; } = "";
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
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/CZE.zip",
                    RegistryLanguage = "Czech",
                    Locale = "cs",
                    TdEngineLanguage = "cze"
                },
                "Deutsch (DEU)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/DEU.zip",
                    RegistryLanguage = "German",
                    Locale = "de_DE",
                    TdEngineLanguage = "deu"
                },
                "English (INT)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/INT.zip",
                    RegistryLanguage = "English",
                    Locale = "en_UK",
                    TdEngineLanguage = "int"
                },
                "Español (ESN)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/ESN.zip",
                    RegistryLanguage = "Spanish",
                    Locale = "es_ES",
                    TdEngineLanguage = "esn"
                },
                "Français (FRA)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/FRA.zip",
                    RegistryLanguage = "French",
                    Locale = "fr_FR",
                    TdEngineLanguage = "fra"
                },
                "Italiano (ITA)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/ITA.zip",
                    RegistryLanguage = "Italian",
                    Locale = "it_IT",
                    TdEngineLanguage = "ita"
                },
                "Magyar (HUN)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/HUN.zip",
                    RegistryLanguage = "Hungarian",
                    Locale = "hu_HU",
                    TdEngineLanguage = "hun"
                },
                "Polski (POL)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/POL.zip",
                    RegistryLanguage = "Polish",
                    Locale = "pl_PL",
                    TdEngineLanguage = "pol"
                },
                "Português (POR)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/POR.zip",
                    RegistryLanguage = "Portuguese Brazil",
                    Locale = "pt_PT",
                    TdEngineLanguage = "por"
                },
                "Русский (RUS)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/RUS.zip",
                    RegistryLanguage = "Russian",
                    Locale = "ru_RU",
                    TdEngineLanguage = "rus"
                },
                "한국어 (KOR)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/KOR.zip",
                    RegistryLanguage = "Korean",
                    Locale = "ko_KR",
                    TdEngineLanguage = "kor"
                },
                "台灣繁體中文 (CHT)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/CHT.zip",
                    RegistryLanguage = "Traditional Chinese Taiwan",
                    Locale = "zh-TW",
                    TdEngineLanguage = "cht"
                },
                "日本語 (JPN)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/JPN.zip",
                    RegistryLanguage = "Japanese",
                    Locale = "ja_JP",
                    TdEngineLanguage = "jpn"
                },
                "简体中文 (CHS)" => new LanguageConfig
                {
                    DownloadUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Language%20Files/CHS.zip",
                    RegistryLanguage = "Simplified Chinese",
                    Locale = "zh_CN",
                    TdEngineLanguage = "chs"
                },
                _ => null
            };
        }

        private void UpdateRegistryValue(string keyPath, string valueName, string newValue)
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

        private async Task DownloadAndExtractLanguageFiles(string url)
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;

            try
            {
                if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                    return;

                string tempZipPath = Path.Combine(Path.GetTempPath(), $"MELanguage_{Guid.NewGuid()}.zip");
                string extractPath = _session.Config.GameDirectoryPath;

                using (var client = new HttpClient())
                {
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes != -1;

                        await using (var contentStream = await response.Content.ReadAsStreamAsync())
                        await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var totalRead = 0L;
                            var buffer = new byte[8192];
                            var isMoreToRead = true;

                            await dispatcher.InvokeAsync(() =>
                            {
                                _downloadProgress.IsDownloadProgressIndeterminate = false;
                                _downloadProgress.DownloadProgressValue = 0;
                                _downloadProgress.IsDownloadProgressVisible = true;
                                _gameStatus.Status = "Downloading language files...";
                            });

                            do
                            {
                                var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                                if (read == 0)
                                {
                                    isMoreToRead = false;
                                }
                                else
                                {
                                    await fileStream.WriteAsync(buffer, 0, read);

                                    totalRead += read;

                                    if (canReportProgress)
                                    {
                                        var progressPercentage = (double)totalRead / totalBytes * 100;
                                        await dispatcher.InvokeAsync(() =>
                                        {
                                            _downloadProgress.DownloadProgressValue = progressPercentage;
                                            _gameStatus.Status = $"Downloading language files... {progressPercentage:F0}%";
                                        });
                                    }
                                }
                            }
                            while (isMoreToRead);
                        }
                    }
                }

                await dispatcher.InvokeAsync(() =>
                {
                    _downloadProgress.IsDownloadProgressIndeterminate = true;
                    _gameStatus.Status = "Extracting language files...";
                });

                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(tempZipPath, extractPath, true);
                });

                File.Delete(tempZipPath);

                await _graphics.ReapplyHighResUIFixIfNeededAsync(showDialogs: false);

                await dispatcher.InvokeAsync(() =>
                {
                    _downloadProgress.IsDownloadProgressVisible = false;
                    _gameStatus.Status = "Ready.";
                });
            }
            catch (Exception ex)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    _downloadProgress.IsDownloadProgressVisible = false;
                    _gameStatus.Status = "Ready.";
                });
                _dialogService.ShowMessage("Error", $"Failed to download or extract language files:\n\n{ex.Message}", DialogMessageType.Error);
                throw;
            }
        }

        [RelayCommand]
        private void ShowGameLanguageInfo()
        {
            _dialogService.ShowMessage("Game Language Information",
                "Allows you to change to any of the game's 14 supported languages.\n\n" +
                "Note: Requires administrator privileges to modify registry values.\n\n" +
                "The following languages support only UI and subtitles: Czech, Hungarian, Portuguese Brazil, Korean, Traditional Chinese Taiwan, and Simplified Chinese.",
                DialogMessageType.Information);
        }
    }
}

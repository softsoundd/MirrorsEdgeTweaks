using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class AudioSettingsViewModel : BusyViewModel
    {
        private readonly IDialogService _dialogService;
        private readonly IFileService _fileService;
        private readonly IDownloadService _download;
        private readonly IAssetUrlProvider _assetUrls;
        private bool _isLoading;

        protected override bool IsApplySuppressed => base.IsApplySuppressed || _isLoading;

        [ObservableProperty]
        private int _selectedAudioBackendIndex = -1;

        public AudioSettingsViewModel(
            IDialogService dialogService,
            IFileService fileService,
            IDownloadService download,
            IAssetUrlProvider assetUrls,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _fileService = fileService;
            _download = download;
            _assetUrls = assetUrls;
        }

        public void RefreshAudioBackendSetting()
        {
            try
            {
                _isLoading = true;

                string tdEnginePath = UserTdGamePathHelper.GetTdEngineIniPath(_session.Config);
                string? deviceName = AudioBackendHelper.ReadDeviceName(tdEnginePath);

                if (deviceName == AudioBackendHelper.DefaultDeviceName)
                {
                    SelectedAudioBackendIndex = 0;
                }
                else if (deviceName == AudioBackendHelper.OpenAlSoftDeviceName)
                {
                    bool hrtfInstalled = !string.IsNullOrEmpty(_session.Config.GameDirectoryPath)
                        && AudioBackendHelper.IsHrtfInstalled(_session.Config.GameDirectoryPath);
                    SelectedAudioBackendIndex = hrtfInstalled ? 2 : 1;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load audio backend setting: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void SetAudioBackendIndexSilently(int index)
        {
            _isLoading = true;
            try
            {
                SelectedAudioBackendIndex = index;
            }
            finally
            {
                _isLoading = false;
            }
        }

        partial void OnSelectedAudioBackendIndexChanged(int oldValue, int newValue)
        {
            if (_isLoading || newValue < 0)
                return;

            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
            {
                SetAudioBackendIndexSilently(oldValue);
                return;
            }

            EnqueueApply(() => ApplyAudioBackendAsync(oldValue, newValue));
        }

        private async Task ApplyAudioBackendAsync(int previousIndex, int selectedIndex)
        {
            _gameStatus.IsUiEnabled = false;
            bool applied = false;

            try
            {
                string fileName;
                int maxChannels;
                string deviceName;

                switch (selectedIndex)
                {
                    case 0:
                        fileName = AudioBackendHelper.DefaultAssetFileName;
                        maxChannels = 32;
                        deviceName = AudioBackendHelper.DefaultDeviceName;
                        break;
                    case 1:
                        fileName = "OpenALSoft.zip";
                        maxChannels = 256;
                        deviceName = AudioBackendHelper.OpenAlSoftDeviceName;
                        break;
                    case 2:
                        fileName = "OpenALSoftHRTF.zip";
                        maxChannels = 256;
                        deviceName = AudioBackendHelper.OpenAlSoftDeviceName;
                        break;
                    default:
                        return;
                }

                await _assetUrls.EnsureLoadedAsync();
                string downloadUrl = _assetUrls.For(fileName);

                await RunDownloadAndExtractAsync(
                    _download,
                    _fileService,
                    downloadUrl,
                    _session.Config.GameDirectoryPath!,
                    "audio backend files");

                if (selectedIndex != 2)
                    AudioBackendHelper.DeleteHrtfFiles(_session.Config.GameDirectoryPath!);

                string tdEnginePath = UserTdGamePathHelper.GetTdEngineIniPath(_session.Config);

                if (!File.Exists(tdEnginePath))
                {
                    _dialogService.ShowMessage("Error",
                        $"Cannot change audio backend, 'TdEngine.ini' file is missing from \"{tdEnginePath}\".\n\n" +
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

                        var lines = _fileService.ReadAllLines(tdEnginePath);
                        bool inALAudioSection = false;
                        bool modifiedMaxChannels = false;
                        bool modifiedDeviceName = false;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string trimmedLine = lines[i].Trim();

                            if (trimmedLine.StartsWith("["))
                            {
                                inALAudioSection = trimmedLine == "[ALAudio.ALAudioDevice]";
                                continue;
                            }

                            if (inALAudioSection)
                            {
                                if (trimmedLine.StartsWith("MaxChannels="))
                                {
                                    int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                    string indent = lines[i].Substring(0, indentLength);
                                    lines[i] = indent + "MaxChannels=" + maxChannels;
                                    modifiedMaxChannels = true;
                                }
                                else if (trimmedLine.StartsWith("DeviceName="))
                                {
                                    int indentLength = lines[i].Length - lines[i].TrimStart().Length;
                                    string indent = lines[i].Substring(0, indentLength);
                                    lines[i] = indent + "DeviceName=" + deviceName;
                                    modifiedDeviceName = true;
                                }

                                if (modifiedMaxChannels && modifiedDeviceName)
                                    break;
                            }
                        }

                        if (!modifiedMaxChannels || !modifiedDeviceName)
                        {
                            throw new UserFacingException("TdEngine.ini is corrupted or missing its audio settings.");
                        }

                        _fileService.WriteAllLinesAndLock(tdEnginePath, lines);
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

                string[] backendNames = { "OpenAL (default)", "OpenAL Soft (modern)", "OpenAL Soft (HRTF)" };
                applied = true;
                _dialogService.ShowMessage("Success", $"Audio backend has been changed to {backendNames[selectedIndex]}.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Could not change the audio backend.", ex);
            }
            finally
            {
                if (!applied)
                {
                    SetAudioBackendIndexSilently(previousIndex);
                    RefreshAudioBackendSetting();
                }
                _gameStatus.IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private void ShowAudioBackendInfo()
        {
            _dialogService.ShowMessage("Audio Backend Information",
                "The default OpenAL implementation in Mirror's Edge has sampling issues where the initial attack and transients of footstep sounds, " +
                "hand placements, and similar effects are lost due to audio fading in. OpenAL Soft fixes these issues and improves overall audio clarity.\n\n" +
                "Both OpenAL Soft options raise the simultaneous audio source limit from 32 to 256. " +
                "The default limit of 32 causes sounds to be cut abruptly during busy scenes with heavy gunfire, foley, and ambient sources.\n\n" +
                "The HRTF option provides realistic 3D spatial audio through stereo headphones. It applies HRTF only to sounds in the game world, " +
                "because applying it to music, dialogue and UI effects can make them sound unnatural.",
                DialogMessageType.Information);
        }
    }
}

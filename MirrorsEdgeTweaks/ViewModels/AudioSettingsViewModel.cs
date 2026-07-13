using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                    return;

                var lines = File.ReadAllLines(tdEnginePath);
                bool inALAudioSection = false;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("["))
                    {
                        inALAudioSection = trimmedLine == "[ALAudio.ALAudioDevice]";
                        continue;
                    }

                    if (inALAudioSection && trimmedLine.StartsWith("DeviceName="))
                    {
                        string value = trimmedLine.Substring("DeviceName=".Length).Trim();

                        if (value == "Generic Hardware")
                        {
                            SelectedAudioBackendIndex = 0;
                        }
                        else if (value == "OpenAL Soft")
                        {
                            bool hrtfInstalled = !string.IsNullOrEmpty(_session.Config.GameDirectoryPath)
                                && File.Exists(Path.Combine(_session.Config.GameDirectoryPath!, "Binaries", "soft_oal.dll"));
                            SelectedAudioBackendIndex = hrtfInstalled ? 2 : 1;
                        }
                        return;
                    }
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
                        fileName = "OpenAL.zip";
                        maxChannels = 32;
                        deviceName = "Generic Hardware";
                        break;
                    case 1:
                        fileName = "OpenALSoft.zip";
                        maxChannels = 256;
                        deviceName = "OpenAL Soft";
                        break;
                    case 2:
                        fileName = "OpenALSoftHRTF.zip";
                        maxChannels = 256;
                        deviceName = "OpenAL Soft";
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
                    CleanupHrtfFiles();

                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string tdEnginePath = Path.Combine(documentsPath, "EA Games", "Mirror's Edge", "TdGame", "Config", "TdEngine.ini");

                if (!File.Exists(tdEnginePath))
                {
                    _dialogService.ShowMessage("Error",
                        $"Cannot change audio backend, 'TdEngine.ini' file is missing from \"{tdEnginePath}\".\n\n" +
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
                            throw new Exception("Failed to find MaxChannels or DeviceName in [ALAudio.ALAudioDevice] section of TdEngine.ini");
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
                _dialogService.ShowMessage("Error", $"Failed to change audio backend:\n\n{ex.Message}", DialogMessageType.Error);
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

        private void CleanupHrtfFiles()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
                return;

            string binDir = Path.Combine(gameDir, "Binaries");
            string[] hrtfFiles = { "soft_oal.dll", "alsoft.ini", "openal-hrtf-proxy.ini" };

            foreach (string file in hrtfFiles)
            {
                string path = Path.Combine(binDir, file);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [RelayCommand]
        private void ShowAudioBackendInfo()
        {
            _dialogService.ShowMessage("Audio Backend Information",
                "The default OpenAL implementation in Mirror's Edge has sampling issues where the initial attack/transients of footstep sounds, " +
                "hand placements, etc. are lost due to the audio fading in. Upgrading to OpenAL Soft is highly recommended and fixes these issues in Mirror's Edge as well " +
                "as providing a noticeable boost in audio clarity.\n\nBoth OpenAL Soft options also raise the simultaneous " +
                "audio source limit from 32 to 256. The default limit of 32 causes sounds to be abruptly cut during busy scenes " +
                "with lots of gunfire, foley and ambient sources.\n\n" +
                "The HRTF option provides realistic 3D spatial audio through stereo headphones, " +
                "and utilises a special proxy that intercepts the engine's audio stream and splits the signal path so that HRTF is only applied " +
                "to actual 3D world sounds. Without the proxy, standard OpenAL Soft HRTF colours everything — including music, " +
                "dialogue, and UI effects — which can sound unnatural. With the proxy, non-spatial audio bypasses HRTF entirely " +
                "and plays back cleanly, while world-space sounds keep their full HRTF spatialisation.",
                DialogMessageType.Information);
        }
    }
}

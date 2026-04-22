using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MirrorsEdgeTweaks.Models;

namespace MirrorsEdgeTweaks.ViewModels
{
    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public class GameStatusViewModel : BaseViewModel
    {
        private string _gameDirectoryPath = "No valid directory selected.";
        private string _gameVersion = "Game Version: N/A";
        private string _configStatus = "Documents Configs: Not Found";
        private System.Windows.Media.Brush _configStatusForeground = System.Windows.Media.Brushes.OrangeRed;
        private string _status = "Ready.";
        private bool _isGameTweaksEnabled = false;

        public string GameDirectoryPath
        {
            get => _gameDirectoryPath;
            set => SetProperty(ref _gameDirectoryPath, value);
        }

        public string GameVersion
        {
            get => _gameVersion;
            set => SetProperty(ref _gameVersion, value);
        }

        public string ConfigStatus
        {
            get => _configStatus;
            set => SetProperty(ref _configStatus, value);
        }

        public System.Windows.Media.Brush ConfigStatusForeground
        {
            get => _configStatusForeground;
            set => SetProperty(ref _configStatusForeground, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public bool IsGameTweaksEnabled
        {
            get => _isGameTweaksEnabled;
            set => SetProperty(ref _isGameTweaksEnabled, value);
        }
    }

    public class FovViewModel : BaseViewModel
    {
        private string _currentFovValue = "N/A";

        public string CurrentFovValue
        {
            get => _currentFovValue;
            set => SetProperty(ref _currentFovValue, value);
        }
    }

    public class ConsoleViewModel : BaseViewModel
    {
        private string _consoleStatus = "Not Installed";
        private System.Windows.Media.Brush _consoleStatusForeground = System.Windows.Media.Brushes.Gray;
        private bool _isInstallConsoleEnabled = false;
        private bool _isUninstallConsoleEnabled = false;

        public string ConsoleStatus
        {
            get => _consoleStatus;
            set => SetProperty(ref _consoleStatus, value);
        }

        public System.Windows.Media.Brush ConsoleStatusForeground
        {
            get => _consoleStatusForeground;
            set => SetProperty(ref _consoleStatusForeground, value);
        }

        public bool IsInstallConsoleEnabled
        {
            get => _isInstallConsoleEnabled;
            set => SetProperty(ref _isInstallConsoleEnabled, value);
        }

        public bool IsUninstallConsoleEnabled
        {
            get => _isUninstallConsoleEnabled;
            set => SetProperty(ref _isUninstallConsoleEnabled, value);
        }
    }

    public class TweaksScriptsViewModel : BaseViewModel
    {
        private string _tweaksScriptsStatus = "Not Installed";
        private System.Windows.Media.Brush _tweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;

        public string TweaksScriptsStatus
        {
            get => _tweaksScriptsStatus;
            set => SetProperty(ref _tweaksScriptsStatus, value);
        }

        public System.Windows.Media.Brush TweaksScriptsStatusForeground
        {
            get => _tweaksScriptsStatusForeground;
            set => SetProperty(ref _tweaksScriptsStatusForeground, value);
        }
    }

    public class UnlockedConfigsViewModel : BaseViewModel
    {
        private string _unlockedConfigsStatus = "N/A";
        private System.Windows.Media.Brush _unlockedConfigsStatusForeground = System.Windows.Media.Brushes.Gray;
        private bool _isPatchConfigsEnabled = false;
        private bool _isUnpatchConfigsEnabled = false;

        public string UnlockedConfigsStatus
        {
            get => _unlockedConfigsStatus;
            set => SetProperty(ref _unlockedConfigsStatus, value);
        }

        public System.Windows.Media.Brush UnlockedConfigsStatusForeground
        {
            get => _unlockedConfigsStatusForeground;
            set => SetProperty(ref _unlockedConfigsStatusForeground, value);
        }

        public bool IsPatchConfigsEnabled
        {
            get => _isPatchConfigsEnabled;
            set => SetProperty(ref _isPatchConfigsEnabled, value);
        }

        public bool IsUnpatchConfigsEnabled
        {
            get => _isUnpatchConfigsEnabled;
            set => SetProperty(ref _isUnpatchConfigsEnabled, value);
        }
    }

    public class DownloadProgressViewModel : BaseViewModel
    {
        private bool _isDownloadProgressVisible = false;
        private bool _isDownloadProgressIndeterminate = false;
        private double _downloadProgressValue = 0;

        public bool IsDownloadProgressVisible
        {
            get => _isDownloadProgressVisible;
            set => SetProperty(ref _isDownloadProgressVisible, value);
        }

        public bool IsDownloadProgressIndeterminate
        {
            get => _isDownloadProgressIndeterminate;
            set => SetProperty(ref _isDownloadProgressIndeterminate, value);
        }

        public double DownloadProgressValue
        {
            get => _downloadProgressValue;
            set => SetProperty(ref _downloadProgressValue, value);
        }
    }

    public class TdGameVersionViewModel : BaseViewModel
    {
        private string _selectedTdGameVersion = "";
        private bool _isUpdatingComboBoxProgrammatically = false;

        public string SelectedTdGameVersion
        {
            get => _selectedTdGameVersion;
            set => SetProperty(ref _selectedTdGameVersion, value);
        }

        public bool IsUpdatingComboBoxProgrammatically
        {
            get => _isUpdatingComboBoxProgrammatically;
            set => SetProperty(ref _isUpdatingComboBoxProgrammatically, value);
        }
    }
}

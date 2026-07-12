using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Shared status-bar / shell state: selected directory, detected game version, config
    // detection, the status line, and the coarse UI-enabled switches.
    public partial class GameStatusViewModel : ObservableObject
    {
        [ObservableProperty] private string _gameDirectoryPath = "No valid directory selected.";
        [ObservableProperty] private string _gameVersion = "Game Version: N/A";
        [ObservableProperty] private string _configStatus = "Documents Configs: Not Found";
        [ObservableProperty] private System.Windows.Media.Brush _configStatusForeground = System.Windows.Media.Brushes.OrangeRed;
        [ObservableProperty] private string _status = "Ready. Please select your Mirror's Edge game directory.";
        [ObservableProperty] private bool _isGameTweaksEnabled = true;
        [ObservableProperty] private bool _isUiEnabled = true;
        [ObservableProperty] private bool _isMainTabEnabled = true;
        [ObservableProperty] private bool _isGameRunning;
    }
}

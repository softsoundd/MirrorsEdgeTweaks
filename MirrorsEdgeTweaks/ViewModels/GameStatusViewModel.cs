using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class GameStatusViewModel : ObservableObject
    {
        [ObservableProperty] private string _gameDirectoryPath = "No valid directory selected.";
        [ObservableProperty] private string _gameVersion = "Game Version: N/A";
        [ObservableProperty] private string _configStatus = "User Folder: Not Found";
        [ObservableProperty] private System.Windows.Media.Brush _configStatusForeground = System.Windows.Media.Brushes.OrangeRed;
        [ObservableProperty] private string _configPathTooltip = string.Empty;
        [ObservableProperty] private string _status = "Ready. Please select your Mirror's Edge game directory.";
        [ObservableProperty] private bool _isGameTweaksEnabled = true;
        [ObservableProperty] private bool _isUiEnabled = true;
        [ObservableProperty] private bool _isMainTabEnabled = true;
        [ObservableProperty] private bool _isGameRunning;
    }
}

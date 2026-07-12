using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Status of the developer-console install (Mods tab).
    public partial class ConsoleViewModel : ObservableObject
    {
        [ObservableProperty] private string _consoleStatus = "Not Installed";
        [ObservableProperty] private System.Windows.Media.Brush _consoleStatusForeground = System.Windows.Media.Brushes.Gray;
        [ObservableProperty] private bool _isInstallConsoleEnabled = false;
        [ObservableProperty] private bool _isUninstallConsoleEnabled = false;
    }
}

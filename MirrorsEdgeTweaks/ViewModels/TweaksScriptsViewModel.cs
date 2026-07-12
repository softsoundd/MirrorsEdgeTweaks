using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class TweaksScriptsViewModel : ObservableObject
    {
        [ObservableProperty] private string _tweaksScriptsStatus = "Not Installed";
        [ObservableProperty] private System.Windows.Media.Brush _tweaksScriptsStatusForeground = System.Windows.Media.Brushes.Gray;
        [ObservableProperty] private string _tweaksScriptsUIStatus = "N/A";
        [ObservableProperty] private System.Windows.Media.Brush _tweaksScriptsUIStatusForeground = System.Windows.Media.Brushes.Gray;
        [ObservableProperty] private bool _isTweaksScriptsUIInstallEnabled = false;
        [ObservableProperty] private string _tweaksScriptsUIInstallTooltip = "Install Tweaks Scripts first to enable this installer.";
        [ObservableProperty] private bool _isTweaksScriptsUIDependencyTextVisible = false;
    }
}

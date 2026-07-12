using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class UnlockedConfigsViewModel : ObservableObject
    {
        [ObservableProperty] private string _unlockedConfigsStatus = "N/A";
        [ObservableProperty] private System.Windows.Media.Brush _unlockedConfigsStatusForeground = System.Windows.Media.Brushes.Gray;
        [ObservableProperty] private bool _isPatchConfigsEnabled = false;
        [ObservableProperty] private bool _isUnpatchConfigsEnabled = false;
    }
}

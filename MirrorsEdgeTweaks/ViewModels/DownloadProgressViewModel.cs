using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class DownloadProgressViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isDownloadProgressVisible = false;
        [ObservableProperty] private bool _isDownloadProgressIndeterminate = false;
        [ObservableProperty] private double _downloadProgressValue = 0;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Shared download/progress bar state shown in the status bar.
    public partial class DownloadProgressViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isDownloadProgressVisible = false;
        [ObservableProperty] private bool _isDownloadProgressIndeterminate = false;
        [ObservableProperty] private double _downloadProgressValue = 0;
    }
}

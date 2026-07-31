using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class DownloadProgressViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isDownloadProgressVisible = false;
        [ObservableProperty] private bool _isDownloadProgressIndeterminate = false;
        [ObservableProperty] private double _downloadProgressValue = 0;

        public bool IsDownloadProgressPercentVisible =>
            IsDownloadProgressVisible && !IsDownloadProgressIndeterminate;

        partial void OnIsDownloadProgressVisibleChanged(bool value) =>
            OnPropertyChanged(nameof(IsDownloadProgressPercentVisible));

        partial void OnIsDownloadProgressIndeterminateChanged(bool value) =>
            OnPropertyChanged(nameof(IsDownloadProgressPercentVisible));
    }
}

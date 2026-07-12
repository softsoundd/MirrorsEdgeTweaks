using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Shared base for feature view models: dispatcher helpers, the global status-bar progress
    // plumbing, and a simple re-entrancy guard for long-running work (RunBusyAsync).
    public abstract class BusyViewModel : ObservableObject
    {
        protected readonly GameStatusViewModel _gameStatus;
        protected readonly DownloadProgressViewModel _downloadProgress;
        private bool _isBusy;
        protected bool IsBusy => _isBusy;

        protected BusyViewModel(GameStatusViewModel gameStatus, DownloadProgressViewModel downloadProgress)
        {
            _gameStatus = gameStatus;
            _downloadProgress = downloadProgress;
        }

        protected static void Dispatch(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        protected static void Post(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.InvokeAsync(action);
        }

        protected void ShowProgress(string message, bool isIndeterminate)
        {
            Dispatch(() =>
            {
                _gameStatus.Status = message;
                _downloadProgress.IsDownloadProgressVisible = true;
                _downloadProgress.IsDownloadProgressIndeterminate = isIndeterminate;
                if (!isIndeterminate)
                {
                    _downloadProgress.DownloadProgressValue = 0;
                }
            });
        }

        protected void HideProgress(string readyMessage = "Ready.")
        {
            Dispatch(() =>
            {
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.IsDownloadProgressIndeterminate = false;
                _downloadProgress.DownloadProgressValue = 0;
                _gameStatus.Status = readyMessage;
            });
        }

        protected async Task<bool> RunBusyAsync(string status, Action work, bool indeterminate = true, string completedStatus = "Ready.")
        {
            if (_isBusy) return false;
            _isBusy = true;
            ShowProgress(status, indeterminate);
            try
            {
                await Task.Run(work);
                return true;
            }
            finally
            {
                HideProgress(completedStatus);
                _isBusy = false;
            }
        }

        protected Action<double, string?> CreateThrottledProgressReporter(int minIntervalMs = 75)
        {
            long lastTick = 0;
            return (value, status) =>
            {
                long now = Environment.TickCount64;
                if (now - lastTick < minIntervalMs) return;
                lastTick = now;
                Post(() =>
                {
                    _downloadProgress.DownloadProgressValue = value;
                    if (status != null)
                    {
                        _gameStatus.Status = status;
                    }
                });
            };
        }
    }
}

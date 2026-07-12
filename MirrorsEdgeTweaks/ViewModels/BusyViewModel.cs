using CommunityToolkit.Mvvm.ComponentModel;

namespace MirrorsEdgeTweaks.ViewModels
{
    public abstract class BusyViewModel : ObservableObject
    {
        protected readonly GameSession _session;
        protected readonly GameStatusViewModel _gameStatus;
        protected readonly DownloadProgressViewModel _downloadProgress;
        private readonly ApplyGate _applyGate;
        private bool _isBusy;
        protected bool IsBusy => _isBusy;

        protected BusyViewModel(GameSession session, GameStatusViewModel gameStatus, DownloadProgressViewModel downloadProgress)
        {
            _session = session;
            _applyGate = session.ApplyGate;
            _gameStatus = gameStatus;
            _downloadProgress = downloadProgress;
        }

        // Checked at enqueue time so deferred work is not scheduled during load/startup.
        protected virtual bool IsApplySuppressed => _session.IsProcessingGameDirectory;

        protected void EnqueueApply(Func<Task> work)
        {
            if (IsApplySuppressed)
                return;

            _applyGate.Enqueue(work);
        }

        protected void EnqueueApply(Action work)
        {
            if (IsApplySuppressed)
                return;

            _applyGate.Enqueue(work);
        }

        protected Task RunApplyAsync(Func<Task> work)
        {
            if (IsApplySuppressed)
                return Task.CompletedTask;

            return _applyGate.RunAsync(work);
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

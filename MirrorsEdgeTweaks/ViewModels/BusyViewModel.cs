using CommunityToolkit.Mvvm.ComponentModel;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;
using System.Threading;

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
                await Task.Run(work).ConfigureAwait(false);
                return true;
            }
            finally
            {
                HideProgress(completedStatus);
                _isBusy = false;
            }
        }

        private sealed class CoalescedProgressReporter
        {
            private readonly DownloadProgressViewModel _downloadProgress;
            private readonly Action<Action> _post;
            private readonly Action<Action> _dispatch;

            private double _latestValue;
            private int _lastDisplayedPercent = -1;
            private int _updateScheduled;

            public CoalescedProgressReporter(
                DownloadProgressViewModel downloadProgress,
                Action<Action> post,
                Action<Action> dispatch)
            {
                _downloadProgress = downloadProgress;
                _post = post;
                _dispatch = dispatch;
            }

            public void Report(double value)
            {
                _latestValue = value;

                int percent = (int)value;
                if (percent <= _lastDisplayedPercent && value < 100)
                    return;

                EnsureUpdateScheduled();
            }

            public void Flush()
            {
                _dispatch(() =>
                {
                    _downloadProgress.DownloadProgressValue = _latestValue;
                    _lastDisplayedPercent = (int)_latestValue;
                });
            }

            private void EnsureUpdateScheduled()
            {
                if (Interlocked.CompareExchange(ref _updateScheduled, 1, 0) != 0)
                    return;

                _post(() =>
                {
                    try
                    {
                        ApplyLatestIfDue();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _updateScheduled, 0);

                        if ((int)_latestValue > _lastDisplayedPercent
                            || (_latestValue >= 100 && _downloadProgress.DownloadProgressValue < _latestValue))
                        {
                            EnsureUpdateScheduled();
                        }
                    }
                });
            }

            private void ApplyLatestIfDue()
            {
                int percent = (int)_latestValue;
                if (percent <= _lastDisplayedPercent && _latestValue < 100)
                    return;

                _lastDisplayedPercent = percent;
                _downloadProgress.DownloadProgressValue = _latestValue;
            }
        }

        protected async Task RunDownloadAndExtractAsync(
            IDownloadService download,
            IFileService fileService,
            string url,
            string extractPath,
            string statusPrefix,
            Func<string, string, Task>? customExtract = null,
            Func<Task>? afterExtract = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            ArgumentException.ThrowIfNullOrWhiteSpace(extractPath);

            string tempZipPath = Path.Combine(fileService.GetTempPath(), $"metweaks_{Guid.NewGuid():N}.zip");

            try
            {
                ShowProgress($"Downloading {statusPrefix}...", true);

                var report = new CoalescedProgressReporter(_downloadProgress, Post, Dispatch);
                bool determinateSet = false;
                await download.DownloadToFileAsync(url, tempZipPath, p =>
                {
                    if (p < 0)
                    {
                        Post(() => _downloadProgress.IsDownloadProgressIndeterminate = true);
                        return;
                    }

                    if (!determinateSet)
                    {
                        determinateSet = true;
                        Post(() => _downloadProgress.IsDownloadProgressIndeterminate = false);
                    }

                    report.Report(p);
                }, cancellationToken).ConfigureAwait(false);

                report.Flush();

                ShowProgress($"Extracting {statusPrefix}...", true);

                if (customExtract != null)
                {
                    await customExtract(tempZipPath, extractPath).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => ZipFile.ExtractToDirectory(tempZipPath, extractPath, overwriteFiles: true), cancellationToken).ConfigureAwait(false);
                }

                if (afterExtract != null)
                    await afterExtract().ConfigureAwait(false);
            }
            finally
            {
                HideProgress();
                if (fileService.FileExists(tempZipPath))
                    fileService.DeleteFile(tempZipPath);
            }
        }
    }
}

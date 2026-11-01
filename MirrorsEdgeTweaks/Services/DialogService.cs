using MaterialDesignThemes.Wpf;
using MirrorsEdgeTweaks.Helpers;

namespace MirrorsEdgeTweaks.Services
{
    public enum DialogMessageType
    {
        Information,
        Warning,
        Error,
        Success
    }

    public interface IDialogService
    {
        void ShowMessage(string title, string message, DialogMessageType messageType = DialogMessageType.Information);
        Task ShowMessageAsync(string title, string message, DialogMessageType messageType = DialogMessageType.Information);
        Task<bool> ShowConfirmationAsync(string title, string message);
        Task<object?> ShowDialogAsync(object content);
    }

    public class DialogService : IDialogService
    {
        // The RootDialog DialogHost shows one dialog at a time, so a dialog requested while another is open waits for it to close.
        private readonly SemaphoreSlim _dialogGate = new(1, 1);

        // Lets callers without an async context show a message.
        // Exceptions are caught, so a failed dialog cannot crash the process.
        public void ShowMessage(string title, string message, DialogMessageType messageType = DialogMessageType.Information)
        {
            _ = ShowMessageSafeAsync(title, message, messageType);
        }

        private async Task ShowMessageSafeAsync(string title, string message, DialogMessageType messageType)
        {
            try
            {
                await ShowMessageAsync(title, message, messageType);
            }
            catch
            {
            }
        }

        public async Task ShowMessageAsync(string title, string message, DialogMessageType messageType = DialogMessageType.Information)
        {
            await ShowCoreAsync(() => new MessageDialog(title, message, messageType));
        }

        public async Task<bool> ShowConfirmationAsync(string title, string message)
        {
            object? result = await ShowCoreAsync(() => new ConfirmationDialog(title, message));
            return result is bool boolResult && boolResult;
        }

        public async Task<object?> ShowDialogAsync(object content)
        {
            return await ShowCoreAsync(() => content);
        }

        // Waits for any open dialog to close, then shows the new one on the UI thread.
        // The content factory also runs on the UI thread, because dialog controls must be created there.
        private async Task<object?> ShowCoreAsync(Func<object> contentFactory)
        {
            await _dialogGate.WaitAsync();
            try
            {
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (dispatcher.CheckAccess())
                {
                    return await DialogHost.Show(contentFactory(), "RootDialog");
                }

                // The outer await waits for the call to run on the UI thread, and the inner await waits for the dialog to close.
                return await await dispatcher.InvokeAsync(() => DialogHost.Show(contentFactory(), "RootDialog"));
            }
            finally
            {
                _dialogGate.Release();
            }
        }
    }
}

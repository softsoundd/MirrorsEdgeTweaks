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

    // Injectable abstraction over MaterialDesign's DialogHost ("RootDialog") so view models can show
    // dialogs without a direct dependency on WPF dialog plumbing (and can be faked in tests).
    public interface IDialogService
    {
        void ShowMessage(string title, string message, DialogMessageType messageType = DialogMessageType.Information);
        Task ShowMessageAsync(string title, string message, DialogMessageType messageType = DialogMessageType.Information);
        Task<bool> ShowConfirmationAsync(string title, string message);
        Task<object?> ShowDialogAsync(object content);
    }

    public class DialogService : IDialogService
    {
        // Serializes access to the single "RootDialog" DialogHost. Dialogs requested while another
        // is open queue up and display when it closes, instead of being silently dropped.
        private readonly SemaphoreSlim _dialogGate = new(1, 1);

        // Fire-and-forget variant for call sites without an async context. Exceptions are observed
        // here so a failed dialog can never crash the process (unlike the previous async void).
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
                // Nothing sensible left to do if the dialog host itself failed; swallowing here is
                // intentional so fire-and-forget messages cannot take the app down.
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

        // Waits for any open dialog to close, then shows the new one on the UI thread. The content
        // factory also runs on the UI thread because dialog controls must be created there.
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

                // InvokeAsync returns a DispatcherOperation<Task<object?>>; the outer await marshals
                // onto the UI thread, the inner await completes when the dialog actually closes.
                return await await dispatcher.InvokeAsync(() => DialogHost.Show(contentFactory(), "RootDialog"));
            }
            finally
            {
                _dialogGate.Release();
            }
        }
    }
}

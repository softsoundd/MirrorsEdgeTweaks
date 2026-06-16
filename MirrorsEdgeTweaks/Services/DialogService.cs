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
        private bool _isDialogOpen = false;
        private readonly object _dialogLock = new object();

        public async void ShowMessage(string title, string message, DialogMessageType messageType = DialogMessageType.Information)
        {
            await ShowMessageAsync(title, message, messageType);
        }

        public async Task ShowMessageAsync(string title, string message, DialogMessageType messageType = DialogMessageType.Information)
        {
            lock (_dialogLock)
            {
                if (_isDialogOpen)
                {
                    return;
                }
                _isDialogOpen = true;
            }

            try
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await DialogHost.Show(new MessageDialog(title, message, messageType), "RootDialog");
                });
            }
            finally
            {
                lock (_dialogLock)
                {
                    _isDialogOpen = false;
                }
            }
        }

        public async Task<bool> ShowConfirmationAsync(string title, string message)
        {
            lock (_dialogLock)
            {
                if (_isDialogOpen)
                {
                    return false;
                }
                _isDialogOpen = true;
            }

            try
            {
                var result = await DialogHost.Show(new ConfirmationDialog(title, message), "RootDialog");
                return result is bool boolResult && boolResult;
            }
            finally
            {
                lock (_dialogLock)
                {
                    _isDialogOpen = false;
                }
            }
        }

        public async Task<object?> ShowDialogAsync(object content)
        {
            return await DialogHost.Show(content, "RootDialog");
        }
    }
}

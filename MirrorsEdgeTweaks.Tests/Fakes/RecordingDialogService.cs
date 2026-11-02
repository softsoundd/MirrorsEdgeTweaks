using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    public sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message, DialogMessageType Type)> Messages { get; } = new();

        public void ShowMessage(string title, string message, DialogMessageType messageType = DialogMessageType.Information, string? details = null) =>
            Messages.Add((title, message, messageType));

        public Task ShowMessageAsync(string title, string message, DialogMessageType messageType = DialogMessageType.Information, string? details = null)
        {
            ShowMessage(title, message, messageType);
            return Task.CompletedTask;
        }

        public void ShowError(string summary, Exception exception, string title = "Error", DialogMessageType messageType = DialogMessageType.Error) =>
            Messages.Add((title, summary, messageType));

        public Task ShowErrorAsync(string summary, Exception exception, string title = "Error", DialogMessageType messageType = DialogMessageType.Error)
        {
            ShowError(summary, exception, title, messageType);
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(false);

        public Task<object?> ShowDialogAsync(object content) => Task.FromResult<object?>(null);
    }
}

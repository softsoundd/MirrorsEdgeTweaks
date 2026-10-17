using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    public sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message, DialogMessageType Type)> Messages { get; } = new();

        public void ShowMessage(string title, string message, DialogMessageType messageType = DialogMessageType.Information) =>
            Messages.Add((title, message, messageType));

        public Task ShowMessageAsync(string title, string message, DialogMessageType messageType = DialogMessageType.Information)
        {
            ShowMessage(title, message, messageType);
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(false);

        public Task<object?> ShowDialogAsync(object content) => Task.FromResult<object?>(null);
    }
}

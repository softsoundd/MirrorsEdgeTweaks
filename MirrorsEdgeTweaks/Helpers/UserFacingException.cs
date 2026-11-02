namespace MirrorsEdgeTweaks.Helpers
{
    // Error dialogs show this exception's message as is, so it must be written for players.
    public class UserFacingException : InvalidOperationException
    {
        public UserFacingException(string message) : base(message) { }

        public UserFacingException(string message, Exception innerException) : base(message, innerException) { }
    }
}

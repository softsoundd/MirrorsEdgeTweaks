using System.IO;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class ErrorMessageHelper
    {
        public static string Explain(Exception exception) => exception switch
        {
            _ when ShowsOwnMessage(exception) => exception.Message,
            UnauthorizedAccessException =>
                "Windows denied access to a file. Make sure Mirror's Edge is closed and that antivirus software " +
                "is not blocking Mirror's Edge Tweaks, then try again.",
            IOException =>
                "A file could not be read or changed. Make sure Mirror's Edge is closed and no other program is using its files, then try again.",
            HttpRequestException or TaskCanceledException =>
                "The download failed. Check your internet connection and try again.",
            _ =>
                "A game file may be from an unsupported version of the game, or another tool may have changed it. " +
                "Verifying your game files usually fixes this. If the problem continues, please report it with the technical details below.",
        };

        // Skips a message that Explain already shows, so the technical details do not repeat it.
        public static string? Details(Exception exception) =>
            ShowsOwnMessage(exception) ? exception.InnerException?.Message : exception.Message;

        // Explain shows the messages of these exceptions as is, so they must be written for players.
        private static bool ShowsOwnMessage(Exception exception) =>
            exception is UserFacingException or FileNotFoundException or DirectoryNotFoundException;
    }
}

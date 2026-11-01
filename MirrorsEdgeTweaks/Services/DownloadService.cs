using System.IO;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Services
{
    public interface IDownloadService
    {
        // onProgress receives percentages from 0 to 100.
        // It receives a single -1 when the server reports no content length, so callers can show indeterminate progress.
        Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default);
    }

    public class DownloadService : IDownloadService
    {
        // Every download shares this client, because a client per request wastes pooled connections.
        // The long timeout allows for large mod archives on slow connections.
        // Downloads use ResponseHeadersRead, so the timeout does not cover their body transfer.
        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        public async Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            if (!totalBytes.HasValue)
            {
                onProgress?.Invoke(-1);
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            long totalRead = 0;
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                onProgress?.Invoke((double)totalRead / totalBytes.Value * 100);
            }
        }
    }
}

using System.IO;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Services
{
    public interface IDownloadService
    {
        // onProgress receives 0-100, or a single -1 when the server reports no content length
        // (callers can switch their progress bar to indeterminate).
        Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default);
    }

    public class DownloadService : IDownloadService
    {
        // One shared client for every download in the app (sockets are pooled; creating a client
        // per request wastes connections). The generous timeout covers large mod zips on slow
        // connections; ResponseHeadersRead means it does not include the body transfer for the
        // streaming paths.
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

using System.Globalization;
using System.IO;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Services
{
    public interface IDownloadService
    {
        // onProgress receives percentages from 0 to 100.
        // It receives a single -1 when the server reports no content length, so callers can show indeterminate progress.
        // Returns the response's ETag for a later GetValidatorAsync call to compare.
        // Returns its Last-Modified date when it has no ETag.
        // Returns null when it has neither.
        Task<string?> DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default);

        // Reads the same validator with a HEAD request.
        // Returns null when the request fails.
        Task<string?> GetValidatorAsync(string url, CancellationToken cancellationToken = default);
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

        private static readonly TimeSpan ValidatorTimeout = TimeSpan.FromSeconds(10);

        public async Task<string?> DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string? validator = ValidatorOf(response);
            long? totalBytes = response.Content.Headers.ContentLength;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            if (!totalBytes.HasValue)
            {
                onProgress?.Invoke(-1);
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                return validator;
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
            return validator;
        }

        public async Task<string?> GetValidatorAsync(string url, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ValidatorTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? ValidatorOf(response) : null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        private static string? ValidatorOf(HttpResponseMessage response) =>
            response.Headers.ETag?.ToString()
            ?? response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
    }
}

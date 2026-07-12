using System.IO;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Services
{
    public interface IDownloadService
    {
        Task<byte[]> DownloadFileAsync(string url, IProgress<int>? progress = null);
        Task DownloadAndExtractZipAsync(string url, string extractPath, IProgress<int>? progress = null);

        // Streams the response directly to a file. onProgress receives 0-100 while the content
        // length is known, or a single -1 when the server does not report a length (callers can
        // switch their progress bar to indeterminate).
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

        private readonly IFileService _fileService;

        public DownloadService(IFileService fileService)
        {
            _fileService = fileService;
        }

        public async Task<byte[]> DownloadFileAsync(string url, IProgress<int>? progress = null)
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();

            if (totalBytes.HasValue)
            {
                long totalBytesRead = 0;
                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await memoryStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    int progressPercentage = (int)((double)totalBytesRead / totalBytes.Value * 100);
                    progress?.Report(progressPercentage);
                }
            }
            else
            {
                await stream.CopyToAsync(memoryStream);
            }

            return memoryStream.ToArray();
        }

        public async Task DownloadToFileAsync(string url, string destinationPath, Action<double>? onProgress = null, CancellationToken cancellationToken = default)
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            if (!totalBytes.HasValue)
            {
                onProgress?.Invoke(-1);
                await stream.CopyToAsync(fileStream, cancellationToken);
                return;
            }

            long totalRead = 0;
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                onProgress?.Invoke((double)totalRead / totalBytes.Value * 100);
            }
        }

        public async Task DownloadAndExtractZipAsync(string url, string extractPath, IProgress<int>? progress = null)
        {
            string tempZipPath = Path.Combine(_fileService.GetTempPath(), $"temp_{Guid.NewGuid()}.zip");

            try
            {
                await DownloadToFileAsync(url, tempZipPath, p => { if (p >= 0) progress?.Report((int)p); });

                System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, extractPath, true);
            }
            finally
            {
                if (_fileService.FileExists(tempZipPath))
                {
                    _fileService.DeleteFile(tempZipPath);
                }
            }
        }
    }
}

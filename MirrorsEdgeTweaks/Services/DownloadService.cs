using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MirrorsEdgeTweaks.Services
{
    public interface IDownloadService
    {
        Task<byte[]> DownloadFileAsync(string url, IProgress<int>? progress = null);
        Task DownloadAndExtractZipAsync(string url, string extractPath, IProgress<int>? progress = null);
    }

    public class DownloadService : IDownloadService
    {
        private readonly IFileService _fileService;

        public DownloadService(IFileService fileService)
        {
            _fileService = fileService;
        }

        public async Task<byte[]> DownloadFileAsync(string url, IProgress<int>? progress = null)
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
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

        public async Task DownloadAndExtractZipAsync(string url, string extractPath, IProgress<int>? progress = null)
        {
            string tempZipPath = Path.Combine(_fileService.GetTempPath(), $"temp_{Guid.NewGuid()}.zip");

            try
            {
                byte[] zipBytes = await DownloadFileAsync(url, progress);
                await _fileService.WriteAllBytesAsync(tempZipPath, zipBytes);

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

using System;
using System.IO;
using System.Threading.Tasks;
using MirrorsEdgeTweaks.Models;
using UELib;

namespace MirrorsEdgeTweaks.Services
{
    public interface IPackageService
    {
        UnrealPackage? LoadPackage(string path);
        void DisposePackage(UnrealPackage? package);
        bool IsValidGameDirectory(string path);
    }

    public class PackageService : IPackageService
    {
        public UnrealPackage? LoadPackage(string path)
        {
            try
            {
                var package = UnrealLoader.LoadPackage(path, FileAccess.Read);
                package?.InitializePackage();
                return package;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void DisposePackage(UnrealPackage? package)
        {
            package?.Dispose();
        }

        public bool IsValidGameDirectory(string path)
        {
            return File.Exists(Path.Combine(path, "TdGame", "CookedPC", "Engine.u")) &&
                   File.Exists(Path.Combine(path, "TdGame", "CookedPC", "TdGame.u"));
        }
    }

    public interface IFileService
    {
        Task<string> ReadAllTextAsync(string path);
        Task WriteAllTextAsync(string path, string content);
        Task<byte[]> ReadAllBytesAsync(string path);
        Task WriteAllBytesAsync(string path, byte[] bytes);
        bool FileExists(string path);
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        void DeleteFile(string path);
        byte[] ReadAllBytes(string path);
        void WriteAllBytes(string path, byte[] bytes);
        void DeleteDirectory(string path, bool recursive = false);
        string GetTempPath();
        string CombinePaths(params string[] paths);
    }

    public class FileService : IFileService
    {
        public async Task<string> ReadAllTextAsync(string path)
        {
            return await File.ReadAllTextAsync(path);
        }

        public async Task WriteAllTextAsync(string path, string content)
        {
            await File.WriteAllTextAsync(path, content);
        }

        public async Task<byte[]> ReadAllBytesAsync(string path)
        {
            return await File.ReadAllBytesAsync(path);
        }

        public async Task WriteAllBytesAsync(string path, byte[] bytes)
        {
            await File.WriteAllBytesAsync(path, bytes);
        }

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void DeleteFile(string path)
        {
            File.Delete(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
        }

        public void DeleteDirectory(string path, bool recursive = false)
        {
            Directory.Delete(path, recursive);
        }

        public string GetTempPath()
        {
            return Path.GetTempPath();
        }

        public string CombinePaths(params string[] paths)
        {
            return Path.Combine(paths);
        }
    }

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
            using var client = new System.Net.Http.HttpClient();
            using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();

            if (totalBytes.HasValue)
            {
                var totalBytesRead = 0L;
                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await memoryStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    var progressPercentage = (int)((double)totalBytesRead / totalBytes.Value * 100);
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
            var tempZipPath = Path.Combine(_fileService.GetTempPath(), $"temp_{Guid.NewGuid()}.zip");
            
            try
            {
                var zipBytes = await DownloadFileAsync(url, progress);
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

    public interface IDecompressionService
    {
        string GetDecompressorPath();
        void ExtractDecompressor();
        void RunDecompressor(string packagePath);
    }

    public class DecompressionService : IDecompressionService
    {
        private readonly IFileService _fileService;

        public DecompressionService(IFileService fileService)
        {
            _fileService = fileService;
        }

        public string GetDecompressorPath()
        {
            const string decompressorExe = "decompress.exe";
            string tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MirrorsEdgeTweaks");
            return Path.Combine(tempDir, decompressorExe);
        }

        public void ExtractDecompressor()
        {
            string exePath = GetDecompressorPath();
            string? directoryPath = Path.GetDirectoryName(exePath);

            if (_fileService.FileExists(exePath))
            {
                return;
            }

            if (directoryPath != null)
            {
                _fileService.CreateDirectory(directoryPath);
            }

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            const string decompressorExe = "decompress.exe";
            string resourceName = $"MirrorsEdgeTweaks.{decompressorExe}";

            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Could not find the embedded 'decompress.exe'. The application cannot continue.");
                }

                using (FileStream fileStream = new FileStream(exePath, FileMode.Create))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        public void RunDecompressor(string packagePath)
        {
            string decompressorExe = GetDecompressorPath();
            if (!_fileService.FileExists(decompressorExe))
            {
                throw new FileNotFoundException("Decompressor is missing.");
            }

            string? packageDirectory = Path.GetDirectoryName(packagePath);
            if (string.IsNullOrEmpty(packageDirectory))
            {
                throw new ArgumentException("Invalid package path for decompression.");
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = decompressorExe,
                Arguments = $"\"{packagePath}\"",
                WorkingDirectory = packageDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                process?.WaitForExit();
            }
        }
    }
}

using MirrorsEdgeTweaks.Helpers;
using System.IO;
using System.Net.Http;
using System.Text;

namespace MirrorsEdgeTweaks.Services
{
    public interface IAssetUrlProvider
    {
        string For(string fileName);

        string AssetBase { get; }

        Task EnsureLoadedAsync(CancellationToken cancellationToken = default);
    }

    public class AssetUrlProvider : IAssetUrlProvider
    {
        private static readonly HttpClient ManifestClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly string _cacheFilePath;
        private readonly string _bootstrapUrl;
        private readonly object _loadLock = new();
        private string _assetBase = DownloadUrls.DefaultAssetBase;
        private Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
        private Task? _loadTask;

        public AssetUrlProvider()
            : this(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MirrorsEdgeTweaks",
                    "asset-manifest.json"),
                DownloadUrls.ManifestBootstrapUrl)
        {
        }

        internal AssetUrlProvider(string cacheFilePath, string bootstrapUrl)
        {
            string? cacheDir = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDir))
                Directory.CreateDirectory(cacheDir);

            _cacheFilePath = cacheFilePath;
            _bootstrapUrl = bootstrapUrl;
        }

        public string AssetBase => _assetBase;

        public string For(string fileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

            if (_overrides.TryGetValue(fileName, out string? overrideUrl))
                return overrideUrl;

            return _assetBase + fileName;
        }

        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
        {
            lock (_loadLock)
            {
                if (_loadTask is { IsCompleted: true, IsFaulted: true }
                    or { IsCompleted: true, IsCanceled: true })
                {
                    _loadTask = null;
                }

                _loadTask ??= LoadInternalAsync(cancellationToken);
                return _loadTask;
            }
        }

        internal void ApplyManifestForTesting(string json, bool persistCache = false)
        {
            if (!TryApplyManifest(json, persistCache))
                throw new InvalidOperationException("Invalid manifest JSON.");
        }

        private async Task LoadInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                string? fetchedJson = await TryFetchManifestAsync(cancellationToken);
                if (fetchedJson != null && TryApplyManifest(fetchedJson, persistCache: true))
                    return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Network or parse errors fall through to cache/default.
            }

            if (TryLoadCachedManifest())
                return;

            ApplyDefaults();
        }

        private async Task<string?> TryFetchManifestAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var response = await ManifestClient.GetAsync(_bootstrapUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private bool TryLoadCachedManifest()
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                    return false;

                string cachedJson = File.ReadAllText(_cacheFilePath, Encoding.UTF8);
                return TryApplyManifest(cachedJson, persistCache: false);
            }
            catch
            {
                return false;
            }
        }

        private bool TryApplyManifest(string json, bool persistCache)
        {
            var manifest = AssetManifest.TryParse(json);
            string? assetBase = manifest?.GetValidatedAssetBase();
            if (assetBase == null)
                return false;

            _assetBase = assetBase;
            _overrides = manifest!.Overrides != null
                ? manifest.Overrides
                    .Select(kvp => (kvp.Key, Url: AssetManifest.ValidateHttpsUrl(kvp.Value)))
                    .Where(entry => entry.Url != null)
                    .ToDictionary(entry => entry.Key, entry => entry.Url!, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (persistCache)
            {
                try
                {
                    File.WriteAllText(_cacheFilePath, json, Encoding.UTF8);
                }
                catch
                {
                    // Cache write failure should not block using the fetched manifest.
                }
            }

            return true;
        }

        private void ApplyDefaults()
        {
            _assetBase = DownloadUrls.DefaultAssetBase;
            _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

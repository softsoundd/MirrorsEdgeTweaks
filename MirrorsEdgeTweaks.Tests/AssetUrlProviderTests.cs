using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class AssetManifestTests
    {
        [Fact]
        public void TryParse_ValidManifest_ReturnsAssetBase()
        {
            const string json = """
                {
                  "version": 1,
                  "assetBase": "https://cdn.example.com/metweaks/",
                  "updated": "2026-07-13T00:00:00Z"
                }
                """;

            var manifest = AssetManifest.TryParse(json);

            Assert.NotNull(manifest);
            Assert.Equal("https://cdn.example.com/metweaks/", manifest!.GetValidatedAssetBase());
        }

        [Fact]
        public void TryParse_AddsTrailingSlashWhenMissing()
        {
            const string json = """{"version":1,"assetBase":"https://cdn.example.com/metweaks"}""";

            var manifest = AssetManifest.TryParse(json);

            Assert.Equal("https://cdn.example.com/metweaks/", manifest!.GetValidatedAssetBase());
        }

        [Fact]
        public void TryParse_RejectsNonHttpsBase()
        {
            const string json = """{"version":1,"assetBase":"http://insecure.example.com/metweaks/"}""";

            var manifest = AssetManifest.TryParse(json);

            Assert.Null(manifest?.GetValidatedAssetBase());
        }

        [Fact]
        public void TryParse_InvalidJsonReturnsNull()
        {
            Assert.Null(AssetManifest.TryParse("{not json"));
        }
    }

    public class AssetUrlProviderTests
    {
        [Fact]
        public void For_UsesDefaultBaseBeforeManifestLoad()
        {
            var provider = new AssetUrlProvider();

            Assert.Equal(DownloadUrls.DefaultAssetBase + "OpenAL.zip", provider.For("OpenAL.zip"));
        }

        [Fact]
        public void ApplyManifestForTesting_UpdatesResolvedUrls()
        {
            using var temp = new TempDirectory();
            string cachePath = Path.Combine(temp.Path, "asset-manifest.json");
            var provider = new AssetUrlProvider(cachePath, "https://example.invalid/manifest.json");

            provider.ApplyManifestForTesting("""{"version":1,"assetBase":"https://cdn.example.com/assets/"}""");

            Assert.Equal("https://cdn.example.com/assets/", provider.AssetBase);
            Assert.Equal("https://cdn.example.com/assets/CZE.zip", provider.For("CZE.zip"));
        }

        [Fact]
        public void ApplyManifestForTesting_UsesPerAssetOverrides()
        {
            using var temp = new TempDirectory();
            string cachePath = Path.Combine(temp.Path, "asset-manifest.json");
            var provider = new AssetUrlProvider(cachePath, "https://example.invalid/manifest.json");

            provider.ApplyManifestForTesting("""
                {
                  "version": 2,
                  "assetBase": "https://cdn.example.com/assets/",
                  "overrides": {
                    "MirrorsEdgeConsole.zip": "https://special.example.com/MirrorsEdgeConsole.zip"
                  }
                }
                """);

            Assert.Equal("https://special.example.com/MirrorsEdgeConsole.zip", provider.For("MirrorsEdgeConsole.zip"));
            Assert.Equal("https://cdn.example.com/assets/OpenAL.zip", provider.For("OpenAL.zip"));
        }

        [Fact]
        public void ApplyManifestForTesting_IgnoresInvalidOverrideUrls()
        {
            using var temp = new TempDirectory();
            string cachePath = Path.Combine(temp.Path, "asset-manifest.json");
            var provider = new AssetUrlProvider(cachePath, "https://example.invalid/manifest.json");

            provider.ApplyManifestForTesting("""
                {
                  "version": 2,
                  "assetBase": "https://cdn.example.com/assets/",
                  "overrides": {
                    "MirrorsEdgeConsole.zip": "http://insecure.example.com/MirrorsEdgeConsole.zip"
                  }
                }
                """);

            Assert.Equal("https://cdn.example.com/assets/MirrorsEdgeConsole.zip", provider.For("MirrorsEdgeConsole.zip"));
        }

        [Fact]
        public async Task EnsureLoadedAsync_FallsBackToCachedManifestWhenFetchFails()
        {
            using var temp = new TempDirectory();
            string cachePath = Path.Combine(temp.Path, "asset-manifest.json");
            await File.WriteAllTextAsync(cachePath, """{"version":1,"assetBase":"https://cached.example.com/assets/"}""", CancellationToken.None);

            var provider = new AssetUrlProvider(cachePath, "https://example.invalid/manifest.json");
            await provider.EnsureLoadedAsync(CancellationToken.None);

            Assert.Equal("https://cached.example.com/assets/", provider.AssetBase);
        }

        [Fact]
        public async Task EnsureLoadedAsync_FallsBackToDefaultWhenFetchAndCacheFail()
        {
            using var temp = new TempDirectory();
            string cachePath = Path.Combine(temp.Path, "asset-manifest.json");
            var provider = new AssetUrlProvider(cachePath, "https://example.invalid/manifest.json");

            await provider.EnsureLoadedAsync(CancellationToken.None);

            Assert.Equal(DownloadUrls.DefaultAssetBase, provider.AssetBase);
        }
    }
}

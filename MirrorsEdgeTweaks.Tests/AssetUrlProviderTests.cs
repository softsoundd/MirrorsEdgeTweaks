using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class AssetManifestTests
    {
        internal const string OtherRelease = "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets-2/";

        [Fact]
        public void TryParse_ValidManifest_ReturnsAssetBase()
        {
            const string json = """
                {
                  "version": 1,
                  "assetBase": "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets-2/",
                  "updated": "2026-07-13T00:00:00Z"
                }
                """;

            var manifest = AssetManifest.TryParse(json);

            Assert.NotNull(manifest);
            Assert.Equal(OtherRelease, manifest!.GetValidatedAssetBase());
        }

        [Fact]
        public void TryParse_AddsTrailingSlashWhenMissing()
        {
            const string json = """{"version":1,"assetBase":"https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets-2"}""";

            var manifest = AssetManifest.TryParse(json);

            Assert.Equal(OtherRelease, manifest!.GetValidatedAssetBase());
        }

        [Fact]
        public void TryParse_RejectsNonHttpsBase()
        {
            const string json = """{"version":1,"assetBase":"http://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets-2/"}""";

            var manifest = AssetManifest.TryParse(json);

            Assert.Null(manifest?.GetValidatedAssetBase());
        }

        [Fact]
        public void TryParse_InvalidJsonReturnsNull()
        {
            Assert.Null(AssetManifest.TryParse("{not json"));
        }

        [Theory]
        [InlineData("https://cdn.example.com/metweaks/")]
        [InlineData("https://raw.githubusercontent.com/softsoundd/MirrorsEdgeTweaks/main/")]
        [InlineData("https://github.com/someone-else/MirrorsEdgeTweaks/releases/download/runtime-assets/")]
        [InlineData("https://github.com/softsoundd/AnotherRepository/releases/download/runtime-assets/")]
        [InlineData("https://github.com.example.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/")]
        [InlineData("https://github.com@example.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/")]
        [InlineData("https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/../../../../someone-else/repo/releases/download/x/")]
        [InlineData("https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/%2e%2e/%2e%2e/%2e%2e/%2e%2e/someone-else/repo/x/")]
        [InlineData("https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/..%2f..%2f..%2f..%2fsomeone-else/repo/x/")]
        [InlineData("https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/..%5C..%5C..%5C..%5Csomeone-else/repo/x/")]
        [InlineData("file:///C:/Windows/")]
        [InlineData("not a url")]
        [InlineData("")]
        public void ValidateAssetUrl_RejectsAUrlOutsideTheTrustedPrefixes(string url)
        {
            Assert.Null(AssetManifest.ValidateAssetUrl(url));
            Assert.Null(AssetManifest.ValidateAssetUrl(url, requireTrailingSlash: true));
        }

        [Theory]
        [InlineData(
            "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/OpenAL.zip",
            "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/OpenAL.zip")]
        [InlineData(
            "HTTPS://GitHub.com:443/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/OpenAL.zip",
            "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/OpenAL.zip")]
        [InlineData(
            "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/old/../runtime-assets/OpenAL.zip",
            "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/OpenAL.zip")]
        public void ValidateAssetUrl_ReturnsATrustedUrlInItsNormalisedForm(string url, string expected)
        {
            Assert.Equal(expected, AssetManifest.ValidateAssetUrl(url));
        }

        // Without its final slash, a prefix would also match a longer host or folder name that starts the same way.
        [Fact]
        public void TrustedAssetPrefixes_AreHttpsAndEndWithASlash()
        {
            Assert.NotEmpty(DownloadUrls.TrustedAssetPrefixes);
            Assert.All(DownloadUrls.TrustedAssetPrefixes, prefix =>
            {
                Assert.StartsWith("https://", prefix);
                Assert.EndsWith("/", prefix);
            });
        }

        [Fact]
        public void ValidateAssetUrl_AcceptsTheDefaultAssetBase()
        {
            Assert.Equal(
                DownloadUrls.DefaultAssetBase,
                AssetManifest.ValidateAssetUrl(DownloadUrls.DefaultAssetBase, requireTrailingSlash: true));
        }

        [Fact]
        public void TheRepositoryManifest_IsOneThatTheAppAccepts()
        {
            string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets-manifest.json"));

            var manifest = AssetManifest.TryParse(json);

            Assert.NotNull(manifest);
            Assert.NotNull(manifest!.GetValidatedAssetBase());
            foreach (var (fileName, url) in manifest.Overrides ?? new Dictionary<string, string>())
                Assert.True(AssetManifest.ValidateAssetUrl(url) != null, $"The override for {fileName} is not under a trusted prefix: {url}");
        }
    }

    public class AssetUrlProviderTests
    {
        private const string OtherRelease = AssetManifestTests.OtherRelease;

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
            var provider = CreateProvider(temp);

            provider.ApplyManifestForTesting($$"""{"version":1,"assetBase":"{{OtherRelease}}"}""");

            Assert.Equal(OtherRelease, provider.AssetBase);
            Assert.Equal(OtherRelease + "CZE.zip", provider.For("CZE.zip"));
        }

        [Fact]
        public void ApplyManifestForTesting_UsesPerAssetOverrides()
        {
            using var temp = new TempDirectory();
            var provider = CreateProvider(temp);
            const string consoleUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/console/MirrorsEdgeConsole.zip";

            provider.ApplyManifestForTesting($$"""
                {
                  "version": 2,
                  "assetBase": "{{OtherRelease}}",
                  "overrides": {
                    "MirrorsEdgeConsole.zip": "{{consoleUrl}}"
                  }
                }
                """);

            Assert.Equal(consoleUrl, provider.For("MirrorsEdgeConsole.zip"));
            Assert.Equal(OtherRelease + "OpenAL.zip", provider.For("OpenAL.zip"));
        }

        [Theory]
        [InlineData("http://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/console/MirrorsEdgeConsole.zip")]
        [InlineData("https://cdn.example.com/MirrorsEdgeConsole.zip")]
        public void ApplyManifestForTesting_IgnoresInvalidOverrideUrls(string overrideUrl)
        {
            using var temp = new TempDirectory();
            var provider = CreateProvider(temp);

            provider.ApplyManifestForTesting($$"""
                {
                  "version": 2,
                  "assetBase": "{{OtherRelease}}",
                  "overrides": {
                    "MirrorsEdgeConsole.zip": "{{overrideUrl}}"
                  }
                }
                """);

            Assert.Equal(OtherRelease + "MirrorsEdgeConsole.zip", provider.For("MirrorsEdgeConsole.zip"));
        }

        [Fact]
        public void ApplyManifestForTesting_RejectsAManifestWithAnUntrustedBase()
        {
            using var temp = new TempDirectory();
            var provider = CreateProvider(temp);

            Assert.Throws<InvalidOperationException>(() =>
                provider.ApplyManifestForTesting("""{"version":1,"assetBase":"https://cdn.example.com/assets/"}""", persistCache: true));

            Assert.Equal(DownloadUrls.DefaultAssetBase, provider.AssetBase);
            Assert.False(File.Exists(CachePath(temp)));
        }

        [Fact]
        public async Task EnsureLoadedAsync_FallsBackToCachedManifestWhenFetchFails()
        {
            using var temp = new TempDirectory();
            await File.WriteAllTextAsync(CachePath(temp), $$"""{"version":1,"assetBase":"{{OtherRelease}}"}""", CancellationToken.None);

            var provider = CreateProvider(temp);
            await provider.EnsureLoadedAsync(CancellationToken.None);

            Assert.Equal(OtherRelease, provider.AssetBase);
        }

        // The cache is a file that any program running as the user can change.
        [Fact]
        public async Task EnsureLoadedAsync_IgnoresACachedManifestWithAnUntrustedBase()
        {
            using var temp = new TempDirectory();
            await File.WriteAllTextAsync(CachePath(temp), """{"version":1,"assetBase":"https://cdn.example.com/assets/"}""", CancellationToken.None);

            var provider = CreateProvider(temp);
            await provider.EnsureLoadedAsync(CancellationToken.None);

            Assert.Equal(DownloadUrls.DefaultAssetBase, provider.AssetBase);
        }

        [Fact]
        public async Task EnsureLoadedAsync_FallsBackToDefaultWhenFetchAndCacheFail()
        {
            using var temp = new TempDirectory();
            var provider = CreateProvider(temp);

            await provider.EnsureLoadedAsync(CancellationToken.None);

            Assert.Equal(DownloadUrls.DefaultAssetBase, provider.AssetBase);
        }

        private static string CachePath(TempDirectory temp) => Path.Combine(temp.Path, "asset-manifest.json");

        private static AssetUrlProvider CreateProvider(TempDirectory temp) =>
            new(CachePath(temp), "https://example.invalid/manifest.json");
    }
}

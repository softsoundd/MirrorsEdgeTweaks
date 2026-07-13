namespace MirrorsEdgeTweaks.Helpers
{
    // Baked-in defaults used when the remote manifest cannot be fetched.
    // Runtime asset hosting is resolved by IAssetUrlProvider (see AssetUrlProvider).
    public static class DownloadUrls
    {
        public const string DefaultAssetBase = "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/";

        public const string ManifestBootstrapUrl =
            "https://raw.githubusercontent.com/softsoundd/MirrorsEdgeTweaks/main/assets-manifest.json";
    }
}

namespace MirrorsEdgeTweaks.Helpers
{
    // These defaults apply when the remote manifest cannot be fetched.
    // AssetUrlProvider resolves where the runtime assets are hosted.
    public static class DownloadUrls
    {
        public const string DefaultAssetBase = "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/";

        public const string ManifestBootstrapUrl =
            "https://raw.githubusercontent.com/softsoundd/MirrorsEdgeTweaks/main/assets-manifest.json";

        // A manifest can only move downloads to URLs that start with one of these,
        // because the downloaded files are extracted into the game folder.
        // Hosting the assets anywhere else needs an app update that adds the new location here.
        public static readonly IReadOnlyList<string> TrustedAssetPrefixes = new[]
        {
            "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/",
        };
    }
}

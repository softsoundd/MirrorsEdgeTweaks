namespace MirrorsEdgeTweaks.Helpers
{
    // These defaults apply when the remote manifest cannot be fetched.
    // AssetUrlProvider resolves where the runtime assets are hosted.
    public static class DownloadUrls
    {
        public const string DefaultAssetBase = "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/";

        public const string ManifestBootstrapUrl =
            "https://raw.githubusercontent.com/softsoundd/MirrorsEdgeTweaks/main/assets-manifest.json";
    }
}

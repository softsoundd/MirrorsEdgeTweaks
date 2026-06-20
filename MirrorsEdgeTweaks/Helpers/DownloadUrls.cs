namespace MirrorsEdgeTweaks.Helpers
{
    // Central definition of where Tweaks' runtime download ZIPs are hosted.
    // ZIPs are published as GitHub Release assets under the "runtime-assets" tag
    // Changing the host only requires editing AssetBase here
    public static class DownloadUrls
    {
        public const string AssetBase = "https://github.com/softsoundd/MirrorsEdgeTweaks/releases/download/runtime-assets/";

        public static string For(string fileName) => AssetBase + fileName;
    }
}

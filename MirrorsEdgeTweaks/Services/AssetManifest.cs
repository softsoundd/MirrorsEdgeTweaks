using MirrorsEdgeTweaks.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirrorsEdgeTweaks.Services
{
    public sealed class AssetManifest
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("assetBase")]
        public string AssetBase { get; init; } = string.Empty;

        [JsonPropertyName("updated")]
        public string? Updated { get; init; }

        [JsonPropertyName("overrides")]
        public Dictionary<string, string>? Overrides { get; init; }

        public static AssetManifest? TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<AssetManifest>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string? GetValidatedAssetBase()
        {
            return ValidateAssetUrl(AssetBase, requireTrailingSlash: true);
        }

        public static string? ValidateAssetUrl(string? url, bool requireTrailingSlash = false)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            // AbsoluteUri has ".." path steps resolved, so they cannot lead out of a trusted prefix.
            // An encoded slash or backslash is refused, because a server could decode it into such a step.
            string normalised = uri.AbsoluteUri;
            if (normalised.Contains("%2F", StringComparison.OrdinalIgnoreCase)
                || normalised.Contains("%5C", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (requireTrailingSlash && !normalised.EndsWith('/'))
                normalised += "/";

            foreach (string prefix in DownloadUrls.TrustedAssetPrefixes)
            {
                if (normalised.StartsWith(prefix, StringComparison.Ordinal))
                    return normalised;
            }

            return null;
        }
    }
}

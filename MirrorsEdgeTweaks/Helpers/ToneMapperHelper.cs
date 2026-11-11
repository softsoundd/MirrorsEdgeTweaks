using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    // Faithful Luma is identified by this comment at the top of each .usf shipped in FaithfulLumaTonemap.zip.
    public static class ToneMapperHelper
    {
        public const string Marker = "// MET:ToneMapper=FaithfulLuma";

        public const string FaithfulLumaAssetFileName = "FaithfulLumaTonemap.zip";
        public const string OriginalAssetFileName = "OriginalTonemap.zip";

        public static readonly IReadOnlyList<string> ShaderFileNames = new[]
        {
            "TdCalibrationShader.usf",
            "GammaCorrectionPixelShader.usf",
            "TdToneMapExposurePixelShader.usf",
            "DOFAndBloomGatherPixelShader.usf",
            "TdToneMappingPixelShader.usf",
        };

        public static bool IsFaithfulLumaInstalled(string? gameDirectory, IFileService files) =>
            CountFaithfulLumaFiles(gameDirectory, files) == ShaderFileNames.Count;

        // Unlike IsFaithfulLumaInstalled, this also finds an install that replaced only some of the shaders.
        public static bool HasFaithfulLumaFiles(string? gameDirectory, IFileService files) =>
            CountFaithfulLumaFiles(gameDirectory, files) > 0;

        private static int CountFaithfulLumaFiles(string? gameDirectory, IFileService files)
        {
            if (string.IsNullOrEmpty(gameDirectory))
                return 0;

            try
            {
                return ShaderFileNames.Count(fileName =>
                {
                    string path = files.CombinePaths(gameDirectory, "Engine", "Shaders", fileName);
                    return files.FileExists(path) && HasMarkerAtTop(files.ReadAllLines(path));
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        internal static bool HasMarkerAtTop(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                return trimmed.Equals(Marker, StringComparison.Ordinal);
            }

            return false;
        }
    }
}

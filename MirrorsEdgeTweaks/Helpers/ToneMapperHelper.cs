using System.IO;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    // Faithful Luma is identified by this comment at the top of each .usf shipped in FaithfulLumaTonemap.zip.
    public static class ToneMapperHelper
    {
        public const string Marker = "// MET:ToneMapper=FaithfulLuma";

        public static readonly IReadOnlyList<string> ShaderFileNames = new[]
        {
            "TdCalibrationShader.usf",
            "GammaCorrectionPixelShader.usf",
            "TdToneMapExposurePixelShader.usf",
            "DOFAndBloomGatherPixelShader.usf",
            "TdToneMappingPixelShader.usf",
        };

        public static bool IsFaithfulLumaInstalled(string? gameDirectory, IFileService files)
        {
            if (string.IsNullOrEmpty(gameDirectory))
                return false;

            try
            {
                foreach (string fileName in ShaderFileNames)
                {
                    string path = files.CombinePaths(gameDirectory, "Engine", "Shaders", fileName);
                    if (!files.FileExists(path) || !HasMarkerAtTop(files.ReadAllLines(path)))
                        return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
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

using System.Collections.Generic;

namespace MirrorsEdgeTweaks.Models
{
    public class GameConfiguration
    {
        public string? GameDirectoryPath { get; set; }
        public string? TdEngineIniPath { get; set; }
        public string? TdInputIniPath { get; set; }
        public string? EnginePackagePath { get; set; }
        public string? TdGamePackagePath { get; set; }
        public string LaunchArguments { get; set; } = string.Empty;
        public float FovValue { get; set; }
        public float AspectRatioWidth { get; set; }
        public float AspectRatioHeight { get; set; }
    }

    public class PackageOffsets
    {
        // fov offsets (Engine.u)
        public long PlayerControllerDefaultFovOffset { get; set; } = -1;
        public long PlayerControllerDesiredFovOffset { get; set; } = -1;
        public long PlayerControllerFovAngleOffset { get; set; } = -1;
        public long CameraFovOffset { get; set; } = -1;
        public long CameraActorFovAngleOffset { get; set; } = -1;

        // fov offsets (TdGame.u)
        public long SeqActCameraFovOffset { get; set; } = -1;
        public long UnzoomFovRateOffset { get; set; } = -1;
        public long TdMoveVertigoZoomFovOffset { get; set; } = -1;
        public long TdMoveVertigoZoomFovFlagsOffset { get; set; } = -1;
        public long NearClippingPlaneOffset { get; set; } = -1;
        public long FovScaleMultiplierOffset { get; set; } = -1;

        // aspect ratio offset (Engine.u)
        public long AspectRatioOffset { get; set; } = -1;

        // console offset (Engine.u)
        public long ConsoleHeightOffset { get; set; } = -1;
    }

    public class GameVersion
    {
        public string Version { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public bool IsValid { get; set; }
    }

    public class TdGameVersion
    {
        public string Name { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public bool IsDetected { get; set; }
    }

    public class AspectRatioInfo
    {
        public string DecimalFormat { get; set; } = string.Empty;
        public string CommonFormat { get; set; } = string.Empty;
        public float Value { get; set; }
    }

    public static class CommonAspectRatios
    {
        public static readonly Dictionary<string, double> Ratios = new Dictionary<string, double>
        {
            { "4:3", 4.0 / 3.0 },
            { "16:10", 16.0 / 10.0 },
            { "16:9", 16.0 / 9.0 },
            { "21:9", 21.0 / 9.0 },
            { "32:9", 32.0 / 9.0 }
        };
    }

    public class ConfigPatchPatterns
    {
        // Steam and GOG use the same pattern
        public byte[] RetailConfigPatternUnpatched { get; set; } = { 0x01, 0x00, 0x00, 0x68, 0x98, 0x11, 0x05, 0x02, 0xC7, 0x05, 0xA0, 0x13, 0x05, 0x02, 0x01, 0x00 };
        public byte[] SteamConfigPatternUnpatched { get; set; } = { 0x01, 0x00, 0x00, 0x68, 0xD8, 0x80, 0x03, 0x02, 0xC7, 0x05, 0xE0, 0x82, 0x03, 0x02, 0x01, 0x00 };
        public byte[] RetailConfigPatternPatched { get; set; } = { 0x01, 0x00, 0x00, 0x68, 0x98, 0x11, 0x05, 0x02, 0xC7, 0x05, 0xA0, 0x13, 0x05, 0x02, 0x00, 0x00 };
        public byte[] SteamConfigPatternPatched { get; set; } = { 0x01, 0x00, 0x00, 0x68, 0xD8, 0x80, 0x03, 0x02, 0xC7, 0x05, 0xE0, 0x82, 0x03, 0x02, 0x00, 0x00 };
        public const int ConfigPatchOffset = 14;
    }
}

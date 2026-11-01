namespace MirrorsEdgeTweaks.Models
{
    public class GameConfiguration
    {
        public string? GameDirectoryPath { get; set; }
        public string? UserFolderPath { get; set; }
        public string? TdEngineIniPath { get; set; }
        public string? TdInputIniPath { get; set; }
        public string? EnginePackagePath { get; set; }
        public string? TdGamePackagePath { get; set; }
        public string LaunchArguments { get; set; } = string.Empty;

        // AppSettingsService saves these user entries in metweaksconfig.ini.
        public string? Fov { get; set; }
        public string? Dpi { get; set; }
        public string? Cm360 { get; set; }
    }

    public class PackageOffsets
    {
        // These FOV offsets point into Engine.u.
        public long PlayerControllerDefaultFovOffset { get; set; } = -1;
        public long PlayerControllerDesiredFovOffset { get; set; } = -1;
        public long PlayerControllerFovAngleOffset { get; set; } = -1;
        public long CameraFovOffset { get; set; } = -1;
        public long CameraActorFovAngleOffset { get; set; } = -1;

        // These FOV offsets point into TdGame.u.
        public long SeqActCameraFovOffset { get; set; } = -1;
        public long UnzoomFovRateOffset { get; set; } = -1;
        public long TdMoveVertigoZoomFovOffset { get; set; } = -1;
        public long TdMoveVertigoZoomFovFlagsOffset { get; set; } = -1;
        public long NearClippingPlaneOffset { get; set; } = -1;
        public long FovScaleMultiplierOffset { get; set; } = -1;

        // These offsets point into Engine.u.
        public long AspectRatioOffset { get; set; } = -1;
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
}

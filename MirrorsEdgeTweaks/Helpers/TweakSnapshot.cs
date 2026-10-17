namespace MirrorsEdgeTweaks.Helpers
{
    // The game files that a re-apply covers.
    [Flags]
    public enum TweakScope
    {
        None = 0,
        Engine = 1,
        TdGame = 2,
        Localisation = 4,
        Exe = 8,
        All = Engine | TdGame | Localisation | Exe,
    }

    // Tweaks that must survive a replacement of game files.
    // The exe flow keeps it as JSON until the user has verified the game files.
    public sealed class TweakSnapshot
    {
        public bool TdGamePatched { get; set; }
        public bool SensEnabled { get; set; }
        public bool ClipEnabled { get; set; }
        public bool OnlineSkipEnabled { get; set; }
        public float? UniformSensitivityTarget { get; set; }
        public string? GamepadButtonType { get; set; }
        public bool HasLoadLastCheckpointKeybind { get; set; }
        public bool HasRestartTimeTrialKeybind { get; set; }
        public int? PhysXFps { get; set; }
        public bool ConsoleInstalled { get; set; }
        public bool HighResFix { get; set; }

        // Set only while the FOV patches are applied.
        public float? Fov { get; set; }
    }

    public sealed class TweakReapplyResult
    {
        public List<string> ReappliedSettings { get; } = new List<string>();
        public List<string> FailedSettings { get; } = new List<string>();
    }
}

namespace MirrorsEdgeTweaks.Helpers
{
    // Snapshot of FOV-related dynamic patch state captured before swapping the TdGame
    // package, so the settings can be re-applied afterwards.
    internal sealed class TdGameFovTouchpointSnapshot
    {
        public bool DynamicPatchesApplied { get; set; }
        public bool SensEnabled { get; set; }
        public bool ClipEnabled { get; set; }
        public bool OnlineSkipEnabled { get; set; }
        public float BaseFov { get; set; } = 90f;

        public bool HasValues => DynamicPatchesApplied;
    }

    // Snapshot of all TdGame-related tweaks that need to survive a TdGame package swap.
    internal sealed class TdGameTouchpointSnapshot
    {
        public TdGameFovTouchpointSnapshot FovSnapshot { get; } = new TdGameFovTouchpointSnapshot();
        public bool WasHighResFixActive { get; set; }
        public float? UniformSensitivityTargetValue { get; set; }
        public string? GamepadButtonType { get; set; }
        public bool HasLoadLastCheckpointKeybind { get; set; }
        public bool HasRestartTimeTrialKeybind { get; set; }
    }

    // Result of re-applying a TdGameTouchpointSnapshot after a package swap.
    internal sealed class TdGameTouchpointReapplyResult
    {
        public List<string> ReappliedSettings { get; } = new List<string>();
        public List<string> FailedSettings { get; } = new List<string>();
    }
}

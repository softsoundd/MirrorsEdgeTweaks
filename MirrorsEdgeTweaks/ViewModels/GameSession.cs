using CommunityToolkit.Mvvm.ComponentModel;
using MirrorsEdgeTweaks.Models;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class GameSession : ObservableObject
    {
        public GameConfiguration Config { get; } = new GameConfiguration();
        public PackageOffsets Offsets { get; } = new PackageOffsets();

        // Is true once Offsets were found in a load of Engine.u and TdGame.u.
        // Flows that splice or replace either file clear this, because they move what Offsets point at.
        public bool PackagesLoaded { get; set; }

        // Is true while a game directory is processed at startup or after selection.
        // Status refreshes are skipped meanwhile to avoid flicker, and a single refresh runs afterwards.
        public bool IsProcessingGameDirectory { get; set; }

        // GameDataService sets this while finding offsets, and GraphicsTweaksViewModel reads it.
        public float? DetectedCameraFov { get; set; }

        // InitialisationSettingsViewModel sets this, and GraphicsTweaksViewModel reads it to reconcile TdGame.u.
        // It lives here because InitialisationSettingsViewModel already depends on GraphicsTweaksViewModel.
        public bool OnlineSkipEnabled { get; set; }

        public ApplyGate ApplyGate { get; } = new();

        public event Action? UserFolderContextChanged;

        public void NotifyUserFolderContextChanged() => UserFolderContextChanged?.Invoke();

        // Raised after Tweaks Scripts is installed or uninstalled.
        public event Action? TweaksScriptsChanged;

        public void NotifyTweaksScriptsChanged() => TweaksScriptsChanged?.Invoke();

        [ObservableProperty]
        private bool _isGameLoaded;
    }
}

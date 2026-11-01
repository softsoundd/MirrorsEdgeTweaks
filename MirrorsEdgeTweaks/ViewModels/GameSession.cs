using CommunityToolkit.Mvvm.ComponentModel;
using MirrorsEdgeTweaks.Models;
using UELib;

namespace MirrorsEdgeTweaks.ViewModels
{
    public partial class GameSession : ObservableObject
    {
        public GameConfiguration Config { get; } = new GameConfiguration();
        public PackageOffsets Offsets { get; } = new PackageOffsets();

        public UnrealPackage? Package { get; set; }
        public UnrealPackage? TdGamePackage { get; set; }

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

        [ObservableProperty]
        private bool _isGameLoaded;
    }
}

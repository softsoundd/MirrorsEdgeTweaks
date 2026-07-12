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

        // True while the game directory is being processed at startup/selection. Per-feature status
        // refreshers skip work during this window (a single refresh runs afterwards) to avoid flicker.
        public bool IsProcessingGameDirectory { get; set; }

        // Populated during offset finding; read by GraphicsTweaksViewModel to avoid service→VM coupling.
        public float? DetectedCameraFov { get; set; }

        // Owned by InitialisationSettingsViewModel but stored here so GraphicsTweaksViewModel can
        // reconcile TdGame.u without a circular dependency (Init already depends on Graphics).
        public bool OnlineSkipEnabled { get; set; }

        public ApplyGate ApplyGate { get; } = new();

        [ObservableProperty]
        private bool _isGameLoaded;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using MirrorsEdgeTweaks.Models;
using UELib;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Observable, application-wide game state shared between the View, the ViewModels and the
    // service layer. Holds the GameConfiguration, PackageOffsets and the loaded Unreal packages
    // so every consumer reads and writes the same instances.
    public partial class GameSession : ObservableObject
    {
        public GameConfiguration Config { get; } = new GameConfiguration();
        public PackageOffsets Offsets { get; } = new PackageOffsets();

        public UnrealPackage? Package { get; set; }
        public UnrealPackage? TdGamePackage { get; set; }

        // True while the game directory is being processed at startup/selection. Per-feature status
        // refreshers skip work during this window (a single refresh runs afterwards) to avoid flicker.
        public bool IsProcessingGameDirectory { get; set; }

        // The horizontal FOV (degrees) discovered in the Camera CDO during package offset finding,
        // or null if not found. Shared so GraphicsTweaksViewModel can refresh its FOV display
        // without the package-load service depending on the feature VM.
        public float? DetectedCameraFov { get; set; }

        // Whether the "skip online check" TdGame patch is enabled. Owned by
        // InitialisationSettingsViewModel but stored here so GraphicsTweaksViewModel can read it when
        // reconciling TdGame.u without a circular dependency (Init already depends on Graphics).
        public bool OnlineSkipEnabled { get; set; }

        [ObservableProperty]
        private bool _isGameLoaded;
    }
}

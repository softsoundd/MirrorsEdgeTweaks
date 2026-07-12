using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MirrorsEdgeTweaks.ViewModels
{
    // View model for the entire Graphics Tweaks tab: Video Settings (FOV, resolution, render
    // resolution, VSync, FPS limit, anti-aliasing, PhysX, PhysX FPS), Quality Presets, the
    // Individual Settings toggles and LOD settings. A single _isLoading guard ensures programmatic
    // loads and error-reverts never re-trigger an apply. The near-identical Enabled/Disabled combos
    // are collapsed into a collection of GraphicsOption sharing one handler body.
    public partial class GraphicsTweaksViewModel : BusyViewModel
    {
        private static readonly string[] AnisoLevels = { "Off", "2x", "4x", "8x", "16x" };
        private static readonly string[] AaLevels = { "Off", "2x", "4x", "8x", "8xQ", "16xQ" };

        private readonly IDialogService _dialogService;
        private readonly IGraphicsSettingsService _graphics;
        private readonly IUIScalingService _uiScaling;
        private readonly IGameDataService _gameData;
        private readonly IAppSettingsService _settings;
        private readonly IDownloadService _download;
        private readonly GameSession _session;
        private readonly UnlockedConfigsViewModel _unlockedConfigs;

        // Re-entrancy guard. While true, programmatic property changes (ini load, preset refresh,
        // error reverts) must not trigger an apply.
        private bool _isLoading;

        private bool _isRenderResolutionDragging;

        // Cached "dynamic FOV scaling fully patched" state for Engine.u. Determining it requires a
        // full package read, so it is refreshed only when the state can actually change (package
        // reload or resolution reconcile) via RefreshEnginePatchState, never on every FOV keystroke.
        private bool _enginePatchesApplied;

        // ---- Video Settings combos (collapsed Enabled/Disabled + level/mode combos) ----
        public GraphicsOption VSync { get; }
        public GraphicsOption PhysX { get; }
        public GraphicsOption Anisotropic { get; }
        public GraphicsOption StaticDecals { get; }
        public GraphicsOption DynamicDecals { get; }
        public GraphicsOption RadialBlur { get; }
        public GraphicsOption BloomDoF { get; }
        public GraphicsOption LensFlare { get; }
        public GraphicsOption DynamicLights { get; }
        public GraphicsOption DynamicShadows { get; }
        public GraphicsOption HqDynamicShadows { get; }
        public GraphicsOption Lightmaps { get; }
        public GraphicsOption SunHaze { get; }
        public GraphicsOption ToneMapping { get; }
        public GraphicsOption TextureManagement { get; }

        // ---- Special combos ----
        [ObservableProperty] private int _antiAliasingIndex = -1;
        [ObservableProperty] private int _streakEffectIndex = -1;
        [ObservableProperty] private int _textureDetailIndex = -1;
        [ObservableProperty] private bool _isTextureDetailCustomVisible;
        [ObservableProperty] private int _graphicsQualityIndex = -1;
        [ObservableProperty] private bool _isGraphicsQualityCustomVisible;

        // ---- Shaders ----
        [ObservableProperty] private int _toneMapperIndex = -1;

        // ---- Render resolution ----
        [ObservableProperty] private double _renderResolutionPercent = 100;

        // ---- Resolution + High-Res Fix ----
        public ObservableCollection<ResolutionHelper.Resolution> Resolutions { get; } = new();
        [ObservableProperty] private ResolutionHelper.Resolution? _selectedResolution;
        [ObservableProperty] private string _highResFixStatus = "High-Res Fix N/A";
        [ObservableProperty] private Brush _highResFixStatusForeground = Brushes.Gray;

        // ---- FPS limit ----
        [ObservableProperty] private string _fpsLimitInput = "";
        [ObservableProperty] private string _fpsLimitStatus = "N/A";
        [ObservableProperty] private Brush _fpsLimitStatusForeground = Brushes.Gray;

        // ---- PhysX FPS ----
        [ObservableProperty] private string _physXFpsInput = "";

        // ---- LOD ----
        [ObservableProperty] private string _minLodText = "";
        [ObservableProperty] private string _maxLodText = "";
        [ObservableProperty] private string _lodBiasText = "";

        // ---- FOV ----
        [ObservableProperty] private string _currentFovValue = "N/A";
        [ObservableProperty] private string _newFovValue = "";
        [ObservableProperty] private bool _fovAgnosticSens;
        [ObservableProperty] private bool _compensatedClip = true;
        [ObservableProperty] private string _horPlusStatus = "";

        public GraphicsTweaksViewModel(
            IDialogService dialogService,
            IGraphicsSettingsService graphics,
            IUIScalingService uiScaling,
            IGameDataService gameData,
            IAppSettingsService settings,
            IDownloadService download,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress,
            UnlockedConfigsViewModel unlockedConfigs)
            : base(gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _graphics = graphics;
            _uiScaling = uiScaling;
            _gameData = gameData;
            _settings = settings;
            _download = download;
            _session = session;
            _unlockedConfigs = unlockedConfigs;

            VSync = new GraphicsOption((o, v) => ApplyStandard(o, v, "VSync", () => _graphics.ApplyVSync(IniPath!, v == 0)));
            PhysX = new GraphicsOption((o, v) => ApplyStandard(o, v, "PhysX", () => _graphics.ApplyPhysX(IniPath!, v == 0)));
            Anisotropic = new GraphicsOption((o, v) => ApplyStandard(o, v, "anisotropic filtering", () => _graphics.ApplyAnisotropicFiltering(IniPath!, AnisoLevels[v])));
            StaticDecals = new GraphicsOption((o, v) => ApplyStandard(o, v, "static decals", () => _graphics.ApplyStaticDecals(IniPath!, v == 0)));
            DynamicDecals = new GraphicsOption((o, v) => ApplyStandard(o, v, "dynamic decals", () => _graphics.ApplyDynamicDecals(IniPath!, v == 0)));
            RadialBlur = new GraphicsOption((o, v) => ApplyStandard(o, v, "radial blur", () => _graphics.ApplyRadialBlur(IniPath!, v == 0)));
            BloomDoF = new GraphicsOption((o, v) => ApplyStandard(o, v, "bloom and DoF", () => _graphics.ApplyBloomAndDoF(IniPath!, v == 0)));
            LensFlare = new GraphicsOption((o, v) => ApplyStandard(o, v, "lens flare", () => _graphics.ApplyLensFlare(IniPath!, v == 0)));
            DynamicLights = new GraphicsOption((o, v) => ApplyStandard(o, v, "dynamic lights", () => _graphics.ApplyDynamicLights(IniPath!, v == 0)));
            DynamicShadows = new GraphicsOption((o, v) => ApplyStandard(o, v, "dynamic shadows", () => _graphics.ApplyDynamicShadows(IniPath!, v == 0)));
            HqDynamicShadows = new GraphicsOption((o, v) => ApplyStandard(o, v, "HQ dynamic shadows", () => _graphics.ApplyHQDynamicShadows(IniPath!, v == 0)));
            Lightmaps = new GraphicsOption((o, v) => ApplyStandard(o, v, "lightmaps", () => _graphics.ApplyLightmaps(IniPath!, v == 0)));
            SunHaze = new GraphicsOption((o, v) => ApplyStandard(o, v, "sun haze", () => _graphics.ApplySunHaze(IniPath!, v == 0)));
            ToneMapping = new GraphicsOption((o, v) => ApplyStandard(o, v, "tone mapping", () => _graphics.ApplyToneMapping(IniPath!, v == 0)));
            TextureManagement = new GraphicsOption((o, v) => ApplyStandard(o, v, "texture management", () => _graphics.ApplyTextureManagement(IniPath!, v == 0 ? "Modern" : "Default")));
        }

        private string? IniPath => _session.Config.TdEngineIniPath;

        // ---- Guard + dispatch helpers ----

        private void SetSilently(Action action)
        {
            bool previous = _isLoading;
            _isLoading = true;
            try { action(); }
            finally { _isLoading = previous; }
        }

        private bool EnsureIniExists(Action revert)
        {
            if (string.IsNullOrEmpty(IniPath) || !File.Exists(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogMessageType.Error);
                SetSilently(revert);
                return false;
            }
            return true;
        }

        // ---- Bulk load from ini ----

        public void LoadFromIni()
        {
            if (string.IsNullOrEmpty(IniPath))
                return;

            SetSilently(() =>
            {
                try
                {
                    string? vsync = _graphics.ReadIniValue(IniPath, "UseVsync");
                    if (vsync != null)
                        VSync.Index = vsync.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? maxMultiSamples = _graphics.ReadIniValue(IniPath, "MaxMultisamples");
                    if (maxMultiSamples != null)
                        AntiAliasingIndex = maxMultiSamples switch
                        {
                            "1" => 0,
                            "2" => 1,
                            "4" => 2,
                            "8" => 3,
                            "10" => 4,
                            "12" => 5,
                            _ => AntiAliasingIndex
                        };

                    string? maxAnisotropy = _graphics.ReadIniValue(IniPath, "MaxAnisotropy");
                    if (maxAnisotropy != null)
                        Anisotropic.Index = maxAnisotropy switch
                        {
                            "0" => 0,
                            "2" => 1,
                            "4" => 2,
                            "8" => 3,
                            "16" => 4,
                            _ => Anisotropic.Index
                        };

                    string? physx = _graphics.ReadIniValue(IniPath, "PhysXEnhanced");
                    if (physx != null)
                        PhysX.Index = physx.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? screenPercentage = _graphics.ReadIniValue(IniPath, "ScreenPercentage");
                    if (screenPercentage != null && double.TryParse(screenPercentage, NumberStyles.Float, CultureInfo.InvariantCulture, out double percentage))
                        RenderResolutionPercent = Math.Round(percentage);

                    string? staticDecals = _graphics.ReadIniValue(IniPath, "StaticDecals");
                    if (staticDecals != null)
                        StaticDecals.Index = staticDecals.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? dynamicDecals = _graphics.ReadIniValue(IniPath, "DynamicDecals");
                    if (dynamicDecals != null)
                        DynamicDecals.Index = dynamicDecals.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? motionBlur = _graphics.ReadIniValue(IniPath, "TdMotionBlur");
                    if (motionBlur != null)
                        RadialBlur.Index = motionBlur.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    if (!string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                    {
                        string defaultHudEffectsPath = Path.Combine(_session.Config.GameDirectoryPath, "TdGame", "Config", "DefaultHudEffects.ini");
                        string? streakEffect = _graphics.ReadStreakEffectStatus(defaultHudEffectsPath);
                        if (streakEffect != null)
                            StreakEffectIndex = streakEffect.Equals("true", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    }

                    string? bloom = _graphics.ReadIniValue(IniPath, "Bloom");
                    if (bloom != null)
                        BloomDoF.Index = bloom.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? lensFlares = _graphics.ReadIniValue(IniPath, "LensFlares");
                    if (lensFlares != null)
                        LensFlare.Index = lensFlares.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? dynamicLights = _graphics.ReadIniValue(IniPath, "DynamicLights");
                    if (dynamicLights != null)
                        DynamicLights.Index = dynamicLights.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? dynamicShadows = _graphics.ReadIniValue(IniPath, "DynamicShadows");
                    if (dynamicShadows != null)
                        DynamicShadows.Index = dynamicShadows.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? hqShadows = _graphics.ReadIniValue(IniPath, "bEnableBranchingPCFShadows");
                    string? vsmShadows = _graphics.ReadIniValue(IniPath, "bEnableVSMShadows");
                    if (hqShadows != null && vsmShadows != null)
                    {
                        bool isHQEnabled = hqShadows.Equals("True", StringComparison.OrdinalIgnoreCase) &&
                                           vsmShadows.Equals("False", StringComparison.OrdinalIgnoreCase);
                        HqDynamicShadows.Index = isHQEnabled ? 0 : 1;
                    }

                    string? lightmaps = _graphics.ReadIniValue(IniPath, "DirectionalLightmaps");
                    if (lightmaps != null)
                        Lightmaps.Index = lightmaps.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? sunHaze = _graphics.ReadIniValue(IniPath, "TdSunHaze");
                    if (sunHaze != null)
                        SunHaze.Index = sunHaze.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? toneMapping = _graphics.ReadIniValue(IniPath, "TdTonemapping");
                    if (toneMapping != null)
                        ToneMapping.Index = toneMapping.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? textureStreaming = _graphics.ReadIniValue(IniPath, "OnlyStreamInTextures");
                    if (textureStreaming != null)
                        TextureManagement.Index = textureStreaming.Equals("True", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

                    string? minLOD = _graphics.ReadIniValue(IniPath, "MinLODSize");
                    if (minLOD != null)
                        MinLodText = minLOD;

                    string? maxLOD = _graphics.ReadIniValue(IniPath, "MaxLODSize");
                    if (maxLOD != null)
                        MaxLodText = maxLOD;

                    string? lodBias = _graphics.ReadIniValue(IniPath, "LODBias");
                    if (lodBias != null)
                        LodBiasText = lodBias;

                    RefreshToneMapper();

                    ApplyDetectedPresets();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load graphics settings: {ex.Message}");
                }
            });
        }

        public void RefreshPresetIndicators()
        {
            if (string.IsNullOrEmpty(IniPath))
                return;

            SetSilently(ApplyDetectedPresets);
        }

        // Must be called from within a SetSilently scope.
        private void ApplyDetectedPresets()
        {
            string texturePreset = _graphics.DetectTextureDetailPreset(IniPath!);
            if (texturePreset == "Custom")
            {
                IsTextureDetailCustomVisible = true;
                TextureDetailIndex = 0;
            }
            else
            {
                IsTextureDetailCustomVisible = false;
                TextureDetailIndex = texturePreset switch
                {
                    "Lowest" => 1,
                    "Low" => 2,
                    "Medium" => 3,
                    "High" => 4,
                    "Highest" => 5,
                    _ => TextureDetailIndex
                };
            }

            string qualityPreset = _graphics.DetectGraphicsQualityPreset(IniPath!);
            if (qualityPreset == "Custom")
            {
                IsGraphicsQualityCustomVisible = true;
                GraphicsQualityIndex = 0;
            }
            else
            {
                IsGraphicsQualityCustomVisible = false;
                GraphicsQualityIndex = qualityPreset switch
                {
                    "Lowest" => 1,
                    "Low" => 2,
                    "Medium" => 3,
                    "High" => 4,
                    "Highest" => 5,
                    _ => GraphicsQualityIndex
                };
            }
        }

        // ---- Shared apply for the collapsed Enabled/Disabled + level/mode combos ----

        private void ApplyStandard(GraphicsOption option, int index, string failureLabel, Action apply)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => option.Index = -1))
                return;

            try
            {
                apply();
                RefreshPresetIndicators();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply {failureLabel}: {ex.Message}", DialogMessageType.Error);
                SetSilently(() => option.Index = -1);
            }
        }

        // ---- Anti-aliasing (16xQ confirmation) ----

        partial void OnAntiAliasingIndexChanged(int oldValue, int newValue) => _ = OnAntiAliasingChangedAsync(oldValue, newValue);

        private async Task OnAntiAliasingChangedAsync(int previousIndex, int index)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => AntiAliasingIndex = previousIndex))
                return;

            string level = AaLevels[index];

            if (level == "16xQ")
            {
                bool proceed = await _dialogService.ShowConfirmationAsync(
                    "Warning for NVIDIA GPU users",
                    "16xQ anti-aliasing (CSAA) is not supported on NVIDIA GPUs newer than the first-generation Maxwell microarchitecture (GTX 960 and up). " +
                    "Mirror's Edge will fail to launch if you choose this setting and have an NVIDIA GPU newer than this.\n\nDo you wish to proceed?");

                if (!proceed)
                {
                    SetSilently(() => AntiAliasingIndex = previousIndex);
                    return;
                }
            }

            try
            {
                _graphics.ApplyAntiAliasing(IniPath!, level);
                RefreshPresetIndicators();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply anti-aliasing: {ex.Message}", DialogMessageType.Error);
                SetSilently(() => AntiAliasingIndex = previousIndex);
            }
        }

        // ---- Streak effect (separate ini + unlocked-configs warning) ----

        partial void OnStreakEffectIndexChanged(int oldValue, int newValue)
        {
            if (_isLoading || newValue < 0)
                return;

            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please specify the correct game install folder path first.", DialogMessageType.Error);
                SetSilently(() => StreakEffectIndex = oldValue);
                return;
            }

            string defaultHudEffectsPath = Path.Combine(gameDir, "TdGame", "Config", "DefaultHudEffects.ini");
            if (!File.Exists(defaultHudEffectsPath))
            {
                _dialogService.ShowMessage("Error", "Cannot toggle streak effect, 'DefaultHudEffects.ini' file not found.", DialogMessageType.Error);
                SetSilently(() => StreakEffectIndex = oldValue);
                return;
            }

            try
            {
                bool enabled = newValue == 0;
                _graphics.ApplyStreakEffect(defaultHudEffectsPath, enabled);

                if (!enabled)
                {
                    bool isPatched = _unlockedConfigs.UnlockedConfigsStatus == "Patched";
                    if (!isPatched)
                    {
                        _dialogService.ShowMessage("Warning", "The config modification patch in the 'Game Tweaks' section is not applied. " +
                            "Please apply the patch in order for your game to launch with the disabled streak effect.", DialogMessageType.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply streak effect: {ex.Message}", DialogMessageType.Error);
                SetSilently(() => StreakEffectIndex = oldValue);
            }
        }

        // ---- Quality presets ----

        partial void OnTextureDetailIndexChanged(int oldValue, int newValue) => _ = OnTextureDetailChangedAsync(oldValue, newValue);

        private async Task OnTextureDetailChangedAsync(int previousIndex, int index)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => TextureDetailIndex = previousIndex))
                return;

            if (index == 0) // Custom
                return;

            bool proceed = await _dialogService.ShowConfirmationAsync(
                "Texture detail preset",
                "Applying a texture detail preset will revert any changes you may have made in the 'Individual Settings' section below.\n\nDo you wish to proceed?");

            if (!proceed)
            {
                SetSilently(() => TextureDetailIndex = previousIndex);
                return;
            }

            string preset = TexturePresetName(index);

            try
            {
                _graphics.ApplyTextureDetailPreset(IniPath!, preset);
                LoadFromIni();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply texture detail preset: {ex.Message}", DialogMessageType.Error);
                SetSilently(() => TextureDetailIndex = previousIndex);
            }
        }

        partial void OnGraphicsQualityIndexChanged(int oldValue, int newValue) => _ = OnGraphicsQualityChangedAsync(oldValue, newValue);

        private async Task OnGraphicsQualityChangedAsync(int previousIndex, int index)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => GraphicsQualityIndex = previousIndex))
                return;

            if (index == 0) // Custom
                return;

            bool proceed = await _dialogService.ShowConfirmationAsync(
                "Graphics quality preset",
                "Applying a graphics quality preset will revert any changes you may have made in the 'Individual Settings' section below.\n\nDo you wish to proceed?");

            if (!proceed)
            {
                SetSilently(() => GraphicsQualityIndex = previousIndex);
                return;
            }

            string preset = TexturePresetName(index);

            try
            {
                _graphics.ApplyGraphicsQualityPreset(IniPath!, preset);
                LoadFromIni();
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply graphics quality preset: {ex.Message}", DialogMessageType.Error);
                SetSilently(() => GraphicsQualityIndex = previousIndex);
            }
        }

        private static string TexturePresetName(int index) => index switch
        {
            1 => "Lowest",
            2 => "Low",
            3 => "Medium",
            4 => "High",
            5 => "Highest",
            _ => "Custom"
        };

        // ---- Render resolution ----

        partial void OnRenderResolutionPercentChanged(double value)
        {
            if (_isLoading || _isRenderResolutionDragging)
                return;

            ApplyRenderResolution();
        }

        // Called by the window's slider Thumb.DragStarted handler.
        public void BeginRenderResolutionDrag() => _isRenderResolutionDragging = true;

        // Called by the window's slider Thumb.DragCompleted handler.
        public void EndRenderResolutionDrag()
        {
            _isRenderResolutionDragging = false;
            ApplyRenderResolution();
        }

        private void ApplyRenderResolution()
        {
            if (string.IsNullOrEmpty(IniPath))
            {
                RevertRenderResolution();
                return;
            }

            if (!File.Exists(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogMessageType.Error);
                RevertRenderResolution();
                return;
            }

            int percentage = (int)RenderResolutionPercent;
            var gameDir = _session.Config.GameDirectoryPath;

            if (percentage > 100 && !string.IsNullOrEmpty(gameDir))
            {
                string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
                if (File.Exists(exePath))
                {
                    try
                    {
                        var state = SupersamplePatchHelper.GetPatchState(exePath);
                        if (state == SupersamplePatchState.Unpatched)
                        {
                            SupersamplePatchHelper.ApplyPatch(exePath);
                        }
                        else if (state != SupersamplePatchState.Patched)
                        {
                            _dialogService.ShowMessage("Error",
                                "Could not verify the supersampling patch state. The executable may be an unsupported version.",
                                DialogMessageType.Error);
                            SetSilently(() => RenderResolutionPercent = 100);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowMessage("Error",
                            $"Failed to apply supersampling patch: {ex.Message}\n\nRender resolution above 100% requires the executable to be patched.",
                            DialogMessageType.Error);
                        SetSilently(() => RenderResolutionPercent = 100);
                        return;
                    }
                }
            }

            try
            {
                _graphics.ApplyRenderResolution(IniPath, percentage);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply render resolution: {ex.Message}", DialogMessageType.Error);
                RevertRenderResolution();
            }
        }

        private void RevertRenderResolution()
        {
            if (RenderResolutionPercent != 100)
                SetSilently(() => RenderResolutionPercent = 100);
        }


        // ---- FPS limit ----

        [RelayCommand]
        private async Task ApplyFpsLimit()
        {
            if (string.IsNullOrEmpty(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini path is not set.", DialogMessageType.Error);
                return;
            }

            string input = (FpsLimitInput ?? "").Trim();

            if (string.IsNullOrEmpty(input))
            {
                _dialogService.ShowMessage("Error", "FPS value not entered.", DialogMessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int value))
            {
                _dialogService.ShowMessage("Error", "Invalid FPS value.", DialogMessageType.Error);
                return;
            }

            if (value < 1)
            {
                _dialogService.ShowMessage("Error", "FPS cannot be less than 1.", DialogMessageType.Error);
                return;
            }

            if (value > 2000)
            {
                _dialogService.ShowMessage("Error", "FPS cannot be greater than 2000.", DialogMessageType.Error);
                return;
            }

            string ini = IniPath!;
            try
            {
                await RunBusyAsync("Applying FPS limit...", () => _graphics.ApplyFPSLimit(ini, value));
                RefreshFpsLimit();
                _dialogService.ShowMessage("Success", $"FPS limit set to {value} FPS.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply FPS limit: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task RemoveFpsLimit()
        {
            if (string.IsNullOrEmpty(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini path is not set.", DialogMessageType.Error);
                return;
            }

            string ini = IniPath!;
            try
            {
                await RunBusyAsync("Removing FPS limit...", () => _graphics.RemoveFPSLimit(ini));
                RefreshFpsLimit();
                _dialogService.ShowMessage("Success", "FPS limit removed.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to remove FPS limit: {ex.Message}", DialogMessageType.Error);
            }
        }

        public void RefreshFpsLimit()
        {
            if (_session.IsProcessingGameDirectory)
                return;

            if (string.IsNullOrEmpty(IniPath))
            {
                FpsLimitStatus = "N/A";
                FpsLimitStatusForeground = Brushes.Gray;
                return;
            }

            try
            {
                var (isLimited, fpsValue) = _graphics.ReadFPSLimitStatus(IniPath);

                if (isLimited && fpsValue.HasValue)
                {
                    FpsLimitStatus = "Limiter On";
                    FpsLimitStatusForeground = Brushes.Gray;
                    FpsLimitInput = fpsValue.Value.ToString();
                }
                else
                {
                    FpsLimitStatus = "Limiter Off";
                    FpsLimitStatusForeground = Brushes.Gray;
                }
            }
            catch
            {
                FpsLimitStatus = "N/A";
                FpsLimitStatusForeground = Brushes.Gray;
            }
        }

        // ---- PhysX FPS ----

        [RelayCommand]
        private async Task ApplyPhysXFps()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }

            string input = (PhysXFpsInput ?? "").Trim();

            if (string.IsNullOrEmpty(input))
            {
                _dialogService.ShowMessage("Error", "PhysX FPS value not entered.", DialogMessageType.Error);
                return;
            }

            if (!int.TryParse(input, out int physxFps))
            {
                _dialogService.ShowMessage("Error", "Invalid PhysX FPS. Please enter a number between 50 and 300.", DialogMessageType.Error);
                return;
            }

            if (physxFps < 50)
            {
                _dialogService.ShowMessage("Error", "PhysX framerate cannot be less than 50.", DialogMessageType.Error);
                return;
            }

            if (physxFps > 300)
            {
                _dialogService.ShowMessage("Error", "PhysX framerate cannot be greater than 300.", DialogMessageType.Error);
                return;
            }

            _gameStatus.IsUiEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.IsDownloadProgressIndeterminate = true;
            _gameStatus.Status = "Applying PhysX FPS settings...";

            try
            {
                await Task.Run(() => PhysXTimingPatcher.Apply(gameDir, physxFps));
                _gameStatus.Status = "Ready.";
                await _dialogService.ShowMessageAsync("Success", $"PhysX FPS set to {physxFps} successfully.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "Failed to apply PhysX FPS.";
                await _dialogService.ShowMessageAsync("Error", $"Failed to apply PhysX FPS:\n\n{ex.Message}", DialogMessageType.Error);
            }
            finally
            {
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.IsDownloadProgressIndeterminate = false;
                _gameStatus.IsUiEnabled = true;
            }
        }

        public void RefreshPhysXFps()
        {
            int? fps = PhysXTimingPatcher.Read(_session.Config.GameDirectoryPath);
            if (fps.HasValue)
                PhysXFpsInput = fps.Value.ToString();
        }

        // Reads the PhysX timing value off the UI thread, then applies it on the UI thread so the
        // status-bar progress bar keeps animating during startup.
        public async Task RefreshPhysXFpsAsync()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            int? fps = await Task.Run(() => PhysXTimingPatcher.Read(gameDir));
            if (fps.HasValue)
                PhysXFpsInput = fps.Value.ToString();
        }

        // ---- LOD ----

        [RelayCommand]
        private async Task ApplyMinLod()
        {
            if (string.IsNullOrEmpty(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini path is not set.", DialogMessageType.Error);
                return;
            }

            string input = (MinLodText ?? "").Trim();
            if (!ValidateLod(input, 1, 4096, out int value))
                return;

            string ini = IniPath!;
            try
            {
                await RunBusyAsync("Applying minimum LOD...", () => _graphics.ApplyMinLOD(ini, value));
                RefreshPresetIndicators();
                _dialogService.ShowMessage("Success", $"Minimum LOD set to {value}.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply minimum LOD: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task ApplyMaxLod()
        {
            if (string.IsNullOrEmpty(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini path is not set.", DialogMessageType.Error);
                return;
            }

            string input = (MaxLodText ?? "").Trim();
            if (!ValidateLod(input, 1, 4096, out int value))
                return;

            string ini = IniPath!;
            try
            {
                await RunBusyAsync("Applying maximum LOD...", () => _graphics.ApplyMaxLOD(ini, value));
                RefreshPresetIndicators();
                _dialogService.ShowMessage("Success", $"Maximum LOD set to {value}.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply maximum LOD: {ex.Message}", DialogMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task ApplyLodBias()
        {
            if (string.IsNullOrEmpty(IniPath))
            {
                _dialogService.ShowMessage("Error", "TdEngine.ini path is not set.", DialogMessageType.Error);
                return;
            }

            string input = (LodBiasText ?? "").Trim();
            if (!ValidateLod(input, -1, 12, out int value, isBias: true))
                return;

            string ini = IniPath!;
            try
            {
                await RunBusyAsync("Applying LOD bias...", () => _graphics.ApplyLODBias(ini, value));
                RefreshPresetIndicators();
                _dialogService.ShowMessage("Success", $"LOD bias set to {value}.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage("Error", $"Failed to apply LOD bias: {ex.Message}", DialogMessageType.Error);
            }
        }

        private bool ValidateLod(string input, int min, int max, out int value, bool isBias = false)
        {
            value = 0;

            if (string.IsNullOrEmpty(input))
            {
                _dialogService.ShowMessage("Error", "LOD value not entered.", DialogMessageType.Error);
                return false;
            }

            if (!int.TryParse(input, out value))
            {
                _dialogService.ShowMessage("Error", "Invalid LOD value.", DialogMessageType.Error);
                return false;
            }

            if (value < min)
            {
                _dialogService.ShowMessage("Error", isBias ? $"LOD bias cannot be lower than {min}." : $"LOD cannot be less than {min}.", DialogMessageType.Error);
                return false;
            }

            if (value > max)
            {
                _dialogService.ShowMessage("Error", isBias ? $"LOD bias cannot be higher than {max}." : $"LOD cannot be higher than {max}.", DialogMessageType.Error);
                return false;
            }

            return true;
        }



        private string? GetGameExePath()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
                return null;

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            return File.Exists(exePath) ? exePath : null;
        }
    }

    // A single graphics ComboBox whose selected index drives an apply action on the owning
    // GraphicsTweaksViewModel. Collapses the many near-identical Enabled/Disabled (and level/mode)
    // combo handlers into one shared body.
    public partial class GraphicsOption : ObservableObject
    {
        private readonly Action<GraphicsOption, int> _onChanged;

        [ObservableProperty] private int _index = -1;

        public GraphicsOption(Action<GraphicsOption, int> onChanged) => _onChanged = onChanged;

        partial void OnIndexChanged(int value) => _onChanged(this, value);
    }
}

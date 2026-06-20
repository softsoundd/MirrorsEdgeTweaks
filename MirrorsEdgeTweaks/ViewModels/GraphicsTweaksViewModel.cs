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
                    if (screenPercentage != null && double.TryParse(screenPercentage, out double percentage))
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

        partial void OnAntiAliasingIndexChanged(int value) => _ = OnAntiAliasingChangedAsync(value);

        private async Task OnAntiAliasingChangedAsync(int index)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => AntiAliasingIndex = -1))
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
                    SetSilently(() => AntiAliasingIndex = -1);
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
                SetSilently(() => AntiAliasingIndex = -1);
            }
        }

        // ---- Streak effect (separate ini + unlocked-configs warning) ----

        partial void OnStreakEffectIndexChanged(int value)
        {
            if (_isLoading || value < 0)
                return;

            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please specify the correct game install folder path first.", DialogMessageType.Error);
                return;
            }

            string defaultHudEffectsPath = Path.Combine(gameDir, "TdGame", "Config", "DefaultHudEffects.ini");
            if (!File.Exists(defaultHudEffectsPath))
            {
                _dialogService.ShowMessage("Error", "Cannot toggle streak effect, 'DefaultHudEffects.ini' file not found.", DialogMessageType.Error);
                return;
            }

            try
            {
                bool enabled = value == 0;
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
                SetSilently(() => StreakEffectIndex = -1);
            }
        }

        // ---- Quality presets ----

        partial void OnTextureDetailIndexChanged(int value) => _ = OnTextureDetailChangedAsync(value);

        private async Task OnTextureDetailChangedAsync(int index)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => TextureDetailIndex = -1))
                return;

            if (index == 0) // Custom
                return;

            bool proceed = await _dialogService.ShowConfirmationAsync(
                "Texture detail preset",
                "Applying a texture detail preset will revert any changes you may have made in the 'Individual Settings' section below.\n\nDo you wish to proceed?");

            if (!proceed)
            {
                SetSilently(() => TextureDetailIndex = -1);
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
                SetSilently(() => TextureDetailIndex = -1);
            }
        }

        partial void OnGraphicsQualityIndexChanged(int value) => _ = OnGraphicsQualityChangedAsync(value);

        private async Task OnGraphicsQualityChangedAsync(int index)
        {
            if (_isLoading || index < 0)
                return;

            if (!EnsureIniExists(() => GraphicsQualityIndex = -1))
                return;

            if (index == 0) // Custom
                return;

            bool proceed = await _dialogService.ShowConfirmationAsync(
                "Graphics quality preset",
                "Applying a graphics quality preset will revert any changes you may have made in the 'Individual Settings' section below.\n\nDo you wish to proceed?");

            if (!proceed)
            {
                SetSilently(() => GraphicsQualityIndex = -1);
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
                SetSilently(() => GraphicsQualityIndex = -1);
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

        // ---- Resolution + High-Res Fix ----

        partial void OnSelectedResolutionChanged(ResolutionHelper.Resolution? value) => _ = OnResolutionChangedAsync(value);

        private async Task OnResolutionChangedAsync(ResolutionHelper.Resolution? selectedResolution)
        {
            if (_isLoading || selectedResolution == null)
                return;

            _gameStatus.IsUiEnabled = false;

            try
            {
                bool success = await UpdateResolutionInConfigAsync(selectedResolution.Width, selectedResolution.Height);
                if (!success)
                    return;

                bool userWantsUIScaling = false;
                var gameDir = _session.Config.GameDirectoryPath;

                if (_uiScaling.ShouldOfferUIScaling(selectedResolution.Width))
                {
                    _gameStatus.IsUiEnabled = true;
                    userWantsUIScaling = await _uiScaling.AskUserForUIScalingConfirmationAsync();
                    _gameStatus.IsUiEnabled = false;

                    if (!string.IsNullOrEmpty(gameDir))
                    {
                        ShowProgress("Applying UI scaling...", true);

                        await Task.Run(async () =>
                        {
                            if (userWantsUIScaling)
                                await _uiScaling.ApplyUIScalingAsync(selectedResolution.Width, selectedResolution.Height, gameDir, () => HideProgress());
                            else
                                await _uiScaling.RollbackUIScalingToDefaultsAsync(selectedResolution.Width, selectedResolution.Height, gameDir, () => HideProgress());
                        });
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(gameDir))
                    {
                        ShowProgress("Resetting UI scaling...", true);

                        await Task.Run(async () =>
                        {
                            await _uiScaling.RollbackUIScalingToDefaultsAsync(selectedResolution.Width, selectedResolution.Height, gameDir, () => HideProgress());
                        });
                    }
                }

                UpdateHighResFixStatus(selectedResolution.Width, userWantsUIScaling);

                await Task.Run(ApplyDynamicPatchesForResolution);

                RefreshEnginePatchState();
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
                _gameStatus.Status = "Ready.";
            }
        }

        // Ensures the exe render target fix, Engine.u dynamic AR/FOV scaling,
        // and TdGame.u core compensation patches are applied when resolution changes
        private void ApplyDynamicPatchesForResolution()
        {
            try
            {
                string? exePath = GetGameExePath();
                if (exePath != null)
                    ExePatcher.Reconcile(exePath);
            }
            catch (OoaLicenseNotFoundException ooaEx)
            {
                Dispatch(() => _dialogService.ShowMessage("EA App License Required", ooaEx.Message, DialogMessageType.Warning));
            }
            catch { }

            try
            {
                var enginePath = _session.Config.EnginePackagePath;
                if (enginePath != null && File.Exists(enginePath))
                    EnginePatcher.Reconcile(enginePath);
            }
            catch { }

            try
            {
                var tdGamePath = _session.Config.TdGamePackagePath;
                if (tdGamePath != null && File.Exists(tdGamePath))
                {
                    bool enableSens = false;
                    bool enableClip = false;
                    bool enableOnlineSkip = false;
                    Dispatch(() =>
                    {
                        enableSens = FovAgnosticSens;
                        enableClip = CompensatedClip;
                        enableOnlineSkip = _session.OnlineSkipEnabled;
                    });
                    TdGamePatcher.Reconcile(tdGamePath, enableSens, enableClip, enableOnlineSkip);
                }
            }
            catch { }
        }

        public void UpdateHighResFixStatus(int width, bool isActive)
        {
            if (_session.IsProcessingGameDirectory)
                return;

            if (width <= 1920)
            {
                HighResFixStatus = "High-Res Fix N/A";
                HighResFixStatusForeground = Brushes.Gray;
            }
            else if (isActive)
            {
                HighResFixStatus = "High-Res Fix Active";
                HighResFixStatusForeground = Brushes.Green;
            }
            else
            {
                HighResFixStatus = "High-Res Fix Inactive";
                HighResFixStatusForeground = Brushes.Orange;
            }
        }

        private async Task<bool> UpdateResolutionInConfigAsync(int width, int height)
        {
            if (string.IsNullOrEmpty(_session.Config.GameDirectoryPath))
                return false;

            try
            {
                string? engineIniPath = IniPath;

                if (string.IsNullOrEmpty(engineIniPath) || !File.Exists(engineIniPath))
                {
                    await _dialogService.ShowMessageAsync("Error", "TdEngine.ini file not found. Please ensure Mirror's Edge has been run at least once to create the config files.", DialogMessageType.Error);
                    return false;
                }

                var fileInfo = new FileInfo(engineIniPath);

                try
                {
                    if (fileInfo.IsReadOnly)
                        fileInfo.IsReadOnly = false;
                }
                catch (UnauthorizedAccessException)
                {
                    await _dialogService.ShowMessageAsync("Error", "Unable to access TdEngine.ini. The file may be in use by another program.", DialogMessageType.Error);
                    return false;
                }
                catch (IOException ex)
                {
                    await _dialogService.ShowMessageAsync("Error", $"Unable to access TdEngine.ini: {ex.Message}", DialogMessageType.Error);
                    return false;
                }

                try
                {
                    ConfigFileHelper.ModifyIniFile(engineIniPath, "SystemSettings", "ResX", width.ToString());
                    ConfigFileHelper.ModifyIniFile(engineIniPath, "SystemSettings", "ResY", height.ToString());
                }
                finally
                {
                    try
                    {
                        if (File.Exists(engineIniPath))
                            fileInfo.IsReadOnly = true;
                    }
                    catch
                    {
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Error", $"Failed to update resolution: {ex.Message}", DialogMessageType.Error);
                return false;
            }
        }

        public void RefreshHighResFix()
        {
            var res = SelectedResolution;
            if (res != null)
            {
                bool isCurrentlyActive = false;
                var gameDir = _session.Config.GameDirectoryPath;
                if (!string.IsNullOrEmpty(gameDir))
                {
                    try
                    {
                        isCurrentlyActive = _uiScaling.IsUIScalingActive(gameDir);
                    }
                    catch
                    {
                        isCurrentlyActive = false;
                    }
                }

                UpdateHighResFixStatus(res.Width, isCurrentlyActive);
                return;
            }

            HighResFixStatus = "High-Res Fix N/A";
            HighResFixStatusForeground = Brushes.Gray;
        }

        public async Task RefreshHighResFixAsync()
        {
            var res = SelectedResolution;
            if (res == null)
            {
                HighResFixStatus = "High-Res Fix N/A";
                HighResFixStatusForeground = Brushes.Gray;
                return;
            }

            bool isCurrentlyActive = false;
            var gameDir = _session.Config.GameDirectoryPath;
            if (!string.IsNullOrEmpty(gameDir))
            {
                isCurrentlyActive = await Task.Run(() =>
                {
                    try { return _uiScaling.IsUIScalingActive(gameDir); }
                    catch { return false; }
                });
            }

            UpdateHighResFixStatus(res.Width, isCurrentlyActive);
        }

        // Reapplies (or rolls back to defaults) the high-res UI scaling fix for the active
        // resolution. Called by flows that overwrite game files on disk (language pack and TdGame
        // version installs) so the fix is not lost.
        public async Task ReapplyHighResUIFixIfNeededAsync(bool? wasUIScalingActiveOverride = null, bool showDialogs = true)
        {
            try
            {
                var gameDir = _session.Config.GameDirectoryPath;
                if (string.IsNullOrEmpty(gameDir))
                    return;

                bool wasUIScalingActive = wasUIScalingActiveOverride ?? _uiScaling.IsUIScalingActive(gameDir);

                ResolutionHelper.Resolution? selectedResolution = SelectedResolution ?? GetCurrentResolutionFromConfig();

                if (selectedResolution == null || !_uiScaling.ShouldOfferUIScaling(selectedResolution.Width))
                {
                    return;
                }

                var dispatcher = System.Windows.Application.Current.Dispatcher;

                await dispatcher.InvokeAsync(() =>
                {
                    _downloadProgress.IsDownloadProgressIndeterminate = true;
                    _downloadProgress.IsDownloadProgressVisible = true;
                    _gameStatus.Status = wasUIScalingActive ? "Reapplying high-res UI fix..." : "Resetting UI scaling...";
                });

                await Task.Run(async () =>
                {
                    if (wasUIScalingActive)
                    {
                        await _uiScaling.ApplyUIScalingAsync(
                            selectedResolution.Width,
                            selectedResolution.Height,
                            gameDir,
                            null,
                            showDialogs);
                    }
                    else
                    {
                        await _uiScaling.RollbackUIScalingToDefaultsAsync(
                            selectedResolution.Width,
                            selectedResolution.Height,
                            gameDir,
                            null,
                            showDialogs);
                    }
                });

                await dispatcher.InvokeAsync(() =>
                {
                    UpdateHighResFixStatus(selectedResolution.Width, wasUIScalingActive);
                    _downloadProgress.IsDownloadProgressVisible = false;
                    _downloadProgress.IsDownloadProgressIndeterminate = false;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to reapply high res UI fix: {ex.Message}");
            }
        }

        public void InitializeResolutions()
        {
            SetSilently(() =>
            {
                Resolutions.Clear();
                foreach (var resolution in ResolutionHelper.GetAvailableResolutions())
                    Resolutions.Add(resolution);

                ResolutionHelper.Resolution? match = null;
                var current = GetCurrentResolutionFromConfig();
                if (current != null)
                    match = Resolutions.FirstOrDefault(r => r.Width == current.Width && r.Height == current.Height);

                if (match == null && Resolutions.Count > 0)
                    match = Resolutions[0];

                SelectedResolution = match;
            });

            if (SelectedResolution != null)
            {
                bool isCurrentlyActive = false;
                var gameDir = _session.Config.GameDirectoryPath;
                if (!string.IsNullOrEmpty(gameDir))
                    isCurrentlyActive = _uiScaling.IsUIScalingActive(gameDir);

                UpdateHighResFixStatus(SelectedResolution.Width, isCurrentlyActive);
            }
        }

        public async Task InitializeResolutionsAsync()
        {
            ResolutionHelper.Resolution? selected = null;
            SetSilently(() =>
            {
                Resolutions.Clear();
                foreach (var resolution in ResolutionHelper.GetAvailableResolutions())
                    Resolutions.Add(resolution);

                ResolutionHelper.Resolution? match = null;
                var current = GetCurrentResolutionFromConfig();
                if (current != null)
                    match = Resolutions.FirstOrDefault(r => r.Width == current.Width && r.Height == current.Height);

                if (match == null && Resolutions.Count > 0)
                    match = Resolutions[0];

                SelectedResolution = match;
                selected = match;
            });

            if (selected != null)
            {
                bool isCurrentlyActive = false;
                var gameDir = _session.Config.GameDirectoryPath;
                if (!string.IsNullOrEmpty(gameDir))
                {
                    isCurrentlyActive = await Task.Run(() =>
                    {
                        try { return _uiScaling.IsUIScalingActive(gameDir); }
                        catch { return false; }
                    });
                }

                UpdateHighResFixStatus(selected.Width, isCurrentlyActive);
            }
        }

        private ResolutionHelper.Resolution? GetCurrentResolutionFromConfig()
        {
            try
            {
                string? engineIniPath = IniPath;

                if (string.IsNullOrEmpty(engineIniPath) || !File.Exists(engineIniPath))
                    return null;

                var lines = File.ReadAllLines(engineIniPath);
                int resX = -1;
                int resY = -1;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("ResX=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmedLine.Substring(5), out int x))
                            resX = x;
                    }
                    else if (trimmedLine.StartsWith("ResY=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmedLine.Substring(5), out int y))
                            resY = y;
                    }
                }

                if (resX > 0 && resY > 0)
                    return new ResolutionHelper.Resolution { Width = resX, Height = resY };
            }
            catch (Exception)
            {
            }

            return null;
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

        // ---- Shaders (Tone Mapper) ----

        partial void OnToneMapperIndexChanged(int value) => _ = OnToneMapperChangedAsync(value);

        private async Task OnToneMapperChangedAsync(int index)
        {
            if (_isLoading || index < 0)
                return;

            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                SetSilently(() => ToneMapperIndex = -1);
                return;
            }

            string variantName = index == 1 ? "Faithful Luma" : "Original";
            string zipName = index == 1 ? "FaithfulLumaTonemap.zip" : "OriginalTonemap.zip";
            string downloadUrl = DownloadUrls.For(zipName);
            string tempZipPath = Path.Combine(Path.GetTempPath(), zipName);

            _gameStatus.IsUiEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.DownloadProgressValue = 0;
            _downloadProgress.IsDownloadProgressIndeterminate = true;
            _gameStatus.Status = $"Downloading {variantName} tone mapper...";

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            try
            {
                await Task.Run(async () =>
                {
                    using (var client = new HttpClient())
                    using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            if (totalBytes.HasValue)
                            {
                                dispatcher.Invoke(() => _downloadProgress.IsDownloadProgressIndeterminate = false);

                                var totalBytesRead = 0L;
                                var buffer = new byte[8192];
                                int bytesRead;
                                var report = CreateThrottledProgressReporter();
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    totalBytesRead += bytesRead;
                                    var progress = (int)((double)totalBytesRead / totalBytes.Value * 100);

                                    report(progress, $"Downloading {variantName} tone mapper... {progress}%");
                                }
                            }
                            else
                            {
                                await stream.CopyToAsync(fileStream);
                            }
                        }
                    }

                    dispatcher.Invoke(() =>
                    {
                        _gameStatus.Status = "Extracting shader files...";
                        _downloadProgress.DownloadProgressValue = 100;
                        _downloadProgress.IsDownloadProgressIndeterminate = true;
                    });

                    ZipFile.ExtractToDirectory(tempZipPath, gameDir, true);
                    File.Delete(tempZipPath);
                });

                _gameStatus.Status = "Ready.";
                RefreshToneMapper();
                await _dialogService.ShowMessageAsync("Success",
                    $"{variantName} tone mapper successfully downloaded and installed.",
                    DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                _gameStatus.Status = "An error occurred during installation.";
                await _dialogService.ShowMessageAsync("Error", $"An error occurred: {ex.Message}", DialogMessageType.Error);
                SetSilently(RefreshToneMapper);
            }
            finally
            {
                _gameStatus.IsUiEnabled = true;
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.DownloadProgressValue = 0;
                _downloadProgress.IsDownloadProgressIndeterminate = false;
            }
        }

        // Detects the installed tone mapper variant from disk and sets the combo accordingly. The
        // Faithful Luma shader is identified by a helper function only present in that variant.
        public void RefreshToneMapper()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            int detected = 0;
            if (!string.IsNullOrEmpty(gameDir))
            {
                string shaderPath = Path.Combine(gameDir, "Engine", "Shaders", "TdToneMappingPixelShader.usf");
                try
                {
                    if (File.Exists(shaderPath) &&
                        File.ReadAllText(shaderPath).Contains("ApplyWhiteNeutralityCorrection"))
                        detected = 1;
                }
                catch { }
            }

            SetSilently(() => ToneMapperIndex = detected);
        }

        // ---- FOV ----

        // Pushes the camera FOV discovered during package offset finding (stored on
        // GameSession.DetectedCameraFov) into the displayed value, seeding the editable FOV when it
        // is still at its default. Called from the post-reload fan-out so the package-load service
        // need not depend on this view model.
        public void RefreshFovDisplay()
        {
            float? detected = _session.DetectedCameraFov;
            if (detected == null)
            {
                CurrentFovValue = "N/A";
                return;
            }

            CurrentFovValue = Math.Round(detected.Value).ToString(CultureInfo.InvariantCulture) + "\u00b0 (horizontal)";

            if (string.IsNullOrEmpty(NewFovValue) || NewFovValue == "90" || NewFovValue == "N/A")
            {
                NewFovValue = Math.Round(detected.Value).ToString(CultureInfo.InvariantCulture);
            }
        }

        partial void OnNewFovValueChanged(string value) => RefreshScalingStatus();

        [RelayCommand]
        private async Task ApplyFov()
        {
            var config = _session.Config;

            if (config.EnginePackagePath == null || config.TdGamePackagePath == null)
            {
                _dialogService.ShowMessage("Error", "Please select a valid game directory first.", DialogMessageType.Error);
                return;
            }

            if (!float.TryParse(NewFovValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float newFov) || newFov < 80 || newFov > 179)
            {
                _dialogService.ShowMessage("Invalid Input", "Please enter a valid number for the FOV (must be between 80 - 179).", DialogMessageType.Warning);
                return;
            }

            bool enableSens = FovAgnosticSens;
            bool enableClip = CompensatedClip;
            bool enableOnlineSkip = _session.OnlineSkipEnabled;

            _gameStatus.IsUiEnabled = false;
            _downloadProgress.IsDownloadProgressVisible = true;
            _downloadProgress.IsDownloadProgressIndeterminate = true;
            _gameStatus.Status = "Applying FOV settings...";

            try
            {
                await Task.Run(() =>
                {
                    _session.Package?.Dispose();
                    _session.TdGamePackage?.Dispose();
                    _session.Package = null;
                    _session.TdGamePackage = null;

                    EnginePatcher.Reconcile(config.EnginePackagePath);

                    TdGamePatcher.Reconcile(config.TdGamePackagePath, enableSens, enableClip, enableOnlineSkip);

                    string? exePath = GetGameExePath();
                    if (exePath != null)
                    {
                        try { ExePatcher.Reconcile(exePath); }
                        catch (OoaLicenseNotFoundException ooaEx)
                        {
                            Dispatch(() => _dialogService.ShowMessage("EA App License Required", ooaEx.Message, DialogMessageType.Warning));
                        }
                        catch { }
                    }
                });

                await _gameData.ReloadPackagesAsync();

                await Task.Run(() =>
                {
                    if (config.EnginePackagePath == null)
                        return;

                    var offsets = _session.Offsets;
                    byte[] fovValueBytes = BitConverter.GetBytes(newFov);
                    using var stream = new FileStream(config.EnginePackagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

                    if (offsets.PlayerControllerDefaultFovOffset != -1)
                    {
                        stream.Position = offsets.PlayerControllerDefaultFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.PlayerControllerDesiredFovOffset != -1)
                    {
                        stream.Position = offsets.PlayerControllerDesiredFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.PlayerControllerFovAngleOffset != -1)
                    {
                        stream.Position = offsets.PlayerControllerFovAngleOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.CameraFovOffset != -1)
                    {
                        stream.Position = offsets.CameraFovOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                    if (offsets.CameraActorFovAngleOffset != -1)
                    {
                        stream.Position = offsets.CameraActorFovAngleOffset;
                        stream.Write(fovValueBytes, 0, fovValueBytes.Length);
                    }
                });

                _session.Config.Fov = NewFovValue;
                _settings.Save();

                _gameStatus.Status = "Ready.";
                await _dialogService.ShowMessageAsync("Success", "Successfully applied FOV patches.", DialogMessageType.Success);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("Save Error", $"Failed to apply changes: {ex.Message}", DialogMessageType.Error);
                _gameStatus.Status = "Error applying changes.";
            }
            finally
            {
                _downloadProgress.IsDownloadProgressVisible = false;
                _downloadProgress.IsDownloadProgressIndeterminate = false;

                await _gameData.ReloadPackagesAsync();

                _gameStatus.IsUiEnabled = true;
            }
        }

        // Re-reads the (expensive) Engine.u dynamic-FOV-scaling patch state from disk into the
        // cache and refreshes the HOR+/VERT+ label. Call this only when the patch state may have
        // changed (package reload or resolution reconcile), not on every FOV keystroke.
        public void RefreshEnginePatchState()
        {
            _enginePatchesApplied = false;
            var enginePath = _session.Config.EnginePackagePath;
            if (enginePath != null && File.Exists(enginePath))
            {
                try { _enginePatchesApplied = EnginePatcher.DetectState(enginePath) == EnginePatchState.FullyPatched; }
                catch { }
            }

            RefreshScalingStatus();
        }

        // Reads the (expensive, Engine.u-loading) dynamic-FOV-scaling patch state off the UI thread,
        // then applies it on the UI thread so the status-bar progress bar keeps animating.
        public async Task RefreshEnginePatchStateAsync()
        {
            bool applied = false;
            var enginePath = _session.Config.EnginePackagePath;
            if (enginePath != null && File.Exists(enginePath))
            {
                applied = await Task.Run(() =>
                {
                    try { return EnginePatcher.DetectState(enginePath) == EnginePatchState.FullyPatched; }
                    catch { return false; }
                });
            }

            _enginePatchesApplied = applied;
            RefreshScalingStatus();
        }

        // Recomputes the HOR+/VERT+ label from the cached engine patch state, the selected
        // resolution and the entered FOV. Pure arithmetic (no disk I/O), safe to call per keystroke.
        public void RefreshScalingStatus()
        {
            if (!_enginePatchesApplied)
            {
                HorPlusStatus = "";
                return;
            }

            var res = SelectedResolution;
            if (res == null || res.Height == 0)
            {
                HorPlusStatus = "";
                return;
            }

            double ar = (double)res.Width / res.Height;
            const double baseline = 16.0 / 9.0;
            const double tolerance = 0.01;

            if (ar > baseline + tolerance)
            {
                if (float.TryParse(NewFovValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float baseFov) && baseFov > 0)
                {
                    double effectiveFov = ComputeHorPlusFov(baseFov, ar);
                    HorPlusStatus = $"(HOR+ \u2192 {effectiveFov:F0}°)";
                }
                else
                {
                    HorPlusStatus = "(HOR+)";
                }
            }
            else if (ar < baseline - tolerance)
            {
                HorPlusStatus = "(VERT+)";
            }
            else
            {
                HorPlusStatus = "";
            }
        }

        private static double ComputeHorPlusFov(double baseFov, double aspectRatio)
        {
            const double baseline = 16.0 / 9.0;
            double halfRad = baseFov * Math.PI / 360.0;
            return Math.Atan(Math.Tan(halfRad) * aspectRatio / baseline) * 360.0 / Math.PI;
        }

        private string? GetGameExePath()
        {
            var gameDir = _session.Config.GameDirectoryPath;
            if (string.IsNullOrEmpty(gameDir))
                return null;

            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            return File.Exists(exePath) ? exePath : null;
        }

        // ---- Info dialogs ----

        [RelayCommand]
        private void ShowFovInfo()
        {
            _dialogService.ShowMessage("FOV Information",
                "Default horizontal FOV = 90°.\n\n" +
                "FOV automatically applies HOR+ scaling at aspect ratios wider than 16:9, and VERT+ scaling at " +
                "narrower aspect ratios. The aspect ratio is detected from the game resolution, no manual entry is needed.\n\n" +
                "The FOV value persists after each level load and game restart, scales correctly during cutscenes " +
                "and camera transitions, and does not break the skybox (unlike the keybind FOV method). " +
                "It also fixes the FOV being reset to 85° when reloading from deaths, compensates the vertigo " +
                "zoom effect, and maintains affected ADS FOV with the sniper rifle.\n\n" +
                "A render target fix is also applied to prevent the white-screen issue at narrower aspect ratios above 720p (e.g. Steam Deck's native resolution).\n\n" +
                "Options:\n\n" +
                "• Compensated near clipping plane: adjusts the near clipping plane based on FOV and aspect ratio " +
                "to reduce viewmodel/geometry clipping at higher FOVs or wider aspect ratios. Please note that Z-fighting will " +
                "become more prevalent at more extreme FOVs or wider aspect ratios with this option enabled.\n\n" +
                "• FOV-agnostic sensitivity: keeps mouse sensitivity consistent across all FOV values, using 90° " +
                "as the baseline. Weapon zoom sensitivity still tracks the zoomed FOV as normal. Also useful for TAS tools.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowHighResFixInfo()
        {
            _dialogService.ShowMessage("Resolution Information",
                "Mirror's Edge accepts only the resolutions currently available in your system's display settings. However, it is possible to use other software " +
                "(e.g. Custom Resolution Utility, NVIDIA Control Panel, etc.) to add custom display resolutions. Once these are configured, they will appear here.\n\n" +
                "Selecting a resolution will also apply the following fixes:\n\n" +
                "• Removes the hardcoded 16:9 aspect ratio constraint, allowing the game to render correctly at any aspect ratio without letterboxing/pillarboxing.\n\n" +
                "• Applies a render target fix to prevent the white-screen issue at narrower aspect ratios above 720p (e.g. Steam Deck's native resolution).\n\n" +
                "• Enables dynamic FOV scaling so the game automatically applies HOR+ correction at aspect ratios wider than 16:9, and VERT+ at narrower ratios.\n\n" +
                "• Compensates cutscene zoom rates, vertigo effects, and unzoom timing to work correctly and consistently at any FOV and aspect ratio.\n\n" +
                "Selecting a resolution with a horizontal pixel count greater than 1920 will also prompt you with the option to fix the blurry in-game text and other UI fixes. " +
                "Once applied, this fix remains dynamic and further in-game resolution adjustments will self-apply the high-res fix.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowVSyncInfo()
        {
            _dialogService.ShowMessage("VSync Information",
                "Vertical Sync synchronises the frame rate with your monitor's refresh rate to prevent screen tearing. Enabling it may increase input latency.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowFpsLimitInfo()
        {
            _dialogService.ShowMessage("FPS Limit Information",
                "Default = FPS limit of 62.\n\n60-62 FPS limit is a requirement for speedruns to be verified, any other setting is banned. " +
                "Speedrunning strategies become increasingly more difficult as FPS increases, therefore it is not advised to deviate from the 60-62 FPS limit.\n\n" +
                "As framerate increases, so does player friction which can alter the speed of certain movement mechanics and make forced slides more difficult to control " +
                "as framerates exceed 150 FPS (i.e. Chapter 1C RP&A building slide). Enemy accuracy is also increased at higher framerates. " +
                "Additionally, as load times are tied to framerate, loading times decrease as framerate increases. These effects are otherwise generally not noticeable to casual players " +
                "and the game can be comfortably played with a higher FPS limit in place.\n\nIf you want to run the game with no FPS limiter at all, click the 'Remove Limit' button.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowAntiAliasingInfo()
        {
            _dialogService.ShowMessage("Anti-Aliasing Information",
                "Anti-aliasing smooths jagged edges in the game. Higher values provide better quality but reduce performance.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowPhysXInfo()
        {
            _dialogService.ShowMessage("PhysX Information",
                "PhysX provides additional physics effects such as detailed debris and cloth simulations, and spawns in extra physics props.\n\n" +
                "Note: PhysX in Mirror's Edge is hardware accelerated only on CUDA-ready NVIDIA GPUs.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowPhysXFpsInfo()
        {
            _dialogService.ShowMessage("PhysX FPS Information",
                "Applies a PhysX FPS value to cloth simulations (flags, construction tarps, strip curtain doors, etc.). Accepts a minimum of 50 FPS and a maximum of 300 FPS. No effect if PhysX is disabled.\n\n" +
                "Cloth simulations in Mirror's Edge are simulated at a rate independent of the game's framerate, otherwise known as time-steps. By default, Mirror's Edge uses a value of 50 FPS " +
                "for PhysX cloth simulations, which can appear choppy when using reaction time or when running the game above the 62 FPS limit.\n\n" +
                "Suggestions:\n\n• If playing at the default 62 FPS limit, change the PhysX FPS value to 62 FPS to match the simulation rate with the game's framerate. " +
                "This effectively removes the frame pacing appearance of PhysX cloth.\n\n• If playing at uncapped FPS, set this value to whatever you want (max of 300 FPS).",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowRenderResolutionInfo()
        {
            _dialogService.ShowMessage("Render Resolution Information",
                "Controls the internal rendering resolution relative to your display output.\n\n" +
                "Below 100%: Renders at a lower resolution and upscales, improving performance on lower-end systems.\n\n" +
                "Above 100%: Renders at a higher resolution and downscales to your display, producing sharper visuals " +
                "with reduced aliasing. Setting 200% renders at 4x pixel density.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowTextureDetailInfo()
        {
            _dialogService.ShowMessage("Texture Detail Information",
                "Texture detail controls the resolution/LODs of textures, as well as the level of anisotropic filtering and bicubic filtering to be applied.\n\nThis setting mirrors the in-game video options.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowGraphicsQualityInfo()
        {
            _dialogService.ShowMessage("Graphics Quality Information",
                "Graphics quality controls mesh/shadow quality, as well as various other post-process effects such as bloom, depth of field, lens flares, etc.\n\nThis setting mirrors the in-game video options.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowAnisotropicFilteringInfo()
        {
            _dialogService.ShowMessage("Anisotropic Filtering Information",
                "Anisotropic filtering improves texture quality when viewed at oblique angles. Higher values provide better quality.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowStaticDecalsInfo()
        {
            _dialogService.ShowMessage("Static Decals Information",
                "Static decals are pre-placed decals (runner glyphs, paint/graffiti, etc.).",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowDynamicDecalsInfo()
        {
            _dialogService.ShowMessage("Dynamic Decals Information",
                "Dynamic decals are decals spawned during gameplay (typically bullet holes and explosion effects).",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowRadialBlurInfo()
        {
            _dialogService.ShowMessage("Radial Blur Information",
                "Radial blur is the blurring applied to the edges of the screen when running. It is seperate from the streak effect.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowStreakEffectInfo()
        {
            _dialogService.ShowMessage("Streak Effect Information",
                "When approaching top running speed, streak effects will appear on the edges of the screen which can become more noticeable at higher FOV settings. " +
                "\n\nDisabling requires the 'Unlocked Configs' patch in the 'Game Tweaks' section.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowBloomDoFInfo()
        {
            _dialogService.ShowMessage("Bloom & DoF Information",
                "Bloom creates a glow effect around bright lights. Depth of Field blurs objects that are out of focus." +
                "\n\nThe shaders involved for rendering Bloom and Depth of Field are dependent on each other and cannot be individually toggled on/off.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowLensFlareInfo()
        {
            _dialogService.ShowMessage("Lens Flare Information",
                "Allows enabling/disabling the lens flares emitted from the sun and various light sources. In some maps this will also remove the appearance of the sun altogether.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowDynamicLightsInfo()
        {
            _dialogService.ShowMessage("Dynamic Lights Information",
                "Dynamic lights are any light sources that dynamically illuminate the scene and characters. Typical examples include flashlights/cop car lights and ambient character illumination.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowDynamicShadowsInfo()
        {
            _dialogService.ShowMessage("Dynamic Shadows Information",
                "Dynamic shadows are the modulated shadows casted onto the environment from characters. This also includes self-shadowing of characters.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowHqDynamicShadowsInfo()
        {
            _dialogService.ShowMessage("HQ Dynamic Shadows Information",
                "High Quality dynamic shadows doubles the resolution of what's available from the \"Highest\" graphics quality preset, " +
                "forces the maximum shadow resolution to always be shown, increases the filtering quality, and disables VSM shadowing in favour of the superior-quality PCF shadowing." +
                "\n\nNote: \"High quality\" dynamic shadows will have no effect if dynamic shadows are disabled.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowLightmapsInfo()
        {
            _dialogService.ShowMessage("Lightmaps Information",
                "Light maps are the pre-baked lighting used to globally illuminate the environment. These light maps can be disabled (for most objects), " +
                "showing the original textures without the environment's GI and shadow contributions. Note that disabling can also make some vertex-baked objects appear black.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowSunHazeInfo()
        {
            _dialogService.ShowMessage("Sun Haze Information",
                "Toggles the appearance of atmospheric haze around the sun. This haze can bleed through buildings in some scenarios.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowToneMappingInfo()
        {
            _dialogService.ShowMessage("Tone Mapping Information",
                "Tone mapping adjusts the post-process exposure/colour curves, which are applied on a per-map basis. " +
                "Disabling tone mapping typically makes the image appear brighter and with less contrast.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowTextureManagementInfo()
        {
            _dialogService.ShowMessage("Texture Management Information",
                "The \"Modern\" setting removes the 250MB VRAM allocation limit to textures and forces textures to remain in the texture pool once loaded. " +
                "This can resolve the random blurry texture bug, and assists with large custom maps that don't utilise level streaming.\n\n" +
                "If you have a low-end system, it may be more preferable to keep this setting to \"Default\".",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowMinLodInfo()
        {
            _dialogService.ShowMessage("Minimum LOD Information",
                "Minimum LOD size controls the lowest quality texture mipmap that will be loaded. Range: 1-4096 (Unreal Engine 3 has a max limit of 4096).",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowMaxLodInfo()
        {
            _dialogService.ShowMessage("Maximum LOD Information",
                "Maximum LOD size controls the highest quality texture mipmap that will be loaded. Range: 1-4096 (Unreal Engine 3 has a max limit of 4096).",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowLodBiasInfo()
        {
            _dialogService.ShowMessage("LOD Bias Information",
                "Adjusts the distance at which different texture mipmaps are loaded. A higher bias value results in lower resolution texture mipmaps being shown sooner " +
                "as the player moves away from the texture surface and vice versa. A minimum bias of 0 (highest quality, shows only the maximum resolution LOD) " +
                "and a maximum bias of 12 (lowest quality) can be entered.",
                DialogMessageType.Information);
        }

        [RelayCommand]
        private void ShowToneMapperInfo()
        {
            _dialogService.ShowMessage("Tone Mapper Information",
                "Replaces the game's post-process tone mapping shaders. Selecting an option downloads and installs the corresponding shader files.\n\n" +
                "• Original — The game's default tone mapping shaders.\n\n" +
                "• Faithful Luma — A luminance-preserving tone mapper that better retains highlight detail and colour, while also " +
                "fixing the black floor level, providing more accurate bloom handling and making the auto-exposure system much more responsive.\n\n" +
                "Note: Neither tone mapping option will have an effect if the 'Tone Mapping' toggle in the Individual Settings section above is disabled.",
                DialogMessageType.Information);
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

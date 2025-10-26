namespace MirrorsEdgeTweaks.Services
{
    public interface IGraphicsSettingsService
    {
        string? ReadIniValue(string tdEngineIniPath, string key);
        string DetectTextureDetailPreset(string tdEngineIniPath);
        string DetectGraphicsQualityPreset(string tdEngineIniPath);
        void ApplyVSync(string tdEngineIniPath, bool enabled);
        void ApplyAntiAliasing(string tdEngineIniPath, string level);
        void ApplyAnisotropicFiltering(string tdEngineIniPath, string level);
        void ApplyPhysX(string tdEngineIniPath, bool enabled);
        void ApplyRenderResolution(string tdEngineIniPath, int percentage);
        void ApplyTextureDetailPreset(string tdEngineIniPath, string preset);
        void ApplyGraphicsQualityPreset(string tdEngineIniPath, string preset);
        void ApplyStaticDecals(string tdEngineIniPath, bool enabled);
        void ApplyDynamicDecals(string tdEngineIniPath, bool enabled);
        void ApplyRadialBlur(string tdEngineIniPath, bool enabled);
        void ApplyBloomAndDoF(string tdEngineIniPath, bool enabled);
        void ApplyLensFlare(string tdEngineIniPath, bool enabled);
        void ApplyDynamicLights(string tdEngineIniPath, bool enabled);
        void ApplyDynamicShadows(string tdEngineIniPath, bool enabled);
        void ApplyHQDynamicShadows(string tdEngineIniPath, bool enabled);
        void ApplyLightmaps(string tdEngineIniPath, bool enabled);
        void ApplySunHaze(string tdEngineIniPath, bool enabled);
        void ApplyToneMapping(string tdEngineIniPath, bool enabled);
        void ApplyTextureManagement(string tdEngineIniPath, string mode);
        void ApplyMinLOD(string tdEngineIniPath, int value);
        void ApplyMaxLOD(string tdEngineIniPath, int value);
        void ApplyLODBias(string tdEngineIniPath, int value);
        void ApplyStreakEffect(string defaultHudEffectsIniPath, bool enabled);
        string? ReadStreakEffectStatus(string defaultHudEffectsIniPath);
        void ApplyFPSLimit(string tdEngineIniPath, int fpsValue);
        void RemoveFPSLimit(string tdEngineIniPath);
        (bool isLimited, int? fpsValue) ReadFPSLimitStatus(string tdEngineIniPath);
    }
}

